using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HVO.AgentControl.Organization;

public sealed partial class OrganizationStore
{
    /// <summary>The largest recent-task page an owner-facing read may request.</summary>
    public const int MaxRecentEmployeeTasks = 25;

    /// <summary>
    /// Returns the newest-first task details for one employee, each joined to its
    /// latest request and (when present) its host verification. The employee scope
    /// is exact: only rows whose <c>employee_id</c> matches are returned.
    /// </summary>
    public IReadOnlyList<HVO.AgentControl.RemoteWorker.EmployeeTaskDetail> ListEmployeeTaskDetails(string employeeId, int limit)
    {
        if (!IsBoundedIdentifier(employeeId, OrganizationIds.EmployeePrefix)) return [];
        var bounded = Math.Clamp(limit, 1, MaxRecentEmployeeTasks);
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            RequireOpen();
            lock (_gate)
            {
                using var connection = OpenConnection();
                return ReadEmployeeTaskDetails(connection, null, employeeId, taskId: null, bounded);
            }
        });
    }

    /// <summary>Returns one durable task detail by task id, or null when it does not exist.</summary>
    public HVO.AgentControl.RemoteWorker.EmployeeTaskDetail? GetEmployeeTaskDetail(string taskId)
    {
        if (!IsBoundedIdentifier(taskId, "tsk-")) return null;
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            RequireOpen();
            lock (_gate)
            {
                using var connection = OpenConnection();
                var rows = ReadEmployeeTaskDetails(connection, null, employeeId: null, taskId, MaxRecentEmployeeTasks);
                return rows.Count == 0 ? null : rows[0];
            }
        });
    }

    private static IReadOnlyList<HVO.AgentControl.RemoteWorker.EmployeeTaskDetail> ReadEmployeeTaskDetails(
        SqliteConnection c,
        SqliteTransaction? tx,
        string? employeeId,
        string? taskId,
        int limit)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            """
            SELECT t.id,t.employee_id,t.runtime_binding_id,t.worker_id,t.description_hash,
                   t.task_spec_json,t.task_spec_hash,t.model_report_json,t.model_report_hash,t.model_reported_at,
                   t.failure_detail,t.state,t.created_at,t.updated_at,t.revision,
                   r.id,r.task_id,r.session_id,r.native_session_id,r.employee_id,r.runtime_binding_id,r.worker_id,
                   r.payload_hash,r.state,r.ownership_epoch,r.process_generation,r.turn_id,r.outcome_hash,
                   r.outcome_category,r.outcome_bytes,r.idempotency_key,r.created_at,r.forwarded_at,r.completed_at,
                   r.updated_at,r.revision,
                   v.id,v.task_id,v.state,v.manifest_json,v.manifest_hash,v.test_summary_json,v.test_summary_hash,
                   v.denied_action_json,v.denied_action_hash,v.verifier_version,v.failure_detail,v.verified_at,
                   v.created_at,v.updated_at,v.revision
            FROM worker_tasks t
            LEFT JOIN worker_requests r ON r.id = (
                SELECT r2.id FROM worker_requests r2 WHERE r2.task_id = t.id
                ORDER BY r2.created_at DESC, r2.id DESC LIMIT 1)
            LEFT JOIN worker_task_verifications v ON v.task_id = t.id
            WHERE 1=1
            """
            + (employeeId is not null ? " AND t.employee_id = $employee" : string.Empty)
            + (taskId is not null ? " AND t.id = $task" : string.Empty)
            + " ORDER BY t.created_at DESC, t.id DESC LIMIT $limit";
        if (employeeId is not null) q.Parameters.AddWithValue("$employee", employeeId);
        if (taskId is not null) q.Parameters.AddWithValue("$task", taskId);
        q.Parameters.AddWithValue("$limit", limit);

        var result = new List<HVO.AgentControl.RemoteWorker.EmployeeTaskDetail>();
        using var reader = q.ExecuteReader();
        while (reader.Read())
        {
            var task = new WorkerTaskRecord(
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
                DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

            WorkerRequestRecord? request = null;
            if (!reader.IsDBNull(15))
            {
                request = new WorkerRequestRecord(
                    reader.GetString(15),
                    reader.GetString(16),
                    reader.GetString(17),
                    reader.GetString(18),
                    reader.GetString(19),
                    reader.GetString(20),
                    reader.GetString(21),
                    reader.GetString(22),
                    reader.GetString(23),
                    reader.GetInt64(24),
                    reader.GetInt64(25),
                    reader.GetString(26),
                    N(reader, 27),
                    N(reader, 28),
                    I(reader, 29),
                    reader.GetString(30),
                    reader.GetInt32(35),
                    DateTimeOffset.Parse(reader.GetString(31), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    N(reader, 32) is { } forwarded ? DateTimeOffset.Parse(forwarded, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null,
                    N(reader, 33) is { } completed ? DateTimeOffset.Parse(completed, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null,
                    DateTimeOffset.Parse(reader.GetString(34), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
            }

            HVO.AgentControl.RemoteWorker.EmployeeTaskVerificationSummary? verification = null;
            if (!reader.IsDBNull(36))
            {
                verification = new HVO.AgentControl.RemoteWorker.EmployeeTaskVerificationSummary(
                    reader.GetString(36),
                    reader.GetString(38),
                    reader.GetString(45),
                    N(reader, 40),
                    N(reader, 42),
                    N(reader, 44),
                    N(reader, 46),
                    N(reader, 47) is { } verified ? DateTimeOffset.Parse(verified, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null,
                    reader.GetInt32(50));
            }

            var spec = DeserializeWorkerTaskSpec(task.TaskSpecJson);
            result.Add(new HVO.AgentControl.RemoteWorker.EmployeeTaskDetail(
                task,
                spec,
                request,
                verification,
                string.Empty));
            result[^1] = result[^1] with { DisplayState = HVO.AgentControl.RemoteWorker.EmployeeTaskDisplayStates.For(result[^1]) };
        }

        return result;
    }

    /// <summary>
    /// Parses and re-validates a persisted canonical task specification. A stored
    /// spec that no longer normalizes is corrupt evidence, not a caller error.
    /// </summary>
    internal static WorkerTaskSpec DeserializeWorkerTaskSpec(string json)
    {
        try
        {
            var spec = JsonSerializer.Deserialize<WorkerTaskSpec>(json)
                ?? throw new OrganizationStoreCorruptException("A persisted task specification is empty.");
            return NormalizeWorkerTaskSpec(spec);
        }
        catch (JsonException exception)
        {
            throw new OrganizationStoreCorruptException("A persisted task specification is invalid.", exception);
        }
    }
}
