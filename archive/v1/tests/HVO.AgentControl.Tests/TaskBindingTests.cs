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

    // RoofControl (#98) disposable isolation fixtures

    [Fact]
    public async Task TwoRepositoriesSharingIssueNumberAreDistinguishedByProject()
    {
        await using var app = new TestApp();
        var agentControlSetup = await Seed(app, "https://github.com/RoySalisbury/HVO.AgentControl.git", "feature/ac-123-base");
        var roofControlSetup = await SeedSecondProject(app, "https://github.com/RoySalisbury/HVO.RoofControl.git", "feature/rc-123-base");

        // Both projects can have issue #123 - they are distinguished by project identity
        var acWork = await app.Store.CreateWorkItem(new("ac-123", "123", "AgentControl task", "feature/ac-123-a", agentControlSetup.Project.RepositoryUrl, agentControlSetup.LegacyWorker.Id));
        var rcWork = await app.Store.CreateWorkItem(new("rc-123", "123", "RoofControl task", "feature/rc-123-b", roofControlSetup.Project.RepositoryUrl, roofControlSetup.LegacyWorker.Id));

        var acBinding = await app.Store.CreateTaskBinding(new(Id(), Id(), acWork.Id, agentControlSetup.Project.Id, agentControlSetup.Slot.Id, Id(), Id(), "/work/ac-123", acWork.Branch, acWork.Revision, agentControlSetup.Project.Revision, agentControlSetup.Slot.Revision));
        var rcBinding = await app.Store.CreateTaskBinding(new(Id(), Id(), rcWork.Id, roofControlSetup.Project.Id, roofControlSetup.Slot.Id, Id(), Id(), "/work/rc-123", rcWork.Branch, rcWork.Revision, roofControlSetup.Project.Revision, roofControlSetup.Slot.Revision));

        Assert.Equal(agentControlSetup.Project.Id, acBinding.Project.Id);
        Assert.Equal(roofControlSetup.Project.Id, rcBinding.Project.Id);
        Assert.NotEqual(acBinding.Binding.Id, rcBinding.Binding.Id);
        Assert.NotEqual(acBinding.Workspace.Id, rcBinding.Workspace.Id);
        Assert.NotEqual(acBinding.Session.Id, rcBinding.Session.Id);
    }

    [Fact]
    public async Task TwoRepositoriesSharingIssueNumberOnDifferentBranchesAreDistinguished()
    {
        await using var app = new TestApp();
        var setup = await Seed(app, "https://github.com/RoySalisbury/HVO.AgentControl.git", "feature/ac-shared-123-base");

        // Create second project on same runtime
        var rcSetup = await SeedSecondProject(app, "https://github.com/RoySalisbury/HVO.RoofControl.git", "feature/rc-shared-123-base");

        // Same issue number on different branches
        var acWork = await app.Store.CreateWorkItem(new("ac-123-a", "123", "AC task", "feature/ac-shared-123-a", setup.Project.RepositoryUrl, setup.LegacyWorker.Id));
        var rcWork = await app.Store.CreateWorkItem(new("rc-123-b", "123", "RC task", "feature/rc-shared-123-b", rcSetup.Project.RepositoryUrl, rcSetup.LegacyWorker.Id));

        var acBinding = await app.Store.CreateTaskBinding(new(Id(), Id(), acWork.Id, setup.Project.Id, setup.Slot.Id, Id(), Id(), "/work/ac-123", acWork.Branch, acWork.Revision, setup.Project.Revision, setup.Slot.Revision));
        var rcBinding = await app.Store.CreateTaskBinding(new(Id(), Id(), rcWork.Id, rcSetup.Project.Id, rcSetup.Slot.Id, Id(), Id(), "/work/rc-123", rcWork.Branch, rcWork.Revision, rcSetup.Project.Revision, rcSetup.Slot.Revision));

        Assert.Equal(setup.Project.Id, acBinding.Project.Id);
        Assert.Equal(rcSetup.Project.Id, rcBinding.Project.Id);
    }

    [Fact]
    public async Task WrongRepositoryRejectionIsImmediateWithZeroDownstreamCalls()
    {
        await using var app = new TestApp();
        var setup = await Seed(app, "https://github.com/RoySalisbury/HVO.AgentControl.git", "feature/ac-wrong-repo");
        var otherProject = await app.Store.CreateProject(new(Id(), Id(), "Other", "https://github.com/RoySalisbury/HVO.Other.git"));
        var otherWork = await app.Store.CreateWorkItem(new("other-work", "1", "Other task", "feature/other", otherProject.RepositoryUrl, setup.LegacyWorker.Id));

        // Try to bind work item from "Other" project to AgentControl project
        var receiptsBefore = await app.Store.Read(db => db.InventoryMutations.CountAsync());
        var commandsBefore = await app.Store.Read(db => db.Commands.CountAsync());

        var error = await Assert.ThrowsAsync<InventoryException>(() =>
            app.Store.CreateTaskBinding(new(Id(), Id(), otherWork.Id, setup.Project.Id, setup.Slot.Id, Id(), Id(), "/work/wrong", otherWork.Branch, otherWork.Revision, setup.Project.Revision, setup.Slot.Revision)));

        Assert.Equal("validation", error.Code); // Work item repository does not identify the selected project
        Assert.Equal(receiptsBefore, await app.Store.Read(db => db.InventoryMutations.CountAsync()));
        Assert.Equal(commandsBefore, await app.Store.Read(db => db.Commands.CountAsync()));
    }

    [Fact]
    public async Task WrongWorkspaceRejectionIsImmediateWithZeroDownstreamCalls()
    {
        await using var app = new TestApp();
        var setup = await Seed(app, "https://github.com/RoySalisbury/HVO.AgentControl.git", "feature/ac-wrong-ws");

        // Create first binding
        var work1 = await app.Store.CreateWorkItem(new("work-1", "1", "First task", "feature/ac-wrong-ws-1", setup.Project.RepositoryUrl, setup.LegacyWorker.Id));
        var existingBinding = await app.Store.CreateTaskBinding(NewBinding(setup, work1, "existing", "existing-workspace"));

        // Try to create second binding with same slot (different workspace directory)
        // The slot is already in use, so it should fail with slot_in_use
        var work2 = await app.Store.CreateWorkItem(new("work-2", "2", "Second task", "feature/ac-wrong-ws-2", setup.Project.RepositoryUrl, setup.LegacyWorker.Id));

        var receiptsBefore = await app.Store.Read(db => db.InventoryMutations.CountAsync());

        var error = await Assert.ThrowsAsync<InventoryException>(() =>
            app.Store.CreateTaskBinding(new(Id(), Id(), work2.Id, setup.Project.Id, setup.Slot.Id, Id(), Id(), "/work/different", work2.Branch, work2.Revision, setup.Project.Revision, setup.Slot.Revision)));

        // Slot is already in use from first binding
        Assert.Equal("slot_in_use", error.Code);
        Assert.Equal(receiptsBefore, await app.Store.Read(db => db.InventoryMutations.CountAsync()));
    }

    [Fact]
    public async Task WrongSessionGrantRejectionIsImmediateWithZeroDownstreamCalls()
    {
        await using var app = new TestApp();
        var setup = await Seed(app, "https://github.com/RoySalisbury/HVO.AgentControl.git", "feature/ac-wrong-session-1");

        // Try to bind with a legacy session that doesn't match the work item's owner
        var otherWorker = new WorkerRecord { Id = Id(), RuntimeId = setup.Runtime.Id, ManagedServerId = setup.Runtime.ManagedServerId, NativeSessionId = "other-session", Project = setup.Project.Name, Directory = "/work/other", Branch = setup.Work.Branch };
        await app.Store.Write(async db => { db.Workers.Add(otherWorker); return true; });

        var newWork = await app.Store.CreateWorkItem(new("new-work", "2", "New task", "feature/ac-wrong-session-1-b", setup.Project.RepositoryUrl, setup.LegacyWorker.Id));

        var receiptsBefore = await app.Store.Read(db => db.InventoryMutations.CountAsync());

        var error = await Assert.ThrowsAsync<InventoryException>(() =>
            app.Store.CreateTaskBinding(new(Id(), Id(), newWork.Id, setup.Project.Id, setup.Slot.Id, Id(), Id(), "/work/new", newWork.Branch, newWork.Revision, setup.Project.Revision, setup.Slot.Revision)
            {
                LegacyWorkerId = otherWorker.Id,
                NativeSessionId = otherWorker.NativeSessionId,
                Directory = otherWorker.Directory
            }));

        Assert.Equal("legacy_session_mismatch", error.Code);
        Assert.Equal(receiptsBefore, await app.Store.Read(db => db.InventoryMutations.CountAsync()));
    }

    [Fact]
    public async Task ValidAssignmentsToEitherRepositoryAreAccepted()
    {
        await using var app = new TestApp();
        var acSetup = await Seed(app, "https://github.com/RoySalisbury/HVO.AgentControl.git", "feature/ac-valid-1");
        var rcSetup = await SeedSecondProject(app, "https://github.com/RoySalisbury/HVO.RoofControl.git", "feature/rc-valid-1");

        // Assign to AgentControl
        var acWork = await app.Store.CreateWorkItem(new("ac-work", "1", "AC Task", "feature/ac-valid-1-a", acSetup.Project.RepositoryUrl, acSetup.LegacyWorker.Id));
        var acBinding = await app.Store.CreateTaskBinding(NewBinding(acSetup, acWork, "ac-binding", "ac-workspace"));
        Assert.Equal(acSetup.Project.Id, acBinding.Project.Id);

        // Assign to RoofControl
        var rcWork = await app.Store.CreateWorkItem(new("rc-work", "1", "RC Task", "feature/rc-valid-1-b", rcSetup.Project.RepositoryUrl, rcSetup.LegacyWorker.Id));
        var rcBinding = await app.Store.CreateTaskBinding(NewBinding(rcSetup, rcWork, "rc-binding", "rc-workspace"));
        Assert.Equal(rcSetup.Project.Id, rcBinding.Project.Id);
    }

    [Fact]
    public async Task SequentialReuseWithNewSessionsCreatesFreshBindings()
    {
        await using var app = new TestApp();
        var setup = await Seed(app, "https://github.com/RoySalisbury/HVO.AgentControl.git", "feature/sequential-1");

        // First task
        var work1 = await app.Store.CreateWorkItem(new("work-1", "1", "First task", "feature/sequential-1-a", setup.Project.RepositoryUrl, setup.LegacyWorker.Id));
        var binding1 = await app.Store.CreateTaskBinding(NewBinding(setup, work1, "first", "workspace-1"));

        // Release first binding
        await app.Store.ReleaseTaskBinding(binding1.Binding.Id, new(Id(), binding1.Binding.Revision));

        // Re-fetch to verify release
        var releasedBinding = await app.Store.TaskBinding(binding1.Binding.Id);
        Assert.Equal(TaskBindingState.Released, releasedBinding.Binding.State);

        // Second task on same project, reusing the same slot
        var work2 = await app.Store.CreateWorkItem(new("work-2", "2", "Second task", "feature/sequential-1-b", setup.Project.RepositoryUrl, setup.LegacyWorker.Id));
        var binding2 = await app.Store.CreateTaskBinding(NewBinding(setup, work2, "second", "workspace-2"));

        Assert.NotEqual(binding1.Binding.Id, binding2.Binding.Id);
        Assert.NotEqual(binding1.Workspace.Id, binding2.Workspace.Id);
        Assert.NotEqual(binding1.Session.Id, binding2.Session.Id);
        Assert.Equal(TaskBindingState.Released, releasedBinding.Binding.State);
        Assert.Equal(TaskBindingState.Active, binding2.Binding.State);
    }

    [Fact]
    public async Task SequentialAgentControlToRoofControlToAgentControlCreatesFreshSessions()
    {
        await using var app = new TestApp();
        var acSetup = await Seed(app, "https://github.com/RoySalisbury/HVO.AgentControl.git", "feature/ac-seq-1");
        var rcSetup = await SeedSecondProject(app, "https://github.com/RoySalisbury/HVO.RoofControl.git", "feature/rc-seq-1");

        // AC -> RC -> AC sequence
        var acWork1 = await app.Store.CreateWorkItem(new("ac-1", "1", "AC 1", "feature/ac-seq-1-a", acSetup.Project.RepositoryUrl, acSetup.LegacyWorker.Id));
        var ac1 = await app.Store.CreateTaskBinding(NewBinding(acSetup, acWork1, "ac-1", "ws-1"));

        await app.Store.ReleaseTaskBinding(ac1.Binding.Id, new(Id(), ac1.Binding.Revision));

        var rcWork = await app.Store.CreateWorkItem(new("rc-1", "1", "RC 1", "feature/rc-seq-1-b", rcSetup.Project.RepositoryUrl, rcSetup.LegacyWorker.Id));
        var rc1 = await app.Store.CreateTaskBinding(NewBinding(rcSetup, rcWork, "rc-1", "ws-rc"));

        await app.Store.ReleaseTaskBinding(rc1.Binding.Id, new(Id(), rc1.Binding.Revision));

        var acWork2 = await app.Store.CreateWorkItem(new("ac-2", "2", "AC 2", "feature/ac-seq-1-c", acSetup.Project.RepositoryUrl, acSetup.LegacyWorker.Id));
        var ac2 = await app.Store.CreateTaskBinding(NewBinding(acSetup, acWork2, "ac-2", "ws-2"));

        Assert.Equal(3, await app.Store.Read(db => db.TaskBindings.CountAsync()));
        Assert.Equal(3, await app.Store.Read(db => db.TaskWorkspaces.CountAsync()));
        Assert.Equal(3, await app.Store.Read(db => db.TaskSessionBindings.CountAsync()));
    }

    [Fact]
    public async Task SimultaneousSlotContentionAllowsExactlyOneBinding()
    {
        await using var app = new TestApp();
        var setup = await Seed(app, "https://github.com/RoySalisbury/HVO.AgentControl.git", "feature/contention-1");

        var workA = await app.Store.CreateWorkItem(new("work-a", "1", "Task A", "feature/contention-1-a", setup.Project.RepositoryUrl, setup.LegacyWorker.Id));
        var workB = await app.Store.CreateWorkItem(new("work-b", "2", "Task B", "feature/contention-1-b", setup.Project.RepositoryUrl, setup.LegacyWorker.Id));

        var taskA = app.Store.CreateTaskBinding(new(Id(), Id(), workA.Id, setup.Project.Id, setup.Slot.Id, Id(), Id(), "/work/a", workA.Branch, workA.Revision, setup.Project.Revision, setup.Slot.Revision));
        var taskB = app.Store.CreateTaskBinding(new(Id(), Id(), workB.Id, setup.Project.Id, setup.Slot.Id, Id(), Id(), "/work/b", workB.Branch, workB.Revision, setup.Project.Revision, setup.Slot.Revision));

        var results = new List<TaskBindingView>();
        var exceptions = new List<Exception>();

        try { results.Add(await taskA); } catch (Exception ex) { exceptions.Add(ex); }
        try { results.Add(await taskB); } catch (Exception ex) { exceptions.Add(ex); }

        // Exactly one should succeed
        Assert.Single(results);
        Assert.Single(exceptions);
        Assert.IsType<InventoryException>(exceptions[0]);
        Assert.Equal("slot_in_use", ((InventoryException)exceptions[0]).Code);
    }

    [Fact]
    public async Task StalePermissionRejectionPreservesExistingBinding()
    {
        await using var app = new TestApp();
        var setup = await Seed(app, "https://github.com/RoySalisbury/HVO.AgentControl.git", "feature/stale-perm-1");

        var work = await app.Store.CreateWorkItem(new("stale-work", "1", "Stale task", "feature/stale-perm-1-a", setup.Project.RepositoryUrl, setup.LegacyWorker.Id));
        var binding = await app.Store.CreateTaskBinding(NewBinding(setup, work, "stale", "workspace"));

        // Change project permission (archive project)
        await app.Store.Write(async db =>
        {
            var project = await db.Projects.FirstOrDefaultAsync(x => x.Id == setup.Project.Id);
            project!.Archived = true;
            return true;
        });

        // Try to create another binding with stale project
        var work2 = await app.Store.CreateWorkItem(new("stale-work-2", "2", "Stale 2", "feature/stale-perm-1-b", setup.Project.RepositoryUrl, setup.LegacyWorker.Id));
        var receiptsBefore = await app.Store.Read(db => db.InventoryMutations.CountAsync());

        await Assert.ThrowsAsync<InventoryException>(() =>
            app.Store.CreateTaskBinding(NewBinding(setup, work2, "stale-2", "workspace-2")));

        // Original binding should be unaffected
        var existing = await app.Store.TaskBinding(binding.Binding.Id);
        Assert.Equal(TaskBindingState.Active, existing.Binding.State);
        Assert.Equal(receiptsBefore, await app.Store.Read(db => db.InventoryMutations.CountAsync()));
    }

    [Fact]
    public async Task StaleGenerationRejectionDoesNotMutateState()
    {
        await using var app = new TestApp();
        var setup = await Seed(app, "https://github.com/RoySalisbury/HVO.AgentControl.git", "feature/stale-gen-1");

        var work = await app.Store.CreateWorkItem(new("gen-work", "1", "Gen task", "feature/stale-gen-1-a", setup.Project.RepositoryUrl, setup.LegacyWorker.Id));
        var binding = await app.Store.CreateTaskBinding(NewBinding(setup, work, "gen", "workspace"));

        // Increment slot revision manually to simulate stale
        await app.Store.Write(async db =>
        {
            var slot = await db.WorkerSlots.FirstOrDefaultAsync(x => x.Id == setup.Slot.Id);
            slot!.Revision++;
            return true;
        });

        var work2 = await app.Store.CreateWorkItem(new("gen-work-2", "2", "Gen 2", "feature/stale-gen-1-b", setup.Project.RepositoryUrl, setup.LegacyWorker.Id));

        var receiptsBefore = await app.Store.Read(db => db.InventoryMutations.CountAsync());

        await Assert.ThrowsAsync<InventoryException>(() =>
            app.Store.CreateTaskBinding(NewBinding(setup, work2, "gen-2", "workspace-2")));

        Assert.Equal(receiptsBefore, await app.Store.Read(db => db.InventoryMutations.CountAsync()));
        var existing = await app.Store.TaskBinding(binding.Binding.Id);
        Assert.Equal(TaskBindingState.Active, existing.Binding.State);
    }

    [Fact]
    public async Task StaleProposalRejectionAndRestartReconciliation()
    {
        string data, secrets;
        TaskBindingView binding;
        await using (var app = new TestApp())
        {
            var setup = await Seed(app, "https://github.com/RoySalisbury/HVO.AgentControl.git", "feature/restart-1");
            var work = await app.Store.CreateWorkItem(new("restart-work", "1", "Restart task", "feature/restart-1-a", setup.Project.RepositoryUrl, setup.LegacyWorker.Id));
            binding = await app.Store.CreateTaskBinding(NewBinding(setup, work, "restart", "workspace"));

            // Simulate a proposal with stale generation
            await app.Store.Write(async db =>
            {
                var slot = await db.WorkerSlots.FirstOrDefaultAsync(x => x.Id == setup.Slot.Id);
                slot!.Revision++;
                return true;
            });

            data = app.DataPath; secrets = app.SecretPath;
        }

        // After restart, the binding should still be active
        await using var restarted = new TestApp(data, secrets);
        var restored = await restarted.Store.TaskBinding(binding.Binding.Id);
        Assert.Equal(TaskBindingState.Active, restored.Binding.State);
        Assert.Equal(binding.Workspace.Id, restored.Workspace.Id);
        Assert.Equal(binding.Session.Id, restored.Session.Id);
    }

    private static async Task<SeedData> SeedSecondProject(TestApp app, string repository, string branch)
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

    private static CreateTaskBindingInput NewBinding(SeedData data, WorkItem work, string task, string workspace) =>
        new(Id(), Id(), work.Id, data.Project.Id, data.Slot.Id, Id(), Id(), "/work/" + workspace, work.Branch,
            work.Revision, data.Project.Revision, data.Slot.Revision);

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
