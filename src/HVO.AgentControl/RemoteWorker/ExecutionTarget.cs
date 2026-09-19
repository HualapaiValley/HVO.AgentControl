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
