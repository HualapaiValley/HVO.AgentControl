using System.Diagnostics;
using HVO.AgentControl.DockerHelper;
using HVO.AgentControl.Organization;
using HVO.AgentControl.RemoteWorker;
using HVO.AgentControl.Runtime;
using HVO.AgentControl.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Real-daemon coverage for the controller-local routed provisioner and helper
/// connector. This intentionally stops at authenticated bridge status: constructing
/// a complete owner-approved hire/store fixture with an in-image fake ACP provider
/// remains outside this step's real-Docker coverage.
/// </summary>
[Trait("Category", "DockerHelper")]
public sealed class LocalManagedHiringDockerIntegrationTests
{
    private const string Image = "hvo-agentcontrol:worker-tests";
    private static readonly bool Required = Environment.GetEnvironmentVariable("AGENTCONTROL_DOCKER_REQUIRED") == "1";
    private static readonly Lazy<bool> Available = new(Probe);

    [Fact]
    public async Task RoutedProvisionerCreatesStartsAndAuthenticatesWorkerBridgeThroughHelper()
    {
        if (!Available.Value) return;
        var digest = ImageDigest();
        var platform = Run(["version", "--format", "{{.Server.Os}}/{{.Server.Arch}}"]).Output.Trim();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var workerId = "worker-local-" + suffix;
        var container = "agentcontrol-worker-" + suffix;
        var volumes = new[] { "agentcontrol-control-" + suffix, "agentcontrol-home-" + suffix, "agentcontrol-workspace-" + suffix, "agentcontrol-session-" + suffix };
        var key = Enumerable.Range(0, 32).Select(index => (byte)(index * 7 % 251)).ToArray();
        var keyDirectory = Path.Combine(Path.GetTempPath(), "agentcontrol-local-route-" + suffix);
        Directory.CreateDirectory(keyDirectory);
        var keyPath = Path.Combine(keyDirectory, "worker.key");
        File.WriteAllBytes(keyPath, key);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        await using var helper = await HelperHarness.StartAsync(digest, platform);
        var options = Options.Create(new WorkerControlOptions
        {
            Enabled = true,
            ControllerId = "controller-local",
            ApprovedImageDigest = digest,
            ApprovedImagePlatform = platform,
            LocalDockerHelperSocketPath = helper.SocketPath,
            ExpectedControllerUid = CurrentUid(),
        });
        var localClient = new LocalDockerHelperClient(options);
        var local = new LocalDockerExecutionOperations(localClient);
        var ssh = new RejectingSshOperations();
        var routed = new RoutingExecutionOperations(ssh, local);
        var provisioner = new RemoteWorkerProvisionerAdapter(routed);
        var target = new ExecutionTarget("local-docker", "local-docker", null);
        var identity = new WorkerResourceIdentity("org-local", "controller-local", target.Id, workerId, "binding-local", "operation-local");
        try
        {
            foreach (var volume in volumes)
                _ = await provisioner.CreateVolumeAsync(target, new VolumeCreateSpec(volume, identity), token);
            var bootstrap = provisioner.BootstrapAsync(target, new BootstrapSpec(volumes[0], digest, platform, identity), key.ToArray(), token);
            try { await bootstrap.WaitAsync(TimeSpan.FromSeconds(30), token); }
            catch (TimeoutException exception) { throw new Xunit.Sdk.XunitException($"helper bootstrap timed out; runner operations: {string.Join(',', helper.Runner.Operations)}", exception); }
            var mounts = new[]
            {
                new NamedVolumeMount(volumes[0], "/control"),
                new NamedVolumeMount(volumes[1], "/home/worker"),
                new NamedVolumeMount(volumes[2], "/workspace"),
                new NamedVolumeMount(volumes[3], "/session"),
            };
            _ = await provisioner.CreateContainerAsync(target, new ContainerCreateSpec(container, digest, platform, identity, mounts, 1024L * 1024 * 1024, 1, 128, [digest]), token);
            await provisioner.StartAsync(target, container, token);

            var storePath = Path.Combine(keyDirectory, "control.db");
            using var store = new OrganizationStore(storePath);
            store.OpenAndAdopt("AgentControl Local Integration", "owner-approved:test", null, "seed://fresh");
            var bindingId = Scalar(storePath, "SELECT id FROM runtime_bindings LIMIT 1");
            using var control = ControlHost(keyDirectory, store);
            var routingConnector = new RoutingWorkerConnector(control, options, new ProcessWorkerConnector(options), localClient);
            InsertEnrollment(storePath, bindingId, workerId, container, volumes, digest, platform, keyPath, WorkerProtocol.KeyId(key));
            WorkerBridgeClient? connected = null;
            Exception? last = null;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (connected is null && DateTime.UtcNow < deadline)
            {
                try { connected = await WorkerBridgeClient.ConnectAsync(routingConnector, workerId, "controller-local", keyPath, TimeSpan.FromSeconds(20), token, expectedControllerUid: CurrentUid(), operationTimeout: TimeSpan.FromSeconds(30)); }
                catch (Exception exception) when (exception is WorkerProtocolException or IOException) { last = exception; await Task.Delay(250, token); }
            }
            await using var bridge = connected ?? throw new Xunit.Sdk.XunitException("the routed helper bridge did not become ready", last);
            var status = await bridge.InvokeAsync("status", new { operation = "status" }, mutation: false, token);
            Assert.Equal("status", status.Operation);
            Assert.True(status.Result.TryGetProperty("processState", out var processState));
            Assert.Equal("running", processState.GetString());
            Assert.Empty(ssh.Calls);
        }
        finally
        {
            _ = Run(["rm", "-f", container]);
            foreach (var volume in volumes) _ = Run(["volume", "rm", "-f", volume]);
            try { Directory.Delete(keyDirectory, true); } catch (IOException) { }
        }
    }

