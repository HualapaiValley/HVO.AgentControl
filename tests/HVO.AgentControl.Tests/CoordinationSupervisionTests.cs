using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed partial class CoordinationTests
{
    [Fact]
    public async Task HostedHeartbeatContinuesPastOldTurnLimitAfterRestartWithoutOwnerRenewalOrReplay()
    {
        string data, secrets, runId, assignmentId;
        await using (var app = new TestApp())
        {
            data = app.DataPath; secrets = app.SecretPath;
            var (coordinator, a, b) = await Seed(app.Store);
            var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Keep working", [a.Id, b.Id], MaxRounds: 1, ContinuousSupervision: true));
            runId = run.Id;
            await app.Store.CoordinationTick();
            await FinishDecision(app.Store, run.Id, new("Assignment", [new("send_prompt", a.Id, "Do it once")]));
            await app.Store.CoordinationTick();
            assignmentId = (await app.Store.Snapshot()).Commands.Single(x => x.Origin == "coordinator:" + run.Id).Id;
            Assert.False(await app.Store.CoordinationTick()); // existing work still active, no tight model loop
            Assert.Equal("Waiting", (await app.Store.Coordinations()).Single().State);
            await IdleDeadlineDue(app.Store, run.Id);
        }
        await using var restarted = new TestApp(data, secrets);
        using var service = new CoordinatorService(restarted.Store, NullLogger<CoordinatorService>.Instance);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await TestApp.Wait(async () => (await restarted.Store.Coordinations()).Single().LastSupervisorAt > 0, "service heartbeat after restart", 10);
            Assert.Single((await restarted.Store.Snapshot()).Commands, x => x.Origin == "coordinator-decision:" + runId);
            await ObserveIdle(restarted.Store);
            await TestApp.Wait(async () => (await restarted.Store.Coordinations()).Single().Round == 2, "automatic planning beyond old turn cap", 10);
            var saved = (await restarted.Store.Coordinations()).Single();
            Assert.Equal(1, saved.MaxRounds); // no fabricated renewal grant
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
        Assert.True(await app.Store.CoordinationTick()); // one bounded service review of unused capacity
        await FinishDecision(app.Store, run.Id, new("Full authorized scope assessed; no work", [], true));
        await app.Store.CoordinationTick();
        Assert.False(await app.Store.CoordinationTick());
        await IdleDeadlineDue(app.Store, run.Id);
        await app.Store.CoordinationTick();
        Assert.Equal(3, (await app.Store.Coordinations()).Single().Round);
    }

    [Fact]
    public async Task HeartbeatHonorsOwnerPauseAndStop()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Watch", [a.Id], ContinuousSupervision: true));
        run = await app.Store.ControlCoordination(run.Id, new(run.Revision, "pause"));
        await app.Store.CoordinationSupervisionTick();
        Assert.True((await app.Store.Coordinations()).Single().LastSupervisorAt > 0);
        Assert.False(await app.Store.CoordinationTick());
        run = (await app.Store.Coordinations()).Single();
        Assert.Equal("Paused", run.State);
        run = await app.Store.ControlCoordination(run.Id, new(run.Revision, "stop"));
        Assert.False(await app.Store.CoordinationSupervisionTick(ControlStore.Now + 60000));
        Assert.Empty((await app.Store.Snapshot()).Commands);
    }

    [Fact]
    public async Task NewlyViableSlotTriggersImmediatePlanningWithoutWaitingForQuietTimer()
    {
        await using var app = new TestApp();
        var (coordinator, a, b) = await Seed(app.Store);
        var priorWork = await app.Store.Prompt(a.Id, new(Guid.NewGuid().ToString(), "Earlier external assignment", a.Revision));
        await app.Store.Write(async db =>
        {
            var command = (await db.Commands.FindAsync(priorWork.Id))!;
            command.CreatedAt = ControlStore.Now - 60000; command.State = Delivery.Running;
            (await db.Workers.FindAsync(a.Id))!.Activity = "Active";
            return true;
        });
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Watch capacity", [a.Id, b.Id], ContinuousSupervision: true));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("B is available; A is working", []));
        await app.Store.CoordinationTick();
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("No independent authorized task currently", []));
        await app.Store.CoordinationTick();
        Assert.False(await app.Store.CoordinationTick());
        await Finish(app.Store, priorWork.Id, "Earlier task finished");
        await ObserveIdle(app.Store);
        await app.Store.CoordinationTick();
        var saved = (await app.Store.Coordinations()).Single();
        Assert.Equal(3, saved.Round);
        var context = Json.Read<CoordinatorContext>(saved.InputJson);
        Assert.Contains(a.Id, context.AvailableWorkerIds!);
        Assert.Contains("slot opened", context.ReassessmentReason);
    }

    [Fact]
    public async Task UncertainWorkerDoesNotStopViablePeerAndIsNeverReplayed()
    {
        await using var app = new TestApp();
        var (coordinator, a, b) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Independent tasks", [a.Id, b.Id], ContinuousSupervision: true));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Assign A", [new("send_prompt", a.Id, "Once")]));
        await app.Store.CoordinationTick();
        var assignment = (await app.Store.Snapshot()).Commands.Single(x => x.Origin == "coordinator:" + run.Id);
        await app.Store.Write(async db => { (await db.Commands.FindAsync(assignment.Id))!.State = Delivery.Unknown; return true; });
        await IdleDeadlineDue(app.Store, run.Id);
        await app.Store.CoordinationTick();
        var context = Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson);
        Assert.DoesNotContain(a.Id, context.AvailableWorkerIds!);
        Assert.Contains(b.Id, context.AvailableWorkerIds!);
        await FinishDecision(app.Store, run.Id, new("Use viable B", [new("send_prompt", b.Id, "Independent task")]));
        await app.Store.CoordinationTick();
        var commands = (await app.Store.Snapshot()).Commands.Where(x => x.Origin == "coordinator:" + run.Id).ToArray();
        Assert.Equal(2, commands.Length);
        Assert.Equal(Delivery.Unknown, commands.Single(x => x.WorkerId == a.Id).State);
    }
}
