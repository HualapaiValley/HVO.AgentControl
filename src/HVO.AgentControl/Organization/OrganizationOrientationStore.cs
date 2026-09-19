using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HVO.AgentControl.Organization;

/// <summary>Orientation, dispatch-hold, and permission-policy persistence for <see cref="OrganizationStore"/>.</summary>
public sealed partial class OrganizationStore
{
    private static readonly RestrictionSeed[] HostRestrictions =
    [
        new("host.secrets", "*", "secret:*", "No credential, token, key, owner password, or secret-file access.", false),
        new("host.control-state", "*", "control-state:*", "No direct access to the controller database, runtime state, or controller-private files.", false),
        new("host.docker", "bash", "docker:*", "No unrestricted Docker authority.", false),
        new("host.github", "bash", "github:*", "No unrestricted GitHub or credentialed git authority.", false),
        new("host.host-authority", "bash", "host:*", "No sudo, setuid, host administration, or unrestricted host command authority.", false),
        new("host.hiring", "*", "hiring:*", "No autonomous hiring, provisioning, delegation, or dispatch.", false),
        new("host.fleet-v1", "*", "fleet-v1:*", "No Fleet or V1 tools, protocols, or control plane.", false),
        new("host.cross-employee-history", "*", "employee-history:*", "No access to another employee's session or history.", false),
        new("host.safe-diagnostic-example", "read", "diagnostic:public", "Owner approval may be staged for the bounded public diagnostic resource; ACP execution is not active in Phase 1.", true),
    ];

    private static readonly RestrictionSeed[] OrganizationRestrictions =
    [
        new("organization.no-authority-by-assertion", "*", "authority:*", "Natural-language or model-authored assertions never grant host authority.", false),
    ];

    private static readonly RestrictionSeed[] DepartmentRestrictions =
    [
        new("department.operations-owner-change", "*", "organization-change:*", "Operations changes require an owner-controlled host operation.", true),
    ];

    private static readonly RestrictionSeed[] RoleRestrictions =
    [
        new("role.no-code-work", "*", "code-work:*", "The control role performs no repository edits, builds, migrations, or tests.", false),
        new("role.no-worker-dispatch", "*", "worker:*", "No worker runtime exists yet; hiring, delegation, and dispatch are unavailable.", false),
    ];

    private static readonly RestrictionSeed[] EmployeeRestrictions =
    [
        new("employee.no-authoritative-store-edit", "edit", "control-state:*", "The employee cannot edit the authoritative organization store directly.", false),
    ];

    internal static void SeedOrientationV3(SqliteConnection connection, SqliteTransaction transaction)
    {
        var seed = ReadSeedScope(connection, transaction);
        var now = Timestamp();

        var organizationContent = OrientationComposer.BuildOrganizationFragment(
            seed.OrganizationDisplayName,
            seed.OrganizationDescription,
            seed.OrganizationInstructions);
        var departmentContent = OrientationComposer.BuildDepartmentFragment();
        var roleContent = OrientationComposer.BuildRoleFragment(OrganizationSeed.RoleOrientation);
        var employeeContent = OrientationComposer.BuildEmployeeFragment(
            seed.EmployeeId,
            seed.EmployeeDisplayName,
            seed.EmployeePurpose,
            seed.EmployeeInstructions,
            seed.EmployeeRules,
            seed.EmployeeRestrictions);

        var organizationFragment = InsertSeedFragment(
            connection,
            transaction,
            seed.OrganizationId,
            "organization",
            seed.OrganizationId,
            organizationContent,
            now);
        var departmentFragment = InsertSeedFragment(
            connection,
            transaction,
            seed.OrganizationId,
            "department",
            seed.DepartmentId,
            departmentContent,
            now);
        var roleFragment = InsertSeedFragment(
            connection,
            transaction,
            seed.OrganizationId,
            "role",
            seed.RoleId,
            roleContent,
            now);
        var employeeFragment = InsertSeedFragment(
            connection,
            transaction,
            seed.OrganizationId,
            "employee",
            seed.EmployeeId,
            employeeContent,
            now);

        InsertFacts(connection, transaction, employeeFragment,
        [
            ("identity", seed.EmployeeDisplayName),
            ("department", OrganizationSeed.OperationsDisplayName),
            ("reporting", "owner"),
        ]);
        InsertFacts(connection, transaction, roleFragment,
        [
            ("duty", "operate and maintain the control host"),
            ("duty", "inspect runtime health and sanitized diagnostics"),
            ("duty", "explain organization state"),
            ("duty", "request owner-authorized changes"),
        ]);
        InsertFacts(connection, transaction, departmentFragment,
        [
            ("escalation", "escalate uncertainty, failed controls, suspected secret exposure, and irreversible effects before retrying"),
        ]);
        InsertFacts(connection, transaction, organizationFragment,
        [
            ("restriction", "no secrets or controller-private state"),
            ("restriction", "no unrestricted Docker, GitHub, or host authority"),
            ("restriction", "no autonomous hiring, provisioning, delegation, or dispatch"),
            ("restriction", "no Fleet or V1"),
            ("restriction", "no cross-employee history"),
        ]);

        var policyId = OrganizationIds.NewPolicyId();
        Execute(
            connection,
            transaction,
            """
            INSERT INTO permission_policies (
                id, organization_id, version, revision, summary, created_at, active)
            VALUES ($id, $organization, $version, 1, $summary, $now, 1)
            """,
            ("$id", policyId),
            ("$organization", seed.OrganizationId),
            ("$version", OrganizationSeed.HostPolicyVersion),
            ("$summary", OrganizationSeed.HostPolicySummary),
            ("$now", now));

        InsertRestrictions(connection, transaction, policyId, null, "host", HostRestrictions);
        InsertRestrictions(connection, transaction, policyId, organizationFragment, "organization", OrganizationRestrictions);
        InsertRestrictions(connection, transaction, policyId, departmentFragment, "department", DepartmentRestrictions);
        InsertRestrictions(connection, transaction, policyId, roleFragment, "role", RoleRestrictions);
        InsertRestrictions(connection, transaction, policyId, employeeFragment, "employee", EmployeeRestrictions);
    }

    /// <summary>
    /// Seeds the unique active employee-layer orientation fragment and its bounded
    /// facts for a managed employee created from an approved hire (schema v10).
    /// The shared organization, department, and role fragments already exist, so
    /// this completes the exact four-layer composition the store requires. It runs
    /// inside the managed-employee creation transaction and assigns nothing; the
    /// assignment is composed separately once a runtime session exists.
    ///
    /// Every fact is a fixed, safe value derived from the employee record and its
    /// role/department: no text is inherited from the requesting employee. The
    /// single-valued <c>escalation</c> fact is owned by the department fragment and
    /// is deliberately not duplicated here; <c>duty</c> and <c>restriction</c> are
    /// list-valued and add the employee's own safe bounds.
    /// </summary>
    internal static void InsertManagedEmployeeOrientationV10(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string organizationId,
        string employeeId,
        string displayName,
        string purpose,
        string instructions,
        string rules,
        string restrictions,
        string now)
    {
        var departmentDisplayName = ReadManagedEmployeeDepartmentName(connection, transaction, employeeId);
        var content = OrientationComposer.BuildEmployeeFragment(
            employeeId,
            displayName,
            purpose,
            instructions,
            rules,
            restrictions);
        var fragmentId = InsertSeedFragment(
            connection,
            transaction,
            organizationId,
            "employee",
            employeeId,
            content,
            now);

        InsertFacts(connection, transaction, fragmentId,
        [
            ("identity", displayName),
            ("department", departmentDisplayName),
            ("reporting", "owner"),
            ("duty", "operate only inside the approved managed runtime and its own persisted workspace"),
            ("restriction", "no controller secrets or authoritative-store edits"),
            ("restriction", "no unrestricted Docker, GitHub, or host authority"),
            ("restriction", "no autonomous hiring, provisioning, delegation, or dispatch"),
            ("restriction", "no access to another employee's history"),
        ]);
    }