    private sealed class RejectingSshOperations : ISshExecutionOperations
    {
        public List<string> Calls { get; } = [];
        private Task<T> Reject<T>() { Calls.Add(typeof(T).Name); throw new Xunit.Sdk.XunitException("local target was routed to SSH"); }
        public Task<RemoteOperationResult> ExecuteAsync(ExecutionTarget target, RemoteDockerOperation operation, IReadOnlyList<string> tokens, byte[]? standardInput, CancellationToken cancellationToken) => Reject<RemoteOperationResult>();
        public Task<RemoteOperationResult> CreateVolumeAsync(ExecutionTarget target, VolumeCreateSpec specification, CancellationToken cancellationToken) => Reject<RemoteOperationResult>();
        public Task<RemoteOperationResult> CreateContainerAsync(ExecutionTarget target, ContainerCreateSpec specification, CancellationToken cancellationToken) => Reject<RemoteOperationResult>();
        public Task<RemoteOperationResult> BootstrapAsync(ExecutionTarget target, BootstrapSpec specification, byte[] standardInput, CancellationToken cancellationToken) => Reject<RemoteOperationResult>();
        public Task<RemoteOperationResult> BuildImageAsync(ExecutionTarget target, ImageBuildSpec specification, byte[] contextTar, CancellationToken cancellationToken) => Reject<RemoteOperationResult>();
        public Task<HostProbePayload> ProbeHostAsync(ExecutionTarget target, CancellationToken cancellationToken) => Reject<HostProbePayload>();
    }

    private sealed class RecordingRunner : IDockerProcessRunner
    {
        private readonly DockerProcessRunner _inner = new();
        public List<string> Operations { get; } = [];
        public Task<HVO.AgentControl.DockerHelper.Protocol.DockerHelperResult> RunAsync(string id, DockerOperation operation, string[] argv, byte[]? input, TimeSpan timeout, CancellationToken token) { lock (Operations) Operations.Add(operation.ToString()); return _inner.RunAsync(id, operation, argv, input, timeout, token); }
        public Task<Process> StartStreamAsync(string[] argv, CancellationToken token) { lock (Operations) Operations.Add("Stream"); return _inner.StartStreamAsync(argv, token); }
    }

