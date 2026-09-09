using HVO.AgentControl.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed partial class ControlStore
{
    private const int WatchdogEntityLimit = 512;
    private const int WatchdogCommandLimit = 4096;

    public Task<WatchdogStatus> WatchdogStatus() => Read(async db =>
    {
        // Keep one consistent read across tables while workers continue to update the ledger.
        await db.Database.OpenConnectionAsync();
        await using var transaction = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
        await db.Database.UseTransactionAsync(transaction);
        var now = Now;
        var runtimes = await db.Runtimes.AsNoTracking().OrderBy(x => x.Id).Take(WatchdogEntityLimit + 1)
            .Select(x => new WatchdogRuntime(x.Id, x.DesiredConnected, x.Transport, x.Health)).ToListAsync();
        var workers = await db.Workers.AsNoTracking().OrderBy(x => x.Id).Take(WatchdogEntityLimit + 1)
            .Select(x => new WatchdogWorker(x.Id, x.Activity, x.Stale)).ToListAsync();
        // All open commands matter, including those older than the full snapshot's latest 500.
        var commands = await db.Commands.AsNoTracking().Where(x => x.State == Delivery.Queued || x.State == Delivery.Dispatching ||
                x.State == Delivery.Accepted || x.State == Delivery.Running || x.State == Delivery.Unknown)
            .OrderBy(x => x.Id).Take(WatchdogCommandLimit + 1)
            .Select(x => new WatchdogCommand(x.Id, x.WorkerId, x.State, x.CreatedAt, x.LastProgressAt)).ToListAsync();
        var requests = await db.Requests.AsNoTracking().Where(x => x.State == "Pending" || x.State == "ReplyUnknown")
            .OrderBy(x => x.Id).Take(WatchdogEntityLimit + 1)
            .Select(x => new WatchdogRequest(x.Id, x.WorkerId, x.State)).ToListAsync();
        var failures = await db.Set<ProviderFailureReceipt>().AsNoTracking().Where(x => x.ObservedAt >= now - 1_800_000 &&
                db.Commands.Any(c => c.Id == x.CommandId && c.Kind == "Prompt" &&
                    !db.Commands.Any(later => later.WorkerId == c.WorkerId && later.Kind == "Prompt" && later.CreatedAt > c.CreatedAt)))
            .OrderBy(x => x.Id).Take(WatchdogEntityLimit + 1)
            .Select(x => new WatchdogProviderFailure(x.CommandId, x.Category, x.Status)).ToListAsync();
        var github = await db.GitHubAccess.AsNoTracking().OrderBy(x => x.Id).Take(WatchdogEntityLimit + 1)
            .Select(x => new WatchdogGitHubAccess(x.Id, x.State, x.ExpiresAt)).ToListAsync();
        // Some native errors (for example context limits and invalid requests) do not create
        // provider-pool holds. Retain their classifications without returning result bodies.
        var nativeErrors = await db.Database.SqlQuery<WatchdogNativeError>($$"""
            SELECT c.Id AS CommandId,
                substr(CAST(json_extract(m.value, '$.info.error.name') AS TEXT), 1, 64) AS Name,
                CAST(json_extract(m.value, '$.info.error.data.statusCode') AS INTEGER) AS Status,
                substr(CAST(json_extract(m.value, '$.info.error.data.code') AS TEXT), 1, 64) AS Code
            FROM Commands c, json_each(CASE WHEN json_valid(c.ResultJson) THEN c.ResultJson ELSE '{}' END, '$.messages') m
            WHERE c.Kind = 'Prompt' AND c.UpdatedAt >= {{now - 1_800_000}} AND m.type = 'object'
                AND json_type(m.value, '$.info.error') IS NOT NULL AND json_type(m.value, '$.info.error') <> 'null'
                AND NOT EXISTS (SELECT 1 FROM Commands later WHERE later.WorkerId = c.WorkerId
                    AND later.Kind = 'Prompt' AND later.CreatedAt > c.CreatedAt)
            ORDER BY c.Id LIMIT 513
            """).ToListAsync();
        // Extract only scalar recovery counters in SQLite. Large retained decision evidence must
        // never be materialized or serialized just to observe the control plane's health.
        var runs = await db.Database.SqlQueryRaw<WatchdogRunRow>("""
            SELECT Id, State, substr(WorkerIdsJson, 1, 8192) AS WorkerIdsJson,
                CASE WHEN json_valid(InputJson) AND json_valid(WorkerIdsJson) THEN
                    CASE WHEN json_type(InputJson) = 'object' AND json_type(WorkerIdsJson) = 'array' AND length(WorkerIdsJson) <= 8192
                        THEN 1 ELSE 0 END ELSE 0 END AS Valid,
                CASE WHEN json_valid(InputJson) THEN CAST(json_extract(InputJson, '$.repair.attempt') AS INTEGER) END AS RepairAttempt,
                CASE WHEN json_valid(InputJson) THEN CAST(json_extract(InputJson, '$.recovery.attempt') AS INTEGER) END AS RecoveryAttempt,
                CASE WHEN json_valid(InputJson) THEN CAST(json_extract(InputJson, '$.nativeFailure.held') AS INTEGER) END AS FailureHeld,
                CASE WHEN json_valid(InputJson) THEN substr(CAST(json_extract(InputJson, '$.nativeFailure.category') AS TEXT), 1, 64) END AS FailureCategory,
                CASE WHEN json_valid(InputJson) THEN CAST(json_extract(InputJson, '$.nativeFailure.status') AS INTEGER) END AS FailureStatus
            FROM CoordinationRuns WHERE State NOT IN ('Stopped', 'Completed', 'Failed', 'Cancelled')
            ORDER BY Id LIMIT 513
            """).ToListAsync();
        var coordinations = runs.Select(x => new WatchdogCoordination(x.Id, x.State, x.WorkerIdsJson, Json.Write(new
        {
            repair = new { attempt = x.RepairAttempt ?? 0 },
            recovery = new { attempt = x.RecoveryAttempt ?? 0 },
            nativeFailure = new { held = x.FailureHeld == 1, category = x.FailureCategory, status = x.FailureStatus }
        }))).ToList();
        var complete = runtimes.Count <= WatchdogEntityLimit && workers.Count <= WatchdogEntityLimit &&
            commands.Count <= WatchdogCommandLimit && requests.Count <= WatchdogEntityLimit &&
            failures.Count <= WatchdogEntityLimit && github.Count <= WatchdogEntityLimit &&
            nativeErrors.Count <= WatchdogEntityLimit &&
            runs.Count <= WatchdogEntityLimit && runs.All(x => x.Valid == 1);
        return new WatchdogStatus(1, now, complete, new(runtimes, workers, commands, requests, failures, github, nativeErrors), coordinations);
    });

    private sealed class WatchdogRunRow
    {
        public string Id { get; set; } = "";
        public string State { get; set; } = "";
        public string WorkerIdsJson { get; set; } = "[]";
        public int Valid { get; set; }
        public long? RepairAttempt { get; set; }
        public long? RecoveryAttempt { get; set; }
        public int? FailureHeld { get; set; }
        public string? FailureCategory { get; set; }
        public int? FailureStatus { get; set; }
    }
}
