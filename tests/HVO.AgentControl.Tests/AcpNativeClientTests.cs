using System.Net;
using System.Text;
using HVO.AgentControl.Runtime;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class AcpNativeClientTests
{
    [Fact]
    public async Task StatusMapBusyReturnsBusyWithoutExactFetch()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/session/status" => Json("""{"ses_1":{"type":"busy"}}"""),
            _ => Json("{}"),
        });

        using var client = new OpenCodeNativeClient("http://127.0.0.1:4096", "opencode", "pw", handler, workspaceDirectory: "/data/workspace");

        Assert.Equal("busy", await client.GetSessionStateAsync("ses_1", CancellationToken.None));
        Assert.Single(handler.Requests);
        Assert.Equal("/session/status", handler.Requests[0]);
    }

    [Fact]
    public async Task SparseStatusConfirmsExactSessionAsIdle()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/session/status" => Json("{}"),
            "/session/ses_1" => Json("""{"id":"ses_1"}"""),
            _ => Json("{}"),
        });

        using var client = new OpenCodeNativeClient("http://127.0.0.1:4096", "opencode", "pw", handler, workspaceDirectory: "/data/workspace");

        Assert.Equal("idle", await client.GetSessionStateAsync("ses_1", CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/session/status", handler.Requests[0]);
        Assert.Contains("/session/ses_1", handler.Requests[1], StringComparison.Ordinal);
        Assert.Contains("directory=%2Fdata%2Fworkspace", handler.Requests[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task SparseStatusWithMismatchedIdReturnsNull()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/session/status" => Json("{}"),
            "/session/ses_1" => Json("""{"id":"ses_other"}"""),
            _ => Json("{}"),
        });

        using var client = new OpenCodeNativeClient("http://127.0.0.1:4096", "opencode", "pw", handler, workspaceDirectory: "/data/workspace");

        Assert.Null(await client.GetSessionStateAsync("ses_1", CancellationToken.None));
    }

    [Fact]
    public async Task MissingExactSessionReturnsNull()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/session/status" => Json("{}"),
            "/session/ses_1" => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ => Json("{}"),
        });

        using var client = new OpenCodeNativeClient("http://127.0.0.1:4096", "opencode", "pw", handler, workspaceDirectory: "/data/workspace");

        Assert.Null(await client.GetSessionStateAsync("ses_1", CancellationToken.None));
    }

    [Fact]
    public async Task NoWorkspaceOmitsDirectoryQuery()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/session/status" => Json("{}"),
            "/session/ses_1" => Json("""{"id":"ses_1"}"""),
            _ => Json("{}"),
        });

        using var client = new OpenCodeNativeClient("http://127.0.0.1:4096", "opencode", "pw", handler);

        Assert.Equal("idle", await client.GetSessionStateAsync("ses_1", CancellationToken.None));
        Assert.DoesNotContain("directory=", handler.Requests[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionModelParsesProviderModelAndVariant()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/session/ses_1" => Json("""{"id":"ses_1","model":{"id":"big-pickle","providerID":"opencode","variant":"default"}}"""),
            _ => Json("{}"),
        });

        using var client = new OpenCodeNativeClient("http://127.0.0.1:4096", "opencode", "pw", handler, workspaceDirectory: "/data/workspace");

        var snapshot = await client.GetSessionModelAsync("ses_1", CancellationToken.None);

        Assert.True(snapshot.Available);
        Assert.NotNull(snapshot.Model);
        Assert.Equal("opencode/big-pickle", snapshot.Model!.Reference);
        Assert.Equal("default", snapshot.Model.Variant);
        Assert.Contains("directory=%2Fdata%2Fworkspace", handler.Requests[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionModelPresentButAbsentIsAvailableAndUnknown()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/session/ses_1" => Json("""{"id":"ses_1"}"""),
            _ => Json("{}"),
        });

        using var client = new OpenCodeNativeClient("http://127.0.0.1:4096", "opencode", "pw", handler);

        var snapshot = await client.GetSessionModelAsync("ses_1", CancellationToken.None);

        Assert.True(snapshot.Available);
        Assert.Null(snapshot.Model);
    }

    [Fact]
    public async Task SessionModelFailureIsUnavailable()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/session/ses_1" => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ => Json("{}"),
        });

        using var client = new OpenCodeNativeClient("http://127.0.0.1:4096", "opencode", "pw", handler);

        var snapshot = await client.GetSessionModelAsync("ses_1", CancellationToken.None);

        Assert.False(snapshot.Available);
        Assert.Null(snapshot.Model);
    }

    [Fact]
    public async Task ProviderCatalogIncludesConnectedProvidersOnly()
    {
        const string providerJson = """
            {"connected":["opencode"],"all":[
              {"id":"opencode","name":"OpenCode","models":{
                 "big-pickle":{"id":"big-pickle","name":"Big Pickle","providerID":"opencode"},
                 "mimo":{"id":"mimo","name":"MiMo","providerID":"opencode"}}},
              {"id":"other","name":"Other","models":{
                 "x":{"id":"x","name":"X","providerID":"other"}}}]}
            """;
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/provider" => Json(providerJson),
            _ => Json("{}"),
        });

        using var client = new OpenCodeNativeClient("http://127.0.0.1:4096", "opencode", "pw", handler);

        var catalog = await client.GetConnectedModelCatalogAsync(CancellationToken.None);

        Assert.NotNull(catalog);
        Assert.Equal(2, catalog!.Count);
        Assert.Contains(catalog, model => model.Id == "opencode/big-pickle" && model.Provider == "opencode" && model.Name == "Big Pickle");
        Assert.Contains(catalog, model => model.Id == "opencode/mimo");
        Assert.DoesNotContain(catalog, model => model.Provider == "other");
    }

    [Fact]
    public async Task ProviderCatalogFailureReturnsNull()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/provider" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            _ => Json("{}"),
        });

        using var client = new OpenCodeNativeClient("http://127.0.0.1:4096", "opencode", "pw", handler);

        Assert.Null(await client.GetConnectedModelCatalogAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SetSessionModelPostsNativeModelRef()
    {
        string? body = null;
        var handler = new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/session/ses_1/model")
            {
                body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return Json("{}");
        });

        using var client = new OpenCodeNativeClient("http://127.0.0.1:4096", "opencode", "pw", handler);

        var written = await client.TrySetSessionModelAsync(
            "ses_1",
            new NativeModelReference("opencode", "big-pickle", Variant: null),
            CancellationToken.None);

        Assert.True(written);
        Assert.NotNull(body);
        using var document = System.Text.Json.JsonDocument.Parse(body!);
        var model = document.RootElement.GetProperty("model");
        Assert.Equal("big-pickle", model.GetProperty("id").GetString());
        Assert.Equal("opencode", model.GetProperty("providerID").GetString());
        Assert.False(model.TryGetProperty("variant", out _));
    }

    [Fact]
    public async Task SetSessionModelFailureReturnsFalse()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/session/ses_1/model" => new HttpResponseMessage(HttpStatusCode.BadRequest),
            _ => Json("{}"),
        });

        using var client = new OpenCodeNativeClient("http://127.0.0.1:4096", "opencode", "pw", handler);

        Assert.False(await client.TrySetSessionModelAsync(
            "ses_1",
            new NativeModelReference("opencode", "missing", Variant: null),
            CancellationToken.None));
    }

    private static HttpResponseMessage Json(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(_responder(request));
        }
    }
}
