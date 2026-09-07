using HVO.AgentControl.Components.Layout;
using HVO.AgentControl.Core;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class WorkerSidebarTests
{
    private static ControlSnapshot Snapshot() => new(1,
        [new() { Id = "runtime-b", Name = "Mac Studio" }, new() { Id = "runtime-a", Name = "Build host" }],
        [
            new() { Id = "worker/alpha", RuntimeId = "runtime-a", Name = "Alpha", Project = "Console", Role = SessionRoles.Worker },
            new() { Id = "worker-beta", RuntimeId = "runtime-b", Name = "Beta", Project = "Dashboard", Role = SessionRoles.Worker },
            new() { Id = "coordinator", RuntimeId = "runtime-a", Name = "Delivery coordinator", Role = SessionRoles.Coordinator },
            new() { Id = "archived", RuntimeId = "runtime-b", Name = "Archived worker", Role = SessionRoles.Worker, Archived = true }
        ], [], []);

    [Fact]
    public void GroupsTaskWorkersByRuntimeAndKeepsCoordinatorSeparate()
    {
        var snapshot = Snapshot();
        var groups = WorkerSidebarView.TaskWorkerGroups(snapshot, "", selectedWorkerId: null);
        var coordinators = WorkerSidebarView.Coordinators(snapshot, "", selectedWorkerId: null);

        Assert.Equal(["Build host", "Mac Studio"], groups.Select(x => x.RuntimeName));
        Assert.Equal("worker/alpha", Assert.Single(groups[0].Workers).Id);
        Assert.Equal("worker-beta", Assert.Single(groups[1].Workers).Id);
        Assert.Equal("coordinator", Assert.Single(coordinators).Id);
        Assert.DoesNotContain(groups.SelectMany(x => x.Workers), x => x.Role == SessionRoles.Coordinator);
    }

    [Fact]
    public void SearchMatchesRuntimeWorkerProjectAndExactId()
    {
        var snapshot = Snapshot();

        Assert.Equal("worker-beta", Assert.Single(Assert.Single(WorkerSidebarView.TaskWorkerGroups(snapshot, "Mac Studio", null)).Workers).Id);
        Assert.Equal("worker-beta", Assert.Single(Assert.Single(WorkerSidebarView.TaskWorkerGroups(snapshot, "dashboard", null)).Workers).Id);
        Assert.Equal("worker/alpha", Assert.Single(Assert.Single(WorkerSidebarView.TaskWorkerGroups(snapshot, "worker/alpha", null)).Workers).Id);
        Assert.Equal("coordinator", Assert.Single(WorkerSidebarView.Coordinators(snapshot, "delivery", null)).Id);
    }

    [Fact]
    public void SelectedArchivedWorkerRemainsAddressableAndIdIsEscaped()
    {
        var snapshot = Snapshot();

        Assert.Empty(WorkerSidebarView.TaskWorkerGroups(snapshot, "Archived", selectedWorkerId: null));
        Assert.Equal("archived", Assert.Single(Assert.Single(WorkerSidebarView.TaskWorkerGroups(snapshot, "Archived", "archived")).Workers).Id);
        Assert.Equal("/?worker=worker%2Falpha", WorkerSidebarView.ConversationUrl("worker/alpha"));
    }
}
