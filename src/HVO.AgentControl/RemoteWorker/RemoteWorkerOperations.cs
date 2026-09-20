using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using HVO.AgentControl.Organization;
using Microsoft.Extensions.Options;

namespace HVO.AgentControl.RemoteWorker;

public enum RemoteDockerOperation { Probe, VersionProbe, StorageFree, ImageInspect, ImageTag, ImageBuild, ImageVerify, ImageRemove, VolumeCreate, VolumeInspect, VolumeRemove, ContainerCreate, ContainerInspect, ContainerStart, ContainerStop, ContainerRemove, Bootstrap, Connector, Viewer }
public sealed record RemoteCommand(string Executable, IReadOnlyList<string> Arguments, byte[]? StandardInput = null);
public sealed record RemoteOperationResult(int ExitCode, string StandardOutput, string ErrorCategory);
/// <summary>
/// The raw, locally assembled result of the fixed multi-command host probe. Each
/// field is exactly what one fixed remote command printed; no field is synthesized
/// by the controller and none is interpreted here.
/// </summary>
public sealed record HostProbePayload(string InfoJson, string VersionJson, string DockerRootDir, long FreeBytes);

public interface IRemoteWorkerOperations
{
    Task<RemoteOperationResult> ExecuteAsync(ExecutionTarget target, RemoteDockerOperation operation, IReadOnlyList<string> tokens, byte[]? standardInput, CancellationToken cancellationToken);
    Task<RemoteOperationResult> CreateVolumeAsync(ExecutionTarget target, VolumeCreateSpec specification, CancellationToken cancellationToken);
    Task<RemoteOperationResult> CreateContainerAsync(ExecutionTarget target, ContainerCreateSpec specification, CancellationToken cancellationToken);
    Task<RemoteOperationResult> BootstrapAsync(ExecutionTarget target, BootstrapSpec specification, byte[] standardInput, CancellationToken cancellationToken);
    Task<RemoteOperationResult> BuildImageAsync(ExecutionTarget target, ImageBuildSpec specification, byte[] contextTar, CancellationToken cancellationToken);

    /// <summary>
    /// Removes one image by exact digest or controller-shaped local tag. The fixed
    /// grammar validates the reference; absence is reported as <c>not-found</c> and
    /// a transport failure remains an uncertain effect. The caller is responsible
    /// for proving the image is not in use before asking the transport to remove it.
    /// </summary>
    Task<RemoteOperationResult> RemoveImageAsync(ExecutionTarget target, string imageReference, CancellationToken cancellationToken);
    Task<HostProbePayload> ProbeHostAsync(ExecutionTarget target, CancellationToken cancellationToken);
}

