using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class CoordinationCapacityTests
{
    [Theory]
    [InlineData(Delivery.Finished)]
    [InlineData(Delivery.Failed)]
    [InlineData(Delivery.Cancelled)]
    public async Task TerminalAssignmentWakesCoordinatorWhileAnotherWorkerRemainsBusy(string terminalState)
    {
        await using var app = new TestApp();
        var coordinator = await PersistenceTests.SeedWorker(app.Store);
        var a = new WorkerRecord { RuntimeId = coordinator.RuntimeId, Name = "A", NativeSessionId = "ses_capacity_a", Directory = "/a", Activity = "Idle", Stale = false };
        var b = new WorkerRecord { RuntimeId = coordinator.RuntimeId, Name = "B", NativeSessionId = "ses_capacity_b", Directory = "/b", Activity = "Idle", Stale = false };
        await app.Store.Write(async db =>
        {
            var saved = (await db.Workers.FindAsync(coordinator.Id))!;
            saved.Role = SessionRoles.Coordinator; saved.Activity = "Idle"; saved.Stale = false;
            db.Workers.AddRange(a, b); return true;
        });
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Keep both workers occupied.", [a.Id, b.Id], IncludeGuidance: true, ProgressMinutes: 60));
        await app.Store.CoordinationTick();
        await Decision(app.Store, new("Initial fan-out", [new("send_prompt", a.Id, "Short task"), new("send_prompt", b.Id, "Long task")]));
        await app.Store.CoordinationTick();
        var assignments = (await app.Store.Snapshot()).Commands.Where(x => x.Origin == "coordinator:" + run.Id).ToArray();
        Assert.Equal(2, assignments.Length);
        await app.Store.Write(async db =>
        {
            foreach (var assignment in assignments)
            {
                (await db.Commands.FindAsync(assignment.Id))!.State = Delivery.Running;
                (await db.Workers.FindAsync(assignment.WorkerId))!.Activity = "Active";
            }
            return true;
        });
        Assert.False(await app.Store.CoordinationTick());
        await app.Store.Write(async db =>
        {
            (await db.CoordinationRuns.FindAsync(run.Id))!.LastDecisionAt = ControlStore.Now - 1000;
            var shortTask = (await db.Commands.FindAsync(assignments.Single(x => x.WorkerId == a.Id).Id))!;
            shortTask.State = terminalState; shortTask.UpdatedAt = ControlStore.Now;
            (await db.Workers.FindAsync(a.Id))!.Activity = "Idle";
            return true;
        });
        Assert.True(await app.Store.CoordinationTick());
        Assert.Equal("Deciding", (await app.Store.Coordinations()).Single().State);
        await Decision(app.Store, new("Use newly available worker", [new("send_prompt", a.Id, "Next task")]));
        await app.Store.CoordinationTick();
        var final = (await app.Store.Snapshot()).Commands.Where(x => x.Origin == "coordinator:" + run.Id).ToArray();
        Assert.Equal(3, final.Length);
        Assert.Single(final, x => x.WorkerId == b.Id);
        Assert.Equal(Delivery.Running, final.Single(x => x.WorkerId == b.Id).State);
        Assert.False(await app.Store.CoordinationTick());
    }

    private static async Task Decision(ControlStore store, CoordinatorDecision decision)
    {
        var run = (await store.Coordinations()).Single();
        await store.Write(async db =>
        {
            var command = (await db.Commands.FindAsync(run.DecisionCommandId))!;
            command.State = Delivery.Finished;
            command.ResultJson = Json.Write(new { messages = new[] { new { parts = new[] { new { type = "text", text = Json.Write(decision) } } } } });
            return true;
        });
    }
}
