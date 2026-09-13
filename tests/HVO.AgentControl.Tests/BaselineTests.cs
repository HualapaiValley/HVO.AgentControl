using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class BaselineTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public BaselineTests(WebApplicationFactory<Program> application)
    {
        _client = application.CreateClient();
    }

    [Fact]
    public async Task RootReportsBaselineRatherThanClaimingWorkerReadiness()
    {
        using var response = await _client.GetAsync("/api/info");
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, body.RootElement.GetProperty("generation").GetInt32());
        Assert.Equal("control-portal", body.RootElement.GetProperty("status").GetString());
        Assert.False(body.RootElement.GetProperty("workerControlImplemented").GetBoolean());
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
        using var terminal = await _client.GetAsync("/terminal");
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
}
