using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Shared route data and assertion helpers for the remote-worker HTTP contract.
/// </summary>
public static class RemoteWorkerApi
{
    private const string PromptBody =
        "{\"employeeId\":\"emp-x\",\"runtimeBindingId\":\"binding-x\",\"workerId\":\"wrk-missing\","
        + "\"sessionRecordId\":\"ses-x\",\"nativeSessionId\":\"native-x\",\"idempotencyKey\":\"idem-x\","
        + "\"prompt\":\"hello\"}";

    /// <summary>Every state-changing remote-worker route with a syntactically valid body.</summary>
    public static TheoryData<string, string?> Mutations => new()
    {
        { "/api/execution-hosts", "{}" },
        { "/api/execution-hosts/host-a/probe", "{\"expectedRevision\":1}" },
        { "/api/execution-hosts/host-a/disable", "{\"expectedRevision\":1}" },
        { "/api/workers/enroll/plan", "{\"runtimeBindingId\":\"binding-x\",\"hostId\":\"host-a\"}" },
        { "/api/workers/wrk-missing/enroll/apply", null },
        { "/api/workers/wrk-missing/cleanup", null },
        { "/api/workers/request", PromptBody },
        { "/api/workers/request/req-missing/cancel", null },
        { "/api/workers/wrk-missing/permission/reject", "{\"decisionId\":\"perm-x\",\"revision\":1}" },
        { "/api/workers/wrk-missing/sync", null },
        { "/api/workers/wrk-missing/recover", "{\"kind\":\"replay-gap\",\"markerHash\":\"sha256:0000\"}" },
    };

    public static HttpRequestMessage Mutation(string path, string? body, string origin, string? password)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        request.Headers.Add("Origin", origin);
        if (password is not null)
        {
            request.Headers.Authorization = Basic("owner", password);
        }

        return request;
    }

    public static AuthenticationHeaderValue Basic(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

    /// <summary>
    /// Asserts the RFC 9457 ProblemDetails content type and the absence of any raw
    /// exception, stack trace or store detail before returning the parsed body.
    /// </summary>
    public static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Exception", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("stackTrace", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("control.db", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Sqlite", raw, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }
}

/// <summary>
/// With the control runtime disabled there is no open store, so every
/// remote-worker route fails closed with a sanitized 503 (mutations only after
/// the same-origin gate) and never leaks a raw exception.
/// </summary>
public sealed class RemoteWorkerApiDisabledRuntimeTests : IClassFixture<DisabledRuntimeFactory>
{
    private readonly DisabledRuntimeFactory _factory;

    public RemoteWorkerApiDisabledRuntimeTests(DisabledRuntimeFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/api/execution-hosts")]
    [InlineData("/api/workers/status")]
    [InlineData("/api/workers/wrk-missing/permissions")]
    public async Task ReadsOnDisabledControlAre503ProblemDetails(string path)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await RemoteWorkerApi.ProblemAsync(response);
        Assert.Equal(503, problem.GetProperty("status").GetInt32());
        Assert.Equal(path, problem.GetProperty("instance").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
    }

    [Theory]
    [MemberData(nameof(RemoteWorkerApi.Mutations), MemberType = typeof(RemoteWorkerApi))]
    public async Task MutationsOnDisabledControlAre503ProblemDetails(string path, string? body)
    {
        using var client = _factory.CreateClient();
        using var request = RemoteWorkerApi.Mutation(
            path, body, client.BaseAddress!.GetLeftPart(UriPartial.Authority), password: null);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await RemoteWorkerApi.ProblemAsync(response);
        Assert.Equal(503, problem.GetProperty("status").GetInt32());
        Assert.Equal(path, problem.GetProperty("instance").GetString());
    }

    [Theory]
    [MemberData(nameof(RemoteWorkerApi.Mutations), MemberType = typeof(RemoteWorkerApi))]
    public async Task CrossOriginMutationsAre403ProblemDetailsBeforeStore(string path, string? body)
    {
        using var client = _factory.CreateClient();
        using var request = RemoteWorkerApi.Mutation(path, body, "https://other.example", password: null);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await RemoteWorkerApi.ProblemAsync(response);
        Assert.Equal(403, problem.GetProperty("status").GetInt32());
        Assert.Equal(path, problem.GetProperty("instance").GetString());
    }
}

/// <summary>
/// Control is enabled and the store is open, but WorkerControl is disabled (the
/// default). Store reads still work; every execution path fails closed with 409
/// rather than a raw 500, and cross-origin is rejected first.
/// </summary>
public sealed class RemoteWorkerApiWorkerDisabledTests : IClassFixture<EnabledRuntimeFactory>
{
    private readonly EnabledRuntimeFactory _factory;

