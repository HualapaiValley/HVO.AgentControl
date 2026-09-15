namespace HVO.AgentControl.Worker;

public interface IWorkerClock { DateTimeOffset UtcNow { get; } long MonotonicMilliseconds { get; } }
public sealed class SystemWorkerClock : IWorkerClock
{
    private readonly long _origin = Environment.TickCount64;
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public long MonotonicMilliseconds => Environment.TickCount64 - _origin;
}

public sealed record WorkerOptions(string ControlDirectory, string WorkerId, string ControllerId, string SocketPath,
    TimeSpan ChallengeLifetime, TimeSpan HeartbeatInterval, TimeSpan LeaseLifetime, int EventLimit = 10_000,
    long EventByteLimit = 64L * 1024 * 1024, int NonceCacheLimit = 4096, int ExpectedBridgeUid = -1,
    int PendingPermissionLimit = 64, long PendingPermissionByteLimit = 256 * 1024, int MaxConnections = 32, int MaxAuthenticatingConnections = 8)
{
    public static WorkerOptions Production(string controlDirectory, string workerId, string controllerId) => new(
        controlDirectory, workerId, controllerId, Path.Combine(controlDirectory, "bridge.sock"), TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), ExpectedBridgeUid: 1101);
}

public interface IWorkerObservationSink
{
    WorkerEvent AppendEvent(string kind, string payloadJson);
    JournalFailure SetJournalFailure(string errorCategory);
}

public sealed record Lease(long Epoch, string ControllerId, string ConnectionNonce, DateTimeOffset ObservedUtc);
public sealed record WorkerStatus(long WorkerGeneration, long ProcessGeneration, string ProcessState, string? LifecycleHandle,
    long? ObservedPid, string? ActiveRequestId, PendingPermission? PendingPermission, long OwnershipEpoch,
    bool LeaseActive, bool DispatchHeld, string? HoldReason, IReadOnlyList<string> HoldReasons, long FirstRetainedSequence, long LastSequence,
    long AcknowledgedWorkerGeneration, long AcknowledgedSequence, ReplayLoss? ReplayLoss, JournalFailure? JournalFailure,
    int ReplayGapCount, IReadOnlyList<ReplayGap> ReplayGaps, bool ViewerSupported = false, bool ViewerAvailable = false);
public sealed record PendingPermission(long ProcessGeneration, long OwnershipEpoch, string RequestId, string TurnId, string DecisionId,
    string PayloadHash, IReadOnlyList<string> OptionIds, string State, string? Decision);
public sealed record StoredRequest(string RequestId, string PayloadHash, string State, string? OutcomeJson,
    long ProcessGeneration, long OwnershipEpoch, string TurnId, string SessionId);
public sealed record StoredCancellation(string CancellationId, string TargetRequestId, string PayloadHash,
    string State, long ProcessGeneration, long OwnershipEpoch);
public sealed record WorkerEvent(long WorkerGeneration, long Sequence, string Kind, string PayloadJson, int ByteCount);
public sealed record ReplayLoss(long WorkerGeneration, long MarkerSequence, long DroppedCount, long DroppedBytes);
public sealed record JournalFailure(string OperationId, long WorkerGeneration, string ErrorCategory);
public sealed record ReplayGap(string Id, string Kind, long WorkerGeneration, long AfterSequence, long FirstRetainedSequence,
    long LastSequence, long? LossMarkerGeneration, long? LossMarkerSequence);
