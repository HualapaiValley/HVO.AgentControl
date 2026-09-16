using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace HVO.AgentControl.Tests;

[Trait("Category", "DockerWorker")]
public sealed class WorkerImageContractTests
{
    private const string Image = "hvo-agentcontrol:worker-tests";
    private static readonly bool Required = Environment.GetEnvironmentVariable("AGENTCONTROL_DOCKER_REQUIRED") == "1";
    private static readonly Lazy<bool> Available = new(Build);

    [Fact]
    public void WorkerTargetHasFixedPinsIdentitiesVolumesAndNoControlIntegration()
    {
        var root = RepositoryRoot();
        var dockerfile = File.ReadAllText(Path.Combine(root, "Dockerfile"));
        var compose = File.ReadAllText(Path.Combine(root, "compose.yaml"));
        var supervisor = File.ReadAllText(Path.Combine(root, "src/container/worker-supervisor.py"));
        Assert.Contains("FROM mcr.microsoft.com/dotnet/sdk:10.0.401", dockerfile);
        Assert.Contains("ARG OPENCODE_VERSION=1.18.30", dockerfile);
        Assert.Contains(" AS worker", dockerfile);
        Assert.Contains("--uid 1101", dockerfile); Assert.Contains("--uid 1102", dockerfile);
        Assert.Contains("chmod 0700 /control /home/worker /workspace /session", dockerfile);
        Assert.Contains("find / -xdev -perm /6000 -type f -exec chmod a-s", dockerfile);
        Assert.Contains("profiles: [\"worker\"]", compose);
        Assert.Contains("restart: \"no\"", compose);
        Assert.Contains("network_mode: none", compose);
        Assert.Contains("read_only: true", compose); Assert.Contains("pids_limit: 256", compose); Assert.Contains("mem_limit: 2g", compose); Assert.Contains("cpus: 2.0", compose);
        Assert.DoesNotContain("docker.sock", compose, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DOCKER_HOST", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("owner-password", compose[compose.IndexOf("worker-local:", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains("operation == \"start\"", supervisor);
        Assert.Contains("operation == \"viewer-start\"", supervisor);
        Assert.Contains("socket.SCM_RIGHTS", supervisor);
        Assert.Contains("opencode\", \"acp\", \"--port\", \"4096\", \"--hostname\", \"127.0.0.1\", \"--cwd\", \"/workspace\", \"--pure", supervisor);
        Assert.Contains("opencode\", \"attach\", \"http://127.0.0.1:4096\", \"--dir\", \"/workspace\", \"--session", supervisor);
        Assert.Contains("set(request) != {\"operation\", \"processGeneration\"}", supervisor);
        Assert.Contains("socket.SO_PEERCRED", supervisor);
        Assert.Contains("IO_TIMEOUT", supervisor);
        Assert.DoesNotContain("time.sleep(0.05)", supervisor);
        Assert.Contains("name == \"bridge\"", supervisor);
        Assert.DoesNotContain("--worker-test-hold-store", dockerfile);
        Assert.DoesNotContain("shell=True", supervisor);
    }

