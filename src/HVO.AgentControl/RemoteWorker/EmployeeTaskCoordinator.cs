using HVO.AgentControl.Organization;
using HVO.AgentControl.Runtime;
using Microsoft.Extensions.Options;

namespace HVO.AgentControl.RemoteWorker;

/// <summary>
/// Owner-facing, employee-scoped task API on top of the durable #220 schema v13
/// task domain. It resolves every internal identity (binding, session, worker,
/// ownership epoch) server-side from the exact managed enrollment, validates the
/// pre-read model, and dispatches the bounded specification through the existing
/// remote worker gate. The caller never names a binding, worker or session.
/// </summary>
public sealed class EmployeeTaskCoordinator(AcpControlHost control, WorkerConnectionManager manager, IRemoteWorkerStatusProvider statusProvider, IRemoteWorkerProvisioner? provisioner = null, IOptions<WorkerControlOptions>? configured = null)
{
    /// <summary>The default recent-task page size.</summary>
    public const int DefaultRecentLimit = 10;

    public async Task<EmployeeTaskDetail> CreateAsync(string employeeId, EmployeeTaskCreate request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExpectedEmployeeRevision < 1)
            throw new OrganizationValidationException("The current employee revision is required.");
        if (!IsBoundedIdempotencyKey(request.IdempotencyKey))
            throw new OrganizationValidationException("A bounded idempotency key is required.");
        if (request.TaskSpec is null)
            throw new OrganizationValidationException("A bounded task specification is required.");
        var spec = BuildSpec(request.TaskSpec);

        var store = Store();
        var overview = store.GetOverview();
        var employee = overview.Employees.SingleOrDefault(x => string.Equals(x.Id, employeeId, StringComparison.Ordinal))
            ?? throw new OrganizationNotFoundException($"Employee '{employeeId}' does not exist.");
        if (employee.Revision != request.ExpectedEmployeeRevision)
            throw new OrganizationConcurrencyException("The employee changed; reload and retry with its current revision.");

        var remote = statusProvider.Snapshot(overview).GetValueOrDefault(employeeId)
            ?? throw new OrganizationConcurrencyException("The employee is not a managed remote worker with an exact enrollment.");
        EnsurePreReadEligible(store, employee, remote);

        // The prompt is host-generated from the canonical, normalized task spec.
        // The request bodies' arbitrary text is never sent as a prompt.
        var prompt = WorkerTaskPrompt.Render(spec);
        var command = new RemoteDispatchCommand(
            employee.Id,
            remote.RuntimeBindingId,
            remote.WorkerId,
            remote.SessionRecordId!,
            remote.NativeSessionId!,
            request.IdempotencyKey,
            prompt,
            spec);

