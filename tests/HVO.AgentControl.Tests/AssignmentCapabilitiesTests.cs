using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Ssh;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class AssignmentCapabilitiesTests
{
    [Fact]
    public async Task GuidanceCapturesExactPromptAndSurvivesRestartAndRetry()
    {
        string data, secrets, workerId; PromptInput input; string rendered;
        await using (var app = new TestApp())
        {
            data = app.DataPath; secrets = app.SecretPath;
            var worker = await PersistenceTests.SeedWorker(app.Store); workerId = worker.Id;
            input = new(Guid.NewGuid().ToString(), "Review PR 1234. Reply with the comment reference.", worker.Revision, IncludeGuidance: true, ProgressMinutes: 5);
            var command = await app.Store.Prompt(worker.Id, input);
            rendered = Json.Read<PromptInput>(command.ExecutionPayload).Text;
            Assert.Contains("every 5 minutes", rendered); Assert.EndsWith(input.Text, rendered);
            var paths = AssignmentGuidance.ManagedPaths(worker.Directory, input.Id);
            Assert.Contains("Verified session directory: " + paths.SessionDirectory, rendered);
            Assert.Contains("Managed task directory: " + paths.TaskDirectory, rendered);
            Assert.Contains("Managed scratch directory: " + paths.ScratchDirectory, rendered);
            Assert.Contains("one-time approval", rendered);
            Assert.Contains("Do not select sibling worktrees, /tmp paths", rendered);
            Assert.Equal(input.Text, Json.Read<PromptInput>(command.Payload).Text);
            Assert.Equal(AssignmentGuidance.Version, (await app.Store.Detail(worker.Id)).Assignments.Single().TemplateVersion);
        }
        await using var restarted = new TestApp(data, secrets);
        Assert.Equal(rendered, Json.Read<PromptInput>((await restarted.Store.Prompt(workerId, input)).ExecutionPayload).Text);
        Assert.Single((await restarted.Store.Snapshot()).Commands, x => x.Kind == "Prompt");
        Assert.Throws<ControlException>(() => AssignmentGuidance.Render(input with { IncludeGuidance = false }, "/workspace"));
        Assert.Equal(input.Text, AssignmentGuidance.Render(input with { IncludeGuidance = false, ProgressMinutes = null }, "/workspace"));
    }

    [Theory]
    [InlineData("/work/session", "/work/session")]
    [InlineData("/work//session/", "/work/session")]
    public void ManagedTaskPathsStayWithinCanonicalSessionDirectory(string session, string expected)
    {
        const string assignmentId = "65af8853-b47a-48e9-9a06-70273c905ece";
        var paths = AssignmentGuidance.ManagedPaths(session, assignmentId);
        Assert.Equal(expected, paths.SessionDirectory);
        Assert.Equal(expected + "/.agentcontrol/tasks/65af8853b47a48e99a0670273c905ece", paths.TaskDirectory);
        Assert.Equal(expected + "/.agentcontrol/scratch/65af8853b47a48e99a0670273c905ece", paths.ScratchDirectory);
        Assert.DoesNotContain("/work/sibling", paths.TaskDirectory);
        Assert.DoesNotContain("/tmp/", paths.ScratchDirectory);
    }

    [Theory]
    [InlineData("relative/session")]
    [InlineData("/work/session/../sibling")]
    [InlineData("/work/./session")]
    [InlineData("/work/session\0outside")]
    public void ManagedTaskPathsRejectUnverifiedOrTraversingSessionDirectories(string session) =>
        Assert.Throws<ControlException>(() => AssignmentGuidance.ManagedPaths(session, Guid.NewGuid().ToString()));

    [Fact]
    public async Task CapabilityInquiryDeduplicatesAndKeepsActiveWorkIndependent()
    {
        await using var app = new TestApp(); var worker = await PersistenceTests.SeedWorker(app.Store);
        await app.Store.Write(async db => { (await db.Workers.FindAsync(worker.Id))!.Activity = "Active"; return true; });
        var request = new RequestId(Guid.NewGuid().ToString());
        var first = await app.Store.DiscoverCapabilities(worker.Id, request);
        var second = await app.Store.DiscoverCapabilities(worker.Id, new(Guid.NewGuid().ToString()));
        Assert.Equal(first.Id, second.Id); Assert.Equal(Delivery.Queued, first.State);
        await app.Store.Write(async db => { (await db.Commands.FindAsync(first.Id))!.State = Delivery.Finished; return true; });
        Assert.Equal(first.Id, (await app.Store.DiscoverCapabilities(worker.Id, request)).Id);
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Kind == "Abort");
        Assert.Contains("signing", Json.Read<PromptInput>(first.Payload).Text);
    }

    [Fact]
    public async Task CoordinatorRoleRejectsOrdinaryTasksAndEitherKindOfInvalidParticipant()
    {
        await using var app = new TestApp(); var coordinator = await PersistenceTests.SeedWorker(app.Store);
        await app.Store.Write(async db =>
        {
            var saved = (await db.Workers.FindAsync(coordinator.Id))!;
            saved.Stale = false; saved.Activity = "Idle"; saved.LastObservedAt = ControlStore.Now;
            return true;
        });
        await app.Store.ReserveCoordinator(coordinator.Id, new(Guid.NewGuid().ToString(), coordinator.SettingsRevision));
        var worker = new WorkerRecord { RuntimeId = coordinator.RuntimeId, Name = "Worker", NativeSessionId = "ses_worker", Directory = "/other", Activity = "Idle", Stale = false };
        var another = new WorkerRecord { RuntimeId = coordinator.RuntimeId, Name = "Other coordinator", NativeSessionId = "ses_other", Directory = "/coordinator2", Role = SessionRoles.Coordinator };
        await app.Store.Write(db => { db.Workers.AddRange(worker, another); return Task.FromResult(true); });
        await Assert.ThrowsAsync<ControlException>(() => app.Store.Prompt(coordinator.Id, new(Guid.NewGuid().ToString(), "Do work", 1)));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.StartCoordination(new(Guid.NewGuid().ToString(), worker.Id, "Do work", [another.Id])));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Do work", [another.Id])));
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Do work", [worker.Id]));
        await app.Store.CoordinationTick();
        run = (await app.Store.Coordinations()).Single();
        await app.Store.Write(async db =>
        {
            (await db.Workers.FindAsync(worker.Id))!.Role = SessionRoles.Coordinator;
            var decision = (await db.Commands.FindAsync(run.DecisionCommandId))!;
            decision.State = Delivery.Finished;
            decision.ResultJson = Json.Write(new { messages = new[] { new { parts = new[] { new { type = "text", text = Json.Write(new CoordinatorDecision("Assign", [new("send_prompt", worker.Id, "Do work")])) } } } } });
            return true;
        });
        await app.Store.CoordinationTick();
        Assert.Equal("Recovering", (await app.Store.Coordinations()).Single().State);
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
    }

    [Fact]
    public void ProbePreservesScopeLimitsAndUnknownsWithoutClaimingAccess()
    {
        var probe = CapabilityProbe.Parse("logicalCores\t64\ncpuQuotaV2\t200000 100000\nmemoryLimitV2\t2147483648\ndockerDaemonAccess\t\n", "/workspace");
        Assert.Equal("64", probe.Facts["logicalCores"]);
        Assert.Equal("200000 100000", probe.Facts["cpuQuotaV2"]);
        Assert.Equal("unknown", probe.Facts["dockerDaemonAccess"]);
        Assert.Equal("/workspace", probe.Scope); Assert.Equal("probe", probe.Source);
    }
}
