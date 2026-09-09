using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class RegistrationLifecycleTests
{
    [Fact]
    public async Task DuplicateWorkspaceNamesItsOwnerAndDeletionAllowsReplacement()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var input = new CreateWorkerInput(Guid.NewGuid().ToString(), worker.RuntimeId, "Replacement", "", worker.Directory + "/", "fixture", "deterministic");
        var failure = await Assert.ThrowsAsync<ControlException>(() => app.Store.CreateWorker(input));
        Assert.Contains(worker.Name, failure.Message);
        Assert.Contains("dismissing a setup card does not delete", failure.Message);
        await app.Store.Write(async db =>
        {
            var saved = (await db.Workers.FindAsync(worker.Id))!;
            saved.Stale = false; saved.Activity = "Idle"; saved.LastObservedAt = ControlStore.Now; saved.Archived = true;
            return true;
        });
        Assert.Contains("archived", (await Assert.ThrowsAsync<ControlException>(() => app.Store.CreateWorker(input))).Message);
        await app.Store.DeleteWorker(worker.Id, new(Guid.NewGuid().ToString(), worker.SettingsRevision));
        var replacement = await app.Store.CreateWorker(input);
        Assert.Equal(Delivery.Queued, replacement.State);
        Assert.Equal(replacement.Id, (await app.Store.CreateWorker(input)).Id);
    }

    [Fact]
    public void CurrentStatusDoesNotPresentHistoricalOutcomeAsActivity()
    {
        var worker = new WorkerRecord { Activity = "Idle", Stale = false, Outcome = "NeedsReview", CurrentAction = "bash: error" };
        Assert.Equal("Ready", DisplayStatus.Worker(worker));
        worker.Stale = true;
        Assert.Equal("Reconnecting", DisplayStatus.Worker(worker));
        worker.Stale = false; worker.Activity = "WaitingPermission";
        Assert.Equal("Approval needed", DisplayStatus.Worker(worker));
    }

    [Fact]
    public async Task CreationCardsSurviveRestartBeyondRecentCommandWindowAndCanBeDismissed()
    {
        string data, secrets, id;
        await using (var app = new TestApp())
        {
            data = app.DataPath; secrets = app.SecretPath;
            var runtime = await app.Store.SaveRuntime(PersistenceTests.Profile());
            var command = await app.Store.CreateWorker(new(Guid.NewGuid().ToString(), runtime.Id, "Pending worker", "project", "/home/agent/workspaces/a", "fixture", "deterministic"));
            id = command.Id;
            await app.Store.Write(db =>
            {
                for (var i = 0; i < 510; i++) db.Commands.Add(new() { Id = Guid.NewGuid().ToString(), RuntimeId = runtime.Id, Kind = "InspectWorkspace", State = Delivery.Finished, CreatedAt = command.CreatedAt + i + 1 });
                return Task.FromResult(true);
            });
            Assert.Contains((await app.Store.Snapshot()).Commands, x => x.Id == id);
        }
        await using var restarted = new TestApp(data, secrets);
        Assert.Contains((await restarted.Store.Snapshot()).Commands, x => x.Id == id);
        await Assert.ThrowsAsync<ControlException>(() => restarted.Store.DismissCreation(id));
        await restarted.Store.Write(async db => { (await db.Commands.FindAsync(id))!.State = Delivery.Failed; return true; });
        await restarted.Store.DismissCreation(id);
        Assert.DoesNotContain((await restarted.Store.Snapshot()).Commands, x => x.Id == id && !x.Dismissed);
        Assert.True((await restarted.Store.Read(db => db.Commands.SingleAsync(x => x.Id == id))).Dismissed);
    }

    [Fact]
    public async Task WorkerDeletionRejectsBusyAndReferencedSessionsThenReleasesClaimIdempotently()
    {
        await using var app = new TestApp(); var worker = await PersistenceTests.SeedWorker(app.Store);
        var input = new DeleteRegistrationInput(Guid.NewGuid().ToString(), worker.SettingsRevision);
        await app.Store.Write(async db =>
        {
            var runtime = (await db.Runtimes.FindAsync(worker.RuntimeId))!;
            runtime.DesiredConnected = true; runtime.Transport = "Connecting";
            return true;
        });
        await Assert.ThrowsAsync<ControlException>(() => app.Store.DeleteWorker(worker.Id, input));
        var run = new CoordinationRun { Id = Guid.NewGuid().ToString(), WorkerIdsJson = Json.Write(new[] { worker.Id }) };
        await app.Store.Write(async db =>
        {
            var saved = (await db.Workers.FindAsync(worker.Id))!; saved.Stale = false; saved.Activity = "Idle"; saved.LastObservedAt = ControlStore.Now;
            db.CoordinationRuns.Add(run);
            db.WorkspaceClaims.Add(new() { Id = "claim", WorkerId = worker.Id, RuntimeId = worker.RuntimeId, Directory = worker.Directory });
            return true;
        });
        await Assert.ThrowsAsync<ControlException>(() => app.Store.DeleteWorker(worker.Id, input));
        await app.Store.Write(async db => { (await db.CoordinationRuns.FindAsync(run.Id))!.State = "Stopped"; return true; });
        var queued = await app.Store.Prompt(worker.Id, new(Guid.NewGuid().ToString(), "task", worker.Revision,
            RiskLevel: TaskRiskLevels.Low));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.DeleteWorker(worker.Id, input));
        await app.Store.EditQueue(queued.Id, "cancel");
        var deleted = await app.Store.DeleteWorker(worker.Id, input);
        Assert.Equal(deleted.Id, (await app.Store.DeleteWorker(worker.Id, input)).Id);
        Assert.Empty((await app.Store.Snapshot()).Workers);
        Assert.False(await app.Store.Read(db => db.WorkspaceClaims.AnyAsync()));
        Assert.Contains((await app.Store.Snapshot()).Commands, x => x.Id == queued.Id);
    }

    [Fact]
    public async Task WorkerDeletionAllowsStaleRegistrationWhenRuntimeIsExplicitlyDisconnected()
    {
        await using var app = new TestApp(); var worker = await PersistenceTests.SeedWorker(app.Store);
        await app.Store.Write(async db =>
        {
            var runtime = (await db.Runtimes.FindAsync(worker.RuntimeId))!;
            runtime.DesiredConnected = false; runtime.Transport = "Disconnected";
            db.WorkspaceClaims.Add(new() { Id = "offline-claim", WorkerId = worker.Id, RuntimeId = worker.RuntimeId, Directory = worker.Directory });
            db.Messages.Add(new() { WorkerId = worker.Id, NativeId = "cached", Role = "assistant" });
            db.Assignments.Add(new() { Id = "cached-assignment", WorkerId = worker.Id });
            db.Requests.Add(new() { WorkerId = worker.Id, NativeId = "answered", Kind = "question", State = "Answered" });
            return true;
        });
        var input = new DeleteRegistrationInput(Guid.NewGuid().ToString(), worker.SettingsRevision);

        var deleted = await app.Store.DeleteWorker(worker.Id, input);
        var retried = await app.Store.DeleteWorker(worker.Id, input);

        Assert.Equal(deleted.Id, retried.Id);
        Assert.Equal(Delivery.Finished, deleted.State);
        Assert.Contains("detached work may continue", deleted.Detail);
        Assert.Empty((await app.Store.Snapshot()).Workers);
        Assert.NotNull(await app.Store.Read(db => db.Runtimes.FindAsync(worker.RuntimeId).AsTask()));
        Assert.False(await app.Store.Read(db => db.WorkspaceClaims.AnyAsync(x => x.WorkerId == worker.Id)));
        Assert.False(await app.Store.Read(db => db.Messages.AnyAsync(x => x.WorkerId == worker.Id)));
        Assert.False(await app.Store.Read(db => db.Assignments.AnyAsync(x => x.WorkerId == worker.Id)));
        Assert.False(await app.Store.Read(db => db.Requests.AnyAsync(x => x.WorkerId == worker.Id)));
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Kind is "Abort" or "StopManagedServer");
    }

    [Theory]
    [InlineData(Delivery.Queued)]
    [InlineData(Delivery.Dispatching)]
    [InlineData(Delivery.Accepted)]
    [InlineData(Delivery.Running)]
    [InlineData(Delivery.Unknown)]
    public async Task OfflineWorkerDeletionStillRejectsUnresolvedDelivery(string state)
    {
        await using var app = new TestApp(); var worker = await PersistenceTests.SeedWorker(app.Store);
        await app.Store.Write(db =>
        {
            db.Commands.Add(new() { Id = Guid.NewGuid().ToString(), RuntimeId = worker.RuntimeId, WorkerId = worker.Id, Kind = "Prompt", State = state });
            return Task.FromResult(true);
        });

        var failure = await Assert.ThrowsAsync<ControlException>(() => app.Store.DeleteWorker(worker.Id,
            new(Guid.NewGuid().ToString(), worker.SettingsRevision)));

        Assert.Contains("queued work", failure.Message);
        Assert.NotNull(await app.Store.Read(db => db.Workers.FindAsync(worker.Id).AsTask()));
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("ReplyUnknown")]
    public async Task OfflineWorkerDeletionStillRejectsPendingInteractions(string state)
    {
        await using var app = new TestApp(); var worker = await PersistenceTests.SeedWorker(app.Store);
        await app.Store.Write(db =>
        {
            db.Requests.Add(new() { WorkerId = worker.Id, NativeId = Guid.NewGuid().ToString(), Kind = "question", State = state });
            return Task.FromResult(true);
        });

        var failure = await Assert.ThrowsAsync<ControlException>(() => app.Store.DeleteWorker(worker.Id,
            new(Guid.NewGuid().ToString(), worker.SettingsRevision)));

        Assert.Contains("pending requests", failure.Message);
    }

    [Fact]
    public async Task OfflineWorkerDeletionStillRejectsUnfinishedCoordination()
    {
        await using var app = new TestApp(); var worker = await PersistenceTests.SeedWorker(app.Store);
        await app.Store.Write(db =>
        {
            db.CoordinationRuns.Add(new() { Id = Guid.NewGuid().ToString(), WorkerIdsJson = Json.Write(new[] { worker.Id }) });
            return Task.FromResult(true);
        });

        var failure = await Assert.ThrowsAsync<ControlException>(() => app.Store.DeleteWorker(worker.Id,
            new(Guid.NewGuid().ToString(), worker.SettingsRevision)));

        Assert.Contains("unfinished coordination", failure.Message);
    }

    [Fact]
    public async Task WorkerDeletionRejectsActiveWorkItemOwnershipButAllowsTerminalHistory()
    {
        await using var app = new TestApp(); var worker = await PersistenceTests.SeedWorker(app.Store);
        var workItem = await app.Store.CreateWorkItem(new("offline-owned", "113", "Offline ownership", "fix/offline", "HVO.AgentControl", worker.Id));
        var input = new DeleteRegistrationInput(Guid.NewGuid().ToString(), worker.SettingsRevision);

        var failure = await Assert.ThrowsAsync<ControlException>(() => app.Store.DeleteWorker(worker.Id, input));
        Assert.Contains("active work item ownership", failure.Message);

        await app.Store.TransitionWorkItem(new(workItem.Id, workItem.Revision, worker.Id, WorkItemState.Abandoned));
        await app.Store.DeleteWorker(worker.Id, input);
        Assert.Equal(WorkItemState.Abandoned, (await app.Store.GetWorkItem(workItem.Id))!.State);
    }

    [Theory]
    [InlineData(true, "Disconnected")]
    [InlineData(true, "Connecting")]
    [InlineData(false, "Connecting")]
    [InlineData(false, "Connected")]
    public async Task WorkerDeletionStillRequiresFreshIdleStateUnlessRuntimeIsExplicitlyDisconnected(bool desiredConnected, string transport)
    {
        await using var app = new TestApp(); var worker = await PersistenceTests.SeedWorker(app.Store);
        await app.Store.Write(async db =>
        {
            var runtime = (await db.Runtimes.FindAsync(worker.RuntimeId))!;
            runtime.DesiredConnected = desiredConnected; runtime.Transport = transport;
            return true;
        });

        var failure = await Assert.ThrowsAsync<ControlException>(() => app.Store.DeleteWorker(worker.Id,
            new(Guid.NewGuid().ToString(), worker.SettingsRevision)));

        Assert.Contains("fresh idle observation", failure.Message);
    }

    [Fact]
    public async Task RuntimeDeletionRejectsWorkersTerminalsAndPendingOperations()
    {
        await using var app = new TestApp(); var worker = await PersistenceTests.SeedWorker(app.Store);
        var runtime = (await app.Store.Snapshot()).Runtimes.Single();
        var input = new DeleteRegistrationInput(Guid.NewGuid().ToString(), runtime.Revision);
        Assert.Contains("in use", (await Assert.ThrowsAsync<ControlException>(() => app.Store.DeleteRuntime(runtime.Id, input))).Message);
        await app.Store.Write(db => { db.Workers.Remove(worker); return Task.FromResult(true); });
        await app.Store.AcquireTerminalRuntime(runtime.Id, CancellationToken.None);
        Assert.Contains("terminals", (await Assert.ThrowsAsync<ControlException>(() => app.Store.DeleteRuntime(runtime.Id, input))).Message);
        await app.Store.ReleaseTerminalRuntime(runtime.Id);
        var creation = await app.Store.CreateWorker(new(Guid.NewGuid().ToString(), runtime.Id, "pending", "", "/home/agent/workspaces/a", "fixture", "deterministic"));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.DeleteRuntime(runtime.Id, input));
        await app.Store.EditQueue(creation.Id, "cancel");
        var deleted = await app.Store.DeleteRuntime(runtime.Id, input);
        Assert.Equal(deleted.Id, (await app.Store.DeleteRuntime(runtime.Id, input)).Id);
        Assert.Empty((await app.Store.Snapshot()).Runtimes);
        await Assert.ThrowsAsync<ControlException>(() => app.Store.AcquireTerminalRuntime(runtime.Id, CancellationToken.None));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.SaveRuntime(runtime));
    }
}
