using System.Text.Json;
using HVO.AgentControl.Runtime;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class AcpOptionsConfigTests
{
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
