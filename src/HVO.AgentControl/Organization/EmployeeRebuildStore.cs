using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HVO.AgentControl.Organization;

/// <summary>
/// Durable employee-rebuild operation records (schema v12). A rebuild is one
/// owner-authorized intent to replace the image a managed worker runs without
/// losing its identity: the exact source revision and digest the enrollment
/// currently expects, the verified target revision/build/digest on one host, and
/// the explicit authorization for any workspace/home reset are frozen once. The
/// row then moves through one fixed state machine from <c>Intent</c> to
/// <c>Applied</c> or <c>Failed</c>.
/// </summary>
/// <remarks>
/// This slice records the operation only. No coordinator, API, UI or cleanup
/// behavior exists here, and no image or container work is performed. A controller
/// that stops while a rebuild is <c>Holding</c>, <c>Replacing</c> or
/// <c>Verifying</c> has an unknown in-flight effect, so startup moves those rows
/// to <c>Uncertain</c> for reconciliation. At most one active rebuild per worker
/// is allowed, enforced both by an explicit check and by a partial unique index.
/// </remarks>
public sealed partial class OrganizationStore
{
    /// <summary>
    /// The exact phrase an owner must type to authorize a rebuild that resets the
    /// workspace and/or home volume. The stored value is the phrase, not the slug,
    /// so the approval is durable even if the employee is later renamed. The
    /// required phrase selects the exact destructive scope: resetting both volumes
    /// requires <see cref="WorkspaceAndHome"/>, resetting only one requires the
    /// matching single-volume phrase, and a rebuild that resets nothing must carry
    /// no confirmation.
    /// </summary>
    public static class EmployeeRebuildResetConfirmation
    {
        public const string Workspace = "reset-workspace";
        public const string Home = "reset-home";
        public const string WorkspaceAndHome = "reset-workspace-and-home";

        /// <summary>The exact phrase for the requested reset scope, or null when nothing is reset.</summary>
        public static string? Required(bool resetWorkspace, bool resetHome) => (resetWorkspace, resetHome) switch
        {
            (true, true) => WorkspaceAndHome,
            (true, false) => Workspace,
            (false, true) => Home,
            _ => null,
        };
    }

