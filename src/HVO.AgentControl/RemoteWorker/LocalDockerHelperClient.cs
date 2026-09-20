using System.Net.Sockets;
using System.Text.Json;
using HVO.AgentControl.DockerHelper.Protocol;
using HVO.AgentControl.Organization;
using HVO.AgentControl.Worker;
using Microsoft.Extensions.Options;

namespace HVO.AgentControl.RemoteWorker;

public sealed class LocalDockerHelperClient(IOptions<WorkerControlOptions> configured)
{
    private readonly WorkerControlOptions _options = configured.Value;

    public Task<RemoteOperationResult> ExecuteAsync(ExecutionTarget target, DockerOperation operation, IReadOnlyList<string> tokens, CancellationToken token) => RequestAsync(target, new("request", Id(), operation, tokens, TimeoutSeconds: Timeout(operation)), null, token);
    public Task<RemoteOperationResult> CreateVolumeAsync(ExecutionTarget target, VolumeCreateSpec spec, CancellationToken token) => RequestAsync(target, new("request", Id(), DockerOperation.VolumeCreate, VolumeCreate: spec, TimeoutSeconds: Timeout(DockerOperation.VolumeCreate)), null, token);
    public Task<RemoteOperationResult> CreateContainerAsync(ExecutionTarget target, ContainerCreateSpec spec, CancellationToken token) => RequestAsync(target, new("request", Id(), DockerOperation.ContainerCreate, ContainerCreate: spec, TimeoutSeconds: Timeout(DockerOperation.ContainerCreate)), null, token);
    public Task<RemoteOperationResult> BootstrapAsync(ExecutionTarget target, BootstrapSpec spec, byte[] key, CancellationToken token) => RequestAsync(target, new("request", Id(), DockerOperation.Bootstrap, Bootstrap: spec, BinaryLength: key.Length, TimeoutSeconds: Timeout(DockerOperation.Bootstrap)), key, token);
    public Task<RemoteOperationResult> BuildImageAsync(ExecutionTarget target, ImageBuildSpec spec, byte[] contextTar, CancellationToken token) => RequestAsync(target, new("request", Id(), DockerOperation.ImageBuild, ImageBuild: spec, BinaryLength: contextTar.Length, TimeoutSeconds: Timeout(DockerOperation.ImageBuild)), contextTar, token);
    public async Task<HostTaskVerification> WorkspaceVerifyAsync(ExecutionTarget target, WorkspaceVerifySpec spec, CancellationToken token)
    {
        var result = await RequestAsync(target, new("request", Id(), DockerOperation.WorkspaceVerify, WorkspaceVerify: spec, TimeoutSeconds: spec.MaximumSeconds), null, token).ConfigureAwait(false);
        if (result.ExitCode != 0) return new(result.ErrorCategory == "timeout" ? WorkerTaskVerificationStates.Uncertain : WorkerTaskVerificationStates.Failed, null, null, null, result.ErrorCategory == "timeout" ? "verification-timeout-uncertain" : "verification-command-failed");
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            var passed = root.GetProperty("state").GetString() == "passed";
            return new(
                passed ? WorkerTaskVerificationStates.Passed : WorkerTaskVerificationStates.Failed,
                passed ? root.GetProperty("manifest").GetRawText() : null,
                passed ? root.GetProperty("testSummary").GetRawText() : null,
                null,
                passed ? null : root.TryGetProperty("failureDetail", out var detail) ? detail.GetString() ?? "verification-failed" : "verification-failed");
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new(WorkerTaskVerificationStates.Uncertain, null, null, null, "verification-output-invalid");
        }
    }
    public Task<Stream> ConnectAsync(ExecutionTarget target, string container, CancellationToken token) => OpenStreamAsync(target, DockerOperation.Connector, container, token);
    public Task<Stream> ConnectViewerAsync(ExecutionTarget target, string container, CancellationToken token) => OpenStreamAsync(target, DockerOperation.Viewer, container, token);

    public async Task<HostProbePayload> ProbeAsync(ExecutionTarget target, CancellationToken token)
    {
        var info = Require(await ExecuteAsync(target, DockerOperation.Probe, [], token).ConfigureAwait(false));
        var version = Require(await ExecuteAsync(target, DockerOperation.VersionProbe, [], token).ConfigureAwait(false));
        // The local helper cannot and must not see the daemon's host root path.
        // Measure the local-volume backing filesystem through a fixed anonymous
        // volume instead; SSH targets continue using host-side df of DockerRootDir.
        var root = HostProbeParser.ReadDockerRootDirectory(info); // retain it as daemon evidence; never mount/use it locally
        var free = Require(await ExecuteAsync(target, DockerOperation.LocalStorageFree, [], token).ConfigureAwait(false));
        return new(info, version, root, HostProbeParser.ParseAvailableBytes(free));
    }

    private async Task<RemoteOperationResult> RequestAsync(ExecutionTarget target, DockerHelperRequest request, byte[]? binary, CancellationToken token)
    {
        EnsureLocal(target); using var socket = await ConnectSocketAsync(token).ConfigureAwait(false); using var stream = new NetworkStream(socket, ownsSocket: false);
        await WorkerProtocol.WriteFrameAsync(stream, request, token).ConfigureAwait(false); if (binary is not null) { await stream.WriteAsync(binary, token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false); }
        using var response = await WorkerProtocol.ReadFrameAsync(stream, token).ConfigureAwait(false) ?? throw new RemoteWorkerUnavailableException("The Docker helper closed without a result.", true);
        var type = response.RootElement.GetProperty("type").GetString();
        if (type == "result") { var result = JsonSerializer.Deserialize<DockerHelperResult>(response.RootElement.GetRawText(), DockerHelperProtocol.JsonOptions)!; return new(result.ExitCode, result.Stdout, result.ErrorCategory); }
        throw new WorkerControlConfigurationException("The Docker helper rejected the fixed request.");
    }

    private async Task<Stream> OpenStreamAsync(ExecutionTarget target, DockerOperation operation, string container, CancellationToken token)
    {
        EnsureLocal(target); var socket = await ConnectSocketAsync(token).ConfigureAwait(false); var stream = new NetworkStream(socket, ownsSocket: true);
        try { await WorkerProtocol.WriteFrameAsync(stream, new DockerHelperRequest("request", Id(), operation, [container], TimeoutSeconds: Timeout(operation)), token).ConfigureAwait(false); using var response = await WorkerProtocol.ReadFrameAsync(stream, token).ConfigureAwait(false) ?? throw new IOException(); if (response.RootElement.GetProperty("type").GetString() != "stream-open") throw new WorkerControlConfigurationException("The Docker helper rejected the stream request."); return stream; }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task<Socket> ConnectSocketAsync(CancellationToken token)
    {
        if (!Path.IsPathRooted(_options.LocalDockerHelperSocketPath)) throw new WorkerControlConfigurationException("The local Docker helper socket path must be absolute.");
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_options.LocalDockerHelperSocketPath), token).ConfigureAwait(false);
            return socket;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            socket.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is SocketException or IOException or UnauthorizedAccessException)
        {
            socket.Dispose();
            throw new RemoteWorkerUnavailableException("The local Docker helper is unavailable.", transport: true, exception);
        }
    }
    private int Timeout(DockerOperation operation) => operation is DockerOperation.ImageBuild or DockerOperation.ImageVerify ? _options.ImageBuildTimeoutSeconds : _options.OperationTimeoutSeconds;
    private static string Id() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    private static void EnsureLocal(ExecutionTarget target) { if (!target.IsLocalDocker) throw new WorkerControlConfigurationException("The Docker helper client accepts only a local-docker execution target."); }
    private static string Require(RemoteOperationResult result) => result.ExitCode == 0 ? result.StandardOutput : throw new RemoteWorkerUnavailableException("The Docker helper probe failed.");
}
