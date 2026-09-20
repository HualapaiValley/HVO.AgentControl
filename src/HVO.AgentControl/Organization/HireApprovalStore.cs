using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HVO.AgentControl.Organization;

/// <summary>
/// Owner approval and managed-employee creation (schema v10). An approval is an
/// atomic, immutable freeze of one hire request revision against one verified
/// profile build on one ready host: the exact profile revision, build id, image
/// digest, host, platform and resource limits are recorded once and cannot change.
/// The employee and runtime binding are allocated later, in a second atomic step,
/// and linked back to the frozen approval. Neither step provisions a worker or
/// moves the hire out of <c>Approved</c>; provisioning consumes the frozen
/// resources from <c>managed_enrollment_resources</c>.
/// </summary>
public sealed partial class OrganizationStore
{
    public const int MaximumApprovalIdentityLength = 128;
    public const int MaximumStatusDetailLength = 512;

    private static string[] HireApprovalSchemaV10Statements =>
    [
        """
        CREATE TABLE hire_request_approvals (
            hire_request_id TEXT PRIMARY KEY REFERENCES hire_requests(id) ON DELETE RESTRICT,
            approved_request_version TEXT NOT NULL UNIQUE CHECK (length(approved_request_version) = 71 AND substr(approved_request_version, 1, 7) = 'sha256:'),
            approved_request_revision INTEGER NOT NULL CHECK (approved_request_revision >= 1),
            request_version_hash TEXT NOT NULL CHECK (length(request_version_hash) = 71 AND substr(request_version_hash, 1, 7) = 'sha256:'),
            profile_revision_id TEXT NOT NULL REFERENCES container_profile_revisions(id) ON DELETE RESTRICT,
            profile_build_id TEXT NOT NULL REFERENCES profile_builds(id) ON DELETE RESTRICT,
            image_digest TEXT NOT NULL CHECK (length(image_digest) = 71 AND substr(image_digest, 1, 7) = 'sha256:'),
            host_id TEXT NOT NULL REFERENCES execution_hosts(id) ON DELETE RESTRICT,
            platform TEXT NOT NULL CHECK (platform IN ('linux/amd64', 'linux/arm64')),
            cpu_limit INTEGER NOT NULL CHECK (cpu_limit BETWEEN 1 AND 64),
            memory_limit_mib INTEGER NOT NULL CHECK (memory_limit_mib BETWEEN 256 AND 131072),
            pids_limit INTEGER NOT NULL CHECK (pids_limit BETWEEN 16 AND 4096),
            approval_identity TEXT NOT NULL CHECK (length(approval_identity) BETWEEN 1 AND 128),
            approved_at TEXT NOT NULL,
            employee_id TEXT UNIQUE REFERENCES employees(id) ON DELETE RESTRICT,
            runtime_binding_id TEXT UNIQUE REFERENCES runtime_bindings(id) ON DELETE RESTRICT,
            worker_id TEXT UNIQUE REFERENCES worker_enrollments(worker_id) ON DELETE RESTRICT,
            revision INTEGER NOT NULL CHECK (revision >= 1),
            CHECK ((employee_id IS NULL AND runtime_binding_id IS NULL)
                OR (employee_id IS NOT NULL AND runtime_binding_id IS NOT NULL))
        ) WITHOUT ROWID
        """,
        """
        CREATE TABLE managed_enrollment_resources (
            runtime_binding_id TEXT PRIMARY KEY REFERENCES runtime_bindings(id) ON DELETE RESTRICT,
            cpu_limit INTEGER NOT NULL CHECK (cpu_limit BETWEEN 1 AND 64),
            memory_limit_mib INTEGER NOT NULL CHECK (memory_limit_mib BETWEEN 256 AND 131072),
            pids_limit INTEGER NOT NULL CHECK (pids_limit BETWEEN 16 AND 4096),
            approved_profile_revision_id TEXT NOT NULL REFERENCES container_profile_revisions(id) ON DELETE RESTRICT,
            approved_profile_build_id TEXT NOT NULL REFERENCES profile_builds(id) ON DELETE RESTRICT,
            approved_image_digest TEXT NOT NULL CHECK (length(approved_image_digest) = 71 AND substr(approved_image_digest, 1, 7) = 'sha256:'),
            approved_host_id TEXT NOT NULL REFERENCES execution_hosts(id) ON DELETE RESTRICT,
            platform TEXT NOT NULL CHECK (platform IN ('linux/amd64', 'linux/arm64')),
            created_at TEXT NOT NULL,
            revision INTEGER NOT NULL CHECK (revision >= 1)
        ) WITHOUT ROWID
        """,
        """
        CREATE TRIGGER hire_request_approvals_frozen_immutable
            BEFORE UPDATE OF hire_request_id, approved_request_version, approved_request_revision, request_version_hash,
                profile_revision_id, profile_build_id, image_digest, host_id, platform,
                cpu_limit, memory_limit_mib, pids_limit, approval_identity, approved_at
            ON hire_request_approvals
        BEGIN
            SELECT RAISE(ABORT, 'owner approval freeze is immutable');
        END
        """,
        """
        CREATE TRIGGER hire_request_approvals_no_delete
            BEFORE DELETE ON hire_request_approvals
        BEGIN
            SELECT RAISE(ABORT, 'owner approvals are never deleted');
        END
        """,
        """
        CREATE TRIGGER hire_request_approvals_no_replace
            BEFORE INSERT ON hire_request_approvals
            WHEN EXISTS (SELECT 1 FROM hire_request_approvals WHERE hire_request_id = NEW.hire_request_id)
        BEGIN
            SELECT RAISE(ABORT, 'owner approvals are never replaced');
        END
        """,
        """
        CREATE TRIGGER managed_enrollment_resources_frozen_immutable
            BEFORE UPDATE OF runtime_binding_id, cpu_limit, memory_limit_mib, pids_limit,
                approved_profile_revision_id, approved_profile_build_id, approved_image_digest,
                approved_host_id, platform, created_at
            ON managed_enrollment_resources
        BEGIN
            SELECT RAISE(ABORT, 'managed enrollment resources are immutable');
        END
        """,
        """
        CREATE TRIGGER managed_enrollment_resources_no_delete
            BEFORE DELETE ON managed_enrollment_resources
        BEGIN
            SELECT RAISE(ABORT, 'managed enrollment resources are never deleted');
        END
        """,
        """
        CREATE TRIGGER managed_enrollment_resources_no_replace
            BEFORE INSERT ON managed_enrollment_resources
            WHEN EXISTS (SELECT 1 FROM managed_enrollment_resources WHERE runtime_binding_id = NEW.runtime_binding_id)
        BEGIN
            SELECT RAISE(ABORT, 'managed enrollment resources are never replaced');
        END
        """,
    ];

