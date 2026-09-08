using HVO.AgentControl.Core;
using HVO.AgentControl.GitHub;
using HVO.AgentControl.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed partial class CoordinationTests
{
    [Fact]
    public async Task IdlePlanningReviewIsImmediateBoundedAndDurableAcrossRestart()
    {
        string data, secrets, runId, originalDecision;
        await using (var app = new TestApp())
        {
            data = app.DataPath; secrets = app.SecretPath;
            var (coordinator, a, _) = await Seed(app.Store);
            runId = (await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Full authorized backlog", [a.Id], ContinuousSupervision: true))).Id;
            await app.Store.CoordinationTick();
            originalDecision = (await app.Store.Coordinations()).Single().DecisionCommandId!;
            await FinishDecision(app.Store, runId, new("One dependency is blocked", []));
            await app.Store.CoordinationTick();
            Assert.True(await app.Store.CoordinationTick());
            var review = (await app.Store.Coordinations()).Single();
            var context = Json.Read<CoordinatorContext>(review.InputJson);
            Assert.Equal(2, review.Round);
            Assert.Equal(originalDecision, context.IdleReview!.TriggerCommandId);
            Assert.Contains("full owner-authorized scope", context.ReassessmentReason);
            Assert.Contains(a.Id, context.AvailableWorkerIds!);
            await FinishDecision(app.Store, runId, new("Whole scope verified; no eligible work", []));
            await app.Store.CoordinationTick();
            Assert.False(await app.Store.CoordinationTick());
        }
        await using var restarted = new TestApp(data, secrets);
        await ObserveIdle(restarted.Store);
        Assert.False(await restarted.Store.CoordinationTick());
        await IdleDeadlineDue(restarted.Store, runId);
        Assert.True(await restarted.Store.CoordinationTick()); // ordinary quiet reassessment remains
        await FinishDecision(restarted.Store, runId, new("Still no work", []));
        await restarted.Store.CoordinationTick();
        Assert.False(await restarted.Store.CoordinationTick());
        Assert.Equal(1, await restarted.Store.Read(db => db.Events.CountAsync(x => x.Type == "CoordinatorIdlePlanningReviewRequested")));
        Assert.DoesNotContain((await restarted.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + runId);
    }

    [Fact]
    public async Task ProgressTokensDoNotRepeatReviewOrReplayOutstandingWork()
    {
        await using var app = new TestApp();
        var (coordinator, a, b) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Independent work", [a.Id, b.Id], ContinuousSupervision: true));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("A starts", [new("send_prompt", a.Id, "Long task")]));
        await app.Store.CoordinationTick();
        var assignment = (await app.Store.Snapshot()).Commands.Single(x => x.Origin == "coordinator:" + run.Id);
        await app.Store.Write(async db =>
        {
            (await db.Commands.FindAsync(assignment.Id))!.State = Delivery.Running;
            (await db.Workers.FindAsync(a.Id))!.Activity = "Active";
            return true;
        });
        await IdleDeadlineDue(app.Store, run.Id);
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Wait on A", []));
        await app.Store.CoordinationTick();
        Assert.True(await app.Store.CoordinationTick());
        var context = Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson);
        Assert.Equal([b.Id], context.AvailableWorkerIds!);
        Assert.Equal(Delivery.Running, context.Results.Single(x => x.Id == assignment.Id).State);
        await FinishDecision(app.Store, run.Id, new("No independent work authorized", []));
        await app.Store.CoordinationTick();
        await app.Store.Write(async db =>
        {
            var command = (await db.Commands.FindAsync(assignment.Id))!;
            command.ProgressText = "Another model token"; command.LastProgressAt = ControlStore.Now;
            return true;
        });
        Assert.False(await app.Store.CoordinationTick());
        Assert.Single((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
        Assert.Equal(1, await app.Store.Read(db => db.Events.CountAsync(x => x.Type == "CoordinatorIdlePlanningReviewRequested")));
    }

    [Fact]
    public async Task CiPermissionEvidenceIsExplicitAndRealChangesWakePlanning()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        await app.Store.Write(db =>
        {
            db.GitHubAccess.Add(new GitHubAccess
            {
                Id = a.RuntimeId,
                State = "Ready",
                PrivateKeyReference = "never-in-model-context",
                ChecksPermission = "Denied",
                CommitStatusesPermission = "Denied",
                ActionsPermission = "Denied",
                PermissionsVerifiedAt = ControlStore.Now
            });
            return Task.FromResult(true);
        });
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Inspect CI", [a.Id], ContinuousSupervision: true));
        await app.Store.CoordinationTick();
        var context = Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson);
        Assert.Equal("PermissionDenied", Assert.Single(context.GitHubAccess!).CiInspectionState);
        Assert.DoesNotContain("never-in-model-context", Json.Write(context));
        await FinishDecision(app.Store, run.Id, new("Access denied", []));
        await app.Store.CoordinationTick();
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("No independent work; need permission", []));
        await app.Store.CoordinationTick();
        await app.Store.Write(async db =>
        {
            (await db.GitHubAccess.FindAsync(a.RuntimeId))!.PermissionsVerifiedAt = ControlStore.Now + 1000;
            return true;
        });
        Assert.False(await app.Store.CoordinationTick()); // fresh timestamp alone is not new readiness
        await app.Store.Write(async db =>
        {
            var grant = (await db.GitHubAccess.FindAsync(a.RuntimeId))!;
            grant.ChecksPermission = grant.CommitStatusesPermission = grant.ActionsPermission = "Granted";
            return true;
        });
        Assert.True(await app.Store.CoordinationTick());
        context = Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson);
        Assert.Equal("Ready", Assert.Single(context.GitHubAccess!).CiInspectionState);
        Assert.Equal(3, (await app.Store.Coordinations()).Single().Round);
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("paused")]
    [InlineData("bounded")]
    public async Task IdleReviewDoesNotOverrideCapacityOwnerPauseOrBoundedTasks(string condition)
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Task", [a.Id], ContinuousSupervision: condition != "bounded"));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Wait", []));
        await app.Store.CoordinationTick();
        if (condition == "paused")
            await app.Store.ControlCoordination(run.Id, new((await app.Store.Coordinations()).Single().Revision, "pause"));
        if (condition == "busy")
            await app.Store.Write(async db => { (await db.Workers.FindAsync(a.Id))!.Activity = "Active"; return true; });
        Assert.False(await app.Store.CoordinationTick());
        Assert.Equal(1, (await app.Store.Coordinations()).Single().Round);
        Assert.Equal(0, await app.Store.Read(db => db.Events.CountAsync(x => x.Type == "CoordinatorIdlePlanningReviewRequested")));
    }
}
