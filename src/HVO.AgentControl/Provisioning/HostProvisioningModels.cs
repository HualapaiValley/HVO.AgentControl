using System.Collections.Immutable;
using System.Text.Json;

namespace HVO.AgentControl.Provisioning;

// Construct only in the trusted host process. These are not web request DTOs.
public sealed record ProvisionerHostAuthority(string HostId, long Revision, string Transport,
    string WorkspaceRoot, string ToolStateDirectory, string NodePath, string CliBundlePath,
    string DockerPath, string GitPath, string DockerSocket, string DockerEngineId,
    ImmutableDictionary<string, ApprovedProvisionWorkspace> Workspaces);

public sealed record ProvisionToolProbe(string Name, ImmutableArray<string> Command, string ExpectedOutput);
public sealed record ApprovedProvisionWorkspace(string Id, string Directory, string Repository,
    string SourceRevision, string ConfigurationPath, string ConfigurationSha256,
    string RemoteUser, string ContainerWorkspace, ImmutableArray<ProvisionToolProbe> Tools);

public sealed record HostProvisionRequest(string OperationId, string WorkspaceId, bool ColdBuild = false);
public sealed record ProvisionIntent(string OperationId, string HostId, long AuthorityRevision,
    ApprovedProvisionWorkspace Workspace, string CliVersion, string CliSha256, bool ColdBuild,
    string Digest, ImmutableDictionary<string, string> Labels);
public sealed class ProvisionIntentConflictException : Exception;
public enum ProvisionAction { CreateOrObserve, Observe, Remove }

// The caller MUST durably bind OperationId -> Digest, serialize all users of this
// operation/workspace, and hold the admission until disposal. It owns capacity and
// authorization. Implementations must reject conflicting intent across restarts.
// Remove authority includes the caller's preservation/backup or explicit loss
// disposition for container-local state and any missing/unverified bind source.
public interface IProvisionAttemptLedger
{
    Task<IProvisionAttempt> Acquire(ProvisionIntent intent, ProvisionAction action, CancellationToken token);
}
public interface IProvisionAttempt : IAsyncDisposable
{
    // Read committed history under the same held admission. This early check
    // avoids requiring vanished source/CLI inputs for an already consumed attempt.
    Task<bool> HasEffect(string effect, string resourceId, CancellationToken token);
    // Commit effect-start before returning true, once only. A lost process or an
    // uncertain result NEVER resets this permission. Removal is a separate intent.
    Task<bool> TryBeginEffect(string effect, string resourceId, CancellationToken token);
}

public sealed record ProvisionProgress(long Sequence, string Stage, string Text);
public sealed record ProvisionProcessRequest(string Executable, ImmutableArray<string> Arguments,
    string WorkingDirectory, ImmutableDictionary<string, string> Environment, TimeSpan Timeout, int OutputLimit);
public sealed record ProvisionProcessResult(bool Started, int? ExitCode, bool Interrupted, bool Truncated,
    string StandardOutput, string StandardError, bool ProgressTruncated = false);
public interface IProvisionProcessRunner
{
    Task<ProvisionProcessResult> Run(ProvisionProcessRequest request, Action<string>? progress, CancellationToken token);
}

public sealed record ProvisionContainerObservation(string ContainerId, string ImageId, string ImageReference,
    string ContainerUser, bool Running, ImmutableDictionary<string, string> Labels,
    ImmutableArray<ProvisionMount> Mounts);
public sealed record ProvisionMount(string Type, string Source, string Destination, string? VolumeName, bool Writable);
public sealed record ProvisionExecutionEvidence(string RemoteUser, string UserId, string Workspace,
    ImmutableDictionary<string, string> Tools);
public sealed record HostProvisionResult(string OperationId, string State, string Code, bool EffectStarted,
    ProvisionIntent? Requested, JsonElement? ResolvedConfiguration, ProvisionContainerObservation? Observed,
    ProvisionExecutionEvidence? Executed, ImmutableArray<ProvisionProgress> Progress,
    ImmutableArray<string> RetainedResources);
