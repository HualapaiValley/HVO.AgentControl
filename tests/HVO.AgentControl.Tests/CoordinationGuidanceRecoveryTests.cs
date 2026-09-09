using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed partial class CoordinationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task ConflictingGuidanceRejectsWholeBatchAndFreshCorrectionDispatchesOnce(bool? includeGuidance)
    {
        await using var app = new TestApp();
        var (coordinator, a, b) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Assign two independent tasks", [a.Id, b.Id]));
        await app.Store.CoordinationTick();
        run = (await app.Store.Coordinations()).Single();
        var rejectedCommandId = run.DecisionCommandId!;
        var oldRevision = Json.Read<CoordinatorContext>(run.InputJson).Workers.Single(x => x.Id == b.Id).Revision;
        await FinishDecision(app.Store, run.Id, new("Assign both", [
            new("send_prompt", a.Id, "Do the first task", IncludeGuidance: true, ProgressMinutes: 10),
            new("send_prompt", b.Id, "Do the second task", IncludeGuidance: includeGuidance, ProgressMinutes: 10)]));
        var rejectedResult = (await app.Store.Command(rejectedCommandId)).ResultJson;

        await app.Store.CoordinationTick();
        var rejected = (await app.Store.Coordinations()).Single();
        Assert.Equal("Recovering", rejected.State);
        var recovery = Json.Read<CoordinatorContext>(rejected.InputJson).Recovery!;
        Assert.Contains("actions[1].progressMinutes", recovery.Reason);
        Assert.Contains("includeGuidance is false", recovery.Reason);
        Assert.Contains("Omit progressMinutes or set it to null", recovery.Reason);
        Assert.Contains("includeGuidance:true", recovery.Reason);
        Assert.InRange(recovery.Reason.Length, 1, 600);
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
        Assert.False(await app.Store.CoordinationTick());

        // A normal owner metadata edit changes the observed revision while recovery waits.
        var current = (await app.Store.Snapshot()).Workers.Single(x => x.Id == b.Id);
        var updated = await app.Store.UpdateWorker(b.Id, new(Guid.NewGuid().ToString(), current.SettingsRevision,
            current.Name, current.Project, "Freshly verified task capability", current.ProviderId, current.ModelId, current.Agent, current.Variant));
        Assert.True(updated.Revision > oldRevision);
        await RecoveryDue(app.Store, run.Id);
        await app.Store.CoordinationTick();
        var fresh = (await app.Store.Coordinations()).Single();
        Assert.Equal("Deciding", fresh.State);
        Assert.NotEqual(rejectedCommandId, fresh.DecisionCommandId);
        var context = Json.Read<CoordinatorContext>(fresh.InputJson);
        Assert.Equal(updated.Revision, context.Workers.Single(x => x.Id == b.Id).Revision);
        Assert.Equal(updated.Description, context.Workers.Single(x => x.Id == b.Id).Description);
        Assert.Equal(recovery.Reason, context.Recovery!.Reason);
        var snapshot = await app.Store.Snapshot();
        var freshPrompt = (await app.Store.CommandPrompt(fresh.DecisionCommandId!)).Text;
        Assert.Contains("Never combine", freshPrompt);
        Assert.Contains("actions[1].progressMinutes", freshPrompt);
        Assert.Contains("Omit progressMinutes or set it to null", freshPrompt);
        Assert.Equal(Delivery.Finished, snapshot.Commands.Single(x => x.Id == rejectedCommandId).State);
        Assert.Equal(rejectedResult, (await app.Store.Command(rejectedCommandId)).ResultJson);

        await FinishDecision(app.Store, run.Id, new("Correct both using the fresh observation", [
            new("send_prompt", a.Id, "Do the first task", IncludeGuidance: true, ProgressMinutes: 10),
            new("send_prompt", b.Id, "Do the second task", IncludeGuidance: false)]));
        await app.Store.CoordinationTick();
        await app.Store.CoordinationTick();
        var dispatched = (await app.Store.Snapshot()).Commands.Where(x => x.Origin == "coordinator:" + run.Id).ToArray();
        Assert.Equal(2, dispatched.Length);
        Assert.Equal(10, (await app.Store.CommandPrompt(dispatched.Single(x => x.WorkerId == a.Id).Id)).ProgressMinutes);
        var corrected = await app.Store.CommandPrompt(dispatched.Single(x => x.WorkerId == b.Id).Id);
        Assert.False(corrected.IncludeGuidance);
        Assert.Null(corrected.ProgressMinutes);
        Assert.Equal(updated.Revision, corrected.ExpectedRevision);
        Assert.Null(Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson).Recovery);
        await app.Store.Write(async db =>
        {
            Assert.Single(await db.Events.Where(x => x.Type == "CoordinatorDecisionRejected" && x.CommandId == rejectedCommandId).ToListAsync());
            return true;
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public async Task InvalidGuidedIntervalKeepsBatchAtomicAndNamesFieldCorrection(int progressMinutes)
    {
        await using var app = new TestApp();
        var (coordinator, a, b) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Assign both", [a.Id, b.Id]));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Assign", [new("send_prompt", a.Id, "Valid task"),
            new("send_prompt", b.Id, "Invalid interval", IncludeGuidance: true, ProgressMinutes: progressMinutes)]));
        await app.Store.CoordinationTick();
        var rejected = (await app.Store.Coordinations()).Single();
        Assert.Equal("Recovering", rejected.State);
        Assert.Contains("actions[1].progressMinutes", rejected.Detail);
        Assert.Contains("integer from 1–1440", rejected.Detail);
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
    }

    [Theory]
    [InlineData(false, false, null, true, false, null)]
    [InlineData(false, false, null, false, false, null)]
    [InlineData(true, false, null, false, false, null)]
    [InlineData(true, null, null, true, true, 30)]
    [InlineData(true, true, null, false, true, 30)]
    [InlineData(false, true, null, false, true, null)]
    [InlineData(false, true, 1, false, true, 1)]
    [InlineData(false, true, 1440, false, true, 1440)]
    public async Task ValidGuidancePairsPreserveOmissionNullInheritanceAndIntervalBounds(bool runGuidance,
        bool? includeGuidance, int? progressMinutes, bool omitProgress, bool expectedGuidance, int? expectedMinutes)
    {
        await using var app = new TestApp();
        var (coordinator, worker, _) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "One task", [worker.Id],
            IncludeGuidance: runGuidance, ProgressMinutes: runGuidance ? 30 : null));
        await app.Store.CoordinationTick();
        var action = new Dictionary<string, object?>
        {
            ["type"] = "send_prompt",
            ["workerId"] = worker.Id,
            ["text"] = "Perform the task",
            ["includeGuidance"] = includeGuidance,
            ["riskLevel"] = TaskRiskLevels.Low
        };
        if (!omitProgress) action["progressMinutes"] = progressMinutes;
        run = (await app.Store.Coordinations()).Single();
        await Finish(app.Store, run.DecisionCommandId!, Json.Write(new { summary = "Assign", actions = new[] { action } }));
        await app.Store.CoordinationTick();
        var dispatched = Assert.Single((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
        var input = await app.Store.CommandPrompt(dispatched.Id);
        Assert.Equal(expectedGuidance, input.IncludeGuidance);
        Assert.Equal(expectedMinutes, input.ProgressMinutes);
    }
}
