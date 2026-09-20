namespace HVO.AgentControl.RemoteWorker;

/// <summary>
/// The bounded, owner-supplied task-specification input for
/// <c>POST /api/employees/{id}/tasks</c>. It deliberately carries no binding,
/// worker or session identity: the controller resolves those server-side from
/// the employee's exact managed enrollment and active session.
/// </summary>
public sealed record EmployeeTaskSpecInput(
    string Description,
    string WorkspaceRoot,
    IReadOnlyList<string> AllowedPaths,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> ForbiddenActions,
    int MaximumSeconds,
    string? TestRecipeId = null);

/// <summary>
/// The high-level, owner-only request to give one managed employee its first
/// bounded task. The caller states the employee revision it acted on and an
/// idempotency key; it never names a binding, worker or session.
/// </summary>
public sealed record EmployeeTaskCreate(
    int ExpectedEmployeeRevision,
    string IdempotencyKey,
    EmployeeTaskSpecInput TaskSpec);

/// <summary>The bounded body of the owner-only task cancellation route.</summary>
public sealed record EmployeeTaskCancel(int ExpectedTaskRevision);

/// <summary>
/// A read-only summary of one host verification. Only the bounded hashes and
/// the sanitized failure detail are surfaced; the raw manifest, test summary and
/// denied-action evidence bytes stay in the store.
/// </summary>
public sealed record EmployeeTaskVerificationSummary(
    string Id,
    string State,
    string VerifierVersion,
    string? ManifestHash,
    string? TestSummaryHash,
    string? DeniedActionHash,
    string? FailureDetail,
    DateTimeOffset? VerifiedAt,
    int Revision);

/// <summary>
/// One durable task joined to its latest request and its host verification. It
/// carries the exact employee/binding/worker/session identities for diagnostics,
/// the bounded specification, model-report fields and the verification summary.
/// The verification is null until slice C records one.
/// </summary>
public sealed record EmployeeTaskDetail(
    HVO.AgentControl.Organization.WorkerTaskRecord Task,
    HVO.AgentControl.Organization.WorkerTaskSpec Spec,
    HVO.AgentControl.Organization.WorkerRequestRecord? Request,
    EmployeeTaskVerificationSummary? Verification,
    string DisplayState);

/// <summary>The cancellation result plus the authoritative current task detail.</summary>
public sealed record EmployeeTaskCancellationDetail(
    HVO.AgentControl.Organization.WorkerCancellationRecord Cancellation,
    EmployeeTaskDetail Task);

/// <summary>
/// Normalized owner-facing display states for a durable task. This is a pure
/// projection of the persisted task state: it invents no transition and never
/// promotes model evidence to success.
/// </summary>
public static class EmployeeTaskDisplayStates
{
    public const string Requested = "requested";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Verified = "verified";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Uncertain = "uncertain";

    public static string ForTaskState(string taskState) => taskState switch
    {
        HVO.AgentControl.Organization.WorkerTaskStates.Requested => Requested,
        HVO.AgentControl.Organization.WorkerTaskStates.Running => Running,
        HVO.AgentControl.Organization.WorkerTaskStates.Completed => Completed,
        HVO.AgentControl.Organization.WorkerTaskStates.Verified => Verified,
        HVO.AgentControl.Organization.WorkerTaskStates.Failed => Failed,
        HVO.AgentControl.Organization.WorkerTaskStates.Cancelled => Cancelled,
        HVO.AgentControl.Organization.WorkerTaskStates.Uncertain => Uncertain,
        _ => Uncertain,
    };
}