    private sealed class HelperHarness : IAsyncDisposable
    {
        private readonly DockerHelperServer _server;
        private readonly Task _running;
        private readonly string _directory;
        private HelperHarness(DockerHelperServer server, Task running, string directory, string socketPath, RecordingRunner runner) { _server = server; _running = running; _directory = directory; SocketPath = socketPath; Runner = runner; }
        public string SocketPath { get; }
        public RecordingRunner Runner { get; }
        public static async Task<HelperHarness> StartAsync(string digest, string platform)
        {
            var directory = Path.Combine(Path.GetTempPath(), "agentcontrol-managed-helper-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(directory);
            var socket = Path.Combine(directory, "helper.sock");
            var runner = new RecordingRunner();
            var server = new DockerHelperServer(new(socket, CurrentUid(), -1, new DockerPolicy(digest, platform, 2L * 1024 * 1024 * 1024, 2, 256, RequireAgentControlPrefixes: true)), runner: runner);
            var harness = new HelperHarness(server, Task.Run(() => server.RunAsync(default)), directory, socket, runner);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(socket) && DateTime.UtcNow < deadline) await Task.Delay(25);
            if (!File.Exists(socket)) throw new Xunit.Sdk.XunitException("the helper socket did not appear");
            return harness;
        }
        public async ValueTask DisposeAsync()
        {
            await _server.DisposeAsync();
            try { await _running.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            try { Directory.Delete(_directory, true); } catch (IOException) { }
        }
    }

    private static AcpControlHost ControlHost(string directory, OrganizationStore store)
    {
        var control = new AcpControlHost(Options.Create(new ControlOptions { DataDirectory = directory, PrivateDataDirectory = directory }), NullLogger<AcpControlHost>.Instance);
        typeof(AcpControlHost).GetField("_organization", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(control, store);
        return control;
    }

    private static void InsertEnrollment(string databasePath, string bindingId, string workerId, string container, string[] volumes, string digest, string platform, string keyPath, string keyId)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO worker_enrollments(worker_id,runtime_binding_id,host_id,organization_id,container_name,container_ref,control_volume_name,control_volume_ref,home_volume_name,home_volume_ref,workspace_volume_name,workspace_volume_ref,session_volume_name,session_volume_ref,resource_labels_hash,expected_image_digest,expected_platform,controller_id,key_file_path,key_id,bridge_socket_path,lifecycle_status,worker_generation,process_generation,ownership_epoch,enabled,revision,created_at,updated_at) VALUES($worker,$binding,'local-docker',(SELECT id FROM organizations LIMIT 1),$container,$container,$control,$control,$home,$home,$workspace,$workspace,$session,$session,$labels,$digest,$platform,'controller-local',$key,$keyId,'/control/bridge.sock','enrolled',0,0,0,1,1,$now,$now)";
        foreach (var item in new[] { ("$worker", workerId), ("$binding", bindingId), ("$container", container), ("$control", volumes[0]), ("$home", volumes[1]), ("$workspace", volumes[2]), ("$session", volumes[3]), ("$labels", "sha256:" + new string('a', 64)), ("$digest", digest), ("$platform", platform), ("$key", keyPath), ("$keyId", keyId), ("$now", DateTimeOffset.UtcNow.ToString("O")) }) command.Parameters.AddWithValue(item.Item1, item.Item2);
        command.ExecuteNonQuery();
    }

    private static string Scalar(string databasePath, string sql)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }

    private static bool Probe()
    {
        if (!OperatingSystem.IsLinux()) return false;
        var available = Run(["info"]).ExitCode == 0 && EnsureImage();
        if (!available && Required) Assert.Fail("Docker and the worker image are required");
        return available;
    }

    private static bool EnsureImage() => Run(["image", "inspect", Image]).ExitCode == 0 || Run(["build", "--target", "worker", "--tag", Image, RepositoryRoot()]).ExitCode == 0;
    private static string ImageDigest() => Run(["image", "inspect", "--format", "{{.Id}}", Image]).Output.Trim();
    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static int CurrentUid() => (int)geteuid();
    [System.Runtime.InteropServices.DllImport("libc")] private static extern uint geteuid();
    private static (int ExitCode, string Output) Run(string[] args)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(600_000)) { process.Kill(true); return (-1, output + " timed out"); }
        return (process.ExitCode, output);
    }
}
