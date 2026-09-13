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
            ["GITHUB_TOKEN"] = "gh-secret",
            ["GITHUB_APP_PRIVATE_KEY"] = "key",
            ["FLEET_HOME"] = "/home/roys/fleet",
            ["FLEET_MEMBER"] = "ada",
            ["CLIPROXY_BASE_URL"] = "http://proxy",
            ["HERDR_SOCKET_PATH"] = "/run/herdr.sock",
            ["OPENCODE"] = "1",
            ["OPENCODE_PID"] = "42",
            ["OPENCODE_CONFIG"] = "/home/roys/fleet/agent.json",
            ["Control__Enabled"] = "true",
            ["Control__DataDirectory"] = "/data",
            ["CONTROL__SECRET"] = "nope",
        };

        var environment = ChildEnvironment.Build(overrides: null, baseEnvironment: baseline);

        Assert.Equal("/usr/bin", environment["PATH"]);
        Assert.DoesNotContain("GH_TOKEN", environment.Keys);
        Assert.DoesNotContain("GITHUB_TOKEN", environment.Keys);
        Assert.DoesNotContain("GITHUB_APP_PRIVATE_KEY", environment.Keys);
        Assert.DoesNotContain("FLEET_HOME", environment.Keys);
        Assert.DoesNotContain("FLEET_MEMBER", environment.Keys);
        Assert.DoesNotContain("CLIPROXY_BASE_URL", environment.Keys);
        Assert.DoesNotContain("HERDR_SOCKET_PATH", environment.Keys);
        Assert.DoesNotContain("OPENCODE", environment.Keys);
        Assert.DoesNotContain("OPENCODE_PID", environment.Keys);
        Assert.DoesNotContain("OPENCODE_CONFIG", environment.Keys);
        Assert.DoesNotContain("Control__Enabled", environment.Keys);
        Assert.DoesNotContain("Control__DataDirectory", environment.Keys);
        Assert.DoesNotContain("CONTROL__SECRET", environment.Keys);
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
    [InlineData("FLEET_HOME")]
    [InlineData("OPENCODE_SERVER_PASSWORD")]
    [InlineData("OPENCODE_CONFIG_CONTENT")]
    [InlineData("Control__Enabled")]
    [InlineData("CONTROL__Enabled")]
    public void IsDeniedIsCaseInsensitive(string key)
    {
        Assert.True(ChildEnvironment.IsDenied(key));
    }
}
