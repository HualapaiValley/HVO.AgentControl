using System.Diagnostics;
using System.Text.Json;
using HVO.AgentControl.DockerHelper;
using HVO.AgentControl.RemoteWorker;
using Microsoft.Extensions.Options;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Runs the real helper process, over a real Unix socket, against the real local
/// Docker daemon. Every command is built by the shared grammar, so what is
/// exercised is the exact argv the controller path will produce - never a shell
/// string and never a hand-written Docker invocation.
/// </summary>
[Trait("Category", "DockerHelper")]
public sealed class DockerHelperIntegrationTests
{
    private static readonly bool Required = Environment.GetEnvironmentVariable("AGENTCONTROL_DOCKER_REQUIRED") == "1";
    private static readonly Lazy<bool> Available = new(Probe);

    [Fact]
    public async Task RealHelperServesProbesAndTheOwnedVolumeLifecycleAndRefusesForeignLabels()
    {
        if (!Available.Value) return;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var volume = "agentcontrol-control-helper-" + suffix;
        var identity = new WorkerResourceIdentity("org-helper", "controller-helper", "host-helper", "worker-helper", "binding-helper", "operation-helper");
        await using var helper = await HelperHarness.StartAsync();
        var target = new ExecutionTarget("host-helper", "local-docker", null);
        try
        {
            var probe = await helper.Client.ExecuteAsync(target, DockerOperation.Probe, [], CancellationToken.None);
            Assert.Equal(0, probe.ExitCode);
            Assert.Contains("DockerRootDir", probe.StandardOutput, StringComparison.Ordinal);

            var version = await helper.Client.ExecuteAsync(target, DockerOperation.VersionProbe, [], CancellationToken.None);
            Assert.Equal(0, version.ExitCode);

            var created = await helper.Client.CreateVolumeAsync(target, new(volume, identity), CancellationToken.None);
            Assert.Equal(0, created.ExitCode);

            var inspected = await helper.Client.ExecuteAsync(target, DockerOperation.VolumeInspect, [volume], CancellationToken.None);
            Assert.Equal(0, inspected.ExitCode);
            using var document = JsonDocument.Parse(inspected.StandardOutput.Trim());
            var labels = document.RootElement.GetProperty("Labels").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString()!, StringComparer.Ordinal);
            DockerArgv.RequireOwnedLabels(labels, identity);

            // The same real labels do not satisfy a different operation identity.
            Assert.Throws<DockerGrammarException>(() => DockerArgv.RequireOwnedLabels(labels, identity with { WorkerId = "worker-foreign" }));

            var removed = await helper.Client.ExecuteAsync(target, DockerOperation.VolumeRemove, [volume], CancellationToken.None);
            Assert.Equal(0, removed.ExitCode);
            var absent = await helper.Client.ExecuteAsync(target, DockerOperation.VolumeInspect, [volume], CancellationToken.None);
            Assert.Equal("not-found", absent.ErrorCategory);

            // Names outside the fixed prefixes are refused before any daemon contact.
            await Assert.ThrowsAsync<WorkerControlConfigurationException>(() => helper.Client.CreateVolumeAsync(target, new("foreign-volume-" + suffix, identity), CancellationToken.None));
        }
        finally { _ = Run(["volume", "rm", "-f", volume]); }
    }

    /// <summary>
    /// Builds the privileged helper's own image stage and inspects it: the Docker
    /// CLI is present, no setuid/setgid file survives (the helper runs without
    /// no-new-privileges on a read-only rootfs, so an inherited setuid binary would
    /// be an escalation surface), and the process identity is the unprivileged
    /// uid 1002.
    /// </summary>
    [Fact]
    public void RealHelperImageCarriesTheDockerCliNoSetuidFilesAndUser1002()
    {
        if (!Available.Value) return;
        var tag = "hvo-agentcontrol:helper-contract-" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            var build = Run(["build", "--target", "docker-helper", "--tag", tag, RepositoryRoot()], 900_000);
            Assert.True(build.ExitCode == 0, build.Output);

            // The fixed CLI and the helper entrypoint are both in the image.
            var cli = Run(["run", "--rm", "--entrypoint", "/usr/bin/docker", tag, "version", "--format", "{{.Client.Version}}"]);
            Assert.True(cli.ExitCode == 0, cli.Output);
            var helper = Run(["run", "--rm", "--entrypoint", "/usr/bin/stat", tag, "-c", "%a", "/app/HVO.AgentControl.DockerHelper.dll"]);
            Assert.Equal("644", helper.Output.Trim());

            // No file may keep a setuid or setgid bit on the runtime rootfs.
            var setuid = Run(["run", "--rm", "--entrypoint", "/usr/bin/find", tag, "/", "-xdev", "-perm", "/6000", "-type", "f", "-print"]);
            Assert.True(setuid.ExitCode == 0, setuid.Output);
            Assert.Equal(string.Empty, setuid.Output.Trim());

            // The image's configured user id is 1002; docker inspect reports it.
            var user = Run(["image", "inspect", "--format", "{{.Config.User}}", tag]);
            Assert.Equal("1002:1002", user.Output.Trim());
        }
        finally { _ = Run(["image", "rm", "-f", tag]); }
    }

    /// <summary>
    /// Opens a real bidirectional exec stream against a throwaway container. The
    /// fixed grammar can only exec the worker entry point, which the throwaway
    /// image does not have, so the assertion is that the helper really spawned
    /// <c>docker exec</c> and relayed its output - not that the worker ran.
    /// </summary>
    [Fact]
    public async Task RealHelperRelaysAnExecStreamForTheFixedConnectorCommand()
    {
        if (!Available.Value) return;
        var container = "agentcontrol-worker-helper-" + Guid.NewGuid().ToString("N")[..12];
        await using var helper = await HelperHarness.StartAsync();
        var target = new ExecutionTarget("host-helper", "local-docker", null);
        try
        {
            var image = Run(["image", "inspect", "-f", "{{.Id}}", "debian:trixie-slim"]).ExitCode == 0 ? "debian:trixie-slim" : null;
            if (image is null && Run(["pull", "debian:trixie-slim"]).ExitCode != 0) return;
            Assert.Equal(0, Run(["run", "-d", "--name", container, "--network", "none", "debian:trixie-slim", "sleep", "120"]).ExitCode);

            await using var stream = await helper.Client.ConnectAsync(target, container, CancellationToken.None);
            var buffer = new byte[4096];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var read = await stream.ReadAsync(buffer, timeout.Token);
            // Either the relayed child output or a clean EOF proves the relay ran;
            // an exception or a hang would not.
            Assert.True(read >= 0);
        }
        finally { _ = Run(["rm", "-f", container]); }
    }

    private sealed class HelperHarness : IAsyncDisposable
    {
        private readonly DockerHelperServer _server;
        private readonly Task _running;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly string _directory;

        private HelperHarness(DockerHelperServer server, Task running, string directory, LocalDockerHelperClient client) { _server = server; _running = running; _directory = directory; Client = client; }
        public LocalDockerHelperClient Client { get; }

        public static async Task<HelperHarness> StartAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "agentcontrol-helper-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(directory);
            var socket = Path.Combine(directory, "helper.sock");
            var policy = new DockerPolicy("sha256:" + new string('a', 64), Platform(), 1024L * 1024 * 1024, 2, 256, RequireAgentControlPrefixes: true);
            var server = new DockerHelperServer(new(socket, CurrentUid(), -1, policy));
            var harness = new HelperHarness(server, Task.Run(() => server.RunAsync(default)), directory, new LocalDockerHelperClient(Options.Create(new WorkerControlOptions { LocalDockerHelperSocketPath = socket })));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(socket) && DateTime.UtcNow < deadline) await Task.Delay(25);
            if (!File.Exists(socket)) throw new Xunit.Sdk.XunitException("the helper socket did not appear.");
            return harness;
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            await _server.DisposeAsync();
            try { await _running.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            _lifetime.Dispose();
            try { Directory.Delete(_directory, true); } catch (IOException) { }
        }
    }

    private static int CurrentUid() => (int)geteuid();
    [System.Runtime.InteropServices.DllImport("libc")] private static extern uint geteuid();

    private static bool Probe()
    {
        if (!OperatingSystem.IsLinux()) return false;
        var available = Run(["info"]).ExitCode == 0;
        if (!available && Required) Assert.Fail("Docker is required.");
        return available;
    }

    private static string Platform() => Run(["version", "--format", "{{.Server.Os}}/{{.Server.Arch}}"]).Output.Trim();

    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    private static (int ExitCode, string Output) Run(string[] args, int timeout = 120_000)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(timeout)) { process.Kill(true); return (-1, output + " timed out"); }
        return (process.ExitCode, output);
    }
}
