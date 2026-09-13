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
        using var response = await _client.GetAsync("/");
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, body.RootElement.GetProperty("generation").GetInt32());
        Assert.Equal("baseline", body.RootElement.GetProperty("status").GetString());
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
        Assert.Equal("2.0.0-alpha.1", body.RootElement.GetProperty("version").GetString());
        Assert.Equal("ACP", body.RootElement.GetProperty("controlProtocol").GetString());
        Assert.Equal("OpenCode", body.RootElement.GetProperty("agentHarness").GetString());
        Assert.Equal("Docker", body.RootElement.GetProperty("executionEnvironment").GetString());
    }
}
