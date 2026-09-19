using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace HVO.AgentControl.RemoteWorker;

public enum RemoteDockerOperation { Probe, VersionProbe, StorageFree, ImageInspect, ImageTag, ImageBuild, ImageVerify, ImageRemove, VolumeCreate, VolumeInspect, VolumeRemove, ContainerCreate, ContainerInspect, ContainerStart, ContainerStop, ContainerRemove, Bootstrap, Connector }
public sealed record RemoteCommand(string Executable, IReadOnlyList<string> Arguments, byte[]? StandardInput = null);
public sealed record RemoteOperationResult(int ExitCode, string StandardOutput, string ErrorCategory);
/// <summary>
/// The exact label set every resource an enrollment owns must carry. A worker
/// provisioned from a verified profile build additionally carries
/// <c>agentcontrol.profile</c> (the immutable revision id); a base-image
/// enrollment carries exactly the pre-profile six labels, so the resource label
/// hashes of existing enrollments are unchanged and their ownership checks
/// still match.
/// </summary>
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

/// <summary>
/// The raw, locally assembled result of the fixed multi-command host probe. Each
/// field is exactly what one fixed remote command printed; no field is synthesized
/// by the controller and none is interpreted here.
/// </summary>
public sealed record HostProbePayload(string InfoJson, string VersionJson, string DockerRootDir, long FreeBytes);

public sealed record VolumeCreateSpec(string Name, WorkerResourceIdentity Identity);
public sealed record NamedVolumeMount(string Name, string ContainerPath, bool ReadOnly = false);
/// <summary>
/// <paramref name="ApprovedDigests"/> is the host's approved set at the time the
/// command is built: the configured base plus every verified profile build on
/// that host. The command builder refuses any other digest.
/// </summary>
public sealed record ContainerCreateSpec(string Name, string ImageDigest, string Platform, WorkerResourceIdentity Identity, IReadOnlyList<NamedVolumeMount> Volumes, long MemoryBytes, decimal CpuLimit, int PidsLimit, IReadOnlyList<string>? ApprovedDigests = null);

/// <summary>
/// The ephemeral key-bootstrap container. It mounts only the control volume and
/// runs before any long-lived container exists, so the enrolled key is present
/// before the worker supervisor can ever start.
/// </summary>
public sealed record BootstrapSpec(string ControlVolumeName, string ImageDigest, string Platform, WorkerResourceIdentity Identity);

/// <summary>
/// One profile image build on one host: the exact base digest the generated
/// Dockerfile extends (addressed through the controller-owned pin tag), the
/// revision and context hash that label the result, and the tar context the
/// build reads from standard input. Nothing else reaches the build: no build
/// arguments, no secrets, no host paths, no network unless the profile's fixed
/// recipes need apt.
/// </summary>
public sealed record ImageBuildSpec(string BaseImageDigest, string Platform, string ProfileRevisionId, string ContextHash, string ResultTag, bool NetworkRequired);

public interface IRemoteWorkerOperations
{
    Task<RemoteOperationResult> ExecuteAsync(ApprovedExecutionHost host, RemoteDockerOperation operation, IReadOnlyList<string> tokens, byte[]? standardInput, CancellationToken cancellationToken);
    Task<RemoteOperationResult> CreateVolumeAsync(ApprovedExecutionHost host, VolumeCreateSpec specification, CancellationToken cancellationToken);
    Task<RemoteOperationResult> CreateContainerAsync(ApprovedExecutionHost host, ContainerCreateSpec specification, CancellationToken cancellationToken);
    Task<RemoteOperationResult> BootstrapAsync(ApprovedExecutionHost host, BootstrapSpec specification, byte[] standardInput, CancellationToken cancellationToken);
    Task<RemoteOperationResult> BuildImageAsync(ApprovedExecutionHost host, ImageBuildSpec specification, byte[] contextTar, CancellationToken cancellationToken);
    Task<HostProbePayload> ProbeHostAsync(ApprovedExecutionHost host, CancellationToken cancellationToken);
}

/// <summary>
/// Encodes the controller-held 32-byte bridge key exactly the way the worker
/// bootstrap entry point parses it: canonical base64 followed by one newline.
/// Both the intermediate characters and the returned buffer are the caller's to
/// zero; nothing is retained here.
/// </summary>
public static class WorkerBootstrapEncoding
{
    public const int KeyBytes = 32;

