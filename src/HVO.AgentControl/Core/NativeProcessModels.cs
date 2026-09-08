namespace HVO.AgentControl.Core;

public static class NativeProcessObservationState
{
    public const string Observed = "Observed", Unknown = "Unknown", Unsupported = "Unsupported", Unavailable = "Unavailable";
}

public static class NativeProcessExitEvidence
{
    public const string Unknown = "Unknown", ExitStatus = "ExitStatus", Signal = "Signal";
}

public sealed record NativeProcessReplacementReceipt(
    string Id,
    int PreviousProcessId,
    string PreviousIncarnation,
    int CurrentProcessId,
    string CurrentIncarnation,
    string Reason,
    string ProcessExitEvidence,
    int? ProcessExitCode,
    string OomEvidence,
    string EnvironmentRestartEvidence);

public sealed record NativeProcessObservation(
    string ManagedServerId,
    string State,
    string Platform,
    int? ProcessId,
    string Incarnation,
    long ObservedAt,
    string Provenance,
    string Detail,
    NativeProcessReplacementReceipt[] Replacements)
{
    public static NativeProcessObservation Unsupported(string managedServerId, long observedAt, string provenance) =>
        new(managedServerId, NativeProcessObservationState.Unsupported, "Unknown", null, "", observedAt, provenance,
            "This transport does not provide a native-process incarnation probe.", []);
}

public sealed record NativeProcessObservationResult(string Freshness, int ReplacementReceipts, int InterruptedCommands);

public sealed record NativeProcessObservationEvidence(
    string RuntimeId,
    string ManagedServerId,
    string State,
    string Platform,
    int? ProcessId,
    string Incarnation,
    long ObservedAt,
    string Provenance,
    string Detail,
    string Freshness);