    [Fact]
    public void RealAlternateUidsCannotCrossPrivateBoundaryAndImageHasNoPrivilegeArtifacts()
    {
        if (!Available.Value) return;
        var script = "set -eu; test \"$(stat -c %a /control)\" = 700; test \"$(stat -c %a /home/worker)\" = 700; " +
            "runuser -u employee -- sh -c '! test -r /control/bridge.key; ! test -w /control; ! test -r /control/bridge.sock; ! test -r /run/worker-supervisor.sock'; " +
            "runuser -u bridge -- sh -c '! test -r /home/worker; ! test -w /workspace; test \"$(id -u)\" = 1101'; " +
            "test -z \"$(find / -xdev -perm /6000 -type f -print -quit)\"; " +
            "test ! -S /var/run/docker.sock; test -z \"${DOCKER_HOST:-}\"; " +
            "runuser -u bridge -- sh -c 'grep -Eq \"^Cap(Eff|Prm):[[:space:]]+0+$\" /proc/self/status'";
        var result = Run(["run", "--rm", "--entrypoint", "/bin/bash", Image, "-c", script]);
        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public void PidOneTerminatesAndReapsFixedChildren()
    {
        if (!Available.Value) return;
        var name = "hvo-worker-lifecycle-" + Guid.NewGuid().ToString("N");
        var volume = name + "-control";
        try
        {
            Assert.True(Run(["volume", "create", volume]).ExitCode == 0);
            var key = Convert.ToBase64String(new byte[32]) + "\n";
            var bootstrap = RunWithInput(["run", "--rm", "-i", "--user", "1101:1101", "--entrypoint", "/usr/bin/dotnet", "-e", "WORKER_CONTROL_DIRECTORY=/control", "-v", volume + ":/control", Image, "/app/HVO.AgentControl.Worker.dll", "--worker-bootstrap-key"], key);
            Assert.True(bootstrap.ExitCode == 0, bootstrap.Output);
            var start = Run(["run", "-d", "--name", name, "--network", "none", "--cap-drop", "ALL", "--cap-add", "CHOWN", "--cap-add", "SETUID", "--cap-add", "SETGID", "--cap-add", "KILL", "--security-opt", "no-new-privileges", "-v", volume + ":/control", "-e", "WORKER_ID=test-worker", "-e", "WORKER_CONTROLLER_ID=test-controller", Image]);
            Assert.True(start.ExitCode == 0, start.Output);
            Thread.Sleep(1000);
            var pid = Run(["inspect", "-f", "{{.State.Pid}}", name]);
            Assert.True(pid.ExitCode == 0, pid.Output);
            var process = Run(["exec", name, "sh", "-c", "set -e; test $(cat /proc/1/comm) = python3; test $(ps -eo stat= | awk '$1 ~ /^Z/ {n++} END {print n+0}') = 0; p=$(pgrep -f '^/usr/local/bin/opencode acp --port 4096 --hostname 127.0.0.1 --cwd /workspace --pure$'); test -n \"$p\"; runuser -u employee -- sh -c \"tr '\\0' '\\n' </proc/$p/environ\" | grep -q '^OPENCODE_SERVER_USERNAME=opencode$'; runuser -u employee -- sh -c \"tr '\\0' '\\n' </proc/$p/environ\" | grep -q '^OPENCODE_SERVER_PASSWORD='; ! runuser -u employee -- sh -c \"tr '\\0' '\\n' </proc/$p/environ\" | grep -q '^WORKER_CONTROLLER_ID='; runuser -u employee -- sh -c '! test -r /run/worker-supervisor.sock; ! test -w /run/worker-supervisor.sock'"]);
            Assert.True(process.ExitCode == 0, process.Output);
            var network = Run(["inspect", "-f", "{{.HostConfig.NetworkMode}}", name]);
            Assert.True(network.ExitCode == 0 && network.Output.Trim() == "none", network.Output);
            var stop = Run(["stop", "-t", "10", name]);
            Assert.True(stop.ExitCode == 0, stop.Output);
        }
        finally { _ = Run(["rm", "-f", name]); _ = Run(["volume", "rm", "-f", volume]); }
    }

    [Fact]
    public void RealWorkerLaunchesAcpOnLoopbackAndAcceptsAttachCliContract()
    {
        if (!Available.Value) return;
        var name = "hvo-worker-cli-" + Guid.NewGuid().ToString("N");
        var volume = name + "-control";
        try
        {
            Assert.True(Run(["volume", "create", volume]).ExitCode == 0);
            var bootstrap = RunWithInput(["run", "--rm", "-i", "--user", "1101:1101", "--entrypoint", "/usr/bin/dotnet", "-e", "WORKER_CONTROL_DIRECTORY=/control", "-v", volume + ":/control", Image, "/app/HVO.AgentControl.Worker.dll", "--worker-bootstrap-key"], Convert.ToBase64String(new byte[32]) + "\n");
            Assert.True(bootstrap.ExitCode == 0, bootstrap.Output);
            var start = Run(["run", "-d", "--name", name, "--network", "none", "--cap-drop", "ALL", "--cap-add", "CHOWN", "--cap-add", "SETUID", "--cap-add", "SETGID", "--cap-add", "KILL", "--security-opt", "no-new-privileges", "-v", volume + ":/control", "-e", "WORKER_ID=test-worker", "-e", "WORKER_CONTROLLER_ID=test-controller", Image]);
            Assert.True(start.ExitCode == 0, start.Output);
            Thread.Sleep(1000);
            var contract = Run(["exec", name, "sh", "-c", "set -e; p=$(pgrep -f '^/usr/local/bin/opencode acp --port 4096 --hostname 127.0.0.1 --cwd /workspace --pure$'); test -n \"$p\"; grep -Eq '0100007F:1000 .* 0A ' /proc/$p/net/tcp; /usr/local/bin/opencode attach --help >/dev/null"]);
            Assert.True(contract.ExitCode == 0, contract.Output);
        }
        finally { _ = Run(["rm", "-f", name]); _ = Run(["volume", "rm", "-f", volume]); }
    }

    /// <summary>
    /// Runs the controller's own fixed command construction against a real local
    /// Docker daemon: the ephemeral bootstrap with controller-encoded key bytes,
    /// then the long-lived container. It proves the constructed argv, environment,
    /// capabilities, labels, network and mounts are exactly what the controller
    /// intends, then starts the container and proves the runtime network contract
    /// (eth0, loopback-only ACP, no published port), which no hermetic string
    /// assertion can establish.
    /// </summary>
    /// <remarks>
    /// Everything is disposable: no SSH, no approved-host configuration, no registry
    /// and no provider credentials. The container is attached to Docker's default
    /// bridge and can egress, but this test submits no prompt and its assertions do
    /// not depend on Internet or DNS reachability once the image is built. The
    /// container is created, started and inspected, then removed with its volumes.
    /// </remarks>
    [Fact]
    public void RealContainerCommandAppliesTheFixedIsolationBootstrapAndLabels()
    {
        if (!Available.Value) return;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var container = "agentcontrol-worker-contract-" + suffix;
        var volumes = new[] { "agentcontrol-control-contract-" + suffix, "agentcontrol-home-contract-" + suffix, "agentcontrol-workspace-contract-" + suffix, "agentcontrol-session-contract-" + suffix };
        try
        {
            var digest = ImageDigest();
            var identity = new HVO.AgentControl.RemoteWorker.WorkerResourceIdentity("org-contract", "controller-contract", "host-contract", "worker-contract", "binding-contract", "operation-contract");
            var options = new HVO.AgentControl.RemoteWorker.WorkerControlOptions { ControllerId = "controller-contract", ApprovedImageDigest = digest, ApprovedImagePlatform = Platform() };

            foreach (var volume in volumes) Assert.True(Run(["volume", "create", volume]).ExitCode == 0);

            // The controller's own encoder produces the bytes the worker parses.
            var key = Enumerable.Range(0, 32).Select(x => (byte)(x * 3 % 251)).ToArray();
            var encoded = HVO.AgentControl.RemoteWorker.WorkerBootstrapEncoding.Encode(key);
            var bootstrapArguments = LocalArguments(HVO.AgentControl.RemoteWorker.RemoteWorkerCommandBuilder.BuildBootstrap(
                LocalHost(), options, new HVO.AgentControl.RemoteWorker.BootstrapSpec(volumes[0], digest, options.ApprovedImagePlatform, identity), encoded));
            var bootstrap = RunWithInput(bootstrapArguments, Encoding.UTF8.GetString(encoded));
            Assert.True(bootstrap.ExitCode == 0, bootstrap.Output);
            Assert.Contains(HVO.AgentControl.Worker.WorkerProtocol.KeyId(key), bootstrap.Output, StringComparison.Ordinal);

            // The key is enrolled before the long-lived container is ever created.
            Assert.NotEqual(0, Run(["container", "inspect", "-f", "{{.Id}}", container]).ExitCode);

            var mounts = new[]
            {
                new HVO.AgentControl.RemoteWorker.NamedVolumeMount(volumes[0], "/control"),
                new HVO.AgentControl.RemoteWorker.NamedVolumeMount(volumes[1], "/home/worker"),
                new HVO.AgentControl.RemoteWorker.NamedVolumeMount(volumes[2], "/workspace"),
                new HVO.AgentControl.RemoteWorker.NamedVolumeMount(volumes[3], "/session"),
            };
            var createArguments = LocalArguments(HVO.AgentControl.RemoteWorker.RemoteWorkerCommandBuilder.BuildContainerCreate(
                LocalHost(), options, new HVO.AgentControl.RemoteWorker.ContainerCreateSpec(container, digest, options.ApprovedImagePlatform, identity, mounts, options.MemoryBytes, options.CpuLimit, options.PidsLimit)));
            // The controller-provisioned worker uses the fixed default bridge so the
            // approved provider is reachable, and must never publish ingress. Assert
            // the positional flag/value pair in the argv, not a loose substring.
            Assert.Equal(1, createArguments.Count(argument => argument == "--network"));
            var networkIndex = Array.IndexOf(createArguments, "--network");
            Assert.True(networkIndex >= 0 && networkIndex + 1 < createArguments.Length, "the create argv must carry a network value.");
            Assert.Equal("bridge", createArguments[networkIndex + 1]);
            Assert.NotEqual("host", createArguments[networkIndex + 1]);
            Assert.DoesNotContain("--publish", createArguments);
            Assert.DoesNotContain("-p", createArguments);
            Assert.DoesNotContain("--expose", createArguments);
            var create = Run(createArguments);
            Assert.True(create.ExitCode == 0, create.Output);

            var inspected = Run(["container", "inspect", "-f",
                "{{.HostConfig.NetworkMode}}|{{.HostConfig.ReadonlyRootfs}}|{{.HostConfig.Memory}}|{{.HostConfig.PidsLimit}}|{{json .HostConfig.CapDrop}}|{{json .HostConfig.CapAdd}}|{{json .HostConfig.SecurityOpt}}|{{json .Config.Env}}|{{json .Config.Labels}}|{{json .HostConfig.PortBindings}}|{{json .Mounts}}",
                container]);
            Assert.True(inspected.ExitCode == 0, inspected.Output);
            var fields = inspected.Output.Trim().Split('|');

            Assert.Equal("bridge", fields[0]);
            Assert.Equal("true", fields[1]);
            Assert.Equal(options.MemoryBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), fields[2]);
            Assert.Equal(options.PidsLimit.ToString(System.Globalization.CultureInfo.InvariantCulture), fields[3]);
            Assert.Contains("ALL", fields[4], StringComparison.Ordinal);
            foreach (var capability in new[] { "CHOWN", "SETUID", "SETGID", "KILL" }) Assert.Contains(capability, fields[5], StringComparison.Ordinal);
            Assert.Contains("no-new-privileges", fields[6], StringComparison.Ordinal);

            var environment = JsonSerializer.Deserialize<string[]>(fields[7])!;
            Assert.Contains("WORKER_CONTROL_DIRECTORY=/control", environment);
            Assert.Contains("WORKER_ID=worker-contract", environment);
            Assert.Contains("WORKER_CONTROLLER_ID=controller-contract", environment);

            var labels = JsonSerializer.Deserialize<Dictionary<string, string>>(fields[8])!;
            foreach (var expected in identity.Labels) Assert.Equal(expected.Value, labels[expected.Key]);
            HVO.AgentControl.RemoteWorker.RemoteWorkerCommandBuilder.RequireOwnedLabels(labels, identity);

            Assert.True(fields[9] is "{}" or "null", "the worker container must publish no ports: " + fields[9]);

            var mounted = JsonSerializer.Deserialize<JsonElement>(fields[10]);
            var destinations = mounted.EnumerateArray().Select(x => x.GetProperty("Destination").GetString() ?? string.Empty).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "/control", "/home/worker", "/session", "/workspace" }, destinations);
            Assert.All(mounted.EnumerateArray(), mount => Assert.Equal("volume", mount.GetProperty("Type").GetString()));

