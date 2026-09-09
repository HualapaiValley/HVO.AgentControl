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

public sealed partial class CoordinationTests
{
    [Fact]
    public async Task HostedCoordinatorRecoversAfterRepeatedBadOutputWithoutOwnerIntervention()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Task", [a.Id]));
        using var service = new HVO.AgentControl.Services.CoordinatorService(app.Store,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HVO.AgentControl.Services.CoordinatorService>.Instance);
        await service.StartAsync(CancellationToken.None);
        try
        {
            string? previous = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await TestApp.Wait(async () => (await app.Store.Coordinations()).Single().DecisionCommandId is { } id && id != previous,
                    "automatic coordinator command", 15);
                previous = (await app.Store.Coordinations()).Single().DecisionCommandId!;
                await Finish(app.Store, previous, "Invalid JSON from native fixture");
            }
            await TestApp.Wait(async () => (await app.Store.Coordinations()).Single().State == "Recovering", "automatic recovery state", 15);
            Assert.Equal(3, (await app.Store.Snapshot()).Commands.Count(x => x.Origin == "coordinator-decision:" + run.Id));
            // Let the real 30-second deadline elapse. No owner resume or timestamp mutation.
            await TestApp.Wait(async () => (await app.Store.Coordinations()).Single().DecisionCommandId is { } id && id != previous,
                "timer-driven recovery after backoff", 45);
            previous = (await app.Store.Coordinations()).Single().DecisionCommandId;
            await FinishDecision(app.Store, run.Id, new("Assign after recovery", [new("send_prompt", a.Id, "Perform the task once.")]));
            await TestApp.Wait(async () => (await app.Store.Snapshot()).Commands.Any(x => x.Origin == "coordinator:" + run.Id), "recovered dispatch", 15);
            var dispatch = (await app.Store.Snapshot()).Commands.Single(x => x.Origin == "coordinator:" + run.Id);
            await Finish(app.Store, dispatch.Id, "Task completed.");
            await TestApp.Wait(async () => (await app.Store.Coordinations()).Single().DecisionCommandId is { } id && id != previous,
                "follow-up decision after worker result", 15);
            await FinishDecision(app.Store, run.Id, new("Recovered", [], true));
            await TestApp.Wait(async () => (await app.Store.Coordinations()).Single().State == "Completed", "automatic completion", 15);
            Assert.Single((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task FormatRepairBudgetAndCommandReceiptsSurviveRestart()
    {
        string data, secrets, runId;
        await using (var app = new TestApp())
        {
            data = app.DataPath; secrets = app.SecretPath;
            var (coordinator, a, _) = await Seed(app.Store);
            runId = (await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Do a task", [a.Id]))).Id;
            await app.Store.CoordinationTick();
            var first = (await app.Store.Coordinations()).Single().DecisionCommandId!;
            await Finish(app.Store, first, "Here is my answer: {\"summary\":\"Done\",\"complete\":true,\"actions\":[]}");
            await app.Store.CoordinationTick();
            var recovery = (await app.Store.Coordinations()).Single();
            Assert.Equal("Ready", recovery.State);
            Assert.Equal(new DecisionRepair(1, first), Json.Read<CoordinatorContext>(recovery.InputJson).Repair);
            Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + runId);
        }
        await using var restarted = new TestApp(data, secrets);
        Assert.False(await restarted.Store.CoordinationTick()); // reconnect must be observed before recovery dispatch
        await ObserveIdle(restarted.Store);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await restarted.Store.CoordinationTick();
            var run = (await restarted.Store.Coordinations()).Single();
            Assert.Equal("Deciding", run.State);
            Assert.Equal(attempt, Json.Read<CoordinatorContext>(run.InputJson).Repair!.Attempt);
            Assert.False(await restarted.Store.CoordinationTick());
            await Finish(restarted.Store, run.DecisionCommandId!, "still not routing JSON");
            await restarted.Store.CoordinationTick();
        }
        Assert.Equal("Recovering", (await restarted.Store.Coordinations()).Single().State);
        Assert.False(await restarted.Store.CoordinationTick());
        var snapshot = await restarted.Store.Snapshot();
        Assert.Equal(3, snapshot.Commands.Count(x => x.Origin == "coordinator-decision:" + runId));
        Assert.DoesNotContain(snapshot.Commands, x => x.Origin == "coordinator:" + runId);
        await restarted.Store.Read(async db =>
        {
            Assert.Equal(3, await db.Events.CountAsync(x => x.Type == "CoordinatorDecisionRejected"));
            Assert.Equal(2, await db.Events.CountAsync(x => x.Type == "CoordinatorCorrectionRequested"));
            return true;
        });
        await RecoveryDue(restarted.Store, runId);
        await restarted.Store.CoordinationTick();
        Assert.Equal("Deciding", (await restarted.Store.Coordinations()).Single().State);
        await FinishDecision(restarted.Store, runId, new("Recovered without dispatch", [], true));
        await restarted.Store.CoordinationTick();
        var finished = (await restarted.Store.Coordinations()).Single();
        Assert.Equal("Completed", finished.State);
        Assert.Null(Json.Read<CoordinatorContext>(finished.InputJson).Recovery);
        Assert.Equal(4, (await restarted.Store.Snapshot()).Commands.Count(x => x.Origin == "coordinator-decision:" + runId));
    }

    [Fact]
    public async Task RecoveryDeadlineSurvivesRestartAndRetainsThePendingDecisionIdentity()
    {
        string data, secrets, runId, commandId;
        long retryAt;
        await using (var app = new TestApp())
        {
            data = app.DataPath; secrets = app.SecretPath;
            var (coordinator, a, _) = await Seed(app.Store);
            runId = (await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Task", [a.Id]))).Id;
            await app.Store.CoordinationTick();
            commandId = (await app.Store.Coordinations()).Single().DecisionCommandId!;
            await app.Store.RecoverActiveCoordination("Injected scheduling failure.");
            var run = (await app.Store.Coordinations()).Single();
            retryAt = Json.Read<CoordinatorContext>(run.InputJson).Recovery!.RetryAt;
            Assert.False(await app.Store.CoordinationTick());
        }
        await using var restarted = new TestApp(data, secrets);
        var saved = (await restarted.Store.Coordinations()).Single();
        Assert.Equal(retryAt, Json.Read<CoordinatorContext>(saved.InputJson).Recovery!.RetryAt);
        Assert.False(await restarted.Store.CoordinationTick());
        await ObserveIdle(restarted.Store);
        await RecoveryDue(restarted.Store, runId);
        await restarted.Store.CoordinationTick();
        Assert.Equal(commandId, (await restarted.Store.Coordinations()).Single().DecisionCommandId);
        Assert.Single((await restarted.Store.Snapshot()).Commands);
        await FinishDecision(restarted.Store, runId, new("Recovered", [], true));
        await restarted.Store.CoordinationTick();
        Assert.Equal("Completed", (await restarted.Store.Coordinations()).Single().State);
    }

    [Fact]
    public async Task FailedCoordinatorTurnUsesBackoffAndFreshCommandInsteadOfStopping()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Task", [a.Id]));
        await app.Store.CoordinationTick();
        var first = (await app.Store.Coordinations()).Single().DecisionCommandId!;
        await app.Store.Write(async db => { (await db.Commands.FindAsync(first))!.State = Delivery.Failed; return true; });
        await app.Store.CoordinationTick();
        Assert.Equal("Recovering", (await app.Store.Coordinations()).Single().State);
        Assert.False(await app.Store.CoordinationTick());
        await RecoveryDue(app.Store, run.Id);
        await app.Store.CoordinationTick();
        Assert.NotEqual(first, (await app.Store.Coordinations()).Single().DecisionCommandId);
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
    }

    [Fact]
    public async Task RecoveryBackoffIsCappedAndExplicitOwnerPauseWins()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Task", [a.Id]));
        foreach (var delay in new[] { 30_000, 60_000, 120_000, 240_000, 300_000, 300_000 })
        {
            var before = ControlStore.Now;
            await app.Store.RecoverActiveCoordination("Injected failure.");
            run = (await app.Store.Coordinations()).Single();
            Assert.InRange(Json.Read<CoordinatorContext>(run.InputJson).Recovery!.RetryAt - before, delay, delay + 3000);
            Assert.False(await app.Store.CoordinationTick());
        }
        await app.Store.ControlCoordination(run.Id, new(run.Revision, "pause"));
        await RecoveryDue(app.Store, run.Id);
        Assert.False(await app.Store.CoordinationTick());
        Assert.False(await app.Store.RecoverActiveCoordination("Must not resume an owner-paused run."));
        Assert.Empty((await app.Store.Snapshot()).Commands);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"messages\":[{}]}")]
    [InlineData("{\"messages\":[{\"parts\":[{\"type\":\"text\",\"text\":\"{}\"}]}]}")]
    public async Task MalformedDecisionEnvelopeAndMissingRequiredFieldsEnterRecovery(string result)
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Task", [a.Id]));
        await app.Store.CoordinationTick();
        var run = (await app.Store.Coordinations()).Single();
        await app.Store.Write(async db =>
        {
            var command = (await db.Commands.FindAsync(run.DecisionCommandId!))!;
            command.State = Delivery.Finished; command.ResultJson = result;
            return true;
        });
        await app.Store.CoordinationTick();
        Assert.Equal("Ready", (await app.Store.Coordinations()).Single().State);
        Assert.NotNull(Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson).Repair);
    }

    private static Task<bool> RecoveryDue(ControlStore store, string id) => store.Write(async db =>
    {
        var run = (await db.CoordinationRuns.FindAsync(id))!;
        var context = Json.Read<CoordinatorContext>(run.InputJson);
        run.InputJson = Json.Write(context with { Recovery = context.Recovery! with { RetryAt = ControlStore.Now - 1 } });
        return true;
    });

    [Fact]
    public async Task LongCompactionIsObservedWithoutHoldingOrExhaustingTheProvider()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Task", [a.Id]));
        await app.Store.CoordinationTick();
        await app.Store.Write(async db =>
        {
            var saved = (await db.CoordinationRuns.FindAsync(run.Id))!;
            var context = Json.Read<CoordinatorContext>(saved.InputJson);
            var command = (await db.Commands.FindAsync(saved.DecisionCommandId!))!;
            command.State = Delivery.Running;
            var worker = (await db.Workers.FindAsync(coordinator.Id))!;
            worker.Activity = "Active"; worker.CurrentAction = "Automatic compaction is continuing.";
            saved.InputJson = Json.Write(context with
            {
                DecisionCheckpoint = context.DecisionCheckpoint! with
                {
                    Phase = "Compaction",
                    StartedAt = ControlStore.Now - 829_000,
                    PhaseStartedAt = ControlStore.Now - 829_000,
                    LastEvidenceAt = ControlStore.Now - 829_000
                }
            });
            return true;
        });

        Assert.False(await app.Store.CoordinationTick());
        var saved = (await app.Store.Coordinations()).Single();
        var checkpoint = Json.Read<CoordinatorContext>(saved.InputJson).DecisionCheckpoint!;
        Assert.Equal("Compaction", checkpoint.Phase);
        Assert.Null(checkpoint.RecoveryIntentId);
        Assert.Equal("Deciding", saved.State);
        Assert.Equal(Delivery.Running, (await app.Store.Snapshot()).Commands.Single(x => x.Id == saved.DecisionCommandId).State);
        await app.Store.Read(async db =>
        {
            Assert.Equal(0, await db.Events.CountAsync(x => x.Type == "CoordinatorDecisionRecoveryHeld"));
            Assert.Equal(0, await db.Events.CountAsync(x => x.Type.StartsWith("Provider") && x.CommandId == saved.DecisionCommandId));
            return true;
        });
    }

    [Fact]
    public async Task ExpiredDecisionRecordsOneFencedHoldWithoutCancellationOrReplacement()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Task", [a.Id]));
        await app.Store.CoordinationTick();
        var commandId = (await app.Store.Coordinations()).Single().DecisionCommandId!;
        await app.Store.Write(async db =>
        {
            var saved = (await db.CoordinationRuns.FindAsync(run.Id))!;
            var context = Json.Read<CoordinatorContext>(saved.InputJson);
            var command = (await db.Commands.FindAsync(commandId))!;
            command.State = Delivery.Running;
            (await db.Workers.FindAsync(coordinator.Id))!.Activity = "Active";
            saved.InputJson = Json.Write(context with
            {
                DecisionCheckpoint = context.DecisionCheckpoint! with
                {
                    Phase = "Inference",
                    StartedAt = ControlStore.Now - 14_400_001,
                    PhaseStartedAt = ControlStore.Now - 7_200_001,
                    LastEvidenceAt = ControlStore.Now - 7_200_001
                }
            });
            return true;
        });

        Assert.True(await app.Store.CoordinationTick());
        var held = (await app.Store.Coordinations()).Single();
        var checkpoint = Json.Read<CoordinatorContext>(held.InputJson).DecisionCheckpoint!;
        Assert.NotNull(checkpoint.RecoveryIntentId);
        Assert.Contains("cannot prove caller-attributed cancellation", checkpoint.RecoveryHold);
        Assert.Contains("Next expected event", held.Detail);
        Assert.Equal(commandId, held.DecisionCommandId);
        Assert.Equal(Delivery.Running, (await app.Store.Snapshot()).Commands.Single(x => x.Id == commandId).State);
        Assert.False(await app.Store.CoordinationTick());
        await app.Store.Read(async db =>
        {
            Assert.Equal(1, await db.Events.CountAsync(x => x.Type == "CoordinatorDecisionRecoveryHeld" && x.CommandId == commandId));
            return true;
        });
    }

    [Fact]
    public async Task ChangedNativeSessionFencesAnExpiredDecisionInsteadOfCancellingIt()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Task", [a.Id]));
        await app.Store.CoordinationTick();
        await app.Store.Write(async db =>
        {
            var saved = (await db.CoordinationRuns.FindAsync(run.Id))!;
            var context = Json.Read<CoordinatorContext>(saved.InputJson);
            (await db.Commands.FindAsync(saved.DecisionCommandId!))!.State = Delivery.Running;
            var worker = (await db.Workers.FindAsync(coordinator.Id))!;
            worker.Activity = "Active"; worker.NativeSessionId = "successor-session";
            saved.InputJson = Json.Write(context with
            {
                DecisionCheckpoint = context.DecisionCheckpoint! with
                {
                    Phase = "Inference",
                    StartedAt = ControlStore.Now - 14_400_001,
                    PhaseStartedAt = ControlStore.Now - 7_200_001
                }
            });
            return true;
        });

        Assert.True(await app.Store.CoordinationTick());
        var saved = (await app.Store.Coordinations()).Single();
        var checkpoint = Json.Read<CoordinatorContext>(saved.InputJson).DecisionCheckpoint!;
        Assert.Contains("fence changed", checkpoint.RecoveryHold);
        Assert.Equal("Deciding", saved.State);
        Assert.Equal(1, (await app.Store.Snapshot()).Commands.Count(x => x.Origin == "coordinator-decision:" + run.Id));
    }

    [Fact]
    public async Task RuntimeActivityObservationDoesNotInvalidateAnOtherwiseStableDecisionFence()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Task", [a.Id]));
        await app.Store.CoordinationTick();
        var commandId = (await app.Store.Coordinations()).Single().DecisionCommandId!;
        await app.Store.Write(async db =>
        {
            var command = (await db.Commands.FindAsync(commandId))!;
            command.State = Delivery.Accepted; command.NativeMessageId = "caller";
            return true;
        });
        Assert.True(await app.Store.CoordinationTick()); // records the exact native caller before reconciliation.
        await ReconcileCoordinator(app, coordinator, BusySnapshot(coordinator, "caller"));
        await app.Store.Write(async db =>
        {
            var saved = (await db.CoordinationRuns.FindAsync(run.Id))!;
            var context = Json.Read<CoordinatorContext>(saved.InputJson);
            saved.InputJson = Json.Write(context with
            {
                DecisionCheckpoint = context.DecisionCheckpoint! with
                {
                    Phase = "Inference",
                    StartedAt = ControlStore.Now - 14_400_001,
                    PhaseStartedAt = ControlStore.Now - 7_200_001
                }
            });
            return true;
        });

        Assert.True(await app.Store.CoordinationTick());
        var checkpoint = Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson).DecisionCheckpoint!;
        Assert.Contains("cannot prove caller-attributed cancellation", checkpoint.RecoveryHold);
        Assert.DoesNotContain("fence changed", checkpoint.RecoveryHold);
    }

    [Fact]
    public async Task ReconciliationRecordsOnlyExactUserCallerAndObservedChildren()
    {
        await using var app = new TestApp();
        var (coordinator, _, _) = await Seed(app.Store);
        const string commandId = "exact-caller-observation";
        await app.Store.Write(db =>
        {
            db.Commands.Add(new CommandRecord
            {
                Id = commandId,
                RuntimeId = coordinator.RuntimeId,
                WorkerId = coordinator.Id,
                Kind = "Prompt",
                State = Delivery.Accepted,
                NativeMessageId = "caller-exact"
            });
            return Task.FromResult(true);
        });
        var wrongSession = JsonSerializer.SerializeToElement(new
        {
            info = new { id = "caller-exact", role = "user", sessionID = "ses_other", time = new { created = 1L } },
            parts = Array.Empty<object>()
        });
        await ReconcileCoordinator(app, coordinator, new(JsonSerializer.SerializeToElement(new { }), [wrongSession], "busy",
            JsonSerializer.SerializeToElement(new { }), [], [], Children: []));
        Assert.Null(await app.Store.Read(async db => await db.CoordinatorNativeObservations.FindAsync(commandId)));

        var child = JsonSerializer.SerializeToElement(new { id = "ses_child", parentID = coordinator.NativeSessionId });
        await ReconcileCoordinator(app, coordinator, new(JsonSerializer.SerializeToElement(new { }),
            [NativeUser("caller-exact", coordinator.NativeSessionId, 2)], "busy", JsonSerializer.SerializeToElement(new { }),
            [], [], Children: [child]));
        var observed = await app.Store.Read(async db => (await db.CoordinatorNativeObservations.FindAsync(commandId))!);
        Assert.Equal("caller-exact", observed.NativeCallerId);
        Assert.Equal(coordinator.NativeSessionId, observed.NativeSessionId);
        Assert.Equal(1, observed.ChildSessionCount);
    }

    [Fact]
    public async Task RetiredRecoveryPromptCannotClearActiveAutomaticCompactionPhase()
    {
        await using var app = new TestApp();
        var (coordinator, _, _) = await Seed(app.Store);
        var active = new CommandRecord
        {
            Id = "active",
            RuntimeId = coordinator.RuntimeId,
            WorkerId = coordinator.Id,
            Kind = "Prompt",
            State = Delivery.Running,
            NativeMessageId = "caller-active",
            AcceptedAt = 2,
            CreatedAt = 2
        };
        var retired = new CommandRecord
        {
            Id = "retired",
            RuntimeId = coordinator.RuntimeId,
            WorkerId = coordinator.Id,
            Kind = "Prompt",
            State = Delivery.Cancelled,
            NativeMessageId = "caller-retired",
            AcceptedAt = 3,
            CreatedAt = 3
        };
        await app.Store.Write(db =>
        {
            db.Commands.AddRange(active, retired);
            db.Set<ProviderPool>().Add(new ProviderPool { Id = "provider:test", ProviderId = "test", RecoveryCommandId = retired.Id });
            return Task.FromResult(true);
        });

        await ReconcileCoordinator(app, coordinator,
            BusyCompactionSnapshot(coordinator, active.NativeMessageId!, retired.NativeMessageId!));

        Assert.Equal("Automatic compaction in progress.", (await app.Store.Snapshot()).Workers.Single(x => x.Id == coordinator.Id).CurrentAction);
    }

    private static Task<bool> ReconcileCoordinator(TestApp app, WorkerRecord worker, NativeSnapshot snapshot)
    {
        var supervisor = new RuntimeSupervisor(app.Store, null!, Options.Create(new ControlOptions()), NullLogger<RuntimeSupervisor>.Instance);
        var method = typeof(RuntimeSupervisor).GetMethod("Reconcile", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task<bool>)method.Invoke(supervisor, [worker.Id, snapshot])!;
    }

    private static NativeSnapshot BusySnapshot(WorkerRecord worker, string callerId) =>
        new(JsonSerializer.SerializeToElement(new { }), [NativeUser(callerId, worker.NativeSessionId, 1)], "busy",
            JsonSerializer.SerializeToElement(new { }), [], []);

    private static NativeSnapshot BusyCompactionSnapshot(WorkerRecord worker, string activeCallerId, string retiredCallerId) =>
        new(JsonSerializer.SerializeToElement(new { }), [NativeUser(activeCallerId, worker.NativeSessionId, 1),
            NativeCompaction(worker.NativeSessionId, 2), NativeUser(retiredCallerId, worker.NativeSessionId, 3)], "busy",
            JsonSerializer.SerializeToElement(new { }), [], []);

    private static JsonElement NativeUser(string id, string sessionId, long created) => JsonSerializer.SerializeToElement(new
    {
        info = new { id, role = "user", sessionID = sessionId, time = new { created } },
        parts = Array.Empty<object>()
    });

    private static JsonElement NativeCompaction(string sessionId, long created) => JsonSerializer.SerializeToElement(new
    {
        info = new { id = "compaction", role = "user", sessionID = sessionId, time = new { created } },
        parts = new[] { new { type = "compaction", auto = true } }
    });

    [Fact]
    public async Task FormatCorrectionRefreshesObservationAndDispatchesOnlyOnce()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Do a task", [a.Id]));
        await app.Store.CoordinationTick();
        await Finish(app.Store, (await app.Store.Coordinations()).Single().DecisionCommandId!, "not JSON");
        await app.Store.CoordinationTick();
        await app.Store.Write(async db => { (await db.Workers.FindAsync(a.Id))!.Revision++; return true; });
        await app.Store.CoordinationTick();
        var observed = Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson).Workers.Single();
        Assert.Equal((await app.Store.Snapshot()).Workers.Single(x => x.Id == a.Id).Revision, observed.Revision);
        await FinishDecision(app.Store, run.Id, new("Assign", [new("send_prompt", a.Id, "Do the task")]));
        await app.Store.CoordinationTick();
        await app.Store.CoordinationTick();
        Assert.Single((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
        Assert.Null(Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson).Repair);
    }

    [Fact]
    public async Task FormatRecoveryRespectsRoundLimitAndNeverRetriesUncertainDelivery()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Do a task", [a.Id], MaxRounds: 1));
        await app.Store.CoordinationTick();
        await Finish(app.Store, (await app.Store.Coordinations()).Single().DecisionCommandId!, "not JSON");
        await app.Store.CoordinationTick();
        Assert.Equal("Paused", (await app.Store.Coordinations()).Single().State);
        Assert.Single((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator-decision:" + run.Id);
        await app.Store.Write(async db =>
        {
            var saved = (await db.CoordinationRuns.FindAsync(run.Id))!;
            saved.State = "Deciding";
            (await db.Commands.FindAsync(saved.DecisionCommandId!))!.State = Delivery.Unknown;
            return true;
        });
        await app.Store.CoordinationTick();
        Assert.Contains("delivery needs attention", (await app.Store.Coordinations()).Single().Detail);
        Assert.Null(Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson).Repair);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerboseNativeHistoryPreservesFinalEvidenceAndCanComplete(bool oversizedReport)
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Review the exact commit.", [a.Id]));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Review", [new("send_prompt", a.Id, "Review and report the exact SHA.")]));
        await app.Store.CoordinationTick();
        var assignment = (await app.Store.Snapshot()).Commands.Single(x => x.Origin == "coordinator:" + run.Id);
        var report = "CLEAN: all requested checks passed.\n" + (oversizedReport ? new string('x', 10000) : "10 tests passed.\n") +
            "Reviewed SHA: a242c6b293fd93428309b3276d8efe6c357c946c";
        var nativeResult = Json.Write(new
        {
            messages = new[]
            {
                new { parts = new[] { new { type = "text", text = "Old narration: " + new string('n', 20000) } } },
                new { parts = new[] { new { type = "tool", text = "Raw tool output: " + new string('t', 80000) } } },
                new { parts = new[] { new { type = "text", text = report } } },
                new { parts = new[] { new { type = "step-finish", text = "" } } }
            }
        });
        await app.Store.Write(async db =>
        {
            var command = (await db.Commands.FindAsync(assignment.Id))!;
            command.State = Delivery.Finished; command.ResultJson = nativeResult;
            command.ProgressText = new string('p', 20000);
            return true;
        });
        await app.Store.CoordinationTick();
        run = (await app.Store.Coordinations()).Single();
        Assert.Equal("Deciding", run.State);
        Assert.True(run.InputJson.Length < 20000);
        Assert.DoesNotContain("Old narration", run.InputJson);
        Assert.DoesNotContain("Raw tool output", run.InputJson);
        var evidence = Assert.Single(Json.Read<CoordinatorContext>(run.InputJson).Results);
        Assert.Equal(assignment.Id, evidence.Id);
        Assert.Contains("CLEAN: all requested checks passed.", evidence.Response);
        Assert.Contains("a242c6b293fd93428309b3276d8efe6c357c946c", evidence.Response);
        Assert.Equal(oversizedReport, evidence.ResponseTruncated);
        Assert.True(evidence.EarlierTextOmitted);
        Assert.True(evidence.Response.Length <= 6000);
        Assert.Empty(evidence.ProgressText);
        Assert.Equal(nativeResult, (await app.Store.Snapshot()).Commands.Single(x => x.Id == assignment.Id).ResultJson);
        await FinishDecision(app.Store, run.Id, new("Clean review received.", [], true));
        await app.Store.CoordinationTick();
        Assert.Equal("Completed", (await app.Store.Coordinations()).Single().State);
        Assert.Single((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
    }

    [Fact]
    public async Task IntermediateProgressAndCapabilitiesReachCoordinatorWithoutDuplicateAssignments()
    {
        await using var app = new TestApp();
        var (coordinator, a, b) = await Seed(app.Store);
        await app.Store.Write(async db =>
        {
            var worker = (await db.Workers.FindAsync(a.Id))!;
            worker.CapabilityReport = "Linux container; Docker daemon access unknown; no iOS signing access.";
            worker.CapabilityReportedAt = ControlStore.Now;
            return true;
        });
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Ask A to work; keep B informed.", [a.Id, b.Id], IncludeGuidance: true, ProgressMinutes: 1));
        await app.Store.CoordinationTick();
        Assert.Contains("no iOS signing access", (await app.Store.Coordinations()).Single().InputJson);
        await FinishDecision(app.Store, run.Id, new("Assign A", [new("send_prompt", a.Id, "Perform the requested long task.")]));
        await app.Store.CoordinationTick();
        var assignment = (await app.Store.Snapshot()).Commands.Single(x => x.Origin == "coordinator:" + run.Id);
        Assert.Contains("every 1 minutes", Json.Read<PromptInput>(assignment.ExecutionPayload).Text);
        await app.Store.Write(async db =>
        {
            var command = (await db.Commands.FindAsync(assignment.Id))!;
            command.State = Delivery.Running; command.ProgressText = "Build finished. Tests are still running.";
            command.LastProgressAt = ControlStore.Now;
            (await db.CoordinationRuns.FindAsync(run.Id))!.LastDecisionAt = ControlStore.Now - 61000;
            (await db.Workers.FindAsync(a.Id))!.Activity = "Active";
            return true;
        });
        await app.Store.CoordinationTick();
        Assert.Contains("Tests are still running", (await app.Store.Coordinations()).Single().InputJson);
        await FinishDecision(app.Store, run.Id, new("Inform B", [new("send_prompt", b.Id, "A has finished its build; tests remain in progress.", IncludeGuidance: false)]));
        await app.Store.CoordinationTick();
        var messages = (await app.Store.Snapshot()).Commands.Where(x => x.Origin == "coordinator:" + run.Id).ToArray();
        Assert.Equal(2, messages.Length);
        var broadcast = messages.Single(x => x.WorkerId == b.Id);
        Assert.False(Json.Read<PromptInput>(broadcast.Payload).IncludeGuidance);
        Assert.Null(Json.Read<PromptInput>(broadcast.Payload).ProgressMinutes);
        Assert.False(await app.Store.CoordinationTick());
        Assert.Equal(2, (await app.Store.Coordinations()).Single().Round);
    }

    [Fact]
    public async Task ArbitraryQuestionResponseAndBroadcastSurviveRestartWithoutDuplicates()
    {
        string data, secrets, runId, aId, bId;
        await using (var app = new TestApp())
        {
            data = app.DataPath; secrets = app.SecretPath;
            var (coordinator, a, b) = await Seed(app.Store); aId = a.Id; bId = b.Id;
            var input = new StartCoordinationInput(Guid.NewGuid().ToString(), coordinator.Id, "Ask A the time and tell B what A reported.", [a.Id, b.Id]);
            var run = await app.Store.StartCoordination(input); runId = run.Id;
            Assert.Equal(runId, (await app.Store.StartCoordination(input)).Id);
            await app.Store.CoordinationTick();
            await FinishDecision(app.Store, runId, new("Ask A", [new("send_prompt", a.Id, "What time is it? Include your timezone.")]));
            await app.Store.CoordinationTick();
            await app.Store.CoordinationTick();
            var sent = (await app.Store.Snapshot()).Commands.Where(x => x.Origin == "coordinator:" + runId).ToArray();
            Assert.Single(sent); Assert.Equal(a.Id, sent[0].WorkerId);
            await Finish(app.Store, sent[0].Id, "10:42 AM EDT, observed on Agent A.");
        }
        await using var restarted = new TestApp(data, secrets);
        await ObserveIdle(restarted.Store);
        await restarted.Store.CoordinationTick();
        var current = (await restarted.Store.Coordinations()).Single();
        Assert.Contains("10:42 AM EDT", current.InputJson);
        await FinishDecision(restarted.Store, runId, new("Tell B", [new("send_prompt", bId, "Agent A reported 10:42 AM EDT. Acknowledge receipt.")]));
        await restarted.Store.CoordinationTick();
        await restarted.Store.CoordinationTick();
        var messages = (await restarted.Store.Snapshot()).Commands.Where(x => x.Origin == "coordinator:" + runId).ToArray();
        Assert.Equal(2, messages.Length); Assert.Single(messages, x => x.WorkerId == aId);
        await Finish(restarted.Store, messages.Single(x => x.WorkerId == bId).Id, "Received Agent A's time.");
        await restarted.Store.CoordinationTick();
        await FinishDecision(restarted.Store, runId, new("Time relayed and acknowledged.", [], true));
        await restarted.Store.CoordinationTick();
        Assert.Equal("Completed", (await restarted.Store.Coordinations()).Single().State);
    }

    [Fact]
    public async Task StaleBatchHasNoPartialDispatchAndRecoveryWaitCannotDispatch()
    {
        await using var app = new TestApp();
        var (coordinator, a, b) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Ask both agents for memory usage.", [a.Id, b.Id]));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Ask", [new("send_prompt", a.Id, "Memory?"), new("send_prompt", b.Id, "Memory?")]));
        await app.Store.Write(async db => { (await db.Workers.FindAsync(b.Id))!.Revision++; return true; });
        await app.Store.CoordinationTick();
        Assert.Equal("Recovering", (await app.Store.Coordinations()).Single().State);
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
        await app.Store.CoordinationTick();
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
    }

    [Fact]
    public async Task OwnerFollowupSupersedesUnappliedDecisionWithoutInterruptingNativeTurn()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Ask the time.", [a.Id]));
        await app.Store.CoordinationTick();
        run = (await app.Store.Coordinations()).Single();
        var followup = new CoordinationPromptInput(Guid.NewGuid().ToString(), run.Revision, "Actually ask memory usage instead.");
        await app.Store.PromptCoordination(run.Id, followup);
        await app.Store.PromptCoordination(run.Id, followup);
        await FinishDecision(app.Store, run.Id, new("Old decision", [new("send_prompt", a.Id, "Time?")]));
        await app.Store.CoordinationTick();
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id || x.Kind == "Abort");
        await app.Store.CoordinationTick();
        run = (await app.Store.Coordinations()).Single();
        Assert.Contains("memory usage", run.InputJson);
        Assert.Equal(2, run.Round);
        Assert.Single((await app.Store.Snapshot()).Commands, x => x.Kind == "CoordinationInstruction");
    }

    [Fact]
    public async Task TaskQuestionIsAnsweredOnceAndDuplicateOwnerReplyIsRejected()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        var question = new PendingRequest
        {
            WorkerId = a.Id,
            NativeId = "question_native",
            Kind = "question",
            Json = Json.Write(new { questions = new[] { new { question = "Which label?", options = new[] { new { label = "acquired", description = "Claim review" } }, custom = false } } })
        };
        await app.Store.Write(db => { db.Requests.Add(question); return Task.FromResult(true); });
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Use the acquired label.", [a.Id]));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Answer the established label", [new("answer_question", a.Id, RequestId: question.Id, Answers: [["acquired"]])]));
        await app.Store.CoordinationTick();
        await app.Store.CoordinationTick();
        Assert.Single((await app.Store.Snapshot()).Commands, x => x.Kind == "Reply");
        await Assert.ThrowsAsync<ControlException>(() => app.Store.Reply(new(Guid.NewGuid().ToString(), question.Id, null, [["acquired"]])));
    }

    [Fact]
    public async Task PermissionApprovalAndUnlistedTargetsAreRejected()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Do a task", [a.Id]));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Approve", [new("approve_tool", a.Id)]));
        await app.Store.CoordinationTick();
        Assert.Equal("Recovering", (await app.Store.Coordinations()).Single().State);
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
        await RecoveryDue(app.Store, run.Id);
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Wrong target", [new("send_prompt", "not-in-this-run", "Do work")]));
        await app.Store.CoordinationTick();
        Assert.Equal("Recovering", (await app.Store.Coordinations()).Single().State);
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
    }

    [Fact]
    public void LegacyContextWithoutReceiptFieldsDeserializes()
    {
        var legacy = Json.Write(new
        {
            instruction = "Legacy instruction.",
            workers = new[] { new WorkerRecord { Name = "A" } },
            results = Array.Empty<CoordinatorResult>(),
            questions = Array.Empty<PendingRequest>()
        });
        var context = Json.Read<CoordinatorContext>(legacy);
        Assert.Equal("Legacy instruction.", context.Instruction);
        var worker = Assert.Single(context.Workers);
        Assert.Equal("A", worker.Name);
        Assert.Empty(context.Results);
        Assert.Empty(context.Questions);
        Assert.Null(context.LastAppliedDecision);
        Assert.Null(context.Dispatch);
    }

    [Fact]
    public async Task OwnerFollowupSupersedingProposalPreparesNoPhantomDispatchAndNoReceipt()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Ask the time.", [a.Id]));
        await app.Store.CoordinationTick();
        run = (await app.Store.Coordinations()).Single();
        await app.Store.PromptCoordination(run.Id, new(Guid.NewGuid().ToString(), run.Revision, "Now ask memory usage."));
        await FinishDecision(app.Store, run.Id, new("Old proposal", [new("send_prompt", a.Id, "Report the time.")]));
        await app.Store.CoordinationTick();
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
        run = (await app.Store.Coordinations()).Single();
        Assert.Equal("Ready", run.State);
        await app.Store.CoordinationTick();
        run = (await app.Store.Coordinations()).Single();
        Assert.Contains("memory usage", run.Instruction);
        var context = Json.Read<CoordinatorContext>(run.InputJson);
        Assert.Null(context.LastAppliedDecision);
        Assert.Empty(context.Dispatch!);
    }

    [Fact]
    public async Task AppliedFanOutReceiptListsEveryDispatchedCommand()
    {
        await using var app = new TestApp();
        var (coordinator, a, b) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Ask both workers.", [a.Id, b.Id]));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Fan out", [new("send_prompt", a.Id, "A task."), new("send_prompt", b.Id, "B task.")]));
        await app.Store.CoordinationTick();
        run = (await app.Store.Coordinations()).Single();
        Assert.Equal("Waiting", run.State);
        Assert.Equal(1, run.Round);
        var dispatched = (await app.Store.Snapshot()).Commands.Where(x => x.Origin == "coordinator:" + run.Id).ToArray();
        Assert.Equal(2, dispatched.Length);
        await Finish(app.Store, dispatched[0].Id, "A finished.");
        await Finish(app.Store, dispatched[1].Id, "B finished.");
        await app.Store.CoordinationTick();
        run = (await app.Store.Coordinations()).Single();
        Assert.Equal(2, run.Round);
        var context = Json.Read<CoordinatorContext>(run.InputJson);
        var receipt = Assert.IsType<DecisionReceipt>(context.LastAppliedDecision);
        Assert.Equal("Fan out", receipt.Summary);
        Assert.Equal(1, receipt.Round);
        Assert.Equal(2, receipt.Dispatched.Length);
        Assert.Contains(receipt.Dispatched, d => d.WorkerId == a.Id);
        Assert.Contains(receipt.Dispatched, d => d.WorkerId == b.Id);
        var receiptIds = receipt.Dispatched.Select(d => d.CommandId).ToArray();
        Assert.Contains(dispatched[0].Id, receiptIds);
        Assert.Contains(dispatched[1].Id, receiptIds);
        var ledger = Assert.IsType<DispatchEvidence[]>(context.Dispatch);
        Assert.Equal(2, ledger.Length);
        Assert.Contains(ledger, d => d.CommandId == dispatched[0].Id && d.State == Delivery.Finished);
        Assert.Contains(ledger, d => d.CommandId == dispatched[1].Id && d.State == Delivery.Finished);
    }

    [Fact]
    public async Task TaskModelOverrideIsPinnedWithoutChangingWorkerDefaults()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        await app.Store.Write(async db =>
        {
            var worker = (await db.Workers.FindAsync(a.Id))!;
            worker.ProviderId = "openai"; worker.ModelId = "implementation"; worker.Variant = "high";
            worker.ModelsJson = Json.Write(new[] { new ModelChoice("openai", "review", "Review") });
            return true;
        });
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Review", [a.Id]));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Review with available model", [new("send_prompt", a.Id, "Review exact commit", ProviderId: "openai", ModelId: "review")]));
        await app.Store.CoordinationTick();
        var snapshot = await app.Store.Snapshot();
        var command = Assert.Single(snapshot.Commands, x => x.Origin == "coordinator:" + run.Id);
        var payload = Json.Read<PromptInput>(command.ExecutionPayload);
        Assert.Equal("review", payload.ModelId); Assert.Equal("openai", payload.ProviderId); Assert.Equal("", payload.Variant);
        var saved = snapshot.Workers.Single(x => x.Id == a.Id);
        Assert.Equal("implementation", saved.ModelId); Assert.Equal("high", saved.Variant); Assert.Equal(a.NativeSessionId, saved.NativeSessionId);
        await app.Store.Recover();
        Assert.Equal(command.ExecutionPayload, (await app.Store.Snapshot()).Commands.Single(x => x.Id == command.Id).ExecutionPayload);
        Assert.Equal("Waiting", (await app.Store.Coordinations()).Single().State);
    }

    [Theory]
    [InlineData(true, "xhigh", "xhigh")]
    [InlineData(true, null, "")]
    [InlineData(false, "xhigh", "xhigh")]
    [InlineData(false, "", "")]
    [InlineData(false, null, "high")]
    public async Task TaskReasoningIsPinnedAcrossRestart(bool overrideModel, string? variant, string expected)
    {
        string data, secrets, workerId, commandId, execution;
        await using (var app = new TestApp())
        {
            data = app.DataPath; secrets = app.SecretPath;
            var (coordinator, worker, _) = await Seed(app.Store);
            workerId = worker.Id;
            await app.Store.Write(async db =>
            {
                var saved = (await db.Workers.FindAsync(workerId))!;
                saved.ProviderId = "openai"; saved.ModelId = "default"; saved.Variant = "high";
                saved.ModelsJson = Json.Write(new[] { new ModelChoice("openai", "default", "Default", ["high", "xhigh"]),
                    new ModelChoice("openai", "astra", "Astra", ["xhigh"]) });
                return true;
            });
            var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Design", [workerId]));
            await app.Store.CoordinationTick();
            await FinishDecision(app.Store, run.Id, new("Design task", [new("send_prompt", workerId, "Design the layout",
                ProviderId: overrideModel ? "openai" : null, ModelId: overrideModel ? "astra" : null, Variant: variant)]));
            await app.Store.CoordinationTick();
            var command = Assert.Single((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
            commandId = command.Id; execution = command.ExecutionPayload;
            var prompt = Json.Read<PromptInput>(execution);
            Assert.Equal(expected, prompt.Variant);
            Assert.Equal(overrideModel ? "astra" : "default", prompt.ModelId);
            Assert.Equal("openai", prompt.ProviderId);
        }
        await using var restarted = new TestApp(data: data, secrets: secrets);
        await restarted.Store.Recover();
        var snapshot = await restarted.Store.Snapshot();
        Assert.Equal(execution, snapshot.Commands.Single(x => x.Id == commandId).ExecutionPayload);
        var defaults = snapshot.Workers.Single(x => x.Id == workerId);
        Assert.Equal("default", defaults.ModelId); Assert.Equal("high", defaults.Variant);
    }

    [Theory]
    [InlineData(false, "send_prompt")]
    [InlineData(true, "send_prompt")]
    [InlineData(false, "answer_question")]
    public async Task InvalidTaskReasoningRejectsEntireBatch(bool overrideModel, string actionType)
    {
        await using var app = new TestApp();
        var (coordinator, a, b) = await Seed(app.Store);
        await app.Store.Write(async db =>
        {
            var worker = (await db.Workers.FindAsync(b.Id))!;
            worker.ProviderId = "openai"; worker.ModelId = "default";
            worker.ModelsJson = Json.Write(new[] { new ModelChoice("openai", "default", "Default", ["high"]),
                new ModelChoice("openai", "review", "Review", ["high"]) });
            return true;
        });
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Batch", [a.Id, b.Id]));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Batch", [new("send_prompt", a.Id, "Valid first task"),
            new(actionType, b.Id, "Invalid reasoning", ProviderId: overrideModel ? "openai" : null,
                ModelId: overrideModel ? "review" : null, Variant: "xhigh")]));
        await app.Store.CoordinationTick();
        var rejected = (await app.Store.Coordinations()).Single();
        Assert.Equal("Recovering", rejected.State);
        Assert.Contains(actionType == "send_prompt" ? "reasoning variant supported" : "only to send_prompt", rejected.Detail);
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
    }

    [Theory]
    [InlineData(null, "review")]
    [InlineData("openai", null)]
    [InlineData("openai", "unavailable")]
    public async Task InvalidTaskModelRejectsEntireBatch(string? provider, string? model)
    {
        await using var app = new TestApp();
        var (coordinator, a, b) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Two tasks", [a.Id, b.Id]));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Batch", [new("send_prompt", a.Id, "Allowed default"),
            new("send_prompt", b.Id, "Invalid model", ProviderId: provider, ModelId: model)]));
        await app.Store.CoordinationTick();
        Assert.Equal("Recovering", (await app.Store.Coordinations()).Single().State);
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
    }

    [Fact]
    public async Task OwnerFollowupWakesDecisionWhileUnrelatedWorkHasNoNewProgress()
    {
        await using var app = new TestApp();
        var (coordinator, a, b) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Long task", [a.Id, b.Id]));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Start long task", [new("send_prompt", a.Id, "Long task")]));
        await app.Store.CoordinationTick();
        var original = Assert.Single((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
        await app.Store.Write(async db => { (await db.Commands.FindAsync(original.Id))!.State = Delivery.Running; return true; });
        await app.Store.CoordinationTick();
        run = (await app.Store.Coordinations()).Single();
        Assert.Null(run.DecisionCommandId);
        await app.Store.PromptCoordination(run.Id, new(Guid.NewGuid().ToString(), run.Revision, "Give idle worker B an independent task"));
        await app.Store.Recover();
        await ObserveIdle(app.Store);
        await app.Store.CoordinationTick();
        var awakened = (await app.Store.Coordinations()).Single();
        Assert.Equal("Deciding", awakened.State); Assert.NotNull(awakened.DecisionCommandId);
        var context = Json.Read<CoordinatorContext>(awakened.InputJson);
        Assert.Contains("Give idle worker B", context.Instruction);
        Assert.Equal(Delivery.Running, Assert.Single(context.Results).State);
        await app.Store.CoordinationTick();
        var snapshot = await app.Store.Snapshot();
        Assert.Single(snapshot.Commands, x => x.Origin == "coordinator:" + run.Id);
        Assert.Equal(2, snapshot.Commands.Count(x => x.Origin == "coordinator-decision:" + run.Id));
        Assert.Equal(Delivery.Running, snapshot.Commands.Single(x => x.Id == original.Id).State);
    }

    [Fact]
    public async Task IdleReassessmentRevisitsUnchangedBacklogOncePerDeadlineWithoutInventingAssignments()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Work the backlog", [a.Id]));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Waiting for a merge", []));
        await app.Store.CoordinationTick();
        Assert.False(await app.Store.CoordinationTick());
        await IdleDeadlineDue(app.Store, run.Id);
        await app.Store.CoordinationTick();
        var reassessed = (await app.Store.Coordinations()).Single();
        Assert.Equal("Deciding", reassessed.State);
        Assert.Contains("Idle capacity", Json.Read<CoordinatorContext>(reassessed.InputJson).ReassessmentReason);
        Assert.False(await app.Store.CoordinationTick());
        await FinishDecision(app.Store, run.Id, new("No eligible work; dependency still open", []));
        await app.Store.CoordinationTick();
        Assert.False(await app.Store.CoordinationTick());
        var snapshot = await app.Store.Snapshot();
        Assert.DoesNotContain(snapshot.Commands, x => x.Origin == "coordinator:" + run.Id);
        Assert.Equal(2, snapshot.Commands.Count(x => x.Origin == "coordinator-decision:" + run.Id));
        Assert.Equal(1, await app.Store.Read(db => db.Events.CountAsync(x => x.Type == "CoordinatorIdleReassessmentRequested")));
    }

    [Fact]
    public async Task IdleReassessmentKeepsLongRunningWorkAndUsesOnlyFreeCapacity()
    {
        await using var app = new TestApp();
        var (coordinator, a, b) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Parallel backlog", [a.Id, b.Id]));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Start A", [new("send_prompt", a.Id, "Long task")]));
        await app.Store.CoordinationTick();
        var original = (await app.Store.Snapshot()).Commands.Single(x => x.Origin == "coordinator:" + run.Id);
        await app.Store.Write(async db =>
        {
            (await db.Commands.FindAsync(original.Id))!.State = Delivery.Running;
            (await db.Workers.FindAsync(a.Id))!.Activity = "Active";
            return true;
        });
        Assert.False(await app.Store.CoordinationTick());
        await IdleDeadlineDue(app.Store, run.Id);
        await app.Store.CoordinationTick();
        var context = Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson);
        Assert.NotNull(context.ReassessmentReason);
        Assert.Equal(Delivery.Running, context.Results.Single(x => x.Id == original.Id).State);
        Assert.Single((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
    }

    [Fact]
    public async Task IdleReassessmentDeadlineSurvivesRestartAndRequiresObservedFreeCapacity()
    {
        string data, secrets, runId;
        await using (var app = new TestApp())
        {
            data = app.DataPath; secrets = app.SecretPath;
            var (coordinator, a, _) = await Seed(app.Store);
            runId = (await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Backlog", [a.Id]))).Id;
            await app.Store.CoordinationTick();
            await FinishDecision(app.Store, runId, new("Waiting", []));
            await app.Store.CoordinationTick();
            await IdleDeadlineDue(app.Store, runId);
        }
        await using var restarted = new TestApp(data, secrets);
        Assert.False(await restarted.Store.CoordinationTick());
        await ObserveIdle(restarted.Store);
        await restarted.Store.CoordinationTick();
        Assert.NotNull(Json.Read<CoordinatorContext>((await restarted.Store.Coordinations()).Single().InputJson).ReassessmentReason);
        Assert.False(await restarted.Store.CoordinationTick());
    }

    [Fact]
    public async Task IdleDeadlineDoesNotBypassAllBusyOrConfiguredRoundLimit()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Backlog", [a.Id]));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Wait", []));
        await app.Store.CoordinationTick();
        await IdleDeadlineDue(app.Store, run.Id);
        await app.Store.Write(async db => { (await db.Workers.FindAsync(a.Id))!.Activity = "Active"; return true; });
        Assert.False(await app.Store.CoordinationTick());
        await ObserveIdle(app.Store);
        await app.Store.Write(async db => { var saved = (await db.CoordinationRuns.FindAsync(run.Id))!; saved.Round = saved.MaxRounds; return true; });
        await app.Store.CoordinationTick();
        Assert.Equal("Paused", (await app.Store.Coordinations()).Single().State);
        Assert.Single((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator-decision:" + run.Id);
    }

    [Fact]
    public async Task OwnerPermissionDoesNotBlockIdlePeerOrAppearAsActionableQuestion()
    {
        await using var app = new TestApp();
        var (coordinator, a, b) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Parallel backlog", [a.Id, b.Id]));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Wait for approval", []));
        await app.Store.CoordinationTick();
        await app.Store.Write(async db =>
        {
            (await db.Workers.FindAsync(a.Id))!.Activity = "WaitingPermission";
            db.Requests.Add(new PendingRequest { Id = "owner-approval", NativeId = "per_owner", WorkerId = a.Id, Kind = "permission", State = "Pending" });
            return true;
        });
        await app.Store.CoordinationTick();
        var observed = Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson);
        Assert.Empty(observed.Questions);
        Assert.Equal("WaitingPermission", observed.Workers.Single(x => x.Id == a.Id).Activity);
        await FinishDecision(app.Store, run.Id, new("Await owner; other work may be ready", []));
        await app.Store.CoordinationTick();
        await IdleDeadlineDue(app.Store, run.Id);
        await app.Store.CoordinationTick();
        var refreshed = Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson);
        Assert.NotNull(refreshed.ReassessmentReason);
        Assert.Empty(refreshed.Questions);
        Assert.Equal("Pending", await app.Store.Read(async db => (await db.Requests.FindAsync("owner-approval"))!.State));
    }

    private static Task<bool> IdleDeadlineDue(ControlStore store, string id) => store.Write(async db =>
    {
        (await db.CoordinationRuns.FindAsync(id))!.LastDecisionAt = ControlStore.Now - 6 * 60000;
        return true;
    });

    private static async Task<(WorkerRecord, WorkerRecord, WorkerRecord)> Seed(ControlStore store)
    {
        var coordinator = await PersistenceTests.SeedWorker(store);
        var a = new WorkerRecord { Name = "A", RuntimeId = coordinator.RuntimeId, NativeSessionId = "ses_a", Directory = "/home/agent/workspaces/b" };
        var b = new WorkerRecord { Name = "B", RuntimeId = coordinator.RuntimeId, NativeSessionId = "ses_b", Directory = "/home/agent/workspaces/c" };
        await store.Write(async db => { (await db.Workers.FindAsync(coordinator.Id))!.Role = SessionRoles.Coordinator; db.Workers.AddRange(a, b); return true; });
        await ObserveIdle(store); return (coordinator, a, b);
    }
    private static Task<bool> ObserveIdle(ControlStore store) => store.Write(async db =>
    {
        foreach (var worker in await db.Workers.ToListAsync()) { worker.Activity = "Idle"; worker.Stale = false; worker.LastObservedAt = ControlStore.Now; }
        return true;
    });
    private static async Task FinishDecision(ControlStore store, string id, CoordinatorDecision decision)
    {
        var run = (await store.Coordinations()).Single(x => x.Id == id);
        await Finish(store, run.DecisionCommandId!, Json.Write(decision));
    }
    private static Task<bool> Finish(ControlStore store, string id, string text) => store.Write(async db =>
    {
        var command = (await db.Commands.FindAsync(id))!; command.State = Delivery.Finished;
        command.ResultJson = Json.Write(new { messages = new[] { new { parts = new[] { new { type = "text", text } } } } });
        return true;
    });
}
