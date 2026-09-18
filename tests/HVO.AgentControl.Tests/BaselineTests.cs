using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class BaselineTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string AcceptanceDate = "2026-09-18";
    private const string AcceptancePhrase = "first managed disposable two-host path";
    private const string KeyRotationExclusion = "does not validate key rotation or compromise re-enrollment";
    private const string ProductionHiringExclusion = "does not authorize production managed hiring/provisioning";

    private readonly HttpClient _client;

    public BaselineTests(WebApplicationFactory<Program> application)
    {
        _client = application.CreateClient();
    }

    [Fact]
    public async Task RootReportsBaselineWorkerCapabilityFlagsExactly()
    {
        using var response = await _client.GetAsync("/api/info");
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, body.RootElement.GetProperty("generation").GetInt32());
        Assert.Equal("control-portal", body.RootElement.GetProperty("status").GetString());
        // #217 operational acceptance: the first managed disposable two-host path
        // is implemented and validated. Enabled stays the separate config gate and
        // is false by default, so it must not be asserted true here.
        Assert.True(body.RootElement.GetProperty("workerControlImplemented").GetBoolean());
        Assert.True(body.RootElement.GetProperty("workerControlCodeAvailable").GetBoolean());
        Assert.True(body.RootElement.GetProperty("workerControlOperationallyValidated").GetBoolean());
        Assert.False(body.RootElement.GetProperty("workerControlEnabled").GetBoolean());
        // The in-band scope bound is exact and stable so clients can distinguish the
        // accepted path from key rotation and production provisioning.
        Assert.Equal(
            Program.WorkerControlValidatedScope,
            body.RootElement.GetProperty("workerControlValidatedScope").GetString());
        Assert.Equal("first-managed-disposable-two-host", Program.WorkerControlValidatedScope);
    }

    [Fact]
    public async Task LivenessReturnsHealthy()
    {
        using var response = await _client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("healthy", body.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task VersionDescribesV2Direction()
    {
        using var response = await _client.GetAsync("/api/version");
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("0.1.0", ApplicationVersion.Current);
        Assert.Equal(ApplicationVersion.Current, body.RootElement.GetProperty("version").GetString());
        Assert.Equal("ACP", body.RootElement.GetProperty("controlProtocol").GetString());
        Assert.Equal("OpenCode", body.RootElement.GetProperty("agentHarness").GetString());
        Assert.Equal("Docker", body.RootElement.GetProperty("executionEnvironment").GetString());
    }

    [Fact]
    public async Task PortalRendersAndDisabledRuntimeCannotOpenTerminal()
    {
        var page = await _client.GetStringAsync("/");
        Assert.Contains("AgentControl V2", page);
        using var status = JsonDocument.Parse(await _client.GetStringAsync("/api/control"));
        Assert.Equal("disabled", status.RootElement.GetProperty("state").GetString());
        Assert.False(status.RootElement.GetProperty("modelSyncSupported").GetBoolean());
        using var malformedTerminal = await _client.GetAsync("/terminal");
        Assert.Equal(HttpStatusCode.BadRequest, malformedTerminal.StatusCode);
        using var terminal = await _client.GetAsync("/terminal?employeeId=emp-disabled");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, terminal.StatusCode);
    }

    [Fact]
    public async Task CancelRejectsCrossOriginAndDisabledRuntime()
    {
        using var invalid = new HttpRequestMessage(HttpMethod.Post, "/api/control/cancel");
        invalid.Headers.Add("Origin", "https://other.example");
        using var denied = await _client.SendAsync(invalid);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var valid = new HttpRequestMessage(HttpMethod.Post, "/api/control/cancel");
        valid.Headers.Add("Origin", _client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        using var unavailable = await _client.SendAsync(valid);
        Assert.Equal(HttpStatusCode.Conflict, unavailable.StatusCode);
    }

    [Theory]
    [InlineData("https://other.example", HttpStatusCode.Forbidden)]
    [InlineData("http://localhost", HttpStatusCode.Conflict)]
    public async Task ModelSelectionRequiresSameOriginAndReadyRuntime(string origin, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/control/model");
        request.Headers.Add("Origin", origin);
        request.Content = new StringContent("{\"model\":\"opencode/big-pickle\"}", System.Text.Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public void LivePortalSmokeTargetsCurrentEmployeeDetailSelectors()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "tests", "Browser", "portal-smoke.mjs"));

        Assert.Contains("/api/organization/portal", script, StringComparison.Ordinal);
        Assert.Contains("/employees/${encodeURIComponent(hostEmployee.id)}", script, StringComparison.Ordinal);
        Assert.Contains("[data-field=\"state-detail\"]", script, StringComparison.Ordinal);
        Assert.DoesNotContain("fieldText(page, 'state')", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptanceDocsBoundTheAcceptedPathWithExclusions()
    {
        // Capability truth: wherever the 2026-09-18 first managed disposable
        // two-host path is marked accepted, the same document must also state the
        // key rotation and production hiring/provisioning exclusions so the
        // accepted path is never read as broader than it is.
        var root = FindRepositoryRoot();
        var docs = new[]
        {
            Path.Combine(root, "README.md"),
            Path.Combine(root, "docs", "ARCHITECTURE.md"),
            Path.Combine(root, "docs", "ROADMAP.md"),
        };

        foreach (var path in docs)
        {
            var text = File.ReadAllText(path);
            Assert.Contains("2026-09-18", text, StringComparison.Ordinal);
            Assert.Contains(AcceptancePhrase, text, StringComparison.Ordinal);
            Assert.Contains(KeyRotationExclusion, text, StringComparison.Ordinal);
            Assert.Contains(ProductionHiringExclusion, text, StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HVO.AgentControl.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
