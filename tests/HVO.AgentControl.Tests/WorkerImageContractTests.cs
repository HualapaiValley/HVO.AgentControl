using System.Diagnostics;
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
        Assert.Contains("chmod 0700 /worker-control /worker/home /worker/workspace /worker/session", dockerfile);
        Assert.Contains("find / -xdev -perm /6000 -type f -exec chmod a-s", dockerfile);
        Assert.Contains("profiles: [\"worker\"]", compose);
        Assert.Contains("restart: \"no\"", compose);
        Assert.DoesNotContain("docker.sock", compose, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DOCKER_HOST", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("owner-password", compose[compose.IndexOf("worker-local:", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains("operation == \"start\"", supervisor);
        Assert.Contains("set(request) != {\"operation\", \"processGeneration\"}", supervisor);
        Assert.Contains("socket.SO_PEERCRED", supervisor);
        Assert.Contains("IO_TIMEOUT", supervisor);
        Assert.DoesNotContain("time.sleep(0.05)", supervisor);
        Assert.DoesNotContain("shell=True", supervisor);
    }

    [Fact]
    public void RealAlternateUidsCannotCrossPrivateBoundaryAndImageHasNoPrivilegeArtifacts()
    {
        if (!Available.Value) return;
        var script = "set -eu; test \"$(stat -c %a /worker-control)\" = 700; test \"$(stat -c %a /worker/home)\" = 700; " +
            "runuser -u employee -- sh -c '! test -r /worker-control/bridge.key; ! test -w /worker-control; ! test -r /worker-control/bridge.sock'; " +
            "runuser -u bridge -- sh -c '! test -r /worker/home; ! test -w /worker/workspace; test \"$(id -u)\" = 1101'; " +
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
            var bootstrap = RunWithInput(["run", "--rm", "-i", "--user", "1101:1101", "--entrypoint", "/usr/bin/dotnet", "-e", "WORKER_CONTROL_DIRECTORY=/worker-control", "-v", volume + ":/worker-control", Image, "/app/HVO.AgentControl.Worker.dll", "--worker-bootstrap-key"], key);
            Assert.True(bootstrap.ExitCode == 0, bootstrap.Output);
            var start = Run(["run", "-d", "--name", name, "--cap-drop", "ALL", "--cap-add", "CHOWN", "--cap-add", "SETUID", "--cap-add", "SETGID", "--cap-add", "KILL", "--security-opt", "no-new-privileges", "-v", volume + ":/worker-control", "-e", "WORKER_ID=test-worker", "-e", "WORKER_CONTROLLER_ID=test-controller", Image]);
            Assert.True(start.ExitCode == 0, start.Output);
            Thread.Sleep(1000);
            var pid = Run(["inspect", "-f", "{{.State.Pid}}", name]);
            Assert.True(pid.ExitCode == 0, pid.Output);
            var process = Run(["exec", name, "sh", "-c", "test $(cat /proc/1/comm) = python3; test $(ps -eo stat= | awk '$1 ~ /^Z/ {n++} END {print n+0}') = 0"]);
            Assert.True(process.ExitCode == 0, process.Output);
            var stop = Run(["stop", "-t", "10", name]);
            Assert.True(stop.ExitCode == 0, stop.Output);
        }
        finally { _ = Run(["rm", "-f", name]); _ = Run(["volume", "rm", "-f", volume]); }
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