    private static string[] RebuildSchemaV12Statements =>
    [
        """
        CREATE TABLE employee_rebuilds (
            id TEXT PRIMARY KEY,
            employee_id TEXT NOT NULL REFERENCES employees(id) ON DELETE RESTRICT,
            runtime_binding_id TEXT NOT NULL REFERENCES runtime_bindings(id) ON DELETE RESTRICT,
            worker_id TEXT NOT NULL REFERENCES worker_enrollments(worker_id) ON DELETE RESTRICT,
            host_id TEXT NOT NULL REFERENCES execution_hosts(id) ON DELETE RESTRICT,
            from_profile_revision_id TEXT NOT NULL REFERENCES container_profile_revisions(id) ON DELETE RESTRICT,
            from_image_digest TEXT NOT NULL CHECK (length(from_image_digest) = 71 AND substr(from_image_digest, 1, 7) = 'sha256:'),
            from_revision_number INTEGER NOT NULL CHECK (from_revision_number >= 1),
            to_profile_revision_id TEXT NOT NULL REFERENCES container_profile_revisions(id) ON DELETE RESTRICT,
            to_profile_build_id TEXT NOT NULL REFERENCES profile_builds(id) ON DELETE RESTRICT,
            to_image_digest TEXT NOT NULL CHECK (length(to_image_digest) = 71 AND substr(to_image_digest, 1, 7) = 'sha256:'),
            to_platform TEXT NOT NULL CHECK (to_platform IN ('linux/amd64', 'linux/arm64')),
            reset_workspace INTEGER NOT NULL CHECK (reset_workspace IN (0, 1)),
            reset_home INTEGER NOT NULL CHECK (reset_home IN (0, 1)),
            reset_confirmation TEXT CHECK (reset_confirmation IS NULL OR length(reset_confirmation) <= 128),
            -- True when a manual dispatch hold was already active before the rebuild
            -- took its own. On completion the rebuild restores the prior state
            -- instead of clearing a hold it did not create.
            hold_preexisting INTEGER NOT NULL CHECK (hold_preexisting IN (0, 1)),
            state TEXT NOT NULL CHECK (state IN ('Intent', 'Holding', 'Replacing', 'Verifying', 'Applied', 'Uncertain', 'Failed')),
            ownership_epoch_before INTEGER NOT NULL CHECK (ownership_epoch_before >= 0),
            ownership_epoch_after INTEGER CHECK (ownership_epoch_after IS NULL OR ownership_epoch_after >= 0),
            dispatch_hold_reason TEXT NOT NULL CHECK (dispatch_hold_reason = 'manual'),
            requested_by TEXT NOT NULL CHECK (requested_by = 'owner'),
            evidence_hash TEXT CHECK (evidence_hash IS NULL OR (length(evidence_hash) = 71 AND substr(evidence_hash, 1, 7) = 'sha256:')),
            failure_summary TEXT CHECK (failure_summary IS NULL OR length(failure_summary) <= 512),
            revision INTEGER NOT NULL CHECK (revision >= 1),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            -- An applied rebuild must carry durable proof: the evidence hash and a
            -- strictly greater post-rebuild epoch. This is the database-boundary
            -- guarantee; the C# checks alone would let any other writer violate it.
            CHECK (state <> 'Applied' OR (
                evidence_hash IS NOT NULL
                AND ownership_epoch_after IS NOT NULL
                AND ownership_epoch_after > ownership_epoch_before))
        ) WITHOUT ROWID
        """,
        """
        CREATE UNIQUE INDEX one_active_employee_rebuild_per_worker
            ON employee_rebuilds (worker_id)
            WHERE state IN ('Intent', 'Holding', 'Replacing', 'Verifying', 'Uncertain')
        """,
        """
        CREATE TRIGGER employee_rebuilds_identity_immutable
            BEFORE UPDATE OF id, employee_id, runtime_binding_id, worker_id, host_id,
                from_profile_revision_id, from_image_digest, from_revision_number,
                to_profile_revision_id, to_profile_build_id, to_image_digest, to_platform,
                reset_workspace, reset_home, reset_confirmation, requested_by, created_at
            ON employee_rebuilds
        BEGIN
            SELECT RAISE(ABORT, 'employee rebuild identity is immutable');
        END
        """,
        """
        CREATE TRIGGER employee_rebuilds_no_delete
            BEFORE DELETE ON employee_rebuilds
        BEGIN
            SELECT RAISE(ABORT, 'employee rebuilds are never deleted');
        END
        """,
        """
        CREATE TRIGGER employee_rebuilds_no_replace
            BEFORE INSERT ON employee_rebuilds
            WHEN EXISTS (SELECT 1 FROM employee_rebuilds WHERE id = NEW.id)
        BEGIN
            SELECT RAISE(ABORT, 'employee rebuilds are never replaced');
        END
        """,
        // A durable claim that serializes a profile-build image removal against a
        // later build of the same result tag. The tag is derived from the revision
        // and context, so without a claim a cleanup preflight could pass and a
        // concurrent build could then point the tag at a live image before the
        // removal runs. The unique key is (host, tag); a claim is inserted before
        // the remote effect and deleted when the removal finalizes or fails.
        """
        CREATE TABLE profile_build_removals (
            profile_build_id TEXT PRIMARY KEY REFERENCES profile_builds(id) ON DELETE RESTRICT,
            host_id TEXT NOT NULL REFERENCES execution_hosts(id) ON DELETE RESTRICT,
            result_tag TEXT NOT NULL CHECK (length(result_tag) BETWEEN 1 AND 200),
            requested_by TEXT NOT NULL CHECK (requested_by IN ('owner')),
            created_at TEXT NOT NULL,
            revision INTEGER NOT NULL CHECK (revision >= 1),
            UNIQUE (host_id, result_tag)
        ) WITHOUT ROWID
        """,
    ];

