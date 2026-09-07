using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class CoordinationTests
{
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
    public async Task StaleBatchHasNoPartialDispatchAndPausedDecisionCannotDispatch()
    {
        await using var app = new TestApp();
        var (coordinator, a, b) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Ask both agents for memory usage.", [a.Id, b.Id]));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Ask", [new("send_prompt", a.Id, "Memory?"), new("send_prompt", b.Id, "Memory?")]));
        await app.Store.Write(async db => { (await db.Workers.FindAsync(b.Id))!.Revision++; return true; });
        await app.Store.CoordinationTick();
        Assert.Equal("Paused", (await app.Store.Coordinations()).Single().State);
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
        Assert.Equal("Paused", (await app.Store.Coordinations()).Single().State);
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
        var paused = (await app.Store.Coordinations()).Single();
        await app.Store.ControlCoordination(run.Id, new(paused.Revision, "resume"));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Wrong target", [new("send_prompt", "not-in-this-run", "Do work")]));
        await app.Store.CoordinationTick();
        Assert.Equal("Paused", (await app.Store.Coordinations()).Single().State);
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
    }

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
