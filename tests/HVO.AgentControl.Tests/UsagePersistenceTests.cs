using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class UsagePersistenceTests
{
    [Fact]
    public async Task ProvisionalFinalAndStaleReplayKeepOneAuthoritativeRow()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        await SaveTranscript(app.Store, worker, Message("assistant-1", worker.NativeSessionId, created: 1000, completed: null,
            total: null, input: 0, output: null, reasoning: null, cacheRead: null, cacheWrite: null, cost: null, currency: null));

        Assert.Equal(1, (await app.Store.BackfillUsage()).Changed);
        var provisional = Assert.Single((await app.Store.Usage(new())).Rows);
        Assert.Null(provisional.CompletedAt);
        Assert.Equal(0, provisional.InputTokens);
        var firstObservedAt = provisional.ObservedAt;

        await SaveTranscript(app.Store, worker, Message("assistant-1", worker.NativeSessionId, 1000, 2000,
            18, 10, 5, 3, 4, 1, 1.25m, "USD"));
        Assert.Equal(1, (await app.Store.BackfillUsage()).Changed);
        var final = Assert.Single((await app.Store.Usage(new())).Rows);
        Assert.Equal(2000, final.CompletedAt);
        Assert.Equal(10, final.InputTokens);
        Assert.True(final.ObservedAt >= firstObservedAt);
        Assert.Equal("provider-reported", final.CostProvenance);
        await app.Store.Write(async db => { (await db.Workers.FindAsync(worker.Id))!.ModelId = "changed-setting"; return true; });

        await SaveTranscript(app.Store, worker, Message("assistant-1", worker.NativeSessionId, 1000, null,
            999, 999, 999, 999, 999, 999, 9.99m, "USD"));
        Assert.Equal(0, (await app.Store.BackfillUsage()).Changed);
        var afterStale = Assert.Single((await app.Store.Usage(new())).Rows);
        Assert.Equal(2000, afterStale.CompletedAt);
        Assert.Equal(10, afterStale.InputTokens);
        Assert.Equal("gpt-5.6-sol", afterStale.ModelId);

        await SaveTranscript(app.Store, worker, Message("assistant-1", worker.NativeSessionId, 1000, 2000,
            18, 10, 5, 3, 4, 1, 1.25m, "USD"));
        Assert.Equal(0, (await app.Store.BackfillUsage()).Changed);
        Assert.Equal(afterStale.ObservedAt, Assert.Single((await app.Store.Usage(new())).Rows).ObservedAt);
    }

    [Fact]
    public async Task TranscriptAndCommandEvidenceDeduplicateAndSurvivePruningAndRestart()
    {
        string data, secrets;
        await using (var app = new TestApp())
        {
            data = app.DataPath; secrets = app.SecretPath;
            var worker = await PersistenceTests.SeedWorker(app.Store);
            var message = Message("assistant-2", worker.NativeSessionId, 3000, 4000, 7, 4, 3, 0, 0, 0, 0, "USD");
            await SaveTranscript(app.Store, worker, message);
            await app.Store.Write(db =>
            {
                db.Commands.Add(new CommandRecord
                {
                    Id = "command-usage",
                    RuntimeId = worker.RuntimeId,
                    WorkerId = worker.Id,
                    Kind = "Prompt",
                    ResultJson = JsonSerializer.Serialize(new { messages = new[] { JsonDocument.Parse(message).RootElement } })
                });
                return Task.FromResult(true);
            });

            var backfill = await app.Store.BackfillUsage();
            Assert.Equal(2, backfill.Accepted);
            var row = Assert.Single((await app.Store.Usage(new())).Rows);
            Assert.True(row.SeenInTranscript);
            Assert.True(row.SeenInCommandResult);
            Assert.Equal(SessionRoles.Worker, row.SessionRole);

            await app.Store.Write(async db => { await db.Messages.ExecuteDeleteAsync(); await db.Workers.ExecuteDeleteAsync(); return true; });
            Assert.Single((await app.Store.Usage(new())).Rows);
        }

        await using var restarted = new TestApp(data, secrets);
        Assert.Single((await restarted.Store.Usage(new())).Rows);
    }

    [Fact]
    public async Task ReportSeparatesRolesCurrenciesMissingValuesAndExportsStableRows()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var coordinator = new WorkerRecord
        {
            RuntimeId = worker.RuntimeId,
            ManagedServerId = worker.ManagedServerId,
            NativeSessionId = "session-coordinator",
            Directory = "/home/agent/workspaces/coordinator",
            Name = "Coordinator",
            Role = SessionRoles.Coordinator,
            HistoryGap = true
        };
        await app.Store.Write(db => { db.Workers.Add(coordinator); return Task.FromResult(true); });
        await SaveTranscript(app.Store, worker, Message("worker-message", worker.NativeSessionId, 5000, 6000,
            0, 0, null, null, null, null, 2.5m, "USD"));
        await SaveTranscript(app.Store, coordinator, Message("coordinator,message", coordinator.NativeSessionId, 3000, 4000,
            9, 5, 4, 0, 1, 0, 1.5m, "US,D"));
        await app.Store.BackfillUsage();

        var report = await app.Store.Usage(new());
        Assert.Equal("retained-evidence-only", report.Coverage.Scope);
        Assert.Equal(2, report.Coverage.Messages);
        Assert.Equal(1, report.Coverage.WorkersWithHistoryGap);
        Assert.Equal([SessionRoles.Coordinator, SessionRoles.Worker], report.Groups.Select(x => x.SessionRole));
        Assert.Equal(["US,D", "USD"], report.Groups.Select(x => x.Currency));
        var workerGroup = report.Groups.Single(x => x.SessionRole == SessionRoles.Worker);
        Assert.Equal(0, workerGroup.InputTokens.Sum);
        Assert.Equal(1, workerGroup.InputTokens.Present);
        Assert.Null(workerGroup.OutputTokens.Sum);
        Assert.Equal(1, workerGroup.OutputTokens.Missing);
        Assert.Single((await app.Store.Usage(new(WorkerId: coordinator.Id, From: 3000, To: 3000))).Rows);

        var csv = await app.Store.ExportUsageCsv(new());
        Assert.True(csv.IndexOf("coordinator,message", StringComparison.Ordinal) < csv.IndexOf("worker-message", StringComparison.Ordinal));
        Assert.Contains("\"coordinator,message\"", csv);
        Assert.Contains("\"US,D\"", csv);
        Assert.Contains(",2.5,USD,provider-reported,", csv);

        using var anonymous = app.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/usage")).StatusCode);
        using var client = await app.SignIn();
        var apiReport = await client.GetFromJsonAsync<UsageReport>("/api/v1/usage?providerId=openai&modelId=gpt-5.6-sol");
        Assert.Equal(2, apiReport!.Coverage.Messages);
        var export = await client.GetAsync("/api/v1/usage/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("model-usage.csv", export.Content.Headers.ContentDisposition?.FileName);
    }

    private static async Task SaveTranscript(ControlStore store, WorkerRecord worker, string message)
    {
        using var document = JsonDocument.Parse(message);
        var info = document.RootElement.GetProperty("info");
        var nativeId = info.GetProperty("id").GetString()!;
        await store.Write(async db =>
        {
            var record = await db.Messages.SingleOrDefaultAsync(x => x.WorkerId == worker.Id && x.NativeId == nativeId);
            if (record is null)
            {
                record = new TranscriptMessage { WorkerId = worker.Id, NativeId = nativeId };
                db.Messages.Add(record);
            }
            record.Role = "assistant";
            record.NativeCreatedAt = info.GetProperty("time").GetProperty("created").GetInt64();
            record.Json = message;
            return true;
        });
    }

    private static string Message(string id, string sessionId, long created, long? completed, long? total, long? input,
        long? output, long? reasoning, long? cacheRead, long? cacheWrite, decimal? cost, string? currency)
    {
        var time = new Dictionary<string, object?> { ["created"] = created };
        if (completed.HasValue) time["completed"] = completed.Value;
        var tokens = new Dictionary<string, object?>();
        if (total.HasValue) tokens["total"] = total.Value;
        if (input.HasValue) tokens["input"] = input.Value;
        if (output.HasValue) tokens["output"] = output.Value;
        if (reasoning.HasValue) tokens["reasoning"] = reasoning.Value;
        var cache = new Dictionary<string, object?>();
        if (cacheRead.HasValue) cache["read"] = cacheRead.Value;
        if (cacheWrite.HasValue) cache["write"] = cacheWrite.Value;
        if (cache.Count > 0) tokens["cache"] = cache;
        var info = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["sessionID"] = sessionId,
            ["role"] = "assistant",
            ["providerID"] = "openai",
            ["modelID"] = "gpt-5.6-sol",
            ["time"] = time,
            ["tokens"] = tokens
        };
        if (cost.HasValue) info["cost"] = cost.Value;
        if (currency is not null) info["currency"] = currency;
        return JsonSerializer.Serialize(new { info, parts = Array.Empty<object>() });
    }
}
