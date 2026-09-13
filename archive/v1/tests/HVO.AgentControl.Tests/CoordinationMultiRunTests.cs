using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed partial class CoordinationTests
{
    [Fact]
    public async Task DifferentCoordinatorSessionsBothPlanAndDispatchInOnePass()
    {
        await using var app = new TestApp();
        var (firstCoordinator, a, b) = await Seed(app.Store);
        var secondCoordinator = await AddCoordinator(app.Store, firstCoordinator.RuntimeId);
        var first = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), firstCoordinator.Id, "Project A", [a.Id]));
        var second = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), secondCoordinator.Id, "Project B", [b.Id]));

        Assert.True(await app.Store.CoordinationTick());
        Assert.All(await app.Store.Coordinations(), run => Assert.Equal("Deciding", run.State));
        await FinishDecision(app.Store, first.Id, new("Assign A", [new("send_prompt", a.Id, "Implement A")]));
        await FinishDecision(app.Store, second.Id, new("Assign B", [new("send_prompt", b.Id, "Implement B")]));
        Assert.True(await app.Store.CoordinationTick());

        var commands = (await app.Store.Snapshot()).Commands;
        Assert.Single(commands, x => x.Origin == "coordinator:" + first.Id && x.WorkerId == a.Id);
        Assert.Single(commands, x => x.Origin == "coordinator:" + second.Id && x.WorkerId == b.Id);
        Assert.All(await app.Store.Coordinations(), run => Assert.Equal("Waiting", run.State));
        Assert.False(await app.Store.CoordinationTick());
    }

    [Fact]
    public async Task OneNonterminalRunPerCoordinatorIncludesOwnerPausedRunsAndAllowsIdempotentRetry()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        var request = new StartCoordinationInput(Guid.NewGuid().ToString(), coordinator.Id, "Project A", [a.Id]);
        var run = await app.Store.StartCoordination(request);
        Assert.Equal(run.Id, (await app.Store.StartCoordination(request)).Id);
        run = await app.Store.ControlCoordination(run.Id, new(run.Revision, "pause"));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.StartCoordination(request with { Id = Guid.NewGuid().ToString() }));
        await app.Store.ControlCoordination(run.Id, new(run.Revision, "stop"));
        var next = await app.Store.StartCoordination(request with { Id = Guid.NewGuid().ToString() });
        Assert.NotEqual(run.Id, next.Id);
        Assert.Equal("Ready", next.State);
    }

    [Fact]
    public async Task ConcurrentStartRequestsCannotClaimTheSameCoordinatorTwice()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try
            {
                await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Same session", [a.Id]));
                return true;
            }
            catch (ControlException) { return false; }
        }));
        Assert.Single(results, x => x);
        Assert.Single(await app.Store.Coordinations());
    }

    [Fact]
    public async Task MalformedStoredContextBacksOffOnlyItsRunAndPreservesPendingDecision()
    {
        await using var app = new TestApp();
        var (coordinator, a, b) = await Seed(app.Store);
        var secondCoordinator = await AddCoordinator(app.Store, coordinator.RuntimeId);
        var broken = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Broken context", [a.Id]));
        var healthy = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), secondCoordinator.Id, "Independent work", [b.Id]));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, broken.Id, new("Broken", [], true));
        await FinishDecision(app.Store, healthy.Id, new("Done", [], true));
        var originalDecision = (await app.Store.Coordinations()).Single(x => x.Id == broken.Id).DecisionCommandId;
        await CorruptCoordinationContext(app.Store, broken.Id);

        Assert.True(await app.Store.CoordinationTick());
        var runs = await app.Store.Coordinations();
        var recovering = runs.Single(x => x.Id == broken.Id);
        Assert.Equal("Recovering", recovering.State);
        Assert.Equal(originalDecision, recovering.DecisionCommandId);
        Assert.Contains("JsonException", Json.Read<CoordinatorContext>(recovering.InputJson).Recovery!.Reason);
        Assert.Equal("Completed", runs.Single(x => x.Id == healthy.Id).State);
        Assert.False(await app.Store.CoordinationTick());
        Assert.Equal(2, (await app.Store.Snapshot()).Commands.Count);
    }

    [Fact]
    public async Task RecoveryStorageFailureDoesNotRollBackOrStarveAnotherRun()
    {
        await using var app = new TestApp();
        var (coordinator, a, b) = await Seed(app.Store);
        var secondCoordinator = await AddCoordinator(app.Store, coordinator.RuntimeId);
        var broken = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Broken context", [a.Id]));
        var healthy = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), secondCoordinator.Id, "Independent work", [b.Id]));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, broken.Id, new("Broken", [], true));
        await FinishDecision(app.Store, healthy.Id, new("Done", [], true));
        await CorruptCoordinationContext(app.Store, broken.Id);
        await app.Store.Write(async db =>
        {
            // Fail only the first run's recovery commit, after its scheduling transaction
            // already rolled back. Other runs must still commit in this same pass.
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER reject_coordination_recovery BEFORE UPDATE ON CoordinationRuns
                WHEN NEW.State = 'Recovering'
                BEGIN SELECT RAISE(FAIL, 'injected recovery persistence failure'); END;
                """);
            return true;
        });

        await Assert.ThrowsAsync<AggregateException>(() => app.Store.CoordinationTick());
        var runs = await app.Store.Coordinations();
        var failed = runs.Single(x => x.Id == broken.Id);
        Assert.Equal("Deciding", failed.State);
        Assert.Equal("{bad checkpoint", failed.InputJson);
        Assert.NotNull(failed.DecisionCommandId);
        Assert.Equal("Completed", runs.Single(x => x.Id == healthy.Id).State);
        await app.Store.Write(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_coordination_recovery;");
            return true;
        });
        Assert.True(await app.Store.CoordinationTick());
        Assert.Equal("Recovering", (await app.Store.Coordinations()).Single(x => x.Id == broken.Id).State);
        Assert.Equal(2, (await app.Store.Snapshot()).Commands.Count);
    }

    [Fact]
    public async Task SharedWorkerIsClaimedOnceAcrossConcurrentCoordinationPasses()
    {
        await using var app = new TestApp();
        var (coordinator, shared, _) = await Seed(app.Store);
        var secondCoordinator = await AddCoordinator(app.Store, coordinator.RuntimeId);
        var first = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Project A", [shared.Id]));
        var second = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), secondCoordinator.Id, "Project B", [shared.Id]));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, first.Id, new("Assign shared worker", [new("send_prompt", shared.Id, "Implement A")]));
        await FinishDecision(app.Store, second.Id, new("Assign same worker", [new("send_prompt", shared.Id, "Implement B")]));

        await Task.WhenAll(app.Store.CoordinationTick(), app.Store.CoordinationTick());
        var assignments = (await app.Store.Snapshot()).Commands.Where(x => x.WorkerId == shared.Id).ToArray();
        Assert.Single(assignments);
        Assert.Equal(Delivery.Queued, assignments[0].State);
        var runs = await app.Store.Coordinations();
        Assert.Single(runs, x => x.State == "Waiting");
        Assert.Single(runs, x => x.State == "Recovering" && x.Detail.Contains("without dispatch", StringComparison.Ordinal));
        Assert.False(await app.Store.CoordinationTick());
        Assert.Single((await app.Store.Snapshot()).Commands, x => x.WorkerId == shared.Id);
    }

    [Fact]
    public async Task EveryActiveRunGetsRecoveryAndEveryNonterminalRunGetsAHeartbeat()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        var secondCoordinator = await AddCoordinator(app.Store, coordinator.RuntimeId);
        var first = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "A", [a.Id]));
        var second = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), secondCoordinator.Id, "B", [a.Id]));
        Assert.True(await app.Store.RecoverActiveCoordination("Shared dependency interrupted"));
        Assert.All(await app.Store.Coordinations(), run => Assert.Equal("Recovering", run.State));
        var firstSaved = (await app.Store.Coordinations()).Single(x => x.Id == first.Id);
        await app.Store.ControlCoordination(first.Id, new(firstSaved.Revision, "pause"));
        var now = ControlStore.Now;
        Assert.True(await app.Store.CoordinationSupervisionTick(now));
        Assert.All(await app.Store.Coordinations(), run => Assert.Equal(now, run.LastSupervisorAt));
        Assert.False(await app.Store.CoordinationSupervisionTick(now + 29999));
        // A current first row must not hide a later overdue heartbeat.
        await app.Store.Write(async db =>
        {
            (await db.CoordinationRuns.FindAsync(second.Id))!.LastSupervisorAt = 0;
            return true;
        });
        Assert.True(await app.Store.CoordinationSupervisionTick(now + 1));
        Assert.Equal("Paused", (await app.Store.Coordinations()).Single(x => x.Id == first.Id).State);
        Assert.Equal(now + 1, (await app.Store.Coordinations()).Single(x => x.Id == second.Id).LastSupervisorAt);
    }

    private static Task<WorkerRecord> AddCoordinator(ControlStore store, string runtimeId) => store.Write(db =>
    {
        var coordinator = new WorkerRecord
        {
            RuntimeId = runtimeId,
            Name = "Second control session",
            NativeSessionId = "ses_control_" + Guid.NewGuid().ToString("N"),
            Directory = "/home/agent/workspaces/control",
            Role = SessionRoles.Coordinator,
            Activity = "Idle",
            Stale = false,
            LastObservedAt = ControlStore.Now
        };
        db.Workers.Add(coordinator);
        return Task.FromResult(coordinator);
    });

    private static Task<bool> CorruptCoordinationContext(ControlStore store, string id) => store.Write(async db =>
    {
        var run = (await db.CoordinationRuns.FindAsync(id))!;
        run.InputJson = "{bad checkpoint";
        run.LastDecisionAt = 1; // Ensure the failing run is attempted before its healthy peer.
        return true;
    });
}
