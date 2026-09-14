using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// The minimal organization overview API: owner-protected, same-origin for
/// mutations, ProblemDetails errors, and optimistic revision required. The
/// runtime stays disabled so no OpenCode process is started; a disabled runtime
/// has no open store and reports 503.
/// </summary>
public sealed class OrganizationApiTests
{
    [Fact]
    public async Task GetOrganizationOnDisabledRuntimeIs503ProblemDetails()
    {
        using var factory = new DisabledRuntimeFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/organization");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(503, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("/api/organization", problem.RootElement.GetProperty("instance").GetString());
    }

    [Fact]
    public async Task OrganizationReadAndMutationAreProtectedByOwnerAuth()
    {
        using var factory = new OwnerAuthFactory();
        using var client = factory.CreateClient();

        using var read = await client.GetAsync("/api/organization");
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);

        using var mutation = new HttpRequestMessage(HttpMethod.Patch, "/api/organization")
        {
            Content = JsonContent("{\"organizationId\":\"org-x\",\"displayName\":\"N\",\"revision\":1}"),
        };
        mutation.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        using var denied = await client.SendAsync(mutation);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Fact]
    public async Task OrganizationMutationRejectsCrossOrigin()
    {
        using var factory = new OwnerAuthFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/organization")
        {
            Content = JsonContent("{\"organizationId\":\"org-x\",\"displayName\":\"Renamed\",\"revision\":1}"),
        };
        request.Headers.Add("Origin", "https://other.example");
        request.Headers.Authorization = Basic("owner", OwnerAuthFactory.OwnerPassword);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("{\"organizationId\":\"\",\"displayName\":\"N\",\"revision\":1}")]
    [InlineData("{\"organizationId\":\"org-x\",\"displayName\":\"N\"}")]
    [InlineData("{\"organizationId\":\"org-x\",\"displayName\":\"N\",\"revision\":0}")]
    public async Task OrganizationMutationRequiresAStableIdAndRevision(string body)
    {
        using var factory = new OwnerAuthFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/organization")
        {
            Content = JsonContent(body),
        };
        request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        request.Headers.Authorization = Basic("owner", OwnerAuthFactory.OwnerPassword);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static StringContent JsonContent(string body) =>
        new(body, Encoding.UTF8, "application/json");

    private static AuthenticationHeaderValue Basic(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
}
