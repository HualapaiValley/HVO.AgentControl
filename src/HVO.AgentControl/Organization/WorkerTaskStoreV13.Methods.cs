using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HVO.AgentControl.Organization;

public sealed partial class OrganizationStore
{
    private const int MaxModelReportSummaryLength = 2048;
    private const int MaxModelReportChangedPathCount = 64;
    private const int MaxModelReportTestCount = 64;
    private const int MaxModelReportTestLength = 256;
    private const int MaxModelReportDeniedLength = 512;
    private const int MaxModelReportLimitationsLength = 1024;
    private const int MaxVerifierVersionLength = 64;

    /// <summary>
    /// Atomically persists one normalized task specification and its request.
    /// The task carries the bounded spec bytes and hash, and the request is the
    /// exact authorized forwarding intent. Nothing about the task is left to a
    /// raw prompt: the caller cannot persist free-form command text.
    /// </summary>
    public WorkerRequestRecord BeginWorkerRequest(BeginWorkerRequest request, WorkerTaskSpec taskSpec)
    {
        ArgumentNullException.ThrowIfNull(taskSpec);
        var normalized = NormalizeWorkerTaskSpec(taskSpec);
        var specJson = SerializeWorkerTaskSpec(normalized);
        var specHash = HashText(specJson);
        if (Encoding.UTF8.GetByteCount(specJson) > MaxTaskSpecJsonLength)
            throw new OrganizationValidationException("The task specification is too large.");

        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            RequireOpen();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var record = BeginWorkerRequestCore(connection, transaction, request, specJson, specHash);
                transaction.Commit();
                return GetWorkerRequest(record.Id)!;
            }
        });
    }

    /// <summary>
    /// Shared atomic insert of a task specification and its request. The exact
    /// employee, binding, session, orientation, enrollment and authenticated
    /// worker gate is unchanged from the pre-#220 contract.
    /// </summary>
    internal WorkerRequestRecord BeginWorkerRequestCore(
        SqliteConnection c,
        SqliteTransaction tx,
        BeginWorkerRequest request,
        string taskSpecJson,
        string taskSpecHash)
    {
        foreach (var value in new[] { request.EmployeeId, request.RuntimeBindingId, request.WorkerId, request.SessionRecordId, request.NativeSessionId, request.IdempotencyKey }) ValidateIdentifier(value, "request identity");
        if (!IsHash(request.PayloadHash) || !IsHash(request.DescriptionHash) || !IsHash(taskSpecHash))
            throw new OrganizationValidationException("Request hashes are invalid.");
        if (Encoding.UTF8.GetByteCount(taskSpecJson) is < 2 or > MaxTaskSpecJsonLength)
            throw new OrganizationValidationException("The task specification is not bounded.");
        if (request.ExpectedOwnershipEpoch < 1 || request.ExpectedProcessGeneration < 0 || request.TurnId is null)
            throw new OrganizationValidationException("Exact worker ownership and turn identity are required.");
        ValidateIdentifier(request.TurnId, "turn identity");

        using (var existing = c.CreateCommand())
        {
            existing.Transaction = tx;
            existing.CommandText = "SELECT id,payload_hash,session_id,native_session_id,employee_id,runtime_binding_id,ownership_epoch,process_generation,turn_id,task_id FROM worker_requests WHERE worker_id=$w AND idempotency_key=$k";
            Add(existing, ("$w", request.WorkerId), ("$k", request.IdempotencyKey));
            using var r = existing.ExecuteReader();
            if (r.Read())
            {
                if (r.GetString(1) != request.PayloadHash || r.GetString(2) != request.SessionRecordId || r.GetString(3) != request.NativeSessionId || r.GetString(4) != request.EmployeeId || r.GetString(5) != request.RuntimeBindingId)
                    throw new OrganizationConcurrencyException("Idempotency key was reused with changed authorization or payload.");
                var existingId = r.GetString(0);
                var existingTaskId = r.GetString(9);
                r.Close();
                // An idempotent replay may not silently substitute a different
                // specification for the durable task it already created.
                var existingSpecHash = ReadTaskSpecHash(c, tx, existingTaskId);
                if (!string.Equals(existingSpecHash, taskSpecHash, StringComparison.Ordinal))
                    throw new OrganizationConcurrencyException("Idempotency key was reused with a changed task specification.");
                return GetWorkerRequestIn(c, tx, existingId);
            }
        }

        using (var gate = c.CreateCommand())
        {
            gate.Transaction = tx;
            gate.CommandText = "SELECT COUNT(*) FROM employees e JOIN runtime_bindings b ON b.employee_id=e.id AND b.id=$b AND b.placement='DeveloperContainer' JOIN acp_sessions s ON s.id=$s AND s.native_session_id=$native AND s.employee_id=e.id AND s.status='active' JOIN worker_enrollments w ON w.runtime_binding_id=b.id AND w.worker_id=$w AND w.enabled=1 AND w.lifecycle_status='enrolled' JOIN worker_cursors c ON c.worker_id=w.worker_id AND c.connection_state='authenticated' AND c.status='running' AND c.hold_summary IS NULL AND c.observed_ownership_epoch=$epoch AND c.observed_process_generation=$process WHERE e.id=$e AND w.ownership_epoch=$epoch AND w.process_generation=$process AND EXISTS(SELECT 1 FROM orientation_assignments o WHERE o.runtime_binding_id=b.id AND o.employee_id=e.id AND o.session_id=s.id AND o.state='Comprehended') AND NOT EXISTS(SELECT 1 FROM dispatch_holds h WHERE h.runtime_binding_id=b.id AND h.active=1)";
            Add(gate, ("$b", request.RuntimeBindingId), ("$s", request.SessionRecordId), ("$native", request.NativeSessionId), ("$w", request.WorkerId), ("$e", request.EmployeeId), ("$epoch", request.ExpectedOwnershipEpoch), ("$process", request.ExpectedProcessGeneration));
            if (Convert.ToInt64(gate.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                throw new OrganizationConcurrencyException("Exact employee, binding, active session, orientation, enrollment, and authenticated running worker are required.");
        }

        var task = "tsk-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var id = "req-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var now = Now();
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText =
                """
                INSERT INTO worker_tasks (
                    id, employee_id, runtime_binding_id, worker_id, description_hash,
                    task_spec_json, task_spec_hash, model_report_json, model_report_hash,
                    model_reported_at, failure_detail, state, created_at, updated_at, revision)
                VALUES ($t, $e, $b, $w, $d, $spec, $specHash, NULL, NULL, NULL, NULL, 'Requested', $now, $now, 1);
                INSERT INTO worker_requests (
                    id, task_id, session_id, native_session_id, employee_id, runtime_binding_id,
                    worker_id, payload_hash, state, ownership_epoch, process_generation, turn_id,
                    outcome_hash, outcome_category, outcome_bytes, idempotency_key,
                    created_at, forwarded_at, completed_at, updated_at, revision)
                VALUES ($id, $t, $s, $native, $e, $b, $w, $p, 'Intent', $epoch, $process, $turn,
                    NULL, NULL, NULL, $k, $now, NULL, NULL, $now, 1)
                """;
            Add(q,
                ("$t", task), ("$e", request.EmployeeId), ("$b", request.RuntimeBindingId), ("$w", request.WorkerId),
                ("$d", request.DescriptionHash), ("$spec", taskSpecJson), ("$specHash", taskSpecHash),
                ("$id", id), ("$s", request.SessionRecordId), ("$native", request.NativeSessionId),
                ("$p", request.PayloadHash), ("$epoch", request.ExpectedOwnershipEpoch),
                ("$process", request.ExpectedProcessGeneration), ("$turn", request.TurnId),
                ("$k", request.IdempotencyKey), ("$now", now));
            q.ExecuteNonQuery();
        }

        return GetWorkerRequestIn(c, tx, id);
    }

    private static string? ReadTaskSpecHash(SqliteConnection c, SqliteTransaction tx, string taskId)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT task_spec_hash FROM worker_tasks WHERE id=$id";
        q.Parameters.AddWithValue("$id", taskId);
        var value = q.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Records one model-authored task report. The report is model evidence only:
    /// it never changes the task's execution state and can never produce
    /// <c>Verified</c>. It is accepted only on a <c>Completed</c> task and is
    /// set-once: an identical replay returns the existing row, a different report
    /// is a conflict.
    /// </summary>
    public WorkerTaskRecord RecordWorkerTaskModelReport(string taskId, int expectedRevision, ModelTaskReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!IsBoundedIdentifier(taskId, "tsk-") || expectedRevision < 1)
            throw new OrganizationValidationException("A stable task id and current revision are required.");
        var (reportJson, reportHash) = NormalizeModelTaskReport(report);

        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            RequireOpen();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var task = ReadWorkerTaskIn(connection, transaction, taskId)
                    ?? throw new OrganizationNotFoundException($"Worker task '{taskId}' does not exist.");
                if (!string.Equals(task.State, WorkerTaskStates.Completed, StringComparison.Ordinal))
                    throw new OrganizationValidationException("A model report may only be recorded for a completed task.");
                if (task.ModelReportHash is not null)
                {
                    if (string.Equals(task.ModelReportHash, reportHash, StringComparison.Ordinal))
                    {
                        transaction.Commit();
                        return task;
                    }

                    throw new OrganizationConcurrencyException("The task already carries a different model report.");
                }

                if (task.Revision != expectedRevision)
                    throw new OrganizationConcurrencyException("The worker task changed; reload and retry with its current revision.");

                var affected = Execute(connection, transaction,
                    """
                    UPDATE worker_tasks
                    SET model_report_json = $json,
                        model_report_hash = $hash,
                        model_reported_at = $now,
                        updated_at = $now,
                        revision = revision + 1
                    WHERE id = $id AND revision = $revision AND state = 'Completed' AND model_report_hash IS NULL
                    """,
                    ("$json", reportJson), ("$hash", reportHash), ("$now", Timestamp()), ("$id", taskId), ("$revision", expectedRevision));
                if (affected != 1)
                    throw new OrganizationConcurrencyException("The worker task changed before the report could be recorded.");
                transaction.Commit();
                return ReadWorkerTaskIn(connection, null, taskId)!;
            }
        });
    }

    /// <summary>
    /// Begins exactly one host verification for a completed task that already
    /// carries a model report. A task without a report cannot be verified, and a
    /// second verification for the same task is a conflict unless it is the
    /// identical replay.
    /// </summary>
    public WorkerTaskVerificationRecord BeginWorkerTaskVerification(string taskId, int expectedTaskRevision, string verifierVersion)
    {
        if (!IsBoundedIdentifier(taskId, "tsk-") || expectedTaskRevision < 1)
            throw new OrganizationValidationException("A stable task id and current revision are required.");
        var version = SanitizeBounded(verifierVersion, MaxVerifierVersionLength)
            ?? throw new OrganizationValidationException("A verifier version is required.");

        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            RequireOpen();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var task = ReadWorkerTaskIn(connection, transaction, taskId)
                    ?? throw new OrganizationNotFoundException($"Worker task '{taskId}' does not exist.");
                if (!string.Equals(task.State, WorkerTaskStates.Completed, StringComparison.Ordinal))
                    throw new OrganizationValidationException("Only a completed task can begin host verification.");
                if (task.ModelReportHash is null)
                    throw new OrganizationValidationException("A task with no model report cannot begin host verification.");
                if (task.Revision != expectedTaskRevision)
                    throw new OrganizationConcurrencyException("The worker task changed; reload and retry with its current revision.");

                var existing = ReadWorkerTaskVerificationIn(connection, transaction, taskId);
                if (existing is not null)
                {
                    if (string.Equals(existing.VerifierVersion, version, StringComparison.Ordinal))
                    {
                        transaction.Commit();
                        return existing;
                    }

                    throw new OrganizationConcurrencyException("The task already has a host verification from a different verifier.");
                }

                var id = OrganizationIds.NewTaskVerificationId();
                var now = Timestamp();
                Execute(connection, transaction,
                    """
                    INSERT INTO worker_task_verifications (
                        id, task_id, state, manifest_json, manifest_hash, test_summary_json,
                        test_summary_hash, denied_action_json, denied_action_hash,
                        verifier_version, failure_detail, verified_at, created_at, updated_at, revision)
                    VALUES ($id, $task, 'Pending', NULL, NULL, NULL, NULL, NULL, NULL, $version, NULL, NULL, $now, $now, 1)
                    """,
                    ("$id", id), ("$task", taskId), ("$version", version), ("$now", now));
                transaction.Commit();
                return ReadWorkerTaskVerificationIn(connection, null, taskId)!;
            }
        });
    }

    /// <summary>
    /// Completes one host verification. A <c>Passed</c> verification requires the
    /// manifest and test-summary evidence and atomically moves the task
    /// <c>Completed → Verified</c>. <c>Failed</c> and <c>Uncertain</c> leave the
    /// task <c>Completed</c> and record a bounded failure detail instead: a model
    /// report plus a failed verification is still not task success.
    /// </summary>
    public WorkerTaskVerificationRecord CompleteWorkerTaskVerification(string id, int expectedRevision, HostTaskVerification verification)
    {
        ArgumentNullException.ThrowIfNull(verification);
        if (!IsBoundedIdentifier(id, OrganizationIds.TaskVerificationPrefix) || expectedRevision < 1)
            throw new OrganizationValidationException("A stable verification id and current revision are required.");
        if (!WorkerTaskVerificationStates.IsDefined(verification.State) || verification.State == WorkerTaskVerificationStates.Pending)
            throw new OrganizationValidationException("A verification must complete as Passed, Failed or Uncertain.");

        string? manifestJson = null;
        string? testSummaryJson = null;
        string? deniedActionJson = null;
        string? failureDetail = null;
        string? manifestHash = null;
        string? testSummaryHash = null;
        string? deniedActionHash = null;

        if (verification.State == WorkerTaskVerificationStates.Passed)
        {
            manifestJson = NormalizeBoundedJson(verification.ManifestJson, MaxManifestJsonLength, "verification manifest")
                ?? throw new OrganizationValidationException("A passed verification requires a manifest.");
            testSummaryJson = NormalizeBoundedJson(verification.TestSummaryJson, MaxTestSummaryJsonLength, "verification test summary")
                ?? throw new OrganizationValidationException("A passed verification requires a test summary.");
            deniedActionJson = NormalizeBoundedJson(verification.DeniedActionJson, MaxDeniedActionJsonLength, "verification denied action");
            manifestHash = HashText(manifestJson);
            testSummaryHash = HashText(testSummaryJson);
            deniedActionHash = deniedActionJson is null ? null : HashText(deniedActionJson);
            if (verification.FailureDetail is not null)
                throw new OrganizationValidationException("A passed verification carries no failure detail.");
        }
        else
        {
            failureDetail = StripBounded(verification.FailureDetail, MaxFailureDetailLength)
                ?? throw new OrganizationValidationException("A failed or uncertain verification requires a sanitized failure detail.");
            deniedActionJson = NormalizeBoundedJson(verification.DeniedActionJson, MaxDeniedActionJsonLength, "verification denied action");
            deniedActionHash = deniedActionJson is null ? null : HashText(deniedActionJson);
        }

        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            RequireOpen();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var current = ReadWorkerTaskVerificationIn(connection, transaction, id: id)
                    ?? throw new OrganizationNotFoundException($"Worker task verification '{id}' does not exist.");
                if (current.Revision != expectedRevision)
                    throw new OrganizationConcurrencyException("The worker task verification changed; reload and retry with its current revision.");
                if (!string.Equals(current.State, WorkerTaskVerificationStates.Pending, StringComparison.Ordinal))
                    throw new OrganizationConcurrencyException("Only a pending host verification can be completed.");

                var task = ReadWorkerTaskIn(connection, transaction, current.TaskId)
                    ?? throw new OrganizationNotFoundException("The verified task no longer exists.");
                if (!string.Equals(task.State, WorkerTaskStates.Completed, StringComparison.Ordinal))
                    throw new OrganizationConcurrencyException("Only a completed task can complete host verification.");

                var now = Timestamp();
                var affected = Execute(connection, transaction,
                    """
                    UPDATE worker_task_verifications
                    SET state = $state,
                        manifest_json = $manifest, manifest_hash = $manifestHash,
                        test_summary_json = $summary, test_summary_hash = $summaryHash,
                        denied_action_json = $denied, denied_action_hash = $deniedHash,
                        failure_detail = $failure,
                        verified_at = CASE WHEN $state = 'Passed' THEN $now ELSE NULL END,
                        updated_at = $now,
                        revision = revision + 1
                    WHERE id = $id AND revision = $revision AND state = 'Pending'
                    """,
                    ("$state", verification.State), ("$manifest", manifestJson), ("$manifestHash", manifestHash),
                    ("$summary", testSummaryJson), ("$summaryHash", testSummaryHash),
                    ("$denied", deniedActionJson), ("$deniedHash", deniedActionHash),
                    ("$failure", failureDetail), ("$now", now), ("$id", id), ("$revision", expectedRevision));
                if (affected != 1)
                    throw new OrganizationConcurrencyException("The worker task verification changed before completion.");

                if (verification.State == WorkerTaskVerificationStates.Passed)
                {
                    // The only durable path to Verified: a passed host verification,
                    // atomically with the task transition.
                    var taskRows = Execute(connection, transaction,
                        "UPDATE worker_tasks SET state = 'Verified', updated_at = $now, revision = revision + 1 WHERE id = $task AND state = 'Completed'",
                        ("$now", now), ("$task", current.TaskId));
                    if (taskRows != 1)
                        throw new OrganizationConcurrencyException("The task was not completed and could not be verified.");
                }

                transaction.Commit();
                return ReadWorkerTaskVerificationIn(connection, null, id: id)!;
            }
        });
    }

    /// <summary>
    /// Explicitly cancels a task from <c>Requested</c>, <c>Running</c> or
    /// <c>Uncertain</c>. Cancellation is not rollback: any request effect already
    /// applied remotely remains, and no recovery obligation is cleared here.
    /// </summary>
    public WorkerTaskRecord MarkWorkerTaskCancelled(string taskId, int expectedRevision, string? detail = null)
    {
        if (!IsBoundedIdentifier(taskId, "tsk-") || expectedRevision < 1)
            throw new OrganizationValidationException("A stable task id and current revision are required.");
        var sanitizedDetail = StripBounded(detail, MaxFailureDetailLength);

        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            RequireOpen();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var task = ReadWorkerTaskIn(connection, transaction, taskId)
                    ?? throw new OrganizationNotFoundException($"Worker task '{taskId}' does not exist.");
                if (task.Revision != expectedRevision)
                    throw new OrganizationConcurrencyException("The worker task changed; reload and retry with its current revision.");
                if (!WorkerTaskStates.IsCancellable(task.State))
                    throw new OrganizationConcurrencyException("Only a requested, running or uncertain task may be cancelled.");

                var affected = Execute(connection, transaction,
                    """
                    UPDATE worker_tasks
                    SET state = 'Cancelled',
                        failure_detail = COALESCE($detail, failure_detail),
                        updated_at = $now,
                        revision = revision + 1
                    WHERE id = $id AND revision = $revision AND state IN ('Requested', 'Running', 'Uncertain')
                    """,
                    ("$detail", sanitizedDetail), ("$now", Timestamp()), ("$id", taskId), ("$revision", expectedRevision));
                if (affected != 1)
                    throw new OrganizationConcurrencyException("The worker task changed before cancellation.");
                transaction.Commit();
                return ReadWorkerTaskIn(connection, null, taskId)!;
            }
        });
    }

    /// <summary>Returns one durable task with its specification, report and timestamps.</summary>
    public WorkerTaskRecord? GetWorkerTask(string id)
    {
        if (!IsBoundedIdentifier(id, "tsk-")) return null;
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            RequireOpen();
            lock (_gate)
            {
                using var connection = OpenConnection();
                return ReadWorkerTaskIn(connection, null, id);
            }
        });
    }

    /// <summary>Lists tasks, optionally scoped by employee and/or worker.</summary>
    public IReadOnlyList<WorkerTaskRecord> ListWorkerTasks(string? employeeId = null, string? workerId = null)
    {
        if (employeeId is not null && !IsBoundedIdentifier(employeeId, OrganizationIds.EmployeePrefix)) return [];
        if (workerId is not null && !IsBoundedIdentifier(workerId, "wrk-")) return [];
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            RequireOpen();
            lock (_gate)
            {
                using var connection = OpenConnection();
                return ReadWorkerTasks(connection, null, employeeId, workerId);
            }
        });
    }

    /// <summary>Returns the host verification for a task, or null when none exists.</summary>
    public WorkerTaskVerificationRecord? GetWorkerTaskVerification(string taskId)
    {
        if (!IsBoundedIdentifier(taskId, "tsk-")) return null;
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            RequireOpen();
            lock (_gate)
            {
                using var connection = OpenConnection();
                return ReadWorkerTaskVerificationIn(connection, null, taskId);
            }
        });
    }

    private static WorkerTaskRecord? ReadWorkerTaskIn(SqliteConnection c, SqliteTransaction? tx, string id)
    {
        var tasks = ReadWorkerTasks(c, tx, id: id);
        return tasks.Count == 0 ? null : tasks[0];
    }

    private static IReadOnlyList<WorkerTaskRecord> ReadWorkerTasks(
        SqliteConnection c,
        SqliteTransaction? tx,
        string? id = null,
        string? employeeId = null,
        string? workerId = null)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        var where = new List<string>();
        if (id is not null) { where.Add("id = $id"); q.Parameters.AddWithValue("$id", id); }
        if (employeeId is not null) { where.Add("employee_id = $employee"); q.Parameters.AddWithValue("$employee", employeeId); }
        if (workerId is not null) { where.Add("worker_id = $worker"); q.Parameters.AddWithValue("$worker", workerId); }
        q.CommandText =
            "SELECT id, employee_id, runtime_binding_id, worker_id, description_hash, "
            + "task_spec_json, task_spec_hash, model_report_json, model_report_hash, model_reported_at, "
            + "failure_detail, state, created_at, updated_at, revision FROM worker_tasks"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : string.Empty)
            + " ORDER BY created_at, id";

        var result = new List<WorkerTaskRecord>();
        using var reader = q.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new WorkerTaskRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(11),
                reader.GetInt32(14),
                reader.GetString(5),
                reader.GetString(6),
                N(reader, 7),
                N(reader, 8),
                N(reader, 9) is { } reported ? DateTimeOffset.Parse(reported, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null,
                N(reader, 10),
                DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return result;
    }

    private static WorkerTaskVerificationRecord? ReadWorkerTaskVerificationIn(
        SqliteConnection c,
        SqliteTransaction? tx,
        string? taskId = null,
        string? id = null)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "SELECT id, task_id, state, manifest_json, manifest_hash, test_summary_json, test_summary_hash, "
            + "denied_action_json, denied_action_hash, verifier_version, failure_detail, verified_at, "
            + "created_at, updated_at, revision FROM worker_task_verifications WHERE "
            + (id is not null ? "id = $id" : "task_id = $task");
        q.Parameters.AddWithValue(id is not null ? "$id" : "$task", id ?? taskId!);
        using var reader = q.ExecuteReader();
        if (!reader.Read()) return null;
        return new WorkerTaskVerificationRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            N(reader, 3),
            N(reader, 4),
            N(reader, 5),
            N(reader, 6),
            N(reader, 7),
            N(reader, 8),
            reader.GetString(9),
            N(reader, 10),
            N(reader, 11) is { } verified ? DateTimeOffset.Parse(verified, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null,
            DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.GetInt32(14));
    }

    private static (string Json, string Hash) NormalizeModelTaskReport(ModelTaskReport report)
    {
        var summary = SanitizeBounded(report.Summary, MaxModelReportSummaryLength);
        if (string.IsNullOrEmpty(summary))
            throw new OrganizationValidationException("A model report requires a bounded summary.");

        var changedPaths = NormalizeReportList(report.ClaimedChangedPaths, MaxModelReportChangedPathCount, "claimed changed path", allowRelativePath: true);
        var claimedTests = NormalizeReportList(report.ClaimedTests, MaxModelReportTestCount, "claimed test", allowRelativePath: false);
        var denied = NormalizeReportOptional(report.DeniedRequestResult, MaxModelReportDeniedLength, "denied request result");
        var limitations = NormalizeReportOptional(report.Limitations, MaxModelReportLimitationsLength, "limitations");

        var envelope = new CanonicalModelReport(1, summary, changedPaths, claimedTests, denied, limitations);
        var json = JsonSerializer.Serialize(envelope);
        if (Encoding.UTF8.GetByteCount(json) > MaxModelReportJsonLength)
            throw new OrganizationValidationException("The model report is too large.");
        return (json, HashText(json));
    }

    private sealed record CanonicalModelReport(
        int Version,
        string Summary,
        IReadOnlyList<string> ClaimedChangedPaths,
        IReadOnlyList<string> ClaimedTests,
        string? DeniedRequestResult,
        string? Limitations);

    private static string[] NormalizeReportList(IReadOnlyList<string>? values, int maximum, string kind, bool allowRelativePath)
    {
        if (values is null) return [];
        if (values.Count > maximum) throw new OrganizationValidationException($"A model report may carry at most {maximum} {kind}s.");
        var normalized = values
            .Select(value => SanitizeBounded(value, MaxModelReportTestLength))
            .Select(value => string.IsNullOrEmpty(value)
                ? throw new OrganizationValidationException($"A model report {kind} must not be empty.")
                : value)
            .Select(value =>
            {
                if (allowRelativePath && !IsSafeAbsoluteSegmentPath(value))
                    throw new OrganizationValidationException("A claimed changed path must be a safe relative path.");
                return value;
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        return normalized;
    }

    private static string? NormalizeReportOptional(string? value, int maximum, string kind)
    {
        if (value is null) return null;
        var normalized = SanitizeBounded(value, maximum);
        return string.IsNullOrEmpty(normalized)
            ? throw new OrganizationValidationException($"A model report {kind} must not be blank when present.")
            : normalized;
    }

    private static string HashText(string value) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>Strips control characters and enforces a maximum length, returning null when empty.</summary>
    private static string? SanitizeBounded(string? value, int maximum)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return null;
        foreach (var ch in trimmed)
        {
            if (char.IsControl(ch)) throw new OrganizationValidationException("Task text must not contain control characters.");
        }

        if (trimmed.Length > maximum) throw new OrganizationValidationException($"Task text must be at most {maximum} characters.");
        return trimmed;
    }

    /// <summary>
    /// Removes control characters, trims and enforces a maximum length, returning
    /// null when nothing remains. Used for host-authored detail where the exact
    /// bytes are not evidence and need only be bounded and safe.
    /// </summary>
    private static string? StripBounded(string? value, int maximum)
    {
        if (value is null) return null;
        var stripped = new string(value.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (stripped.Length == 0) return null;
        if (stripped.Length > maximum) throw new OrganizationValidationException($"Task text must be at most {maximum} characters.");
        return stripped;
    }

    /// <summary>
    /// Validates that a value is bounded JSON text and returns it unchanged. The
    /// evidence is stored as exact canonical bytes; only its length is bounded here.
    /// </summary>
    private static string? NormalizeBoundedJson(string? value, int maximum, string kind)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return null;
        if (Encoding.UTF8.GetByteCount(trimmed) > maximum)
            throw new OrganizationValidationException($"The {kind} is too large.");
        return trimmed;
    }
}
