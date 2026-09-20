using System.Globalization;
using System.Text.RegularExpressions;

namespace HVO.AgentControl.RemoteWorker;

public enum DockerOperation { Probe, VersionProbe, StorageFree, ImageInspect, ImageTag, ImageBuild, ImageVerify, ImageRemove, VolumeCreate, VolumeInspect, VolumeRemove, ContainerCreate, ContainerInspect, ContainerStart, ContainerStop, ContainerRemove, Bootstrap, Connector, Viewer, LocalStorageFree }

public sealed record WorkerResourceIdentity(string OrganizationId, string ControllerId, string HostId, string WorkerId, string BindingId, string OperationId, string? ProfileRevisionId = null)
{
    public IReadOnlyDictionary<string, string> Labels
    {
        get
        {
            var labels = new Dictionary<string, string>(StringComparer.Ordinal) { ["agentcontrol.generation"] = "2", ["agentcontrol.owner"] = $"{OrganizationId}/{ControllerId}", ["agentcontrol.host"] = HostId, ["agentcontrol.worker"] = WorkerId, ["agentcontrol.binding"] = BindingId, ["agentcontrol.operation"] = OperationId };
            if (ProfileRevisionId is not null) labels["agentcontrol.profile"] = ProfileRevisionId;
            return labels;
        }
    }
}

public sealed record VolumeCreateSpec(string Name, WorkerResourceIdentity Identity);
public sealed record NamedVolumeMount(string Name, string ContainerPath, bool ReadOnly = false);
public sealed record ContainerCreateSpec(string Name, string ImageDigest, string Platform, WorkerResourceIdentity Identity, IReadOnlyList<NamedVolumeMount> Volumes, long MemoryBytes, decimal CpuLimit, int PidsLimit, IReadOnlyList<string>? ApprovedDigests = null, IReadOnlyDictionary<string, string>? EmployeeEnvironment = null);
public sealed record BootstrapSpec(string ControlVolumeName, string ImageDigest, string Platform, WorkerResourceIdentity Identity);
public sealed record ImageBuildSpec(string BaseImageDigest, string Platform, string ProfileRevisionId, string ContextHash, string ResultTag, bool NetworkRequired);
public sealed record DockerPolicy(string ApprovedBaseDigest, string ApprovedPlatform, long MaxMemoryBytes, decimal MaxCpu, int MaxPids, string WorkerTarget = "/app/HVO.AgentControl.Worker.dll", bool RequireAgentControlPrefixes = false);

public sealed class DockerGrammarException(string message) : Exception(message);

