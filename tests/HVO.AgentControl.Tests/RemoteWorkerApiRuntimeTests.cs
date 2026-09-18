using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Organization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
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
        { "/api/workers/wrk-missing/recover", "{\"obligationId\":\"rec-missing\",\"expectedRevision\":1}" },
        { "/api/workers/wrk-missing/recover/rec-missing/acknowledge", "{\"expectedRevision\":1,\"evidenceHash\":\"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"disposition\":\"acknowledged-after-external-reconciliation\"}" },
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
    /// Every typed remote-worker failure and the exact status and title it owns.
    /// Distinct meanings must not collapse into one generic conflict.
    /// </summary>
    public static TheoryData<Exception, int, string> TypedFailures => new()
    {
        { new HVO.AgentControl.RemoteWorker.WorkerControlDisabledException(SecretDetail), 409, "Remote worker control is disabled." },
        { new HVO.AgentControl.RemoteWorker.WorkerControlConfigurationException(SecretDetail), 409, "Remote worker configuration is invalid." },
        { new HVO.AgentControl.RemoteWorker.RemoteWorkerUnavailableException(SecretDetail, transport: true), 502, "Remote worker host is unreachable." },
        { new HVO.AgentControl.RemoteWorker.RemoteWorkerUnavailableException(SecretDetail), 503, "Remote worker host is unavailable." },
        { new HVO.AgentControl.RemoteWorker.WorkerReconciliationInvalidException(SecretDetail), 502, "Remote worker reconciliation is invalid" },
        { new HVO.AgentControl.RemoteWorker.WorkerRecoveryRequiredException(SecretDetail, "replay-gap"), 409, "Remote worker recovery is required." },
        { new HVO.AgentControl.RemoteWorker.ForeignResourceException(SecretDetail), 409, "Remote resource is not owned by this controller." },
        { new HVO.AgentControl.RemoteWorker.WorkerPermissionOptionsUnsupportedException(), 409, "Worker permission cannot be rejected safely." },
        { new HVO.AgentControl.Organization.OrganizationValidationException(SecretDetail), 400, "Remote worker request is invalid." },
        { new HVO.AgentControl.Organization.OrganizationConcurrencyException(SecretDetail), 409, "Remote worker request conflicted." },
        { new HVO.AgentControl.Organization.OrganizationNotFoundException(SecretDetail), 404, "Remote worker record not found." },
        { new KeyNotFoundException(SecretDetail), 404, "Remote worker record not found." },
        { new HVO.AgentControl.Organization.OrganizationStoreException(SecretDetail), 503, "Worker store unavailable." },
        { new HVO.AgentControl.RemoteWorker.WorkerWriteUncertainException(SecretDetail), 502, "Remote worker bridge is unavailable." },
        { new HVO.AgentControl.RemoteWorker.WorkerReadUncertainException(SecretDetail), 502, "Remote worker bridge is unavailable." },
        { new HVO.AgentControl.Worker.WorkerOperationUncertainException(SecretDetail), 502, "Remote worker bridge is unavailable." },
        { new HVO.AgentControl.RemoteWorker.WorkerRemoteException("worker-request-rejected"), 409, "Remote worker bridge rejected the operation." },
        { new HVO.AgentControl.RemoteWorker.WorkerRemoteException("worker-operation-failed"), 502, "Remote worker bridge is unavailable." },
        // An internal invariant failure is a sanitized 500, never a client-fixable conflict.
        { new InvalidOperationException(SecretDetail), 500, "Remote worker operation failed." },
    };

    /// <summary>A distinctive message that must never appear in any mapped response.</summary>
    public const string SecretDetail = "raw-internal-detail-/control-data/control.db";

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
/// The typed remote-worker failures each map to their own RFC 9457 contract, so
/// no caller has to infer intent from a generic conflict.
/// </summary>
public sealed class RemoteWorkerProblemMappingTests
{
    [Theory]
    [MemberData(nameof(RemoteWorkerApi.TypedFailures), MemberType = typeof(RemoteWorkerApi))]
    public async Task EachTypedFailureOwnsItsExactStatusAndTitle(Exception exception, int expectedStatus, string expectedTitle)
    {
        Assert.True(Program.IsRemoteWorkerFailure(exception), $"{exception.GetType().Name} is not routed to the remote-worker contract.");

        var problem = await ExecuteAsync(Program.RemoteWorkerProblem(exception));

        Assert.Equal(expectedStatus, problem.GetProperty("status").GetInt32());
        Assert.Equal(expectedTitle, problem.GetProperty("title").GetString());
        // The raw exception message is never surfaced.
        Assert.DoesNotContain(exception.Message, problem.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void GenericLocalProtocolInvariantIsNotMappedAsARemoteFailure()
    {
        Assert.False(Program.IsRemoteWorkerFailure(new HVO.AgentControl.Worker.WorkerProtocolException(RemoteWorkerApi.SecretDetail)));
    }

    [Fact]
    public async Task UnsupportedPermissionOptionsProblemUsesFixedSanitizedDetail()
    {
        var exception = new HVO.AgentControl.RemoteWorker.WorkerPermissionOptionsUnsupportedException();
        var problem = await ExecuteAsync(Program.RemoteWorkerProblem(exception));

        Assert.Equal(409, problem.GetProperty("status").GetInt32());
        Assert.Equal("Worker permission cannot be rejected safely.", problem.GetProperty("title").GetString());
        Assert.Equal("The worker offered no recognized reject option; dispatch remains held. Update compatibility before retrying.", problem.GetProperty("detail").GetString());
        Assert.DoesNotContain("vendor-deny-secret", problem.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(RemoteWorkerApi.SecretDetail, problem.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidReconciliationProblemUsesSanitizedHeldDispatchDetail()
    {
        var exception = new HVO.AgentControl.RemoteWorker.WorkerReconciliationInvalidException(RemoteWorkerApi.SecretDetail);
        Assert.True(Program.IsRemoteWorkerFailure(exception));

        var problem = await ExecuteAsync(Program.RemoteWorkerProblem(exception));

        Assert.Equal(502, problem.GetProperty("status").GetInt32());
        Assert.Equal("Remote worker reconciliation is invalid", problem.GetProperty("title").GetString());
        Assert.Equal("Worker returned state that could not be correlated; dispatch remains held.", problem.GetProperty("detail").GetString());
        Assert.DoesNotContain(RemoteWorkerApi.SecretDetail, problem.GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>The recovery problem names the obligation kind an operator must act on.</summary>
    [Fact]
    public async Task RecoveryRequiredProblemNamesTheObligationKind()
    {
        var problem = await ExecuteAsync(Program.RemoteWorkerProblem(new HVO.AgentControl.RemoteWorker.WorkerRecoveryRequiredException("held", "replay-ack-uncertain")));
        Assert.Contains("replay-ack-uncertain", problem.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    private static async Task<JsonElement> ExecuteAsync(IResult result)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            RequestServices = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
                .AddLogging()
                .AddProblemDetails()
                .BuildServiceProvider(),
        };
        using var body = new MemoryStream();
        context.Response.Body = body;
        await result.ExecuteAsync(context);
        body.Position = 0;
        using var document = await JsonDocument.ParseAsync(body);
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
[Collection(LocalPortBindingCollection.Name)]
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
    public async Task WorkerStatusExposesExactHashOnlyRecoveryAuditRow()
    {
        using var client = await AuthorizedClientAsync();
        var store = _factory.Host.Organization!;
        var overview = store.GetOverview();
        var binding = overview.Employees.Single(x => x.RuntimeBindingId is not null).RuntimeBindingId!;
        var databasePath = Path.Combine(_factory.DataDirectory, OrganizationStore.DatabaseFileName);
        const string workerId = "wrk-audit-api";
        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO execution_hosts(id,slug,display_name,transport_kind,endpoint_host,endpoint_port,endpoint_user,known_hosts_path,os,capability_status,enabled,enrolled,status,created_at,updated_at,revision)
                VALUES('host-audit-api','host-audit-api','Audit API host','ssh-docker','worker.example',22,'docker','/known','linux','valid',1,1,'ready',$now,$now,1);
                UPDATE runtime_bindings SET placement='DeveloperContainer',container_ref='audit-container' WHERE id=$binding;
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$binding", binding);
            command.ExecuteNonQuery();
        }
        store.CreateWorkerEnrollment(binding, "host-audit-api", "controller-a", "sha256:" + new string('a', 64), "linux/amd64", "/tmp/audit-api.key", "sha256:" + new string('b', 64), workerId);
        store.RecordControllerRecovery(workerId, "replay-gap", "markerless-audit-api");
        var obligation = Assert.Single(store.ListWorkerRecoveryObligations(workerId, true));
        const string evidenceHash = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        store.AcknowledgeControllerRecovery(workerId, obligation.Id, obligation.Revision, evidenceHash, "acknowledged-after-external-reconciliation");
        var expected = Assert.Single(store.ListWorkerRecoveryAudit(workerId));

        using var response = await client.GetAsync("/api/workers/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(raw);
        var audit = Assert.Single(document.RootElement.GetProperty("recoveryAudit").EnumerateArray());
        Assert.Equal(["id", "obligationId", "workerId", "kind", "markerHash", "evidenceHash", "disposition", "recordedAt"], audit.EnumerateObject().Select(x => x.Name).ToArray());
        Assert.Equal(expected.Id, audit.GetProperty("id").GetString());
        Assert.Equal(obligation.Id, audit.GetProperty("obligationId").GetString());
        Assert.Equal(workerId, audit.GetProperty("workerId").GetString());
        Assert.Equal("replay-gap", audit.GetProperty("kind").GetString());
        Assert.Equal(obligation.MarkerHash, audit.GetProperty("markerHash").GetString());
        Assert.Equal(evidenceHash, audit.GetProperty("evidenceHash").GetString());
        Assert.Equal("acknowledged-after-external-reconciliation", audit.GetProperty("disposition").GetString());
        Assert.Equal(expected.RecordedAt, audit.GetProperty("recordedAt").GetDateTimeOffset());
        Assert.DoesNotContain("markerJson", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("detail", audit.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("note", audit.GetRawText(), StringComparison.OrdinalIgnoreCase);
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
[Collection(LocalPortBindingCollection.Name)]
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
