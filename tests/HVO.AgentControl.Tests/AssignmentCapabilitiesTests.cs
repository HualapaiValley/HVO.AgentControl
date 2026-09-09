using System.Reflection;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Services;
using HVO.AgentControl.Ssh;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
            input = new(Guid.NewGuid().ToString(), "Review PR 1234. Reply with the comment reference.", worker.Revision,
                IncludeGuidance: true, ProgressMinutes: 5, RiskLevel: TaskRiskLevels.Low);
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

    [Theory]
    [InlineData(Delivery.Finished)]
    [InlineData(Delivery.Cancelled)]
    public async Task CoalescedCapabilityReceiptSurvivesTerminalCompletionRestartAndReplay(string terminalState)
    {
        string data, secrets, workerId, firstId, coalescedId;
        await using (var app = new TestApp())
        {
            data = app.DataPath; secrets = app.SecretPath;
            var worker = await PersistenceTests.SeedWorker(app.Store); workerId = worker.Id;
            firstId = Guid.NewGuid().ToString(); coalescedId = Guid.NewGuid().ToString();

            var first = await app.Store.DiscoverCapabilities(worker.Id, new(firstId));
            var coalesced = await app.Store.DiscoverCapabilities(worker.Id, new(coalescedId));
            Assert.Equal(first.Id, coalesced.Id);
            Assert.Single((await app.Store.Snapshot()).Commands, x => x.Kind == "Prompt");

            await app.Store.Write(async db => { (await db.Commands.FindAsync(first.Id))!.State = Delivery.Unknown; return true; });
            Assert.Equal(first.Id, (await app.Store.DiscoverCapabilities(worker.Id, new(coalescedId))).Id);
            var other = await PersistenceTests.SeedWorker(app.Store);
            await Assert.ThrowsAsync<ControlException>(() => app.Store.DiscoverCapabilities(other.Id, new(coalescedId)));
            await app.Store.Write(async db => { (await db.Commands.FindAsync(first.Id))!.State = terminalState; return true; });
        }

        await using var restarted = new TestApp(data, secrets);
        var replayed = await restarted.Store.DiscoverCapabilities(workerId, new(coalescedId));
        var repeated = await restarted.Store.DiscoverCapabilities(workerId, new(coalescedId));

        Assert.Equal(firstId, replayed.Id);
        Assert.Equal(firstId, repeated.Id);
        Assert.Equal(terminalState, replayed.State);
        Assert.Single((await restarted.Store.Snapshot()).Commands, x => x.Kind == "Prompt");
        Assert.Single((await restarted.Store.Snapshot()).Commands, x => x.Id == coalescedId && x.Kind == "CapabilityInquiryAlias");
        Assert.Single(await restarted.Store.Read(db => db.Events.Where(x => x.Type == "CapabilityInquiryCoalesced" && x.CommandId == coalescedId).ToListAsync()));
    }

    [Fact]
    public async Task CoalescedCapabilityReceiptSurvivesEventRetentionBeforeReplay()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var first = await app.Store.DiscoverCapabilities(worker.Id, new(Guid.NewGuid().ToString()));
        string coalescedId;
        await app.Store.Write(async db =>
        {
            for (var index = 0; index < 110; index++) ControlStore.Event(db, "RetentionFixture", payload: new { index });
            return true;
        });
        coalescedId = Guid.NewGuid().ToString();
        await app.Store.DiscoverCapabilities(worker.Id, new(coalescedId));
        await app.Store.Write(async db => { (await db.Commands.FindAsync(first.Id))!.State = Delivery.Finished; return true; });
        var supervisor = new RuntimeSupervisor(app.Store, null!, Options.Create(new ControlOptions { EventRetention = 100 }), NullLogger<RuntimeSupervisor>.Instance);
        var retain = typeof(RuntimeSupervisor).GetMethod("Retain", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task<bool>)retain.Invoke(supervisor, null)!;

        var replayed = await app.Store.DiscoverCapabilities(worker.Id, new(coalescedId));

        Assert.Equal(first.Id, replayed.Id);
        Assert.Equal(Delivery.Finished, replayed.State);
        Assert.Single((await app.Store.Snapshot()).Commands, x => x.Kind == "Prompt");
        Assert.Single(await app.Store.Read(db => db.Events.Where(x => x.Type == "CapabilityInquiryCoalesced" && x.CommandId == coalescedId).ToListAsync()));
    }

    [Fact]
    public async Task EventRetentionPrunesStaleAliasesAcrossWorkersAndPreservesRecentReceiptAfterRestart()
    {
        string data, secrets, otherWorkerId, otherFirstId, recentAliasId, otherStaleAliasId;
        await using (var app = new TestApp())
        {
            data = app.DataPath; secrets = app.SecretPath;
            var worker = await PersistenceTests.SeedWorker(app.Store);
            var other = await PersistenceTests.SeedWorker(app.Store); otherWorkerId = other.Id;
            var first = await app.Store.DiscoverCapabilities(worker.Id, new(Guid.NewGuid().ToString()));
            var otherFirst = await app.Store.DiscoverCapabilities(other.Id, new(Guid.NewGuid().ToString())); otherFirstId = otherFirst.Id;
            Assert.Equal(first.Id, (await app.Store.DiscoverCapabilities(worker.Id, new(Guid.NewGuid().ToString()))).Id);
            otherStaleAliasId = Guid.NewGuid().ToString();
            Assert.Equal(otherFirst.Id, (await app.Store.DiscoverCapabilities(other.Id, new(otherStaleAliasId))).Id);
            await app.Store.Write(async db =>
            {
                for (var index = 0; index < 110; index++) ControlStore.Event(db, "RetentionFixture", payload: new { index });
                return true;
            });
            recentAliasId = Guid.NewGuid().ToString();
            Assert.Equal(otherFirst.Id, (await app.Store.DiscoverCapabilities(other.Id, new(recentAliasId))).Id);
            await app.Store.Write(async db =>
            {
                (await db.Commands.FindAsync(first.Id))!.State = Delivery.Finished;
                (await db.Commands.FindAsync(otherFirst.Id))!.State = Delivery.Finished;
                return true;
            });
            var supervisor = new RuntimeSupervisor(app.Store, null!, Options.Create(new ControlOptions { EventRetention = 4 }), NullLogger<RuntimeSupervisor>.Instance);
            var retain = typeof(RuntimeSupervisor).GetMethod("Retain", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task<bool>)retain.Invoke(supervisor, null)!;

            var retainedAliases = await app.Store.Read(db => db.Events.Where(x => x.Type == "CapabilityInquiryCoalesced")
                .OrderBy(x => x.Sequence).Select(x => x.CommandId).ToListAsync());
            Assert.Equal([recentAliasId], retainedAliases);
            Assert.NotNull(await app.Store.Read(async db => await db.Commands.FindAsync(otherStaleAliasId)));
            Assert.DoesNotContain(await app.Store.Read(db => db.Events.Where(x => x.Type == "RetentionFixture").ToListAsync()),
                x => x.Payload.Contains("\"index\":0", StringComparison.Ordinal));
            Assert.NotNull(await app.Store.Read(async db => await db.Commands.FindAsync(first.Id)));
            Assert.NotNull(await app.Store.Read(async db => await db.Commands.FindAsync(otherFirst.Id)));
        }

        await using var restarted = new TestApp(data, secrets);
        var replayed = await restarted.Store.DiscoverCapabilities(otherWorkerId, new(otherStaleAliasId));
        var repeated = await restarted.Store.DiscoverCapabilities(otherWorkerId, new(otherStaleAliasId));

        Assert.Equal(otherFirstId, replayed.Id);
        Assert.Equal(otherFirstId, repeated.Id);
        Assert.Equal(Delivery.Finished, replayed.State);
        Assert.Equal(2, (await restarted.Store.Snapshot()).Commands.Count(x => x.Kind == "Prompt"));
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
        await Assert.ThrowsAsync<ControlException>(() => app.Store.Prompt(coordinator.Id,
            new(Guid.NewGuid().ToString(), "Do work", 1, RiskLevel: TaskRiskLevels.Low)));
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
