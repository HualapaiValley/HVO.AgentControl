namespace HVO.AgentControl.Core;

// Deliberately excludes prompts, transcripts, credentials, model catalogs and diagnostic text.
public sealed record WatchdogStatus(int Version, long ObservedAt, bool Complete, WatchdogSnapshot Snapshot,
    IReadOnlyList<WatchdogCoordination> Coordinations);
public sealed record WatchdogSnapshot(IReadOnlyList<WatchdogRuntime> Runtimes, IReadOnlyList<WatchdogWorker> Workers,
    IReadOnlyList<WatchdogCommand> Commands, IReadOnlyList<WatchdogRequest> Requests,
    IReadOnlyList<WatchdogProviderFailure> ProviderFailures, IReadOnlyList<WatchdogGitHubAccess> GitHubAccess,
    IReadOnlyList<WatchdogNativeError> NativeErrors);
public sealed record WatchdogRuntime(string Id, bool DesiredConnected, string Transport, string Health);
public sealed record WatchdogWorker(string Id, string Activity, bool Stale);
public sealed record WatchdogCommand(string Id, string? WorkerId, string State, long CreatedAt, long? LastProgressAt);
public sealed record WatchdogRequest(string Id, string WorkerId, string State);
public sealed record WatchdogProviderFailure(string CommandId, string Category, int? Status);
public sealed class WatchdogNativeError
{
    public string CommandId { get; set; } = "";
    public string? Name { get; set; }
    public int? Status { get; set; }
    public string? Code { get; set; }
}
public sealed record WatchdogGitHubAccess(string Id, string State, long? ExpiresAt);
public sealed record WatchdogCoordination(string Id, string State, string WorkerIdsJson, string InputJson);