        var dispatched = await manager.DispatchAsync(command, cancellationToken).ConfigureAwait(false);
        return store.GetEmployeeTaskDetail(dispatched.TaskId)
            ?? throw new OrganizationStoreCorruptException("The dispatched worker task could not be re-read.");
    }

    /// <summary>Returns the newest-first recent tasks for one employee, bounded by the supplied limit.</summary>
    public IReadOnlyList<EmployeeTaskDetail> Recent(string employeeId, int limit) =>
        Store().ListEmployeeTaskDetails(employeeId, limit);

    /// <summary>Returns one durable task detail, or throws when the task does not exist.</summary>
    public EmployeeTaskDetail Get(string taskId)
    {
        if (!IsBoundedTaskId(taskId))
            throw new OrganizationValidationException("A bounded stable task id is required.");
        return Store().GetEmployeeTaskDetail(taskId)
            ?? throw new OrganizationNotFoundException($"Worker task '{taskId}' does not exist.");
    }

    /// <summary>Synchronizes one exact durable request and never resubmits it.</summary>
    public async Task<EmployeeTaskSyncDetail> SyncAsync(string taskId, EmployeeTaskSync request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsBoundedTaskId(taskId) || request.ExpectedTaskRevision < 1)
            throw new OrganizationValidationException("A bounded stable task id and current revision are required.");
        var store = Store();
        var detail = store.GetEmployeeTaskDetail(taskId)
            ?? throw new OrganizationNotFoundException($"Worker task '{taskId}' does not exist.");
        if (detail.Task.Revision != request.ExpectedTaskRevision)
            throw new OrganizationConcurrencyException("The worker task changed; reload and retry with its current revision.");
        if (detail.Request is null)
            throw new OrganizationConcurrencyException("The task has no exact request to synchronize.");
        await manager.SynchronizeOnceAsync(detail.Task.WorkerId, cancellationToken).ConfigureAwait(false);
        var updated = store.GetEmployeeTaskDetail(taskId)
            ?? throw new OrganizationStoreCorruptException("The synchronized worker task could not be re-read.");
        var message = updated.Request?.State switch
        {
            "Completed" => "remote-completed",
            "Failed" => updated.Task.State == WorkerTaskStates.Cancelled ? "cancellation-observed" : "remote-failed",
            "Uncertain" or "Interrupted" => "remote-outcome-uncertain",
            _ => "remote-still-in-flight",
        };
        return new EmployeeTaskSyncDetail(updated, DateTimeOffset.UtcNow, message);
    }

    /// <summary>Runs the fixed helper verifier against the exact persisted workspace and image.</summary>
    public async Task<EmployeeTaskDetail> VerifyAsync(string taskId, int expectedTaskRevision, CancellationToken cancellationToken)
    {
        if (!IsBoundedTaskId(taskId) || expectedTaskRevision < 1)
            throw new OrganizationValidationException("A bounded stable task id and current revision are required.");
        var store = Store();
        var detail = store.GetEmployeeTaskDetail(taskId)
            ?? throw new OrganizationNotFoundException($"Worker task '{taskId}' does not exist.");
        if (detail.Task.Revision != expectedTaskRevision)
            throw new OrganizationConcurrencyException("The worker task changed; reload and retry with its current revision.");
        if (detail.Task.State != WorkerTaskStates.Completed || detail.Task.ModelReportHash is null)
            throw new OrganizationValidationException("Only a completed task with a model report can be verified.");
        if (detail.Spec.TestRecipeId != WorkerTaskTestRecipes.DotnetTestRelease)
            throw new OrganizationValidationException("The task has no supported test recipe.");
        var enrollment = store.GetWorkerEnrollment(detail.Task.WorkerId)
            ?? throw new OrganizationStoreCorruptException("The task worker enrollment is missing.");
        var resources = store.GetManagedEnrollmentResources(enrollment.RuntimeBindingId)
            ?? throw new OrganizationValidationException("Only a managed local Docker workspace can be verified.");
        if (resources.ApprovedHostId != ExecutionHosts.LocalDockerId || enrollment.HostId != ExecutionHosts.LocalDockerId)
            throw new OrganizationValidationException("Workspace verification requires the controller-local Docker helper.");
        var volumeResource = store.ListWorkerResources(enrollment.WorkerId).SingleOrDefault(resource => resource.ResourceKind == "volume" && resource.ResourceName == enrollment.WorkspaceVolumeName && resource.State == "present")
            ?? throw new OrganizationConcurrencyException("The exact workspace volume is not durably present.");
        var baseDigest = configured?.Value.ApprovedImageDigest ?? string.Empty;
        // Named-volume ownership labels are immutable from initial provisioning;
        // a later profile rebuild replaces only the container and image target.
        var volumeProfileRevision = resources.ApprovedImageDigest == baseDigest ? null : resources.ApprovedProfileRevisionId;
        var identity = new WorkerResourceIdentity(enrollment.OrganizationId, enrollment.ControllerId, enrollment.HostId, enrollment.WorkerId, enrollment.RuntimeBindingId, volumeResource.OperationId, volumeProfileRevision);
        var relativeRoot = detail.Spec.WorkspaceRoot[OrganizationStore.TaskWorkspaceRootPrefix.Length..];
        if (string.IsNullOrEmpty(baseDigest)) throw new WorkerControlConfigurationException("The approved worker base digest is unavailable.");
        var approved = store.ListApprovedImageDigests(enrollment.HostId, baseDigest);
        var pending = store.BeginWorkerTaskVerification(taskId, expectedTaskRevision, "workspace-task-verify-v1");
        HostTaskVerification outcome;
        try
        {
            var spec = new WorkspaceVerifySpec(identity, enrollment.WorkspaceVolumeName, relativeRoot, detail.Spec.AllowedPaths, detail.Spec.TestRecipeId, detail.Spec.MaximumSeconds, 1024, 64L * 1024 * 1024, enrollment.ExpectedImageDigest, enrollment.ExpectedPlatform, approved, resources.MemoryLimitMiB * 1024L * 1024L, resources.CpuLimit);
            outcome = await (provisioner ?? throw new WorkerControlConfigurationException("Workspace verification operations are unavailable.")).WorkspaceVerifyAsync(new ExecutionTarget(ExecutionHosts.LocalDockerId, "local-docker", null), spec, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (WorkerControlConfigurationException)
        {
            outcome = new(WorkerTaskVerificationStates.Failed, null, null, null, "verification-request-refused");
        }
        catch (Exception exception) when (exception is RemoteWorkerUnavailableException or IOException)
        {
            outcome = new(WorkerTaskVerificationStates.Uncertain, null, null, null, "verification-transport-uncertain");
        }
        store.CompleteWorkerTaskVerification(pending.Id, pending.Revision, outcome with { DeniedActionJson = null });
        return store.GetEmployeeTaskDetail(taskId) ?? throw new OrganizationStoreCorruptException("The verified task could not be re-read.");
    }

    /// <summary>Sets or clears only the employee's manual hold, revision-bound.</summary>
    public EmployeeDispatchHoldDetail SetDispatchHold(string employeeId, EmployeeDispatchHoldUpdate request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExpectedEmployeeRevision < 1)
            throw new OrganizationValidationException("The current employee revision is required.");
        var store = Store();
        var employee = store.GetOverview().Employees.SingleOrDefault(item => item.Id == employeeId)
            ?? throw new OrganizationNotFoundException($"Employee '{employeeId}' does not exist.");
        if (employee.Revision != request.ExpectedEmployeeRevision)
            throw new OrganizationConcurrencyException("The employee changed; reload and retry with its current revision.");
        if (employee.Placement != "DeveloperContainer")
            throw new OrganizationConcurrencyException("The employee is not a managed DeveloperContainer employee.");
        var status = store.SetManualDispatchHold(employeeId, request.Held, request.Detail);
        var updated = store.GetOverview().Employees.Single(item => item.Id == employeeId);
        return new EmployeeDispatchHoldDetail(updated, status);
    }

    /// <summary>
    /// Resolves the task's current request and forwards an exact cancellation for
    /// it. The task is deliberately not moved to <c>Cancelled</c> here: a
    /// forwarded cancellation is an observation the later slice records, and a
    /// cancellation is never a rollback of any effect already applied.
    /// </summary>
    public async Task<EmployeeTaskCancellationDetail> CancelAsync(string taskId, EmployeeTaskCancel request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsBoundedTaskId(taskId))
            throw new OrganizationValidationException("A bounded stable task id is required.");
        if (request.ExpectedTaskRevision < 1)
            throw new OrganizationValidationException("The current task revision is required.");

        var store = Store();
        var detail = store.GetEmployeeTaskDetail(taskId)
            ?? throw new OrganizationNotFoundException($"Worker task '{taskId}' does not exist.");
        if (detail.Task.Revision != request.ExpectedTaskRevision)
            throw new OrganizationConcurrencyException("The worker task changed; reload and retry with its current revision.");
        var current = detail.Request
            ?? throw new OrganizationConcurrencyException("The task has no current request to cancel.");

        var cancellation = await manager.CancelAsync(new RemoteCancellationCommand(current.Id), cancellationToken).ConfigureAwait(false);
        var updated = store.GetEmployeeTaskDetail(taskId)
            ?? throw new OrganizationStoreCorruptException("The cancelled worker task could not be re-read.");
        return new EmployeeTaskCancellationDetail(cancellation, updated);
    }

    private static void EnsurePreReadEligible(OrganizationStore store, EmployeeSummary employee, RemoteWorkerSnapshot remote)
    {
        if (employee.Orientation?.State != OrientationStates.Comprehended)
            throw new OrganizationConcurrencyException("The employee orientation is not comprehended and ready for work.");
        var binding = store.GetRemoteBindingSession(remote.RuntimeBindingId);
        if (binding.Placement != "DeveloperContainer"
            || remote.SessionRecordId is null
            || remote.NativeSessionId is null)
            throw new OrganizationConcurrencyException("The employee has no exact managed DeveloperContainer session.");
        if (remote.LifecycleStatus != "enrolled"
            || !remote.EnrollmentEnabled
            || remote.ConnectionState != "authenticated"
            || remote.ProcessState != "running"
            || remote.Held)
            throw new OrganizationConcurrencyException("The worker is not enrolled, authenticated, running and free of recovery obligations.");
    }

    private static WorkerTaskSpec BuildSpec(EmployeeTaskSpecInput input) => OrganizationStore.NormalizeWorkerTaskSpec(new WorkerTaskSpec(
        input.Description ?? string.Empty,
        input.WorkspaceRoot ?? string.Empty,
        input.AllowedPaths ?? [],
        input.AllowedTools ?? [],
        input.ForbiddenActions ?? [],
        input.MaximumSeconds,
        input.TestRecipeId,
        Version: 1,
        MaximumTurns: 1));

    private OrganizationStore Store() => control.Organization ?? throw new OrganizationStoreException("Organization store unavailable.");

    private static bool IsBoundedTaskId(string? value) =>
        value is { Length: >= 6 and <= 128 }
        && value.StartsWith("tsk-", StringComparison.Ordinal)
        && value.AsSpan(4).IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789-".AsSpan()) < 0
        && (value.AsSpan(4).IndexOfAnyInRange('a', 'z') >= 0 || value.AsSpan(4).IndexOfAnyInRange('0', '9') >= 0);

    private static bool IsBoundedIdempotencyKey(string? value) =>
        value is { Length: >= 1 and <= 128 }
        && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or ':' or '-');
}
