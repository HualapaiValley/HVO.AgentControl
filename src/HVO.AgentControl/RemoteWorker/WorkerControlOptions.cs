using System.Text.RegularExpressions;

namespace HVO.AgentControl.RemoteWorker;

public sealed class WorkerControlOptions
{
    public const string SectionName = "WorkerControl";

    /// <summary>
    /// The fixed Docker network for every controller-provisioned worker container.
    /// Docker's default <c>bridge</c> grants the worker <em>unrestricted outbound
    /// egress</em>: it can reach the public Internet through the daemon's NAT, the
    /// Docker host's bridge gateway and any host services listening there, and any
    /// other container co-attached to the same default bridge. The safety guarantee
    /// is therefore only ingress-free and loopback/private inside the container, not
    /// network isolation or egress restriction: the fixed command publishes no
    /// ingress (no <c>-p</c>, <c>--publish</c> or <c>--expose</c> and empty port
    /// bindings), the ACP/TUI HTTP listener binds container loopback only, and the
    /// supervisor Unix socket is a container-private path. The name is a constant
    /// rather than an operator knob so it cannot be pointed at the host network or
    /// an arbitrary network. A per-worker dedicated network, or a host firewall/NAT
    /// policy that restricts egress to the approved provider, is possible future
    /// work. The hermetic local <c>worker</c> Compose profile keeps
    /// <c>network_mode: none</c> because it is not a live provider runtime.
    /// </summary>
    public const string ContainerNetworkMode = "bridge";

    public bool Enabled { get; set; }
    public string ControllerId { get; set; } = string.Empty;
    public ApprovedExecutionHost[] ApprovedHosts { get; set; } = [];
    public string ApprovedImageDigest { get; set; } = string.Empty;
    public string ApprovedImagePlatform { get; set; } = "linux/amd64";
    public string ConnectorExecutable { get; set; } = "/usr/bin/ssh";
    public string WorkerTarget { get; set; } = "/app/HVO.AgentControl.Worker.dll";
    public string LocalDockerHelperSocketPath { get; set; } = HVO.AgentControl.DockerHelper.Protocol.DockerHelperProtocol.DefaultSocketPath;
    public string ContainerPrefix { get; set; } = "agentcontrol-worker-";
    public string ControlVolumePrefix { get; set; } = "agentcontrol-control-";
    public string HomeVolumePrefix { get; set; } = "agentcontrol-home-";
    public string WorkspaceVolumePrefix { get; set; } = "agentcontrol-workspace-";
    public string SessionVolumePrefix { get; set; } = "agentcontrol-session-";
    public bool HostedManagerEnabled { get; set; }
    public int ConnectTimeoutSeconds { get; set; } = 10;
    public int AuthenticationTimeoutSeconds { get; set; } = 10;
    public int OperationTimeoutSeconds { get; set; } = 60;
    /// <summary>Bound for one profile image build or verification run; builds pull nothing but may run apt.</summary>
    public int ImageBuildTimeoutSeconds { get; set; } = 900;
    public int ExpectedControllerUid { get; set; } = 1001;
    public long MemoryBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public decimal CpuLimit { get; set; } = 2;
    public int PidsLimit { get; set; } = 256;

