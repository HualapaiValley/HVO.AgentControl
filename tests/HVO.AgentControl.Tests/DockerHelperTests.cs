using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.DockerHelper;
using HVO.AgentControl.DockerHelper.Protocol;
using HVO.AgentControl.RemoteWorker;
using HVO.AgentControl.Worker;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class DockerHelperTests
{
    private const string Digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DockerPolicy Policy = new(Digest, "linux/amd64", 1024 * 1024 * 1024, 2, 256, RequireAgentControlPrefixes: true);

    /// <summary>
    /// The daemon socket must reach exactly one service. The control service keeps
    /// only the private helper socket volume, and the helper itself is an
    /// unprivileged, capability-free, read-only-rootfs service.
    /// </summary>
    [Fact]
    public void ComposeGivesTheDaemonSocketOnlyToTheHardenedHelperService()
    {
        var root = ControllerIsolationLayoutTests.RepositoryRoot();
        var compose = File.ReadAllText(Path.Combine(root, "compose.yaml"));
        var dockerfile = File.ReadAllText(Path.Combine(root, "Dockerfile"));

        var control = compose[..compose.IndexOf("  docker-helper:", StringComparison.Ordinal)];
        var helper = compose[compose.IndexOf("  docker-helper:", StringComparison.Ordinal)..compose.IndexOf("  worker-local:", StringComparison.Ordinal)];

        Assert.DoesNotContain("docker.sock", control, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("docker-helper-socket:/run/agentcontrol-docker-helper", control, StringComparison.Ordinal);
        Assert.Contains("/var/run/docker.sock:/var/run/docker.sock", helper, StringComparison.Ordinal);
        // Exactly one line in the whole file mentions the daemon socket at all.
        Assert.Single(compose.Split('\n'), line => line.Contains("docker.sock", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("read_only: true", helper, StringComparison.Ordinal);
        Assert.Contains("init: true", helper, StringComparison.Ordinal);
        Assert.Contains("no-new-privileges:true", helper, StringComparison.Ordinal);
        Assert.Contains("- ALL", helper, StringComparison.Ordinal);
        Assert.Contains("group_add", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("ports:", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("privileged", helper, StringComparison.Ordinal);

        Assert.Contains(" AS docker-helper", dockerfile, StringComparison.Ordinal);
        Assert.Contains("--uid 1002", dockerfile, StringComparison.Ordinal);
        Assert.Contains("USER 1002:1002", dockerfile, StringComparison.Ordinal);
        Assert.Contains("chmod 0750 /run/agentcontrol-docker-helper", dockerfile, StringComparison.Ordinal);
    }

    [Fact]
    public void GrammarBuildsAllFixedOperationKindsWithoutAShell()
    {
        Assert.Equal(new[] { "docker", "system", "info", "--format", "{{json .}}" }, DockerArgv.Build(DockerOperation.Probe, [], Policy));
        Assert.Equal("df", DockerArgv.Build(DockerOperation.StorageFree, ["/var/lib/docker"], Policy)[0]);
        Assert.Equal("docker", DockerArgv.Build(DockerOperation.ImageInspect, [Digest], Policy)[0]);
        Assert.Equal("docker", DockerArgv.Build(DockerOperation.ImageTag, [Digest, "agentcontrol-pin:x"], Policy)[0]);
        Assert.Equal("docker", DockerArgv.Build(DockerOperation.ImageVerify, ["agentcontrol-profile:x", Digest, "linux/amd64"], Policy)[0]);
        Assert.Equal("docker", DockerArgv.Build(DockerOperation.ContainerStop, ["agentcontrol-worker-x"], Policy)[0]);
        Assert.Equal("--worker-pipe", DockerArgv.Build(DockerOperation.Connector, ["agentcontrol-worker-x"], Policy)[^1]);
        Assert.Equal("--worker-viewer-pipe", DockerArgv.Build(DockerOperation.Viewer, ["agentcontrol-worker-x"], Policy)[^1]);
    }

    [Fact]
    public void GrammarRefusesInjectionMountTamperingWrongPathsDigestsLabelsAndCeilings()
    {
        var identity = Identity(); var volumes = Volumes();
        Assert.Throws<DockerGrammarException>(() => DockerArgv.Build(DockerOperation.ContainerInspect, ["x;id"], Policy));
        Assert.Throws<DockerGrammarException>(() => DockerArgv.Build(DockerOperation.StorageFree, ["/var/lib/../etc"], Policy));
        Assert.Throws<DockerGrammarException>(() => DockerArgv.BuildContainerCreate(new("agentcontrol-worker-x", Digest, "linux/amd64", identity, [new("/host", "/control"), .. volumes.Skip(1)], 1, 1, 32, [Digest]), Policy));
        Assert.Throws<DockerGrammarException>(() => DockerArgv.BuildContainerCreate(new("agentcontrol-worker-x", Digest, "linux/amd64", identity, [new("agentcontrol-control-x", "/evil"), .. volumes.Skip(1)], 1, 1, 32, [Digest]), Policy));
        Assert.Throws<DockerGrammarException>(() => DockerArgv.BuildContainerCreate(new("agentcontrol-worker-x", "latest", "linux/amd64", identity, volumes, 1, 1, 32, [Digest]), Policy));
        Assert.Throws<DockerGrammarException>(() => DockerArgv.BuildContainerCreate(new("agentcontrol-worker-x", Digest, "linux/amd64", identity, volumes, Policy.MaxMemoryBytes + 1, 1, 32, [Digest]), Policy));
        var changed = identity.Labels.ToDictionary(x => x.Key, x => x.Value); changed["agentcontrol.worker"] = "other";
        Assert.Throws<DockerGrammarException>(() => DockerArgv.RequireOwnedLabels(changed, identity));
    }

    [Fact]
    public async Task ProtocolRejectsWrongPeerUnknownOperationAndOversizedBinary()
    {
        var request = new DockerHelperRequest("request", "id", (DockerOperation)999, TimeoutSeconds: 1);
        var response = await ExchangeAsync(request, new FakeCredentials(77), expectedUid: 1001);
        Assert.Equal("peer-unauthorized", response.GetProperty("error").GetString());

        response = await ExchangeAsync(request, new FakeCredentials(1001), expectedUid: 1001);
        Assert.Equal("request-rejected", response.GetProperty("error").GetString());

        response = await ExchangeAsync(new DockerHelperRequest("request", "id", DockerOperation.ImageBuild, ImageBuild: new(Digest, "linux/amd64", "prev-0123456789abcdef", Digest, "agentcontrol-result:x", false), BinaryLength: DockerHelperProtocol.MaxBinaryBytes + 1), new FakeCredentials(1001), expectedUid: 1001);
        Assert.Equal("protocol-error", response.GetProperty("error").GetString());
    }

    /// <summary>
    /// The stream path must relay the socket to the spawned child in both
    /// directions and stop when the child ends. <c>cat</c> stands in for
    /// <c>docker exec</c> so the relay itself is asserted, not the daemon.
    /// </summary>
    [Fact]
    public async Task StreamRelaysTheSocketToTheChildInBothDirections()
    {
        var path = Path.Combine(Path.GetTempPath(), "agentcontrol-helper-stream-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path)); listener.Listen(1);
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(path));
        var accepted = await listener.AcceptAsync();
        await using var server = new DockerHelperServer(new(path + ".unused", 1001, -1, Policy), new FakeCredentials(1001), new EchoRunner());
        using var stream = new NetworkStream(client, ownsSocket: false);
        await WorkerProtocol.WriteFrameAsync(stream, JsonSerializer.SerializeToElement(new DockerHelperRequest("request", "stream-id", DockerOperation.Connector, ["agentcontrol-worker-x"], TimeoutSeconds: 30), DockerHelperProtocol.JsonOptions), CancellationToken.None);
        var handling = server.HandleAsync(accepted, CancellationToken.None);

        using var open = await WorkerProtocol.ReadFrameAsync(stream, CancellationToken.None);
        Assert.Equal("stream-open", open!.RootElement.GetProperty("type").GetString());

        var payload = Encoding.UTF8.GetBytes("relay-probe\n");
        await stream.WriteAsync(payload, CancellationToken.None);
        var buffer = new byte[payload.Length];
        var read = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (read < buffer.Length) { var chunk = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token); if (chunk == 0) break; read += chunk; }
        Assert.Equal("relay-probe\n", Encoding.UTF8.GetString(buffer, 0, read));

        client.Shutdown(SocketShutdown.Send);
        await handling.WaitAsync(TimeSpan.FromSeconds(20));
        try { File.Delete(path); } catch (IOException) { }
    }

    [Fact]
    public async Task RunnerKillsTimedOutProcessAndBoundsStdout()
    {
        var runner = new DockerProcessRunner();
        var timed = await runner.RunAsync("x", DockerOperation.Probe, ["/bin/sh", "-c", "sleep 30"], null, TimeSpan.FromMilliseconds(50), CancellationToken.None);
        Assert.Equal("timeout", timed.ErrorCategory);
        await Assert.ThrowsAsync<IOException>(() => runner.RunAsync("x", DockerOperation.Probe, ["/usr/bin/python3", "-c", "print('x'*1100000)"], null, TimeSpan.FromSeconds(10), CancellationToken.None));
    }

    private static async Task<JsonElement> ExchangeAsync(DockerHelperRequest request, IPeerCredentialProvider credentials, int expectedUid)
    {
        var path = Path.Combine(Path.GetTempPath(), "agentcontrol-helper-test-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path)); listener.Listen(1);
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(path));
        var accepted = await listener.AcceptAsync();
        await using var server = new DockerHelperServer(new(path + ".unused", expectedUid, -1, Policy), credentials, new FakeRunner());
        using var stream = new NetworkStream(client, ownsSocket: false);
        // The request is written before the handler runs: an unauthorized peer is
        // refused and disconnected without ever reading the request, so writing
        // afterwards would race a closed socket rather than test the refusal.
        await WorkerProtocol.WriteFrameAsync(stream, JsonSerializer.SerializeToElement(request, DockerHelperProtocol.JsonOptions), CancellationToken.None);
        var handling = server.HandleAsync(accepted, CancellationToken.None);
        using var response = await WorkerProtocol.ReadFrameAsync(stream, CancellationToken.None);
        await handling;
        try { File.Delete(path); } catch (IOException) { }
        return response!.RootElement.Clone();
    }

    private static WorkerResourceIdentity Identity() => new("org", "controller", "host", "worker", "binding", "operation");
    private static NamedVolumeMount[] Volumes() => [new("agentcontrol-control-x", "/control"), new("agentcontrol-home-x", "/home/worker"), new("agentcontrol-workspace-x", "/workspace"), new("agentcontrol-session-x", "/session")];
    private sealed class FakeCredentials(int uid) : IPeerCredentialProvider { public int GetUid(Socket socket) => uid; }
    private sealed class FakeRunner : IDockerProcessRunner
    {
        public Task<DockerHelperResult> RunAsync(string id, DockerOperation operation, string[] argv, byte[]? input, TimeSpan timeout, CancellationToken token) => Task.FromResult(new DockerHelperResult("result", id, 0, string.Empty, "none"));
        public Task<Process> StartStreamAsync(string[] argv, CancellationToken token) => throw new NotSupportedException();
    }

    /// <summary>Spawns <c>cat</c> in place of <c>docker exec</c> so the relay is what is measured.</summary>
    private sealed class EchoRunner : IDockerProcessRunner
    {
        public Task<DockerHelperResult> RunAsync(string id, DockerOperation operation, string[] argv, byte[]? input, TimeSpan timeout, CancellationToken token) => throw new NotSupportedException();
        public Task<Process> StartStreamAsync(string[] argv, CancellationToken token)
        {
            var start = new ProcessStartInfo("/bin/cat") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            return Task.FromResult(Process.Start(start)!);
        }
    }
}
