using System.Net;
using System.Net.Http.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class TaskBindingTests
{
    private static string Id() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task BindingApiRoutesRequireOwnerAndCsrf()
    {
        await using var app = new TestApp();
        using var anonymous = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/worker-slots")).StatusCode);
        using var owner = await app.SignIn();
        owner.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/worker-slots", new { requestId = Id(), id = Id(), runtimeId = Id(), name = "slot" })).StatusCode);
        using var authorized = await app.SignIn();
        Assert.Equal(HttpStatusCode.OK, (await authorized.GetAsync("/api/v1/worker-slots")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await authorized.GetAsync("/api/v1/task-bindings")).StatusCode);
    }

    [Fact]
    public async Task ControlHttpRuntimeRejectsWorkerSlotThroughOwnerApiWithoutMutationReceipt()
    {
        await using var app = new TestApp();
        var runtimeId = Id();
        await app.Store.Write(db =>
        {
            db.Runtimes.Add(new RuntimeRecord { Id = runtimeId, ConnectionKind = RuntimeConnections.ControlHttp, Name = "control" });
            return Task.FromResult(true);
        });
        using var owner = await app.SignIn();

        var response = await owner.PostAsJsonAsync("/api/v1/worker-slots", new
        {
            requestId = Id(),
            id = Id(),
            runtimeId,
            name = "invalid-slot"
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await app.Store.Read(db => db.WorkerSlots.CountAsync()));
        Assert.Equal(0, await app.Store.Read(db => db.InventoryMutations.CountAsync()));
    }

    [Fact]
    public async Task ControlHttpRuntimeRejectsTaskBindingThroughOwnerApiWithoutBindingReceipt()
    {
        await using var app = new TestApp();
        var setup = await Seed(app, "https://github.com/RoySalisbury/HVO.ControlHttp.git", "feature/control-http");
        await app.Store.Write(async db =>
        {
            (await db.Runtimes.FindAsync(setup.Runtime.Id))!.ConnectionKind = RuntimeConnections.ControlHttp;
            return true;
        });
        using var owner = await app.SignIn();
        var input = NewBinding(setup, "control-http-task", "control-http-workspace");
        var receiptsBefore = await app.Store.Read(db => db.InventoryMutations.CountAsync());

        var response = await owner.PostAsJsonAsync("/api/v1/task-bindings", input);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await app.Store.Read(db => db.TaskBindings.CountAsync()));
        Assert.Equal(0, await app.Store.Read(db => db.TaskWorkspaces.CountAsync()));
        Assert.Equal(0, await app.Store.Read(db => db.TaskSessionBindings.CountAsync()));
        Assert.Equal(receiptsBefore, await app.Store.Read(db => db.InventoryMutations.CountAsync()));
    }

    [Fact]
    public async Task BindingCreatesFreshCrossRepositoryWorkspaceAndUnboundSession()
    {
        await using var app = new TestApp();
        var runtime = await app.Store.SaveRuntimeForSetup(new RuntimeRecord { Id = Id(), Name = "legacy", Host = "host", Username = "agent", HostKeySha256 = "SHA256:1234567890123456789012345678901234567890123", CredentialReference = "key", ServerPasswordReference = "server-password", StateDirectory = "/home/agent/state", AllowedRoots = "/home/agent/workspaces" }, Id());
        var runtimeRecord = await app.Store.Read(db => db.Runtimes.SingleAsync(x => x.Id == runtime.ResultId));
        var host = await app.Store.CreateHost(new(Id(), Id(), "machine"));
        await app.Store.ConfigureRuntimeEnvironment(runtimeRecord.Id, new(Id(), 0, runtimeRecord.Revision, host.Id, RuntimeEnvironmentKind.ExistingMachine));
        var project = await app.Store.CreateProject(new(Id(), Id(), "Repo", "https://github.com/RoySalisbury/HVO.Other.git"));
        var legacyWorker = new WorkerRecord { Id = Id(), RuntimeId = runtimeRecord.Id, ManagedServerId = runtimeRecord.ManagedServerId, NativeSessionId = "legacy-session" };
        await app.Store.Write(async db => { db.Workers.Add(legacyWorker); return true; });
        var work = await app.Store.CreateWorkItem(new("work-one", "1", "Other", "feature/one", project.RepositoryUrl, legacyWorker.Id));
        var slot = await app.Store.CreateWorkerSlot(new(Id(), Id(), runtimeRecord.Id, "slot-one"));
        var binding = await app.Store.CreateTaskBinding(new(Id(), Id(), work.Id, project.Id, slot.Id, Id(), Id(), "/work/other-one", work.Branch, work.Revision, project.Revision, slot.Revision));
        Assert.Equal(project.Id, binding.Project.Id);
        Assert.Equal(TaskBindingState.Active, binding.Binding.State);
        Assert.Equal(TaskSessionBindingState.Unbound, binding.Session.State);
        Assert.Empty(binding.Session.NativeSessionId);
        Assert.False(binding.Binding.PlacementVerified);
        Assert.Equal(project.RepositoryUrl, binding.Workspace.ProjectId == project.Id ? project.RepositoryUrl : "");
    }

    [Fact]
    public async Task ConcurrentAttemptsCannotReuseSlotWorkspaceOrTask()
    {
        await using var app = new TestApp();
        var setup = await Seed(app, "https://github.com/RoySalisbury/HVO.One.git", "feature/one");
        var first = NewBinding(setup, "task-a", "workspace-a");
        var second = first with { RequestId = Id(), Id = Id(), WorkspaceId = Id(), Directory = "/work/workspace-b" };
        var results = await Task.WhenAll(new[] { first, second }.Select(async input =>
        {
            try { await app.Store.CreateTaskBinding(input); return true; }
            catch (InventoryException ex) { Assert.Contains(ex.Code, new[] { "slot_in_use", "task_in_use" }); return false; }
        }));
        Assert.Single(results, x => x);
    }

    [Fact]
    public async Task RequestReplaySurvivesRestartAndReleaseUsesCas()
    {
        string data, secrets;
        CreateTaskBindingInput input;
        TaskBindingView created;
        await using (var app = new TestApp())
        {
            var setup = await Seed(app, "https://github.com/RoySalisbury/HVO.Two.git", "feature/two");
            input = NewBinding(setup, "task-two", "workspace-two");
            created = await app.Store.CreateTaskBinding(input);
            Assert.Equal(Json.Write(created), Json.Write(await app.Store.CreateTaskBinding(input)));
            await Assert.ThrowsAsync<InventoryException>(() => app.Store.ReleaseTaskBinding(created.Binding.Id, new(Id(), created.Binding.Revision - 1)));
            data = app.DataPath; secrets = app.SecretPath;
        }
        await using var restarted = new TestApp(data, secrets);
        Assert.Equal(Json.Write(created), Json.Write(await restarted.Store.CreateTaskBinding(input)));
        var released = await restarted.Store.ReleaseTaskBinding(created.Binding.Id, new(Id(), created.Binding.Revision));
        Assert.Equal(TaskBindingState.Released, released.Binding.State);
        Assert.Equal(TaskBindingState.Released, released.Workspace.State);
        Assert.Equal(TaskSessionBindingState.Released, released.Session.State);
    }

    [Fact]
    public async Task LegacySessionRequiresExactExplicitCompatibilityBinding()
    {
        await using var app = new TestApp();
        var setup = await Seed(app, "https://github.com/RoySalisbury/HVO.Legacy.git", "feature/legacy");
        var wrong = NewBinding(setup, "legacy-task", "legacy-workspace") with { LegacyWorkerId = setup.LegacyWorker.Id, NativeSessionId = "wrong" };
        var error = await Assert.ThrowsAsync<InventoryException>(() => app.Store.CreateTaskBinding(wrong));
        Assert.Equal("legacy_session_mismatch", error.Code);
        var right = wrong with { RequestId = Id(), Id = Id(), WorkspaceId = Id(), SessionBindingId = Id(), NativeSessionId = setup.LegacyWorker.NativeSessionId, Directory = setup.LegacyWorker.Directory };
        var binding = await app.Store.CreateTaskBinding(right);
        Assert.Equal(TaskSessionBindingState.Bound, binding.Session.State);
        Assert.Equal(setup.LegacyWorker.NativeSessionId, binding.Session.NativeSessionId);
    }

    [Fact]
    public async Task BoundCompatibilityWorkerCannotBeArchivedUntilBindingIsReleased()
    {
        await using var app = new TestApp();
        var setup = await Seed(app, "https://github.com/RoySalisbury/HVO.ArchiveBound.git", "feature/archive-bound");
        var binding = await app.Store.CreateTaskBinding(NewBinding(setup, "archive-bound", "archive-bound") with
        {
            LegacyWorkerId = setup.LegacyWorker.Id,
            NativeSessionId = setup.LegacyWorker.NativeSessionId,
            Directory = setup.LegacyWorker.Directory
        });
        await app.Store.Write(async db =>
        {
            var worker = (await db.Workers.FindAsync(setup.LegacyWorker.Id))!;
            worker.Activity = "Idle"; worker.Stale = false; worker.LastObservedAt = ControlStore.Now;
            return true;
        });

        await Assert.ThrowsAsync<ControlException>(() => app.Store.ArchiveWorker(setup.LegacyWorker.Id,
            new(Id(), setup.LegacyWorker.SettingsRevision, true)));
        Assert.False((await app.Store.Detail(setup.LegacyWorker.Id)).Worker.Archived);

        await app.Store.ReleaseTaskBinding(binding.Binding.Id, new(Id(), binding.Binding.Revision));
        var archived = await app.Store.ArchiveWorker(setup.LegacyWorker.Id,
            new(Id(), setup.LegacyWorker.SettingsRevision, true));
        Assert.True(archived.Archived);
    }

    [Fact]
    public async Task ArchivedSlotCannotRestoreAfterItsRuntimeIsDeletedEvenAfterRestart()
    {
        string data, secrets;
        WorkerSlotRecord archived;
        await using (var app = new TestApp())
        {
            var runtimeCommand = await app.Store.SaveRuntimeForSetup(PersistenceTests.Profile(), Id());
            var runtime = await app.Store.Read(db => db.Runtimes.SingleAsync(x => x.Id == runtimeCommand.ResultId));
            var slot = await app.Store.CreateWorkerSlot(new(Id(), Id(), runtime.Id, "retained-slot"));
            archived = await app.Store.ArchiveWorkerSlot(slot.Id, new(Id(), slot.Revision, true));
            await app.Store.DeleteRuntime(runtime.Id, new(Id(), runtime.Revision));
            data = app.DataPath; secrets = app.SecretPath;
        }

        await using var restarted = new TestApp(data, secrets);
        var error = await Assert.ThrowsAsync<InventoryException>(() => restarted.Store.ArchiveWorkerSlot(
            archived.Id, new(Id(), archived.Revision, false)));
        Assert.Equal("runtime_unavailable", error.Code);
        Assert.True((await restarted.Store.WorkerSlot(archived.Id)).Archived);
        Assert.Equal(0, await restarted.Store.Read(db => db.Runtimes.CountAsync()));
    }

    [Fact]
    public async Task LegacySessionFailsClosedForWrongProjectTaskOrUnknownAssociation()
    {
        await using var app = new TestApp();
        var setup = await Seed(app, "https://github.com/RoySalisbury/HVO.Identity.git", "feature/identity");
        var input = NewBinding(setup, "identity-task", "identity-workspace") with
        {
            LegacyWorkerId = setup.LegacyWorker.Id,
            NativeSessionId = setup.LegacyWorker.NativeSessionId,
            Directory = setup.LegacyWorker.Directory
        };

        await app.Store.Write(async db => { (await db.Workers.FindAsync(setup.LegacyWorker.Id))!.Project = "another-project"; return true; });
        var wrongProject = await Assert.ThrowsAsync<InventoryException>(() => app.Store.CreateTaskBinding(input));
        Assert.Equal("legacy_session_mismatch", wrongProject.Code);

        await app.Store.Write(async db =>
        {
            var worker = (await db.Workers.FindAsync(setup.LegacyWorker.Id))!;
            worker.Project = setup.Project.Name;
            (await db.WorkItems.FindAsync(setup.Work.Id))!.OwnerWorkerId = Id();
            return true;
        });
        var wrongTask = await Assert.ThrowsAsync<InventoryException>(() => app.Store.CreateTaskBinding(input with { RequestId = Id(), Id = Id(), WorkspaceId = Id(), SessionBindingId = Id() }));
        Assert.Equal("legacy_session_mismatch", wrongTask.Code);

        await app.Store.Write(async db => { (await db.WorkItems.FindAsync(setup.Work.Id))!.OwnerWorkerId = ""; return true; });
        var unknownAssociation = await Assert.ThrowsAsync<InventoryException>(() => app.Store.CreateTaskBinding(input with { RequestId = Id(), Id = Id(), WorkspaceId = Id(), SessionBindingId = Id() }));
        Assert.Equal("legacy_session_mismatch", unknownAssociation.Code);
    }

    [Fact]
    public async Task TaskBindingApiRequiresMatchingLegacyProjectAndTaskBeforeBinding()
    {
        await using var app = new TestApp();
        var setup = await Seed(app, "https://github.com/RoySalisbury/HVO.ApiIdentity.git", "feature/api-identity");
        using var client = await app.SignIn();
        var input = NewBinding(setup, "api-identity-task", "api-identity-workspace") with
        {
            LegacyWorkerId = setup.LegacyWorker.Id,
            NativeSessionId = setup.LegacyWorker.NativeSessionId,
            Directory = setup.LegacyWorker.Directory
        };

        await app.Store.Write(async db => { (await db.Workers.FindAsync(setup.LegacyWorker.Id))!.Project = "wrong-project"; return true; });
        var rejected = await client.PostAsJsonAsync("/api/v1/task-bindings", input);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);

        await app.Store.Write(async db => { (await db.Workers.FindAsync(setup.LegacyWorker.Id))!.Project = setup.Project.Name; return true; });
        var accepted = await client.PostAsJsonAsync("/api/v1/task-bindings", input with { RequestId = Id() });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var binding = await accepted.Content.ReadFromJsonAsync<TaskBindingView>();
        Assert.Equal(TaskSessionBindingState.Bound, binding!.Session.State);
    }

    [Fact]
    public async Task PaginationUsesReturnedSequenceCursorWithoutBoundaryTie()
    {
        await using var app = new TestApp();
        var first = await Seed(app, "https://github.com/RoySalisbury/HVO.Page.git", "feature/page-1");
        var slot = await app.Store.CreateWorkerSlot(new(Id(), Id(), first.Runtime.Id, "slot-page"));
        var bindings = new List<TaskBindingView>();
        foreach (var number in new[] { "one", "two" })
        {
            var work = await app.Store.CreateWorkItem(new("page-" + number, null, number, "feature/page-" + number, first.Project.RepositoryUrl, first.LegacyWorker.Id));
            bindings.Add(await app.Store.CreateTaskBinding(new(Id(), Id(), work.Id, first.Project.Id, slot.Id, Id(), Id(), "/work/page-" + number, work.Branch, work.Revision, first.Project.Revision, slot.Revision)));
            await app.Store.ReleaseTaskBinding(bindings[^1].Binding.Id, new(Id(), bindings[^1].Binding.Revision));
        }
        var page = await app.Store.TaskBindings(take: 1, includeReleased: true);
        var next = await app.Store.TaskBindings(page.NextAfter!.Value, 10, true);
        Assert.Equal(bindings[0].Binding.Id, page.Items[0].Binding.Id);
        Assert.Contains(bindings[1].Binding.Id, next.Items.Select(x => x.Binding.Id));
    }

    [Fact]
    public async Task LegacyRuntimeKeyCasingIsPreservedWhenBindingAWorkerSlot()
    {
        await using var app = new TestApp();
        var runtimeId = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var command = await app.Store.SaveRuntimeForSetup(new RuntimeRecord
        {
            Id = runtimeId,
            Name = "legacy-case",
            Host = "host",
            Username = "agent",
            HostKeySha256 = "SHA256:1234567890123456789012345678901234567890123",
            CredentialReference = "key",
            ServerPasswordReference = "server-password",
            StateDirectory = "/home/agent/state",
            AllowedRoots = "/home/agent/workspaces"
        }, Id());
        var host = await app.Store.CreateHost(new(Id(), Id(), "case-host"));
        var runtime = await app.Store.Read(db => db.Runtimes.SingleAsync(x => x.Id == command.ResultId));
        await app.Store.ConfigureRuntimeEnvironment(runtime.Id, new(Id(), 0, runtime.Revision, host.Id, RuntimeEnvironmentKind.ExistingMachine));
        var slot = await app.Store.CreateWorkerSlot(new(Id(), Id(), runtimeId, "case-slot"));
        Assert.Equal(runtimeId, slot.RuntimeId);
        Assert.Equal(runtimeId, (await app.Store.RuntimeEnvironment(runtimeId)).RuntimeId);
    }

    private static CreateTaskBindingInput NewBinding(SeedData data, string task, string workspace) =>
        new(Id(), Id(), data.Work.Id, data.Project.Id, data.Slot.Id, Id(), Id(), "/work/" + workspace, data.Work.Branch,
            data.Work.Revision, data.Project.Revision, data.Slot.Revision);

    private static async Task<SeedData> Seed(TestApp app, string repository, string branch)
    {
        var runtimeCommand = await app.Store.SaveRuntimeForSetup(new RuntimeRecord { Id = Id(), Name = "runtime", Host = "host", Username = "agent", HostKeySha256 = "SHA256:1234567890123456789012345678901234567890123", CredentialReference = "key", ServerPasswordReference = "server-password", StateDirectory = "/home/agent/state", AllowedRoots = "/home/agent/workspaces" }, Id());
        var runtime = await app.Store.Read(db => db.Runtimes.SingleAsync(x => x.Id == runtimeCommand.ResultId));
        var host = await app.Store.CreateHost(new(Id(), Id(), "host"));
        await app.Store.ConfigureRuntimeEnvironment(runtime.Id, new(Id(), 0, runtime.Revision, host.Id, RuntimeEnvironmentKind.ExistingMachine));
        var project = await app.Store.CreateProject(new(Id(), Id(), "project", repository));
        var worker = new WorkerRecord { Id = Id(), RuntimeId = runtime.Id, ManagedServerId = runtime.ManagedServerId, NativeSessionId = "native-" + Id(), Project = project.Name, Directory = "/work/legacy", Branch = branch };
        await app.Store.Write(async db => { db.Workers.Add(worker); return true; });
        var work = await app.Store.CreateWorkItem(new("work-" + Id(), null, "task", branch, project.RepositoryUrl, worker.Id));
        var slot = await app.Store.CreateWorkerSlot(new(Id(), Id(), runtime.Id, "slot-" + Id()));
        return new(runtime, project, worker, work, slot);
    }

    private sealed record SeedData(RuntimeRecord Runtime, ProjectRecord Project, WorkerRecord LegacyWorker, WorkItem Work, WorkerSlotRecord Slot);
}
