using System.Text.Json;
using HVO.AgentControl.Organization;
using HVO.AgentControl.RemoteWorker;
using Microsoft.Data.Sqlite;
using Xunit;
using RemoteStoreFixture = HVO.AgentControl.Tests.RemoteWorkerControlTests.RemoteStoreFixture;

namespace HVO.AgentControl.Tests;

/// <summary>
/// The employee-scoped task coordinator dispatches only through the existing
/// remote-worker gate, resolves every internal identity server-side, persists the
/// canonical specification before send, and treats an idempotent replay as the
/// same task rather than a second dispatch.
/// </summary>
public sealed class EmployeeTaskCoordinatorTests
{
    private const string NativeSessionId = "native-remote";

    private static EmployeeTaskSpecInput SpecInput(
        string description = "Add a bounded health endpoint",
        int maximumSeconds = 300) => new(
            description,
            "/workspace/project",
            ["src", "tests"],
            [WorkerTaskTools.Read, WorkerTaskTools.Edit, WorkerTaskTools.Test],
            ["network egress", "secret access"],
            maximumSeconds,
            WorkerTaskTestRecipes.DotnetTestRelease);

    [Fact]
    public async Task PublicTaskInputRejectsTheLegacyCompatibilityWorkspace()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        MarkAuthenticated(fixture, enrollment.WorkerId);
        var session = new TaskFakeBridgeSession(enrollment.ControllerId);
        await using var manager = fixture.CreateManager(new TaskFakeBridgeSessionFactory(session));
        var coordinator = Coordinator(fixture, manager);
        var legacy = SpecInput() with { WorkspaceRoot = "/workspace/legacy-request" };

