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
        Assert.Equal("ask", permission.GetProperty("bash").GetProperty("*").GetString());
        Assert.Equal("ask", permission.GetProperty("read").GetProperty("*").GetString());
        Assert.Equal("ask", permission.GetProperty("edit").GetProperty("*").GetString());
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
        Assert.False(agent.TryGetProperty("variant", out _));
        Assert.False(root.TryGetProperty("provider", out _));
        Assert.Equal(new[] { "opencode" }, root.GetProperty("enabled_providers").EnumerateArray().Select(x => x.GetString()));

        // OpenCode applies the last matching permission rule, so the broad ask
        // must serialize before the explicit denies.
        Assert.True(
            json.IndexOf("\"*\": \"ask\"", StringComparison.Ordinal)
            < json.IndexOf("\"*sudo *\"", StringComparison.Ordinal));
    }

    [Fact]
    public void CliProxyConfigIsCuratedVersionedAndContainsNoSecret()
    {
        using var root = new TemporaryDirectory();
        var secretPath = Path.Combine(root.Path, "cliproxy.key");
        const string disposable = "disposable-test-key-243";
        File.WriteAllText(secretPath, disposable);
        var options = new ControlOptions
        {
            Model = "cliproxy/gpt-6-astra",
            ModelVariant = "medium",
            CliProxyEndpoint = "http://127.0.0.1:8317/v1",
            CliProxySecretFile = secretPath,
        };

        var cliProxy = CliProxyRuntimeConfiguration.LoadRequired(options);
        var json = AgentControlOpenCodeConfig.Build(options.Model, "/agent-config/instructions.md", cliProxy);
        using var document = JsonDocument.Parse(json);
        var rootJson = document.RootElement;

        Assert.Equal(new[] { "cliproxy" }, rootJson.GetProperty("enabled_providers").EnumerateArray().Select(x => x.GetString()));
        var provider = rootJson.GetProperty("provider").GetProperty("cliproxy");

        // Only the deterministic selectable subset is exposed; the proxy's
        // internal fallback source aliases and the image model are not.
        Assert.Equal(
            CliProxyProfile.SelectableLanes.Select(lane => lane.Id),
            provider.GetProperty("models").EnumerateObject().Select(model => model.Name));
        Assert.Equal(CliProxyModelCatalog.Models.Count - CliProxyProfile.NonExposedSourceAliases.Count,
            provider.GetProperty("models").EnumerateObject().Count());
        Assert.False(provider.GetProperty("models").TryGetProperty("auto", out _));
        Assert.False(provider.GetProperty("models").TryGetProperty("gpt-image-2.5-sunburst", out _));
        foreach (var hidden in CliProxyProfile.NonExposedSourceAliases)
        {
            Assert.False(provider.GetProperty("models").TryGetProperty(hidden, out _));
        }

        Assert.Equal("medium", rootJson.GetProperty("agent").GetProperty(AgentControlOpenCodeConfig.RoleName).GetProperty("variant").GetString());
        Assert.Equal(
            "medium",
            provider.GetProperty("models").GetProperty("gpt-6-astra")
                .GetProperty("options").GetProperty("reasoningEffort").GetString());
        Assert.Contains("{env:CLIPROXY_API_KEY}", json, StringComparison.Ordinal);
        Assert.DoesNotContain(disposable, json, StringComparison.Ordinal);
        // The direct config carries no plugin/MCP surface or workstation sync.
        Assert.False(rootJson.TryGetProperty("plugin", out _));
        Assert.False(rootJson.TryGetProperty("mcp", out _));
        Assert.Equal("cliproxy-phase1-2026-09-14-v1", CliProxyModelCatalog.Version);
    }

    /// <summary>
    /// Every advertised variant must state its own reasoning effort. An empty
    /// option object would let OpenCode send a variant request with no effort and
    /// silently fall back to provider defaults, so every entry is inspected.
    /// </summary>
    [Fact]
    public void EveryAdvertisedCliProxyVariantCarriesItsReasoningEffort()
    {
        using var root = new TemporaryDirectory();
        var secretPath = Path.Combine(root.Path, "cliproxy.key");
        File.WriteAllText(secretPath, "disposable-test-key-243");
        var options = new ControlOptions
        {
            Model = "cliproxy/default",
            ModelVariant = "medium",
            CliProxyEndpoint = "http://127.0.0.1:8317/v1",
            CliProxySecretFile = secretPath,
        };

        var cliProxy = CliProxyRuntimeConfiguration.LoadRequired(options);
        var json = AgentControlOpenCodeConfig.Build(options.Model, "/agent-config/instructions.md", cliProxy);
        using var document = JsonDocument.Parse(json);
        var models = document.RootElement.GetProperty("provider").GetProperty("cliproxy").GetProperty("models");

        var inspected = 0;
        foreach (var model in models.EnumerateObject())
        {
            var lane = CliProxyModelCatalog.Find(model.Name)
                ?? throw new Xunit.Sdk.XunitException($"Generated an unknown policy lane '{model.Name}'.");
            var variants = model.Value.GetProperty("variants");
            Assert.Equal(lane.AllowedVariants, variants.EnumerateObject().Select(variant => variant.Name));
            Assert.Equal(lane.AllowedVariants.Count > 0, model.Value.GetProperty("reasoning").GetBoolean());
            foreach (var variant in variants.EnumerateObject())
            {
                Assert.Equal(
                    variant.Name,
                    variant.Value.GetProperty("reasoningEffort").GetString());
                inspected++;
            }

            var task = CliProxyProfile.TaskClasses.SingleOrDefault(candidate => candidate.LaneId == lane.Id);
            if (task is not null)
            {
                Assert.Equal(
                    task.Variant,
                    model.Value.GetProperty("options").GetProperty("reasoningEffort").GetString());
            }
        }

        Assert.True(inspected > 0, "no advertised variants were inspected");
    }

    /// <summary>
    /// The profile generates one bounded read-only agent per deterministic task
    /// class and never generates a review agent: independent review selection is
    /// explicit, so no implicit agent can satisfy it.
    /// </summary>
    [Fact]
    public void CliProxyProfileGeneratesTaskAgentsButNoImplicitReviewAgent()
    {
        using var root = new TemporaryDirectory();
        var secretPath = Path.Combine(root.Path, "cliproxy.key");
        File.WriteAllText(secretPath, "disposable-test-key-243");
        var options = new ControlOptions
        {
            Model = "cliproxy/default",
            ModelVariant = "medium",
            CliProxyEndpoint = "http://127.0.0.1:8317/v1",
            CliProxySecretFile = secretPath,
        };

        var cliProxy = CliProxyRuntimeConfiguration.LoadRequired(options);
        var json = AgentControlOpenCodeConfig.Build(options.Model, "/agent-config/instructions.md", cliProxy);
        using var document = JsonDocument.Parse(json);
        var agents = document.RootElement.GetProperty("agent");

        foreach (var task in CliProxyProfile.TaskClasses)
        {
            var agent = agents.GetProperty(task.AgentName);
            Assert.Equal("all", agent.GetProperty("mode").GetString());
            Assert.Equal($"cliproxy/{task.LaneId}", agent.GetProperty("model").GetString());
            Assert.Equal(task.Variant, agent.GetProperty("variant").GetString());
            Assert.Equal(
                task.Variant,
                agent.GetProperty("options").GetProperty("reasoningEffort").GetString());
            Assert.Equal(task.MaxSteps, agent.GetProperty("steps").GetInt32());
            Assert.Equal(task.MaxSteps, agent.GetProperty("maxSteps").GetInt32());
            Assert.True(task.ProcessTimeout > TimeSpan.Zero);
            var permission = agent.GetProperty("permission");
            Assert.Equal("deny", permission.GetProperty("edit").GetString());
            Assert.Equal("deny", permission.GetProperty("bash").GetString());
            Assert.Equal("deny", permission.GetProperty("webfetch").GetString());
            Assert.Equal("allow", permission.GetProperty("read").GetProperty("*").GetString());
        }

        foreach (var forbidden in new[] { "review", "reviewer", "default", "auto" })
        {
            Assert.False(agents.TryGetProperty(forbidden, out _), $"unexpected implicit agent '{forbidden}'");
        }
    }

    [Fact]
    public void FullCatalogRetainsEveryPolicyLaneWhileTheProfileExposesASubset()
    {
        // The committed catalog keeps all 18 sanitized lanes for lookup and docs.
        Assert.Equal(18, CliProxyModelCatalog.Models.Count);

        // The generated profile is a strict subset that excludes the fallback
        // internals and never renames a lane.
        Assert.Equal(
            CliProxyProfile.SelectableLanes.Select(lane => lane.Id),
            CliProxyProfile.SelectableLanes.Select(lane => lane.Id).Distinct(StringComparer.Ordinal));
        foreach (var hidden in CliProxyProfile.NonExposedSourceAliases)
        {
            Assert.DoesNotContain(CliProxyProfile.SelectableLanes, lane => lane.Id == hidden);
        }

        // The review pool is explicit and never includes the non-attributable
        // default or the anonymous Big Pickle lane.
        Assert.DoesNotContain(CliProxyProfile.ReviewLanes, lane => lane.Id == "default");
        Assert.DoesNotContain(CliProxyProfile.ReviewLanes, lane => lane.Id == "big-pickle");
        Assert.All(CliProxyProfile.ReviewLanes, lane => Assert.Contains(lane, CliProxyProfile.SelectableLanes));

        // Every task class names an exposed lane and an advertised variant.
        Assert.NotEmpty(CliProxyProfile.TaskClasses);
        foreach (var task in CliProxyProfile.TaskClasses)
        {
            var lane = Assert.Single(CliProxyProfile.SelectableLanes, candidate => candidate.Id == task.LaneId);
            Assert.Contains(task.Variant, lane.AllowedVariants);
        }
    }

    [Theory]
    [InlineData("http://127.0.0.1:8317")]
    [InlineData("http://127.0.0.1:8317/v1/extra")]
    [InlineData("http://127.0.0.1:8317/v1?tenant=x")]
    public void CliProxyEndpointMustBeTheExactV1Base(string endpoint)
    {
        using var root = new TemporaryDirectory();
        var secretPath = Path.Combine(root.Path, "cliproxy.key");
        File.WriteAllText(secretPath, "disposable-test-key-243");
        var options = new ControlOptions
        {
            Model = "cliproxy/default",
            ModelVariant = "medium",
            CliProxyEndpoint = endpoint,
            CliProxySecretFile = secretPath,
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CliProxyRuntimeConfiguration.LoadRequired(options));
        Assert.Contains("exactly /v1", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(NonSelectableCliProxyLanes))]
    public void CliProxySelectionRejectsEveryNonExposedSourceAlias(string laneId)
    {
        using var root = new TemporaryDirectory();
        var secretPath = Path.Combine(root.Path, "cliproxy.key");
        File.WriteAllText(secretPath, "disposable-test-key-243");
        var options = new ControlOptions
        {
            Model = $"cliproxy/{laneId}",
            ModelVariant = "medium",
            CliProxyEndpoint = "http://127.0.0.1:8317/v1",
            CliProxySecretFile = secretPath,
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CliProxyRuntimeConfiguration.LoadRequired(options));
        Assert.Contains("not selectable", exception.Message, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> NonSelectableCliProxyLanes() =>
        CliProxyProfile.NonExposedSourceAliases.Select(lane => new object[] { lane });

    [Fact]
    public void CliProxySelectionFailsClosedWithoutValidSecretAndNeverFallsBack()
    {
        var options = new ControlOptions
        {
            Model = "cliproxy/gpt-5.6-sol",
            ModelVariant = "medium",
            CliProxyEndpoint = "http://127.0.0.1:8317/v1",
            CliProxySecretFile = "/definitely/missing/agentcontrol-cliproxy-key",
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CliProxyRuntimeConfiguration.LoadRequired(options));
        Assert.Contains("secret file", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("cliproxy/gpt-5.6-sol", options.Model);
        var errors = options.Validate();
        Assert.DoesNotContain(errors, error => error.Contains("big-pickle", StringComparison.OrdinalIgnoreCase));
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

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agentcontrol-cliproxy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