            // The assertions above only prove the requested configuration. Start the
            // container and prove the runtime network contract on the shared default
            // bridge: the worker gets an eth0, while ACP stays bound to container
            // loopback only and no host port is published.
            var start = Run(["container", "start", container]);
            Assert.True(start.ExitCode == 0, start.Output);

            var listeners = WaitForAcpListeners(container) ?? throw new Xunit.Sdk.XunitException("the fixed ACP process and its loopback:4096 listener did not appear within the bound.");

            var eth0 = Run(["exec", container, "sh", "-c", "test -e /sys/class/net/eth0"]);
            Assert.True(eth0.ExitCode == 0, "a container on the default bridge must have an eth0 interface: " + eth0.Output);

            // A live default-bridge attachment must carry an IPv4 address of its own.
            var address = Run(["inspect", "-f", "{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}", container]).Output.Trim();
            Assert.Matches("^[0-9]+\\.[0-9]+\\.[0-9]+\\.[0-9]+$", address);

            // /proc/<pid>/net/tcp is the container network namespace's IPv4 table.
            // Local addresses are little-endian hex and state 0A is LISTEN. ACP is
            // given the literal IPv4 loopback address 127.0.0.1, so its only IPv4
            // LISTEN on :4096 is 0100007F:1000. That single equality already excludes
            // 0.0.0.0 and this container's bridge address, so no separate negative
            // assertions are needed.
            Assert.True(listeners.IPv4.Readable, "the process IPv4 TCP table must be readable: " + listeners.IPv4.Error);
            Assert.NotEmpty(listeners.IPv4.Values);
            Assert.All(listeners.IPv4.Values, value => Assert.Equal("0100007F:1000", value, ignoreCase: true));
            // /proc/<pid>/net/tcp6 is the same namespace's IPv6 table. ACP is bound to
            // the IPv4 literal 127.0.0.1, so it must not open any tcp6 LISTEN on
            // :4096 - not even the otherwise-acceptable [::1]:4096.
            Assert.True(listeners.IPv6.Readable, "the process IPv6 TCP table must be readable: " + listeners.IPv6.Error);
            Assert.Empty(listeners.IPv6.Values);

