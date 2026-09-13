using System.Text.Json;
using HVO.AgentControl.Runtime;
using HVO.AgentControl.Terminal;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class AcpOptionsConfigTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ShutdownRequiresTimeForBoundedCleanup(int seconds)
    {
        Assert.Contains(new ControlOptions { ShutdownGraceSeconds = seconds }.Validate(),
            error => error.Contains(nameof(ControlOptions.ShutdownGraceSeconds), StringComparison.Ordinal));
        Assert.Empty(new ControlOptions { ShutdownGraceSeconds = 1 }.Validate());
    }

    [Theory]
    [InlineData("127.0.0.1", "http://127.0.0.1:4096")]
    [InlineData("::1", "http://[::1]:4096")]
    public void NativeUrlSupportsValidatedIpLiterals(string hostname, string expected)
    {
        using var host = new AcpControlHost(
            Microsoft.Extensions.Options.Options.Create(new ControlOptions { Hostname = hostname }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AcpControlHost>.Instance);
        Assert.Equal(expected, host.NativeUrl);
    }

    [Fact]
    public void ControlOptionsDefaultsMatchV2Contract()
    {
        var options = new ControlOptions();

        Assert.False(options.Enabled);
        Assert.Equal("/data", options.DataDirectory);
        Assert.Equal("AgentControl Development", options.OrganizationName);
        Assert.Equal("opencode", options.OpenCodeExecutable);
        Assert.Equal(4096, options.NativePort);
        Assert.Equal("opencode/big-pickle", options.Model);
    }

    [Fact]
    public void ValidateReportsInvalidValues()
    {
        var options = new ControlOptions { NativePort = 0, OrganizationName = " " };

        var errors = options.Validate();

        Assert.Contains(errors, error => error.Contains("NativePort", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("OrganizationName", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateAcceptsDefaultOptions()
    {
        Assert.Empty(new ControlOptions().Validate());
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.53")]
    [InlineData("127.255.255.254")]
    [InlineData("::1")]
    [InlineData("0:0:0:0:0:0:0:1")]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    public void ValidateAcceptsLoopbackHostnames(string hostname)
    {
        var options = new ControlOptions { Hostname = hostname };

        Assert.DoesNotContain(
            options.Validate(),
            error => error.Contains(nameof(ControlOptions.Hostname), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.20")]
    [InlineData("172.16.0.1")]
    [InlineData("8.8.8.8")]
    [InlineData("255.255.255.255")]
    [InlineData("example.com")]
    [InlineData("localhost.example.com")]
    [InlineData("localhost.")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::1")]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateRejectsNonLoopbackHostnames(string hostname)
    {
        var options = new ControlOptions { Hostname = hostname };

        Assert.Contains(
            options.Validate(),
            error => error.Contains(nameof(ControlOptions.Hostname), StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateUsesSharedTerminalSessionNameRule()
    {
        // The options gate and the terminal attach gate must agree exactly;
        // otherwise a name accepted here can be rejected (or reinterpreted) by tmux.
        string[] names =
        [
            "agentcontrol",
            "agent-control_2",
            "ABC123",
            "agent control",
            "agentcontrol:1",
            "agentcontrol.0",
            "agent;rm -rf /",
            "session\nname",
            new string('a', TerminalProtocol.MaxSessionNameLength),
            new string('a', TerminalProtocol.MaxSessionNameLength + 1),
            string.Empty,
            "   ",
        ];

        foreach (var name in names)
        {
            var options = new ControlOptions { TmuxSessionName = name };
            var rejected = options.Validate()
                .Any(error => error.Contains(nameof(ControlOptions.TmuxSessionName), StringComparison.Ordinal));

            Assert.Equal(!TerminalProtocol.IsValidSessionName(name), rejected);
        }
    }

    [Fact]
    public void GeneratedConfigCarriesModelInstructionsRoleAndSafePermissions()
    {
        const string model = "opencode/big-pickle";
        const string instructions = "/data/home/agentcontrol-instructions.md";

        var json = AgentControlOpenCodeConfig.Build(model, instructions);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(model, root.GetProperty("model").GetString());
        Assert.Equal(instructions, root.GetProperty("instructions")[0].GetString());

        var permission = root.GetProperty("permission");
        Assert.Equal("allow", permission.GetProperty("bash").GetProperty("*").GetString());
        Assert.Equal("deny", permission.GetProperty("bash").GetProperty("*sudo *").GetString());
        Assert.Equal("deny", permission.GetProperty("read").GetProperty("**/.env").GetString());
        Assert.Equal("deny", permission.GetProperty("edit").GetProperty("**/.ssh/**").GetString());
        Assert.Equal("deny", permission.GetProperty("read").GetProperty("/run/agentcontrol-secrets/**").GetString());
        Assert.Equal("deny", permission.GetProperty("read").GetProperty("/data/runtime.json").GetString());
        Assert.Equal("deny", permission.GetProperty("edit").GetProperty("/data/runtime.json").GetString());
        Assert.Equal("deny", permission.GetProperty("bash").GetProperty("*agentcontrol-secrets*").GetString());
        Assert.Equal("deny", permission.GetProperty("bash").GetProperty("*runtime.json*").GetString());
        Assert.Equal("deny", permission.GetProperty("webfetch").GetString());
        Assert.Equal("deny", permission.GetProperty("fleet_send").GetString());
        Assert.Equal("deny", permission.GetProperty("fleet_member_prompt").GetString());

        var agent = root.GetProperty("agent").GetProperty(AgentControlOpenCodeConfig.RoleName);
        Assert.Equal("primary", agent.GetProperty("mode").GetString());

        // OpenCode applies the last matching permission rule, so the broad allow
        // must serialize before the explicit denies.
        Assert.True(
            json.IndexOf("\"*\": \"allow\"", StringComparison.Ordinal)
            < json.IndexOf("\"*sudo *\"", StringComparison.Ordinal));
    }

    [Fact]
    public void InstructionsDescribeConsolidatedRoleWithoutFleetOrCodeWork()
    {
        var instructions = AgentControlOpenCodeConfig.BuildInstructions("Contoso");

        Assert.Contains("Contoso", instructions, StringComparison.Ordinal);
        Assert.Contains("no Fleet", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not perform code work", instructions, StringComparison.Ordinal);
        Assert.Contains("readiness", instructions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ControlStatusSerializesWithLowercaseProperties()
    {
        var status = new ControlStatus
        {
            State = "ready",
            OrganizationName = "Contoso",
            Model = "opencode/big-pickle",
            Models = [new ControlModel { Id = "opencode/big-pickle", Name = "Big Pickle", Provider = "opencode" }],
            TerminalReady = true,
        };

        var json = JsonSerializer.Serialize(status);

        Assert.Contains("\"state\":\"ready\"", json, StringComparison.Ordinal);
        Assert.Contains("\"organizationName\":\"Contoso\"", json, StringComparison.Ordinal);
        Assert.Contains("\"model\":\"opencode/big-pickle\"", json, StringComparison.Ordinal);
        Assert.Contains("\"models\":[{\"id\":\"opencode/big-pickle\",\"name\":\"Big Pickle\",\"provider\":\"opencode\"}]", json, StringComparison.Ordinal);
        Assert.Contains("\"terminalReady\":true", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ControlStatusDefaultsModelToUnknownAndModelsToEmpty()
    {
        var status = new ControlStatus { State = "starting", OrganizationName = "Contoso" };

        var json = JsonSerializer.Serialize(status);

        Assert.Contains("\"model\":\"unknown\"", json, StringComparison.Ordinal);
        Assert.Contains("\"models\":[]", json, StringComparison.Ordinal);
    }
}
