using System.Net;
using System.Net.Http.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed partial class CoordinationTests
{
    [Fact]
    public async Task LegacyExhaustedCheckpointCanEnableContinuousModeWithoutInventingAnotherTurnGrant()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Bounded task", [a.Id], MaxRounds: 1));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Waiting", []));
        await app.Store.CoordinationTick();
        await app.Store.CoordinationTick();
        run = (await app.Store.Coordinations()).Single();
        run = await app.Store.RenewCoordination(run.Id, new(Guid.NewGuid().ToString(), run.Revision, 0, "Keep supervising backlog", true));
        Assert.Equal(1, run.MaxRounds);
        await app.Store.CoordinationTick();
        Assert.Equal(2, (await app.Store.Coordinations()).Single().Round);
    }

    [Fact]
    public async Task RenewalPreservesAssignmentsAndDecisionReceiptAcrossRestartWithoutReplayingWork()
    {
        string data, secrets, runId, assignmentId, sessionId;
        CoordinationRenewalInput input;
        await using (var app = new TestApp())
        {
            data = app.DataPath; secrets = app.SecretPath;
            var (coordinator, a, b) = await Seed(app.Store);
            sessionId = coordinator.NativeSessionId!;
            var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, new string('x', 15990), [a.Id, b.Id], MaxRounds: 1));
            runId = run.Id;
            await app.Store.CoordinationTick();
            await FinishDecision(app.Store, run.Id, new("A is implementing", [new("send_prompt", a.Id, "Implement once")]));
            await app.Store.CoordinationTick();
            assignmentId = (await app.Store.Snapshot()).Commands.Single(x => x.Origin == "coordinator:" + run.Id).Id;
            await app.Store.CoordinationTick();
            run = (await app.Store.Coordinations()).Single();
            Assert.Equal("Paused", run.State);
            await Assert.ThrowsAsync<ControlException>(() => app.Store.ControlCoordination(run.Id, new(run.Revision, "resume")));
            input = new(Guid.NewGuid().ToString(), run.Revision, 3, "Keep A's existing assignment; let B review after completion.");
            run = await app.Store.RenewCoordination(run.Id, input);
            Assert.Equal(1, run.Round);
            Assert.Equal(4, run.MaxRounds);
            Assert.Equal("Ready", run.State);
            Assert.Equal(input.Instruction, run.Instruction);
        }
        await using var restarted = new TestApp(data, secrets);
        var renewed = await restarted.Store.RenewCoordination(runId, input); // network retry after restart
        Assert.Equal(4, renewed.MaxRounds);
        Assert.False(await restarted.Store.CoordinationTick()); // observation is required after restart
        await ObserveIdle(restarted.Store);
        await restarted.Store.CoordinationTick();
        var observed = Json.Read<CoordinatorContext>((await restarted.Store.Coordinations()).Single().InputJson);
        Assert.Contains(observed.Results, x => x.Id == assignmentId && x.State == Delivery.Queued);
        Assert.Equal(assignmentId, Assert.Single(observed.LastAppliedDecision!.Dispatched).CommandId);
        var snapshot = await restarted.Store.Snapshot();
        Assert.Single(snapshot.Commands, x => x.Origin == "coordinator:" + runId);
        Assert.Single(snapshot.Commands, x => x.Kind == "CoordinationRenewal");
        Assert.Equal(sessionId, snapshot.Workers.Single(x => x.Role == SessionRoles.Coordinator).NativeSessionId);
        Assert.Equal(1, await restarted.Store.Read(db => db.Events.CountAsync(x => x.Type == "CoordinationRenewed")));
        await Assert.ThrowsAsync<ControlException>(() => restarted.Store.RenewCoordination(runId, input with { Instruction = "Changed retry" }));
    }

    [Fact]
    public async Task RenewalRequiresOwnerRevisionAndBoundedGrantAndDoesNotReopenTerminalRuns()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Task", [a.Id], MaxRounds: 20));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.RenewCoordination(run.Id, new(Guid.NewGuid().ToString(), run.Revision, 1, "Policy")));
        run = await app.Store.ControlCoordination(run.Id, new(run.Revision, "pause"));
        foreach (var grant in new[] { -1, 0, 81, 101, int.MaxValue })
            await Assert.ThrowsAsync<ControlException>(() => app.Store.RenewCoordination(run.Id, new(Guid.NewGuid().ToString(), run.Revision, grant, "Policy")));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.RenewCoordination(run.Id, new(Guid.NewGuid().ToString(), run.Revision - 1, 1, "Policy")));
        foreach (var policy in new[] { "", " ", new string('x', 16001) })
            await Assert.ThrowsAsync<ControlException>(() => app.Store.RenewCoordination(run.Id, new(Guid.NewGuid().ToString(), run.Revision, 1, policy)));
        Assert.Equal("Paused", (await app.Store.Coordinations()).Single().State);
        Assert.Empty((await app.Store.Snapshot()).Commands);
        run = await app.Store.ControlCoordination(run.Id, new(run.Revision, "stop"));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.RenewCoordination(run.Id, new(Guid.NewGuid().ToString(), run.Revision, 1, "Policy")));
    }

    [Theory]
    [InlineData(Delivery.Running)]
    [InlineData(Delivery.Unknown)]
    [InlineData(Delivery.Cancelled)]
    public async Task RenewalNeverDiscardsAnUnresolvedCoordinatorDecision(string state)
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Task", [a.Id], MaxRounds: 1));
        await app.Store.CoordinationTick();
        run = (await app.Store.Coordinations()).Single();
        await app.Store.Write(async db => { (await db.Commands.FindAsync(run.DecisionCommandId))!.State = state; return true; });
        run = await app.Store.ControlCoordination(run.Id, new(run.Revision, "pause"));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.RenewCoordination(run.Id, new(Guid.NewGuid().ToString(), run.Revision, 1, "Policy")));
        var saved = (await app.Store.Coordinations()).Single();
        Assert.Equal(run.DecisionCommandId, saved.DecisionCommandId);
        Assert.Equal(1, saved.MaxRounds);
        Assert.Equal(state, (await app.Store.Snapshot()).Commands.Single().State);
    }

    [Fact]
    public async Task OwnerAssignmentAndItsCompletionAppearInCoordinatorEvidenceWithoutChangingProvenance()
    {
        await using var app = new TestApp();
        var (coordinator, a, b) = await Seed(app.Store);
        var old = await app.Store.Prompt(a.Id, new(Guid.NewGuid().ToString(), "Earlier task", a.Revision));
        await Finish(app.Store, old.Id, "Previous run evidence");
        await app.Store.Write(async db => { (await db.Commands.FindAsync(old.Id))!.CreatedAt = ControlStore.Now - 60000; return true; });
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Track owner work", [a.Id]));
        a = (await app.Store.Snapshot()).Workers.Single(x => x.Id == a.Id);
        var ownerWork = await app.Store.Prompt(a.Id, new(Guid.NewGuid().ToString(), "Root-assigned correction", a.Revision));
        var outsider = await app.Store.Prompt(b.Id, new(Guid.NewGuid().ToString(), "Other project", b.Revision));
        await app.Store.CoordinationTick();
        var evidence = Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson);
        Assert.Equal("owner", Assert.Single(evidence.Results).Origin);
        Assert.Equal(ownerWork.Id, evidence.Results[0].Id);
        await FinishDecision(app.Store, run.Id, new("Wait for owner assignment", []));
        await app.Store.CoordinationTick();
        await Finish(app.Store, ownerWork.Id, "Correction complete at SHA123");
        await app.Store.CoordinationTick();
        evidence = Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson);
        Assert.Equal("Correction complete at SHA123", Assert.Single(evidence.Results).Response);
        Assert.Equal("owner", (await app.Store.Snapshot()).Commands.Single(x => x.Id == ownerWork.Id).Origin);
        Assert.DoesNotContain(evidence.Results, x => x.Id == old.Id || x.Id == outsider.Id);
    }

    [Fact]
    public async Task RenewalApiRequiresAuthenticationAndAntiforgery()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Task", [a.Id]));
        run = await app.Store.ControlCoordination(run.Id, new(run.Revision, "pause"));
        var path = "/api/v1/coordinations/" + run.Id + "/renew";
        var input = new CoordinationRenewalInput(Guid.NewGuid().ToString(), run.Revision, 10, "Continue existing work");
        using var anonymous = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(path, input)).StatusCode);
        using var owner = await app.SignIn();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(path, input)).StatusCode);
        owner.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(path, input)).StatusCode);
    }
}
