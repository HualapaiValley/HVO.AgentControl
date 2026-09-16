using HVO.AgentControl.Organization;
using HVO.AgentControl.Runtime;
using Microsoft.Extensions.Options;

namespace HVO.AgentControl.RemoteWorker;

public sealed record RegisterExecutionHostRequest(string ApprovedHostId, string Slug, string DisplayName);
public sealed record ProbeExecutionHostRequest(int ExpectedRevision);
public sealed record DisableExecutionHostRequest(int ExpectedRevision);

public sealed class ExecutionHostRegistry(AcpControlHost control, IOptions<WorkerControlOptions> configured, IRemoteWorkerOperations operations)
{
    private readonly WorkerControlOptions _options = configured.Value;

    public IReadOnlyList<ExecutionHostRecord> List() => Store().ListExecutionHosts();

    public ExecutionHostRecord Register(RegisterExecutionHostRequest request)
    {
        var host = Approved(request.ApprovedHostId);
        return Store().RegisterExecutionHost(host.Id, host.Hostname, host.Port, host.Username, host.KnownHostsPath, new ExecutionHostRegistration(host.Id, request.Slug, request.DisplayName));
    }

    /// <summary>
    /// Runs the fixed multi-command probe and records what the host reported. A
    /// host that answers but cannot prove a required capability is persisted as
    /// <c>invalid</c>; only an unreachable or unparsable host is an error.
    /// </summary>
    public async Task<ExecutionHostRecord> ProbeAsync(string id, int expectedRevision, CancellationToken cancellationToken)
    {
        if (!_options.Enabled) throw new WorkerControlDisabledException("Worker control is disabled; no host process is executed.");
        var host = Approved(id);
        var payload = await operations.ProbeHostAsync(host, cancellationToken).ConfigureAwait(false);
        var probe = HostProbeParser.Parse(payload, host, _options.ApprovedImagePlatform, _options.ExpectedControllerUid);
        return Store().RecordExecutionHostProbe(id, expectedRevision, probe);
    }

    public ExecutionHostRecord Disable(string id, int expectedRevision) => Store().DisableExecutionHost(id, expectedRevision);

    private ApprovedExecutionHost Approved(string id)
    {
        if (_options.Validate(inspectFiles: _options.Enabled).Count != 0) throw new WorkerControlConfigurationException("Worker control configuration is invalid.");
        return _options.ApprovedHosts.SingleOrDefault(host => string.Equals(host.Id, id, StringComparison.Ordinal)) ?? throw new KeyNotFoundException("The execution host is not in the configured allowlist.");
    }
    private OrganizationStore Store() => control.Organization ?? throw new OrganizationStoreException("The organization store is unavailable.");
}
