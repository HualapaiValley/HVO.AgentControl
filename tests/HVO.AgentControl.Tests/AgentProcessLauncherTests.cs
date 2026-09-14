using System.Diagnostics;
using HVO.AgentControl.Runtime;
using HVO.AgentControl.Terminal;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Contract for the privileged-launcher seam. These assert the launch requests
/// the controller can express at all; the kernel-level isolation itself is
/// proven by <see cref="AgentIsolationContainerTests"/> under Docker.
/// </summary>
public sealed class AgentProcessLauncherTests
{
    private const string LauncherPath = "/usr/local/bin/agentcontrol-launch";

    [Fact]
    public void DirectLauncherLeavesStartInfoUnchanged()
    {
        var launcher = AgentProcessLauncher.Direct;
        Assert.False(launcher.IsEnabled);
        Assert.Null(launcher.LauncherPath);

        var startInfo = new ProcessStartInfo { FileName = "opencode" };
        startInfo.ArgumentList.Add("acp");

        launcher.WrapAcp(startInfo, "127.0.0.1", 4096, "/data/workspace");

        Assert.Equal("opencode", startInfo.FileName);
        Assert.Equal(new[] { "acp" }, startInfo.ArgumentList);
    }

    [Fact]
    public void BlankLauncherPathIsTreatedAsDirect()
    {
        Assert.False(new AgentProcessLauncher(null).IsEnabled);
        Assert.False(new AgentProcessLauncher(string.Empty).IsEnabled);
        Assert.False(new AgentProcessLauncher("   ").IsEnabled);
    }

    [Fact]
    public void WrapAcpRebuildsTheArgumentVectorFromValidatedValues()
    {
        var launcher = new AgentProcessLauncher(LauncherPath);
        var startInfo = new ProcessStartInfo { FileName = "/tmp/attacker-opencode" };
        startInfo.ArgumentList.Add("acp");
        startInfo.ArgumentList.Add("--dangerous");

        launcher.WrapAcp(startInfo, "127.0.0.1", 4096, "/data/workspace");

        // The caller's executable and arguments never survive: the launcher owns
        // the opencode mapping, so a compromised caller cannot substitute one.
        Assert.Equal(LauncherPath, startInfo.FileName);
        Assert.Equal(
            new[] { "acp", "127.0.0.1", "4096", "/data/workspace" },
            startInfo.ArgumentList);
    }

    [Fact]
    public void WrapTmuxPrependsTheOperationTokenAndPreservesArgumentOrder()
    {
        var launcher = new AgentProcessLauncher(LauncherPath);
        var startInfo = new ProcessStartInfo { FileName = "tmux" };
        foreach (var argument in new[] { "has-session", "-t", "=agentcontrol" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        launcher.WrapTmux(startInfo);

        Assert.Equal(LauncherPath, startInfo.FileName);
        Assert.Equal(
            new[] { "tmux", "has-session", "-t", "=agentcontrol" },
            startInfo.ArgumentList);
    }

    [Fact]
    public void WrapPtyDropsTheInterpreterAndScriptPath()
    {
        var launcher = new AgentProcessLauncher(LauncherPath);
        var startInfo = TerminalEndpoint.CreateBridgeStartInfo(
            "/data/home",
            "/app/Terminal/pty_bridge.py",
            "agentcontrol",
            new Dictionary<string, string?> { ["PATH"] = "/usr/bin" },
            launcher);

        // Neither python3 nor the bridge path is caller-controlled any more.
        Assert.Equal(LauncherPath, startInfo.FileName);
        Assert.Equal(new[] { "pty", "/data/home", "agentcontrol" }, startInfo.ArgumentList);
        Assert.DoesNotContain("pty_bridge.py", startInfo.ArgumentList);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
    }

    [Fact]
    public void BridgeEnvironmentContractSurvivesWrapping()
    {
        var launcher = new AgentProcessLauncher(LauncherPath);
        var startInfo = TerminalEndpoint.CreateBridgeStartInfo(
            "/data/home",
            "/app/Terminal/pty_bridge.py",
            "agentcontrol",
            new Dictionary<string, string?>
            {
                ["PATH"] = "/usr/bin",
                ["OPENCODE_SERVER_PASSWORD"] = "placeholder",
            },
            launcher);

        Assert.Equal("/data/home", startInfo.Environment["HOME"]);
        Assert.False(startInfo.Environment.ContainsKey("OPENCODE_SERVER_PASSWORD"));
    }

    [Fact]
    public void TerminateOnAnExitedProcessReportsNoRequest()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/sh",
            ArgumentList = { "-c", "exit 0" },
            UseShellExecute = false,
        })!;
        process.WaitForExit();

        Assert.False(AgentProcessLauncher.Direct.TryTerminate(process, force: true));
    }

    [Fact]
    public void DirectTerminateStopsALiveChild()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/sh",
            ArgumentList = { "-c", "sleep 30" },
            UseShellExecute = false,
        })!;

        Assert.True(AgentProcessLauncher.Direct.TryTerminate(process, force: true));
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(10)));
    }
}