public interface ISshExecutionOperations : IRemoteWorkerOperations;
public interface ILocalDockerExecutionOperations : IRemoteWorkerOperations;

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
    private static readonly HashSet<string> ShellSyntax = ["docker", "df", "system", "info", "version", "image", "inspect", "tag", "rm", "run", "volume", "create", "container", "start", "stop", "exec", "build", "-", "-i", "-B1", "-T", "-I", "-S"];

    private static readonly System.Text.RegularExpressions.Regex ResourceName = new("^[a-z0-9](?:[a-z0-9_.-]{0,126}[a-z0-9])?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex Identifier = new("^[A-Za-z0-9](?:[A-Za-z0-9._:-]{0,127})$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex AbsolutePath = new("^/(?:[A-Za-z0-9._-]+/?)*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ContainerPaths = ["/control", "/home/worker", "/workspace", "/session"];

    public static RemoteCommand Build(ApprovedExecutionHost host, WorkerControlOptions options, RemoteDockerOperation operation, IReadOnlyList<string> tokens, byte[]? stdin = null)
    {
        if (host.Validate(false).Count != 0) throw new WorkerControlConfigurationException("Approved host configuration is invalid.");
        try
        {
            var argv = DockerArgv.Build((DockerOperation)operation, tokens, Policy(options));
            return BuildSsh(host, options, RenderRemote(argv), stdin);
        }
        catch (DockerGrammarException exception) { throw new WorkerControlConfigurationException(exception.Message, exception); }
    }

    /// <summary>
    /// <c>docker build</c> reading the deterministic tar context from standard
    /// input. No build arguments, no cache-from, no secrets, no host context; the
    /// base is addressed only through the pin tag, so <c>--pull=false</c> cannot
    /// fetch anything, and <c>--network none</c> is used unless a fixed feature
    /// recipe needs apt.
    /// </summary>
    public static RemoteCommand BuildImageBuild(ApprovedExecutionHost host, WorkerControlOptions options, ImageBuildSpec spec, byte[] contextTar)
    {
        if (contextTar is not { Length: > 0 and <= MaximumContextBytes }) throw new WorkerControlConfigurationException("Image build context is empty or exceeds the fixed limit.");
        try { return BuildSsh(host, options, RenderRemote(DockerArgv.BuildImageBuild(spec)), contextTar); }
        catch (DockerGrammarException exception) { throw new WorkerControlConfigurationException(exception.Message, exception); }
    }

    public const int MaximumContextBytes = 256 * 1024;
    private static readonly System.Text.RegularExpressions.Regex Digest = new("^sha256:[0-9a-f]{64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex ProfileRevisionId = new("^prev-[a-f0-9]{16}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    // Local repository:tag only: no registry host or path component, so a build can
    // never be tagged into (or a FROM resolved from) anything but the host's own store.
    private static readonly System.Text.RegularExpressions.Regex ImageReference = new("^agentcontrol-[a-z0-9-]+:[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string QuoteDigest(string value) { if (!Digest.IsMatch(value)) throw new WorkerControlConfigurationException("Image digest is invalid."); return QuoteShell(value); }
    private static string FixedDotnetEnvironment() => string.Join(' ', new[]
    {
        "HOME=/control", "PATH=/usr/bin:/bin", "DOTNET_ROOT=/usr/share/dotnet",
        "DOTNET_STARTUP_HOOKS=", "DOTNET_ADDITIONAL_DEPS=", "DOTNET_SHARED_STORE=",
        "LD_PRELOAD=", "LD_AUDIT=", "LD_LIBRARY_PATH=",
    }.Select(value => "--env " + QuoteShell(value)));
    private static string ValidatedImageReference(string value) { ValidateImageReference(value); return value; }
    private static void ValidateImageReference(string value) { if (!ImageReference.IsMatch(value) || value.Length > 200) throw new WorkerControlConfigurationException("Image reference is invalid."); }
    /// <summary>An exact digest or a controller-shaped <c>repository:tag</c> reference; never a registry path.</summary>
    private static string QuoteImageReference(string value) { if (Digest.IsMatch(value)) return QuoteShell(value); ValidateImageReference(value); return QuoteShell(value); }

    public static RemoteCommand BuildVolumeCreate(ApprovedExecutionHost host, WorkerControlOptions options, VolumeCreateSpec spec)
    {
        try { return BuildSsh(host, options, RenderRemote(DockerArgv.BuildVolumeCreate(spec)), null); }
        catch (DockerGrammarException exception) { throw new WorkerControlConfigurationException(exception.Message, exception); }
    }

    /// <summary>
    /// The fixed ephemeral bootstrap run. It exists so the enrolled key is written
    /// into the control volume before the long-lived container is created, and it
    /// mounts nothing but that control volume.
    /// </summary>
    public static RemoteCommand BuildBootstrap(ApprovedExecutionHost host, WorkerControlOptions options, BootstrapSpec spec, byte[]? stdin)
    {
        try { return BuildSsh(host, options, RenderRemote(DockerArgv.BuildBootstrap(spec, Policy(options))), stdin); }
        catch (DockerGrammarException exception) { throw new WorkerControlConfigurationException(exception.Message, exception); }
    }

    public static RemoteCommand BuildContainerCreate(ApprovedExecutionHost host, WorkerControlOptions options, ContainerCreateSpec spec)
    {
        try { return BuildSsh(host, options, RenderRemote(DockerArgv.BuildContainerCreate(spec, Policy(options))), null); }
        catch (DockerGrammarException exception) { throw new WorkerControlConfigurationException(exception.Message, exception); }
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

    private static DockerPolicy Policy(WorkerControlOptions options) => new(options.ApprovedImageDigest, options.ApprovedImagePlatform, options.MemoryBytes, options.CpuLimit, options.PidsLimit, options.WorkerTarget);

    private static string RenderRemote(IReadOnlyList<string> argv)
    {
        var valueOptions = new HashSet<string>(StringComparer.Ordinal) { "--format", "--network", "--platform", "--mount", "--entrypoint", "--user", "--cap-drop", "--cap-add", "--security-opt", "--pids-limit", "--env", "--label", "--name", "--memory", "--cpus", "--tmpfs", "--tag" };
        var parts = new List<string>(argv.Count);
        var quoteNext = false;
        for (var index = 0; index < argv.Count; index++)
        {
            var token = argv[index];
            var commandWord = index == 0 || (!quoteNext && token is "system" or "info" or "version" or "image" or "inspect" or "tag" or "rm" or "run" or "volume" or "create" or "container" or "start" or "stop" or "exec" or "build");
            var positionalSyntax = token is "-B1" or "-i" or "-";
            var trailingProgramArgument = token is "--worker-bootstrap-key" or "--worker-pipe" or "--worker-viewer-pipe";
            var unquoted = commandWord || token.StartsWith("--", StringComparison.Ordinal) && !trailingProgramArgument || positionalSyntax || (token == "10" && index > 0 && argv[index - 1] == "--time");
            if (positionalSyntax) quoteNext = false;
            parts.Add(unquoted && !quoteNext ? token : QuoteShell(token));
            quoteNext = !quoteNext && valueOptions.Contains(token);
        }
        return string.Join(' ', parts);
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
        // Docker copies image labels into container Config.Labels. Verified profile
        // images therefore contribute the immutable build-provenance labels below;
        // they are not resource ownership claims and must not make the container
        // foreign. Every ownership label remains exact, and any other AgentControl
        // label still fails closed.
        var allowed = identity.Labels.Keys
            .Append(ProfileBuildContext.ContextHashLabel)
            .Append(ProfileBuildContext.BaseDigestLabel)
            .ToHashSet(StringComparer.Ordinal);
        if (actual.Keys.Any(label => label.StartsWith("agentcontrol.", StringComparison.Ordinal) && !allowed.Contains(label)))
            throw new ForeignResourceException("The resource carries unexpected AgentControl labels.");
        foreach (var expected in identity.Labels) if (!actual.TryGetValue(expected.Key, out var value) || !string.Equals(value, expected.Value, StringComparison.Ordinal)) throw new ForeignResourceException("The resource is not exactly owned by this operation.");
    }
}

public sealed class LocalDockerExecutionOperations(LocalDockerHelperClient client) : ILocalDockerExecutionOperations
{
    public Task<RemoteOperationResult> ExecuteAsync(ExecutionTarget target, RemoteDockerOperation operation, IReadOnlyList<string> tokens, byte[]? standardInput, CancellationToken cancellationToken)
    {
        if (standardInput is not null) throw new WorkerControlConfigurationException("The local Docker helper accepts binary input only for typed operations.");
        return client.ExecuteAsync(target, (DockerOperation)operation, tokens, cancellationToken);
    }

    public Task<RemoteOperationResult> CreateVolumeAsync(ExecutionTarget target, VolumeCreateSpec specification, CancellationToken cancellationToken) => client.CreateVolumeAsync(target, specification, cancellationToken);
    public Task<RemoteOperationResult> CreateContainerAsync(ExecutionTarget target, ContainerCreateSpec specification, CancellationToken cancellationToken) => client.CreateContainerAsync(target, specification, cancellationToken);
    public Task<RemoteOperationResult> BootstrapAsync(ExecutionTarget target, BootstrapSpec specification, byte[] standardInput, CancellationToken cancellationToken) => client.BootstrapAsync(target, specification, standardInput, cancellationToken);
    public Task<RemoteOperationResult> BuildImageAsync(ExecutionTarget target, ImageBuildSpec specification, byte[] contextTar, CancellationToken cancellationToken) => client.BuildImageAsync(target, specification, contextTar, cancellationToken);
    public Task<RemoteOperationResult> RemoveImageAsync(ExecutionTarget target, string imageReference, CancellationToken cancellationToken) => ExecuteAsync(target, RemoteDockerOperation.ImageRemove, [imageReference], null, cancellationToken);
    public Task<HostProbePayload> ProbeHostAsync(ExecutionTarget target, CancellationToken cancellationToken) => client.ProbeAsync(target, cancellationToken);
}

public sealed class RoutingExecutionOperations(ISshExecutionOperations ssh, ILocalDockerExecutionOperations local) : IRemoteWorkerOperations
{
    private IRemoteWorkerOperations Select(ExecutionTarget target) => target.IsLocalDocker ? local : ssh;
    public Task<RemoteOperationResult> ExecuteAsync(ExecutionTarget target, RemoteDockerOperation operation, IReadOnlyList<string> tokens, byte[]? standardInput, CancellationToken cancellationToken) => Select(target).ExecuteAsync(target, operation, tokens, standardInput, cancellationToken);
    public Task<RemoteOperationResult> CreateVolumeAsync(ExecutionTarget target, VolumeCreateSpec specification, CancellationToken cancellationToken) => Select(target).CreateVolumeAsync(target, specification, cancellationToken);
    public Task<RemoteOperationResult> CreateContainerAsync(ExecutionTarget target, ContainerCreateSpec specification, CancellationToken cancellationToken) => Select(target).CreateContainerAsync(target, specification, cancellationToken);
    public Task<RemoteOperationResult> BootstrapAsync(ExecutionTarget target, BootstrapSpec specification, byte[] standardInput, CancellationToken cancellationToken) => Select(target).BootstrapAsync(target, specification, standardInput, cancellationToken);
    public Task<RemoteOperationResult> BuildImageAsync(ExecutionTarget target, ImageBuildSpec specification, byte[] contextTar, CancellationToken cancellationToken) => Select(target).BuildImageAsync(target, specification, contextTar, cancellationToken);
    public Task<RemoteOperationResult> RemoveImageAsync(ExecutionTarget target, string imageReference, CancellationToken cancellationToken) => Select(target).RemoveImageAsync(target, imageReference, cancellationToken);
    public Task<HostProbePayload> ProbeHostAsync(ExecutionTarget target, CancellationToken cancellationToken) => Select(target).ProbeHostAsync(target, cancellationToken);
}

public sealed class ProcessRemoteWorkerOperations(IOptions<WorkerControlOptions> configured, ILogger<ProcessRemoteWorkerOperations> logger) : ISshExecutionOperations
{
    private const int MaxOutputBytes = 1024 * 1024;
    private readonly WorkerControlOptions _options = configured.Value;
    public Task<RemoteOperationResult> ExecuteAsync(ExecutionTarget target, RemoteDockerOperation operation, IReadOnlyList<string> tokens, byte[]? standardInput, CancellationToken cancellationToken) { var host = RequireSsh(target); return RunAsync(host, RemoteWorkerCommandBuilder.Build(host, _options, operation, tokens, standardInput), operation, cancellationToken); }
    public Task<RemoteOperationResult> CreateVolumeAsync(ExecutionTarget target, VolumeCreateSpec specification, CancellationToken cancellationToken) { var host = RequireSsh(target); return RunAsync(host, RemoteWorkerCommandBuilder.BuildVolumeCreate(host, _options, specification), RemoteDockerOperation.VolumeCreate, cancellationToken); }
    public Task<RemoteOperationResult> CreateContainerAsync(ExecutionTarget target, ContainerCreateSpec specification, CancellationToken cancellationToken) { var host = RequireSsh(target); return RunAsync(host, RemoteWorkerCommandBuilder.BuildContainerCreate(host, _options, specification), RemoteDockerOperation.ContainerCreate, cancellationToken); }
    public Task<RemoteOperationResult> BootstrapAsync(ExecutionTarget target, BootstrapSpec specification, byte[] standardInput, CancellationToken cancellationToken) { var host = RequireSsh(target); return RunAsync(host, RemoteWorkerCommandBuilder.BuildBootstrap(host, _options, specification, standardInput), RemoteDockerOperation.Bootstrap, cancellationToken); }
    public Task<RemoteOperationResult> BuildImageAsync(ExecutionTarget target, ImageBuildSpec specification, byte[] contextTar, CancellationToken cancellationToken) { var host = RequireSsh(target); return RunAsync(host, RemoteWorkerCommandBuilder.BuildImageBuild(host, _options, specification, contextTar), RemoteDockerOperation.ImageBuild, cancellationToken); }
    public Task<RemoteOperationResult> RemoveImageAsync(ExecutionTarget target, string imageReference, CancellationToken cancellationToken) { var host = RequireSsh(target); return RunAsync(host, RemoteWorkerCommandBuilder.Build(host, _options, RemoteDockerOperation.ImageRemove, [imageReference], null), RemoteDockerOperation.ImageRemove, cancellationToken); }

    /// <summary>
    /// Runs the fixed probe sequence and returns only what the host actually
    /// printed. The storage path comes from the daemon's own DockerRootDir and is
    /// re-validated as a fixed absolute path before it is ever used in a command.
    /// </summary>
    public async Task<HostProbePayload> ProbeHostAsync(ExecutionTarget target, CancellationToken cancellationToken)
    {
        var info = Require(await ExecuteAsync(target, RemoteDockerOperation.Probe, [], null, cancellationToken).ConfigureAwait(false), "host information");
        var version = Require(await ExecuteAsync(target, RemoteDockerOperation.VersionProbe, [], null, cancellationToken).ConfigureAwait(false), "daemon version");
        var root = HostProbeParser.ReadDockerRootDirectory(info);
        var free = Require(await ExecuteAsync(target, RemoteDockerOperation.StorageFree, [root], null, cancellationToken).ConfigureAwait(false), "storage capacity");
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

    private static ApprovedExecutionHost RequireSsh(ExecutionTarget target) =>
        target is { IsLocalDocker: false, Ssh: not null } ? target.Ssh : throw new WorkerControlConfigurationException("SSH execution operations require an ssh-docker target.");

    private void EnsureApproved(ApprovedExecutionHost host)
    {
        if (!_options.Enabled) throw new WorkerControlDisabledException();
        if (_options.Validate().Count != 0 || !_options.ApprovedHosts.Any(item => item.Id == host.Id && item.Hostname == host.Hostname && item.Port == host.Port && item.Username == host.Username)) throw new WorkerControlConfigurationException("Remote worker configuration is not approved.");
    }
    private static Process Start(RemoteCommand command) { var start = new ProcessStartInfo(command.Executable) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }; foreach (var argument in command.Arguments) start.ArgumentList.Add(argument); return Process.Start(start) ?? throw new RemoteWorkerUnavailableException("The fixed connector could not start.", transport: true); }
    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken token) { using var memory = new MemoryStream(); var buffer = new byte[8192]; while (true) { var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false); if (read == 0) return memory.ToArray(); if (memory.Length + read > maximum) throw new RemoteWorkerUnavailableException("Remote operation output exceeded the fixed limit."); memory.Write(buffer, 0, read); } }
}

public sealed class ProcessWorkerConnector(IOptions<WorkerControlOptions> configured)
{
    private readonly WorkerControlOptions _options = configured.Value;
    public async Task<Stream> ConnectAsync(ExecutionTarget target, string containerName, bool viewer, CancellationToken cancellationToken)
    {
        if (!_options.Enabled) throw new WorkerControlDisabledException();
        var host = target is { IsLocalDocker: false, Ssh: not null } ? target.Ssh : throw new WorkerControlConfigurationException("The SSH connector requires an ssh-docker target.");
        var operation = viewer ? RemoteDockerOperation.Viewer : RemoteDockerOperation.Connector;
        var command = RemoteWorkerCommandBuilder.Build(host, _options, operation, [containerName]);
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

public sealed class RoutingWorkerConnector(
    HVO.AgentControl.Runtime.AcpControlHost control,
    IOptions<WorkerControlOptions> configured,
    ProcessWorkerConnector ssh,
    LocalDockerHelperClient local) : IWorkerConnector, IRemoteTerminalConnector
{
    private readonly WorkerControlOptions _options = configured.Value;
    private readonly ExecutionTargetResolver _targets = new(control, configured);

    public Task<Stream> ConnectAsync(string workerId, CancellationToken cancellationToken) => ConnectAsync(workerId, viewer: false, cancellationToken);
    public Task<Stream> ConnectViewerAsync(string workerId, CancellationToken cancellationToken) => ConnectAsync(workerId, viewer: true, cancellationToken);

    private Task<Stream> ConnectAsync(string workerId, bool viewer, CancellationToken cancellationToken)
    {
        if (!_options.Enabled) throw new WorkerControlDisabledException();
        var enrollment = control.Organization?.GetWorkerEnrollment(workerId) ?? throw new KeyNotFoundException("Worker enrollment not found.");
        var target = _targets.Resolve(enrollment.HostId);
        return target.IsLocalDocker
            ? viewer ? local.ConnectViewerAsync(target, enrollment.ContainerName, cancellationToken) : local.ConnectAsync(target, enrollment.ContainerName, cancellationToken)
            : ssh.ConnectAsync(target, enrollment.ContainerName, viewer, cancellationToken);
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
