using System.Net;
using System.Net.Http.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class RuntimeEnvironmentTests
{
    private static string Id() => Guid.NewGuid().ToString("N");
    private static Task<HostRecord> Host(ControlStore store) => store.CreateHost(new(Id(), Id(), "Docker host", "PhysicalMachine"));
    private static Task<ProjectRecord> Project(ControlStore store) => store.CreateProject(new(Id(), Id(), "Configuration source", "https://github.com/example/config"));
    private static ConfigureRuntimeEnvironmentInput Configure(RuntimeRecord runtime, HostRecord host, string kind = RuntimeEnvironmentKind.ManagedDevcontainer) =>
        new(Id(), 0, runtime.Revision, host.Id, kind);
    private static CreateWorkerInput Create(RuntimeRecord runtime) => new(Id(), runtime.Id, "Worker", "", "/home/agent/workspaces/" + Id(), "fixture", "deterministic");
    private static async Task Conflict<T>(Func<Task<T>> action, string code)
    {
        var error = await Assert.ThrowsAsync<InventoryException>(action);
        Assert.Equal(409, error.Status); Assert.Equal(code, error.Code);
    }

    private static async Task<ActiveBindingSetup> ActiveBinding(TestApp app)
    {
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var runtime = await app.Store.Read(async db => (await db.Runtimes.FindAsync(worker.RuntimeId))!);
        var host = await Host(app.Store);
        await app.Store.ConfigureRuntimeEnvironment(runtime.Id, Configure(runtime, host, RuntimeEnvironmentKind.ExistingMachine));
        var project = await Project(app.Store);
        var work = await app.Store.CreateWorkItem(new(Id(), "121", "Bound task", "feature/bound-task", project.RepositoryUrl, worker.Id));
        var slot = await app.Store.CreateWorkerSlot(new(Id(), Id(), runtime.Id, "bound-slot"));
        var binding = await app.Store.CreateTaskBinding(new(Id(), Id(), work.Id, project.Id, slot.Id, Id(), Id(), "/work/bound-task", work.Branch,
            work.Revision, project.Revision, slot.Revision));
        return new(worker, runtime, binding, work, await Host(app.Store));
    }

    private sealed record ActiveBindingSetup(WorkerRecord Worker, RuntimeRecord Runtime, TaskBindingView Binding, WorkItem Work, HostRecord ReplacementHost);

    [Fact]
    public async Task LegacyReadDoesNotInventHostingOrChangeSessions()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var runtime = await app.Store.Read(async db => (await db.Runtimes.FindAsync(worker.RuntimeId))!);
        var view = await app.Store.RuntimeEnvironment(runtime.Id);
        Assert.Equal(0, view.Revision); Assert.Equal(runtime.Revision, view.RuntimeRevision);
        Assert.Equal(RuntimeEnvironmentKind.LegacySsh, view.RequestedKind);
        Assert.Null(view.RequestedHostId); Assert.Null(view.MaxWorkerRegistrations); Assert.False(view.PlacementVerified);
        Assert.Equal(runtime.Capacity, view.ActiveTaskCapacity);
        Assert.Equal(0, await app.Store.Read(db => db.RuntimeEnvironments.CountAsync()));
        Assert.Equal(worker.NativeSessionId, (await app.Store.Read(async db => (await db.Workers.FindAsync(worker.Id))!)).NativeSessionId);
    }

    [Fact]
    public async Task ConfigurationIsRequestedMetadataWithProjectQualifiedSourceAndOneTaskCapacity()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var runtime = await app.Store.Read(async db => (await db.Runtimes.FindAsync(worker.RuntimeId))!);
        var host = await Host(app.Store); var project = await Project(app.Store);
        var input = Configure(runtime, host) with { ConfigurationProjectId = project.Id, DevcontainerPath = ".devcontainer/dotnet/devcontainer.json" };
        var result = await app.Store.ConfigureRuntimeEnvironment(runtime.Id, input);
        Assert.Equal(host.Id, result.RequestedHostId); Assert.Equal(project.Id, result.ConfigurationProjectId);
        Assert.Equal("Configured", result.State); Assert.False(result.PlacementVerified);
        Assert.Equal(1, result.MaxWorkerRegistrations); Assert.Equal(1, result.ActiveTaskCapacity);
        Assert.Equal(runtime.Revision + 1, result.RuntimeRevision);
        var saved = await app.Store.Read(async db => (await db.Workers.FindAsync(worker.Id))!);
        Assert.Equal(Json.Write(worker), Json.Write(saved));
        Assert.Equal(0, await app.Store.Read(db => db.Commands.CountAsync()));
        Assert.Equal("RuntimeEnvironment", (await app.Store.InventoryMutation(input.RequestId)).ResourceKind);
        Assert.DoesNotContain("fingerprint", Json.Write(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActiveTaskBindingBlocksEnvironmentChangesUntilTaskAndBindingAreReleased()
    {
        await using var app = new TestApp();
        var setup = await ActiveBinding(app);
        var before = await app.Store.RuntimeEnvironment(setup.Runtime.Id);

        await Conflict(() => app.Store.ConfigureRuntimeEnvironment(setup.Runtime.Id,
            new(Id(), before.Revision, before.RuntimeRevision, setup.ReplacementHost.Id, RuntimeEnvironmentKind.ExistingMachine)), "runtime_in_use");
        await Conflict(() => app.Store.ResetRuntimeEnvironment(setup.Runtime.Id,
            new(Id(), before.Revision, before.RuntimeRevision)), "runtime_in_use");
        Assert.Equal(before, await app.Store.RuntimeEnvironment(setup.Runtime.Id));
        Assert.Equal(TaskBindingState.Active, (await app.Store.TaskBinding(setup.Binding.Binding.Id)).Binding.State);

        await app.Store.ReleaseWorkItem(new(setup.Work.Id, setup.Worker.Id));
        await app.Store.ReleaseTaskBinding(setup.Binding.Binding.Id, new(Id(), setup.Binding.Binding.Revision));
        var configured = await app.Store.ConfigureRuntimeEnvironment(setup.Runtime.Id,
            new(Id(), before.Revision, before.RuntimeRevision, setup.ReplacementHost.Id, RuntimeEnvironmentKind.ExistingMachine));
        var reset = await app.Store.ResetRuntimeEnvironment(setup.Runtime.Id,
            new(Id(), configured.Revision, configured.RuntimeRevision));
        Assert.Equal(RuntimeEnvironmentKind.LegacySsh, reset.RequestedKind);
    }

    [Fact]
    public async Task EnvironmentApiReturnsConflictForActiveBindingAndSucceedsAfterRelease()
    {
        await using var app = new TestApp();
        var setup = await ActiveBinding(app);
        using var owner = await app.SignIn();
        var route = $"/api/v1/runtimes/{setup.Runtime.Id}/environment";
        var before = await app.Store.RuntimeEnvironment(setup.Runtime.Id);
        var configure = new ConfigureRuntimeEnvironmentInput(Id(), before.Revision, before.RuntimeRevision,
            setup.ReplacementHost.Id, RuntimeEnvironmentKind.ExistingMachine);

        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync(route, configure)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(route + "/reset",
            new ResetRuntimeEnvironmentInput(Id(), before.Revision, before.RuntimeRevision))).StatusCode);
        Assert.Equal(before, await owner.GetFromJsonAsync<RuntimeEnvironmentView>(route));

        await app.Store.ReleaseWorkItem(new(setup.Work.Id, setup.Worker.Id));
        await app.Store.ReleaseTaskBinding(setup.Binding.Binding.Id, new(Id(), setup.Binding.Binding.Revision));
        var response = await owner.PutAsJsonAsync(route, configure with { RequestId = Id() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = (await response.Content.ReadFromJsonAsync<RuntimeEnvironmentView>())!;
        Assert.Equal(setup.ReplacementHost.Id, saved.RequestedHostId);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(route + "/reset",
            new ResetRuntimeEnvironmentInput(Id(), saved.Revision, saved.RuntimeRevision))).StatusCode);
    }

    [Fact]
    public async Task ProjectApiRejectsArchiveWhileTaskBindingIsActiveAndAllowsItAfterRelease()
    {
        await using var app = new TestApp();
        var setup = await ActiveBinding(app);
        using var owner = await app.SignIn();
        var route = $"/api/v1/projects/{setup.Binding.Project.Id}/archive";
        var before = await app.Store.Project(setup.Binding.Project.Id);
        var receiptsBefore = await app.Store.Read(db => db.InventoryMutations.CountAsync());

        var rejected = await owner.PostAsJsonAsync(route, new ArchiveInventoryInput(Id(), setup.Binding.Project.Revision));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        var afterRejected = await app.Store.Project(setup.Binding.Project.Id);
        Assert.Equal(before.Id, afterRejected.Id);
        Assert.Equal(before.Revision, afterRejected.Revision);
        Assert.Equal(before.Archived, afterRejected.Archived);
        Assert.Equal(before.Name, afterRejected.Name);
        Assert.Equal(before.RepositoryUrl, afterRejected.RepositoryUrl);
        Assert.Equal(before.BaseBranch, afterRejected.BaseBranch);
        Assert.Equal(before.Description, afterRejected.Description);
        Assert.Equal(before.UpdatedAt, afterRejected.UpdatedAt);
        Assert.Equal(receiptsBefore, await app.Store.Read(db => db.InventoryMutations.CountAsync()));
        Assert.Equal(TaskBindingState.Active, (await app.Store.TaskBinding(setup.Binding.Binding.Id)).Binding.State);

        await app.Store.ReleaseWorkItem(new(setup.Work.Id, setup.Worker.Id));
        await app.Store.ReleaseTaskBinding(setup.Binding.Binding.Id, new(Id(), setup.Binding.Binding.Revision));
        var archived = await owner.PostAsJsonAsync(route, new ArchiveInventoryInput(Id(), setup.Binding.Project.Revision));
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        Assert.True((await app.Store.Project(setup.Binding.Project.Id)).Archived);
    }

    [Fact]
    public async Task LegacyRuntimeKeyCasingIsPreservedWithoutRedirectingAnAssociationOrReceipt()
    {
        await using var app = new TestApp(); using var owner = await app.SignIn(); var host = await Host(app.Store);
        string[] keys = ["AABBCCDD00112233445566778899AABB", "aabbccdd00112233445566778899aabb", new('0', 32)];
        foreach (var key in keys)
        {
            var profile = PersistenceTests.Profile(); profile.Id = key;
            var runtime = await app.Store.SaveRuntime(profile);
            var route = $"/api/v1/runtimes/{key}/environment";
            var legacy = await owner.GetFromJsonAsync<RuntimeEnvironmentView>(route);
            Assert.Equal(key, legacy!.RuntimeId); Assert.Equal(0, legacy.Revision);
            var input = Configure(runtime, host);
            var response = await owner.PutAsJsonAsync(route, input);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var configured = (await response.Content.ReadFromJsonAsync<RuntimeEnvironmentView>())!;
            Assert.Equal(key, configured.RuntimeId); Assert.Equal(host.Id, configured.RequestedHostId);
            Assert.Equal(key, (await app.Store.InventoryMutation(input.RequestId)).ResourceId);
            Assert.Equal(configured, await app.Store.ConfigureRuntimeEnvironment(key, input));
        }
        var first = await app.Store.HostRuntimeEnvironments(host.Id, take: 2);
        Assert.Equal(new[] { keys[2], keys[0] }, first.Items.Select(x => x.RuntimeId));
        Assert.Equal(keys[0], first.NextAfter);
        Assert.Equal(keys[1], Assert.Single((await app.Store.HostRuntimeEnvironments(host.Id, first.NextAfter)).Items).RuntimeId);
        var upper = await app.Store.RuntimeEnvironment(keys[0]);
        await app.Store.ResetRuntimeEnvironment(keys[0], new(Id(), upper.Revision, upper.RuntimeRevision));
        Assert.Equal("Configured", (await app.Store.RuntimeEnvironment(keys[1])).State);
    }

    [Fact]
    public async Task BothRevisionsAndRequestIntentAreRequiredAndConcurrentEditsCommitOnce()
    {
        await using var app = new TestApp();
        var runtime = await app.Store.SaveRuntime(PersistenceTests.Profile()); var host = await Host(app.Store);
        var input = Configure(runtime, host);
        await Conflict(() => app.Store.ConfigureRuntimeEnvironment(runtime.Id, input with { ExpectedRuntimeRevision = runtime.Revision + 1 }), "revision_conflict");
        await Conflict(() => app.Store.ConfigureRuntimeEnvironment(runtime.Id, input with { ExpectedRevision = 1 }), "revision_conflict");
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try { return await app.Store.ConfigureRuntimeEnvironment(runtime.Id, input with { RequestId = Id() }); }
            catch (InventoryException ex) { Assert.Equal("revision_conflict", ex.Code); return null; }
        }));
        var result = Assert.Single(results, x => x is not null)!;
        Assert.Equal(1, result.Revision);
        Assert.Equal(1, await app.Store.Read(db => db.InventoryMutations.CountAsync(x => x.ResourceKind == "RuntimeEnvironment")));
        var current = input with { RequestId = Id(), ExpectedRevision = result.Revision, ExpectedRuntimeRevision = result.RuntimeRevision };
        await app.Store.ConfigureRuntimeEnvironment(runtime.Id, current);
        await Conflict(() => app.Store.ConfigureRuntimeEnvironment(runtime.Id, current with { Kind = RuntimeEnvironmentKind.ExistingMachine }), "idempotency_conflict");
    }

    [Fact]
    public async Task ResetRetainsRevisionAndRestartReplayCannotUndoResetOrResurrectDeletedRuntime()
    {
        string data, secrets;
        RuntimeRecord runtime; HostRecord host;
        ConfigureRuntimeEnvironmentInput input;
        ResetRuntimeEnvironmentInput reset;
        RuntimeEnvironmentView configured, cleared;
        await using (var app = new TestApp())
        {
            runtime = await app.Store.SaveRuntime(PersistenceTests.Profile()); host = await Host(app.Store);
            input = Configure(runtime, host);
            configured = await app.Store.ConfigureRuntimeEnvironment(runtime.Id, input);
            reset = new(Id(), configured.Revision, configured.RuntimeRevision);
            cleared = await app.Store.ResetRuntimeEnvironment(runtime.Id, reset);
            Assert.Equal(2, cleared.Revision); Assert.Null(cleared.RequestedHostId);
            Assert.Equal(1, cleared.ActiveTaskCapacity); Assert.Null(cleared.MaxWorkerRegistrations);
            await Conflict(() => app.Store.ConfigureRuntimeEnvironment(runtime.Id, input with { RequestId = Id(), ExpectedRuntimeRevision = cleared.RuntimeRevision }), "revision_conflict");
            data = app.DataPath; secrets = app.SecretPath;
        }
        await using var restarted = new TestApp(data, secrets);
        Assert.Equal(configured, await restarted.Store.ConfigureRuntimeEnvironment(runtime.Id, input));
        Assert.Equal(cleared, await restarted.Store.ResetRuntimeEnvironment(runtime.Id, reset));
        Assert.Equal(cleared, await restarted.Store.RuntimeEnvironment(runtime.Id));
        await restarted.Store.DeleteRuntime(runtime.Id, new(Id(), cleared.RuntimeRevision));
        Assert.Equal(0, await restarted.Store.Read(db => db.RuntimeEnvironments.CountAsync()));
        Assert.Equal(configured, await restarted.Store.ConfigureRuntimeEnvironment(runtime.Id, input));
        Assert.Equal(cleared, await restarted.Store.ResetRuntimeEnvironment(runtime.Id, reset));
        Assert.Equal(0, await restarted.Store.Read(db => db.Runtimes.CountAsync()));
        Assert.Equal(2, await restarted.Store.Read(db => db.InventoryMutations.CountAsync(x => x.ResourceKind == "RuntimeEnvironment")));
        await restarted.Store.ArchiveHost(host.Id, new(Id(), host.Revision));
    }

    [Theory]
    [InlineData("connected")]
    [InlineData("desired")]
    [InlineData("reconnecting")]
    [InlineData("terminal")]
    [InlineData("claim")]
    [InlineData(Delivery.Queued)]
    [InlineData(Delivery.Dispatching)]
    [InlineData(Delivery.Accepted)]
    [InlineData(Delivery.Running)]
    [InlineData(Delivery.Unknown)]
    [InlineData("pending-request")]
    [InlineData("uncertain-request")]
    [InlineData("work-item")]
    [InlineData("coordination-worker")]
    [InlineData("coordination-controller")]
    public async Task ConfigureAndResetRejectOutstandingWorkAndConnections(string blocker)
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var runtime = await app.Store.Read(async db => (await db.Runtimes.FindAsync(worker.RuntimeId))!);
        var host = await Host(app.Store);
        await app.Store.Write(async db =>
        {
            var record = (await db.Runtimes.FindAsync(runtime.Id))!;
            switch (blocker)
            {
                case "connected": record.Transport = "Connected"; break;
                case "desired": record.DesiredConnected = true; break;
                case "reconnecting": record.Transport = "Reconnecting"; break;
                case "claim": db.WorkspaceClaims.Add(new() { Id = Id(), RuntimeId = runtime.Id, CommandId = Id(), Directory = "/pending" }); break;
                case "pending-request": case "uncertain-request": db.Requests.Add(new() { WorkerId = worker.Id, NativeId = Id(), State = blocker == "pending-request" ? "Pending" : "ReplyUnknown" }); break;
                case "work-item": db.WorkItems.Add(new() { Id = Id(), OwnerWorkerId = worker.Id, Repository = "repo", Branch = "branch", State = WorkItemState.Active }); break;
                case "coordination-worker": db.CoordinationRuns.Add(new() { Id = Id(), WorkerIdsJson = Json.Write(new[] { worker.Id }), State = "Paused" }); break;
                case "coordination-controller": db.CoordinationRuns.Add(new() { Id = Id(), CoordinatorWorkerId = worker.Id, State = "Waiting" }); break;
                case "terminal": break;
                default: db.Commands.Add(new() { RuntimeId = runtime.Id, Kind = "Prompt", State = blocker }); break;
            }
            return true;
        });
        if (blocker == "terminal") await app.Store.AcquireTerminalRuntime(runtime.Id, CancellationToken.None);
        var error = await Assert.ThrowsAsync<InventoryException>(() => app.Store.ConfigureRuntimeEnvironment(runtime.Id, Configure(runtime, host)));
        Assert.Equal(409, error.Status);
        await Assert.ThrowsAsync<InventoryException>(() => app.Store.ResetRuntimeEnvironment(runtime.Id, new(Id(), 0, runtime.Revision)));
        Assert.Equal(0, await app.Store.Read(db => db.RuntimeEnvironments.CountAsync()));
        if (blocker == "terminal") await app.Store.ReleaseTerminalRuntime(runtime.Id);
    }

    [Fact]
    public async Task ReferencesBlockArchiveAndArchivedResourcesBlockNewAssociations()
    {
        await using var app = new TestApp();
        var runtime = await app.Store.SaveRuntime(PersistenceTests.Profile()); var host = await Host(app.Store); var project = await Project(app.Store);
        var input = Configure(runtime, host) with { ConfigurationProjectId = project.Id, DevcontainerPath = ".devcontainer/devcontainer.json" };
        var view = await app.Store.ConfigureRuntimeEnvironment(runtime.Id, input);
        await Conflict(() => app.Store.ArchiveHost(host.Id, new(Id(), host.Revision)), "resource_in_use");
        await Conflict(() => app.Store.ArchiveProject(project.Id, new(Id(), project.Revision)), "resource_in_use");
        var reset = await app.Store.ResetRuntimeEnvironment(runtime.Id, new(Id(), view.Revision, view.RuntimeRevision));
        var archived = await app.Store.ArchiveProject(project.Id, new(Id(), project.Revision));
        var next = input with { RequestId = Id(), ExpectedRevision = reset.Revision, ExpectedRuntimeRevision = reset.RuntimeRevision };
        await Conflict(() => app.Store.ConfigureRuntimeEnvironment(runtime.Id, next), "resource_archived");
        await app.Store.ArchiveProject(project.Id, new(Id(), archived.Revision, false));
        await app.Store.ArchiveHost(host.Id, new(Id(), host.Revision));
        await Conflict(() => app.Store.ConfigureRuntimeEnvironment(runtime.Id, next), "resource_archived");
    }

    [Fact]
    public async Task ManagedAdmissionSerializesCompetingCreatesAndIdenticalRetriesKeepOriginalCommand()
    {
        await using var app = new TestApp();
        var runtime = await app.Store.SaveRuntime(PersistenceTests.Profile()); var host = await Host(app.Store);
        await app.Store.ConfigureRuntimeEnvironment(runtime.Id, Configure(runtime, host));
        var inputs = new[] { Create(runtime), Create(runtime) };
        var results = await Task.WhenAll(inputs.Select(async input =>
        {
            try { return await app.Store.CreateWorker(input); }
            catch (ControlException ex) { Assert.Contains("managed devcontainer", ex.Message); return null; }
        }));
        var accepted = Assert.Single(results, x => x is not null)!;
        var original = inputs.Single(x => x.Id == accepted.Id);
        Assert.Equal(accepted.Id, (await app.Store.CreateWorker(original)).Id);
        await app.Store.Write(async db => { (await db.Commands.FindAsync(accepted.Id))!.State = Delivery.Unknown; return true; });
        await Assert.ThrowsAsync<ControlException>(() => app.Store.CreateWorker(Create(runtime)));
        Assert.Equal(Delivery.Unknown, (await app.Store.CreateWorker(original)).State);
        await app.Store.EditQueue(accepted.Id, "resolveUnknown");
        Assert.Equal(Delivery.Queued, (await app.Store.CreateWorker(Create(runtime))).State);
    }

    [Theory]
    [InlineData(false, SessionRoles.Worker)]
    [InlineData(true, SessionRoles.Worker)]
    [InlineData(true, SessionRoles.Coordinator)]
    public async Task ExistingWorkerIncludingArchivedCoordinatorOccupiesManagedSlot(bool archived, string role)
    {
        await using var app = new TestApp(); var worker = await PersistenceTests.SeedWorker(app.Store);
        await app.Store.Write(async db => { var saved = (await db.Workers.FindAsync(worker.Id))!; saved.Archived = archived; saved.Role = role; return true; });
        var runtime = await app.Store.Read(async db => (await db.Runtimes.FindAsync(worker.RuntimeId))!);
        await app.Store.ConfigureRuntimeEnvironment(runtime.Id, Configure(runtime, await Host(app.Store)));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.CreateWorker(Create(runtime)));
        await app.Store.DeleteWorker(worker.Id, new(Id(), worker.SettingsRevision));
        Assert.Equal(Delivery.Queued, (await app.Store.CreateWorker(Create(runtime))).State);
    }

    [Fact]
    public async Task ManagedKindRejectsMultipleLegacyWorkersAndLegacyOrPhysicalKeepSharedCapacity()
    {
        await using var app = new TestApp(); var worker = await PersistenceTests.SeedWorker(app.Store);
        var runtime = await app.Store.Read(async db => (await db.Runtimes.FindAsync(worker.RuntimeId))!); var host = await Host(app.Store);
        await app.Store.Write(db => { db.Workers.Add(new() { RuntimeId = runtime.Id, NativeSessionId = "ses_second", Directory = "/home/agent/workspaces/b" }); return Task.FromResult(true); });
        await Conflict(() => app.Store.ConfigureRuntimeEnvironment(runtime.Id, Configure(runtime, host)), "worker_limit");
        var legacy = await app.Store.CreateWorker(Create(runtime));
        await app.Store.EditQueue(legacy.Id, "cancel");
        var physical = await app.Store.ConfigureRuntimeEnvironment(runtime.Id, Configure(runtime, host, RuntimeEnvironmentKind.ExistingMachine));
        Assert.Null(physical.MaxWorkerRegistrations); Assert.Equal(2, physical.ActiveTaskCapacity);
        Assert.Equal(Delivery.Queued, (await app.Store.CreateWorker(Create(runtime))).State);
    }

    [Fact]
    public async Task FailedSetupClaimHoldsSlotUntilDismissedAndProfileSaveCannotBypassCapacity()
    {
        await using var app = new TestApp();
        var runtime = await app.Store.SaveRuntime(PersistenceTests.Profile());
        await app.Store.ConfigureRuntimeEnvironment(runtime.Id, Configure(runtime, await Host(app.Store)));
        var setup = await app.Store.CreateWorker(Create(runtime));
        await app.Store.Write(async db =>
        {
            (await db.Commands.FindAsync(setup.Id))!.State = Delivery.Failed;
            db.WorkspaceClaims.Add(new() { Id = Id(), RuntimeId = runtime.Id, CommandId = setup.Id, Directory = "/failed" });
            return true;
        });
        await Assert.ThrowsAsync<ControlException>(() => app.Store.CreateWorker(Create(runtime)));
        await app.Store.DismissCreation(setup.Id);
        Assert.Equal(Delivery.Queued, (await app.Store.CreateWorker(Create(runtime))).State);
        var current = await app.Store.Read(async db => (await db.Runtimes.FindAsync(runtime.Id))!);
        current.Capacity = 2;
        await Assert.ThrowsAsync<ControlException>(() => app.Store.SaveRuntime(current));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.SaveRuntimeForSetup(current, Id()));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.SaveRuntimeAndConnect(current, Id()));
        Assert.Equal(1, (await app.Store.RuntimeEnvironment(runtime.Id)).ActiveTaskCapacity);
    }

    [Fact]
    public async Task ChangedConnectionIsExplicitlyStaleButDisplayAndTransportHealthAreNotPlacementEvidence()
    {
        await using var app = new TestApp(); var runtime = await app.Store.SaveRuntime(PersistenceTests.Profile());
        await app.Store.ConfigureRuntimeEnvironment(runtime.Id, Configure(runtime, await Host(app.Store)));
        var current = await app.Store.Read(async db => (await db.Runtimes.FindAsync(runtime.Id))!);
        current.Name = "Renamed"; current.Labels = "display";
        current = await app.Store.SaveRuntime(current);
        await app.Store.Write(async db => { var saved = (await db.Runtimes.FindAsync(runtime.Id))!; saved.Generation++; saved.Health = "Healthy"; return true; });
        Assert.Equal("Configured", (await app.Store.RuntimeEnvironment(runtime.Id)).State);
        current.Host = "other-host";
        await app.Store.SaveRuntime(current);
        var view = await app.Store.RuntimeEnvironment(runtime.Id);
        Assert.Equal("ConnectionProfileChanged", view.State); Assert.False(view.PlacementVerified);
        Assert.Equal(1, view.MaxWorkerRegistrations);
    }

    [Fact]
    public async Task HostMembershipPaginationReturnsEveryConfiguredRuntimeAndRejectsBadBounds()
    {
        await using var app = new TestApp(); var host = await Host(app.Store); var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var runtime = await app.Store.SaveRuntime(PersistenceTests.Profile()); ids.Add(runtime.Id);
            await app.Store.ConfigureRuntimeEnvironment(runtime.Id, Configure(runtime, host));
        }
        ids.Sort(StringComparer.Ordinal);
        var first = await app.Store.HostRuntimeEnvironments(host.Id, take: 1);
        Assert.Equal(ids[0], Assert.Single(first.Items).RuntimeId); Assert.Equal(ids[0], first.NextAfter);
        var rest = await app.Store.HostRuntimeEnvironments(host.Id, first.NextAfter, take: 2);
        Assert.Equal(ids.Skip(1), rest.Items.Select(x => x.RuntimeId)); Assert.Null(rest.NextAfter);
        await Assert.ThrowsAsync<InventoryException>(() => app.Store.HostRuntimeEnvironments(host.Id, take: 101));
        await Assert.ThrowsAsync<InventoryException>(() => app.Store.HostRuntimeEnvironments(host.Id, after: "not-an-id"));
    }

    [Theory]
    [InlineData("../devcontainer.json")]
    [InlineData("/tmp/devcontainer.json")]
    [InlineData(".devcontainer//devcontainer.json")]
    [InlineData(".devcontainer/./devcontainer.json")]
    [InlineData("C:\\devcontainer.json")]
    [InlineData(".devcontainer/another.json")]
    [InlineData(".devcontainer/\n/devcontainer.json")]
    public async Task InvalidConfigurationPathsLeaveNoAssociation(string path)
    {
        await using var app = new TestApp(); var runtime = await app.Store.SaveRuntime(PersistenceTests.Profile()); var project = await Project(app.Store);
        var input = Configure(runtime, await Host(app.Store)) with { ConfigurationProjectId = project.Id, DevcontainerPath = path };
        Assert.Equal("validation", (await Assert.ThrowsAsync<InventoryException>(() => app.Store.ConfigureRuntimeEnvironment(runtime.Id, input))).Code);
        Assert.Equal(0, (await app.Store.RuntimeEnvironment(runtime.Id)).Revision);
    }

    [Fact]
    public async Task RealOwnerRoutesRequireAuthCsrfAndRejectInventedVerificationAndIncompleteSources()
    {
        await using var app = new TestApp(); var runtime = await app.Store.SaveRuntime(PersistenceTests.Profile()); var host = await Host(app.Store);
        var route = $"/api/v1/runtimes/{runtime.Id}/environment"; var input = Configure(runtime, host);
        using var anonymous = app.CreateClient(new() { AllowAutoRedirect = false }); using var owner = await app.SignIn();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(route, input)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(route + "/reset", new ResetRuntimeEnvironmentInput(Id(), 0, runtime.Revision))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/hosts/{host.Id}/runtimes")).StatusCode);
        owner.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync(route, input)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(route + "/reset", new ResetRuntimeEnvironmentInput(Id(), 0, runtime.Revision))).StatusCode);
        using var authorized = await app.SignIn();
        var spoofed = Json.Read<Dictionary<string, object>>(Json.Write(input)); spoofed["placementVerified"] = true;
        Assert.Equal(HttpStatusCode.BadRequest, (await authorized.PutAsJsonAsync(route, spoofed)).StatusCode);
        foreach (var invalid in new[] { input with { Kind = "Docker" }, input with { ConfigurationProjectId = Id() }, input with { DevcontainerPath = "devcontainer.json" } })
            Assert.Equal(HttpStatusCode.BadRequest, (await authorized.PutAsJsonAsync(route, invalid)).StatusCode);
        var response = await authorized.PutAsJsonAsync(route, input);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = (await response.Content.ReadFromJsonAsync<RuntimeEnvironmentView>())!;
        Assert.Equal(saved, await authorized.GetFromJsonAsync<RuntimeEnvironmentView>(route));
        Assert.Equal(saved, await (await authorized.PutAsJsonAsync(route, input)).Content.ReadFromJsonAsync<RuntimeEnvironmentView>());
        Assert.Equal(HttpStatusCode.Conflict, (await authorized.PutAsJsonAsync(route, input with { RequestId = Id() })).StatusCode);
        Assert.Single((await authorized.GetFromJsonAsync<RuntimeEnvironmentPage>($"/api/v1/hosts/{host.Id}/runtimes?take=1"))!.Items);
        Assert.Equal(HttpStatusCode.OK, (await authorized.PostAsJsonAsync(route + "/reset", new ResetRuntimeEnvironmentInput(Id(), saved.Revision, saved.RuntimeRevision))).StatusCode);
    }

    [Fact]
    public async Task MigrationPreservesExistingInventorySequencesReceiptsAndEnforcesForeignKeys()
    {
        await using var app = new TestApp();
        var options = new DbContextOptionsBuilder<ControlDb>().UseSqlite($"Data Source={Path.Combine(app.DataPath, "agentcontrol.db")}").Options;
        var host = new HostRecord { Id = Id(), Sequence = 14, Name = "Retained host", Revision = 4, Archived = true };
        var project = new ProjectRecord { Id = Id(), Sequence = 28, Name = "Retained project", RepositoryUrl = "https://github.com/example/kept", Revision = 6 };
        var receipt = new InventoryMutationReceipt { RequestId = Id(), ResourceKind = "Host", ResourceId = host.Id, ResultJson = Json.Write(host) };
        var runtime = PersistenceTests.Profile();
        await using (var db = new ControlDb(options))
        {
            var previous = db.Database.GetMigrations().Single(x => x.EndsWith("HostProjectInventory", StringComparison.Ordinal));
            await db.GetService<IMigrator>().MigrateAsync(previous);
            db.Hosts.Add(host); db.Projects.Add(project); db.InventoryMutations.Add(receipt);
            var properties = db.Entry(runtime).Metadata.GetProperties().Where(x => x.Name != nameof(RuntimeRecord.ConnectionKind)).ToArray();
            var columns = string.Join(",", properties.Select(x => "\"" + x.Name + "\""));
            var placeholders = string.Join(",", properties.Select((_, index) => "{" + index + "}"));
            var values = properties.Select(x => x.PropertyInfo!.GetValue(runtime)!).ToArray();
            var historicalInsert = "INSERT INTO Runtimes (" + columns + ") VALUES (" + placeholders + ")";
            await db.Database.ExecuteSqlRawAsync(historicalInsert, values);
            await db.SaveChangesAsync();
        }
        Assert.Equal(Json.Write(host), Json.Write(await app.Store.Host(host.Id)));
        Assert.Equal(Json.Write(project), Json.Write(await app.Store.Project(project.Id)));
        Assert.Equal(receipt.ResultJson, await app.Store.Read(async db => (await db.InventoryMutations.FindAsync(receipt.RequestId))!.ResultJson));
        Assert.True((await Host(app.Store)).Sequence > host.Sequence);
        Assert.Equal(0, (await app.Store.RuntimeEnvironment(runtime.Id)).Revision);
        await Assert.ThrowsAsync<DbUpdateException>(() => app.Store.Write(db =>
        {
            db.RuntimeEnvironments.Add(new() { RuntimeId = runtime.Id, HostId = Id() }); return Task.FromResult(true);
        }));
        await app.Store.Write(db => { db.RuntimeEnvironments.Add(new() { RuntimeId = runtime.Id, HostId = host.Id, ConfigurationProjectId = project.Id }); return Task.FromResult(true); });
        await Assert.ThrowsAsync<DbUpdateException>(() => app.Store.Write(async db => { db.Hosts.Remove((await db.Hosts.SingleAsync(x => x.Id == host.Id))); return true; }));
        await Assert.ThrowsAsync<DbUpdateException>(() => app.Store.Write(async db => { db.Projects.Remove((await db.Projects.SingleAsync(x => x.Id == project.Id))); return true; }));
        Assert.Empty(await app.Store.Read(async db => await db.Database.GetPendingMigrationsAsync()));
    }
}
