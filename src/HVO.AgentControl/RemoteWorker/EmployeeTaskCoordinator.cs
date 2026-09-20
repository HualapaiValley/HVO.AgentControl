using HVO.AgentControl.Organization;
using HVO.AgentControl.Runtime;

namespace HVO.AgentControl.RemoteWorker;

/// <summary>
/// Owner-facing, employee-scoped task API on top of the durable #220 schema v13
/// task domain. It resolves every internal identity (binding, session, worker,
/// ownership epoch) server-side from the exact managed enrollment, validates the
/// pre-read model, and dispatches the bounded specification through the existing
/// remote worker gate. The caller never names a binding, worker or session.
/// </summary>
public sealed class EmployeeTaskCoordinator(AcpControlHost control, WorkerConnectionManager manager, IRemoteWorkerStatusProvider statusProvider)
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
