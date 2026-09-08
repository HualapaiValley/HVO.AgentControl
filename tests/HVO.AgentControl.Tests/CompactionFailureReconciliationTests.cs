using System.Reflection;
using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.OpenCode;
using HVO.AgentControl.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class CompactionFailureReconciliationTests
{
    [Theory]
    [InlineData("ContextOverflowError", true)]
    [InlineData("ContextOverflowError", false)]
    [InlineData("APIError", true)]
    [InlineData("APIError", false)]
    public async Task CompletedFailedAutomaticSummarySettlesOriginalCallerOnce(string kind, bool eventDelivered)
    {
        await using var app = new TestApp();
        var (worker, command) = await Setup(app);
        var error = Error(kind);
        var snapshot = Snapshot(worker, error, continueSuccessfully: false, providerId: "original", modelId: "task-model");
        var supervisor = Supervisor(app);
        if (eventDelivered)
        {
            var envelope = JsonSerializer.SerializeToElement(new
            {
                directory = worker.Directory,
                payload = new { type = "session.error", properties = new { sessionID = worker.NativeSessionId, error } }
            });
            await Invoke(supervisor, "ObserveEvents", worker.RuntimeId, new List<JsonElement> { envelope });
        }

        for (var i = 0; i < 4; i++) await Invoke(supervisor, "Reconcile", worker.Id, snapshot);

        var detail = await app.Store.Detail(worker.Id);
        var original = Assert.Single(detail.Commands);
        Assert.Equal(Delivery.Finished, original.State);
        Assert.Equal("Failed", detail.Worker.Outcome);
        Assert.Equal("Failed", Assert.Single(detail.Assignments).Outcome);
        Assert.Contains("automatic compaction failed", original.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("msg_summary", original.ResultJson, StringComparison.Ordinal);
        Assert.Contains(kind, original.ResultJson, StringComparison.Ordinal);
        Assert.Equal(1, await app.Store.Read(db => db.Events.CountAsync(x =>
            x.Type == "AutomaticCompactionFailed" && x.CommandId == command.Id && x.NativeId == "msg_summary")));
        Assert.Equal(eventDelivered ? 1 : 0, await app.Store.Read(db => db.Events.CountAsync(x => x.Type == "session.error")));
        var receipts = await app.Store.Read(db => db.Set<ProviderFailureReceipt>().ToListAsync());
        if (kind == "APIError")
        {
            var receipt = Assert.Single(receipts);
            Assert.Equal("provider:original", receipt.PoolId);
            Assert.Equal("AuthenticationRequired", receipt.Category);
        }
        else Assert.Empty(receipts);
    }

    [Fact]
    public async Task SuccessfulSummaryAndMarkedContinuationStillFinishOriginalCallerWithoutSummaryResult()
    {
        await using var app = new TestApp();
        var (worker, _) = await Setup(app);
        var snapshot = Snapshot(worker, null, continueSuccessfully: true, providerId: "summary-provider", modelId: "summary-model");

        await Invoke(Supervisor(app), "Reconcile", worker.Id, snapshot);

        var command = Assert.Single((await app.Store.Detail(worker.Id)).Commands);
        Assert.Equal(Delivery.Finished, command.State);
        Assert.Contains("Verified task result", command.ResultJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Internal summary", command.ResultJson, StringComparison.Ordinal);
        Assert.Empty(await app.Store.Read(db => db.Events.Where(x => x.Type == "AutomaticCompactionFailed").ToListAsync()));
    }

    [Fact]
    public async Task DifferentCompactionProviderBlocksOnlyItsProvenPool()
    {
        await using var app = new TestApp();
        var (worker, command) = await Setup(app);
        await app.Store.Write(db =>
        {
            db.Add(new ProviderPool { Id = "provider:original", ProviderId = "original", State = "Available" });
            return Task.FromResult(true);
        });
        var snapshot = Snapshot(worker, Error("APIError"), continueSuccessfully: false,
            providerId: "compaction-provider", modelId: "compaction-model");

        await Invoke(Supervisor(app), "Reconcile", worker.Id, snapshot);

        var pools = await app.Store.Read(db => db.Set<ProviderPool>().OrderBy(x => x.Id).ToListAsync());
        var original = Assert.Single(pools, x => x.Id == "provider:original");
        Assert.Equal("Available", original.State);
        Assert.Equal(0, original.ConsecutiveFailures);
        Assert.Equal("", original.LastCommandId);
        var compaction = Assert.Single(pools, x => x.Id == "provider:compaction-provider");
        Assert.Equal("AuthenticationRequired", compaction.State);
        Assert.Equal(command.Id, compaction.LastCommandId);
        var receipt = Assert.Single(await app.Store.Read(db => db.Set<ProviderFailureReceipt>().ToListAsync()));
        Assert.Equal("provider:compaction-provider", receipt.PoolId);
        var journal = Assert.Single(await app.Store.Read(db => db.Events.Where(x => x.Type == "AutomaticCompactionFailed").ToListAsync()));
        Assert.Contains("compaction-provider", journal.Payload, StringComparison.Ordinal);
        Assert.Contains("compaction-model", journal.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DifferentRouteCannotSilentlyReleaseReservedOriginalPoolAcrossRestart()
    {
        string data;
        string secrets;
        string workerId;
        string commandId;
        await using (var app = new TestApp())
        {
            var (worker, command) = await Setup(app);
            workerId = worker.Id;
            commandId = command.Id;
            await app.Store.Write(db =>
            {
                db.Add(new ProviderPool
                {
                    Id = "provider:original",
                    ProviderId = "original",
                    State = "Recovering",
                    RecoveryCommandId = command.Id
                });
                return Task.FromResult(true);
            });
            await Invoke(Supervisor(app), "Reconcile", worker.Id, Snapshot(worker, Error("APIError"), false,
                "compaction-provider", "compaction-model"));
            data = app.DataPath;
            secrets = app.SecretPath;
        }

        await using var restarted = new TestApp(data, secrets);
        var savedWorker = (await restarted.Store.Snapshot()).Workers.Single(x => x.Id == workerId);
        var snapshot = Snapshot(savedWorker, Error("APIError"), false, "compaction-provider", "compaction-model");
        for (var i = 0; i < 3; i++) await Invoke(Supervisor(restarted), "Reconcile", savedWorker.Id, snapshot);

        var pool = await restarted.Store.Read(async db => (await db.Set<ProviderPool>().FindAsync("provider:original"))!);
        Assert.Equal("RecoveryRequired", pool.State);
        Assert.True(pool.RecoveryOwnershipUnknown);
        Assert.Equal(commandId, pool.RecoveryCommandId);
        Assert.Empty(await restarted.Store.Read(db => db.Set<ProviderFailureReceipt>().Where(x => x.PoolId == "provider:original").ToListAsync()));
        Assert.Equal(1, await restarted.Store.Read(db => db.Events.CountAsync(x => x.Type == "ProviderRecoverySettlementUnattributed")));
        Assert.Equal(1, await restarted.Store.Read(db => db.Events.CountAsync(x => x.Type == "AutomaticCompactionFailed")));
    }

    [Theory]
    [InlineData(false, true, "idle")]
    [InlineData(true, false, "idle")]
    [InlineData(true, true, "busy")]
    public async Task IncompleteSuccessfulOrNonIdleSummaryDoesNotCreateFalseFailure(bool completed, bool hasError, string status)
    {
        await using var app = new TestApp();
        var (worker, command) = await Setup(app);
        var snapshot = Snapshot(worker, hasError ? Error("ContextOverflowError") : null, false,
            "summary-provider", "summary-model", completed) with
        { Status = status };

        await Invoke(Supervisor(app), "Reconcile", worker.Id, snapshot);

        var saved = Assert.Single((await app.Store.Detail(worker.Id)).Commands);
        Assert.Equal(status == "busy" ? Delivery.Running : Delivery.Accepted, saved.State);
        Assert.Empty(await app.Store.Read(db => db.Events.Where(x => x.Type == "AutomaticCompactionFailed").ToListAsync()));
        Assert.Empty(await app.Store.Read(db => db.Set<ProviderFailureReceipt>().ToListAsync()));
    }

    private static async Task<(WorkerRecord Worker, CommandRecord Command)> Setup(TestApp app)
    {
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var input = new PromptInput(Guid.NewGuid().ToString(), "Task", worker.Revision, "original", "task-model");
        var command = new CommandRecord
        {
            Id = Guid.NewGuid().ToString(),
            RuntimeId = worker.RuntimeId,
            WorkerId = worker.Id,
            Kind = "Prompt",
            State = Delivery.Running,
            NativeMessageId = "msg_caller",
            ProviderPoolId = "provider:original",
            Payload = Json.Write(input),
            ExecutionPayload = Json.Write(input)
        };
        await app.Store.Write(db =>
        {
            db.Commands.Add(command);
            db.Assignments.Add(new AssignmentRecord { Id = command.Id, WorkerId = worker.Id, Outcome = "Running" });
            return Task.FromResult(true);
        });
        return (worker, command);
    }

    private static JsonElement Error(string kind) => JsonSerializer.SerializeToElement(new
    {
        name = kind,
        data = new
        {
            statusCode = kind == "APIError" ? 401 : 400,
            message = kind == "APIError" ? "Authentication failed" : "Session too large to compact"
        }
    });

    private static NativeSnapshot Snapshot(WorkerRecord worker, JsonElement? error, bool continueSuccessfully,
        string providerId, string modelId, bool summaryCompleted = true)
    {
        JsonElement Message(string id, string role, long created, object[] parts, string? parent = null, bool summary = false,
            JsonElement? failure = null, bool completed = true, string? provider = null, string? model = null) =>
            JsonSerializer.SerializeToElement(new
            {
                info = new
                {
                    id,
                    role,
                    parentID = parent,
                    sessionID = worker.NativeSessionId,
                    time = new { created, completed = role == "assistant" && completed ? created + 1 : (long?)null },
                    summary,
                    finish = role == "assistant" ? failure.HasValue ? "error" : "stop" : null,
                    error = summary ? failure : null,
                    providerID = provider,
                    modelID = model
                },
                parts
            });
        var messages = new List<JsonElement>
        {
            Message("msg_caller", "user", 1, [new { type = "text", text = "Task" }]),
            Message("msg_compaction", "user", 2, [new { type = "compaction", auto = true }]),
            Message("msg_summary", "assistant", 3, error.HasValue ? [] : [new { type = "text", text = "Internal summary" }],
                "msg_compaction", true, error, summaryCompleted, providerId, modelId)
        };
        if (continueSuccessfully)
        {
            messages.Add(Message("msg_continue", "user", 5,
                [new { type = "text", text = "Continue if you have next steps", synthetic = true, metadata = new { compaction_continue = true } }]));
            messages.Add(Message("msg_final", "assistant", 6, [new { type = "text", text = "Verified task result" }],
                "msg_continue", provider: "original", model: "task-model"));
        }
        return new(JsonSerializer.SerializeToElement(new { }), messages.ToArray(), "idle",
            JsonSerializer.SerializeToElement(new { }), [], []);
    }

    private static RuntimeSupervisor Supervisor(TestApp app) => new(app.Store, null!, Options.Create(new ControlOptions()), NullLogger<RuntimeSupervisor>.Instance);
    private static Task<bool> Invoke(RuntimeSupervisor supervisor, string name, params object[] args) =>
        (Task<bool>)typeof(RuntimeSupervisor).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(supervisor, args)!;
}
