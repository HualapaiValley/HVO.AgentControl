using HVO.AgentControl.Terminal;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Asserts the bridge child environment contract without spawning python. All
/// credential values below are inert placeholders used only to prove removal.
/// </summary>
public sealed class TerminalEndpointEnvironmentTests
{
    private const string Placeholder = "test-token-placeholder";

    private static IReadOnlyDictionary<string, string?> ParentEnvironment()
    {
        return new Dictionary<string, string?>
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/host",
            ["TERM"] = "dumb",
            ["COLORTERM"] = "false",
            ["LANG"] = "en_US.UTF-8",
            ["TMUX"] = "/tmp/tmux-1000/default,123,0",
            ["TMUX_PANE"] = "%1",
            ["GH_TOKEN"] = Placeholder,
            ["GH_PAT"] = Placeholder,
            ["GITHUB_TOKEN"] = Placeholder,
            ["CLIPROXY_API_KEY"] = Placeholder,
            ["ANTHROPIC_API_KEY"] = Placeholder,
            ["OPENAI_API_KEY"] = Placeholder,
            ["AWS_SECRET_ACCESS_KEY"] = Placeholder,
            ["AZURE_CLIENT_SECRET"] = Placeholder,
            ["GOOGLE_APPLICATION_CREDENTIALS"] = "/secrets/gcp.json",
            ["NPM_TOKEN"] = Placeholder,
            ["NODE_AUTH_TOKEN"] = Placeholder,
            ["SSH_AUTH_SOCK"] = "/run/ssh-agent.sock",
            ["FLEET_HOME"] = "/home/host/fleet",
            ["HERDR_SOCKET_PATH"] = "/run/herdr.sock",
            ["OPENCODE_CONFIG"] = "/home/host/opencode.json",
            ["OPENCODE_CONFIG_CONTENT"] = "{}",
            ["OPENCODE_SERVER_PASSWORD"] = Placeholder,
            ["XDG_CONFIG_HOME"] = "/home/host/.config",
            ["Control__OwnerPasswordFile"] = "/run/agentcontrol-owner",
        };
    }

    [Fact]
    public void CreateBridgeStartInfoRebuildsABenignChildEnvironment()
    {
        var startInfo = TerminalEndpoint.CreateBridgeStartInfo(
            "/data/home",
            "/app/Terminal/pty_bridge.py",
            "agentcontrol",
            ParentEnvironment());

        // Terminal-specific values are applied last.
        Assert.Equal("/data/home", startInfo.Environment["HOME"]);
        Assert.Equal("xterm-256color", startInfo.Environment["TERM"]);
        Assert.Equal("truecolor", startInfo.Environment["COLORTERM"]);
        Assert.Equal("C.UTF-8", startInfo.Environment["LANG"]);

        // Benign host values still pass through.
        Assert.Equal("/usr/bin", startInfo.Environment["PATH"]);

        // tmux state is removed so the attach client cannot nest.
        Assert.False(startInfo.Environment.ContainsKey("TMUX"));
        Assert.False(startInfo.Environment.ContainsKey("TMUX_PANE"));
    }

    [Fact]
    public void CreateBridgeStartInfoDropsCredentialFamilies()
    {
        var startInfo = TerminalEndpoint.CreateBridgeStartInfo(
            "/data/home",
            "/app/Terminal/pty_bridge.py",
            "agentcontrol",
            ParentEnvironment());

        string[] denied =
        [
            "GH_TOKEN",
            "GH_PAT",
            "GITHUB_TOKEN",
            "CLIPROXY_API_KEY",
            "ANTHROPIC_API_KEY",
            "OPENAI_API_KEY",
            "AWS_SECRET_ACCESS_KEY",
            "AZURE_CLIENT_SECRET",
            "GOOGLE_APPLICATION_CREDENTIALS",
            "NPM_TOKEN",
            "NODE_AUTH_TOKEN",
            "SSH_AUTH_SOCK",
            "FLEET_HOME",
            "HERDR_SOCKET_PATH",
            "OPENCODE_CONFIG",
            "OPENCODE_CONFIG_CONTENT",
            "OPENCODE_SERVER_PASSWORD",
            "XDG_CONFIG_HOME",
            "Control__OwnerPasswordFile",
        ];

        foreach (var key in denied)
        {
            Assert.False(startInfo.Environment.ContainsKey(key));
        }

        Assert.DoesNotContain(
            startInfo.Environment.Values,
            value => string.Equals(value, Placeholder, StringComparison.Ordinal));
    }

    [Fact]
    public void CreateBridgeStartInfoUsesArgumentListWithoutShell()
    {
        var startInfo = TerminalEndpoint.CreateBridgeStartInfo(
            "/data/home",
            "/app/Terminal/pty_bridge.py",
            "agentcontrol",
            ParentEnvironment());

        Assert.Equal("python3", startInfo.FileName);
        Assert.Equal("/data/home", startInfo.WorkingDirectory);
        Assert.Equal(new[] { "-u", "/app/Terminal/pty_bridge.py", "agentcontrol" }, startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
    }

    [Fact]
    public void CreateBridgeStartInfoRejectsBlankTargets()
    {
        Assert.Throws<ArgumentException>(() =>
            TerminalEndpoint.CreateBridgeStartInfo(" ", "/app/Terminal/pty_bridge.py", "agentcontrol"));
        Assert.Throws<ArgumentException>(() =>
            TerminalEndpoint.CreateBridgeStartInfo("/data/home", " ", "agentcontrol"));
        Assert.Throws<ArgumentException>(() =>
            TerminalEndpoint.CreateBridgeStartInfo("/data/home", "/app/Terminal/pty_bridge.py", " "));
    }

    [Fact]
    public void CreateBridgeStartInfoDoesNotInheritCurrentProcessEnvironment()
    {
        // With an explicit baseline the result must not contain unrelated host
        // variables (proves the process block is rebuilt, not mutated).
        var startInfo = TerminalEndpoint.CreateBridgeStartInfo(
            "/data/home",
            "/app/Terminal/pty_bridge.py",
            "agentcontrol",
            new Dictionary<string, string?> { ["PATH"] = "/usr/bin" });

        Assert.True(startInfo.Environment.Count <= 6);
        Assert.Equal("/usr/bin", startInfo.Environment["PATH"]);
        Assert.Equal("/data/home", startInfo.Environment["HOME"]);
    }
}
