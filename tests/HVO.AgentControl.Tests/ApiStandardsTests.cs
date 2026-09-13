using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// API framework contract: RFC 9457 ProblemDetails with <c>traceId</c> and
/// <c>instance</c>, the built-in OpenAPI document, and owner Basic auth.
/// The runtime stays disabled so no OpenCode process or model provider is used.
/// </summary>
public sealed class ApiStandardsTests : IClassFixture<DisabledRuntimeFactory>
{
    private readonly DisabledRuntimeFactory _factory;

    public ApiStandardsTests(DisabledRuntimeFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task LivenessIsUnauthenticatedProcessLivenessOnly()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("healthy", body.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ReadinessIs503ProblemDetailsWhenRuntimeDisabled()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(503, problem.GetProperty("status").GetInt32());
        Assert.Equal("/health/ready", problem.GetProperty("instance").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task InvalidJsonBodyIs400ProblemDetailsWithoutLeaking()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/control/model");
        request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        request.Content = new StringContent("{\"model\":", Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
        Assert.False(problem.TryGetProperty("exception", out _));
        Assert.False(problem.TryGetProperty("stackTrace", out _));
    }

    [Fact]
    public async Task CrossOriginModelChangeIs403ProblemDetails()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/control/model");
        request.Headers.Add("Origin", "https://other.example");
        request.Content = new StringContent("{\"model\":\"opencode/big-pickle\"}", Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(403, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task DisabledRuntimeModelChangeIs409ProblemDetails()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/control/model");
        request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        request.Content = new StringContent("{\"model\":\"opencode/big-pickle\"}", Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(409, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task UnknownRouteIs404ProblemDetails()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/not-a-real-endpoint");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.Equal("/api/not-a-real-endpoint", problem.GetProperty("instance").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task OwnerAuthRejectsUnauthenticatedRequestsWith401ProblemDetails()
    {
        using var factory = new OwnerAuthFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/info");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Basic", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(401, problem.GetProperty("status").GetInt32());
        Assert.Equal("/api/info", problem.GetProperty("instance").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task OwnerAuthLeavesLivenessOpenButProtectsReadiness()
    {
        using var factory = new OwnerAuthFactory();
        using var client = factory.CreateClient();

        using var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        using var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.Unauthorized, ready.StatusCode);

        using var authorized = new HttpRequestMessage(HttpMethod.Get, "/health/ready");
        authorized.Headers.Authorization = Basic("owner", OwnerAuthFactory.OwnerPassword);
        using var readyAuthorized = await client.SendAsync(authorized);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readyAuthorized.StatusCode);
        var problem = await ReadProblemAsync(readyAuthorized);
        Assert.Equal(503, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task OpenApiDocumentIsServedWithoutAuthWhenUnconfigured()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        var paths = root.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/control/model", out var model));
        Assert.True(paths.TryGetProperty("/health/ready", out var ready));
        Assert.True(paths.TryGetProperty("/health/live", out _));

        var modelResponses = model.GetProperty("post").GetProperty("responses");
        foreach (var status in new[] { "200", "400", "403", "409", "502" })
        {
            Assert.True(modelResponses.TryGetProperty(status, out _), $"Missing model response {status}.");
        }

        var readyResponses = ready.GetProperty("get").GetProperty("responses");
        Assert.True(readyResponses.TryGetProperty("200", out _));
        Assert.True(readyResponses.TryGetProperty("503", out _));

        // No owner auth configured, so no security scheme and no global requirement.
        Assert.False(root.TryGetProperty("components", out var components)
            && components.TryGetProperty("securitySchemes", out _));
    }

    [Fact]
    public async Task OpenApiDocumentSitsBehindBasicAuthWhenConfigured()
    {
        using var factory = new OwnerAuthFactory();
        using var client = factory.CreateClient();

        using var unauthenticated = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        await ReadProblemAsync(unauthenticated);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/openapi/v1.json");
        request.Headers.Authorization = Basic("owner", OwnerAuthFactory.OwnerPassword);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        var scheme = root.GetProperty("components").GetProperty("securitySchemes").GetProperty("basic");
        Assert.Equal("http", scheme.GetProperty("type").GetString());
        Assert.Equal("basic", scheme.GetProperty("scheme").GetString());

        // Protected operations reference the scheme; process liveness does not.
        var modelSecurity = root.GetProperty("paths")
            .GetProperty("/api/control/model").GetProperty("post").GetProperty("security");
        Assert.Contains(
            modelSecurity.EnumerateArray(),
            requirement => requirement.TryGetProperty("basic", out _));
        var live = root.GetProperty("paths").GetProperty("/health/live").GetProperty("get");
        Assert.False(live.TryGetProperty("security", out _));
    }

    private static AuthenticationHeaderValue Basic(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}

/// <summary>Disabled runtime with no owner password, so no auth and no process.</summary>
public sealed class DisabledRuntimeFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Control:Enabled", "false");
    }
}

/// <summary>
/// Disabled runtime with a disposable, inert owner password file. The runtime
/// never starts, so no OpenCode process or model provider is touched.
/// </summary>
public sealed class OwnerAuthFactory : WebApplicationFactory<Program>
{
    public const string OwnerPassword = "inert-owner-password-for-tests-000000";

    private readonly string _passwordPath;

    public OwnerAuthFactory()
    {
        _passwordPath = Path.Combine(Path.GetTempPath(), $"hvo-agentcontrol-owner-{Guid.NewGuid():N}.txt");
        File.WriteAllText(_passwordPath, OwnerPassword);
    }

    public string PasswordPath => _passwordPath;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Control:Enabled", "false");
        builder.UseSetting("Control:OwnerPasswordFile", _passwordPath);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        try
        {
            File.Delete(_passwordPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Readiness contract for the pure predicate that backs <c>/health/ready</c>.
/// Exercised directly so the exact session+TUI rule can be checked without a
/// live OpenCode process.
/// </summary>
public sealed class ReadinessPredicateTests
{
    [Fact]
    public void TerminalLaunchDisabledStillFailsClosedWhenNoTerminalIsAttached()
    {
        // Control:EnableTerminal=false skips the tmux attach client, so
        // TerminalReady stays false even for an otherwise valid session. The
        // session+TUI contract requires the attached terminal regardless of
        // that configuration, so readiness must not consult it.
        Assert.False(Program.IsRuntimeReady(Status(terminalReady: false)));
    }

    [Fact]
    public void ExactIdleSessionWithAttachedTerminalIsReady()
    {
        Assert.True(Program.IsRuntimeReady(Status()));
    }

    [Theory]
    [InlineData("starting", "ses_exact", "idle", true)]
    [InlineData("degraded", "ses_exact", "idle", true)]
    [InlineData("faulted", "ses_exact", "idle", true)]
    [InlineData("ready", null, "idle", true)]
    [InlineData("ready", "", "idle", true)]
    [InlineData("ready", "ses_exact", null, true)]
    [InlineData("ready", "ses_exact", "unknown", true)]
    [InlineData("ready", "ses_exact", "idle", false)]
    public void IncompleteOrUnknownReadinessInputsAreNotReady(
        string state,
        string? sessionId,
        string? sessionState,
        bool terminalReady)
    {
        Assert.False(Program.IsRuntimeReady(Status(state, sessionId, sessionState, terminalReady)));
    }

    private static ControlStatus Status(
        string state = "ready",
        string? sessionId = "ses_exact",
        string? sessionState = "idle",
        bool terminalReady = true) => new()
        {
            State = state,
            OrganizationName = "AgentControl Development",
            SessionId = sessionId,
            SessionState = sessionState,
            TerminalReady = terminalReady,
        };
}

/// <summary>
/// Disabled runtime with a test-only middleware that throws. The middleware is
/// injected through an <see cref="IStartupFilter"/> and appended after the
/// application pipeline so the failure is handled by the registered
/// ProblemDetails exception handler. No test-only route is added to production.
/// </summary>
public sealed class ExceptionPathFactory : WebApplicationFactory<Program>
{
    public const string InjectedFailure = "injected-sensitive-failure-detail";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Control:Enabled", "false");
        builder.ConfigureServices(services =>
            services.AddSingleton<IStartupFilter>(new ThrowingStartupFilter()));
    }

    private sealed class ThrowingStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Path.Equals("/__test/fault", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(InjectedFailure);
                }

                await nextMiddleware(context);
            });
        };
    }
}

/// <summary>
/// Sanitized 500 contract: every unexpected failure is a ProblemDetails with
/// <c>instance</c> and <c>traceId</c>, served as <c>application/problem+json</c>
/// for both clients that accept JSON and clients that decline it, and it never
/// leaks the exception.
/// </summary>
public sealed class ExceptionPathTests : IClassFixture<ExceptionPathFactory>
{
    private readonly ExceptionPathFactory _factory;

    public ExceptionPathTests(ExceptionPathFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    public async Task UnexpectedFailureIsSanitizedProblemDetails(string accept)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__test/fault");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ExceptionPathFactory.InjectedFailure, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), raw, StringComparison.Ordinal);
        Assert.DoesNotContain("stackTrace", raw, StringComparison.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(raw);
        var problem = document.RootElement;
        Assert.Equal(500, problem.GetProperty("status").GetInt32());
        Assert.Equal("/__test/fault", problem.GetProperty("instance").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
        Assert.False(problem.TryGetProperty("exception", out _));
    }
}