            var stop = Run(["container", "stop", "--timeout", "10", container]);
            Assert.True(stop.ExitCode == 0, stop.Output);
        }
        finally
        {
            _ = Run(["rm", "-f", container]);
            foreach (var volume in volumes) _ = Run(["volume", "rm", "-f", volume]);
        }
    }

    /// <summary>An approved-host stand-in whose SSH prefix is stripped for local execution.</summary>
    private static HVO.AgentControl.RemoteWorker.ApprovedExecutionHost LocalHost() => new()
    {
        Id = "host-contract",
        Hostname = "worker.invalid",
        Port = 22,
        Username = "docker",
        KnownHostsPath = "/dev/null",
        IdentityFilePath = "/dev/zero",
    };

    /// <summary>
    /// Takes the exact remote command the controller built and runs it against the
    /// local daemon, so the asserted argv is the controller's own construction.
    /// </summary>
    private static string[] LocalArguments(HVO.AgentControl.RemoteWorker.RemoteCommand command)
    {
        var remote = command.Arguments[^1];
        Assert.StartsWith("docker ", remote, StringComparison.Ordinal);
        var arguments = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var character in remote["docker ".Length..])
        {
            if (character == '\'') { quoted = !quoted; continue; }
            if (character == ' ' && !quoted) { if (current.Length > 0) { arguments.Add(current.ToString()); current.Clear(); } continue; }
            current.Append(character);
        }
        if (current.Length > 0) arguments.Add(current.ToString());
        return [.. arguments];
    }

    /// <summary>
    /// Bounded poll for the fixed ACP process and its LISTEN sockets. The worker
    /// supervisor launches ACP asynchronously as PID1 starts, so the test waits a
    /// deterministic maximum for both the process and a loopback:4096 listener
    /// rather than sleeping an arbitrary fixed interval.
    /// </summary>
    private sealed record ListenerResult(bool Readable, string[] Values, string Error);

    private static (ListenerResult IPv4, ListenerResult IPv6)? WaitForAcpListeners(string container, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var found = Run(["exec", container, "pgrep", "-f", "^/usr/local/bin/opencode acp --port 4096 --hostname 127.0.0.1 --cwd /workspace --pure$"]);
            if (found.ExitCode == 0 && found.Output.Trim().Length > 0)
            {
                var pid = found.Output.Trim().Split('\n')[0].Trim();
                var ipv4 = Listeners(container, pid, "tcp");
                var ipv6 = Listeners(container, pid, "tcp6");
                if (ipv4.Readable && ipv4.Values.Length > 0) return (ipv4, ipv6);
            }
            Thread.Sleep(250);
        }
        return null;
    }

    /// <summary>
    /// Reads the LISTEN (state <c>0A</c>) local addresses for port 4096 (little-endian
    /// hex <c>1000</c>) from the process network namespace's IPv4 (<c>tcp</c>) or IPv6
    /// (<c>tcp6</c>) table.
    /// </summary>
    private static ListenerResult Listeners(string container, string pid, string table)
    {
        var path = "/proc/" + pid + "/net/" + table;
        var result = Run(["exec", container, "sh", "-c", "test -r " + path + "; readable=$?; if [ \"$readable\" -ne 0 ]; then exit 2; fi; awk '$4 == \"0A\" { print $2 }' " + path]);
        if (result.ExitCode == 2) return new(false, [], result.Output);
        if (result.ExitCode != 0) return new(false, [], result.Output);
        return new(true, result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(value => value.EndsWith(":1000", StringComparison.OrdinalIgnoreCase)).ToArray(), string.Empty);
    }

    private static string ImageDigest()
    {
        var result = Run(["image", "inspect", "-f", "{{.Id}}", Image]);
        Assert.True(result.ExitCode == 0, result.Output);
        var digest = result.Output.Trim();
        Assert.Matches("^sha256:[0-9a-f]{64}$", digest);
        return digest;
    }

    private static string Platform()
    {
        var result = Run(["version", "--format", "{{.Server.Os}}/{{.Server.Arch}}"]);
        Assert.True(result.ExitCode == 0, result.Output);
        return result.Output.Trim();
    }

    private static bool Build()
    {
        var probe = Run(["info"]);
        if (probe.ExitCode != 0)
        {
            if (Required) Assert.Fail("Docker is required: " + probe.Output);
            return false;
        }
        var result = Run(["build", "--target", "worker", "-t", Image, "."], RepositoryRoot(), 300_000);
        if (result.ExitCode != 0) Assert.Fail(result.Output);
        return true;
    }

    private static (int ExitCode, string Output) RunWithInput(string[] args, string input, string? workdir = null, int timeout = 60_000)
    {
        using var process = CreateProcess(args, workdir, redirectInput: true);
        process.Start(); process.StandardInput.Write(input); process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(timeout)) { process.Kill(true); return (-1, output + " timed out"); }
        return (process.ExitCode, output);
    }

    private static (int ExitCode, string Output) Run(string[] args, string? workdir = null, int timeout = 60_000)
    {
        using var process = CreateProcess(args, workdir, redirectInput: false);
        process.Start(); var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(timeout)) { process.Kill(true); return (-1, output + " timed out"); }
        return (process.ExitCode, output);
    }
    private static Process CreateProcess(string[] args, string? workdir, bool redirectInput)
    {
        var process = new Process { StartInfo = new ProcessStartInfo("docker") { WorkingDirectory = workdir ?? RepositoryRoot(), RedirectStandardInput = redirectInput, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        return process;
    }
    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
}
