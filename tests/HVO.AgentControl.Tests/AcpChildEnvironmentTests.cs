using HVO.AgentControl.Runtime;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class AcpChildEnvironmentTests
{
    [Fact]
    public void StripsKnownCredentialAndFleetVariables()
    {
        var baseline = new Dictionary<string, string?>
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/host",
            ["GH_TOKEN"] = "gh-secret",
            ["GH_PAT"] = "gh-secret",
            ["GITHUB_TOKEN"] = "gh-secret",
            ["GITHUB_APP_PRIVATE_KEY"] = "key",
            ["GIT_ASKPASS"] = "/usr/bin/askpass",
            ["SSH_AUTH_SOCK"] = "/run/ssh-agent.sock",
            ["FLEET_HOME"] = "/home/roys/fleet",
            ["FLEET_MEMBER"] = "ada",
            ["CLIPROXY_BASE_URL"] = "http://proxy",
            ["CLIPROXY_API_KEY"] = "proxy-secret",
            ["HERDR_SOCKET_PATH"] = "/run/herdr.sock",
            ["ANTHROPIC_API_KEY"] = "sk-ant-secret",
            ["OPENAI_API_KEY"] = "sk-openai-secret",
            ["GEMINI_API_KEY"] = "gemini-secret",
            ["AWS_SECRET_ACCESS_KEY"] = "aws-secret",
            ["AZURE_CLIENT_SECRET"] = "azure-secret",
            ["GOOGLE_APPLICATION_CREDENTIALS"] = "/secrets/gcp.json",
            ["NPM_TOKEN"] = "npm-secret",
            ["NODE_AUTH_TOKEN"] = "node-secret",
            ["DOCKER_AUTH_CONFIG"] = "{}",
            ["VAULT_TOKEN"] = "vault-secret",
            ["OPENCODE"] = "1",
            ["OPENCODE_PID"] = "42",
            ["OPENCODE_CONFIG"] = "/home/roys/fleet/agent.json",
            ["OPENCODE_CONFIG_CONTENT"] = "{}",
            ["OPENCODE_SERVER_PASSWORD"] = "stale",
            ["XDG_CONFIG_HOME"] = "/home/host/.config",
            ["XDG_DATA_HOME"] = "/home/host/.local/share",
            ["Control__Enabled"] = "true",
            ["Control__DataDirectory"] = "/data",
            ["CONTROL__SECRET"] = "nope",
        };

        var environment = ChildEnvironment.Build(overrides: null, baseEnvironment: baseline);

        Assert.Equal("/usr/bin", environment["PATH"]);
        Assert.Equal("/home/host", environment["HOME"]);
        Assert.DoesNotContain("GH_TOKEN", environment.Keys);
        Assert.DoesNotContain("GH_PAT", environment.Keys);
        Assert.DoesNotContain("GITHUB_TOKEN", environment.Keys);
        Assert.DoesNotContain("GITHUB_APP_PRIVATE_KEY", environment.Keys);
        Assert.DoesNotContain("GIT_ASKPASS", environment.Keys);
        Assert.DoesNotContain("SSH_AUTH_SOCK", environment.Keys);
        Assert.DoesNotContain("FLEET_HOME", environment.Keys);
        Assert.DoesNotContain("FLEET_MEMBER", environment.Keys);
        Assert.DoesNotContain("CLIPROXY_BASE_URL", environment.Keys);
        Assert.DoesNotContain("CLIPROXY_API_KEY", environment.Keys);
        Assert.DoesNotContain("HERDR_SOCKET_PATH", environment.Keys);
        Assert.DoesNotContain("ANTHROPIC_API_KEY", environment.Keys);
        Assert.DoesNotContain("OPENAI_API_KEY", environment.Keys);
        Assert.DoesNotContain("GEMINI_API_KEY", environment.Keys);
        Assert.DoesNotContain("AWS_SECRET_ACCESS_KEY", environment.Keys);
        Assert.DoesNotContain("AZURE_CLIENT_SECRET", environment.Keys);
        Assert.DoesNotContain("GOOGLE_APPLICATION_CREDENTIALS", environment.Keys);
        Assert.DoesNotContain("NPM_TOKEN", environment.Keys);
        Assert.DoesNotContain("NODE_AUTH_TOKEN", environment.Keys);
        Assert.DoesNotContain("DOCKER_AUTH_CONFIG", environment.Keys);
        Assert.DoesNotContain("VAULT_TOKEN", environment.Keys);
        Assert.DoesNotContain("OPENCODE", environment.Keys);
        Assert.DoesNotContain("OPENCODE_PID", environment.Keys);
        Assert.DoesNotContain("OPENCODE_CONFIG", environment.Keys);
        Assert.DoesNotContain("OPENCODE_CONFIG_CONTENT", environment.Keys);
        Assert.DoesNotContain("OPENCODE_SERVER_PASSWORD", environment.Keys);
        Assert.DoesNotContain("XDG_CONFIG_HOME", environment.Keys);
        Assert.DoesNotContain("XDG_DATA_HOME", environment.Keys);
        Assert.DoesNotContain("Control__Enabled", environment.Keys);
        Assert.DoesNotContain("Control__DataDirectory", environment.Keys);
        Assert.DoesNotContain("CONTROL__SECRET", environment.Keys);
    }

    [Fact]
    public void ExplicitCliProxyOverrideSurvivesWhileInheritedWorkstationKeyIsStripped()
    {
        var environment = ChildEnvironment.Build(
            new Dictionary<string, string> { ["CLIPROXY_API_KEY"] = "disposable-system-key" },
            new Dictionary<string, string?> { ["CLIPROXY_API_KEY"] = "workstation-key" });

        Assert.Equal("disposable-system-key", environment["CLIPROXY_API_KEY"]);
        Assert.DoesNotContain("workstation-key", environment.Values);
    }

    /// <summary>
    /// The tmux attach client only needs the native server credentials, never the
    /// inference key. It therefore omits CLIPROXY_API_KEY from its overrides, and
    /// the shared filter strips any inherited value even though the privileged
    /// launcher could forward it. The terminal child must not be able to read the
    /// managed employee's proxy credential.
    /// </summary>
    [Fact]
    public void TerminalCallerEnvironmentNeverCarriesTheInferenceKey()
    {
        // Exactly the override shape TmuxAttachLauncher builds: no proxy key.
        var overrides = new Dictionary<string, string>
        {
            ["HOME"] = "/data/home",
            ["XDG_DATA_HOME"] = "/data/home/data",
            ["XDG_CONFIG_HOME"] = "/data/home/config",
            ["AGENTCONTROL_OWNER"] = "owner-token",
            ["OPENCODE_SERVER_USERNAME"] = "opencode",
            ["OPENCODE_SERVER_PASSWORD"] = "server-password",
        };

        var environment = ChildEnvironment.Build(
            overrides,
            new Dictionary<string, string?> { ["CLIPROXY_API_KEY"] = "workstation-key", ["PATH"] = "/usr/bin" });

        Assert.DoesNotContain(CliProxyModelCatalog.ApiKeyEnvironmentVariable, environment.Keys);
        Assert.DoesNotContain("workstation-key", environment.Values);
    }

    [Fact]
    public void OverridesWinAndRestoreServerCredentials()
    {
        var baseline = new Dictionary<string, string?>
        {
            ["OPENCODE_SERVER_PASSWORD"] = "stale",
            ["OPENCODE_SERVER_USERNAME"] = "stale",
        };

        var environment = ChildEnvironment.Build(
            new Dictionary<string, string>
            {
                ["HOME"] = "/data/home",
                ["OPENCODE_SERVER_USERNAME"] = "opencode",
                ["OPENCODE_SERVER_PASSWORD"] = "fresh",
            },
            baseline);

        Assert.Equal("/data/home", environment["HOME"]);
        Assert.Equal("opencode", environment["OPENCODE_SERVER_USERNAME"]);
        Assert.Equal("fresh", environment["OPENCODE_SERVER_PASSWORD"]);
        Assert.DoesNotContain("stale", environment.Values);
    }

    [Theory]
    [InlineData("GH_TOKEN")]
    [InlineData("github_token")]
    [InlineData("GH_PAT")]
    [InlineData("GIT_ASKPASS")]
    [InlineData("SSH_AUTH_SOCK")]
    [InlineData("FLEET_HOME")]
    [InlineData("CLIPROXY_API_KEY")]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("GEMINI_API_KEY")]
    [InlineData("AWS_SESSION_TOKEN")]
    [InlineData("AZURE_CLIENT_SECRET")]
    [InlineData("GOOGLE_APPLICATION_CREDENTIALS")]
    [InlineData("NPM_TOKEN")]
    [InlineData("NODE_AUTH_TOKEN")]
    [InlineData("DOCKER_AUTH_CONFIG")]
    [InlineData("VAULT_TOKEN")]
    [InlineData("KUBECONFIG")]
    [InlineData("XDG_CONFIG_HOME")]
    [InlineData("OPENCODE_SERVER_PASSWORD")]
    [InlineData("OPENCODE_CONFIG_CONTENT")]
    [InlineData("Control__Enabled")]
    [InlineData("CONTROL__Enabled")]
    public void IsDeniedIsCaseInsensitive(string key)
    {
        Assert.True(ChildEnvironment.IsDenied(key));
    }

    [Theory]
    [InlineData("PATH")]
    [InlineData("HOME")]
    [InlineData("TERM")]
    [InlineData("LANG")]
    [InlineData("TZ")]
    public void BenignVariablesAreKept(string key)
    {
        Assert.False(ChildEnvironment.IsDenied(key));
    }
}