    public HireRequestApprovalRecord? GetHireRequestApproval(string hireRequestId)
    {
        if (!IsBoundedIdentifier(hireRequestId, OrganizationIds.HireRequestPrefix)) return null;
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                return ReadApproval(connection, null, hireRequestId);
            }
        });
    }

    public ManagedEnrollmentResourcesRecord? GetManagedEnrollmentResources(string runtimeBindingId)
    {
        if (!IsBoundedIdentifier(runtimeBindingId, OrganizationIds.RuntimeBindingPrefix)) return null;
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                return ReadManagedResources(connection, null, runtimeBindingId);
            }
        });
    }

    /// <summary>
    /// Resolves the frozen owner approval that owns a runtime binding. A managed
    /// enrollment is planned from a binding id alone, so this is the only stable
    /// link back to the exact approval revision a later worker link must carry.
    /// Returns null when no approval carries that binding.
    /// </summary>
    public HireRequestApprovalRecord? GetHireRequestApprovalByBinding(string runtimeBindingId)
    {
        if (!IsBoundedIdentifier(runtimeBindingId, OrganizationIds.RuntimeBindingPrefix)) return null;
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                return ReadApprovalByBinding(connection, null, runtimeBindingId);
            }
        });
    }

    /// <summary>
    /// Atomically approves one hire request revision against a server-selected,
    /// exact verified profile build. No employee or binding is created here. The
    /// frozen columns are immutable at the database boundary, so a later
    /// provisioning run reads exactly what the owner approved.
    /// </summary>
    public HireRequestSummary ApproveHireRequest(string id, HireRequestApprove request, string approvalIdentity)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsBoundedIdentifier(id, OrganizationIds.HireRequestPrefix) || request.ExpectedRevision < 1)
            throw new OrganizationValidationException("A stable hire request id and current revision are required.");
        var identity = ValidateApprovalIdentity(approvalIdentity);
        if (!IsBoundedIdentifier(request.ProfileRevisionId, OrganizationIds.ContainerProfileRevisionPrefix))
            throw new OrganizationValidationException("A stable container profile revision id is required.");

        // Managed hiring in this step is controller-local Docker only: the host is
        // the reserved local row, never a caller-supplied execution host.
        const string hostId = ExecutionHosts.LocalDockerId;

        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var current = ReadHireRequest(connection, transaction, id)
                    ?? throw new OrganizationNotFoundException($"Hire request '{id}' does not exist.");

                var existing = ReadApproval(connection, transaction, id);
                if (existing is not null)
                {
                    // An approval is a one-time freeze. A replay of the exact same
                    // selection and request revision is idempotent for any
                    // post-approval lifecycle state; a different selection is a
                    // conflict, never a second approval.
                    if (string.Equals(existing.ProfileRevisionId, request.ProfileRevisionId, StringComparison.Ordinal)
                        && string.Equals(existing.RequestVersionHash, current.RequestVersionHash, StringComparison.Ordinal)
                        && existing.ApprovedRequestRevision == request.ExpectedRevision)
                    {
                        transaction.Commit();
                        return current;
                    }

                    throw new OrganizationConcurrencyException("The hire request is already approved against a different frozen selection.");
                }

                if (current.Revision != request.ExpectedRevision)
                    throw new OrganizationConcurrencyException("The hire request changed; reload and retry with its current revision.");
                if (current.State != HireRequestStates.Requested)
                    throw new OrganizationConcurrencyException($"A hire request in state {current.State} cannot be approved.");
                if (!string.Equals(current.Placement, RuntimePlacements.DeveloperContainer, StringComparison.Ordinal))
                    throw new OrganizationValidationException("Only a DeveloperContainer hire can be approved in this phase.");

                // The server selects the unique verified build; the caller never
                // names a build id. The revision must belong to the active profile
                // and be its current revision.
                EnsureBuildableRevision(connection, transaction, request.ProfileRevisionId);
                var build = ReadVerifiedBuild(connection, transaction, request.ProfileRevisionId, hostId)
                    ?? throw new OrganizationConcurrencyException("No exact verified build for that revision exists on the controller-local Docker target.");
                var host = ReadApprovalHost(connection, transaction, hostId);
                if (host.Enabled != 1 || !string.Equals(host.Status, "ready", StringComparison.Ordinal)
                    || !string.Equals(host.CapabilityStatus, "valid", StringComparison.Ordinal))
                    throw new OrganizationConcurrencyException("The execution host is not enabled, ready and valid.");
                if (host.LimitsSupported != 1)
                    throw new OrganizationValidationException("The execution host does not support enforced resource limits.");
                if (!string.Equals(host.ImagePlatform, build.Platform, StringComparison.Ordinal))
                    throw new OrganizationValidationException("The host platform does not match the verified build platform.");
                if (current.CpuLimit > host.CpuCount)
                    throw new OrganizationValidationException("The requested CPU limit exceeds the host capacity.");
                if ((long)current.MemoryLimitMiB * 1024 * 1024 > host.MemoryBytes)
                    throw new OrganizationValidationException("The requested memory limit exceeds the host capacity.");

                // The request has no build id of its own; the approved version is
                // the freeze over the whole selection so a later parameter change
                // cannot reuse it.
                var frozenVersion = HashHireApproval(string.Join('\n',
                    id,
                    current.Revision.ToString(CultureInfo.InvariantCulture),
                    current.RequestVersionHash,
                    request.ProfileRevisionId,
                    build.Id,
                    build.ImageDigest!,
                    hostId,
                    build.Platform,
                    current.CpuLimit.ToString(CultureInfo.InvariantCulture),
                    current.MemoryLimitMiB.ToString(CultureInfo.InvariantCulture),
                    current.PidsLimit.ToString(CultureInfo.InvariantCulture)));
                var now = Timestamp();
                Execute(connection, transaction,
                    """
                    INSERT INTO hire_request_approvals (
                        hire_request_id, approved_request_version, approved_request_revision, request_version_hash,
                        profile_revision_id, profile_build_id, image_digest, host_id, platform,
                        cpu_limit, memory_limit_mib, pids_limit, approval_identity, approved_at,
                        employee_id, runtime_binding_id, worker_id, revision)
                    VALUES ($id, $version, $revision, $requestHash, $profile, $build, $digest, $host, $platform,
                        $cpu, $memory, $pids, $identity, $now, NULL, NULL, NULL, 1)
                    """,
                    ("$id", id), ("$version", frozenVersion), ("$revision", current.Revision),
                    ("$requestHash", current.RequestVersionHash), ("$profile", request.ProfileRevisionId),
                    ("$build", build.Id), ("$digest", build.ImageDigest), ("$host", hostId),
                    ("$platform", build.Platform), ("$cpu", current.CpuLimit), ("$memory", current.MemoryLimitMiB),
                    ("$pids", current.PidsLimit), ("$identity", identity), ("$now", now));
                var affected = Execute(connection, transaction,
                    """
                    UPDATE hire_requests
                    SET approved_request_version = $version,
                        owner_approval = $identity,
                        container_profile_revision_id = $profile,
                        state = 'Approved',
                        status_detail = NULL,
                        revision = revision + 1,
                        updated_at = $now
                    WHERE id = $id AND revision = $revision AND state = 'Requested'
                    """,
                    ("$version", frozenVersion), ("$identity", identity), ("$profile", request.ProfileRevisionId),
                    ("$now", now), ("$id", id), ("$revision", request.ExpectedRevision));
                if (affected != 1)
                    throw new OrganizationConcurrencyException("The hire request changed before approval.");
                AppendHireRequestEvent(connection, transaction, id, HireRequestStates.Approved, current.Revision + 1, frozenVersion, now);
                transaction.Commit();
                return ReadHireRequests(connection, id).Single();
            }
        });
    }

    /// <summary>
    /// Atomically allocates the managed employee and its DeveloperContainer
    /// runtime binding from an approved hire and freezes the per-binding resources
    /// provisioning will read. Idempotent: a replay returns the existing
    /// identities with <c>Created=false</c>. The hire state is never changed here.
    /// </summary>
    public ManagedEmployeeCreation CreateManagedEmployeeFromHire(string hireRequestId)
    {
        if (!IsBoundedIdentifier(hireRequestId, OrganizationIds.HireRequestPrefix))
            throw new OrganizationValidationException("A stable hire request id is required.");

        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var current = ReadHireRequest(connection, transaction, hireRequestId)
                    ?? throw new OrganizationNotFoundException($"Hire request '{hireRequestId}' does not exist.");
                var approval = ReadApproval(connection, transaction, hireRequestId)
                    ?? throw new OrganizationConcurrencyException("The hire request has no owner approval.");

                if (current.State is not (HireRequestStates.Approved or HireRequestStates.Provisioning
                    or HireRequestStates.Orienting or HireRequestStates.Ready
                    or HireRequestStates.Failed or HireRequestStates.Interrupted or HireRequestStates.Uncertain))
                    throw new OrganizationConcurrencyException($"A hire request in state {current.State} cannot create an employee.");

                if (approval.EmployeeId is not null && approval.RuntimeBindingId is not null)
                {
                    transaction.Commit();
                    return BuildManagedCreation(connection, null, current, approval, approval.EmployeeId, approval.RuntimeBindingId, created: false);
                }

                if (approval.EmployeeId is not null || approval.RuntimeBindingId is not null)
                    throw new OrganizationStoreCorruptException("The owner approval carries an incomplete employee/binding link.");

                // The requested department/role must still resolve inside the
                // organization before the employee is inserted; a hire approved
                // against a moved or deleted role must not create a dangling row.
                EnsureHireRelationships(connection, transaction, current.OrganizationId, current.DepartmentId, current.RoleId);

                var employeeId = OrganizationIds.NewEmployeeId();
                var bindingId = OrganizationIds.NewRuntimeBindingId();
                var slug = BuildManagedSlug(current.RequestedDisplayName, hireRequestId);
                var displayName = BuildManagedDisplayName(connection, transaction, current.OrganizationId, current.RequestedDisplayName, hireRequestId);
                var now = Timestamp();

                Execute(connection, transaction,
                    """
                    INSERT INTO employees (
                        id, organization_id, department_id, role_id, slug, display_name, purpose,
                        instructions, rules, restrictions, created_at, updated_at, revision)
                    VALUES ($id, $organization, $department, $role, $slug, $name, $purpose,
                        $instructions, $rules, $restrictions, $now, $now, 1)
                    """,
                    ("$id", employeeId), ("$organization", current.OrganizationId), ("$department", current.DepartmentId),
                    ("$role", current.RoleId), ("$slug", slug), ("$name", displayName), ("$purpose", current.Purpose),
                    ("$instructions", ManagedEmployeeDefaults.Instructions), ("$rules", ManagedEmployeeDefaults.Rules),
                    ("$restrictions", ManagedEmployeeDefaults.Restrictions), ("$now", now));

                Execute(connection, transaction,
                    """
                    INSERT INTO runtime_bindings (
                        id, employee_id, placement, container_ref, volume_ref, home_ref, workspace_ref,
                        session_ref, tmux_owner_token, ownership_epoch, created_at, updated_at, revision)
                    VALUES ($id, $employee, 'DeveloperContainer', NULL, NULL, NULL, NULL, NULL, $token, NULL, $now, $now, 1)
                    """,
                    ("$id", bindingId), ("$employee", employeeId), ("$token", OrganizationIds.NewOwnerToken()), ("$now", now));

                // The managed employee must resolve the exact four-layer
                // composition (organization, department, role, employee). The
                // shared first three layers already exist; this seeds the unique
                // active employee fragment and its bounded facts in the same
                // transaction. No assignment is composed here.
                InsertManagedEmployeeOrientationV10(
                    connection,
                    transaction,
                    current.OrganizationId,
                    employeeId,
                    displayName,
                    current.Purpose,
                    ManagedEmployeeDefaults.Instructions,
                    ManagedEmployeeDefaults.Rules,
                    ManagedEmployeeDefaults.Restrictions,
                    now);

                Execute(connection, transaction,
                    """
                    INSERT INTO managed_enrollment_resources (
                        runtime_binding_id, cpu_limit, memory_limit_mib, pids_limit,
                        approved_profile_revision_id, approved_profile_build_id, approved_image_digest,
                        approved_host_id, platform, created_at, revision)
                    VALUES ($binding, $cpu, $memory, $pids, $revision, $build, $digest, $host, $platform, $now, 1)
                    """,
                    ("$binding", bindingId), ("$cpu", approval.CpuLimit), ("$memory", approval.MemoryLimitMiB),
                    ("$pids", approval.PidsLimit), ("$revision", approval.ProfileRevisionId),
                    ("$build", approval.ProfileBuildId), ("$digest", approval.ImageDigest),
                    ("$host", approval.HostId), ("$platform", approval.Platform), ("$now", now));

                var linked = Execute(connection, transaction,
                    """
                    UPDATE hire_request_approvals
                    SET employee_id = $employee, runtime_binding_id = $binding, revision = revision + 1
                    WHERE hire_request_id = $id AND employee_id IS NULL AND runtime_binding_id IS NULL AND revision = $revision
                    """,
                    ("$employee", employeeId), ("$binding", bindingId), ("$id", hireRequestId), ("$revision", approval.Revision));
                if (linked != 1)
                    throw new OrganizationConcurrencyException("The owner approval changed before the employee link was recorded.");

                transaction.Commit();
                var updated = ReadApproval(connection, null, hireRequestId)!;
                return BuildManagedCreation(connection, null, current, updated, employeeId, bindingId, created: true);
            }
        });
    }

    /// <summary>
    /// Binds the later worker enrollment to the exact approved employee/binding.
    /// This is a provisioning helper: it never changes the freeze or the status,
    /// only records the allocated worker identity under the expected approval
    /// revision.
    /// </summary>
    public HireRequestApprovalRecord LinkHireWorker(string hireRequestId, string employeeId, string runtimeBindingId, string workerId, int expectedApprovalRevision)
    {
        if (!IsBoundedIdentifier(hireRequestId, OrganizationIds.HireRequestPrefix)
            || !IsBoundedIdentifier(employeeId, OrganizationIds.EmployeePrefix)
            || !IsBoundedIdentifier(runtimeBindingId, OrganizationIds.RuntimeBindingPrefix)
            || !IsBoundedIdentifier(workerId, "wrk-")
            || expectedApprovalRevision < 1)
            throw new OrganizationValidationException("The exact hire, employee, binding, worker and approval revision are required.");

        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var approval = ReadApproval(connection, transaction, hireRequestId)
                    ?? throw new OrganizationNotFoundException($"Hire request '{hireRequestId}' has no owner approval.");
                if (approval.Revision != expectedApprovalRevision)
                    throw new OrganizationConcurrencyException("The owner approval changed; reload and retry.");
                if (!string.Equals(approval.EmployeeId, employeeId, StringComparison.Ordinal)
                    || !string.Equals(approval.RuntimeBindingId, runtimeBindingId, StringComparison.Ordinal))
                    throw new OrganizationValidationException("The approval is not linked to that exact employee and binding.");
                if (approval.WorkerId is not null)
                {
                    if (!string.Equals(approval.WorkerId, workerId, StringComparison.Ordinal))
                        throw new OrganizationConcurrencyException("The approval already carries a different worker identity.");
                    transaction.Commit();
                    return approval;
                }

                var affected = Execute(connection, transaction,
                    "UPDATE hire_request_approvals SET worker_id = $worker, revision = revision + 1 WHERE hire_request_id = $id AND revision = $revision AND worker_id IS NULL",
                    ("$worker", workerId), ("$id", hireRequestId), ("$revision", expectedApprovalRevision));
                if (affected != 1)
                    throw new OrganizationConcurrencyException("The owner approval changed before the worker link was recorded.");
                transaction.Commit();
                return ReadApproval(connection, null, hireRequestId)!;
            }
        });
    }

    /// <summary>
    /// Advances one hire request along the post-approval lifecycle. The request
    /// and rejection states are owned by their own methods and are not reachable
    /// here. Each transition appends exactly one immutable event.
    /// </summary>
    public HireRequestSummary TransitionHireRequestState(string id, int expectedRevision, string from, string to, string? statusDetail = null)
    {
        if (!IsBoundedIdentifier(id, OrganizationIds.HireRequestPrefix) || expectedRevision < 1)
            throw new OrganizationValidationException("A stable hire request id and current revision are required.");
        if (!CanTransitionHireRequest(from, to))
            throw new OrganizationValidationException($"The hire request cannot move from {from} to {to}.");
        var detail = statusDetail is null ? null : SanitizeStatusDetail(statusDetail);
        if (to == HireRequestStates.Ready) detail = null;

        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var current = ReadHireRequest(connection, transaction, id)
                    ?? throw new OrganizationNotFoundException($"Hire request '{id}' does not exist.");
                if (current.Revision != expectedRevision || !string.Equals(current.State, from, StringComparison.Ordinal))
                    throw new OrganizationConcurrencyException("The hire request changed or is no longer in the expected state.");

                var now = Timestamp();
                var affected = Execute(connection, transaction,
                    """
                    UPDATE hire_requests
                    SET state = $to, status_detail = $detail, revision = revision + 1, updated_at = $now
                    WHERE id = $id AND revision = $revision AND state = $from
                    """,
                    ("$to", to), ("$detail", detail), ("$now", now), ("$id", id), ("$revision", expectedRevision), ("$from", from));
                if (affected != 1)
                    throw new OrganizationConcurrencyException("The hire request changed before the transition.");
                var eventHash = HashHireValue(string.Join('\n',
                    "transition", id, from, to, (expectedRevision + 1).ToString(CultureInfo.InvariantCulture), detail ?? string.Empty));
                AppendHireRequestEvent(connection, transaction, id, to, expectedRevision + 1, eventHash, now);
                transaction.Commit();
                return ReadHireRequests(connection, id).Single();
            }
        });
    }

    private static bool CanTransitionHireRequest(string from, string to) => (from, to) switch
    {
        (HireRequestStates.Approved, HireRequestStates.Provisioning) => true,
        (HireRequestStates.Provisioning, HireRequestStates.Orienting) => true,
        (HireRequestStates.Provisioning, HireRequestStates.Failed) => true,
        (HireRequestStates.Provisioning, HireRequestStates.Interrupted) => true,
        (HireRequestStates.Provisioning, HireRequestStates.Uncertain) => true,
        (HireRequestStates.Uncertain, HireRequestStates.Provisioning) => true,
        (HireRequestStates.Interrupted, HireRequestStates.Provisioning) => true,
        // Failed is terminal for automatic processing. It can return to
        // Provisioning only through the owner-explicit recovery coordinator after
        // exact applied-plan/resource reconciliation.
        (HireRequestStates.Failed, HireRequestStates.Provisioning) => true,
        (HireRequestStates.Orienting, HireRequestStates.Ready) => true,
        (HireRequestStates.Orienting, HireRequestStates.Failed) => true,
        (HireRequestStates.Orienting, HireRequestStates.Interrupted) => true,
        (HireRequestStates.Orienting, HireRequestStates.Uncertain) => true,
        _ => false,
    };

    private static string ValidateApprovalIdentity(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > MaximumApprovalIdentityLength)
            throw new OrganizationValidationException($"An approval identity of 1 to {MaximumApprovalIdentityLength} characters is required.");
        if (trimmed.Any(char.IsControl))
            throw new OrganizationValidationException("The approval identity must not contain control characters.");
        return trimmed;
    }

    private static string SanitizeStatusDetail(string value)
    {
        var sanitized = new string(value.Where(ch => !char.IsControl(ch)).Take(MaximumStatusDetailLength).ToArray()).Trim();
        if (sanitized.Length > MaximumStatusDetailLength)
            sanitized = sanitized[..MaximumStatusDetailLength];
        return sanitized;
    }

    private static void EnsureBuildableRevision(SqliteConnection connection, SqliteTransaction transaction, string profileRevisionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT p.status, (r.revision_number = p.current_revision_number)
            FROM container_profile_revisions r JOIN container_profiles p ON p.id = r.profile_id
            WHERE r.id = $revision
            """;
        command.Parameters.AddWithValue("$revision", profileRevisionId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new OrganizationNotFoundException($"Container profile revision '{profileRevisionId}' does not exist.");
        if (!string.Equals(reader.GetString(0), ContainerProfileStatuses.Active, StringComparison.Ordinal))
            throw new OrganizationConcurrencyException("A retired container profile cannot be approved.");
        if (reader.GetInt64(1) != 1)
            throw new OrganizationConcurrencyException("Only the current profile revision can be approved.");
    }

    private static ProfileBuildRecord? ReadVerifiedBuild(SqliteConnection connection, SqliteTransaction transaction, string profileRevisionId, string hostId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT id, profile_revision_id, host_id, base_image_digest, platform, context_hash, result_tag, state, image_digest, verified, failure_summary, evidence_hash, requested_by, revision, started_at, finished_at, created_at, updated_at FROM profile_builds WHERE profile_revision_id = $revision AND host_id = $host AND state = 'built' AND verified = 1";
        command.Parameters.AddWithValue("$revision", profileRevisionId);
        command.Parameters.AddWithValue("$host", hostId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var record = new ProfileBuildRecord(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
            reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetInt32(9) == 1, reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11), reader.GetString(12), reader.GetInt32(13),
            reader.IsDBNull(14) ? null : DateTimeOffset.Parse(reader.GetString(14), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(15) ? null : DateTimeOffset.Parse(reader.GetString(15), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(16), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(17), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        if (record.ImageDigest is null)
            throw new OrganizationStoreCorruptException("A verified profile build has no image digest.");
        return record;
    }

    private static (int Enabled, string Status, string CapabilityStatus, int LimitsSupported, string? ImagePlatform, int CpuCount, long MemoryBytes)
        ReadApprovalHost(SqliteConnection connection, SqliteTransaction transaction, string hostId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT enabled, status, capability_status, limits_supported, image_platform, cpu_count, memory_bytes FROM execution_hosts WHERE id = $id";
        command.Parameters.AddWithValue("$id", hostId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new OrganizationNotFoundException($"Execution host '{hostId}' does not exist.");
        if (reader.IsDBNull(5) || reader.IsDBNull(6))
            throw new OrganizationValidationException("The execution host has no probed CPU or memory capacity.");
        return (reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt32(5), reader.GetInt64(6));
    }

    private static HireRequestApprovalRecord? ReadApproval(SqliteConnection connection, SqliteTransaction? transaction, string hireRequestId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT hire_request_id, approved_request_version, approved_request_revision, request_version_hash,
                   profile_revision_id, profile_build_id, image_digest, host_id, platform,
                   cpu_limit, memory_limit_mib, pids_limit, approval_identity, approved_at,
                   employee_id, runtime_binding_id, worker_id, revision
            FROM hire_request_approvals WHERE hire_request_id = $id
            """;
        command.Parameters.AddWithValue("$id", hireRequestId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new HireRequestApprovalRecord(
            reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
            reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11), reader.GetString(12),
            DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(14) ? null : reader.GetString(14), reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16), reader.GetInt32(17));
    }

    private static HireRequestApprovalRecord? ReadApprovalByBinding(SqliteConnection connection, SqliteTransaction? transaction, string runtimeBindingId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT hire_request_id, approved_request_version, approved_request_revision, request_version_hash,
                   profile_revision_id, profile_build_id, image_digest, host_id, platform,
                   cpu_limit, memory_limit_mib, pids_limit, approval_identity, approved_at,
                   employee_id, runtime_binding_id, worker_id, revision
            FROM hire_request_approvals WHERE runtime_binding_id = $binding
            """;
        command.Parameters.AddWithValue("$binding", runtimeBindingId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new HireRequestApprovalRecord(
            reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
            reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11), reader.GetString(12),
            DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(14) ? null : reader.GetString(14), reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16), reader.GetInt32(17));
    }

    private static ManagedEnrollmentResourcesRecord? ReadManagedResources(SqliteConnection connection, SqliteTransaction? transaction, string runtimeBindingId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT runtime_binding_id, cpu_limit, memory_limit_mib, pids_limit,
                   approved_profile_revision_id, approved_profile_build_id, approved_image_digest,
                   approved_host_id, platform, created_at, revision
            FROM managed_enrollment_resources WHERE runtime_binding_id = $id
            """;
        command.Parameters.AddWithValue("$id", runtimeBindingId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new ManagedEnrollmentResourcesRecord(
            reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
            DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.GetInt32(10));
    }

    private static ManagedEmployeeCreation BuildManagedCreation(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        HireRequestSummary request,
        HireRequestApprovalRecord approval,
        string employeeId,
        string runtimeBindingId,
        bool created)
    {
        string slug;
        string displayName;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT slug, display_name FROM employees WHERE id = $id";
            command.Parameters.AddWithValue("$id", employeeId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                throw new OrganizationStoreCorruptException($"The approved hire employee '{employeeId}' is missing.");
            slug = reader.GetString(0);
            displayName = reader.GetString(1);
        }

        return new ManagedEmployeeCreation(
            request.Id, employeeId, runtimeBindingId, slug,
            displayName,
            RuntimePlacements.DeveloperContainer,
            created,
            approval.ProfileRevisionId, approval.ProfileBuildId, approval.ImageDigest, approval.HostId, approval.Platform,
            approval.CpuLimit, approval.MemoryLimitMiB, approval.PidsLimit,
            approval.WorkerId);
    }

    /// <summary>
    /// Deterministic, bounded slug for a managed employee. ASCII lowercase,
    /// non-alphanumerics folded to hyphens, trimmed, with a stable per-hire suffix
    /// so two requests for the same display name never collide.
    /// </summary>
    internal static string BuildManagedSlug(string displayName, string hireRequestId)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var character in displayName)
        {
            var lower = char.ToLowerInvariant(character);
            builder.Append(lower is >= 'a' and <= 'z' or >= '0' and <= '9' ? lower : '-');
        }

        var slug = builder.ToString().Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        if (slug.Length == 0) slug = "employee";
        var suffix = new string(hireRequestId.Reverse().Take(8).Reverse().ToArray()).ToLowerInvariant();
        var maximumBase = 63 - 1 - suffix.Length;
        if (slug.Length > maximumBase) slug = slug[..maximumBase].TrimEnd('-');
        return slug + "-" + suffix;
    }

    /// <summary>
    /// A display name unique within the organization. The requested name is used
    /// verbatim when it is free; otherwise a stable hire-specific 8-character
    /// suffix is appended, truncating the base so the result stays within 128
    /// characters. A further numeric fallback guards the (astronomically unlikely)
    /// suffix collision.
    /// </summary>
    private static string BuildManagedDisplayName(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string organizationId,
        string requested,
        string hireRequestId)
    {
        using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT COUNT(*) FROM employees WHERE organization_id = $organization AND display_name = $name";
            check.Parameters.AddWithValue("$organization", organizationId);
            check.Parameters.AddWithValue("$name", requested);
            if (Convert.ToInt32(check.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return requested;
        }

        var suffix = new string(hireRequestId.Reverse().Take(8).Reverse().ToArray()).ToLowerInvariant();
        var marker = " \u00b7 " + suffix;
        var candidate = AppendSuffix(requested, marker);
        var attempt = 0;
        while (true)
        {
            using var exists = connection.CreateCommand();
            exists.Transaction = transaction;
            exists.CommandText = "SELECT COUNT(*) FROM employees WHERE organization_id = $organization AND display_name = $name";
            exists.Parameters.AddWithValue("$organization", organizationId);
            exists.Parameters.AddWithValue("$name", candidate);
            if (Convert.ToInt32(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return candidate;

            attempt++;
            candidate = AppendSuffix(requested, marker + attempt.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static string AppendSuffix(string requested, string suffix)
    {
        var maximumBase = MaxDisplayNameLength - suffix.Length;
        if (maximumBase < 1) maximumBase = 1;
        var baseName = requested.Length > maximumBase ? requested[..maximumBase].TrimEnd() : requested;
        if (baseName.Length == 0) baseName = "Employee";
        return baseName + suffix;
    }

    private static string HashHireApproval(string value) => HashHireValue(value);
}

/// <summary>
/// Fixed, safe managed-employee defaults. A managed hire never inherits the
/// requesting employee's text; it is oriented by the versioned host fragments, so
/// these fields are non-empty and deliberately contain no controller secrets or
/// unrestricted host authority.
/// </summary>
public static class ManagedEmployeeDefaults
{
    public const string Instructions = "Follow the current versioned orientation delivered by the control host. Treat it as authoritative, request changes through the owner, and report uncertain effects instead of retrying them.";
    public const string Rules = "Use stable persisted identities; work only inside your own runtime and workspace; preserve existing sessions and history; treat owner approval as a host record, never a model assertion.";
    public const string Restrictions = "No controller secrets, no direct access to the authoritative store, no unrestricted Docker or host authority, and no access to another employee's history.";
}