    public RemoteWorkerApiWorkerDisabledTests(EnabledRuntimeFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/api/execution-hosts")]
    [InlineData("/api/workers/status")]
    public async Task StoreReadsAreAvailableWhenWorkerControlDisabled(string path)
    {
        using var client = await AuthorizedClientAsync();
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UnknownWorkerPermissionsAre404ProblemDetails()
    {
        using var client = await AuthorizedClientAsync();
        using var response = await client.GetAsync("/api/workers/wrk-missing/permissions");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await RemoteWorkerApi.ProblemAsync(response);
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
    }

    [Theory]
    [MemberData(nameof(RemoteWorkerApi.Mutations), MemberType = typeof(RemoteWorkerApi))]
    public async Task MutationsAre409ProblemDetailsWhenWorkerControlDisabled(string path, string? body)
    {
        using var client = await AuthorizedClientAsync();
        using var request = RemoteWorkerApi.Mutation(
            path, body, client.BaseAddress!.GetLeftPart(UriPartial.Authority), EnabledRuntimeFactory.OwnerPassword);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await RemoteWorkerApi.ProblemAsync(response);
        Assert.Equal(409, problem.GetProperty("status").GetInt32());
        Assert.Equal(path, problem.GetProperty("instance").GetString());
    }

    [Theory]
    [MemberData(nameof(RemoteWorkerApi.Mutations), MemberType = typeof(RemoteWorkerApi))]
    public async Task CrossOriginMutationsAre403BeforeWorkerConfig(string path, string? body)
    {
        using var client = await AuthorizedClientAsync();
        using var request = RemoteWorkerApi.Mutation(path, body, "https://other.example", EnabledRuntimeFactory.OwnerPassword);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await RemoteWorkerApi.ProblemAsync(response);
        Assert.Equal(403, problem.GetProperty("status").GetInt32());
    }

    private async Task<HttpClient> AuthorizedClientAsync()
    {
        var client = _factory.CreateClient();
        await _factory.WaitForReadyAsync(TimeSpan.FromSeconds(45));
        client.DefaultRequestHeaders.Authorization =
            RemoteWorkerApi.Basic("owner", EnabledRuntimeFactory.OwnerPassword);
        return client;
    }
}

/// <summary>
/// Control is enabled and the store is open, and WorkerControl is enabled with a
/// deliberately invalid configuration (no controller id, image digest, or
/// approved hosts). Startup does not validate those options, so every execution
/// endpoint owns a truthful 409 and never leaks configuration detail.
/// </summary>
public sealed class RemoteWorkerApiWorkerInvalidConfigTests : IClassFixture<WorkerControlInvalidConfigFactory>
{
    private readonly WorkerControlInvalidConfigFactory _factory;

    public RemoteWorkerApiWorkerInvalidConfigTests(WorkerControlInvalidConfigFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/api/execution-hosts")]
    [InlineData("/api/workers/status")]
    public async Task StoreReadsAreAvailableWithInvalidWorkerConfig(string path)
    {
        using var client = await AuthorizedClientAsync();
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UnknownWorkerPermissionsAre404ProblemDetails()
    {
        using var client = await AuthorizedClientAsync();
        using var response = await client.GetAsync("/api/workers/wrk-missing/permissions");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await RemoteWorkerApi.ProblemAsync(response);
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task WorkerSurfaceRequiresOwnerAuth()
    {
        using var client = _factory.CreateClient();
        await _factory.WaitForReadyAsync(TimeSpan.FromSeconds(45));

        using var response = await client.GetAsync("/api/workers/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await RemoteWorkerApi.ProblemAsync(response);
        Assert.Equal(401, problem.GetProperty("status").GetInt32());
    }

    [Theory]
    [MemberData(nameof(RemoteWorkerApi.Mutations), MemberType = typeof(RemoteWorkerApi))]
    public async Task MutationsAre409ProblemDetailsWithInvalidWorkerConfig(string path, string? body)
    {
        using var client = await AuthorizedClientAsync();
        using var request = RemoteWorkerApi.Mutation(
            path, body, client.BaseAddress!.GetLeftPart(UriPartial.Authority), EnabledRuntimeFactory.OwnerPassword);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await RemoteWorkerApi.ProblemAsync(response);
        Assert.Equal(409, problem.GetProperty("status").GetInt32());
        Assert.Equal(path, problem.GetProperty("instance").GetString());
    }

    [Theory]
    [MemberData(nameof(RemoteWorkerApi.Mutations), MemberType = typeof(RemoteWorkerApi))]
    public async Task CrossOriginMutationsAre403BeforeInvalidConfig(string path, string? body)
    {
        using var client = await AuthorizedClientAsync();
        using var request = RemoteWorkerApi.Mutation(path, body, "https://other.example", EnabledRuntimeFactory.OwnerPassword);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await RemoteWorkerApi.ProblemAsync(response);
        Assert.Equal(403, problem.GetProperty("status").GetInt32());
    }

    private async Task<HttpClient> AuthorizedClientAsync()
    {
        var client = _factory.CreateClient();
        await _factory.WaitForReadyAsync(TimeSpan.FromSeconds(45));
        client.DefaultRequestHeaders.Authorization =
            RemoteWorkerApi.Basic("owner", EnabledRuntimeFactory.OwnerPassword);
        return client;
    }
}

/// <summary>
/// Owner authentication contract for a worker-only enabled host: the runtime
/// switch alone requires a configured owner password, and the remote-worker
/// surface is owner-protected.
/// </summary>
public sealed class RemoteWorkerOwnerAuthTests
{
    [Fact]
    public void WorkerOnlyEnabledWithoutPasswordFailsHostStartup()
    {
        using var factory = new WorkerOnlyEnabledMissingPasswordFactory();

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("Control:OwnerPasswordFile", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkerOnlyEnabledRequiresOwnerAuth()
    {
        using var password = new TempPasswordFile("worker-only-owner-password-000000");
        using var factory = new WorkerOnlyEnabledFactory(password.Path);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/workers/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await RemoteWorkerApi.ProblemAsync(response);
        Assert.Equal(401, problem.GetProperty("status").GetInt32());
    }

    private sealed class TempPasswordFile : IDisposable
    {
        public TempPasswordFile(string contents)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hvo-worker-only-{Guid.NewGuid():N}.txt");
            File.WriteAllText(Path, contents);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>Control enabled with the deliberately invalid WorkerControl defaults.</summary>
public sealed class WorkerControlInvalidConfigFactory : EnabledRuntimeFactory
{
    public WorkerControlInvalidConfigFactory()
        : base("prompt_fast", workerControlEnabled: true)
    {
    }
}

/// <summary>WorkerControl enabled without any owner password file.</summary>
public sealed class WorkerOnlyEnabledMissingPasswordFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Control:Enabled", "false");
        builder.UseSetting("WorkerControl:Enabled", "true");
    }
}

/// <summary>WorkerControl enabled with a disposable inert owner password file.</summary>
public sealed class WorkerOnlyEnabledFactory : WebApplicationFactory<Program>
{
    private readonly string _passwordPath;

    public WorkerOnlyEnabledFactory(string passwordPath)
    {
        _passwordPath = passwordPath;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Control:Enabled", "false");
        builder.UseSetting("WorkerControl:Enabled", "true");
        builder.UseSetting("Control:OwnerPasswordFile", _passwordPath);
    }
}
