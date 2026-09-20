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

        // Only mount lines matter: comments may explain the daemon socket, but a
        // mount of it must appear exactly once in the whole file, and in the helper.
        static bool MountsDaemonSocket(string line) => line.TrimStart().StartsWith("- ", StringComparison.Ordinal)
            && line.Contains("docker.sock", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(control.Split('\n'), MountsDaemonSocket);
        Assert.Contains("docker-helper-socket:/run/agentcontrol-docker-helper", control, StringComparison.Ordinal);
        Assert.Contains("/var/run/docker.sock:/var/run/docker.sock", helper, StringComparison.Ordinal);
        Assert.Single(compose.Split('\n'), MountsDaemonSocket);
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

    /// <summary>
    /// The helper socket is the only channel the controller may use for Docker.
    /// Exactly two services mount the shared volume (the helper writes it, the
    /// control service reads it), the controller has no daemon socket, and the
    /// helper stage installs the Docker CLI while the final control stage does
    /// not.
    /// </summary>
    [Fact]
    public void ComposeSharesTheHelperSocketVolumeWithControlOnlyAndKeepsDockerOutOfTheControlImage()
    {
        var root = ControllerIsolationLayoutTests.RepositoryRoot();
        var compose = File.ReadAllText(Path.Combine(root, "compose.yaml"));
        var dockerfile = File.ReadAllText(Path.Combine(root, "Dockerfile"));

        // The shared helper socket volume is mounted by exactly two services.
        var socketMounts = compose.Split('\n')
            .Count(line => line.Contains("docker-helper-socket:/run/agentcontrol-docker-helper", StringComparison.Ordinal));
        Assert.Equal(2, socketMounts);

        var control = compose[..compose.IndexOf("  docker-helper:", StringComparison.Ordinal)];
        var helper = compose[compose.IndexOf("  docker-helper:", StringComparison.Ordinal)..compose.IndexOf("  worker-local:", StringComparison.Ordinal)];
        // The worker service section ends where the top-level volumes block begins;
        // that block legitimately declares the shared volume.
        var workerStart = compose.IndexOf("  worker-local:", StringComparison.Ordinal);
        var volumesStart = compose.IndexOf("\nvolumes:", StringComparison.Ordinal);
        Assert.True(volumesStart > workerStart, "the top-level volumes block must follow the worker service");
        var worker = compose[workerStart..volumesStart];
        Assert.Contains("docker-helper-socket:/run/agentcontrol-docker-helper", control, StringComparison.Ordinal);
        Assert.Contains("docker-helper-socket:/run/agentcontrol-docker-helper", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("docker-helper-socket", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("docker.sock", worker, StringComparison.OrdinalIgnoreCase);

        // The final control stage is `final` -> `control` -> `control-runtime`.
        // Docker's CLI/daemon package is installed only in the helper stage, so
        // neither the control runtime nor the final image can invoke Docker.
        var controlStages = Stages(dockerfile, "control-runtime", "control", "final");
        Assert.DoesNotContain("docker.io", controlStages, StringComparison.Ordinal);
        Assert.DoesNotContain("docker-ce", controlStages, StringComparison.Ordinal);
        Assert.DoesNotContain("docker-cli", controlStages, StringComparison.Ordinal);
        Assert.DoesNotContain("install -y docker", controlStages, StringComparison.Ordinal);

        var helperStage = Stages(dockerfile, "docker-helper");
        Assert.Contains("docker.io", helperStage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Compose must pass the deployment inputs the helper fails closed without:
    /// the approved base digest and the daemon's group id. The comments must say
    /// they are required for deployment and that an empty digest makes the
    /// helper refuse every container-create until it is set.
    /// </summary>
    [Fact]
    public void ComposePassesTheApprovedBaseDigestAndDockerGidAndDocumentsFailClosed()
    {
        var root = ControllerIsolationLayoutTests.RepositoryRoot();
        var compose = File.ReadAllText(Path.Combine(root, "compose.yaml"));

        Assert.Contains(
            "AGENTCONTROL_DOCKER_HELPER_APPROVED_BASE_DIGEST: ${AGENTCONTROL_DOCKER_HELPER_APPROVED_BASE_DIGEST:-}",
            compose,
            StringComparison.Ordinal);
        Assert.Contains("${AGENTCONTROL_DOCKER_GID:-", compose, StringComparison.Ordinal);
        Assert.Contains("group_add", compose, StringComparison.Ordinal);

        // The fail-closed contract must be stated where an operator reads it.
        Assert.Contains("fail", compose, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AGENTCONTROL_DOCKER_HELPER_APPROVED_BASE_DIGEST", compose, StringComparison.Ordinal);
        Assert.Contains("AGENTCONTROL_DOCKER_GID", compose, StringComparison.Ordinal);
        Assert.Contains("required for deployment", compose, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the concatenated text of the named Dockerfile stages, from the
    /// `FROM ... AS <name>` line up to the next `FROM`.
    /// </summary>
    private static string Stages(string dockerfile, params string[] names)
    {
        var wanted = names.ToHashSet(StringComparer.Ordinal);
        var selected = new List<string>();
        string? current = null;
        foreach (var line in dockerfile.Split('\n'))
        {
            if (line.StartsWith("FROM ", StringComparison.Ordinal))
            {
                var marker = " AS ";
                var index = line.IndexOf(marker, StringComparison.Ordinal);
                current = index < 0 ? line[marker.Length..].Trim() : line[(index + marker.Length)..].Trim();
                continue;
            }
            if (current is not null && wanted.Contains(current)) selected.Add(line);
        }
        return string.Join('\n', selected);
    }

    /// <summary>
    /// An employee container can only ever receive the four fixed named volumes.
    /// The grammar has no bind-mount form at all, rejects a Docker socket path as
    /// either a mount name or a mount point, and never emits `-v`/`--volume`.
    /// </summary>
    [Fact]
    public void GrammarMountsOnlyTheFourNamedVolumesAndRefusesAnyBindOrDockerSocket()
    {
        var identity = Identity();
        var volumes = Volumes();
        var argv = DockerArgv.BuildContainerCreate(new("agentcontrol-worker-x", Digest, "linux/amd64", identity, volumes, 1, 1, 32, [Digest]), Policy);

        // Every emitted mount is a named volume at one of the four fixed points.
        var mounts = argv.Where((token, index) => index > 0 && argv[index - 1] == "--mount").ToArray();
        Assert.Equal(4, mounts.Length);
        Assert.All(mounts, mount => Assert.StartsWith("type=volume,src=agentcontrol-", mount, StringComparison.Ordinal));
        Assert.DoesNotContain("type=bind", string.Join(' ', argv), StringComparison.Ordinal);
        Assert.DoesNotContain("-v", argv);
        Assert.DoesNotContain("--volume", argv);

        // A Docker socket path as the source name or as the mount point is refused.
        Assert.Throws<DockerGrammarException>(() => DockerArgv.BuildContainerCreate(
            new("agentcontrol-worker-x", Digest, "linux/amd64", identity, [new("/var/run/docker.sock", "/control"), .. volumes.Skip(1)], 1, 1, 32, [Digest]), Policy));
        Assert.Throws<DockerGrammarException>(() => DockerArgv.BuildContainerCreate(
            new("agentcontrol-worker-x", Digest, "linux/amd64", identity, [new("agentcontrol-control-x", "/var/run/docker.sock"), .. volumes.Skip(1)], 1, 1, 32, [Digest]), Policy));

        // A fifth mount, or a duplicate, is refused: the set must match exactly.
        Assert.Throws<DockerGrammarException>(() => DockerArgv.BuildContainerCreate(
            new("agentcontrol-worker-x", Digest, "linux/amd64", identity, [.. volumes, new("agentcontrol-extra-x", "/control")], 1, 1, 32, [Digest]), Policy));
    }

    [Fact]
    public void ApprovedBaseDigestIsRequiredAndTheHelperFailsClosedWhenEmpty()
    {
        // The helper's policy is built from the environment; an unset digest is
        // the empty string. Every container-create must then be refused rather
        // than silently accept an unapproved image. The same holds for a
        // malformed digest.
        var emptyPolicy = new DockerPolicy(string.Empty, "linux/amd64", 1024 * 1024 * 1024, 2, 256, RequireAgentControlPrefixes: true);
        Assert.Throws<DockerGrammarException>(() => DockerArgv.BuildContainerCreate(Spec(), emptyPolicy));
        Assert.Throws<DockerGrammarException>(() => DockerArgv.BuildBootstrap(new("agentcontrol-control-x", Digest, "linux/amd64", Identity()), emptyPolicy));

        var malformedPolicy = emptyPolicy with { ApprovedBaseDigest = "not-a-digest" };
        Assert.Throws<DockerGrammarException>(() => DockerArgv.BuildContainerCreate(Spec(), malformedPolicy));
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
        var inherited = identity.Labels.ToDictionary(x => x.Key, x => x.Value);
        inherited[ProfileBuildContext.ContextHashLabel] = "sha256:" + new string('a', 64);
        inherited[ProfileBuildContext.BaseDigestLabel] = Digest;
        DockerArgv.RequireOwnedLabels(inherited, identity);
        var changed = new Dictionary<string, string>(inherited) { ["agentcontrol.worker"] = "other" };
        Assert.Throws<DockerGrammarException>(() => DockerArgv.RequireOwnedLabels(changed, identity));
        var unexpected = new Dictionary<string, string>(inherited) { ["agentcontrol.unexpected"] = "present" };
        Assert.Throws<DockerGrammarException>(() => DockerArgv.RequireOwnedLabels(unexpected, identity));
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

    [Fact]
    public async Task TypedBinaryRequestDoesNotLosePayloadBytesAfterEnvelope()
    {
        var path = Path.Combine(Path.GetTempPath(), "agentcontrol-helper-binary-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path)); listener.Listen(1);
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(path));
        var accepted = await listener.AcceptAsync();
        var runner = new CapturingRunner();
        await using var server = new DockerHelperServer(new(path + ".unused", 1001, -1, Policy), new FakeCredentials(1001), runner);
        using var stream = new NetworkStream(client, ownsSocket: false);
        var payload = Enumerable.Range(0, 45).Select(index => (byte)index).ToArray();
        var request = new DockerHelperRequest("request", "binary-id", DockerOperation.Bootstrap, Bootstrap: new("agentcontrol-control-x", Digest, "linux/amd64", Identity()), BinaryLength: payload.Length, TimeoutSeconds: 30);
        var frame = JsonSerializer.SerializeToUtf8Bytes(request, DockerHelperProtocol.JsonOptions);
        byte[] combined = [.. frame, (byte)'\n', .. payload];
        await stream.WriteAsync(combined);
        await stream.FlushAsync();
        var handling = server.HandleAsync(accepted, CancellationToken.None);

        using var response = await WorkerProtocol.ReadFrameAsync(stream, CancellationToken.None);
        await handling;
        Assert.Equal("result", response!.RootElement.GetProperty("type").GetString());
        Assert.Equal(payload, runner.Input);
        try { File.Delete(path); } catch (IOException) { }
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

    /// <summary>
    /// An authorized peer that never sends its envelope must not hold the
    /// accepted connection open: the helper closes it with a protocol error once
    /// the bounded envelope window elapses.
    /// </summary>
    [Fact]
    public async Task IdleAuthorizedConnectionIsClosedAfterTheEnvelopeDeadline()
    {
        var path = Path.Combine(Path.GetTempPath(), "agentcontrol-helper-idle-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path)); listener.Listen(1);
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(path));
        var accepted = await listener.AcceptAsync();
        await using var server = new DockerHelperServer(new(path + ".unused", 1001, -1, Policy, EnvelopeTimeoutSeconds: 1), new FakeCredentials(1001), new FakeRunner());
        using var stream = new NetworkStream(client, ownsSocket: false);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var handling = server.HandleAsync(accepted, CancellationToken.None);
        using var response = await WorkerProtocol.ReadFrameAsync(stream, CancellationToken.None);
        await handling.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(response);
        Assert.Equal("error", response.RootElement.GetProperty("type").GetString());
        Assert.Equal("protocol-error", response.RootElement.GetProperty("error").GetString());
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(8), "the idle connection was not closed within the bounded window");
        // The socket is closed by the helper afterwards: a further read yields EOF.
        var trailing = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(trailing, CancellationToken.None));
        try { File.Delete(path); } catch (IOException) { }
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
    private static ContainerCreateSpec Spec() => new("agentcontrol-worker-x", Digest, "linux/amd64", Identity(), Volumes(), 1, 1, 32, [Digest]);
    private sealed class FakeCredentials(int uid) : IPeerCredentialProvider { public int GetUid(Socket socket) => uid; }
    private sealed class FakeRunner : IDockerProcessRunner
    {
        public Task<DockerHelperResult> RunAsync(string id, DockerOperation operation, string[] argv, byte[]? input, TimeSpan timeout, CancellationToken token) => Task.FromResult(new DockerHelperResult("result", id, 0, string.Empty, "none"));
        public Task<Process> StartStreamAsync(string[] argv, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class CapturingRunner : IDockerProcessRunner
    {
        public byte[]? Input { get; private set; }
        public Task<DockerHelperResult> RunAsync(string id, DockerOperation operation, string[] argv, byte[]? input, TimeSpan timeout, CancellationToken token) { Input = input?.ToArray(); return Task.FromResult(new DockerHelperResult("result", id, 0, string.Empty, "none")); }
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
