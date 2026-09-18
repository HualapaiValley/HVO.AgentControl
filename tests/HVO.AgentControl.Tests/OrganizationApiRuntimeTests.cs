using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Organization;
using HVO.AgentControl.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// The organization API against a real enabled runtime: the seeded SQLite store
/// is opened, the fake ACP process establishes the session, and HTTP requests
/// exercise the real endpoint pipeline (owner Basic auth, same-origin checks and
/// RFC 9457 ProblemDetails). No provider or credential is touched.
/// </summary>
[Collection(LocalPortBindingCollection.Name)]
public sealed class OrganizationApiRuntimeTests : IClassFixture<EnabledRuntimeFactory>
{
    private readonly EnabledRuntimeFactory _factory;

    public OrganizationApiRuntimeTests(EnabledRuntimeFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetReturnsTheSeededOverviewWithoutSecretFields()
    {
        using var client = await CreateReadyClientAsync();
        using var response = await client.GetAsync("/api/organization");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal("agentcontrol-development", root.GetProperty("slug").GetString());
        Assert.Equal(OrganizationSeed.OrganizationDescription, root.GetProperty("description").GetString());
        Assert.Equal(OrganizationSeed.OrganizationInstructions, root.GetProperty("basicInstructions").GetString());

        var role = Assert.Single(root.GetProperty("roles").EnumerateArray());
        Assert.Equal(OrganizationSeed.OperationsItRoleInstructionProfile, role.GetProperty("instructionProfile").GetString());
        Assert.Equal(OrganizationSeed.OperationsItRolePermissionProfile, role.GetProperty("permissionProfile").GetString());

        var employee = Assert.Single(root.GetProperty("employees").EnumerateArray());
        Assert.Equal(OrganizationSeed.AdoptedEmployeePurpose, employee.GetProperty("purpose").GetString());
        Assert.Equal(OrganizationSeed.AdoptedEmployeeInstructions, employee.GetProperty("instructions").GetString());
        Assert.Equal(OrganizationSeed.AdoptedEmployeeRules, employee.GetProperty("rules").GetString());
        Assert.Equal(OrganizationSeed.AdoptedEmployeeRestrictions, employee.GetProperty("restrictions").GetString());

        // The adoption audit carries the exact owner-approved reference.
        var audit = Assert.Single(root.GetProperty("adoptionAudit").EnumerateArray());
        Assert.Equal("owner-approved:issue-211", audit.GetProperty("authorizationReference").GetString());

        // No controller secret reaches the read model: not the tmux owner token
        // and not the owner password.
        var identity = _factory.Host.OrganizationIdentity;
        Assert.NotNull(identity);
        Assert.False(string.IsNullOrWhiteSpace(identity!.TmuxOwnerToken));
        Assert.DoesNotContain(identity.TmuxOwnerToken, body, StringComparison.Ordinal);
        Assert.DoesNotContain(EnabledRuntimeFactory.OwnerPassword, body, StringComparison.Ordinal);
        Assert.DoesNotContain("tmuxOwnerToken", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ownerToken", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PortalAndEmployeeEndpointsReturnExactSafeDiagnostics()
    {
        using var client = await CreateReadyClientAsync();
        using var portalResponse = await client.GetAsync("/api/organization/portal");
        Assert.Equal(HttpStatusCode.OK, portalResponse.StatusCode);
        var portalBody = await portalResponse.Content.ReadAsStringAsync();
        using var portalDocument = JsonDocument.Parse(portalBody);
        var portal = portalDocument.RootElement;
        var pendingApprovals = portal.GetProperty("pendingApprovals");
        Assert.True(pendingApprovals.GetProperty("supported").GetBoolean());
        Assert.Equal(
            pendingApprovals.GetProperty("items").GetArrayLength(),
            pendingApprovals.GetProperty("count").GetInt32());
        var employee = Assert.Single(portal.GetProperty("employees").EnumerateArray());
        var employeeId = employee.GetProperty("id").GetString()!;

        using var detailResponse = await client.GetAsync($"/api/employees/{employeeId}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detailBody = await detailResponse.Content.ReadAsStringAsync();
        using var detailDocument = JsonDocument.Parse(detailBody);
        var detail = detailDocument.RootElement;
        Assert.Equal(employeeId, detail.GetProperty("id").GetString());
        Assert.Equal(_factory.Host.OrganizationIdentity!.RuntimeBindingId, detail.GetProperty("runtime").GetProperty("bindingId").GetString());
        Assert.Equal(_factory.Host.GetStatus().SessionId, detail.GetProperty("runtime").GetProperty("nativeSessionId").GetString());
        Assert.False(detail.GetProperty("recentLogs").GetProperty("supported").GetBoolean());
        Assert.False(detail.GetProperty("terminal").GetProperty("available").GetBoolean());
        Assert.DoesNotContain(_factory.Host.OrganizationIdentity.TmuxOwnerToken, detailBody, StringComparison.Ordinal);
        Assert.DoesNotContain("tmuxOwnerToken", detailBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmployeeEndpointReturns404ForUnknownStableId()
    {
        using var client = await CreateReadyClientAsync();
        using var response = await client.GetAsync("/api/employees/emp-does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DepartmentEndpointReturnsScopedAuthoritativeDetailAndSafeErrors()
    {
        using var client = await CreateReadyClientAsync();
        var overview = await ReadOrganizationAsync(client);
        var departments = overview.GetProperty("departments").EnumerateArray().ToArray();
        var operations = departments.Single(x => x.GetProperty("slug").GetString() == OrganizationSeed.OperationsSlug);
        var operationsId = operations.GetProperty("id").GetString()!;
        var developmentId = departments.Single(x => x.GetProperty("slug").GetString() == OrganizationSeed.DevelopmentSlug)
            .GetProperty("id").GetString()!;

        using var response = await client.GetAsync($"/api/departments/{operationsId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(operationsId, root.GetProperty("id").GetString());
        Assert.Equal(OrganizationSeed.OperationsSlug, root.GetProperty("slug").GetString());
        Assert.Equal(overview.GetProperty("displayName").GetString(), root.GetProperty("organizationDisplayName").GetString());
        Assert.Equal(1, root.GetProperty("revision").GetInt32());
        Assert.Equal(OrganizationSeed.DepartmentOrientation, root.GetProperty("standingInstructions").GetString());
        Assert.Equal($"/hiring?departmentId={operationsId}", root.GetProperty("hireUrl").GetString());
        Assert.Equal("operations-it", Assert.Single(root.GetProperty("roles").EnumerateArray()).GetProperty("slug").GetString());
        var roster = Assert.Single(root.GetProperty("employees").EnumerateArray());
        Assert.StartsWith("emp-", roster.GetProperty("id").GetString()!, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(roster.GetProperty("availability").GetString()));

        using var emptyResponse = await client.GetAsync($"/api/departments/{developmentId}");
        Assert.Equal(HttpStatusCode.OK, emptyResponse.StatusCode);
        using var emptyDocument = JsonDocument.Parse(await emptyResponse.Content.ReadAsStringAsync());
        var empty = emptyDocument.RootElement;
        Assert.Empty(empty.GetProperty("roles").EnumerateArray());
        Assert.Empty(empty.GetProperty("employees").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("standingInstructions").ValueKind);
        Assert.Equal($"/hiring?departmentId={developmentId}", empty.GetProperty("hireUrl").GetString());

        using var invalid = await client.GetAsync("/api/departments/dept-");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var unknown = await client.GetAsync("/api/departments/dept-does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task TerminalRejectsMalformedUnknownAndUnreadySelectionsWithoutFallback()
    {
        using var client = await CreateReadyClientAsync();

        foreach (var path in new[] { "/terminal", "/terminal?employeeId=emp-", "/terminal?employeeId=EMP-test" })
        {
            using var malformed = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        }

        using var unknown = await client.GetAsync("/terminal?employeeId=emp-does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // The seeded employee is the exact host-owned binding/session, but the
        // enabled test runtime starts with Control:EnableTerminal=false so
        // TerminalReady stays false. The exact tuple must still fail closed with
        // 503 and never accept a WebSocket.
        using var portal = await client.GetAsync("/api/organization/portal");
        Assert.Equal(HttpStatusCode.OK, portal.StatusCode);
        using var document = JsonDocument.Parse(await portal.Content.ReadAsStringAsync());
        var employeeId = Assert.Single(document.RootElement.GetProperty("employees").EnumerateArray())
            .GetProperty("id").GetString()!;

        using var unready = await client.GetAsync(
            $"/terminal?employeeId={Uri.EscapeDataString(employeeId)}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unready.StatusCode);
    }

    [Fact]
    public async Task HireRequestApiEnforcesOriginIdempotencyRelationshipsBoundsAndRevisionRejection()
    {
        using var client = await CreateReadyClientAsync();
        var overview = await ReadOrganizationAsync(client);
        var department = overview.GetProperty("departments").EnumerateArray().Single(x => x.GetProperty("slug").GetString() == OrganizationSeed.OperationsSlug);
        var role = Assert.Single(overview.GetProperty("roles").EnumerateArray());
        var payload = new
        {
            idempotencyKey = "api-hire-key-1",
            requestedDisplayName = "API Developer",
            purpose = "Exercise the first durable hiring slice.",
            departmentId = department.GetProperty("id").GetString(),
            roleId = role.GetProperty("id").GetString(),
            placement = RuntimePlacements.DeveloperContainer,
            cpuLimit = 2,
            memoryLimitMiB = 2048,
            pidsLimit = 256,
        };

        using var unauthenticated = _factory.CreateClient();
        using var unauthenticatedResponse = await unauthenticated.PostAsJsonAsync("/api/hire-requests", payload);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticatedResponse.StatusCode);

        using var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/hire-requests") { Content = JsonContent.Create(payload) };
        crossOrigin.Headers.Add("Origin", "https://other.example");
        using var crossOriginResponse = await client.SendAsync(crossOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, crossOriginResponse.StatusCode);

        // A body key is sufficient when the header is absent.
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/hire-requests") { Content = JsonContent.Create(payload) };
        create.Headers.Add("Origin", _factory.ClientOptions.BaseAddress.GetLeftPart(UriPartial.Authority));
        using var createdResponse = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.OK, createdResponse.StatusCode);
        using var createdDocument = JsonDocument.Parse(await createdResponse.Content.ReadAsStringAsync());
        var created = createdDocument.RootElement;
        var id = created.GetProperty("id").GetString()!;
        var revision = created.GetProperty("revision").GetInt32();
        Assert.Equal(HireRequestStates.Requested, created.GetProperty("state").GetString());

        using var duplicate = new HttpRequestMessage(HttpMethod.Post, "/api/hire-requests") { Content = JsonContent.Create(payload) };
        duplicate.Headers.Add("Origin", _factory.ClientOptions.BaseAddress.GetLeftPart(UriPartial.Authority));
        duplicate.Headers.Add("Idempotency-Key", "api-hire-key-1");
        using var duplicateResponse = await client.SendAsync(duplicate);
        Assert.Equal(HttpStatusCode.OK, duplicateResponse.StatusCode);
        using var duplicateDocument = JsonDocument.Parse(await duplicateResponse.Content.ReadAsStringAsync());
        Assert.Equal(id, duplicateDocument.RootElement.GetProperty("id").GetString());

        // A header key is also sufficient when the nullable body field is omitted.
        var headerOnlyPayload = new
        {
            requestedDisplayName = "Header-only Developer",
            payload.purpose,
            payload.departmentId,
            payload.roleId,
            payload.placement,
            payload.cpuLimit,
            payload.memoryLimitMiB,
            payload.pidsLimit,
        };
        using var headerOnly = new HttpRequestMessage(HttpMethod.Post, "/api/hire-requests") { Content = JsonContent.Create(headerOnlyPayload) };
        headerOnly.Headers.Add("Origin", _factory.ClientOptions.BaseAddress.GetLeftPart(UriPartial.Authority));
        headerOnly.Headers.Add("Idempotency-Key", "api-hire-header-only");
        using var headerOnlyResponse = await client.SendAsync(headerOnly);
        Assert.Equal(HttpStatusCode.OK, headerOnlyResponse.StatusCode);

        using var mismatch = new HttpRequestMessage(HttpMethod.Post, "/api/hire-requests") { Content = JsonContent.Create(payload with { idempotencyKey = "body-key" }) };
        mismatch.Headers.Add("Origin", _factory.ClientOptions.BaseAddress.GetLeftPart(UriPartial.Authority));
        mismatch.Headers.Add("Idempotency-Key", "header-key");
        using var mismatchResponse = await client.SendAsync(mismatch);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatchResponse.StatusCode);
        Assert.Contains("must match exactly", (await ReadProblemAsync(mismatchResponse)).GetProperty("detail").GetString(), StringComparison.Ordinal);

        using var conflict = new HttpRequestMessage(HttpMethod.Post, "/api/hire-requests") { Content = JsonContent.Create(payload with { purpose = "Different payload" }) };
        conflict.Headers.Add("Origin", _factory.ClientOptions.BaseAddress.GetLeftPart(UriPartial.Authority));
        conflict.Headers.Add("Idempotency-Key", "api-hire-key-1");
        using var conflictResponse = await client.SendAsync(conflict);
        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);

        using var missing = new HttpRequestMessage(HttpMethod.Post, "/api/hire-requests") { Content = JsonContent.Create(headerOnlyPayload with { requestedDisplayName = "Missing Key" }) };
        missing.Headers.Add("Origin", _factory.ClientOptions.BaseAddress.GetLeftPart(UriPartial.Authority));
        using var missingResponse = await client.SendAsync(missing);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missingResponse.StatusCode);
        Assert.Contains("is required", (await ReadProblemAsync(missingResponse)).GetProperty("detail").GetString(), StringComparison.Ordinal);

        foreach (var (resourcePayload, expectedDetail) in new[]
        {
            (payload with { idempotencyKey = "api-hire-cpu-bounds", cpuLimit = 0 }, "CPU limit"),
            (payload with { idempotencyKey = "api-hire-memory-bounds", memoryLimitMiB = 0 }, "Memory limit"),
            (payload with { idempotencyKey = "api-hire-pids-bounds", pidsLimit = 0 }, "PID limit"),
        })
        {
            using var bounded = new HttpRequestMessage(HttpMethod.Post, "/api/hire-requests") { Content = JsonContent.Create(resourcePayload) };
            bounded.Headers.Add("Origin", _factory.ClientOptions.BaseAddress.GetLeftPart(UriPartial.Authority));
            using var boundedResponse = await client.SendAsync(bounded);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, boundedResponse.StatusCode);
            Assert.Contains(expectedDetail, (await ReadProblemAsync(boundedResponse)).GetProperty("detail").GetString(), StringComparison.Ordinal);
        }

        using var malformedGet = await client.GetAsync("/api/hire-requests/not-a-hire-id");
        Assert.Equal(HttpStatusCode.BadRequest, malformedGet.StatusCode);
        Assert.Equal("Invalid hire request id.", (await ReadProblemAsync(malformedGet)).GetProperty("title").GetString());

        using var malformedReject = new HttpRequestMessage(HttpMethod.Post, "/api/hire-requests/not-a-hire-id/reject") { Content = JsonContent.Create(new { expectedRevision = revision }) };
        malformedReject.Headers.Add("Origin", _factory.ClientOptions.BaseAddress.GetLeftPart(UriPartial.Authority));
        using var malformedRejectResponse = await client.SendAsync(malformedReject);
        Assert.Equal(HttpStatusCode.BadRequest, malformedRejectResponse.StatusCode);
        Assert.Equal("Invalid hire request id.", (await ReadProblemAsync(malformedRejectResponse)).GetProperty("title").GetString());

        using var reject = new HttpRequestMessage(HttpMethod.Post, $"/api/hire-requests/{id}/reject") { Content = JsonContent.Create(new { expectedRevision = revision }) };
        reject.Headers.Add("Origin", _factory.ClientOptions.BaseAddress.GetLeftPart(UriPartial.Authority));
        using var rejectResponse = await client.SendAsync(reject);
        Assert.Equal(HttpStatusCode.OK, rejectResponse.StatusCode);
        using var rejectedDocument = JsonDocument.Parse(await rejectResponse.Content.ReadAsStringAsync());
        Assert.Equal(HireRequestStates.Rejected, rejectedDocument.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task PatchRenamesUnderTheCurrentRevision()
    {
        using var client = await CreateReadyClientAsync();

        var before = await ReadOrganizationAsync(client);
        var organizationId = before.GetProperty("id").GetString()!;
        var revision = before.GetProperty("revision").GetInt32();

        using var request = Mutation(organizationId, "Renamed Runtime Organization", revision);
        request.Headers.Authorization = Basic("owner", EnabledRuntimeFactory.OwnerPassword);
        request.Headers.Add("Origin", _factory.ClientOptions.BaseAddress.GetLeftPart(UriPartial.Authority));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("Renamed Runtime Organization", root.GetProperty("displayName").GetString());
        Assert.Equal(revision + 1, root.GetProperty("revision").GetInt32());
        Assert.Equal(organizationId, root.GetProperty("id").GetString());

        // The rename is persisted, not just echoed.
        var persisted = await ReadOrganizationAsync(client);
        Assert.Equal("Renamed Runtime Organization", persisted.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task StaleRevisionIs409ProblemDetails()
    {
        using var client = await CreateReadyClientAsync();
        var before = await ReadOrganizationAsync(client);

        using var request = Mutation(
            before.GetProperty("id").GetString()!,
            "Stale Rename",
            before.GetProperty("revision").GetInt32() + 10);
        request.Headers.Authorization = Basic("owner", EnabledRuntimeFactory.OwnerPassword);
        request.Headers.Add("Origin", _factory.ClientOptions.BaseAddress.GetLeftPart(UriPartial.Authority));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(409, problem.GetProperty("status").GetInt32());
        Assert.Equal("/api/organization", problem.GetProperty("instance").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task UnknownOrganizationIs404ProblemDetails()
    {
        using var client = await CreateReadyClientAsync();

        using var request = Mutation("org-does-not-exist", "Nope", 1);
        request.Headers.Authorization = Basic("owner", EnabledRuntimeFactory.OwnerPassword);
        request.Headers.Add("Origin", _factory.ClientOptions.BaseAddress.GetLeftPart(UriPartial.Authority));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.Equal("/api/organization", problem.GetProperty("instance").GetString());
    }

    [Fact]
    public async Task UnauthenticatedReadIs401ProblemDetails()
    {
        await _factory.WaitForReadyAsync(TimeSpan.FromSeconds(45));
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/organization");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Basic", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(401, problem.GetProperty("status").GetInt32());
        Assert.Equal("/api/organization", problem.GetProperty("instance").GetString());
    }

    [Fact]
    public async Task CrossOriginMutationIs403ProblemDetails()
    {
        using var client = await CreateReadyClientAsync();
        var before = await ReadOrganizationAsync(client);

        using var request = Mutation(
            before.GetProperty("id").GetString()!,
            "Cross Origin",
            before.GetProperty("revision").GetInt32());
        request.Headers.Authorization = Basic("owner", EnabledRuntimeFactory.OwnerPassword);
        request.Headers.Add("Origin", "https://other.example");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(403, problem.GetProperty("status").GetInt32());
    }

    /// <summary>
    /// A store that opened and validated but faults during a later read is still
    /// a 503 ProblemDetails, not a leaked 500 or database detail. The damage is
    /// applied after the runtime is ready on a dedicated factory so the shared
    /// fixture store stays valid for the other tests.
    /// </summary>
    [Fact]
    public async Task DamagedOpenableStoreReadIs503ProblemDetailsWithoutLeaking()
    {
        using var factory = new EnabledRuntimeFactory();
        using var client = factory.CreateClient();
        await factory.WaitForReadyAsync(TimeSpan.FromSeconds(45));
        client.DefaultRequestHeaders.Authorization = Basic("owner", EnabledRuntimeFactory.OwnerPassword);

        // The store opened and passed validation; now drop a table the overview
        // reads so the next read faults at the SQLite layer.
        var databasePath = Path.Combine(factory.DataDirectory, OrganizationStore.DatabaseFileName);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };
        using (var connection = new SqliteConnection(builder.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE adoption_audit;";
            command.ExecuteNonQuery();
        }

        using var response = await client.GetAsync("/api/organization");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("control.db", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("adoption_audit", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("SqliteException", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("no such table", raw, StringComparison.OrdinalIgnoreCase);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(503, problem.GetProperty("status").GetInt32());
        Assert.Equal("/api/organization", problem.GetProperty("instance").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
    }

    private async Task<HttpClient> CreateReadyClientAsync()
    {
        var client = _factory.CreateClient();
        await _factory.WaitForReadyAsync(TimeSpan.FromSeconds(45));
        client.DefaultRequestHeaders.Authorization = Basic("owner", EnabledRuntimeFactory.OwnerPassword);
        return client;
    }

    private static async Task<JsonElement> ReadOrganizationAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/organization");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static HttpRequestMessage Mutation(string organizationId, string displayName, int revision) =>
        new(HttpMethod.Patch, "/api/organization")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { organizationId, displayName, revision }),
                Encoding.UTF8,
                "application/json"),
        };

    private static AuthenticationHeaderValue Basic(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}

/// <summary>
/// Enabled runtime backed by the disposable fake ACP server and a temporary
/// controller-private database. The owner password is a disposable file; no
/// provider credentials or model inference are involved.
/// </summary>
public class EnabledRuntimeFactory : WebApplicationFactory<Program>, IDisposable
{
    public const string OwnerPassword = "enabled-runtime-owner-password-000000";

    private readonly string _root;
    private readonly string _passwordPath;
    private readonly bool _workerControlEnabled;

    public EnabledRuntimeFactory()
        : this("prompt_fast")
    {
    }

    internal EnabledRuntimeFactory(string scenario, bool workerControlEnabled = false)
    {
        _root = Path.Combine(Path.GetTempPath(), "agentcontrol-enabled-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataDirectory);
        _passwordPath = Path.Combine(_root, "owner-password");
        File.WriteAllText(_passwordPath, OwnerPassword);
        OpenCodeExecutable = AcpFakeServer.CreateExecutable(scenario);
        NativePort = GetFreePort();
        _workerControlEnabled = workerControlEnabled;
    }

    public string OpenCodeExecutable { get; }

    public int NativePort { get; }

    public string DataDirectory => Path.Combine(_root, "data");

    public AcpControlHost Host => Services.GetRequiredService<AcpControlHost>();

    public async Task<AcpControlHost> WaitForReadyAsync(TimeSpan timeout)
    {
        var host = Host;
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = host.GetStatus();
            // Ready is only suitable for owner work once the startup bootstrap
            // prompt has settled; the session is reported busy while it holds the
            // serialized prompt slot.
            if (string.Equals(status.State, "ready", StringComparison.Ordinal)
                && !string.Equals(status.SessionState, "busy", StringComparison.Ordinal))
            {
                return host;
            }

            if (string.Equals(status.State, "faulted", StringComparison.Ordinal))
            {
                throw new Xunit.Sdk.XunitException($"Enabled runtime faulted: {status.Error}");
            }

            await Task.Delay(100);
        }

        var final = host.GetStatus();
        throw new Xunit.Sdk.XunitException(
            $"Enabled runtime did not become ready. Current: {final.State} ({final.Error}).");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Control:Enabled", "true");
        builder.UseSetting("Control:DataDirectory", DataDirectory);
        builder.UseSetting("Control:OpenCodeExecutable", OpenCodeExecutable);
        builder.UseSetting("Control:NativePort", NativePort.ToString(CultureInfo.InvariantCulture));
        builder.UseSetting("Control:OwnerPasswordFile", _passwordPath);
        builder.UseSetting("Control:EnableTerminal", "false");
        builder.UseSetting("Control:StartupTimeoutSeconds", "20");
        builder.UseSetting("Control:PromptTimeoutSeconds", "20");

        // Enabled but deliberately invalid WorkerControl configuration: no
        // ControllerId, image digest, or approved hosts. Startup does not
        // validate those options, so the endpoint contract owns the 409.
        if (_workerControlEnabled)
        {
            builder.UseSetting("WorkerControl:Enabled", "true");
        }
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
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