        await Assert.ThrowsAsync<OrganizationValidationException>(() => coordinator.CreateAsync(
            fixture.EmployeeId, new EmployeeTaskCreate(1, "idem-legacy", legacy), CancellationToken.None));
        Assert.Equal(0, session.SubmitCount);
    }

    [Fact]
    public async Task SuccessfulDispatchPersistsTheCanonicalSpecAndLeavesTheTaskRunning()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        MarkAuthenticated(fixture, enrollment.WorkerId);
        var session = new TaskFakeBridgeSession(enrollment.ControllerId);
        await using var manager = fixture.CreateManager(new TaskFakeBridgeSessionFactory(session));
        var coordinator = Coordinator(fixture, manager);

        var detail = await coordinator.CreateAsync(fixture.EmployeeId, new EmployeeTaskCreate(1, "idem-success", SpecInput()), CancellationToken.None);

        Assert.Equal(WorkerTaskStates.Running, detail.Task.State);
        Assert.Equal(EmployeeTaskDisplayStates.Running, detail.DisplayState);
        Assert.Equal("Forwarded", detail.Request!.State);
        Assert.Equal(NativeSessionId, detail.Request.NativeSessionId);
        Assert.Equal(fixture.SessionId, detail.Request.SessionRecordId);
        Assert.Equal(enrollment.WorkerId, detail.Task.WorkerId);
        Assert.Equal("Add a bounded health endpoint", detail.Spec.Description);
        Assert.Null(detail.Verification);
        Assert.Null(detail.Task.ModelReportHash);
        Assert.Equal(1, session.SubmitCount);
        Assert.Equal(fixture.EmployeeId, detail.Task.EmployeeId);
        Assert.Single(fixture.Store.ListWorkerTasks(employeeId: fixture.EmployeeId));
        Assert.Single(fixture.Store.ListWorkerTasks());
    }

    [Fact]
    public async Task RepeatedIdempotencyCreatesOneTaskRequestAndSubmit()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        MarkAuthenticated(fixture, enrollment.WorkerId);
        var session = new TaskFakeBridgeSession(enrollment.ControllerId);
        await using var manager = fixture.CreateManager(new TaskFakeBridgeSessionFactory(session));
        var coordinator = Coordinator(fixture, manager);

        var first = await coordinator.CreateAsync(fixture.EmployeeId, new EmployeeTaskCreate(1, "idem-repeat", SpecInput()), CancellationToken.None);
        var replay = await coordinator.CreateAsync(fixture.EmployeeId, new EmployeeTaskCreate(1, "idem-repeat", SpecInput()), CancellationToken.None);

        Assert.Equal(first.Task.Id, replay.Task.Id);
        Assert.Equal(first.Request!.Id, replay.Request!.Id);
        Assert.Equal(1, session.SubmitCount);
        Assert.Single(fixture.Store.ListWorkerTasks(fixture.EmployeeId));
        Assert.Single(fixture.Store.ListWorkerRequests());
    }

    [Fact]
    public async Task ChangedSpecificationWithTheSameKeyIsAConflict()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        MarkAuthenticated(fixture, enrollment.WorkerId);
        var session = new TaskFakeBridgeSession(enrollment.ControllerId);
        await using var manager = fixture.CreateManager(new TaskFakeBridgeSessionFactory(session));
        var coordinator = Coordinator(fixture, manager);

        await coordinator.CreateAsync(fixture.EmployeeId, new EmployeeTaskCreate(1, "idem-conflict", SpecInput()), CancellationToken.None);

        await Assert.ThrowsAsync<OrganizationConcurrencyException>(() => coordinator.CreateAsync(
            fixture.EmployeeId,
            new EmployeeTaskCreate(1, "idem-conflict", SpecInput(description: "A completely different change")),
            CancellationToken.None));
        Assert.Equal(1, session.SubmitCount);
        Assert.Single(fixture.Store.ListWorkerTasks(fixture.EmployeeId));
    }

    [Fact]
    public async Task ActiveRecoveryObligationRefusesBeforeAnySubmit()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        MarkAuthenticated(fixture, enrollment.WorkerId);
        fixture.Store.RecordControllerRecovery(enrollment.WorkerId, "replay-gap", "held-marker");
        var session = new TaskFakeBridgeSession(enrollment.ControllerId);
        await using var manager = fixture.CreateManager(new TaskFakeBridgeSessionFactory(session));
        var coordinator = Coordinator(fixture, manager);

        await Assert.ThrowsAsync<OrganizationConcurrencyException>(() => coordinator.CreateAsync(
            fixture.EmployeeId, new EmployeeTaskCreate(1, "idem-held", SpecInput()), CancellationToken.None));

        Assert.Equal(0, session.SubmitCount);
        Assert.Empty(fixture.Store.ListWorkerTasks(fixture.EmployeeId));
    }

    [Fact]
    public async Task StaleOrientationRefusesBeforeAnySubmit()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        MarkAuthenticated(fixture, enrollment.WorkerId);
        using (var connection = new SqliteConnection($"Data Source={fixture.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE orientation_assignments SET state='Stale' WHERE runtime_binding_id='{fixture.BindingId}'";
            command.ExecuteNonQuery();
        }

        var session = new TaskFakeBridgeSession(enrollment.ControllerId);
        await using var manager = fixture.CreateManager(new TaskFakeBridgeSessionFactory(session));
        var coordinator = Coordinator(fixture, manager);

        await Assert.ThrowsAsync<OrganizationConcurrencyException>(() => coordinator.CreateAsync(
            fixture.EmployeeId, new EmployeeTaskCreate(1, "idem-stale", SpecInput()), CancellationToken.None));

        Assert.Equal(0, session.SubmitCount);
    }

    [Fact]
    public async Task EmployeeRevisionConflictRefusesBeforeAnySubmit()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        MarkAuthenticated(fixture, enrollment.WorkerId);
        var session = new TaskFakeBridgeSession(enrollment.ControllerId);
        await using var manager = fixture.CreateManager(new TaskFakeBridgeSessionFactory(session));
        var coordinator = Coordinator(fixture, manager);

        await Assert.ThrowsAsync<OrganizationConcurrencyException>(() => coordinator.CreateAsync(
            fixture.EmployeeId, new EmployeeTaskCreate(99, "idem-revision", SpecInput()), CancellationToken.None));

        Assert.Equal(0, session.SubmitCount);
    }

    [Fact]
    public async Task RecentIsEmployeeScopedNewestFirstAndSeparatesReportFromVerification()
    {
        using var fixture = new RemoteStoreFixture();
        fixture.CreateEnrolledAndReady();
        MarkAuthenticated(fixture, "wrk-a");
        var taskA = BeginTask(fixture, "idem-read-a");
        var taskB = BeginTask(fixture, "idem-read-b");

        var recent = fixture.Store.ListEmployeeTaskDetails(fixture.EmployeeId, 10);
        Assert.Equal(2, recent.Count);
        Assert.Equal(EmployeeTaskDisplayStates.Requested, recent[0].DisplayState);
        Assert.All(recent, detail => Assert.Null(detail.Verification));
        Assert.All(recent, detail => Assert.Null(detail.Task.ModelReportHash));
        Assert.Contains(recent, detail => detail.Task.Id == taskA.TaskId);
        Assert.Contains(recent, detail => detail.Task.Id == taskB.TaskId);
        // Newest first by created_at then id, both descending.
        for (var i = 1; i < recent.Count; i++)
        {
            var previous = recent[i - 1].Task;
            var current = recent[i].Task;
            Assert.True(previous.CreatedAt > current.CreatedAt
                || previous.CreatedAt == current.CreatedAt && string.CompareOrdinal(previous.Id, current.Id) > 0);
        }

        // Exact employee scoping: another employee's id never sees these tasks.
        Assert.Empty(fixture.Store.ListEmployeeTaskDetails("emp-other", 10));
        Assert.Null(fixture.Store.GetEmployeeTaskDetail("tsk-does-not-exist"));
    }

    [Fact]
    public async Task DetailSurfacesAModelReportAndItsVerificationSeparately()
    {
        using var fixture = new RemoteStoreFixture();
        fixture.CreateEnrolledAndReady();
        MarkAuthenticated(fixture, "wrk-a");
        var request = BeginTask(fixture, "idem-verify");
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Intent", "Forwarding");
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Forwarding", "Forwarded");
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Forwarded", "Completed");
        var task = fixture.Store.GetWorkerTask(request.TaskId)!;
        task = fixture.Store.RecordWorkerTaskModelReport(task.Id, task.Revision, new ModelTaskReport("done", ["src/a.cs"], [new ModelTaskTestReport(WorkerTaskTestRecipes.DotnetTestRelease, "passed", "dotnet test passed")], null, []));
        var verification = fixture.Store.BeginWorkerTaskVerification(task.Id, task.Revision, "workspace-task-verify-v1");

        var detail = Assert.Single(fixture.Store.ListEmployeeTaskDetails(fixture.EmployeeId, 10));

        Assert.Equal(EmployeeTaskDisplayStates.Completed, detail.DisplayState);
        Assert.NotNull(detail.Task.ModelReportHash);
        Assert.NotNull(detail.Task.ModelReportedAt);
        Assert.NotNull(detail.Verification);
        Assert.Equal(verification.Id, detail.Verification!.Id);
        Assert.Equal(WorkerTaskVerificationStates.Pending, detail.Verification.State);
        Assert.Null(detail.Verification.VerifiedAt);
        Assert.NotNull(detail.Request);
    }

    [Fact]
    public async Task SyncCompletesWithReportExactlyOnceAndNeverResubmits()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        MarkAuthenticated(fixture, enrollment.WorkerId);
        var session = new TaskFakeBridgeSession(enrollment.ControllerId);
        await using var manager = fixture.CreateManager(new TaskFakeBridgeSessionFactory(session));
        var coordinator = Coordinator(fixture, manager);
        var running = await coordinator.CreateAsync(fixture.EmployeeId, new EmployeeTaskCreate(1, "idem-sync", SpecInput()), CancellationToken.None);
        session.Complete(running.Request!.Id);

        var synchronized = await coordinator.SyncAsync(running.Task.Id, new EmployeeTaskSync(running.Task.Revision), CancellationToken.None);

        Assert.Equal(WorkerTaskStates.Completed, synchronized.Task.Task.State);
        Assert.Equal("Completed", synchronized.Task.Request!.State);
        Assert.NotNull(synchronized.Task.Task.ModelReportJson);
        Assert.Equal(1, session.SubmitCount);
        var reread = coordinator.Get(running.Task.Id);
        Assert.Equal(synchronized.Task.Task.ModelReportHash, reread.Task.ModelReportHash);
    }

    [Fact]
    public async Task RestartPreservesForwardedAndSyncCompletesWithoutDuplicateSubmit()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        MarkAuthenticated(fixture, enrollment.WorkerId);
        var session = new TaskFakeBridgeSession(enrollment.ControllerId);
        EmployeeTaskDetail running;
        await using (var firstManager = fixture.CreateManager(new TaskFakeBridgeSessionFactory(session)))
        {
            running = await Coordinator(fixture, firstManager).CreateAsync(fixture.EmployeeId, new EmployeeTaskCreate(1, "idem-restart-sync", SpecInput()), CancellationToken.None);
        }
        session.Complete(running.Request!.Id);

        await using var restarted = fixture.CreateManager(new TaskFakeBridgeSessionFactory(session));
        var synchronized = await Coordinator(fixture, restarted).SyncAsync(running.Task.Id, new EmployeeTaskSync(running.Task.Revision), CancellationToken.None);

        Assert.Equal(WorkerTaskStates.Completed, synchronized.Task.Task.State);
        Assert.NotNull(synchronized.Task.Task.ModelReportHash);
        Assert.Equal(1, session.SubmitCount);
    }

    [Fact]
    public async Task CancellationIsObservedOnlyAfterTerminalReconciliation()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        MarkAuthenticated(fixture, enrollment.WorkerId);
        var session = new TaskFakeBridgeSession(enrollment.ControllerId);
        await using var manager = fixture.CreateManager(new TaskFakeBridgeSessionFactory(session));
        var coordinator = Coordinator(fixture, manager);
        var running = await coordinator.CreateAsync(fixture.EmployeeId, new EmployeeTaskCreate(1, "idem-cancel-observe", SpecInput()), CancellationToken.None);
        var cancellation = await coordinator.CancelAsync(running.Task.Id, new EmployeeTaskCancel(running.Task.Revision), CancellationToken.None);
        Assert.Equal(WorkerTaskStates.Running, cancellation.Task.Task.State);
        session.Fail(running.Request!.Id);

        var synchronized = await coordinator.SyncAsync(running.Task.Id, new EmployeeTaskSync(running.Task.Revision), CancellationToken.None);

        Assert.Equal(WorkerTaskStates.Cancelled, synchronized.Task.Task.State);
        Assert.Equal("Observed", fixture.Store.ListWorkerCancellations().Single().State);
    }

    [Fact]
    public async Task NormalCompletionAfterCancellationIsPreservedWithDiagnostic()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        MarkAuthenticated(fixture, enrollment.WorkerId);
        var session = new TaskFakeBridgeSession(enrollment.ControllerId);
        await using var manager = fixture.CreateManager(new TaskFakeBridgeSessionFactory(session));
        var coordinator = Coordinator(fixture, manager);
        var running = await coordinator.CreateAsync(fixture.EmployeeId, new EmployeeTaskCreate(1, "idem-cancel-complete", SpecInput()), CancellationToken.None);
        await coordinator.CancelAsync(running.Task.Id, new EmployeeTaskCancel(running.Task.Revision), CancellationToken.None);
        session.Complete(running.Request!.Id);

        var synchronized = await coordinator.SyncAsync(running.Task.Id, new EmployeeTaskSync(running.Task.Revision), CancellationToken.None);

        Assert.Equal(WorkerTaskStates.Completed, synchronized.Task.Task.State);
        Assert.Equal("cancellation-requested-but-completed", synchronized.Task.Task.FailureDetail);
        Assert.Equal("Observed", fixture.Store.ListWorkerCancellations().Single().State);
    }

    [Fact]
    public async Task ManualHoldBlocksDispatchAndClearingOnlyManualStillHonorsStaleOrientation()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        MarkAuthenticated(fixture, enrollment.WorkerId);
        var session = new TaskFakeBridgeSession(enrollment.ControllerId);
        await using var manager = fixture.CreateManager(new TaskFakeBridgeSessionFactory(session));
        var coordinator = Coordinator(fixture, manager);

        var held = coordinator.SetDispatchHold(fixture.EmployeeId, new EmployeeDispatchHoldUpdate(1, true, "maintenance"));
        Assert.False(held.Status.Ready);
        await Assert.ThrowsAsync<OrganizationConcurrencyException>(() => coordinator.CreateAsync(fixture.EmployeeId, new EmployeeTaskCreate(1, "idem-held-manual", SpecInput()), CancellationToken.None));
        coordinator.SetDispatchHold(fixture.EmployeeId, new EmployeeDispatchHoldUpdate(1, false, null));
        var dispatched = await coordinator.CreateAsync(fixture.EmployeeId, new EmployeeTaskCreate(1, "idem-held-cleared", SpecInput()), CancellationToken.None);
        Assert.Equal(WorkerTaskStates.Running, dispatched.Task.State);

        using var connection = new SqliteConnection($"Data Source={fixture.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE orientation_assignments SET state='Stale' WHERE runtime_binding_id='{fixture.BindingId}'";
        command.ExecuteNonQuery();
        coordinator.SetDispatchHold(fixture.EmployeeId, new EmployeeDispatchHoldUpdate(1, false, null));
        await Assert.ThrowsAsync<OrganizationConcurrencyException>(() => coordinator.CreateAsync(fixture.EmployeeId, new EmployeeTaskCreate(1, "idem-still-stale", SpecInput()), CancellationToken.None));
    }

    [Fact]
    public void GetAndRecentRejectMalformedIdentifiers()
    {
        using var fixture = new RemoteStoreFixture();
        var coordinator = Coordinator(fixture, fixture.CreateManager(new TaskFakeBridgeSessionFactory(new TaskFakeBridgeSession("controller-a"))));

        Assert.Throws<OrganizationValidationException>(() => coordinator.Get("tsk-"));
        Assert.Throws<OrganizationValidationException>(() => coordinator.Get("wrong-1"));
        Assert.Empty(coordinator.Recent("not-an-employee", 10));
    }

    private static EmployeeTaskCoordinator Coordinator(RemoteStoreFixture fixture, WorkerConnectionManager manager) =>
        new(fixture.ControlHost(), manager, new RemoteWorkerStatusProvider(fixture.ControlHost()));

    private static void MarkAuthenticated(RemoteStoreFixture fixture, string workerId) =>
        fixture.Store.RecordWorkerConnectionState(workerId, "authenticated", 1);

    private static WorkerRequestRecord BeginTask(RemoteStoreFixture fixture, string idempotencyKey) =>
        fixture.Store.BeginWorkerRequest(
            new BeginWorkerRequest(fixture.EmployeeId, fixture.BindingId, "wrk-a", fixture.SessionId, NativeSessionId, idempotencyKey, "sha256:" + new string('d', 64), "sha256:" + new string('f', 64), 1, 1, "turn-" + idempotencyKey),
            new WorkerTaskSpec("Bounded task", "/workspace/project", ["src", "tests"], [WorkerTaskTools.Read, WorkerTaskTools.Edit, WorkerTaskTools.Test], ["network egress"], 300, WorkerTaskTestRecipes.DotnetTestRelease));

    private sealed class TaskFakeBridgeSessionFactory(TaskFakeBridgeSession session) : IWorkerBridgeSessionFactory
    {
        public Task<IWorkerBridgeSession> ConnectAsync(WorkerEnrollmentRecord enrollment, CancellationToken cancellationToken) =>
            Task.FromResult<IWorkerBridgeSession>(session);
    }

    /// <summary>
    /// A minimal in-process worker that answers status, submit, reconcile and
    /// replay with exactly correlated identities. It records every submit so a
    /// test can assert dispatch reached the worker exactly once.
    /// </summary>
    private sealed class TaskFakeBridgeSession : IWorkerBridgeSession
    {
        private readonly Dictionary<string, (string TurnId, long Epoch, long Process, string State, string? Outcome)> _requests = new(StringComparer.Ordinal);

        public TaskFakeBridgeSession(string controllerId) =>
            Lease = new WorkerBridgeLease(1, controllerId, Convert.ToBase64String(new byte[32]), DateTimeOffset.UtcNow);

        public WorkerBridgeLease Lease { get; }

        public int SubmitCount { get; private set; }

        public void Complete(string requestId, string? outcome = null) =>
            _requests[requestId] = (_requests[requestId].TurnId, 1, 1, "completed", outcome ?? "{\"summary\":\"done\",\"changedPaths\":[\"src/a.cs\"],\"tests\":[{\"recipeId\":\"dotnet-test-release\",\"status\":\"passed\",\"summary\":\"passed\"}],\"deniedAction\":null,\"limitations\":[]}");

        public void Fail(string requestId, string category = "cancelled") =>
            _requests[requestId] = (_requests[requestId].TurnId, 1, 1, "failed", $"{{\"category\":\"{category}\"}}");

        public object Mutation(string operation, IReadOnlyDictionary<string, object?> fields) => new Dictionary<string, object?>(fields) { ["operation"] = operation };

        public Task<WorkerSessionResult> InvokeAsync(string operation, object request, bool mutation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = JsonSerializer.Serialize(request, HVO.AgentControl.Worker.WorkerProtocol.JsonOptions);
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            object value = operation switch
            {
                "status" => Status(),
                "submit" => Submit(root),
                "reconcile" => Reconcile(root),
                "replay" => new BridgeReplayPage([], false, root.GetProperty("afterSequence").GetInt64()),
                _ => new { ok = true },
            };
            return Task.FromResult(new WorkerSessionResult(operation, JsonSerializer.SerializeToElement(value, HVO.AgentControl.Worker.WorkerProtocol.JsonOptions), mutation));
        }

        private object Submit(JsonElement root)
        {
            var requestId = root.GetProperty("requestId").GetString()!;
            var turnId = root.GetProperty("turnId").GetString()!;
            SubmitCount++;
            _requests[requestId] = (turnId, 1, 1, "forwarded", null);
            return Stored(requestId, turnId, "forwarded", null);
        }

        private object Reconcile(JsonElement root)
        {
            var requestId = root.GetProperty("requestId").GetString()!;
            if (!_requests.TryGetValue(requestId, out var recorded)) throw new WorkerRemoteException("worker-request-rejected");
            return Stored(requestId, recorded.TurnId, recorded.State, recorded.Outcome);
        }

        private static object Stored(string requestId, string turnId, string state, string? outcome) => new
        {
            requestId,
            payloadHash = "sha256:" + new string('0', 64),
            state,
            outcomeJson = outcome,
            processGeneration = 1,
            ownershipEpoch = 1,
            turnId,
            sessionId = NativeSessionId,
        };

        private BridgeWorkerStatus Status() => new(
            WorkerGeneration: 1,
            ProcessGeneration: 1,
            ProcessState: "running",
            LifecycleHandle: "life",
            ObservedPid: 42,
            ActiveRequestId: null,
            PendingPermission: null,
            OwnershipEpoch: 1,
            LeaseActive: true,
            DispatchHeld: false,
            HoldReason: null,
            HoldReasons: [],
            FirstRetainedSequence: 0,
            LastSequence: 0,
            AcknowledgedWorkerGeneration: 0,
            AcknowledgedSequence: 0,
            ReplayLoss: null,
            JournalFailure: null,
            ReplayGapCount: 0,
            ReplayGaps: [],
            ViewerSupported: true,
            ViewerAvailable: true,
            AcpInitialized: true,
            SessionId: NativeSessionId);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
