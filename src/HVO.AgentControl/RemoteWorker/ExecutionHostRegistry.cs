using System.Security.Cryptography;
using System.Text.Json;
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

    public async Task<ExecutionHostRecord> ProbeAsync(string id, int expectedRevision, CancellationToken cancellationToken)
    {
        if (!_options.Enabled) throw new InvalidOperationException("Worker control is disabled; no host process is executed.");
        var host = Approved(id); var result = await operations.ExecuteAsync(host, RemoteDockerOperation.Probe, [], null, cancellationToken);
        if (result.ExitCode != 0 || result.StandardOutput.Length > 1024 * 1024) throw new InvalidOperationException("The bounded host probe failed.");
        var probe = ParseProbe(result.StandardOutput, host, _options.ApprovedImagePlatform, _options.ExpectedControllerUid);
        return Store().RecordExecutionHostProbe(id, expectedRevision, probe);
    }

    public ExecutionHostRecord Disable(string id, int expectedRevision) => Store().DisableExecutionHost(id, expectedRevision);

    public static ExecutionHostProbe ParseProbe(string json, ApprovedExecutionHost host, string expectedPlatform, int? expectedUid = null)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 }); var root = document.RootElement;
        string Required(string name, int maximum = 128) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text && text.Length <= maximum ? text : throw new InvalidOperationException($"Probe field {name} is invalid.");
        long RequiredLong(string name) => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) && number >= 0 ? number : throw new InvalidOperationException($"Probe field {name} is invalid.");
        bool RequiredBool(string name) => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new InvalidOperationException($"Probe field {name} is invalid.");
        var os = Required("OSType"); var architecture = Required("Architecture"); var shared = RequiredBool("SharedStorage"); var memory = RequiredLong("MemTotal"); var cpu = checked((int)RequiredLong("NCPU")); var free = RequiredLong("VolumeFreeBytes"); var limits = RequiredBool("LimitsSupported"); var platform = $"{os}/{architecture}";
        var valid = os == "linux" && architecture is "amd64" or "arm64" && !shared && free >= 1024L * 1024 * 1024 && memory >= 512L * 1024 * 1024 && cpu > 0 && limits && platform == expectedPlatform;
        var knownHost = KnownHostsParser.Parse(host, expectedUid ?? ControllerPrivateFile.EffectiveUid);
        return new ExecutionHostProbe(knownHost.Algorithm, knownHost.Fingerprint, knownHost.ContentHash, Required("ServerVersion"), Required("ApiVersion"), architecture, Required("Driver"), Required("BackingFilesystem"), shared, free, memory, cpu, limits, platform, valid ? "valid" : "invalid");
    }

    private ApprovedExecutionHost Approved(string id)
    {
        if (_options.Validate(inspectFiles: _options.Enabled).Count != 0) throw new InvalidOperationException("Worker control configuration is invalid.");
        return _options.ApprovedHosts.SingleOrDefault(host => string.Equals(host.Id, id, StringComparison.Ordinal)) ?? throw new KeyNotFoundException("The execution host is not in the configured allowlist.");
    }
    private OrganizationStore Store() => control.Organization ?? throw new OrganizationStoreException("The organization store is unavailable.");
}
