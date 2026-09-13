using HVO.AgentControl.Components.Layout;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class WorkerSidebarTests
{
    [Fact]
    public async Task NavigationRemainsBoundedWithLargeRetainedEvidenceAndPreservesWorkerIdentity()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var evidence = new string('x', 2_000_000);
        await app.Store.Write(async db =>
        {
            var saved = (await db.Workers.FindAsync(worker.Id))!;
            saved.Name = "Navigation worker"; saved.Project = "Project A"; saved.Description = "Searchable description";
            saved.Activity = "WaitingPermission"; saved.Stale = false; saved.ModelsJson = evidence;
            saved.CapabilitiesJson = evidence; saved.CapabilityReport = evidence;
            var runtime = (await db.Runtimes.FindAsync(worker.RuntimeId))!;
            runtime.ModelsJson = evidence;
            db.Commands.Add(new CommandRecord { Id = "large-evidence", RuntimeId = worker.RuntimeId, WorkerId = worker.Id, Kind = "Prompt", Payload = evidence, ResultJson = evidence });
            return true;
        });
        var navigation = await app.Store.NavigationSnapshot();
        var result = Assert.Single(navigation.Workers);
        Assert.Equal(worker.Id, result.Id); Assert.Equal(worker.RuntimeId, result.RuntimeId);
        Assert.Equal("Project A", result.Project); Assert.Equal("Searchable description", result.Description);
        Assert.Equal("Approval needed", DisplayStatus.Worker(result));
        Assert.Single(WorkerSidebarView.TaskWorkerGroups(navigation, "Searchable", null));
        Assert.Empty(navigation.Commands); Assert.Empty(navigation.Requests);
        Assert.True(Json.Write(navigation).Length < 10000);
        await app.Store.Read(async db =>
        {
            Assert.Equal(evidence, (await db.Commands.FindAsync("large-evidence"))!.ResultJson);
            Assert.Equal(evidence, (await db.Workers.FindAsync(worker.Id))!.ModelsJson);
            return true;
        });
    }

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
