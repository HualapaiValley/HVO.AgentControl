using HVO.AgentControl.Organization;
using HVO.AgentControl.Runtime;
using Microsoft.Extensions.Options;

namespace HVO.AgentControl.RemoteWorker;

/// <summary>
/// The resolved execution target for one host id. <see cref="Kind"/> is the
/// persisted <c>transport_kind</c> (<c>local-docker</c> or <c>ssh-docker</c>).
/// <see cref="Ssh"/> is the configured approved SSH host for an SSH target and
/// null for the controller-local Docker target.
/// </summary>
public sealed record ExecutionTarget(string Id, string Kind, ApprovedExecutionHost? Ssh)
{
    public bool IsLocalDocker => string.Equals(Kind, "local-docker", StringComparison.Ordinal);
}

/// <summary>
/// Resolves one stored execution host id into an <see cref="ExecutionTarget"/>.
/// An SSH target must also be present in the configured approved allowlist; a
/// local-docker target never is, and carries no SSH configuration by design.
/// </summary>
public sealed class ExecutionTargetResolver(AcpControlHost control, IOptions<WorkerControlOptions> configured)
{
    private readonly WorkerControlOptions _options = configured.Value;

    public ExecutionTarget Resolve(string hostId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        var store = control.Organization ?? throw new OrganizationStoreException("Organization store unavailable.");
        var host = store.GetExecutionHost(hostId)
            ?? throw new KeyNotFoundException("Execution host is not registered.");
        if (string.Equals(host.TransportKind, "local-docker", StringComparison.Ordinal))
            return new ExecutionTarget(host.Id, "local-docker", null);
        var ssh = _options.ApprovedHosts?.SingleOrDefault(x => string.Equals(x.Id, host.Id, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException("Host is not approved.");
        return new ExecutionTarget(host.Id, "ssh-docker", ssh);
    }
}

/// <summary>
/// Fail-closed execution operations for the controller-local Docker target.
/// Managed hiring targets the local daemon so placement is deterministic, but the
/// local execution path is not implemented in this build: every operation throws
/// a configuration exception, so a managed hire fails with sanitized detail
/// instead of silently SSH-ing anywhere.
/// </summary>
public sealed class LocalDockerUnavailableOperations : IRemoteWorkerProvisioner, IRemoteWorkerOperations
{
    public const string Message = "Controller-local Docker execution is not available in this build.";

    public Task<HostProbePayload> ProbeAsync(ExecutionTarget target, CancellationToken token) => throw Unavailable();
    public Task<HostProbePayload> ProbeAsync(ApprovedExecutionHost host, CancellationToken token) => throw Unavailable();
    public Task<RemoteOperationResult> ExecuteAsync(ApprovedExecutionHost host, RemoteDockerOperation operation, IReadOnlyList<string> tokens, byte[]? standardInput, CancellationToken cancellationToken) => throw Unavailable();
    public Task<RemoteOperationResult> CreateVolumeAsync(ApprovedExecutionHost host, VolumeCreateSpec specification, CancellationToken cancellationToken) => throw Unavailable();
    public Task<RemoteOperationResult> CreateContainerAsync(ApprovedExecutionHost host, ContainerCreateSpec specification, CancellationToken cancellationToken) => throw Unavailable();
    public Task<RemoteOperationResult> BootstrapAsync(ApprovedExecutionHost host, BootstrapSpec specification, byte[] standardInput, CancellationToken cancellationToken) => throw Unavailable();
    public Task<RemoteOperationResult> BuildImageAsync(ApprovedExecutionHost host, ImageBuildSpec specification, byte[] contextTar, CancellationToken cancellationToken) => throw Unavailable();
    public Task<HostProbePayload> ProbeHostAsync(ApprovedExecutionHost host, CancellationToken cancellationToken) => throw Unavailable();
    public Task<string> CreateVolumeAsync(ExecutionTarget target, VolumeCreateSpec spec, CancellationToken token) => throw Unavailable();
    public Task<RemoteResourceInspection> InspectVolumeAsync(ExecutionTarget target, string name, CancellationToken token) => throw Unavailable();
    public Task<string> CreateContainerAsync(ExecutionTarget target, ContainerCreateSpec spec, CancellationToken token) => throw Unavailable();
    public Task<RemoteResourceInspection> InspectContainerAsync(ExecutionTarget target, string name, CancellationToken token) => throw Unavailable();
    public Task BootstrapAsync(ExecutionTarget target, BootstrapSpec spec, byte[] key, CancellationToken token) => throw Unavailable();
    public Task StartAsync(ExecutionTarget target, string container, CancellationToken token) => throw Unavailable();
    public Task StopAsync(ExecutionTarget target, string container, CancellationToken token) => throw Unavailable();
    public Task RemoveContainerAsync(ExecutionTarget target, string container, CancellationToken token) => throw Unavailable();
    public Task RemoveVolumeAsync(ExecutionTarget target, string volume, CancellationToken token) => throw Unavailable();

    private static WorkerControlConfigurationException Unavailable() => new(Message);
}