    /// <summary>Returns UTF-8 <c>base64(key) + "\n"</c>, zeroing every intermediate buffer.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyBytes) throw new WorkerControlConfigurationException("The bridge key must be exactly 32 bytes.");
        Span<char> characters = stackalloc char[48];
        try
        {
            if (!Convert.TryToBase64Chars(key, characters, out var written) || written != 44) throw new WorkerControlConfigurationException("The bridge key could not be encoded.");
            var encoded = new byte[written + 1];
            for (var index = 0; index < written; index++) encoded[index] = checked((byte)characters[index]);
            encoded[^1] = (byte)'\n';
            return encoded;
        }
        finally { characters.Clear(); }
    }
}

public static class RemoteWorkerCommandBuilder
{
    private static readonly System.Text.RegularExpressions.Regex ResourceName = new("^[a-z0-9](?:[a-z0-9_.-]{0,126}[a-z0-9])?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex Identifier = new("^[A-Za-z0-9](?:[A-Za-z0-9._:-]{0,127})$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex AbsolutePath = new("^/(?:[A-Za-z0-9._-]+/?)*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ContainerPaths = ["/control", "/home/worker", "/workspace", "/session"];

    public static RemoteCommand Build(ApprovedExecutionHost host, WorkerControlOptions options, RemoteDockerOperation operation, IReadOnlyList<string> tokens, byte[]? stdin = null)
    {
        if (host.Validate(false).Count != 0) throw new WorkerControlConfigurationException("Approved host configuration is invalid.");
        var remote = operation switch
        {
            RemoteDockerOperation.Probe when tokens.Count == 0 => "docker system info --format '{{json .}}'",
            RemoteDockerOperation.VersionProbe when tokens.Count == 0 => "docker version --format '{{json .Server}}'",
            RemoteDockerOperation.StorageFree when tokens.Count == 1 => $"df -B1 --output=avail -- {QuoteAbsolutePath(tokens[0])}",
            RemoteDockerOperation.ImageInspect when tokens.Count == 1 => $"docker image inspect --format '{{{{json .}}}}' {QuoteImageReference(tokens[0])}",
            // Pins the exact base digest under the controller-owned tag the generated
            // Dockerfile's FROM names; BuildKit refuses an image id as a FROM source.
            RemoteDockerOperation.ImageTag when tokens.Count == 2 => $"docker image tag {QuoteDigest(tokens[0])} {QuoteImageReference(tokens[1])}",
            RemoteDockerOperation.ImageRemove when tokens.Count == 1 => $"docker image rm --no-prune {QuoteImageReference(tokens[0])}",
            // Fixed post-build contract check. It runs the APPROVED BASE image (never
            // the candidate) with the candidate's filesystem mounted read-only at
            // /candidate, so no executable supplied by the candidate is ever run;
            // the base's python3 walks the mount, hashes the contract artifacts and
            // prints one JSON object. Tokens: candidate tag, base digest, platform.
            RemoteDockerOperation.ImageVerify when tokens.Count == 3 => $"docker run --rm --network 'none' --read-only --cap-drop 'ALL' --security-opt 'no-new-privileges' --pids-limit '32' --platform {QuotePlatform(tokens[2])} --mount {QuoteShell($"type=image,source={ValidatedImageReference(tokens[0])},target=/candidate,readonly")} --entrypoint '/usr/bin/python3' {QuoteDigest(tokens[1])} '-c' {QuoteShell(ImageVerifyProgram)}",
            RemoteDockerOperation.VolumeInspect when tokens.Count == 1 => $"docker volume inspect --format '{{{{json .}}}}' {QuoteResource(tokens[0])}",
            RemoteDockerOperation.ContainerInspect when tokens.Count == 1 => $"docker container inspect --format '{{{{json .}}}}' {QuoteResource(tokens[0])}",
            RemoteDockerOperation.ContainerStart when tokens.Count == 1 => $"docker container start {QuoteResource(tokens[0])}",
            RemoteDockerOperation.ContainerStop when tokens.Count == 1 => $"docker container stop --time 10 {QuoteResource(tokens[0])}",
            RemoteDockerOperation.VolumeRemove when tokens.Count == 1 => $"docker volume rm {QuoteResource(tokens[0])}",
            RemoteDockerOperation.ContainerRemove when tokens.Count == 1 => $"docker container rm {QuoteResource(tokens[0])}",
            RemoteDockerOperation.Connector when tokens.Count == 1 => $"docker container exec -i --user '1101:1101' {QuoteResource(tokens[0])} '/usr/bin/dotnet' {QuoteShell(options.WorkerTarget)} '--worker-pipe'",
            _ => throw new WorkerControlConfigurationException("Remote operation arguments or operation kind are invalid."),
        };
        return BuildSsh(host, options, remote, stdin);
    }

    /// <summary>
    /// The verification program the built image must run to completion. It is a
    /// fixed Python program (the base ships python3 and the trailer asserts it),
    /// reads only the filesystem, and emits one JSON line. A profile cannot
    /// influence it: it is a controller constant, not context. It is carried as
    /// base64 inside a one-line bootstrap so the remote token contains no quotes
    /// or control characters.
    /// </summary>
    public static readonly string ImageVerifyProgram =
        "import base64;exec(base64.b64decode(" + '"' + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(ImageVerifyScript)) + '"' + ").decode())";

    public const string ImageVerifyScript =
        "import hashlib,json,os,stat\n" +
        "C=\"/candidate\"\n" +
        "def mode(p):\n  st=os.lstat(C+p); return {\"uid\":st.st_uid,\"gid\":st.st_gid,\"mode\":stat.S_IMODE(st.st_mode)}\n" +
        "def sha(p):\n  h=hashlib.sha256()\n  with open(p,\"rb\") as f:\n    for b in iter(lambda: f.read(1<<20), b\"\"): h.update(b)\n  return h.hexdigest()\n" +
        "def tree(p):\n  h=hashlib.sha256()\n  for root,dirs,files in os.walk(p):\n    dirs.sort()\n    for f in sorted(files):\n      q=os.path.join(root,f)\n      if os.path.islink(q) or not os.path.isfile(q): continue\n      h.update(os.path.relpath(q,p).encode()); h.update(sha(q).encode())\n  return h.hexdigest()\n" +
        "def same(p):\n  a=C+p\n  if not os.path.exists(a) or not os.path.exists(p): return False\n  if os.path.islink(a) or os.path.islink(p): return os.path.islink(a) and os.path.islink(p) and os.readlink(a)==os.readlink(p)\n  return (tree(a)==tree(p)) if os.path.isdir(a) else (sha(a)==sha(p))\n" +
        "def superset(p):\n  a=C+p\n  if not os.path.isdir(a) or not os.path.isdir(p): return False\n  for root,dirs,files in os.walk(p):\n    for f in files:\n      q=os.path.join(root,f); r=C+q\n      if os.path.islink(q):\n        if not os.path.islink(r) or os.readlink(r)!=os.readlink(q): return False\n      elif not os.path.isfile(r) or os.path.islink(r) or sha(r)!=sha(q): return False\n  return True\n" +
        "def resolve(p):\n  seen=0\n  while os.path.islink(C+p) and seen<8:\n    t=os.readlink(C+p); p=t if t.startswith(\"/\") else os.path.normpath(os.path.join(os.path.dirname(p),t)); seen+=1\n  return p\n" +
        "def account(name):\n  try:\n    for line in open(C+\"/etc/passwd\"):\n      f=line.rstrip(\"\\n\").split(\":\")\n      if f[0]==name: return {\"uid\":int(f[2]),\"gid\":int(f[3]),\"home\":f[5],\"shell\":f[6]}\n  except OSError: pass\n  return None\n" +
        "setuid=[];caps=[]\n" +
        "for root,dirs,files in os.walk(C):\n  for f in files:\n    p=os.path.join(root,f)\n    try: st=os.lstat(p)\n    except OSError: continue\n    if stat.S_ISREG(st.st_mode) and st.st_mode & 0o6000: setuid.append(p[len(C):])\n    try:\n      if \"security.capability\" in os.listxattr(p, follow_symlinks=False): caps.append(p[len(C):])\n    except OSError: pass\n" +
        "artifacts={p:same(p) for p in (\"/usr/local/bin/worker-supervisor\",\"/app\",\"/usr/share/dotnet\",\"/usr/local/lib/node_modules/opencode-ai\",\"/usr/local/bin/node\",\"/usr/bin/dotnet\",\"/usr/local/bin/opencode\",\"/usr/bin/python3\",\"/usr/bin/python3.12\",\"/usr/bin/env\",\"/bin/sh\",\"/usr/bin/dash\",\"/etc/ld.so.conf\",\"/etc/ld.so.conf.d\",\"/etc/nsswitch.conf\")}\n" +
        "artifacts[\"/usr/lib/python3.12\"]=superset(\"/usr/lib/python3.12\")\n" +
        "artifacts[\"/lib\"]=os.readlink(C+\"/lib\")==os.readlink(\"/lib\") if os.path.islink(\"/lib\") else same(\"/lib\")\n" +
        "artifacts[\"/lib64\"]=os.readlink(C+\"/lib64\")==os.readlink(\"/lib64\") if os.path.islink(\"/lib64\") else same(\"/lib64\")\n" +
        "artifacts[\"/usr/lib/x86_64-linux-gnu\"]=superset(\"/usr/lib/x86_64-linux-gnu\") and superset(\"/usr/lib64\") if os.path.isdir(\"/usr/lib64\") else superset(\"/usr/lib/x86_64-linux-gnu\")\n" +
        "artifacts[\"/usr/lib/aarch64-linux-gnu\"]=superset(\"/usr/lib/aarch64-linux-gnu\") if os.path.isdir(\"/usr/lib/aarch64-linux-gnu\") else True\n" +
        "artifacts[\"/etc/ld.so.preload\"]=not os.path.lexists(C+\"/etc/ld.so.preload\")\n" +
        "artifacts[\"/lib/python-shadow\"]=not any(os.path.lexists(C+d+\"/python3\") for d in (\"/usr/local/bin\",\"/usr/local/sbin\"))\n" +
        "print(json.dumps({\"bridge\":account(\"bridge\"),\"employee\":account(\"employee\"),\"dirs\":{d:mode(d) for d in (\"/control\",\"/home/worker\",\"/workspace\",\"/session\")},\"app\":mode(\"/app\"),\"supervisor\":mode(\"/usr/local/bin/worker-supervisor\"),\"artifacts\":artifacts,\"setuid\":setuid[:16],\"fileCaps\":caps[:16],\"dockerSock\":os.path.exists(C+\"/var/run/docker.sock\")}))\n";

    /// <summary>
    /// <c>docker build</c> reading the deterministic tar context from standard
    /// input. No build arguments, no cache-from, no secrets, no host context; the
    /// base is addressed only through the pin tag, so <c>--pull=false</c> cannot
    /// fetch anything, and <c>--network none</c> is used unless a fixed feature
    /// recipe needs apt.
    /// </summary>
    public static RemoteCommand BuildImageBuild(ApprovedExecutionHost host, WorkerControlOptions options, ImageBuildSpec spec, byte[] contextTar)
    {
        if (!Digest.IsMatch(spec.BaseImageDigest) || !Digest.IsMatch(spec.ContextHash)) throw new WorkerControlConfigurationException("Image build digests must be exact sha256 values.");
        if (!ProfileRevisionId.IsMatch(spec.ProfileRevisionId)) throw new WorkerControlConfigurationException("Image build profile revision id is invalid.");
        ValidateImageReference(spec.ResultTag);
        if (contextTar is not { Length: > 0 and <= MaximumContextBytes }) throw new WorkerControlConfigurationException("Image build context is empty or exceeds the fixed limit.");
        var parts = new List<string>
        {
            "docker build", "--quiet", "--pull=false", "--no-cache",
            "--network", QuoteShell(spec.NetworkRequired ? "default" : "none"),
            "--platform", QuotePlatform(spec.Platform),
            "--label", QuoteLabel(ProfileBuildContext.ProfileLabel, spec.ProfileRevisionId),
            "--label", QuoteLabel(ProfileBuildContext.ContextHashLabel, spec.ContextHash),
            "--label", QuoteLabel(ProfileBuildContext.BaseDigestLabel, spec.BaseImageDigest),
            "--tag", QuoteImageReference(spec.ResultTag),
            "-",
        };
        return BuildSsh(host, options, string.Join(' ', parts), contextTar);
    }

    public const int MaximumContextBytes = 256 * 1024;
    private static readonly System.Text.RegularExpressions.Regex Digest = new("^sha256:[0-9a-f]{64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex ProfileRevisionId = new("^prev-[a-f0-9]{16}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    // Local repository:tag only: no registry host or path component, so a build can
    // never be tagged into (or a FROM resolved from) anything but the host's own store.
    private static readonly System.Text.RegularExpressions.Regex ImageReference = new("^agentcontrol-[a-z0-9-]+:[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string QuoteDigest(string value) { if (!Digest.IsMatch(value)) throw new WorkerControlConfigurationException("Image digest is invalid."); return QuoteShell(value); }
    private static string ValidatedImageReference(string value) { ValidateImageReference(value); return value; }
    private static void ValidateImageReference(string value) { if (!ImageReference.IsMatch(value) || value.Length > 200) throw new WorkerControlConfigurationException("Image reference is invalid."); }
    /// <summary>An exact digest or a controller-shaped <c>repository:tag</c> reference; never a registry path.</summary>
    private static string QuoteImageReference(string value) { if (Digest.IsMatch(value)) return QuoteShell(value); ValidateImageReference(value); return QuoteShell(value); }

    public static RemoteCommand BuildVolumeCreate(ApprovedExecutionHost host, WorkerControlOptions options, VolumeCreateSpec spec)
    {
        var parts = new List<string> { "docker volume create" };
        foreach (var label in ExactLabels(spec.Identity)) parts.Add($"--label {QuoteLabel(label.Key, label.Value)}");
        parts.Add(QuoteResource(spec.Name));
        return BuildSsh(host, options, string.Join(' ', parts), null);
    }

    /// <summary>
    /// The fixed ephemeral bootstrap run. It exists so the enrolled key is written
    /// into the control volume before the long-lived container is created, and it
    /// mounts nothing but that control volume.
    /// </summary>
    public static RemoteCommand BuildBootstrap(ApprovedExecutionHost host, WorkerControlOptions options, BootstrapSpec spec, byte[]? stdin)
    {
        RequireApprovedImage(options, spec.ImageDigest, spec.Platform);
        ValidateResource(spec.ControlVolumeName);
        var parts = new List<string>
        {
            "docker run", "--rm", "-i",
            "--user", "'1101:1101'",
            "--network", "'none'",
            "--read-only",
            "--cap-drop", "'ALL'",
            "--security-opt", "'no-new-privileges'",
            "--pids-limit", QuoteNumber(64),
            "--env", QuoteShell("WORKER_CONTROL_DIRECTORY=/control"),
            "--mount", QuoteShell($"type=volume,src={spec.ControlVolumeName},dst=/control"),
            "--platform", QuotePlatform(spec.Platform),
            "--entrypoint", QuoteShell("/usr/bin/dotnet"),
        };
        foreach (var label in ExactLabels(spec.Identity)) { parts.Add("--label"); parts.Add(QuoteLabel(label.Key, label.Value)); }
        parts.Add(QuoteShell(spec.ImageDigest));
        parts.Add(QuoteShell(options.WorkerTarget));
        parts.Add("'--worker-bootstrap-key'");
        return BuildSsh(host, options, string.Join(' ', parts), stdin);
    }

    public static RemoteCommand BuildContainerCreate(ApprovedExecutionHost host, WorkerControlOptions options, ContainerCreateSpec spec)
    {
        RequireApprovedImage(options, spec.ImageDigest, spec.Platform, spec.ApprovedDigests);
        if (spec.MemoryBytes != options.MemoryBytes || spec.CpuLimit != options.CpuLimit || spec.PidsLimit != options.PidsLimit) throw new WorkerControlConfigurationException("Container resource limits differ from controller policy.");
        if (spec.Volumes.Count != 4 || spec.Volumes.Select(x => x.ContainerPath).ToHashSet(StringComparer.Ordinal).SetEquals(ContainerPaths) is false) throw new WorkerControlConfigurationException("Container must use the four fixed named-volume mount points.");
        var parts = new List<string> { "docker container create", "--name", QuoteResource(spec.Name), "--network", QuoteShell(WorkerControlOptions.ContainerNetworkMode), "--read-only", "--cap-drop", "'ALL'", "--cap-add", "'CHOWN'", "--cap-add", "'SETUID'", "--cap-add", "'SETGID'", "--cap-add", "'KILL'", "--security-opt", "'no-new-privileges'", "--env", QuoteShell("WORKER_CONTROL_DIRECTORY=/control"), "--env", QuoteShell("WORKER_ID=" + spec.Identity.WorkerId), "--env", QuoteShell("WORKER_CONTROLLER_ID=" + spec.Identity.ControllerId), "--pids-limit", QuoteNumber(spec.PidsLimit), "--memory", QuoteNumber(spec.MemoryBytes), "--cpus", QuoteNumber(spec.CpuLimit), "--tmpfs", "'/tmp:rw,noexec,nosuid,nodev,size=64m'", "--tmpfs", "'/run:rw,noexec,nosuid,nodev,size=16m'", "--platform", QuotePlatform(spec.Platform) };
        foreach (var label in ExactLabels(spec.Identity)) { parts.Add("--label"); parts.Add(QuoteLabel(label.Key, label.Value)); }
        foreach (var mount in spec.Volumes.OrderBy(x => x.ContainerPath, StringComparer.Ordinal))
        {
            ValidateResource(mount.Name); if (!ContainerPaths.Contains(mount.ContainerPath)) throw new WorkerControlConfigurationException("Container mount path is not fixed.");
            parts.Add("--mount"); parts.Add(QuoteShell($"type=volume,src={mount.Name},dst={mount.ContainerPath}{(mount.ReadOnly ? ",readonly" : string.Empty)}"));
        }
        parts.Add(QuoteShell(spec.ImageDigest));
        return BuildSsh(host, options, string.Join(' ', parts), null);
    }

    public static string QuoteShell(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Any(c => c == '\'' || char.IsControl(c))) throw new WorkerControlConfigurationException("Remote shell token contains a quote or control character.");
        return "'" + token + "'";
    }

    /// <summary>Validates one absolute, metacharacter-free remote path reported by the fixed probe.</summary>
    public static string QuoteAbsolutePath(string value)
    {
        if (value is not { Length: >= 1 and <= 512 } || !AbsolutePath.IsMatch(value) || value.Split('/').Any(segment => segment is "." or "..")) throw new WorkerControlConfigurationException("The remote storage path is not a fixed absolute path.");
        return QuoteShell(value);
    }

    /// <summary>
    /// The bootstrap always runs the configured base (it only writes the key). A
    /// long-lived container may run the base or any digest in the host's approved
    /// set, which is the base plus verified profile builds on that host; the set
    /// is supplied by the caller from the store and every entry must itself be an
    /// exact digest.
    /// </summary>
    private static void RequireApprovedImage(WorkerControlOptions options, string digest, string platform, IReadOnlyList<string>? approvedDigests = null)
    {
        if (!Digest.IsMatch(digest)) throw new WorkerControlConfigurationException("Container image must be an exact sha256 digest.");
        var approved = digest == options.ApprovedImageDigest || (approvedDigests is not null && approvedDigests.All(Digest.IsMatch) && approvedDigests.Contains(digest, StringComparer.Ordinal));
        if (!approved) throw new WorkerControlConfigurationException("Container image must be the exact approved base digest or a verified profile build digest for this host.");
        if (platform != options.ApprovedImagePlatform) throw new WorkerControlConfigurationException("Container platform differs from controller policy.");
    }

    private static RemoteCommand BuildSsh(ApprovedExecutionHost host, WorkerControlOptions options, string remote, byte[]? stdin)
    {
        var args = new List<string> { "-T", "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes", "-o", $"UserKnownHostsFile={host.KnownHostsPath}", "-o", "StrictHostKeyChecking=yes", "-o", $"ConnectTimeout={options.ConnectTimeoutSeconds}", "-i", host.IdentityFilePath, "-p", host.Port.ToString(CultureInfo.InvariantCulture), "--", $"{host.Username}@{host.Hostname}", remote };
        return new RemoteCommand(options.ConnectorExecutable, args, stdin);
    }

    private static IEnumerable<KeyValuePair<string, string>> ExactLabels(WorkerResourceIdentity identity)
    {
        if (identity.Labels.Count != (identity.ProfileRevisionId is null ? 6 : 7)) throw new WorkerControlConfigurationException("Resource label set is invalid.");
        if (identity.ProfileRevisionId is not null && !ProfileRevisionId.IsMatch(identity.ProfileRevisionId)) throw new WorkerControlConfigurationException("Resource profile label is invalid.");
        foreach (var label in identity.Labels.OrderBy(x => x.Key, StringComparer.Ordinal)) { if (!Identifier.IsMatch(label.Value.Replace("/", ":", StringComparison.Ordinal))) throw new WorkerControlConfigurationException("Resource label value is invalid."); yield return label; }
    }
    private static string QuoteResource(string value) { ValidateResource(value); return QuoteShell(value); }
    private static void ValidateResource(string value) { if (!ResourceName.IsMatch(value)) throw new WorkerControlConfigurationException("Resource name is invalid."); }
    private static string QuoteLabel(string key, string value) { if (!key.StartsWith("agentcontrol.", StringComparison.Ordinal)) throw new WorkerControlConfigurationException("Resource label key is invalid."); return QuoteShell(key + "=" + value); }
    private static string QuotePlatform(string value) => value is "linux/amd64" or "linux/arm64" ? QuoteShell(value) : throw new WorkerControlConfigurationException("Platform is invalid.");
    private static string QuoteNumber<T>(T value) where T : IFormattable => QuoteShell(value.ToString(null, CultureInfo.InvariantCulture));

    public static void RequireOwnedLabels(IReadOnlyDictionary<string, string> actual, WorkerResourceIdentity identity)
    {
        var owned = actual.Where(label => label.Key.StartsWith("agentcontrol.", StringComparison.Ordinal)).ToArray();
        if (owned.Length != identity.Labels.Count) throw new ForeignResourceException("The resource carries unexpected AgentControl labels.");
        foreach (var expected in identity.Labels) if (!actual.TryGetValue(expected.Key, out var value) || !string.Equals(value, expected.Value, StringComparison.Ordinal)) throw new ForeignResourceException("The resource is not exactly owned by this operation.");
    }
}

public sealed class ProcessRemoteWorkerOperations(IOptions<WorkerControlOptions> configured, ILogger<ProcessRemoteWorkerOperations> logger) : IRemoteWorkerOperations
{
    private const int MaxOutputBytes = 1024 * 1024;
    private readonly WorkerControlOptions _options = configured.Value;
    public Task<RemoteOperationResult> ExecuteAsync(ApprovedExecutionHost host, RemoteDockerOperation operation, IReadOnlyList<string> tokens, byte[]? standardInput, CancellationToken cancellationToken) => RunAsync(host, RemoteWorkerCommandBuilder.Build(host, _options, operation, tokens, standardInput), operation, cancellationToken);
    public Task<RemoteOperationResult> CreateVolumeAsync(ApprovedExecutionHost host, VolumeCreateSpec specification, CancellationToken cancellationToken) => RunAsync(host, RemoteWorkerCommandBuilder.BuildVolumeCreate(host, _options, specification), RemoteDockerOperation.VolumeCreate, cancellationToken);
    public Task<RemoteOperationResult> CreateContainerAsync(ApprovedExecutionHost host, ContainerCreateSpec specification, CancellationToken cancellationToken) => RunAsync(host, RemoteWorkerCommandBuilder.BuildContainerCreate(host, _options, specification), RemoteDockerOperation.ContainerCreate, cancellationToken);
    public Task<RemoteOperationResult> BootstrapAsync(ApprovedExecutionHost host, BootstrapSpec specification, byte[] standardInput, CancellationToken cancellationToken) => RunAsync(host, RemoteWorkerCommandBuilder.BuildBootstrap(host, _options, specification, standardInput), RemoteDockerOperation.Bootstrap, cancellationToken);
    public Task<RemoteOperationResult> BuildImageAsync(ApprovedExecutionHost host, ImageBuildSpec specification, byte[] contextTar, CancellationToken cancellationToken) => RunAsync(host, RemoteWorkerCommandBuilder.BuildImageBuild(host, _options, specification, contextTar), RemoteDockerOperation.ImageBuild, cancellationToken);

    /// <summary>
    /// Runs the fixed probe sequence and returns only what the host actually
    /// printed. The storage path comes from the daemon's own DockerRootDir and is
    /// re-validated as a fixed absolute path before it is ever used in a command.
    /// </summary>
    public async Task<HostProbePayload> ProbeHostAsync(ApprovedExecutionHost host, CancellationToken cancellationToken)
    {
        var info = Require(await ExecuteAsync(host, RemoteDockerOperation.Probe, [], null, cancellationToken).ConfigureAwait(false), "host information");
        var version = Require(await ExecuteAsync(host, RemoteDockerOperation.VersionProbe, [], null, cancellationToken).ConfigureAwait(false), "daemon version");
        var root = HostProbeParser.ReadDockerRootDirectory(info);
        var free = Require(await ExecuteAsync(host, RemoteDockerOperation.StorageFree, [root], null, cancellationToken).ConfigureAwait(false), "storage capacity");
        return new HostProbePayload(info, version, root, HostProbeParser.ParseAvailableBytes(free));
    }

    private static string Require(RemoteOperationResult result, string what)
    {
        if (result.ExitCode == 0) return result.StandardOutput;
        throw new RemoteWorkerUnavailableException($"The fixed host probe could not read {what}.", result.ErrorCategory == "transport");
    }

    private async Task<RemoteOperationResult> RunAsync(ApprovedExecutionHost host, RemoteCommand command, RemoteDockerOperation operation, CancellationToken cancellationToken)
    {
        EnsureApproved(host); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(operation is RemoteDockerOperation.ImageBuild or RemoteDockerOperation.ImageVerify ? _options.ImageBuildTimeoutSeconds : _options.OperationTimeoutSeconds));
        using var process = Start(command);
        try
        {
            if (command.StandardInput is { } bytes)
            {
                try { await process.StandardInput.BaseStream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false); await process.StandardInput.BaseStream.FlushAsync(timeout.Token).ConfigureAwait(false); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            process.StandardInput.Close();
            var outputTask = ReadBoundedAsync(process.StandardOutput.BaseStream, MaxOutputBytes, timeout.Token);
            var errorTask = ReadBoundedAsync(process.StandardError.BaseStream, MaxOutputBytes, timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = System.Text.Encoding.UTF8.GetString(await outputTask.ConfigureAwait(false));
            var error = System.Text.Encoding.UTF8.GetString(await errorTask.ConfigureAwait(false));
            logger.LogInformation("Remote worker operation {Operation} for approved host {HostId} exited {ExitCode}.", operation, host.Id, process.ExitCode);
            return new RemoteOperationResult(process.ExitCode, output, ClassifyError(operation, process.ExitCode, error));
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Classifies a failed fixed operation. "Absent" is only ever concluded from
    /// the exact Docker CLI exit code and message for the inspected resource kind,
    /// matched case-insensitively; everything else stays a failure.
    /// </summary>
    internal static string ClassifyError(RemoteDockerOperation operation, int exitCode, string stderr)
    {
        if (exitCode == 0) return "none";
        if (exitCode == 255) return "transport";
        if (exitCode != 1) return "remote-command-failed";
        var expected = operation switch
        {
            RemoteDockerOperation.ContainerInspect => "no such container",
            RemoteDockerOperation.VolumeInspect => "no such volume",
            RemoteDockerOperation.ImageInspect or RemoteDockerOperation.ImageRemove or RemoteDockerOperation.ImageTag => "no such image",
            _ => null,
        };
        if (expected is null) return "remote-command-failed";
        var text = stderr.Trim();
        return text.Length <= 512 && text.Contains(expected, StringComparison.OrdinalIgnoreCase) ? "not-found" : "remote-command-failed";
    }

    private void EnsureApproved(ApprovedExecutionHost host)
    {
        if (!_options.Enabled) throw new WorkerControlDisabledException();
        if (_options.Validate().Count != 0 || !_options.ApprovedHosts.Any(item => item.Id == host.Id && item.Hostname == host.Hostname && item.Port == host.Port && item.Username == host.Username)) throw new WorkerControlConfigurationException("Remote worker configuration is not approved.");
    }
    private static Process Start(RemoteCommand command) { var start = new ProcessStartInfo(command.Executable) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }; foreach (var argument in command.Arguments) start.ArgumentList.Add(argument); return Process.Start(start) ?? throw new RemoteWorkerUnavailableException("The fixed connector could not start.", transport: true); }
    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken token) { using var memory = new MemoryStream(); var buffer = new byte[8192]; while (true) { var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false); if (read == 0) return memory.ToArray(); if (memory.Length + read > maximum) throw new RemoteWorkerUnavailableException("Remote operation output exceeded the fixed limit."); memory.Write(buffer, 0, read); } }
}

public sealed class ProcessWorkerConnector(HVO.AgentControl.Runtime.AcpControlHost control, IOptions<WorkerControlOptions> configured) : IWorkerConnector, IRemoteTerminalConnector
{
    private readonly WorkerControlOptions _options = configured.Value;
    public Task<Stream> ConnectViewerAsync(string workerId, CancellationToken cancellationToken) => ConnectAsync(workerId, cancellationToken);
    public async Task<Stream> ConnectAsync(string workerId, CancellationToken cancellationToken)
    {
        if (!_options.Enabled) throw new WorkerControlDisabledException();
        var enrollment = control.Organization?.GetWorkerEnrollment(workerId) ?? throw new KeyNotFoundException("Worker enrollment not found.");
        var host = _options.ApprovedHosts.SingleOrDefault(x => x.Id == enrollment.HostId) ?? throw new WorkerControlConfigurationException("The enrollment host is not approved.");
        var command = RemoteWorkerCommandBuilder.Build(host, _options, RemoteDockerOperation.Connector, [enrollment.ContainerName]);
        var start = new ProcessStartInfo(command.Executable) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new RemoteWorkerUnavailableException("The fixed connector could not start.", transport: true);
        _ = DrainStderrAsync(process.StandardError.BaseStream, process, cancellationToken);
        await Task.Yield();
        if (cancellationToken.IsCancellationRequested) { try { process.Kill(true); } catch { } process.Dispose(); cancellationToken.ThrowIfCancellationRequested(); }
        return new ProcessDuplexStream(process);
    }
    private static async Task DrainStderrAsync(Stream stream, Process process, CancellationToken token)
    {
        var buffer = new byte[4096]; var total = 0;
        try { while (total < 64 * 1024) { var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, 64 * 1024 - total)), token).ConfigureAwait(false); if (read == 0) return; total += read; } if (!process.HasExited) process.Kill(true); }
        catch { try { if (!process.HasExited) process.Kill(true); } catch { } }
    }
}

internal sealed class ProcessDuplexStream(Process process) : Stream
{
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => true; public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => process.StandardInput.BaseStream.Flush(); public override Task FlushAsync(CancellationToken cancellationToken) => process.StandardInput.BaseStream.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => process.StandardOutput.BaseStream.Read(buffer, offset, count); public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => process.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken);
    public override void Write(byte[] buffer, int offset, int count) => process.StandardInput.BaseStream.Write(buffer, offset, count); public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => process.StandardInput.BaseStream.WriteAsync(buffer, cancellationToken);
    protected override void Dispose(bool disposing) { if (disposing) { try { process.StandardInput.Close(); if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } process.Dispose(); } base.Dispose(disposing); }
    public override async ValueTask DisposeAsync() { try { process.StandardInput.Close(); if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { } process.Dispose(); GC.SuppressFinalize(this); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
}