public static class DockerArgv
{
    public const int MaximumContextBytes = 64 * 1024 * 1024;
    private static readonly Regex ResourceName = new("^[a-z0-9](?:[a-z0-9_.-]{0,126}[a-z0-9])?$", RegexOptions.CultureInvariant);
    private static readonly Regex Identifier = new("^[A-Za-z0-9](?:[A-Za-z0-9._:-]{0,127})$", RegexOptions.CultureInvariant);
    private static readonly Regex AbsolutePath = new("^/(?:[A-Za-z0-9._-]+/?)*$", RegexOptions.CultureInvariant);
    private static readonly Regex Digest = new("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex ProfileRevisionId = new("^prev-[a-f0-9]{16}$", RegexOptions.CultureInvariant);
    private static readonly Regex ImageReference = new("^agentcontrol-[a-z0-9-]+:[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ContainerPaths = ["/control", "/home/worker", "/workspace", "/session"];
    private static readonly HashSet<string> AllowedEnvironmentKeys = ["TZ", "LANG", "LC_ALL", "EDITOR", "VISUAL", "DOTNET_ROOT", "PATH", "GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL", "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_NOLOGO", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "NPM_CONFIG_UPDATE_NOTIFIER", "NPM_CONFIG_FUND", "PYTHONDONTWRITEBYTECODE", "PIP_DISABLE_PIP_VERSION_CHECK"];

    public static string[] Build(DockerOperation operation, IReadOnlyList<string> tokens, DockerPolicy policy) => operation switch
    {
        DockerOperation.Probe when tokens.Count == 0 => ["docker", "system", "info", "--format", "{{json .}}"],
        DockerOperation.VersionProbe when tokens.Count == 0 => ["docker", "version", "--format", "{{json .Server}}"],
        DockerOperation.StorageFree when tokens.Count == 1 => ["df", "-B1", "--output=avail", "--", ValidAbsolutePath(tokens[0])],
        // The daemon's DockerRootDir is a host path and is intentionally not
        // mounted into the privileged helper. An anonymous local volume uses the
        // daemon's own volume backing filesystem, so a fixed, isolated run of the
        // approved base can measure that filesystem without any host bind mount.
        // --rm also removes the anonymous probe volume after the command exits.
        DockerOperation.LocalStorageFree when tokens.Count == 0 => ["docker", "run", "--rm", "--network", "none", "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--pids-limit", "16", "--platform", ValidPlatform(policy.ApprovedPlatform), "--mount", "type=volume,dst=/probe", "--entrypoint", "/usr/bin/df", ValidDigest(policy.ApprovedBaseDigest), "-B1", "--output=avail", "/probe"],
        DockerOperation.ImageInspect when tokens.Count == 1 => ["docker", "image", "inspect", "--format", "{{json .}}", ValidImageReference(tokens[0])],
        DockerOperation.ImageTag when tokens.Count == 2 => ["docker", "image", "tag", ValidDigest(tokens[0]), ValidLocalImageReference(tokens[1])],
        DockerOperation.ImageRemove when tokens.Count == 1 => ["docker", "image", "rm", "--no-prune", ValidImageReference(tokens[0])],
        DockerOperation.ImageVerify when tokens.Count == 3 => BuildImageVerify(tokens),
        DockerOperation.VolumeInspect when tokens.Count == 1 => ["docker", "volume", "inspect", "--format", "{{json .}}", ValidResource(tokens[0])],
        DockerOperation.ContainerInspect when tokens.Count == 1 => ["docker", "container", "inspect", "--format", "{{json .}}", ValidResource(tokens[0])],
        DockerOperation.ContainerStart when tokens.Count == 1 => ["docker", "container", "start", ValidResource(tokens[0])],
        DockerOperation.ContainerStop when tokens.Count == 1 => ["docker", "container", "stop", "--time", "10", ValidResource(tokens[0])],
        DockerOperation.VolumeRemove when tokens.Count == 1 => ["docker", "volume", "rm", ValidResource(tokens[0])],
        DockerOperation.ContainerRemove when tokens.Count == 1 => ["docker", "container", "rm", ValidResource(tokens[0])],
        DockerOperation.Connector when tokens.Count == 1 => BuildExec(tokens[0], policy.WorkerTarget, viewer: false),
        DockerOperation.Viewer when tokens.Count == 1 => BuildExec(tokens[0], policy.WorkerTarget, viewer: true),
        _ => throw new DockerGrammarException("Docker operation arguments or operation kind are invalid."),
    };

    public static string[] BuildVolumeCreate(VolumeCreateSpec spec)
    {
        RequirePrefix(spec.Name, "agentcontrol-");
        var parts = new List<string> { "docker", "volume", "create" };
        foreach (var label in ExactLabels(spec.Identity)) { parts.Add("--label"); parts.Add(Label(label.Key, label.Value)); }
        parts.Add(ValidResource(spec.Name));
        return [.. parts];
    }

    public static string[] BuildContainerCreate(ContainerCreateSpec spec, DockerPolicy policy)
    {
        if (policy.RequireAgentControlPrefixes) RequirePrefix(spec.Name, "agentcontrol-worker-"); else ValidResource(spec.Name);
        RequireApprovedImage(spec.ImageDigest, spec.Platform, spec.ApprovedDigests, policy, allowApprovedSet: true);
        if (spec.MemoryBytes <= 0 || spec.MemoryBytes > policy.MaxMemoryBytes || spec.CpuLimit <= 0 || spec.CpuLimit > policy.MaxCpu || spec.PidsLimit <= 0 || spec.PidsLimit > policy.MaxPids) throw new DockerGrammarException("Container resource limits must be positive and within helper policy.");
        if (spec.Volumes.Count != 4 || !spec.Volumes.Select(x => x.ContainerPath).ToHashSet(StringComparer.Ordinal).SetEquals(ContainerPaths)) throw new DockerGrammarException("Container must use the four fixed named-volume mount points.");
        var parts = new List<string> { "docker", "container", "create", "--name", ValidResource(spec.Name), "--network", "bridge", "--read-only", "--cap-drop", "ALL", "--cap-add", "CHOWN", "--cap-add", "SETUID", "--cap-add", "SETGID", "--cap-add", "KILL", "--security-opt", "no-new-privileges", "--env", "WORKER_CONTROL_DIRECTORY=/control", "--env", "WORKER_ID=" + ValidIdentifier(spec.Identity.WorkerId), "--env", "WORKER_CONTROLLER_ID=" + ValidIdentifier(spec.Identity.ControllerId), "--pids-limit", Number(spec.PidsLimit), "--memory", Number(spec.MemoryBytes), "--cpus", Number(spec.CpuLimit), "--tmpfs", "/tmp:rw,noexec,nosuid,nodev,size=64m", "--tmpfs", "/run:rw,noexec,nosuid,nodev,size=16m", "--platform", ValidPlatform(spec.Platform) };
        foreach (var variable in (spec.EmployeeEnvironment ?? new Dictionary<string, string>()).OrderBy(x => x.Key, StringComparer.Ordinal)) { ValidateEnvironmentEntry(variable.Key, variable.Value); parts.Add("--env"); parts.Add(variable.Key + "=" + variable.Value); }
        foreach (var label in ExactLabels(spec.Identity)) { parts.Add("--label"); parts.Add(Label(label.Key, label.Value)); }
        foreach (var mount in spec.Volumes.OrderBy(x => x.ContainerPath, StringComparer.Ordinal))
        {
            if (policy.RequireAgentControlPrefixes) RequirePrefix(mount.Name, VolumePrefix(mount.ContainerPath)); else ValidResource(mount.Name);
            parts.Add("--mount"); parts.Add($"type=volume,src={ValidResource(mount.Name)},dst={mount.ContainerPath},volume-nocopy{(mount.ReadOnly ? ",readonly" : string.Empty)}");
        }
        parts.Add(ValidDigest(spec.ImageDigest));
        return [.. parts];
    }

    public static string[] BuildBootstrap(BootstrapSpec spec, DockerPolicy policy)
    {
        if (policy.RequireAgentControlPrefixes) RequirePrefix(spec.ControlVolumeName, "agentcontrol-control-"); else ValidResource(spec.ControlVolumeName);
        RequireApprovedImage(spec.ImageDigest, spec.Platform, null, policy, allowApprovedSet: false);
        var parts = new List<string> { "docker", "run", "--rm", "-i", "--user", "1101:1101", "--network", "none", "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--pids-limit", "64", "--env", "WORKER_CONTROL_DIRECTORY=/control" };
        AddFixedDotnetEnvironment(parts);
        parts.AddRange(["--mount", $"type=volume,src={ValidResource(spec.ControlVolumeName)},dst=/control", "--platform", ValidPlatform(spec.Platform), "--entrypoint", "/usr/bin/dotnet"]);
        foreach (var label in ExactLabels(spec.Identity)) { parts.Add("--label"); parts.Add(Label(label.Key, label.Value)); }
        parts.Add(ValidDigest(spec.ImageDigest)); parts.Add(policy.WorkerTarget); parts.Add("--worker-bootstrap-key");
        return [.. parts];
    }

    public static string[] BuildImageBuild(ImageBuildSpec spec)
    {
        ValidDigest(spec.BaseImageDigest); ValidDigest(spec.ContextHash);
        if (!ProfileRevisionId.IsMatch(spec.ProfileRevisionId)) throw new DockerGrammarException("Image build profile revision id is invalid.");
        var parts = new List<string> { "docker", "build", "--quiet", "--pull=false", "--no-cache", "--network", spec.NetworkRequired ? "default" : "none", "--platform", ValidPlatform(spec.Platform), "--label", $"agentcontrol.profile={spec.ProfileRevisionId}", "--label", $"agentcontrol.context-hash={spec.ContextHash}", "--label", $"agentcontrol.base-digest={spec.BaseImageDigest}", "--tag", ValidLocalImageReference(spec.ResultTag), "-" };
        return [.. parts];
    }

    public static void RequireOwnedLabels(IReadOnlyDictionary<string, string> actual, WorkerResourceIdentity identity)
    {
        // Containers inherit the verified profile image's context/base provenance
        // labels. They do not assert resource ownership; all ownership labels still
        // have to match exactly, and every other AgentControl label is rejected.
        var allowed = identity.Labels.Keys
            .Append("agentcontrol.context-hash")
            .Append("agentcontrol.base-digest")
            .ToHashSet(StringComparer.Ordinal);
        if (actual.Keys.Any(label => label.StartsWith("agentcontrol.", StringComparison.Ordinal) && !allowed.Contains(label)))
            throw new DockerGrammarException("The resource carries unexpected AgentControl labels.");
        foreach (var expected in identity.Labels) if (!actual.TryGetValue(expected.Key, out var value) || !string.Equals(value, expected.Value, StringComparison.Ordinal)) throw new DockerGrammarException("The resource is not exactly owned by this operation.");
    }

    private static string[] BuildImageVerify(IReadOnlyList<string> tokens) => ["docker", "run", "--rm", "--network", "none", "--read-only", "--cap-drop", "ALL", "--cap-add", "DAC_READ_SEARCH", "--security-opt", "no-new-privileges", "--pids-limit", "32", "--platform", ValidPlatform(tokens[2]), "--mount", $"type=image,source={ValidLocalImageReference(tokens[0])},target=/candidate,readonly", "--entrypoint", "/usr/bin/python3", ValidDigest(tokens[1]), "-I", "-S", "/usr/local/bin/profile-image-verify"];
    private static string[] BuildExec(string container, string target, bool viewer)
    {
        var parts = new List<string> { "docker", "container", "exec", "-i", "--user", "1101:1101" }; AddFixedDotnetEnvironment(parts); parts.Add(ValidResource(container)); parts.Add("/usr/bin/dotnet"); parts.Add(target); parts.Add(viewer ? "--worker-viewer-pipe" : "--worker-pipe"); return [.. parts];
    }
    private static void AddFixedDotnetEnvironment(List<string> parts) { foreach (var value in new[] { "HOME=/control", "PATH=/usr/bin:/bin", "DOTNET_ROOT=/usr/share/dotnet", "DOTNET_STARTUP_HOOKS=", "DOTNET_ADDITIONAL_DEPS=", "DOTNET_SHARED_STORE=", "LD_PRELOAD=", "LD_AUDIT=", "LD_LIBRARY_PATH=" }) { parts.Add("--env"); parts.Add(value); } }
    private static IEnumerable<KeyValuePair<string, string>> ExactLabels(WorkerResourceIdentity identity) { if (identity.Labels.Count != (identity.ProfileRevisionId is null ? 6 : 7)) throw new DockerGrammarException("Resource label set is invalid."); if (identity.ProfileRevisionId is not null && !ProfileRevisionId.IsMatch(identity.ProfileRevisionId)) throw new DockerGrammarException("Resource profile label is invalid."); foreach (var label in identity.Labels.OrderBy(x => x.Key, StringComparer.Ordinal)) { if (!Identifier.IsMatch(label.Value.Replace("/", ":", StringComparison.Ordinal))) throw new DockerGrammarException("Resource label value is invalid."); yield return label; } }
    private static void RequireApprovedImage(string digest, string platform, IReadOnlyList<string>? approvedDigests, DockerPolicy policy, bool allowApprovedSet) { ValidDigest(policy.ApprovedBaseDigest); ValidDigest(digest); if (platform != policy.ApprovedPlatform) throw new DockerGrammarException("Container platform differs from helper policy."); if (!string.Equals(digest, policy.ApprovedBaseDigest, StringComparison.Ordinal) && (!allowApprovedSet || approvedDigests is null || approvedDigests.Any(x => !Digest.IsMatch(x)) || !approvedDigests.Contains(policy.ApprovedBaseDigest, StringComparer.Ordinal) || !approvedDigests.Contains(digest, StringComparer.Ordinal))) throw new DockerGrammarException("Container image is not approved."); }
    private static void ValidateEnvironmentEntry(string name, string value) { if (!AllowedEnvironmentKeys.Contains(name) || value.Length > 256 || value.Any(c => char.IsControl(c) || c > 0x7e) || value.Contains("${", StringComparison.Ordinal) || value.Contains("$(", StringComparison.Ordinal)) throw new DockerGrammarException("Container environment entry is invalid."); if (name == "PATH" && value != "/opt/dotnet-sdk:/usr/local/bin:/usr/bin:/bin") throw new DockerGrammarException("Container PATH is invalid."); if (name == "DOTNET_ROOT" && value != "/opt/dotnet-sdk") throw new DockerGrammarException("Container DOTNET_ROOT is invalid."); }
    private static string ValidAbsolutePath(string value) { if (value is not { Length: >= 1 and <= 512 } || !AbsolutePath.IsMatch(value) || value.Split('/').Any(segment => segment is "." or "..")) throw new DockerGrammarException("The storage path is not a fixed absolute path."); return value; }
    private static string ValidDigest(string value) => Digest.IsMatch(value) ? value : throw new DockerGrammarException("Image digest is invalid.");
    private static string ValidImageReference(string value) => Digest.IsMatch(value) ? value : ValidLocalImageReference(value);
    private static string ValidLocalImageReference(string value) => ImageReference.IsMatch(value) && value.Length <= 200 ? value : throw new DockerGrammarException("Image reference is invalid.");
    private static string ValidResource(string value) => ResourceName.IsMatch(value) ? value : throw new DockerGrammarException("Resource name is invalid.");
    private static string ValidIdentifier(string value) => Identifier.IsMatch(value) ? value : throw new DockerGrammarException("Identifier is invalid.");
    private static string ValidPlatform(string value) => value is "linux/amd64" or "linux/arm64" ? value : throw new DockerGrammarException("Platform is invalid.");
    private static string Label(string key, string value) => key.StartsWith("agentcontrol.", StringComparison.Ordinal) ? key + "=" + value : throw new DockerGrammarException("Resource label key is invalid.");
    private static string Number<T>(T value) where T : IFormattable => value.ToString(null, CultureInfo.InvariantCulture);
    private static void RequirePrefix(string value, string prefix) { ValidResource(value); if (!value.StartsWith(prefix, StringComparison.Ordinal)) throw new DockerGrammarException("Resource name is outside the fixed AgentControl prefix."); }
    private static string VolumePrefix(string path) => path switch { "/control" => "agentcontrol-control-", "/home/worker" => "agentcontrol-home-", "/workspace" => "agentcontrol-workspace-", "/session" => "agentcontrol-session-", _ => throw new DockerGrammarException("Container mount path is not fixed.") };
}
