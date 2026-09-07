using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed partial class CoordinationTests
{
    [Fact]
    public async Task HostedHeartbeatReopensBudgetAfterRestartWithoutOwnerOrModelRenewal()
    {
        string data, secrets, runId, assignmentId;
        await using (var app = new TestApp())
        {
            data = app.DataPath; secrets = app.SecretPath;
            var (coordinator, a, _) = await Seed(app.Store);
            var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Keep working", [a.Id], MaxRounds: 1, ContinuousSupervision: true, TurnWindowMinutes: 1));
            runId = run.Id;
            await app.Store.CoordinationTick();
            await FinishDecision(app.Store, run.Id, new("Assignment", [new("send_prompt", a.Id, "Do it once")]));
            await app.Store.CoordinationTick();
            assignmentId = (await app.Store.Snapshot()).Commands.Single(x => x.Origin == "coordinator:" + run.Id).Id;
            await app.Store.CoordinationTick();
            Assert.Equal("WaitingBudget", (await app.Store.Coordinations()).Single().State);
            await app.Store.CoordinationSupervisionTick();
            Assert.True((await app.Store.Coordinations()).Single().LastSupervisorAt > 0);
            Assert.False(await app.Store.CoordinationTick());
            // Persist a due deadline, then prove the real hosted timer discovers it after restart.
            await app.Store.Write(async db => { (await db.CoordinationRuns.FindAsync(run.Id))!.BudgetWindowEndsAt = ControlStore.Now - 1; return true; });
        }
        await using var restarted = new TestApp(data, secrets);
        using var service = new CoordinatorService(restarted.Store, NullLogger<CoordinatorService>.Instance);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await TestApp.Wait(async () => (await restarted.Store.Coordinations()).Single().MaxRounds == 2, "C# heartbeat opens budget", 10);
            Assert.Single((await restarted.Store.Snapshot()).Commands, x => x.Origin == "coordinator-decision:" + runId);
            await ObserveIdle(restarted.Store);
            await TestApp.Wait(async () => (await restarted.Store.Coordinations()).Single().Round == 2, "fresh observation dispatch", 10);
            var saved = (await restarted.Store.Coordinations()).Single();
            Assert.True(saved.BudgetWindowEndsAt > ControlStore.Now);
            Assert.Contains(Json.Read<CoordinatorContext>(saved.InputJson).Results, x => x.Id == assignmentId);
            Assert.Single((await restarted.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + runId);
            Assert.DoesNotContain((await restarted.Store.Snapshot()).Commands, x => x.Kind == "CoordinationRenewal");
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ModelCompletionCannotStopSupervisionAndIdleTimerRechecksCapacity()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Watch backlog", [a.Id], ContinuousSupervision: true));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("No current work", [], true));
        await app.Store.CoordinationTick();
        Assert.Equal("Waiting", (await app.Store.Coordinations()).Single().State);
        Assert.False(await app.Store.CoordinationTick()); // no tight model loop
        await IdleDeadlineDue(app.Store, run.Id);
        await app.Store.CoordinationTick();
        var saved = (await app.Store.Coordinations()).Single();
        Assert.Equal(2, saved.Round);
        Assert.NotNull(Json.Read<CoordinatorContext>(saved.InputJson).ReassessmentReason);
    }

    [Fact]
    public async Task HeartbeatHonorsOwnerPauseAndStopAndDoesNotAccumulateMissedAllowances()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Watch", [a.Id], MaxRounds: 2, ContinuousSupervision: true, TurnWindowMinutes: 1));
        run = await app.Store.ControlCoordination(run.Id, new(run.Revision, "pause"));
        var later = ControlStore.Now + 24 * 60 * 60000L;
        await app.Store.CoordinationSupervisionTick(later);
        var saved = (await app.Store.Coordinations()).Single();
        Assert.Equal("Paused", saved.State);
        Assert.Equal(2, saved.MaxRounds);
        Assert.Equal(later, saved.LastSupervisorAt); // service still observes an intentional pause
        saved = await app.Store.ControlCoordination(run.Id, new(saved.Revision, "resume"));
        await app.Store.CoordinationSupervisionTick(later);
        saved = (await app.Store.Coordinations()).Single();
        Assert.Equal(2, saved.MaxRounds); // one window, not 1440 grants
        saved = await app.Store.ControlCoordination(run.Id, new(saved.Revision, "stop"));
        Assert.False(await app.Store.CoordinationSupervisionTick(later + 60000));
        Assert.Equal("Stopped", (await app.Store.Coordinations()).Single().State);
        Assert.Empty((await app.Store.Snapshot()).Commands);
    }
}