    public IReadOnlyList<string> Validate(bool inspectFiles = true)
    {
        var errors = new List<string>();
        if (!ValidId(ControllerId)) errors.Add("ControllerId must be a stable bounded identifier.");
        if (!Path.IsPathRooted(ConnectorExecutable) || ConnectorExecutable != "/usr/bin/ssh") errors.Add("ConnectorExecutable must be /usr/bin/ssh.");
        if (WorkerTarget != "/app/HVO.AgentControl.Worker.dll") errors.Add("WorkerTarget must be /app/HVO.AgentControl.Worker.dll.");
        if (Enabled && !Path.IsPathRooted(LocalDockerHelperSocketPath)) errors.Add("LocalDockerHelperSocketPath must be absolute when WorkerControl is enabled.");
        if (!Regex.IsMatch(ApprovedImageDigest ?? string.Empty, "^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)) errors.Add("ApprovedImageDigest must be a lowercase sha256 digest.");
        if (ApprovedImagePlatform is not ("linux/amd64" or "linux/arm64")) errors.Add("ApprovedImagePlatform must be linux/amd64 or linux/arm64.");
        if (ConnectTimeoutSeconds is < 1 or > 60 || AuthenticationTimeoutSeconds is < 1 or > 60 || OperationTimeoutSeconds is < 1 or > 600) errors.Add("WorkerControl timeouts are outside their bounds.");
        if (ImageBuildTimeoutSeconds is < 60 or > 3600) errors.Add("WorkerControl image build timeout is outside 60-3600 seconds.");
        if (ExpectedControllerUid < 0) errors.Add("ExpectedControllerUid is invalid.");
        if (MemoryBytes < 256L * 1024 * 1024 || CpuLimit is <= 0 or > 64 || PidsLimit is < 32 or > 4096) errors.Add("Worker resource limits are invalid.");
        foreach (var host in ApprovedHosts ?? []) errors.AddRange(host.Validate(Enabled && inspectFiles).Select(value => $"ApprovedHosts[{host.Id}]: {value}"));
        if ((ApprovedHosts ?? []).GroupBy(host => host.Id, StringComparer.Ordinal).Any(group => group.Count() != 1)) errors.Add("Approved host IDs must be unique.");
        // ApprovedHosts may be empty when enabled: managed hiring targets the
        // controller-local Docker daemon, so no SSH host is required. The SSH
        // manual path simply has no hosts to offer.
        return errors;
    }

    internal static bool ValidId(string? value) => value is { Length: >= 1 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
}

public sealed class ApprovedExecutionHost
{
    public string Id { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public string KnownHostsPath { get; set; } = string.Empty;
    public string IdentityFilePath { get; set; } = string.Empty;

    public IReadOnlyList<string> Validate(bool inspectFiles)
    {
        var errors = new List<string>();
        if (!WorkerControlOptions.ValidId(Id)) errors.Add("invalid stable host ID.");
        if (Hostname is not { Length: >= 1 and <= 253 } || !Regex.IsMatch(Hostname, "^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?$", RegexOptions.CultureInvariant)) errors.Add("invalid configured hostname.");
        if (Port is < 1 or > 65535) errors.Add("port is invalid.");
        if (Username is not { Length: >= 1 and <= 64 } || !Regex.IsMatch(Username, "^[a-z_][a-z0-9_-]*$", RegexOptions.CultureInvariant)) errors.Add("username is invalid.");
        if (!Path.IsPathRooted(KnownHostsPath) || !Path.IsPathRooted(IdentityFilePath)) errors.Add("known_hosts and identity paths must be absolute.");
        if (string.Equals(Path.GetFullPath(KnownHostsPath), Path.GetFullPath(IdentityFilePath), StringComparison.Ordinal)) errors.Add("known_hosts and identity paths must differ.");
        if (inspectFiles)
        {
            var expectedUid = ControllerPrivateFile.EffectiveUid;
            ValidatePrivateFile(KnownHostsPath, expectedUid, ControllerFileModes.Private0600 | ControllerFileModes.PublicKey0644, "known_hosts", errors);
            ValidatePrivateFile(IdentityFilePath, expectedUid, ControllerFileModes.Private0600, "identity", errors);
        }
        return errors;
    }

    private static void ValidatePrivateFile(string path, int expectedUid, ControllerFileModes modes, string name, List<string> errors)
    {
        try { using var ignored = ControllerPrivateFile.OpenRead(path, expectedUid, modes); }
        catch { errors.Add($"{name} file is not a controller-owned regular single-link file with an exact approved mode."); }
    }
}