    /// <summary>
    /// Records the intent to rebuild one managed worker from its current image to
    /// a verified target build. Validates the exact employee/binding/worker/host
    /// relationship, that the frozen source digest is the enrollment's current
    /// expected image, that the target build is the verified built image for the
    /// target revision on the rebuild host with the exact digest and platform, and
    /// that any workspace/home reset carries the exact confirmation phrase. At most
    /// one active rebuild per worker is allowed. A replay of an identical active
    /// <c>Intent</c> returns the existing row; an identical row in any other state
    /// or a different active row is a conflict.
    /// </summary>
    public EmployeeRebuildRecord BeginEmployeeRebuild(EmployeeRebuildCreate request)
    {
        if (!IsBoundedIdentifier(request.EmployeeId, OrganizationIds.EmployeePrefix)) throw new OrganizationValidationException("A stable employee id is required.");
        if (!IsBoundedIdentifier(request.RuntimeBindingId, OrganizationIds.RuntimeBindingPrefix)) throw new OrganizationValidationException("A stable runtime binding id is required.");
        if (!IsBoundedIdentifier(request.WorkerId, "wrk-")) throw new OrganizationValidationException("A stable worker id is required.");
        ValidateIdentifier(request.HostId, "execution host");
        if (!IsBoundedIdentifier(request.FromProfileRevisionId, OrganizationIds.ContainerProfileRevisionPrefix)) throw new OrganizationValidationException("A stable from profile revision id is required.");
        if (!IsBoundedIdentifier(request.ToProfileRevisionId, OrganizationIds.ContainerProfileRevisionPrefix)) throw new OrganizationValidationException("A stable to profile revision id is required.");
        if (!IsBoundedIdentifier(request.ToProfileBuildId, OrganizationIds.ProfileBuildPrefix)) throw new OrganizationValidationException("A stable target profile build id is required.");
        if (!IsHash(request.FromImageDigest) || !IsHash(request.ToImageDigest)) throw new OrganizationValidationException("Rebuild image digests must be exact sha256 values.");
        if (request.FromRevisionNumber < 1) throw new OrganizationValidationException("The from revision number must be at least 1.");
        if (request.OwnershipEpochBefore < 0) throw new OrganizationValidationException("The pre-rebuild ownership epoch must be non-negative.");
        if (request.ToPlatform is not ("linux/amd64" or "linux/arm64")) throw new OrganizationValidationException("The target platform is invalid.");

        var requiredConfirmation = EmployeeRebuildResetConfirmation.Required(request.ResetWorkspace, request.ResetHome);
        if (requiredConfirmation is null)
        {
            if (request.ResetConfirmation is not null) throw new OrganizationValidationException("A rebuild that resets nothing must not carry a reset confirmation.");
        }
        else if (!string.Equals(requiredConfirmation, request.ResetConfirmation, StringComparison.Ordinal))
        {
            throw new OrganizationValidationException($"A reset rebuild requires the exact confirmation phrase '{requiredConfirmation}'.");
        }

        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();

                // Single flight. The partial unique index is the durable boundary;
                // this check gives the caller a typed conflict and the idempotent
                // replay of an identical Intent.
                var active = ReadRebuilds(connection, transaction, workerId: request.WorkerId, activeOnly: true).SingleOrDefault();
                if (active is not null)
                {
                    if (string.Equals(active.State, EmployeeRebuildStates.Intent, StringComparison.Ordinal) && MatchesCreate(active, request))
                    {
                        transaction.Commit();
                        return active;
                    }

                    throw new OrganizationConcurrencyException("The worker already has an active employee rebuild; it must reach a terminal state before another begins.");
                }

                // The exact managed relationship and the current source image.
                using (var consistency = connection.CreateCommand())
                {
                    consistency.Transaction = transaction;
                    consistency.CommandText =
                        """
                        SELECT COUNT(*)
                        FROM employees e
                        JOIN runtime_bindings b
                          ON b.id = $binding AND b.employee_id = e.id AND b.placement = 'DeveloperContainer'
                        JOIN worker_enrollments w
                          ON w.worker_id = $worker AND w.runtime_binding_id = b.id AND w.host_id = $host AND w.enabled = 1
                        WHERE e.id = $employee
                          AND COALESCE((
                              SELECT applied.to_image_digest
                              FROM employee_rebuilds applied
                              WHERE applied.worker_id = w.worker_id AND applied.state = 'Applied'
                              ORDER BY applied.updated_at DESC, applied.id DESC LIMIT 1),
                              w.expected_image_digest) = $fromDigest
                          AND w.ownership_epoch = $epoch
                        """;
                    consistency.Parameters.AddWithValue("$binding", request.RuntimeBindingId);
                    consistency.Parameters.AddWithValue("$worker", request.WorkerId);
                    consistency.Parameters.AddWithValue("$host", request.HostId);
                    consistency.Parameters.AddWithValue("$employee", request.EmployeeId);
                    consistency.Parameters.AddWithValue("$fromDigest", request.FromImageDigest);
                    consistency.Parameters.AddWithValue("$epoch", request.OwnershipEpochBefore);
                    if (Convert.ToInt64(consistency.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                    {
                        throw new OrganizationConcurrencyException("The exact managed employee, binding, enrolled worker and host are required, and the frozen source digest and ownership epoch must match the enrollment.");
                    }
                }

                // The source revision must exist and carry the frozen number.
                using (var source = connection.CreateCommand())
                {
                    source.Transaction = transaction;
                    source.CommandText = "SELECT revision_number FROM container_profile_revisions WHERE id = $revision";
                    source.Parameters.AddWithValue("$revision", request.FromProfileRevisionId);
                    var value = source.ExecuteScalar();
                    if (value is null) throw new OrganizationNotFoundException($"Container profile revision '{request.FromProfileRevisionId}' does not exist.");
                    if (Convert.ToInt32(value, CultureInfo.InvariantCulture) != request.FromRevisionNumber)
                        throw new OrganizationValidationException("The frozen from revision number does not match the retained container profile revision.");
                }

                // The target must be the one verified built image for the target
                // revision on the rebuild host, with the exact digest and platform.
                using (var target = connection.CreateCommand())
                {
                    target.Transaction = transaction;
                    target.CommandText =
                        """
                        SELECT state, verified, image_digest, platform, host_id, profile_revision_id
                        FROM profile_builds
                        WHERE id = $build
                        """;
                    target.Parameters.AddWithValue("$build", request.ToProfileBuildId);
                    using var reader = target.ExecuteReader();
                    if (!reader.Read()) throw new OrganizationNotFoundException($"Profile build '{request.ToProfileBuildId}' does not exist.");
                    var state = reader.GetString(0);
                    var verified = reader.GetInt32(1) == 1;
                    var digest = reader.IsDBNull(2) ? null : reader.GetString(2);
                    if (state != ProfileBuildStates.Built || !verified
                        || !string.Equals(digest, request.ToImageDigest, StringComparison.Ordinal)
                        || !string.Equals(reader.GetString(3), request.ToPlatform, StringComparison.Ordinal)
                        || !string.Equals(reader.GetString(4), request.HostId, StringComparison.Ordinal)
                        || !string.Equals(reader.GetString(5), request.ToProfileRevisionId, StringComparison.Ordinal))
                    {
                        throw new OrganizationValidationException("The target profile build must be the verified built image for the target revision on the rebuild host, with the exact digest and platform.");
                    }
                }

                var id = OrganizationIds.NewEmployeeRebuildId();
                var now = Timestamp();
                Execute(connection, transaction,
                    """
                    INSERT INTO employee_rebuilds (
                        id, employee_id, runtime_binding_id, worker_id, host_id,
                        from_profile_revision_id, from_image_digest, from_revision_number,
                        to_profile_revision_id, to_profile_build_id, to_image_digest, to_platform,
                        reset_workspace, reset_home, reset_confirmation, hold_preexisting, state,
                        ownership_epoch_before, ownership_epoch_after, dispatch_hold_reason,
                        requested_by, evidence_hash, failure_summary, revision, created_at, updated_at)
                    VALUES ($id, $employee, $binding, $worker, $host,
                        $fromRevision, $fromDigest, $fromNumber,
                        $toRevision, $toBuild, $toDigest, $toPlatform,
                        $resetWorkspace, $resetHome, $resetConfirmation, $holdPreexisting, 'Intent',
                        $epoch, NULL, 'manual',
                        'owner', NULL, NULL, 1, $now, $now)
                    """,
                    ("$id", id), ("$employee", request.EmployeeId), ("$binding", request.RuntimeBindingId),
                    ("$worker", request.WorkerId), ("$host", request.HostId),
                    ("$fromRevision", request.FromProfileRevisionId), ("$fromDigest", request.FromImageDigest),
                    ("$fromNumber", request.FromRevisionNumber),
                    ("$toRevision", request.ToProfileRevisionId), ("$toBuild", request.ToProfileBuildId),
                    ("$toDigest", request.ToImageDigest), ("$toPlatform", request.ToPlatform),
                    ("$resetWorkspace", request.ResetWorkspace ? 1 : 0), ("$resetHome", request.ResetHome ? 1 : 0),
                    ("$resetConfirmation", request.ResetConfirmation), ("$holdPreexisting", request.HoldPreexisting ? 1 : 0),
                    ("$epoch", request.OwnershipEpochBefore),
                    ("$now", now));
                transaction.Commit();
                return ReadRebuilds(connection, null, id: id).Single();
            }
        });
    }

    /// <summary>
    /// Advances one rebuild along its fixed state machine. A failure carries a
    /// sanitized summary; an application requires the evidence hash and the
    /// post-rebuild ownership epoch and clears any earlier summary. The revision
    /// and current state are both checked, so a stale or replayed transition is a
    /// conflict rather than a double advance.
    /// </summary>
    public EmployeeRebuildRecord TransitionEmployeeRebuild(
        string id,
        int expectedRevision,
        string from,
        string to,
        string? failureSummary = null,
        string? evidenceHash = null,
        long? ownershipEpochAfter = null)
    {
        if (!IsBoundedIdentifier(id, OrganizationIds.RebuildPrefix) || expectedRevision < 1) throw new OrganizationValidationException("A stable employee rebuild id and current revision are required.");
        if (!EmployeeRebuildStates.IsDefined(to)) throw new OrganizationValidationException("The target employee rebuild state is invalid.");
        if (!EmployeeRebuildStates.CanTransition(from, to)) throw new OrganizationConcurrencyException($"An employee rebuild cannot move from {from} to {to}.");
        if (evidenceHash is not null && !IsHash(evidenceHash)) throw new OrganizationValidationException("Evidence hash must be an exact sha256 value.");
        if (ownershipEpochAfter is < 0) throw new OrganizationValidationException("The post-rebuild ownership epoch must be non-negative.");

        var summary = SanitizeRebuildSummary(failureSummary);
        if (string.Equals(to, EmployeeRebuildStates.Failed, StringComparison.Ordinal) && summary is null)
            throw new OrganizationValidationException("A failed employee rebuild requires a sanitized failure summary.");
        if (string.Equals(to, EmployeeRebuildStates.Applied, StringComparison.Ordinal))
        {
            if (evidenceHash is null) throw new OrganizationValidationException("An applied employee rebuild requires an evidence hash.");
            if (ownershipEpochAfter is null) throw new OrganizationValidationException("An applied employee rebuild requires the post-rebuild ownership epoch.");
            if (summary is not null) throw new OrganizationValidationException("An applied employee rebuild carries no failure summary.");
        }

        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var current = ReadRebuilds(connection, transaction, id: id).SingleOrDefault()
                    ?? throw new OrganizationNotFoundException($"Employee rebuild '{id}' does not exist.");
                if (current.Revision != expectedRevision) throw new OrganizationConcurrencyException("The employee rebuild changed; reload and retry with its current revision.");
                if (!string.Equals(current.State, from, StringComparison.Ordinal)) throw new OrganizationConcurrencyException($"The employee rebuild is not in state {from}.");

                var now = Timestamp();
                // Evidence is the proof of the exact applied result, so it is set
                // only on the transition that produces it and never carried forward:
                // a later re-transition of the same row must not inherit stale proof.
                // A failure summary likewise belongs only to the failed outcome and
                // is cleared when the row moves anywhere else.
                var evidence = string.Equals(to, EmployeeRebuildStates.Applied, StringComparison.Ordinal) ? evidenceHash : null;
                var affected = Execute(connection, transaction,
                    """
                    UPDATE employee_rebuilds
                    SET state = $state,
                        ownership_epoch_after = COALESCE($after, ownership_epoch_after),
                        evidence_hash = CASE WHEN $state = 'Applied' THEN $evidence
                                             WHEN $state = 'Failed' THEN evidence_hash
                                             ELSE NULL END,
                        failure_summary = CASE WHEN $state IN ('Failed', 'Uncertain') THEN $summary ELSE NULL END,
                        revision = revision + 1,
                        updated_at = $now
                    WHERE id = $id AND revision = $revision AND state = $from
                    """,
                    ("$state", to), ("$after", ownershipEpochAfter), ("$evidence", evidence),
                    ("$summary", summary), ("$now", now), ("$id", id), ("$revision", expectedRevision), ("$from", from));
                if (affected != 1) throw new OrganizationConcurrencyException("The employee rebuild changed before the transition.");
                transaction.Commit();
                return ReadRebuilds(connection, null, id: id).Single();
            }
        });
    }

    public EmployeeRebuildRecord? GetEmployeeRebuild(string id)
    {
        if (!IsBoundedIdentifier(id, OrganizationIds.RebuildPrefix)) return null;
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                return ReadRebuilds(connection, null, id: id).SingleOrDefault();
            }
        });
    }

    /// <summary>
    /// Specialized recovery edge used only after the coordinator proves the
    /// target revision container already exists and is running. Failed is not a
    /// general resumable state: this revision-bound edge reopens verification only
    /// and never permits another stop/remove/create cycle.
    /// </summary>
    internal EmployeeRebuildRecord ResumeFailedEmployeeRebuild(string id, int expectedRevision)
    {
        if (!IsBoundedIdentifier(id, OrganizationIds.RebuildPrefix) || expectedRevision < 1)
            throw new OrganizationValidationException("A stable employee rebuild id and current revision are required.");
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var current = ReadRebuilds(connection, transaction, id: id).SingleOrDefault()
                    ?? throw new OrganizationNotFoundException($"Employee rebuild '{id}' does not exist.");
                if (current.State != EmployeeRebuildStates.Failed || current.Revision != expectedRevision)
                    throw new OrganizationConcurrencyException("The employee rebuild changed or is not Failed.");
                var affected = Execute(connection, transaction,
                    "UPDATE employee_rebuilds SET state='Verifying', failure_summary=NULL, revision=revision+1, updated_at=$now WHERE id=$id AND revision=$revision AND state='Failed'",
                    ("$now", Timestamp()), ("$id", id), ("$revision", expectedRevision));
                if (affected != 1) throw new OrganizationConcurrencyException("The employee rebuild changed before recovery.");
                transaction.Commit();
                return ReadRebuilds(connection, null, id: id).Single();
            }
        });
    }

    public EmployeeRebuildRecord? GetActiveEmployeeRebuild(string workerId)
    {
        if (!IsBoundedIdentifier(workerId, "wrk-")) return null;
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                return ReadRebuilds(connection, null, workerId: workerId, activeOnly: true).SingleOrDefault();
            }
        });
    }

    public IReadOnlyList<EmployeeRebuildRecord> ListEmployeeRebuilds(string? workerId = null)
    {
        if (workerId is not null && !IsBoundedIdentifier(workerId, "wrk-")) return [];
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                return ReadRebuilds(connection, null, workerId: workerId);
            }
        });
    }

    /// <summary>
    /// A controller that stops while a rebuild is <c>Holding</c>, <c>Replacing</c>
    /// or <c>Verifying</c> cannot know whether the host applied the replacement.
    /// On startup those rows are moved to <c>Uncertain</c> so the next run
    /// reconciles them instead of repeating the effect blind. Each row changes at
    /// most once, so a restarted startup never double-marks. Returns the ids moved.
    /// </summary>
    public IReadOnlyList<string> MarkInterruptedEmployeeRebuildsUncertain()
    {
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var ids = new List<string>();
                using (var read = connection.CreateCommand())
                {
                    read.Transaction = transaction;
                    read.CommandText = "SELECT id FROM employee_rebuilds WHERE state IN ('Holding', 'Replacing', 'Verifying') ORDER BY id";
                    using var reader = read.ExecuteReader();
                    while (reader.Read()) ids.Add(reader.GetString(0));
                }

                if (ids.Count > 0)
                {
                    // Preserve an existing sanitized summary; only synthesize the
                    // interrupted marker when the row carried none, so a real
                    // failure reason is never overwritten by the restart notice.
                    Execute(connection, transaction,
                        "UPDATE employee_rebuilds SET state = 'Uncertain', failure_summary = COALESCE(failure_summary, $summary), revision = revision + 1, updated_at = $now WHERE state IN ('Holding', 'Replacing', 'Verifying')",
                        ("$summary", "controller-restarted-during-rebuild"), ("$now", Timestamp()));
                }

                transaction.Commit();
                return (IReadOnlyList<string>)ids;
            }
        });
    }

    private static string? SanitizeRebuildSummary(string? value)
    {
        if (value is null) return null;
        var sanitized = new string(value.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (sanitized.Length == 0) return null;
        if (sanitized.Length > 512) throw new OrganizationValidationException("The failure summary must be at most 512 characters.");
        return sanitized;
    }

    private static bool MatchesCreate(EmployeeRebuildRecord existing, EmployeeRebuildCreate request) =>
        string.Equals(existing.EmployeeId, request.EmployeeId, StringComparison.Ordinal)
        && string.Equals(existing.RuntimeBindingId, request.RuntimeBindingId, StringComparison.Ordinal)
        && string.Equals(existing.WorkerId, request.WorkerId, StringComparison.Ordinal)
        && string.Equals(existing.HostId, request.HostId, StringComparison.Ordinal)
        && string.Equals(existing.FromProfileRevisionId, request.FromProfileRevisionId, StringComparison.Ordinal)
        && string.Equals(existing.FromImageDigest, request.FromImageDigest, StringComparison.Ordinal)
        && existing.FromRevisionNumber == request.FromRevisionNumber
        && string.Equals(existing.ToProfileRevisionId, request.ToProfileRevisionId, StringComparison.Ordinal)
        && string.Equals(existing.ToProfileBuildId, request.ToProfileBuildId, StringComparison.Ordinal)
        && string.Equals(existing.ToImageDigest, request.ToImageDigest, StringComparison.Ordinal)
        && string.Equals(existing.ToPlatform, request.ToPlatform, StringComparison.Ordinal)
        && existing.ResetWorkspace == request.ResetWorkspace
        && existing.ResetHome == request.ResetHome
        && string.Equals(existing.ResetConfirmation, request.ResetConfirmation, StringComparison.Ordinal)
        && existing.OwnershipEpochBefore == request.OwnershipEpochBefore;

    private static IReadOnlyList<EmployeeRebuildRecord> ReadRebuilds(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string? id = null,
        string? workerId = null,
        bool activeOnly = false)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var where = new List<string>();
        if (id is not null) { where.Add("id = $id"); command.Parameters.AddWithValue("$id", id); }
        if (workerId is not null) { where.Add("worker_id = $worker"); command.Parameters.AddWithValue("$worker", workerId); }
        if (activeOnly) where.Add("state IN ('Intent', 'Holding', 'Replacing', 'Verifying', 'Uncertain')");
        command.CommandText =
            "SELECT id, employee_id, runtime_binding_id, worker_id, host_id, "
            + "from_profile_revision_id, from_image_digest, from_revision_number, "
            + "to_profile_revision_id, to_profile_build_id, to_image_digest, to_platform, "
            + "reset_workspace, reset_home, reset_confirmation, hold_preexisting, state, ownership_epoch_before, ownership_epoch_after, "
            + "dispatch_hold_reason, requested_by, evidence_hash, failure_summary, revision, created_at, updated_at "
            + "FROM employee_rebuilds"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : string.Empty)
            + " ORDER BY created_at DESC, id";

        var result = new List<EmployeeRebuildRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new EmployeeRebuildRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt32(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetInt32(12) == 1,
                reader.GetInt32(13) == 1,
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.GetInt32(15) == 1,
                reader.GetString(16),
                reader.GetInt64(17),
                reader.IsDBNull(18) ? null : reader.GetInt64(18),
                reader.GetString(19),
                reader.GetString(20),
                reader.IsDBNull(21) ? null : reader.GetString(21),
                reader.IsDBNull(22) ? null : reader.GetString(22),
                reader.GetInt32(23),
                DateTimeOffset.Parse(reader.GetString(24), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(reader.GetString(25), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return result;
    }
}
