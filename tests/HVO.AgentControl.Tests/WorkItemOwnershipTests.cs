using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class WorkItemOwnershipTests
{
    [Fact]
    public async Task CreateWorkItemSucceedsWithValidInput()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var input = new CreateWorkItemInput("wi-1", "42", "Implement feature X", "feat/x", "HVO.AgentControl", worker.Id, "implementation");
        var workItem = await app.Store.CreateWorkItem(input);
        Assert.Equal("wi-1", workItem.Id);
        Assert.Equal("42", workItem.IssueNumber);
        Assert.Equal("Implement feature X", workItem.Title);
        Assert.Equal("feat/x", workItem.Branch);
        Assert.Equal("HVO.AgentControl", workItem.Repository);
        Assert.Equal(worker.Id, workItem.OwnerWorkerId);
        Assert.Equal(WorkItemState.Active, workItem.State);
        Assert.Equal("implementation", workItem.CurrentPhase);
    }

    [Fact]
    public async Task CreateWorkItemWithDuplicateIdThrows()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var input = new CreateWorkItemInput("wi-dup", "42", "First", "feat/x", "HVO.AgentControl", worker.Id);
        await app.Store.CreateWorkItem(input);
        await Assert.ThrowsAsync<ControlException>(() => app.Store.CreateWorkItem(input with { Title = "Second" }));
    }

    [Fact]
    public async Task CreateWorkItemWithArchivedWorkerThrows()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        await app.Store.Write(async db =>
        {
            var w = (await db.Workers.FindAsync(worker.Id))!;
            w.Archived = true;
            return true;
        });
        var input = new CreateWorkItemInput("wi-archived", "42", "Test", "feat/x", "HVO.AgentControl", worker.Id);
        await Assert.ThrowsAsync<ControlException>(() => app.Store.CreateWorkItem(input));
    }

    [Fact]
    public async Task ClaimWorkItemSucceedsForOwner()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var workItem = await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-claim", "42", "Test", "feat/x", "HVO.AgentControl", worker.Id));
        var claimed = await app.Store.ClaimWorkItem(new WorkItemClaimInput(workItem.Id, worker.Id));
        Assert.Equal(workItem.Id, claimed.Id);
        Assert.Equal(workItem.Revision + 1, claimed.Revision);
    }

    [Fact]
    public async Task ClaimWorkItemWithWrongOwnerThrows()
    {
        await using var app = new TestApp();
        var worker1 = await PersistenceTests.SeedWorker(app.Store);
        var runtime2 = await app.Store.SaveRuntime(PersistenceTests.Profile());
        var worker2 = new WorkerRecord { RuntimeId = runtime2.Id, ManagedServerId = runtime2.ManagedServerId, NativeSessionId = "ses_2", Directory = "/home/agent/workspaces/b", Name = "Worker2" };
        await app.Store.Write(db => { db.Workers.Add(worker2); return Task.FromResult(true); });
        var workItem = await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-wrong", "42", "Test", "feat/x", "HVO.AgentControl", worker1.Id));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.ClaimWorkItem(new WorkItemClaimInput(workItem.Id, worker2.Id)));
    }

    [Fact]
    public async Task ClaimWorkItemWithPhaseTransitionSucceeds()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var workItem = await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-phase", "42", "Test", "feat/x", "HVO.AgentControl", worker.Id, "implementation"));
        var claimed = await app.Store.ClaimWorkItem(new WorkItemClaimInput(workItem.Id, worker.Id, "review"));
        Assert.Equal("review", claimed.CurrentPhase);
        var phases = await app.Store.GetPhases(workItem.Id);
        Assert.Equal(2, phases.Count);
        Assert.Contains(phases, p => p.Name == "implementation" && p.State == WorkItemPhaseState.Complete);
        Assert.Contains(phases, p => p.Name == "review" && p.State == WorkItemPhaseState.Active);
    }

    [Fact]
    public async Task ReleaseWorkItemSucceedsForOwner()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var workItem = await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-release", "42", "Test", "feat/x", "HVO.AgentControl", worker.Id));
        var released = await app.Store.ReleaseWorkItem(new WorkItemReleaseInput(workItem.Id, worker.Id, null, "Done"));
        Assert.Equal(WorkItemState.Released, released.State);
    }

    [Fact]
    public async Task ReleaseWorkItemWithWrongOwnerThrows()
    {
        await using var app = new TestApp();
        var worker1 = await PersistenceTests.SeedWorker(app.Store);
        var runtime2 = await app.Store.SaveRuntime(PersistenceTests.Profile());
        var worker2 = new WorkerRecord { RuntimeId = runtime2.Id, ManagedServerId = runtime2.ManagedServerId, NativeSessionId = "ses_3", Directory = "/home/agent/workspaces/c", Name = "Worker3" };
        await app.Store.Write(db => { db.Workers.Add(worker2); return Task.FromResult(true); });
        var workItem = await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-rel-wrong", "42", "Test", "feat/x", "HVO.AgentControl", worker1.Id));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.ReleaseWorkItem(new WorkItemReleaseInput(workItem.Id, worker2.Id)));
    }

    [Fact]
    public async Task TransitionWorkItemActiveToInReviewSucceeds()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var workItem = await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-trans", "42", "Test", "feat/x", "HVO.AgentControl", worker.Id, "implementation"));
        var transitioned = await app.Store.TransitionWorkItem(new TransitionWorkItemInput(workItem.Id, workItem.Revision, worker.Id, WorkItemState.InReview, "review"));
        Assert.Equal(WorkItemState.InReview, transitioned.State);
        Assert.Equal("review", transitioned.CurrentPhase);
    }

    [Fact]
    public async Task TransitionWorkItemInvalidTransitionThrows()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var workItem = await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-inv", "42", "Test", "feat/x", "HVO.AgentControl", worker.Id));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.TransitionWorkItem(new TransitionWorkItemInput(workItem.Id, workItem.Revision, worker.Id, WorkItemState.Released, null)));
    }

    [Fact]
    public async Task TransitionWorkItemStaleRevisionThrows()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var workItem = await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-stale", "42", "Test", "feat/x", "HVO.AgentControl", worker.Id));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.TransitionWorkItem(new TransitionWorkItemInput(workItem.Id, workItem.Revision + 1, worker.Id, WorkItemState.InReview, null)));
    }

    [Fact]
    public async Task TransitionWorkItemWithWrongOwnerThrows()
    {
        await using var app = new TestApp();
        var worker1 = await PersistenceTests.SeedWorker(app.Store);
        var runtime2 = await app.Store.SaveRuntime(PersistenceTests.Profile());
        var worker2 = new WorkerRecord { RuntimeId = runtime2.Id, ManagedServerId = runtime2.ManagedServerId, NativeSessionId = "ses_trans", Directory = "/home/agent/workspaces/trans", Name = "WorkerTrans" };
        await app.Store.Write(db => { db.Workers.Add(worker2); return Task.FromResult(true); });
        var workItem = await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-trans-wrong", "42", "Test", "feat/x", "HVO.AgentControl", worker1.Id));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.TransitionWorkItem(new TransitionWorkItemInput(workItem.Id, workItem.Revision, worker2.Id, WorkItemState.InReview, null)));
    }

    [Fact]
    public async Task AdvancePhaseSucceedsForOwner()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var workItem = await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-adv", "42", "Test", "feat/x", "HVO.AgentControl", worker.Id, "implementation"));
        var advanced = await app.Store.AdvancePhase(new AdvancePhaseInput(workItem.Id, worker.Id, "implementation", "review", "Phase complete"));
        Assert.Equal("review", advanced.CurrentPhase);
        var phases = await app.Store.GetPhases(workItem.Id);
        var impl = phases.First(p => p.Name == "implementation");
        Assert.Equal(WorkItemPhaseState.Complete, impl.State);
        Assert.NotNull(impl.CompletedAt);
        Assert.Equal("Phase complete", impl.Evidence);
    }

    [Fact]
    public async Task AdvancePhaseWithWrongOwnerThrows()
    {
        await using var app = new TestApp();
        var worker1 = await PersistenceTests.SeedWorker(app.Store);
        var runtime2 = await app.Store.SaveRuntime(PersistenceTests.Profile());
        var worker2 = new WorkerRecord { RuntimeId = runtime2.Id, ManagedServerId = runtime2.ManagedServerId, NativeSessionId = "ses_4", Directory = "/home/agent/workspaces/d", Name = "Worker4" };
        await app.Store.Write(db => { db.Workers.Add(worker2); return Task.FromResult(true); });
        var workItem = await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-adv-wrong", "42", "Test", "feat/x", "HVO.AgentControl", worker1.Id, "implementation"));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.AdvancePhase(new AdvancePhaseInput(workItem.Id, worker2.Id, "implementation", "review")));
    }

    [Fact]
    public async Task WorkItemOwnershipSurvivesRestart()
    {
        string data, secrets, workItemId;
        await using (var app = new TestApp())
        {
            var worker = await PersistenceTests.SeedWorker(app.Store);
            var workItem = await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-restart", "42", "Restart test", "feat/restart", "HVO.AgentControl", worker.Id, "implementation"));
            workItemId = workItem.Id;
            data = app.DataPath; secrets = app.SecretPath;
        }
        await using var restarted = new TestApp(data, secrets);
        var recovered = await restarted.Store.GetWorkItem(workItemId);
        Assert.NotNull(recovered);
        Assert.Equal("wi-restart", recovered.Id);
        Assert.Equal("implementation", recovered.CurrentPhase);
        Assert.Equal(WorkItemState.Active, recovered.State);
        var phases = await restarted.Store.GetPhases(workItemId);
        Assert.Single(phases);
        Assert.Equal(WorkItemPhaseState.Active, phases[0].State);
    }

    [Fact]
    public async Task GetActiveWorkItemsExcludesReleasedAndAbandoned()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-active", "42", "Active", "feat/active", "HVO.AgentControl", worker.Id));
        await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-released", "43", "Released", "feat/released", "HVO.AgentControl", worker.Id));
        await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-abandoned", "44", "Abandoned", "feat/abandoned", "HVO.AgentControl", worker.Id));
        var workItem = await app.Store.GetWorkItem("wi-released");
        await app.Store.ReleaseWorkItem(new WorkItemReleaseInput(workItem!.Id, worker.Id));
        workItem = await app.Store.GetWorkItem("wi-abandoned");
        await app.Store.TransitionWorkItem(new TransitionWorkItemInput(workItem!.Id, workItem.Revision, worker.Id, WorkItemState.Abandoned, null));
        var active = await app.Store.GetActiveWorkItems();
        Assert.DoesNotContain(active, x => x.Id == "wi-released");
        Assert.DoesNotContain(active, x => x.Id == "wi-abandoned");
        Assert.Contains(active, x => x.Id == "wi-active");
    }

    [Fact]
    public async Task GetWorkItemsByOwnerReturnsCorrectItems()
    {
        await using var app = new TestApp();
        var worker1 = await PersistenceTests.SeedWorker(app.Store);
        var runtime2 = await app.Store.SaveRuntime(PersistenceTests.Profile());
        var worker2 = new WorkerRecord { RuntimeId = runtime2.Id, ManagedServerId = runtime2.ManagedServerId, NativeSessionId = "ses_5", Directory = "/home/agent/workspaces/e", Name = "Worker5" };
        await app.Store.Write(db => { db.Workers.Add(worker2); return Task.FromResult(true); });
        await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-w1", "42", "Worker1 item", "feat/w1", "HVO.AgentControl", worker1.Id));
        await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-w2", "43", "Worker2 item", "feat/w2", "HVO.AgentControl", worker2.Id));
        await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-w1-2", "44", "Worker1 item 2", "feat/w1-2", "HVO.AgentControl", worker1.Id));
        var w1Items = await app.Store.GetWorkItemsByOwner(worker1.Id);
        Assert.Equal(2, w1Items.Count);
        Assert.All(w1Items, x => Assert.Equal(worker1.Id, x.OwnerWorkerId));
    }

    [Fact]
    public async Task IsWorkItemOwnerReturnsTrueForOwner()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var workItem = await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-owner", "42", "Test", "feat/owner", "HVO.AgentControl", worker.Id));
        Assert.True(await app.Store.IsWorkItemOwner(workItem.Id, worker.Id));
        var runtime2 = await app.Store.SaveRuntime(PersistenceTests.Profile());
        var worker2 = new WorkerRecord { RuntimeId = runtime2.Id, ManagedServerId = runtime2.ManagedServerId, NativeSessionId = "ses_6", Directory = "/home/agent/workspaces/f", Name = "Worker6" };
        await app.Store.Write(db => { db.Workers.Add(worker2); return Task.FromResult(true); });
        Assert.False(await app.Store.IsWorkItemOwner(workItem.Id, worker2.Id));
    }

    [Fact]
    public async Task ValidateOneModifyingOwnerChecksWorkerNotArchived()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-validate", "42", "Test", "feat/validate", "HVO.AgentControl", worker.Id));
        Assert.True(await app.Store.ValidateOneModifyingOwner("wi-validate", worker.Id));
        await app.Store.Write(async db =>
        {
            var w = (await db.Workers.FindAsync(worker.Id))!;
            w.Archived = true;
            return true;
        });
        Assert.False(await app.Store.ValidateOneModifyingOwner("wi-validate", worker.Id));
    }

    [Fact]
    public async Task ValidateOneModifyingOwnerReturnsFalseForReleasedState()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var workItem = await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-released-val", "42", "Test", "feat/rel", "HVO.AgentControl", worker.Id));
        await app.Store.ReleaseWorkItem(new WorkItemReleaseInput(workItem.Id, worker.Id));
        Assert.False(await app.Store.ValidateOneModifyingOwner("wi-released-val", worker.Id));
    }

    [Fact]
    public async Task ValidateOneModifyingOwnerReturnsFalseForAbandonedState()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var workItem = await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-abandoned-val", "42", "Test", "feat/abd", "HVO.AgentControl", worker.Id));
        await app.Store.TransitionWorkItem(new TransitionWorkItemInput(workItem.Id, workItem.Revision, worker.Id, WorkItemState.Abandoned, null));
        Assert.False(await app.Store.ValidateOneModifyingOwner("wi-abandoned-val", worker.Id));
    }

    [Fact]
    public async Task CreateWorkItemWithDuplicateRepoBranchIssueThrows()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-dup-1", "42", "First", "feat/x", "HVO.AgentControl", worker.Id));
        var second = new CreateWorkItemInput("wi-dup-2", "42", "Second", "feat/x", "HVO.AgentControl", worker.Id);
        var ex = await Assert.ThrowsAsync<ControlException>(() => app.Store.CreateWorkItem(second));
        Assert.Contains("feat/x", ex.Message);
        Assert.Contains("HVO.AgentControl", ex.Message);
        Assert.Contains("wi-dup-1", ex.Message);
    }

    [Fact]
    public async Task CreateWorkItemWithDifferentIssueSameRepoBranchThrows()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-issue-1", "42", "First", "feat/x", "HVO.AgentControl", worker.Id));
        var second = new CreateWorkItemInput("wi-issue-2", "99", "Second", "feat/x", "HVO.AgentControl", worker.Id);
        var ex = await Assert.ThrowsAsync<ControlException>(() => app.Store.CreateWorkItem(second));
        Assert.Contains("already has an active work item", ex.Message);
        Assert.Contains("feat/x", ex.Message);
        Assert.Contains("HVO.AgentControl", ex.Message);
    }

    [Fact]
    public async Task CreateWorkItemWithNullIssueSameRepoBranchThrows()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-null-1", "42", "First", "feat/x", "HVO.AgentControl", worker.Id));
        var second = new CreateWorkItemInput("wi-null-2", null, "Second (no issue)", "feat/x", "HVO.AgentControl", worker.Id);
        var ex = await Assert.ThrowsAsync<ControlException>(() => app.Store.CreateWorkItem(second));
        Assert.Contains("already has an active work item", ex.Message);
    }

    [Fact]
    public async Task CreateWorkItemWithSameBranchDifferentRepoSucceedsCrossRepo()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-cross-repo-1", "42", "First", "feat/x", "HVO.AgentControl", worker.Id));
        var second = new CreateWorkItemInput("wi-cross-repo-2", "99", "Second", "feat/x", "other-repo", worker.Id);
        var workItem = await app.Store.CreateWorkItem(second);
        Assert.Equal("wi-cross-repo-2", workItem.Id);
    }

    [Fact]
    public async Task CreateWorkItemAfterReleaseFreesBranch()
    {
        string data, secrets;
        await using (var app = new TestApp())
        {
            var worker = await PersistenceTests.SeedWorker(app.Store);
            await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-release-free", "42", "First", "feat/x", "HVO.AgentControl", worker.Id));
            var item = await app.Store.GetWorkItem("wi-release-free");
            await app.Store.ReleaseWorkItem(new WorkItemReleaseInput(item!.Id, worker.Id));
            var second = new CreateWorkItemInput("wi-release-free-2", "99", "Second after release", "feat/x", "HVO.AgentControl", worker.Id);
            var workItem = await app.Store.CreateWorkItem(second);
            Assert.Equal("wi-release-free-2", workItem.Id);
            data = app.DataPath; secrets = app.SecretPath;
        }
        await using var restarted = new TestApp(data, secrets);
        var recovered = await restarted.Store.GetWorkItem("wi-release-free-2");
        Assert.NotNull(recovered);
    }

    [Fact]
    public async Task CreateWorkItemAfterAbandonFreesBranch()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-abandon-free", "42", "First", "feat/x", "HVO.AgentControl", worker.Id));
        var item = await app.Store.GetWorkItem("wi-abandon-free");
        await app.Store.TransitionWorkItem(new TransitionWorkItemInput(item!.Id, item.Revision, worker.Id, WorkItemState.Abandoned, null));
        var second = new CreateWorkItemInput("wi-abandon-free-2", "99", "Second after abandon", "feat/x", "HVO.AgentControl", worker.Id);
        var workItem = await app.Store.CreateWorkItem(second);
        Assert.Equal("wi-abandon-free-2", workItem.Id);
    }

    [Fact]
    public async Task ClaimWorkItemForReleasedWorkItemThrows()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var item = await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-claim-released", "42", "Test", "feat/x", "HVO.AgentControl", worker.Id));
        await app.Store.ReleaseWorkItem(new WorkItemReleaseInput(item.Id, worker.Id));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.ClaimWorkItem(new WorkItemClaimInput(item.Id, worker.Id)));
    }

    [Fact]
    public async Task ClaimWorkItemForAbandonedWorkItemThrows()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var item = await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-claim-abandoned", "42", "Test", "feat/x", "HVO.AgentControl", worker.Id));
        await app.Store.TransitionWorkItem(new TransitionWorkItemInput(item.Id, item.Revision, worker.Id, WorkItemState.Abandoned, null));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.ClaimWorkItem(new WorkItemClaimInput(item.Id, worker.Id)));
    }

    [Fact]
    public async Task CreateWorkItemWithSameRepoDifferentBranchSucceeds()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-branch-1", "42", "First", "feat/x", "HVO.AgentControl", worker.Id));
        var second = new CreateWorkItemInput("wi-branch-2", "42", "Second", "feat/y", "HVO.AgentControl", worker.Id);
        var workItem = await app.Store.CreateWorkItem(second);
        Assert.Equal("wi-branch-2", workItem.Id);
    }

    [Fact]
    public async Task CreateWorkItemWithSameBranchDifferentRepoSucceeds()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-repo-1", "42", "First", "feat/x", "HVO.AgentControl", worker.Id));
        var second = new CreateWorkItemInput("wi-repo-2", "42", "Second", "feat/x", "other-repo", worker.Id);
        var workItem = await app.Store.CreateWorkItem(second);
        Assert.Equal("wi-repo-2", workItem.Id);
    }

    [Fact]
    public async Task CreateWorkItemWithoutPhaseNameCreatesDefaultPhase()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var workItem = await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-nophase", "42", "No phase", "feat/nophase", "HVO.AgentControl", worker.Id));
        var phases = await app.Store.GetPhases(workItem.Id);
        Assert.Single(phases);
        Assert.Equal("implementation", phases[0].Name);
        Assert.Equal(WorkItemPhaseState.Active, phases[0].State);
    }

    [Fact]
    public async Task AdvancePhaseFailsForReleasedWorkItem()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var workItem = await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-adv-rel", "42", "Test", "feat/advrel", "HVO.AgentControl", worker.Id, "implementation"));
        await app.Store.ReleaseWorkItem(new WorkItemReleaseInput(workItem.Id, worker.Id));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.AdvancePhase(new AdvancePhaseInput(workItem.Id, worker.Id, "implementation", "review")));
    }

    [Fact]
    public async Task AdvancePhaseFailsForAbandonedWorkItem()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var workItem = await app.Store.CreateWorkItem(new CreateWorkItemInput("wi-adv-abd", "42", "Test", "feat/advabd", "HVO.AgentControl", worker.Id, "implementation"));
        await app.Store.TransitionWorkItem(new TransitionWorkItemInput(workItem.Id, workItem.Revision, worker.Id, WorkItemState.Abandoned, null));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.AdvancePhase(new AdvancePhaseInput(workItem.Id, worker.Id, "implementation", "review")));
    }
}