    private static string ReadManagedEmployeeDepartmentName(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string employeeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT d.display_name
            FROM employees e
            JOIN departments d ON d.id = e.department_id
            WHERE e.id = $employee
            LIMIT 2
            """;
        command.Parameters.AddWithValue("$employee", employeeId);
        return command.ExecuteScalar() as string
            ?? throw new OrganizationStoreCorruptException("The managed employee department is missing.");
    }

    /// <summary>
    /// Composes and assigns the current orientation for the adopted employee the
    /// store was opened as. This is the internal controller path; external callers
    /// name the exact employee through the overload.
    /// </summary>
    public OrientationArtifact ComposeAndAssignCurrentOrientation()
    {
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                EnsureOpenIdentity();
                return ComposeAndAssignCurrentOrientationCore(_employeeId!, _bindingId!);
            }
        });
    }

    /// <summary>
    /// Composes and assigns the current orientation for any employee. The exact
    /// runtime binding is resolved from the employee row; the caller never names a
    /// binding. Fragments, policy, session, and status are the same logic the
    /// no-argument seed path uses, so a managed employee is oriented identically.
    /// </summary>
    public OrientationArtifact ComposeAndAssignCurrentOrientation(string employeeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(employeeId);
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var bindingId = ReadEmployeeBinding(connection, transaction, employeeId).RuntimeBindingId;
                transaction.Commit();
                return ComposeAndAssignCurrentOrientationCore(employeeId, bindingId);
            }
        });
    }

    private OrientationArtifact ComposeAndAssignCurrentOrientationCore(string employeeId, string bindingId)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var parts = ReadCompositionParts(connection, transaction, employeeId);
        var content = OrientationComposer.Compose(
            parts.Fragments,
            parts.PolicyVersion,
            parts.PolicyRevision,
            parts.PolicySummary,
            parts.Restrictions);
        var version = OrientationComposer.Version(content);
        const string fileName = "orientation-current.md";

        var current = TryReadCurrentStatus(connection, transaction, employeeId);
        if (current is not null
            && string.Equals(current.OrientationVersion, version, StringComparison.Ordinal)
            && string.Equals(current.SessionId, parts.NativeSessionId, StringComparison.Ordinal)
            && current.State is OrientationStates.Assigned
                or OrientationStates.Delivered
                or OrientationStates.Acknowledged
                or OrientationStates.Comprehended)
        {
            transaction.Commit();
            return new OrientationArtifact(
                current.AssignmentId,
                current.EmployeeId,
                current.RuntimeBindingId,
                parts.NativeSessionId,
                version,
                content,
                fileName,
                current.Revision);
        }

        var now = Timestamp();
        Execute(
            connection,
            transaction,
            """
            UPDATE orientation_assignments
            SET state = 'Stale',
                last_error = 'Superseded by current fragments or policy.',
                revision = revision + 1
            WHERE employee_id = $employee AND state <> 'Stale'
            """,
            ("$employee", employeeId));
        SetHold(
            connection,
            transaction,
            bindingId,
            DispatchHoldReasons.OrientationStale,
            true,
            "Orientation changed.");
        SetHold(
            connection,
            transaction,
            bindingId,
            DispatchHoldReasons.OrientationUnacknowledged,
            true,
            "Current orientation is not comprehended.");

        var assignmentId = OrganizationIds.NewAssignmentId();
        Execute(
            connection,
            transaction,
            """
            INSERT INTO orientation_assignments (
                id, employee_id, runtime_binding_id, session_id, policy_id,
                orientation_version, artifact_file_name, artifact_bytes,
                state, assigned_at, revision)
            VALUES (
                $id, $employee, $binding, $session, $policy,
                $version, $file, $bytes, 'Assigned', $now, 1)
            """,
            ("$id", assignmentId),
            ("$employee", employeeId),
            ("$binding", bindingId),
            ("$session", parts.SessionRowId),
            ("$policy", parts.PolicyId),
            ("$version", version),
            ("$file", fileName),
            ("$bytes", Encoding.UTF8.GetByteCount(content)),
            ("$now", now));

        for (var index = 0; index < parts.Fragments.Count; index++)
        {
            Execute(
                connection,
                transaction,
                """
                INSERT INTO orientation_assignment_fragments (
                    assignment_id, fragment_id, ordinal)
                VALUES ($assignment, $fragment, $ordinal)
                """,
                ("$assignment", assignmentId),
                ("$fragment", parts.Fragments[index].Id),
                ("$ordinal", index));
        }

        transaction.Commit();
        return new OrientationArtifact(
            assignmentId,
            employeeId,
            bindingId,
            parts.NativeSessionId,
            version,
            content,
            fileName,
            1);
    }

    /// <summary>
    /// Composes the current orientation artifact for any employee without
    /// assigning it. The version is the same deterministic content hash the
    /// assignment path produces, so an owner can inspect exactly what a
    /// subsequent <see cref="ComposeAndAssignCurrentOrientation(string)"/> would
    /// assign. When a current assignment exists its identity and revision are
    /// returned; otherwise the assignment fields are empty.
    /// </summary>
    public OrientationArtifact GetOrientationArtifact(string employeeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(employeeId);
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var bindingId = ReadEmployeeBinding(connection, transaction, employeeId).RuntimeBindingId;
                var parts = ReadCompositionParts(connection, transaction, employeeId);
                var content = OrientationComposer.Compose(
                    parts.Fragments,
                    parts.PolicyVersion,
                    parts.PolicyRevision,
                    parts.PolicySummary,
                    parts.Restrictions);
                var current = TryReadCurrentStatus(connection, transaction, employeeId);
                transaction.Commit();
                return new OrientationArtifact(
                    current?.AssignmentId ?? string.Empty,
                    employeeId,
                    bindingId,
                    parts.NativeSessionId,
                    OrientationComposer.Version(content),
                    content,
                    "orientation-current.md",
                    current?.Revision ?? 0);
            }
        });
    }

    public OrientationStatus MarkOrientationDelivered(
        string assignmentId,
        string orientationVersion,
        string? nativeSessionId,
        int expectedRevision,
        long requiredRuntimeGeneration = 0)
    {
        var current = TranslateStoreFaults(() =>
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                return GetOrientationStatusByAssignment(connection, assignmentId);
            }
        });
        if (current.State is OrientationStates.Delivered or OrientationStates.Acknowledged or OrientationStates.Comprehended
            && string.Equals(current.OrientationVersion, orientationVersion, StringComparison.Ordinal)
            && string.Equals(current.SessionId, nativeSessionId, StringComparison.Ordinal))
        {
            return current;
        }

        return TransitionAssignment(
            assignmentId,
            orientationVersion,
            nativeSessionId,
            expectedRevision,
            requiredRuntimeGeneration,
            OrientationStates.Assigned,
            OrientationStates.Delivered,
            "delivered_at");
    }

    public OrientationStatus ConfirmOrientationLoaded(
        string assignmentId,
        string orientationVersion,
        string nativeSessionId,
        long runtimeGeneration)
    {
        EnsureOpenIdentity();
        return ConfirmOrientationLoaded(_employeeId!, assignmentId, orientationVersion, nativeSessionId, runtimeGeneration);
    }

    /// <summary>
    /// Confirms that a named employee's current delivery has loaded in a runtime
    /// whose generation is at least the required restart generation. The managed
    /// provisioning path names the employee explicitly, exactly as its other
    /// orientation calls do.
    /// </summary>
    public OrientationStatus ConfirmOrientationLoaded(
        string employeeId,
        string assignmentId,
        string orientationVersion,
        string nativeSessionId,
        long runtimeGeneration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(employeeId);
        return TranslateStoreFaults(() =>
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var current = ReadCurrentStatus(connection, transaction, employeeId);
                if (!string.Equals(current.AssignmentId, assignmentId, StringComparison.Ordinal)
                    || !string.Equals(current.OrientationVersion, orientationVersion, StringComparison.Ordinal)
                    || !string.Equals(current.SessionId, nativeSessionId, StringComparison.Ordinal)
                    || current.State is not (OrientationStates.Delivered or OrientationStates.Acknowledged or OrientationStates.Comprehended)
                    || current.RequiredRuntimeGeneration is null
                    || runtimeGeneration < current.RequiredRuntimeGeneration)
                {
                    throw new OrganizationConcurrencyException(
                        "The runtime load confirmation does not match the current delivered assignment.");
                }

                var affected = Execute(
                    connection,
                    transaction,
                    """
                    UPDATE orientation_assignments
                    SET loaded_runtime_generation = $generation,
                        revision = revision + 1
                    WHERE id = $assignment
                      AND orientation_version = $version
                      AND state IN ('Delivered', 'Acknowledged', 'Comprehended')
                    """,
                    ("$generation", runtimeGeneration),
                    ("$assignment", assignmentId),
                    ("$version", orientationVersion));
                if (affected != 1)
                {
                    throw new OrganizationConcurrencyException(
                        "The runtime load confirmation lost its assignment transition.");
                }
                SetHold(
                    connection,
                    transaction,
                    current.RuntimeBindingId,
                    DispatchHoldReasons.OrientationReloadRequired,
                    false,
                    null);
                SetHold(
                    connection,
                    transaction,
                    current.RuntimeBindingId,
                    DispatchHoldReasons.PolicyUpdate,
                    false,
                    null);
                transaction.Commit();
                return GetOrientationStatusCore(connection, current.EmployeeId);
            }
        });
    }

    public OrientationStatus ValidateAndRecordComprehension(OrientationEvidenceRequest request) =>
        ValidateAndRecordComprehension(request, OrientationEvidenceSource.OwnerSubmitted);

    public OrientationStatus ValidateAndRecordComprehension(
        OrientationEvidenceRequest request,
        OrientationEvidenceSource source)
    {
        ValidateEvidenceRequest(request);
        var sourceValue = source.ToWireValue();

        return TranslateStoreFaults(() =>
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var current = ReadCurrentStatus(connection, transaction, request.EmployeeId);
                if (current.State != OrientationStates.Delivered
                    || current.Revision != request.ExpectedRevision
                    || !string.Equals(current.AssignmentId, request.AssignmentId, StringComparison.Ordinal))
                {
                    throw new OrganizationConcurrencyException(
                        "The current delivered orientation assignment and revision do not match the request.");
                }

                if (current.RestartRequired)
                {
                    throw new OrganizationConcurrencyException(
                        "The current runtime has not confirmed loading this orientation assignment.");
                }

                if (!string.Equals(current.OrientationVersion, request.OrientationVersion, StringComparison.Ordinal)
                    || !string.Equals(current.SessionId, request.SessionId, StringComparison.Ordinal)
                    || !string.Equals(current.EmployeeId, request.EmployeeId, StringComparison.Ordinal))
                {
                    return RejectEvidence(
                        connection,
                        transaction,
                        current,
                        request,
                        sourceValue,
                        "Identity, session, or orientation version mismatch.");
                }

                var facts = ReadExpectedFacts(connection, transaction, current.AssignmentId);
                var duties = NormalizeSet(request.Duties);
                var restrictions = NormalizeSet(request.Restrictions);
                var valid = EqualFact(request.Identity, facts.Identity)
                    && EqualFact(request.Department, facts.Department)
                    && EqualFact(request.Reporting, facts.Reporting)
                    && EqualFact(request.Escalation, facts.Escalation)
                    && duties.SetEquals(facts.Duties)
                    && restrictions.SetEquals(facts.Restrictions);
                if (!valid)
                {
                    return RejectEvidence(
                        connection,
                        transaction,
                        current,
                        request,
                        sourceValue,
                        "Bounded evidence did not exactly match persisted expected facts.");
                }

                var canonical = CanonicalEvidence(request);
                var hash = OrientationComposer.Hash(canonical);
                var now = Timestamp();
                var acknowledged = Execute(
                    connection,
                    transaction,
                    """
                    UPDATE orientation_assignments
                    SET state = 'Acknowledged', acknowledged_at = $now, revision = revision + 1
                    WHERE id = $id AND revision = $revision AND state = 'Delivered'
                    """,
                    ("$now", now),
                    ("$id", current.AssignmentId),
                    ("$revision", current.Revision));
                if (acknowledged != 1)
                {
                    throw new OrganizationConcurrencyException("The delivered orientation changed while evidence was validated.");
                }

                Execute(
                    connection,
                    transaction,
                    """
                    UPDATE orientation_assignments
                    SET state = 'Comprehended',
                        comprehended_at = $now,
                        evidence_hash = $hash,
                        evidence_summary = $summary,
                        evidence_source = $source,
                        last_error = NULL,
                        revision = revision + 1
                    WHERE id = $id AND state = 'Acknowledged'
                    """,
                    ("$now", now),
                    ("$hash", hash),
                    ("$summary", $"Host validated exact persisted orientation facts from {sourceValue} evidence."),
                    ("$source", sourceValue),
                    ("$id", current.AssignmentId));

                var evidenceRows = Execute(
                    connection,
                    transaction,
                    """
                    INSERT INTO orientation_evidence (
                        id, assignment_id, employee_id, session_id, orientation_version,
                        evidence_hash, sanitized_summary, evidence_source, outcome, created_at)
                    SELECT $id, $assignment, $employee, id, $version,
                           $hash, $summary, $source, 'Comprehended', $now
                    FROM acp_sessions
                    WHERE native_session_id = $session AND employee_id = $employee
                    """,
                    ("$id", OrganizationIds.NewEvidenceId()),
                    ("$assignment", current.AssignmentId),
                    ("$employee", request.EmployeeId),
                    ("$version", request.OrientationVersion),
                    ("$hash", hash),
                    ("$summary", $"{sourceValue} evidence matched the exact persisted fact set."),
                    ("$source", sourceValue),
                    ("$now", now),
                    ("$session", request.SessionId));
                if (evidenceRows != 1)
                {
                    throw new OrganizationConcurrencyException("The evidence session is no longer the employee session.");
                }

                SetHold(
                    connection,
                    transaction,
                    current.RuntimeBindingId,
                    DispatchHoldReasons.OrientationUnacknowledged,
                    false,
                    null);
                SetHold(
                    connection,
                    transaction,
                    current.RuntimeBindingId,
                    DispatchHoldReasons.OrientationStale,
                    false,
                    null);
                SetHold(
                    connection,
                    transaction,
                    current.RuntimeBindingId,
                    DispatchHoldReasons.OrientationFailed,
                    false,
                    null);
                transaction.Commit();
                return GetOrientationStatusCore(connection, request.EmployeeId);
            }
        });
    }

    public OrientationStatus RecordComprehensionTimeout(
        string assignmentId,
        string employeeId,
        string sessionId,
        string orientationVersion,
        int expectedRevision)
    {
        return RecordHistoricalAttemptFailure(
            assignmentId,
            employeeId,
            sessionId,
            orientationVersion,
            expectedRevision,
            OrientationStates.TimedOut,
            OrientationEvidenceSource.LiveModel,
            "The bounded comprehension operation timed out.");
    }

    public OrientationStatus RecordComprehensionFailure(
        string assignmentId,
        string employeeId,
        string sessionId,
        string orientationVersion,
        int expectedRevision,
        string error)
    {
        return RecordHistoricalAttemptFailure(
            assignmentId,
            employeeId,
            sessionId,
            orientationVersion,
            expectedRevision,
            OrientationStates.Failed,
            OrientationEvidenceSource.LiveModel,
            error);
    }

    public OrientationStatus GetOrientationStatus(string employeeId)
    {
        return TranslateStoreFaults(() =>
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                return GetOrientationStatusCore(connection, employeeId);
            }
        });
    }

    private OrientationStatus? TryGetOrientationStatus(SqliteConnection connection, string employeeId)
    {
        try
        {
            return GetOrientationStatusCore(connection, employeeId);
        }
        catch (OrganizationNotFoundException)
        {
            return null;
        }
    }

    public bool CanDispatchEmployee(string employeeId)
    {
        return GetOrientationStatus(employeeId).Ready;
    }

    public OrientationStatus SetManualDispatchHold(string employeeId, bool held, string? detail)
    {
        return TranslateStoreFaults(() =>
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var binding = BindingForEmployee(connection, transaction, employeeId);
                SetHold(
                    connection,
                    transaction,
                    binding,
                    DispatchHoldReasons.Manual,
                    held,
                    Sanitize(detail, 256));
                transaction.Commit();
                return GetOrientationStatusCore(connection, employeeId);
            }
        });
    }

    public PermissionGrantSummary CreatePermissionGrant(PermissionGrantRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = DateTimeOffset.UtcNow;
        if (request.ExpiresAt <= now || request.ExpiresAt > now.AddDays(30))
        {
            throw new OrganizationValidationException("Grant expiry must be in the future and within 30 days.");
        }

        ValidatePermissionToken(request.Tool, nameof(request.Tool));
        ValidatePermissionToken(request.Resource, nameof(request.Resource));
        ValidatePermissionToken(request.IdempotencyKey, nameof(request.IdempotencyKey));

        return TranslateStoreFaults(() =>
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var restriction = ReadGrantRestriction(connection, transaction, request);
                var expiry = request.ExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                var id = OrganizationIds.NewGrantId();

                try
                {
                    Execute(
                        connection,
                        transaction,
                        """
                        INSERT INTO permission_grants (
                            id, organization_id, employee_id, restriction_id, policy_id,
                            policy_revision, tool, resource, expires_at, idempotency_key,
                            created_at, revision)
                        VALUES (
                            $id, $organization, $employee, $restriction, $policy,
                            $revision, $tool, $resource, $expires, $key, $now, 1)
                        """,
                        ("$id", id),
                        ("$organization", restriction.OrganizationId),
                        ("$employee", request.EmployeeId),
                        ("$restriction", request.RestrictionId),
                        ("$policy", restriction.PolicyId),
                        ("$revision", restriction.PolicyRevision),
                        ("$tool", request.Tool),
                        ("$resource", request.Resource),
                        ("$expires", expiry),
                        ("$key", request.IdempotencyKey),
                        ("$now", Timestamp()));
                }
                catch (SqliteException exception) when (IsUniqueConstraint(exception))
                {
                    var existing = ReadGrantByIdempotencyKey(
                        connection,
                        transaction,
                        request.EmployeeId,
                        request.IdempotencyKey);
                    if (existing is null
                        || existing.RestrictionId != request.RestrictionId
                        || existing.Tool != request.Tool
                        || existing.Resource != request.Resource
                        || existing.PolicyRevision != request.PolicyRevision
                        || existing.ExpiresAt.ToUniversalTime() != request.ExpiresAt.ToUniversalTime())
                    {
                        throw new OrganizationConcurrencyException(
                            "The idempotency key was already used for a different grant.");
                    }

                    transaction.Commit();
                    return existing;
                }

                transaction.Commit();
                return new PermissionGrantSummary(
                    id,
                    request.EmployeeId,
                    request.RestrictionId,
                    request.Tool,
                    request.Resource,
                    restriction.PolicyRevision,
                    request.ExpiresAt.ToUniversalTime(),
                    null,
                    1);
            }
        });
    }

    public PermissionGrantSummary RevokePermissionGrant(string grantId, int expectedRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grantId);
        return TranslateStoreFaults(() =>
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var existing = ReadGrantById(connection, transaction, grantId)
                    ?? throw new OrganizationNotFoundException("The permission grant does not exist.");
                if (existing.Revision != expectedRevision || existing.RevokedAt is not null)
                {
                    throw new OrganizationConcurrencyException("The grant changed or was already revoked.");
                }

                var affected = Execute(
                    connection,
                    transaction,
                    """
                    UPDATE permission_grants
                    SET revoked_at = $now, revision = revision + 1
                    WHERE id = $id AND revision = $revision AND revoked_at IS NULL
                    """,
                    ("$now", Timestamp()),
                    ("$id", grantId),
                    ("$revision", expectedRevision));
                if (affected != 1)
                {
                    throw new OrganizationConcurrencyException("The grant changed or was already revoked.");
                }

                var result = ReadGrantById(connection, transaction, grantId)
                    ?? throw new OrganizationStoreException("The revoked grant could not be re-read.");
                transaction.Commit();
                return result;
            }
        });
    }

    /// <summary>
    /// Evaluates the persisted deny layers and records a sanitized decision. In
    /// Phase 1 no inbound ACP claim has a host-derived canonical resource, so
    /// even a fully granted waivable match remains staged and non-executable.
    /// </summary>
    public PermissionDecision EvaluatePermission(
        string employeeId,
        string? nativeSessionId,
        long generation,
        string tool,
        string resource)
    {
        ValidatePermissionToken(tool, nameof(tool));
        ValidatePermissionToken(resource, nameof(resource));

        return TranslateStoreFaults(() =>
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var binding = ReadEmployeeBinding(connection, transaction, employeeId);
                var policy = ReadActivePolicy(connection, transaction, binding.OrganizationId);
                var now = Timestamp();

                var sessionRowId = ReadExactActiveSessionRow(
                    connection,
                    transaction,
                    employeeId,
                    binding.SessionRowId,
                    nativeSessionId);
                var requestId = OrganizationIds.NewPermissionRequestId();
                var resourceHash = OrientationComposer.Hash(resource);
                Execute(
                    connection,
                    transaction,
                    """
                    INSERT INTO permission_requests (
                        id, employee_id, runtime_binding_id, session_id, generation,
                        tool, resource_hash, status, created_at, decided_at)
                    VALUES (
                        $id, $employee, $binding, $session, $generation,
                        $tool, $hash, 'rejected', $now, $now)
                    """,
                    ("$id", requestId),
                    ("$employee", employeeId),
                    ("$binding", binding.RuntimeBindingId),
                    ("$session", sessionRowId),
                    ("$generation", generation),
                    ("$tool", tool),
                    ("$hash", resourceHash),
                    ("$now", now));

                var matches = ReadMatchingRestrictions(
                    connection,
                    transaction,
                    policy.Id,
                    employeeId,
                    tool,
                    resource,
                    now);
                var decisive = matches.FirstOrDefault(match => !match.Waivable)
                    ?? matches.FirstOrDefault();
                var summary = sessionRowId is null
                    ? "Rejected because the permission request was absent from the exact active employee session."
                    : matches.Count == 0
                        ? "Rejected because no active restriction recognized the claim."
                        : matches.Any(match => !match.Waivable)
                            ? "Rejected because at least one matching restriction is non-waivable."
                            : matches.All(match => match.GrantId is not null)
                                ? "Rejected because owner grants are staged only; inbound ACP execution has no canonical host resource adapter."
                                : "Rejected because every matching waivable restriction requires an exact active grant.";

                Execute(
                    connection,
                    transaction,
                    """
                    INSERT INTO permission_audit (
                        id, permission_request_id, employee_id, policy_id,
                        policy_revision, tool, resource_hash, decision,
                        restriction_id, grant_id, summary, matched_restriction_ids, created_at)
                    VALUES (
                        $id, $request, $employee, $policy,
                        $revision, $tool, $hash, 'rejected',
                        $restriction, $grant, $summary, $matches, $now)
                    """,
                    ("$id", OrganizationIds.NewPermissionAuditId()),
                    ("$request", requestId),
                    ("$employee", employeeId),
                    ("$policy", policy.Id),
                    ("$revision", policy.Revision),
                    ("$tool", tool),
                    ("$hash", resourceHash),
                    ("$restriction", decisive?.Id),
                    ("$grant", decisive?.GrantId),
                    ("$summary", summary),
                    ("$matches", JsonSerializer.Serialize(matches.Select(match => match.Id).Order(StringComparer.Ordinal).ToArray())),
                    ("$now", now));

                transaction.Commit();
                return new PermissionDecision(
                    Allowed: false,
                    Decision: "rejected",
                    OptionId: null,
                    RestrictionId: decisive?.Id,
                    Tool: tool,
                    Resource: resource);
            }
        });
    }

    public OrganizationOverview UpdateRoleInstructions(
        string roleId,
        string instructions,
        int expectedRevision)
    {
        var value = Sanitize(instructions, 4096);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new OrganizationValidationException("Role standing instructions are required.");
        }

        return TranslateStoreFaults(() =>
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                using (var current = connection.CreateCommand())
                {
                    current.Transaction = transaction;
                    current.CommandText =
                        "SELECT revision FROM orientation_fragments WHERE layer = 'role' AND scope_id = $id AND active = 1";
                    current.Parameters.AddWithValue("$id", roleId);
                    var revision = current.ExecuteScalar();
                    if (revision is null)
                    {
                        throw new OrganizationNotFoundException($"Role '{roleId}' does not exist.");
                    }
                    if (Convert.ToInt32(revision, CultureInfo.InvariantCulture) != expectedRevision)
                    {
                        throw new OrganizationConcurrencyException("The role instruction revision changed.");
                    }
                }

                ReplaceFragment(
                    connection,
                    transaction,
                    "role",
                    roleId,
                    OrientationComposer.BuildRoleFragment(value));
                string organizationId;
                using (var organization = connection.CreateCommand())
                {
                    organization.Transaction = transaction;
                    organization.CommandText =
                        "SELECT d.organization_id FROM roles r JOIN departments d ON d.id = r.department_id WHERE r.id = $id";
                    organization.Parameters.AddWithValue("$id", roleId);
                    organizationId = (string?)organization.ExecuteScalar()
                        ?? throw new OrganizationStoreCorruptException("The updated role department is missing.");
                }
                MarkCurrentAssignmentsStale(connection, transaction, organizationId, "Role instructions changed.");
                transaction.Commit();
                return BuildOverview(connection, ReadSingleOrganization(connection));
            }
        });
    }

    public OrganizationOverview UpdateOrganizationBasicInstructions(
        string organizationId,
        string instructions,
        int expectedRevision)
    {
        var value = Sanitize(instructions, 4096);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new OrganizationValidationException("Basic instructions are required.");
        }

        return TranslateStoreFaults(() =>
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var affected = Execute(
                    connection,
                    transaction,
                    """
                    UPDATE organizations
                    SET basic_instructions = $value,
                        updated_at = $now,
                        revision = revision + 1
                    WHERE id = $id AND revision = $revision
                    """,
                    ("$value", value),
                    ("$now", Timestamp()),
                    ("$id", organizationId),
                    ("$revision", expectedRevision));
                if (affected != 1)
                {
                    ThrowOrganizationUpdateFailure(connection, transaction, organizationId);
                }

                RefreshOrganizationFragment(connection, transaction, organizationId);
                MarkCurrentAssignmentsStale(connection, transaction, organizationId, "Organization instructions changed.");
                transaction.Commit();
                return BuildOverview(connection, ReadSingleOrganization(connection));
            }
        });
    }

    private OrientationStatus TransitionAssignment(
        string assignmentId,
        string version,
        string? session,
        int expectedRevision,
        long requiredRuntimeGeneration,
        string from,
        string to,
        string timestampColumn)
    {
        return TranslateStoreFaults(() =>
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var sessionRowId = ReadAssignmentSessionRow(
                    connection,
                    transaction,
                    assignmentId,
                    session);
                if (sessionRowId is null)
                {
                    throw new OrganizationConcurrencyException(
                        "The delivery session is absent, stale, or belongs to another employee.");
                }

                var now = Timestamp();
                var affected = Execute(
                    connection,
                    transaction,
                    $"""
                    UPDATE orientation_assignments
                    SET state = $to,
                        {timestampColumn} = $now,
                        required_runtime_generation = $generation,
                        loaded_runtime_generation = $loadedGeneration,
                        last_error = NULL,
                        revision = revision + 1,
                        session_id = $session
                    WHERE id = $id
                      AND orientation_version = $version
                      AND revision = $revision
                      AND state = $from
                    """,
                    ("$to", to),
                    ("$now", now),
                    ("$generation", requiredRuntimeGeneration),
                    ("$loadedGeneration", requiredRuntimeGeneration == 0 ? 0 : null),
                    ("$session", sessionRowId),
                    ("$id", assignmentId),
                    ("$version", version),
                    ("$revision", expectedRevision),
                    ("$from", from));
                if (affected != 1)
                {
                    throw new OrganizationConcurrencyException(
                        "The orientation assignment changed, is stale, or is in the wrong state.");
                }

                SetHold(
                    connection,
                    transaction,
                    binding: BindingForAssignment(connection, transaction, assignmentId),
                    reason: DispatchHoldReasons.OrientationReloadRequired,
                    active: requiredRuntimeGeneration > 0,
                    detail: requiredRuntimeGeneration > 0
                        ? $"Runtime restart required to load orientation generation {requiredRuntimeGeneration}."
                        : null);
                transaction.Commit();
                return GetOrientationStatusByAssignment(connection, assignmentId);
            }
        });
    }

    private OrientationStatus RecordHistoricalAttemptFailure(
        string assignmentId,
        string employeeId,
        string sessionId,
        string version,
        int startedRevision,
        string outcome,
        OrientationEvidenceSource source,
        string error)
    {
        error = SanitizeFailureCategory(error);
        return TranslateStoreFaults(() =>
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var assignment = GetOrientationStatusByAssignment(connection, assignmentId, transaction);
                if (!string.Equals(assignment.EmployeeId, employeeId, StringComparison.Ordinal)
                    || !string.Equals(assignment.SessionId, sessionId, StringComparison.Ordinal)
                    || !string.Equals(assignment.OrientationVersion, version, StringComparison.Ordinal))
                {
                    throw new OrganizationConcurrencyException(
                        "The failed orientation attempt does not match its started assignment, employee, version, and session.");
                }

                var sourceValue = source.ToWireValue();
                var summary = $"{sourceValue} comprehension attempt ended in {outcome}.";
                var hash = OrientationComposer.Hash(
                    $"{assignmentId}\n{employeeId}\n{sessionId}\n{version}\n{startedRevision}\n{outcome}\n{error}");
                var duplicate = string.Equals(assignment.EvidenceHash, hash, StringComparison.Ordinal)
                    && string.Equals(assignment.EvidenceSource, sourceValue, StringComparison.Ordinal)
                    && string.Equals(assignment.LastError, error, StringComparison.Ordinal);
                var deliveredStart = string.Equals(assignment.State, OrientationStates.Delivered, StringComparison.Ordinal)
                    && assignment.Revision == startedRevision;
                var supersededStart = string.Equals(assignment.State, OrientationStates.Stale, StringComparison.Ordinal)
                    && assignment.Revision >= startedRevision + 1;
                var terminalDuplicate = string.Equals(assignment.State, outcome, StringComparison.Ordinal)
                    && assignment.Revision == startedRevision + 1
                    && duplicate;
                if (!deliveredStart && !supersededStart && !terminalDuplicate)
                {
                    throw new OrganizationConcurrencyException(
                        "The failed orientation attempt does not match the assignment state captured at operation start.");
                }

                if (!duplicate)
                {
                    var affected = Execute(
                        connection,
                        transaction,
                        deliveredStart
                            ? """
                              UPDATE orientation_assignments
                              SET state = $outcome,
                                  evidence_hash = $hash,
                                  evidence_summary = $summary,
                                  evidence_source = $source,
                                  last_error = $error,
                                  revision = revision + 1
                              WHERE id = $id AND state = 'Delivered' AND revision = $revision
                              """
                            : """
                              UPDATE orientation_assignments
                              SET evidence_hash = $hash,
                                  evidence_summary = $summary,
                                  evidence_source = $source,
                                  last_error = $error
                              WHERE id = $id AND state = 'Stale'
                              """,
                        ("$outcome", outcome),
                        ("$hash", hash),
                        ("$summary", summary),
                        ("$source", sourceValue),
                        ("$error", error),
                        ("$id", assignmentId),
                        ("$revision", startedRevision));
                    if (affected != 1)
                    {
                        throw new OrganizationConcurrencyException(
                            "The failed orientation attempt changed while its outcome was recorded.");
                    }
                }

                Execute(
                    connection,
                    transaction,
                    """
                    INSERT OR IGNORE INTO orientation_evidence (
                        id, assignment_id, employee_id, session_id, orientation_version,
                        evidence_hash, sanitized_summary, evidence_source, outcome, created_at)
                    SELECT $evidence, a.id, a.employee_id, a.session_id, a.orientation_version,
                           $hash, $summary, $source, $outcome, $now
                    FROM orientation_assignments a
                    JOIN acp_sessions s ON s.id = a.session_id
                    WHERE a.id = $assignment
                      AND a.employee_id = $employee
                      AND a.orientation_version = $version
                      AND s.native_session_id = $session
                    """,
                    ("$evidence", OrganizationIds.NewEvidenceId()),
                    ("$hash", hash),
                    ("$summary", summary),
                    ("$source", sourceValue),
                    ("$outcome", outcome),
                    ("$now", Timestamp()),
                    ("$assignment", assignmentId),
                    ("$employee", employeeId),
                    ("$version", version),
                    ("$session", sessionId));

                if (deliveredStart)
                {
                    SetHold(
                        connection,
                        transaction,
                        assignment.RuntimeBindingId,
                        DispatchHoldReasons.OrientationFailed,
                        true,
                        error);
                }

                transaction.Commit();
                return GetOrientationStatusCore(connection, employeeId);
            }
        });
    }

    private OrientationStatus RejectEvidence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        OrientationStatus current,
        OrientationEvidenceRequest request,
        string source,
        string reason)
    {
        var liveModel = string.Equals(source, OrientationEvidenceSources.LiveModel, StringComparison.Ordinal);
        var outcome = liveModel ? OrientationStates.Failed : OrientationStates.Rejected;
        var hash = OrientationComposer.Hash(CanonicalEvidence(request));
        var now = Timestamp();
        Execute(
            connection,
            transaction,
            """
            UPDATE orientation_assignments
            SET state = $outcome,
                acknowledged_at = $now,
                evidence_hash = $hash,
                evidence_summary = $summary,
                evidence_source = $source,
                last_error = $reason,
                revision = revision + 1
            WHERE id = $id
            """,
            ("$outcome", outcome),
            ("$now", now),
            ("$hash", hash),
            ("$summary", liveModel
                ? "Host rejected invalid live-model structured orientation evidence."
                : "Rejected owner-submitted structured orientation evidence."),
            ("$source", source),
            ("$reason", reason),
            ("$id", current.AssignmentId));
        var evidenceRows = Execute(
            connection,
            transaction,
            """
            INSERT INTO orientation_evidence (
                id, assignment_id, employee_id, session_id, orientation_version,
                evidence_hash, sanitized_summary, evidence_source, outcome, created_at)
            SELECT $evidence, id, employee_id, session_id, orientation_version,
                   $hash, $summary, $source, $outcome, $now
            FROM orientation_assignments
            WHERE id = $assignment AND session_id IS NOT NULL
            """,
            ("$evidence", OrganizationIds.NewEvidenceId()),
            ("$hash", hash),
            ("$summary", $"{source} evidence was rejected by exact persisted fact validation."),
            ("$source", source),
            ("$outcome", outcome),
            ("$now", now),
            ("$assignment", current.AssignmentId));
        if (evidenceRows != 1)
        {
            throw new OrganizationConcurrencyException("The rejected evidence assignment no longer has a session binding.");
        }

        SetHold(
            connection,
            transaction,
            current.RuntimeBindingId,
            DispatchHoldReasons.OrientationFailed,
            true,
            reason);
        transaction.Commit();
        return GetOrientationStatusCore(connection, current.EmployeeId);
    }

    private static void ValidateEvidenceRequest(OrientationEvidenceRequest? request)
    {
        if (request is null)
        {
            throw new OrganizationValidationException("Orientation evidence is required.");
        }

        ValidateEvidenceScalar(request.AssignmentId, nameof(request.AssignmentId), 256);
        ValidateEvidenceScalar(request.EmployeeId, nameof(request.EmployeeId), 256);
        ValidateEvidenceScalar(request.SessionId, nameof(request.SessionId), 256);
        ValidateEvidenceScalar(request.OrientationVersion, nameof(request.OrientationVersion), 128);
        ValidateEvidenceScalar(request.Identity, nameof(request.Identity), 256);
        ValidateEvidenceScalar(request.Department, nameof(request.Department), 256);
        ValidateEvidenceScalar(request.Reporting, nameof(request.Reporting), 256);
        ValidateEvidenceScalar(request.Escalation, nameof(request.Escalation), 2048);
        ValidateEvidenceList(request.Duties, nameof(request.Duties));
        ValidateEvidenceList(request.Restrictions, nameof(request.Restrictions));
        if (request.ExpectedRevision < 1)
        {
            throw new OrganizationValidationException("ExpectedRevision must be positive.");
        }

        if (JsonSerializer.SerializeToUtf8Bytes(request).Length > 16 * 1024)
        {
            throw new OrganizationValidationException("Orientation evidence exceeds 16 KiB.");
        }
    }

    private static void ValidateEvidenceScalar(string? value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw new OrganizationValidationException($"{name} is invalid.");
        }
    }

    private static void ValidateEvidenceList(IReadOnlyList<string>? values, string name)
    {
        if (values is null || values.Count == 0 || values.Count > 64)
        {
            throw new OrganizationValidationException($"{name} is invalid.");
        }

        foreach (var value in values)
        {
            ValidateEvidenceScalar(value, name, 512);
        }
    }

    private static string CanonicalEvidence(OrientationEvidenceRequest request)
    {
        return JsonSerializer.Serialize(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["assignmentId"] = request.AssignmentId ?? string.Empty,
            ["department"] = request.Department?.Trim() ?? string.Empty,
            ["duties"] = NormalizeSet(request.Duties).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            ["employeeId"] = request.EmployeeId ?? string.Empty,
            ["escalation"] = request.Escalation?.Trim() ?? string.Empty,
            ["identity"] = request.Identity?.Trim() ?? string.Empty,
            ["orientationVersion"] = request.OrientationVersion ?? string.Empty,
            ["reporting"] = request.Reporting?.Trim() ?? string.Empty,
            ["restrictions"] = NormalizeSet(request.Restrictions).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            ["sessionId"] = request.SessionId ?? string.Empty,
        });
    }

    private static HashSet<string> NormalizeSet(IReadOnlyList<string>? values)
    {
        return new HashSet<string>(
            (values ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()),
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool EqualFact(string supplied, string expected)
    {
        return string.Equals(supplied?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeFailureCategory(string? value)
    {
        const int maximumLength = 256;
        var sanitized = new string((value ?? string.Empty)
            .Where(character => !char.IsControl(character))
            .Take(maximumLength)
            .ToArray())
            .Trim();
        return sanitized.Length == 0 ? "Live-model comprehension failed." : sanitized;
    }

    private static string Sanitize(string? value, int max)
    {
        var sanitized = (value ?? string.Empty).Trim();
        if (sanitized.Length > max)
        {
            throw new OrganizationValidationException($"Value exceeds {max} characters.");
        }

        if (sanitized.Any(character => char.IsControl(character) && character is not '\n' and not '\t'))
        {
            throw new OrganizationValidationException("Unsupported control characters are not allowed.");
        }

        return sanitized;
    }

    private static void ValidatePermissionToken(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
        {
            throw new OrganizationValidationException($"{name} is invalid.");
        }
    }

    private static bool PermissionMatches(string pattern, string value)
    {
        return pattern == "*"
            || string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase)
            || (pattern.EndsWith(":*", StringComparison.Ordinal)
                && value.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUniqueConstraint(SqliteException exception)
    {
        return exception.SqliteErrorCode == 19 && exception.SqliteExtendedErrorCode == 2067;
    }

    private static void SetHold(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string binding,
        string reason,
        bool active,
        string? detail)
    {
        var now = Timestamp();
        Execute(
            connection,
            transaction,
            """
            INSERT INTO dispatch_holds (
                id, runtime_binding_id, reason, active, detail, created_at, cleared_at, revision)
            VALUES ($id, $binding, $reason, $active, $detail, $now, $cleared, 1)
            ON CONFLICT(runtime_binding_id, reason) DO UPDATE SET
                active = excluded.active,
                detail = excluded.detail,
                cleared_at = excluded.cleared_at,
                revision = dispatch_holds.revision + 1
            """,
            ("$id", OrganizationIds.NewHoldId()),
            ("$binding", binding),
            ("$reason", reason),
            ("$active", active ? 1 : 0),
            ("$detail", detail),
            ("$now", now),
            ("$cleared", active ? null : now));
    }

    private static string BindingForAssignment(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string assignmentId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT runtime_binding_id FROM orientation_assignments WHERE id = $assignment";
        command.Parameters.AddWithValue("$assignment", assignmentId);
        return (string?)command.ExecuteScalar()
            ?? throw new OrganizationNotFoundException("Orientation assignment not found.");
    }

    private static string BindingForEmployee(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string employee)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM runtime_bindings WHERE employee_id = $employee";
        command.Parameters.AddWithValue("$employee", employee);
        return (string?)command.ExecuteScalar()
            ?? throw new OrganizationNotFoundException("Employee binding not found.");
    }

    private static void ReplaceFragment(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string layer,
        string scope,
        string content)
    {
        using var current = connection.CreateCommand();
        current.Transaction = transaction;
        current.CommandText =
            "SELECT id, organization_id FROM orientation_fragments WHERE layer = $layer AND scope_id = $scope AND active = 1";
        current.Parameters.AddWithValue("$layer", layer);
        current.Parameters.AddWithValue("$scope", scope);
        using var currentReader = current.ExecuteReader();
        if (!currentReader.Read())
        {
            throw new OrganizationNotFoundException("The active orientation fragment does not exist.");
        }

        var priorFragmentId = currentReader.GetString(0);
        var organization = currentReader.GetString(1);
        currentReader.Close();

        Execute(
            connection,
            transaction,
            "UPDATE orientation_fragments SET active = 0 WHERE layer = $layer AND scope_id = $scope AND active = 1",
            ("$layer", layer),
            ("$scope", scope));

        using var nextRevision = connection.CreateCommand();
        nextRevision.Transaction = transaction;
        nextRevision.CommandText =
            "SELECT COALESCE(MAX(revision), 0) + 1 FROM orientation_fragments WHERE layer = $layer AND scope_id = $scope";
        nextRevision.Parameters.AddWithValue("$layer", layer);
        nextRevision.Parameters.AddWithValue("$scope", scope);
        var revision = Convert.ToInt32(nextRevision.ExecuteScalar(), CultureInfo.InvariantCulture);
        var normalized = OrientationComposer.Normalize(content);
        var fragmentId = OrganizationIds.NewFragmentId();
        Execute(
            connection,
            transaction,
            """
            INSERT INTO orientation_fragments (
                id, organization_id, layer, scope_id, revision,
                content, content_hash, active, created_at)
            VALUES (
                $id, $organization, $layer, $scope, $revision,
                $content, $hash, 1, $now)
            """,
            ("$id", fragmentId),
            ("$organization", organization),
            ("$layer", layer),
            ("$scope", scope),
            ("$revision", revision),
            ("$content", normalized),
            ("$hash", OrientationComposer.Hash(normalized)),
            ("$now", Timestamp()));
        Execute(
            connection,
            transaction,
            """
            INSERT INTO orientation_facts (fragment_id, category, value, ordinal)
            SELECT $newFragment, category, value, ordinal
            FROM orientation_facts
            WHERE fragment_id = $priorFragment
            """,
            ("$newFragment", fragmentId),
            ("$priorFragment", priorFragmentId));
        Execute(
            connection,
            transaction,
            """
            UPDATE permission_restrictions
            SET fragment_id = $newFragment, revision = revision + 1
            WHERE fragment_id = $priorFragment
            """,
            ("$newFragment", fragmentId),
            ("$priorFragment", priorFragmentId));
    }

    private static void RefreshOrganizationFragment(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string organizationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT display_name, description, basic_instructions FROM organizations WHERE id = $id";
        command.Parameters.AddWithValue("$id", organizationId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new OrganizationNotFoundException($"Organization '{organizationId}' does not exist.");
        }

        var content = OrientationComposer.BuildOrganizationFragment(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2));
        reader.Close();
        ReplaceFragment(connection, transaction, "organization", organizationId, content);
    }

    private static void MarkCurrentAssignmentsStale(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string organizationId,
        string detail)
    {
        var bindings = new List<string>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                """
                SELECT b.id
                FROM runtime_bindings b
                JOIN employees e ON e.id = b.employee_id
                WHERE e.organization_id = $organization
                """;
            read.Parameters.AddWithValue("$organization", organizationId);
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                bindings.Add(reader.GetString(0));
            }
        }

        Execute(
            connection,
            transaction,
            """
            UPDATE orientation_assignments
            SET state = 'Stale', last_error = $detail, revision = revision + 1
            WHERE employee_id IN (
                SELECT id FROM employees WHERE organization_id = $organization)
              AND state <> 'Stale'
            """,
            ("$detail", detail),
            ("$organization", organizationId));
        foreach (var binding in bindings)
        {
            SetHold(
                connection,
                transaction,
                binding,
                DispatchHoldReasons.OrientationStale,
                true,
                detail);
            SetHold(
                connection,
                transaction,
                binding,
                DispatchHoldReasons.PolicyUpdate,
                true,
                detail);
        }
    }

    private static CompositionParts ReadCompositionParts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string employee)
    {
        var fragments = new List<OrientationFragment>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT f.id, f.layer, f.scope_id, f.revision, f.content
                FROM orientation_fragments f
                JOIN employees e ON e.id = $employee
                WHERE f.active = 1
                  AND ((f.layer = 'organization' AND f.scope_id = e.organization_id)
                    OR (f.layer = 'department' AND f.scope_id = e.department_id)
                    OR (f.layer = 'role' AND f.scope_id = e.role_id)
                    OR (f.layer = 'employee' AND f.scope_id = e.id))
                ORDER BY CASE f.layer
                    WHEN 'organization' THEN 1
                    WHEN 'department' THEN 2
                    WHEN 'role' THEN 3
                    ELSE 4 END
                """;
            command.Parameters.AddWithValue("$employee", employee);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                fragments.Add(new OrientationFragment(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetString(4)));
            }
        }

        if (fragments.Count != 4)
        {
            throw new OrganizationStoreCorruptException("Exactly four active orientation fragments are required.");
        }

        var policy = ReadActivePolicyForEmployee(connection, transaction, employee);
        var restrictions = new List<OrientationRestriction>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT r.id, r.layer, r.stable_key, r.tool_pattern,
                       r.resource_pattern, r.description, r.waivable
                FROM permission_restrictions r
                WHERE r.policy_id = $policy
                  AND (r.fragment_id IS NULL OR r.fragment_id IN (
                      SELECT id FROM orientation_fragments
                      WHERE active = 1 AND id IN ($organization, $department, $role, $employee)))
                ORDER BY CASE r.layer
                    WHEN 'host' THEN 0
                    WHEN 'organization' THEN 1
                    WHEN 'department' THEN 2
                    WHEN 'role' THEN 3
                    ELSE 4 END,
                    r.stable_key
                """;
            command.Parameters.AddWithValue("$policy", policy.Id);
            command.Parameters.AddWithValue("$organization", fragments[0].Id);
            command.Parameters.AddWithValue("$department", fragments[1].Id);
            command.Parameters.AddWithValue("$role", fragments[2].Id);
            command.Parameters.AddWithValue("$employee", fragments[3].Id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                restrictions.Add(new OrientationRestriction(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt32(6) == 1));
            }
        }

        string? sessionRowId = null;
        string? nativeSessionId = null;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT s.id, s.native_session_id
                FROM runtime_bindings b
                LEFT JOIN acp_sessions s ON s.id = b.session_ref
                WHERE b.employee_id = $employee
                """;
            command.Parameters.AddWithValue("$employee", employee);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new OrganizationStoreCorruptException("The employee runtime binding is missing.");
            }

            if (!reader.IsDBNull(0))
            {
                sessionRowId = reader.GetString(0);
                nativeSessionId = reader.GetString(1);
            }
        }

        return new CompositionParts(
            fragments,
            policy.Id,
            policy.Version,
            policy.Revision,
            policy.Summary,
            restrictions,
            sessionRowId,
            nativeSessionId);
    }

    private OrientationStatus ReadCurrentStatus(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string employee)
    {
        return TryReadCurrentStatus(connection, transaction, employee)
            ?? throw new OrganizationNotFoundException("No current orientation assignment exists.");
    }

    private OrientationStatus? TryReadCurrentStatus(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string employee)
    {
        var currentIds = new List<string>();
        using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText =
                """
                SELECT id
                FROM orientation_assignments
                WHERE employee_id = $employee AND state <> 'Stale'
                ORDER BY assigned_at DESC, rowid DESC
                LIMIT 2
                """;
            current.Parameters.AddWithValue("$employee", employee);
            using var reader = current.ExecuteReader();
            while (reader.Read())
            {
                currentIds.Add(reader.GetString(0));
            }
        }

        if (currentIds.Count > 1)
        {
            throw new OrganizationStoreCorruptException("More than one current orientation assignment exists.");
        }

        if (currentIds.Count == 1)
        {
            return GetOrientationStatusByAssignment(connection, currentIds[0], transaction);
        }

        using var stale = connection.CreateCommand();
        stale.Transaction = transaction;
        stale.CommandText =
            """
            SELECT id
            FROM orientation_assignments
            WHERE employee_id = $employee AND state = 'Stale'
            ORDER BY assigned_at DESC, rowid DESC
            LIMIT 1
            """;
        stale.Parameters.AddWithValue("$employee", employee);
        var staleId = stale.ExecuteScalar() as string;
        return staleId is null ? null : GetOrientationStatusByAssignment(connection, staleId, transaction);
    }

    private OrientationStatus GetOrientationStatusCore(SqliteConnection connection, string employee)
    {
        using var transaction = connection.BeginTransaction();
        var result = ReadCurrentStatus(connection, transaction, employee);
        transaction.Commit();
        return result;
    }

    private OrientationStatus GetOrientationStatusByAssignment(
        SqliteConnection connection,
        string assignment,
        SqliteTransaction? transaction = null)
    {
        AssignmentRow row;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT a.id, a.employee_id, a.runtime_binding_id, s.native_session_id,
                       a.revision, a.orientation_version, a.state, a.assigned_at,
                       a.delivered_at, a.acknowledged_at, a.comprehended_at,
                        a.evidence_hash, a.evidence_summary, a.evidence_source,
                        a.artifact_file_name, a.artifact_bytes, p.version, p.revision,
                        a.required_runtime_generation, a.loaded_runtime_generation, a.last_error
                FROM orientation_assignments a
                JOIN permission_policies p ON p.id = a.policy_id
                LEFT JOIN acp_sessions s ON s.id = a.session_id
                WHERE a.id = $id
                """;
            command.Parameters.AddWithValue("$id", assignment);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new OrganizationNotFoundException("Orientation assignment not found.");
            }

            row = new AssignmentRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetString(6),
                ParseTime(reader.GetString(7)),
                ReadTime(reader, 8),
                ReadTime(reader, 9),
                ReadTime(reader, 10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.GetString(14),
                reader.GetInt64(15),
                reader.GetString(16),
                reader.GetInt32(17),
                reader.IsDBNull(18) ? null : reader.GetInt64(18),
                reader.IsDBNull(19) ? null : reader.GetInt64(19),
                reader.IsDBNull(20) ? null : reader.GetString(20));
        }

        var holds = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT reason FROM dispatch_holds WHERE runtime_binding_id = $binding AND active = 1 ORDER BY reason";
            command.Parameters.AddWithValue("$binding", row.RuntimeBindingId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                holds.Add(reader.GetString(0));
            }
        }

        return new OrientationStatus(
            row.EmployeeId,
            row.RuntimeBindingId,
            row.NativeSessionId,
            row.Id,
            row.Revision,
            row.OrientationVersion,
            row.State,
            row.AssignedAt,
            row.DeliveredAt,
            row.AcknowledgedAt,
            row.ComprehendedAt,
            row.EvidenceHash,
            row.EvidenceSummary,
            row.EvidenceSource,
            row.ArtifactFileName,
            row.ArtifactBytes,
            row.PolicyVersion,
            row.PolicyRevision,
            row.RequiredRuntimeGeneration,
            row.LoadedRuntimeGeneration,
            row.RequiredRuntimeGeneration is not null
                && (row.LoadedRuntimeGeneration is null
                    || row.LoadedRuntimeGeneration < row.RequiredRuntimeGeneration),
            holds.Count > 0,
            holds,
            row.LastError);
    }

    private static DateTimeOffset ParseTime(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static DateTimeOffset? ReadTime(SqliteDataReader reader, int index)
    {
        return reader.IsDBNull(index) ? null : ParseTime(reader.GetString(index));
    }

    private static ExpectedFacts ReadExpectedFacts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string assignmentId)
    {
        var facts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT f.category, f.value
            FROM orientation_assignment_fragments af
            JOIN orientation_facts f ON f.fragment_id = af.fragment_id
            WHERE af.assignment_id = $assignment
            ORDER BY af.ordinal, f.category, f.ordinal
            """;
        command.Parameters.AddWithValue("$assignment", assignmentId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var category = reader.GetString(0);
            if (!facts.TryGetValue(category, out var values))
            {
                values = [];
                facts[category] = values;
            }

            values.Add(reader.GetString(1));
        }

        return new ExpectedFacts(
            SingleFact(facts, "identity"),
            SingleFact(facts, "department"),
            SingleFact(facts, "reporting"),
            new HashSet<string>(RequiredFacts(facts, "duty"), StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(RequiredFacts(facts, "restriction"), StringComparer.OrdinalIgnoreCase),
            SingleFact(facts, "escalation"));
    }

    private static string SingleFact(Dictionary<string, List<string>> facts, string category)
    {
        var values = RequiredFacts(facts, category);
        if (values.Count != 1)
        {
            throw new OrganizationStoreCorruptException($"Orientation fact category '{category}' must contain exactly one value.");
        }

        return values[0];
    }

    private static IReadOnlyList<string> RequiredFacts(
        Dictionary<string, List<string>> facts,
        string category)
    {
        if (!facts.TryGetValue(category, out var values) || values.Count == 0)
        {
            throw new OrganizationStoreCorruptException($"Orientation fact category '{category}' is missing.");
        }

        return values;
    }

    private static SeedScope ReadSeedScope(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT o.id, o.display_name, o.description, o.basic_instructions,
                   d.id, r.id,
                   e.id, e.display_name, e.purpose, e.instructions, e.rules, e.restrictions
            FROM runtime_bindings b
            JOIN employees e ON e.id = b.employee_id
            JOIN organizations o ON o.id = e.organization_id
            JOIN departments d ON d.id = e.department_id
            JOIN roles r ON r.id = e.role_id
            WHERE b.placement = $placement
              AND e.slug = $employeeSlug
              AND d.slug = $departmentSlug
              AND r.slug = $roleSlug
            LIMIT 2
            """;
        command.Parameters.AddWithValue("$placement", RuntimePlacements.InternalSharedContainer);
        command.Parameters.AddWithValue("$employeeSlug", OrganizationSeed.AdoptedEmployeeSlug);
        command.Parameters.AddWithValue("$departmentSlug", OrganizationSeed.OperationsSlug);
        command.Parameters.AddWithValue("$roleSlug", OrganizationSeed.OperationsItRoleSlug);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new OrganizationStoreCorruptException(
                "The adopted Operations/IT employee binding required for orientation is missing.");
        }

        var result = new SeedScope(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11));
        if (reader.Read())
        {
            throw new OrganizationStoreCorruptException(
                "More than one adopted Operations/IT employee binding matched the orientation seed.");
        }

        return result;
    }

    private static string InsertSeedFragment(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string organizationId,
        string layer,
        string scopeId,
        string content,
        string now)
    {
        var id = OrganizationIds.NewFragmentId();
        var normalized = OrientationComposer.Normalize(content);
        Execute(
            connection,
            transaction,
            """
            INSERT INTO orientation_fragments (
                id, organization_id, layer, scope_id, revision,
                content, content_hash, active, created_at)
            VALUES (
                $id, $organization, $layer, $scope, 1,
                $content, $hash, 1, $now)
            """,
            ("$id", id),
            ("$organization", organizationId),
            ("$layer", layer),
            ("$scope", scopeId),
            ("$content", normalized),
            ("$hash", OrientationComposer.Hash(normalized)),
            ("$now", now));
        return id;
    }

    private static void InsertFacts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string fragmentId,
        IReadOnlyList<(string Category, string Value)> facts)
    {
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            ordinals.TryGetValue(fact.Category, out var ordinal);
            Execute(
                connection,
                transaction,
                """
                INSERT INTO orientation_facts (fragment_id, category, value, ordinal)
                VALUES ($fragment, $category, $value, $ordinal)
                """,
                ("$fragment", fragmentId),
                ("$category", fact.Category),
                ("$value", fact.Value),
                ("$ordinal", ordinal));
            ordinals[fact.Category] = ordinal + 1;
        }
    }

    private static void InsertRestrictions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string policyId,
        string? fragmentId,
        string layer,
        IReadOnlyList<RestrictionSeed> restrictions)
    {
        foreach (var restriction in restrictions)
        {
            Execute(
                connection,
                transaction,
                """
                INSERT INTO permission_restrictions (
                    id, policy_id, fragment_id, layer, stable_key,
                    tool_pattern, resource_pattern, description, waivable, revision)
                VALUES (
                    $id, $policy, $fragment, $layer, $key,
                    $tool, $resource, $description, $waivable, 1)
                """,
                ("$id", "rst-" + restriction.Key.Replace('.', '-')),
                ("$policy", policyId),
                ("$fragment", fragmentId),
                ("$layer", layer),
                ("$key", restriction.Key),
                ("$tool", restriction.Tool),
                ("$resource", restriction.Resource),
                ("$description", restriction.Description),
                ("$waivable", restriction.Waivable ? 1 : 0));
        }
    }

    private static ActivePolicy ReadActivePolicyForEmployee(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string employeeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT p.id, p.version, p.revision, p.summary
            FROM permission_policies p
            JOIN employees e ON e.organization_id = p.organization_id
            WHERE e.id = $employee AND p.active = 1
            LIMIT 2
            """;
        command.Parameters.AddWithValue("$employee", employeeId);
        return ReadSinglePolicy(command);
    }

    private static ActivePolicy ReadActivePolicy(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string organizationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id, version, revision, summary
            FROM permission_policies
            WHERE organization_id = $organization AND active = 1
            LIMIT 2
            """;
        command.Parameters.AddWithValue("$organization", organizationId);
        return ReadSinglePolicy(command);
    }

    private static ActivePolicy ReadSinglePolicy(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new OrganizationStoreCorruptException("An active permission policy is required.");
        }

        var policy = new ActivePolicy(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3));
        if (reader.Read())
        {
            throw new OrganizationStoreCorruptException("Exactly one active permission policy is required.");
        }

        return policy;
    }

    private static EmployeeBinding ReadEmployeeBinding(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string employeeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT e.organization_id, b.id, b.session_ref
            FROM employees e
            JOIN runtime_bindings b ON b.employee_id = e.id
            WHERE e.id = $employee
            LIMIT 2
            """;
        command.Parameters.AddWithValue("$employee", employeeId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new OrganizationNotFoundException("The employee or runtime binding does not exist.");
        }

        var binding = new EmployeeBinding(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
        if (reader.Read())
        {
            throw new OrganizationStoreCorruptException("The employee has more than one runtime binding.");
        }

        return binding;
    }

    private static string? ReadExactActiveSessionRow(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string employeeId,
        string? bindingSessionRowId,
        string? nativeSessionId)
    {
        if (bindingSessionRowId is null || string.IsNullOrWhiteSpace(nativeSessionId))
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id
            FROM acp_sessions
            WHERE id = $bindingSession
              AND employee_id = $employee
              AND native_session_id = $native
              AND status = 'active'
            LIMIT 2
            """;
        command.Parameters.AddWithValue("$bindingSession", bindingSessionRowId);
        command.Parameters.AddWithValue("$employee", employeeId);
        command.Parameters.AddWithValue("$native", nativeSessionId);
        var rows = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(reader.GetString(0));
        }

        return rows.Count == 1 ? rows[0] : null;
    }

    private static string? ReadAssignmentSessionRow(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string assignmentId,
        string? nativeSessionId)
    {
        if (string.IsNullOrWhiteSpace(nativeSessionId))
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT s.id
            FROM orientation_assignments a
            JOIN runtime_bindings b ON b.id = a.runtime_binding_id
            JOIN acp_sessions s ON s.id = b.session_ref AND s.employee_id = a.employee_id
            WHERE a.id = $assignment
              AND s.native_session_id = $native
              AND s.status = 'active'
            LIMIT 2
            """;
        command.Parameters.AddWithValue("$assignment", assignmentId);
        command.Parameters.AddWithValue("$native", nativeSessionId);
        var values = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values.Count == 1 ? values[0] : null;
    }

    private static List<RestrictionMatch> ReadMatchingRestrictions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string policyId,
        string employeeId,
        string tool,
        string resource,
        string now)
    {
        var candidates = new List<RestrictionMatch>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT r.id, r.layer, r.tool_pattern, r.resource_pattern,
                   r.waivable, g.id
            FROM permission_restrictions r
            LEFT JOIN orientation_fragments f ON f.id = r.fragment_id
            LEFT JOIN employees e ON e.id = $employee
            LEFT JOIN permission_grants g
              ON g.restriction_id = r.id
             AND g.employee_id = $employee
             AND g.policy_revision = (
                 SELECT revision FROM permission_policies WHERE id = r.policy_id)
             AND g.tool = $tool
             AND g.resource = $resource
             AND g.revoked_at IS NULL
             AND g.expires_at > $now
            WHERE r.policy_id = $policy
              AND (r.fragment_id IS NULL
                OR (f.active = 1 AND (
                    (f.layer = 'organization' AND f.scope_id = e.organization_id)
                    OR (f.layer = 'department' AND f.scope_id = e.department_id)
                    OR (f.layer = 'role' AND f.scope_id = e.role_id)
                    OR (f.layer = 'employee' AND f.scope_id = e.id))))
            ORDER BY CASE r.layer
                WHEN 'host' THEN 0
                WHEN 'organization' THEN 1
                WHEN 'department' THEN 2
                WHEN 'role' THEN 3
                ELSE 4 END,
                r.stable_key
            """;
        command.Parameters.AddWithValue("$employee", employeeId);
        command.Parameters.AddWithValue("$tool", tool);
        command.Parameters.AddWithValue("$resource", resource);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$policy", policyId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (PermissionMatches(reader.GetString(2), tool)
                && PermissionMatches(reader.GetString(3), resource))
            {
                candidates.Add(new RestrictionMatch(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(4) == 1,
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
        }

        return candidates;
    }

    private static GrantRestriction ReadGrantRestriction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PermissionGrantRequest request)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT p.id, p.organization_id, p.revision,
                   r.tool_pattern, r.resource_pattern, r.waivable
            FROM permission_restrictions r
            JOIN permission_policies p ON p.id = r.policy_id
            JOIN employees e ON e.id = $employee AND e.organization_id = p.organization_id
            LEFT JOIN orientation_fragments f ON f.id = r.fragment_id
            WHERE r.id = $restriction
              AND p.active = 1
              AND (r.fragment_id IS NULL OR f.active = 1)
            LIMIT 2
            """;
        command.Parameters.AddWithValue("$employee", request.EmployeeId);
        command.Parameters.AddWithValue("$restriction", request.RestrictionId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new OrganizationNotFoundException(
                "The employee or active policy restriction does not exist in the organization.");
        }

        var result = new GrantRestriction(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5) == 1);
        if (reader.Read())
        {
            throw new OrganizationStoreCorruptException("The grant restriction lookup was not unique.");
        }

        if (!result.Waivable)
        {
            throw new OrganizationValidationException("This restriction is non-waivable.");
        }

        if (result.PolicyRevision != request.PolicyRevision)
        {
            throw new OrganizationConcurrencyException("The permission policy revision changed.");
        }

        if (!PermissionMatches(result.ToolPattern, request.Tool)
            || !PermissionMatches(result.ResourcePattern, request.Resource))
        {
            throw new OrganizationValidationException(
                "The requested tool and resource do not match the restriction pattern.");
        }

        return result;
    }

    private static PermissionGrantSummary? ReadGrantByIdempotencyKey(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string employeeId,
        string idempotencyKey)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id, employee_id, restriction_id, tool, resource,
                   policy_revision, expires_at, revoked_at, revision
            FROM permission_grants
            WHERE employee_id = $employee AND idempotency_key = $key
            """;
        command.Parameters.AddWithValue("$employee", employeeId);
        command.Parameters.AddWithValue("$key", idempotencyKey);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadGrant(reader) : null;
    }

    private static PermissionGrantSummary? ReadGrantById(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string grantId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id, employee_id, restriction_id, tool, resource,
                   policy_revision, expires_at, revoked_at, revision
            FROM permission_grants
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", grantId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadGrant(reader) : null;
    }

    private static PermissionGrantSummary ReadGrant(SqliteDataReader reader)
    {
        return new PermissionGrantSummary(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            ParseTime(reader.GetString(6)),
            reader.IsDBNull(7) ? null : ParseTime(reader.GetString(7)),
            reader.GetInt32(8));
    }

    private void EnsureOpenIdentity()
    {
        if (_employeeId is null || _bindingId is null)
        {
            throw new OrganizationStoreException("The store is not open.");
        }
    }

    private static void ThrowOrganizationUpdateFailure(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string organizationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM organizations WHERE id = $id";
        command.Parameters.AddWithValue("$id", organizationId);
        if (command.ExecuteScalar() is null)
        {
            throw new OrganizationNotFoundException($"Organization '{organizationId}' does not exist.");
        }

        throw new OrganizationConcurrencyException("The organization revision changed.");
    }

    private sealed record RestrictionSeed(
        string Key,
        string Tool,
        string Resource,
        string Description,
        bool Waivable);

    private sealed record SeedScope(
        string OrganizationId,
        string OrganizationDisplayName,
        string OrganizationDescription,
        string OrganizationInstructions,
        string DepartmentId,
        string RoleId,
        string EmployeeId,
        string EmployeeDisplayName,
        string EmployeePurpose,
        string EmployeeInstructions,
        string EmployeeRules,
        string EmployeeRestrictions);

    private sealed record CompositionParts(
        IReadOnlyList<OrientationFragment> Fragments,
        string PolicyId,
        string PolicyVersion,
        int PolicyRevision,
        string PolicySummary,
        IReadOnlyList<OrientationRestriction> Restrictions,
        string? SessionRowId,
        string? NativeSessionId);

    private sealed record ExpectedFacts(
        string Identity,
        string Department,
        string Reporting,
        HashSet<string> Duties,
        HashSet<string> Restrictions,
        string Escalation);

    private sealed record ActivePolicy(string Id, string Version, int Revision, string Summary);
    private sealed record EmployeeBinding(string OrganizationId, string RuntimeBindingId, string? SessionRowId);
    private sealed record RestrictionMatch(string Id, string Layer, bool Waivable, string? GrantId);
    private sealed record GrantRestriction(
        string PolicyId,
        string OrganizationId,
        int PolicyRevision,
        string ToolPattern,
        string ResourcePattern,
        bool Waivable);

    private sealed record AssignmentRow(
        string Id,
        string EmployeeId,
        string RuntimeBindingId,
        string? NativeSessionId,
        int Revision,
        string OrientationVersion,
        string State,
        DateTimeOffset AssignedAt,
        DateTimeOffset? DeliveredAt,
        DateTimeOffset? AcknowledgedAt,
        DateTimeOffset? ComprehendedAt,
        string? EvidenceHash,
        string? EvidenceSummary,
        string? EvidenceSource,
        string ArtifactFileName,
        long ArtifactBytes,
        string PolicyVersion,
        int PolicyRevision,
        long? RequiredRuntimeGeneration,
        long? LoadedRuntimeGeneration,
        string? LastError);
}

public sealed record OrientationFragment(string Id, string Layer, string ScopeId, int Revision, string Content);

public sealed record OrientationRestriction(
    string Id,
    string Layer,
    string StableKey,
    string ToolPattern,
    string ResourcePattern,
    string Description,
    bool Waivable);

public static class OrientationComposer
{
    public static string Normalize(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim() + "\n";
    }

    public static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    public static string Version(string composedContent)
    {
        var semantic = System.Text.RegularExpressions.Regex.Replace(
            composedContent,
            @"<!-- fragment:(?:organization|department|role|employee) id=[^ ]+ revision=\d+ -->",
            "<!-- fragment -->",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return Hash(semantic);
    }

    public static string BuildOrganizationFragment(string name, string description, string instructions)
    {
        return Normalize($"""
            # Organization

            Name: {name}
            Purpose: {description}
            Standing instructions: {instructions}
            """);
    }

    public static string BuildDepartmentFragment()
    {
        return Normalize($"""
            # Department

            {OrganizationSeed.DepartmentOrientation}
            """);
    }

    public static string BuildRoleFragment(string standingInstructions)
    {
        return Normalize($"""
            # Role

            Profile references: {OrganizationSeed.OperationsItRoleInstructionProfile}; {OrganizationSeed.OperationsItRolePermissionProfile}
            Standing instructions:
            {standingInstructions}

            Operating behavior:
            - There is no Fleet. Do not call, emulate, or reference Fleet/V1 tooling.
            - No worker runtime exists yet. Do not hire, delegate to, or dispatch workers.
            - Do not perform code work: no repository edits, builds, migrations, or tests.
            - Prefer plain informational answers. Keep responses short and factual.
            - On the bootstrap turn, reply with one brief readiness line without tools or long-running work.
            - During comprehension, return only the requested bounded facts; do not expose reasoning or chain-of-thought.
            """);
    }

    public static string BuildEmployeeFragment(
        string id,
        string displayName,
        string purpose,
        string instructions,
        string rules,
        string restrictions)
    {
        return Normalize($"""
            # Employee

            Identity: {displayName} ({id})
            Purpose: {purpose}
            Instructions: {instructions}
            Rules: {rules}
            Restrictions: {restrictions}
            """);
    }

    public static string Compose(
        IReadOnlyList<OrientationFragment> fragments,
        string policyVersion,
        int policyRevision,
        string policySummary,
        IReadOnlyList<OrientationRestriction> restrictions)
    {
        if (fragments.Select(fragment => fragment.Layer).ToArray()
            is not ["organization", "department", "role", "employee"])
        {
            throw new OrganizationValidationException(
                "Orientation fragments must be ordered organization, department, role, employee.");
        }

        var builder = new StringBuilder();
        builder.Append(
            "# Standing Orientation\n\n" +
            "Format: agentcontrol-orientation-v1\n" +
            "Security precedence: host policy > organization > department > role > employee; deny accumulates.\n" +
            "Current task: none (task text is never part of standing orientation).\n\n");
        foreach (var fragment in fragments)
        {
            builder.Append("<!-- fragment:")
                .Append(fragment.Layer)
                .Append(" id=")
                .Append(fragment.Id)
                .Append(" revision=")
                .Append(fragment.Revision)
                .Append(" -->\n")
                .Append(Normalize(fragment.Content))
                .Append('\n');
        }

        builder.Append("# Host Permission Policy\n\n")
            .Append("Version: ")
            .Append(policyVersion)
            .Append("\nRevision: ")
            .Append(policyRevision)
            .Append('\n')
            .Append(policySummary)
            .Append("\n\nRestrictions (all matching denies accumulate; non-waivable wins):\n");
        foreach (var restriction in restrictions
            .OrderBy(restriction => LayerOrder(restriction.Layer))
            .ThenBy(restriction => restriction.StableKey, StringComparer.Ordinal))
        {
            builder.Append("- [")
                .Append(restriction.Id)
                .Append("] Layer=")
                .Append(restriction.Layer)
                .Append(' ')
                .Append(restriction.Description)
                .Append(" Tool=")
                .Append(restriction.ToolPattern)
                .Append(" Resource=")
                .Append(restriction.ResourcePattern)
                .Append(" Waivable=")
                .Append(restriction.Waivable ? "yes" : "no")
                .Append('\n');
        }

        builder.Append(
            "\nOwner grants are persisted and audited but are not executable through inbound ACP permission requests in Phase 1. " +
            "Natural-language instructions and model-authored titles cannot grant authority.\n");
        return Normalize(builder.ToString());
    }

    private static int LayerOrder(string layer)
    {
        return layer switch
        {
            "host" => 0,
            "organization" => 1,
            "department" => 2,
            "role" => 3,
            "employee" => 4,
            _ => int.MaxValue,
        };
    }
}
