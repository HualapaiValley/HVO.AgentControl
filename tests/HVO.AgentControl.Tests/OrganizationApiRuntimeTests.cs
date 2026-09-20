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

        // The additive profile-status projection is present and all-null for the
        // internal seed employee, which is not a managed employee.
        var profileStatus = detail.GetProperty("profileStatus");
        Assert.Equal(JsonValueKind.Null, profileStatus.GetProperty("currentProfileRevisionId").ValueKind);
        Assert.Equal(JsonValueKind.Null, profileStatus.GetProperty("currentRevisionNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, profileStatus.GetProperty("currentImageDigest").ValueKind);
        Assert.False(profileStatus.GetProperty("newerRevisionAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, profileStatus.GetProperty("newerRevisionNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, profileStatus.GetProperty("activeRebuildState").ValueKind);
        Assert.Equal(JsonValueKind.Null, profileStatus.GetProperty("activeRebuildId").ValueKind);
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
    public async Task ContainerProfileEndpointsAreOwnerOnlySameOriginIdempotentRevisionBoundAndNeverProvision()
    {
        using var client = await CreateReadyClientAsync();
        var origin = _factory.ClientOptions.BaseAddress.GetLeftPart(UriPartial.Authority);
        HttpRequestMessage Post(string path, object body, string? key = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            request.Headers.Add("Origin", origin);
            if (key is not null) request.Headers.Add("Idempotency-Key", key);
            return request;
        }

        // The seeded generic-employee profile is listed and readable with its single unbuilt revision.
        using var list = await client.GetAsync("/api/profiles");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listDocument = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var seeded = listDocument.RootElement.EnumerateArray().Single(p => p.GetProperty("slug").GetString() == ContainerProfileSeed.GenericEmployeeSlug);
        var seededId = seeded.GetProperty("id").GetString()!;
        Assert.Equal(ContainerProfileBuildStatuses.Unbuilt, seeded.GetProperty("currentBuildStatus").GetString());

        using var detail = await client.GetAsync($"/api/profiles/{seededId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var detailDocument = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        var revision = Assert.Single(detailDocument.RootElement.GetProperty("revisions").EnumerateArray());
        Assert.Equal("seed", revision.GetProperty("createdBy").GetString());
        Assert.Equal(ContainerProfileDefinition.BaseImageReference, revision.GetProperty("baseImageReference").GetString());

        using var malformed = await client.GetAsync("/api/profiles/not-a-profile");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal("Invalid container profile id.", (await ReadProblemAsync(malformed)).GetProperty("title").GetString());
        using var unknown = await client.GetAsync("/api/profiles/prof-doesnotexist");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        using var revisionsList = await client.GetAsync($"/api/profiles/{seededId}/revisions");
        Assert.Equal(HttpStatusCode.OK, revisionsList.StatusCode);
        using var revisionsDocument = JsonDocument.Parse(await revisionsList.Content.ReadAsStringAsync());
        Assert.Equal(revision.GetProperty("id").GetString(), Assert.Single(revisionsDocument.RootElement.EnumerateArray()).GetProperty("id").GetString());
        using var malformedRevisions = await client.GetAsync("/api/profiles/not-a-profile/revisions");
        Assert.Equal(HttpStatusCode.BadRequest, malformedRevisions.StatusCode);
        using var unknownRevisions = await client.GetAsync("/api/profiles/prof-doesnotexist/revisions");
        Assert.Equal(HttpStatusCode.NotFound, unknownRevisions.StatusCode);
        using var revisionsWrongMethod = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/profiles/{seededId}/revisions"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, revisionsWrongMethod.StatusCode);
        Assert.Equal("GET, POST", string.Join(", ", revisionsWrongMethod.Content.Headers.Allow));

        var payload = new { idempotencyKey = "api-profile-1", slug = "api-team", displayName = "API Team", description = "Created through the API.", definition = """{"image":"agentcontrol-worker-base","name":"API Team"}""", dockerfileFragment = (string?)null };
        using var unauthenticated = _factory.CreateClient();
        using var unauthenticatedResponse = await unauthenticated.PostAsJsonAsync("/api/profiles", payload);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticatedResponse.StatusCode);

        using var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/profiles") { Content = JsonContent.Create(payload) };
        crossOrigin.Headers.Add("Origin", "https://other.example");
        using var crossOriginResponse = await client.SendAsync(crossOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, crossOriginResponse.StatusCode);

        using var createResponse = await client.SendAsync(Post("/api/profiles", payload));
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        using var createdDocument = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var created = createdDocument.RootElement;
        var id = created.GetProperty("id").GetString()!;
        var profileRevision = created.GetProperty("revision").GetInt32();
        Assert.Equal(1, created.GetProperty("currentRevisionNumber").GetInt32());

        using var replay = await client.SendAsync(Post("/api/profiles", payload, "api-profile-1"));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(id, replayDocument.RootElement.GetProperty("id").GetString());

        using var keyConflict = await client.SendAsync(Post("/api/profiles", payload with { displayName = "Changed" }, "api-profile-1"));
        Assert.Equal(HttpStatusCode.Conflict, keyConflict.StatusCode);
        using var slugConflict = await client.SendAsync(Post("/api/profiles", payload with { idempotencyKey = "api-profile-2" }));
        Assert.Equal(HttpStatusCode.Conflict, slugConflict.StatusCode);
        Assert.Contains("already exists", (await ReadProblemAsync(slugConflict)).GetProperty("detail").GetString(), StringComparison.Ordinal);

        using var forbiddenKey = await client.SendAsync(Post("/api/profiles", payload with { idempotencyKey = "api-profile-3", slug = "api-priv", definition = """{"image":"agentcontrol-worker-base","privileged":true}""" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, forbiddenKey.StatusCode);
        Assert.Contains("'privileged' is not allowed", (await ReadProblemAsync(forbiddenKey)).GetProperty("detail").GetString(), StringComparison.Ordinal);

        using var fragmentRevision = await client.SendAsync(Post($"/api/profiles/{id}/revisions", new { expectedProfileRevision = profileRevision, definition = """{"build":{"dockerfile":"Dockerfile"}}""", dockerfileFragment = "FROM agentcontrol-worker-base\nRUN true\n" }));
        Assert.Equal(HttpStatusCode.OK, fragmentRevision.StatusCode);
        using var revisionDocument = JsonDocument.Parse(await fragmentRevision.Content.ReadAsStringAsync());
        Assert.Equal(2, revisionDocument.RootElement.GetProperty("revisionNumber").GetInt32());
        using var chain = await client.GetAsync($"/api/profiles/{id}/revisions");
        using var chainDocument = JsonDocument.Parse(await chain.Content.ReadAsStringAsync());
        Assert.Equal([2, 1], chainDocument.RootElement.EnumerateArray().Select(r => r.GetProperty("revisionNumber").GetInt32()).ToArray());

        // The original create replays to the same profile after it gained a revision.
        using var lateReplay = await client.SendAsync(Post("/api/profiles", payload, "api-profile-1"));
        Assert.Equal(HttpStatusCode.OK, lateReplay.StatusCode);
        using var lateReplayDocument = JsonDocument.Parse(await lateReplay.Content.ReadAsStringAsync());
        Assert.Equal(id, lateReplayDocument.RootElement.GetProperty("id").GetString());
        Assert.Equal(2, lateReplayDocument.RootElement.GetProperty("currentRevisionNumber").GetInt32());

        using var staleRevision = await client.SendAsync(Post($"/api/profiles/{id}/revisions", new { expectedProfileRevision = profileRevision, definition = """{"image":"agentcontrol-worker-base","name":"stale"}""", dockerfileFragment = (string?)null }));
        Assert.Equal(HttpStatusCode.Conflict, staleRevision.StatusCode);
        using var badFragment = await client.SendAsync(Post($"/api/profiles/{id}/revisions", new { expectedProfileRevision = profileRevision + 1, definition = """{"build":{"dockerfile":"Dockerfile"}}""", dockerfileFragment = "FROM ubuntu\n" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badFragment.StatusCode);
        using var missingRevisionTarget = await client.SendAsync(Post("/api/profiles/prof-missing/revisions", new { expectedProfileRevision = 1, definition = """{"image":"agentcontrol-worker-base"}""", dockerfileFragment = (string?)null }));
        Assert.Equal(HttpStatusCode.NotFound, missingRevisionTarget.StatusCode);

        using var retireStale = await client.SendAsync(Post($"/api/profiles/{id}/retire", new { expectedRevision = profileRevision }));
        Assert.Equal(HttpStatusCode.Conflict, retireStale.StatusCode);
        using var retire = await client.SendAsync(Post($"/api/profiles/{id}/retire", new { expectedRevision = profileRevision + 1 }));
        Assert.Equal(HttpStatusCode.OK, retire.StatusCode);
        using var retiredDocument = JsonDocument.Parse(await retire.Content.ReadAsStringAsync());
        Assert.Equal(ContainerProfileStatuses.Retired, retiredDocument.RootElement.GetProperty("status").GetString());

        // Nothing in the profile slice creates employees, hosts or enrollments.
        using var organization = await client.GetAsync("/api/organization");
        using var organizationDocument = JsonDocument.Parse(await organization.Content.ReadAsStringAsync());
        Assert.Single(organizationDocument.RootElement.GetProperty("employees").EnumerateArray());
        using var hosts = await client.GetAsync("/api/execution-hosts");
        Assert.Equal(HttpStatusCode.OK, hosts.StatusCode);
        using var hostsDocument = JsonDocument.Parse(await hosts.Content.ReadAsStringAsync());
        // Only the reserved controller-local Docker row exists; no SSH host was
        // registered and the profile slice never creates an enrollment.
        var host = Assert.Single(hostsDocument.RootElement.EnumerateArray());
        Assert.Equal(ExecutionHosts.LocalDockerId, host.GetProperty("id").GetString());
        Assert.Equal("local-docker", host.GetProperty("transportKind").GetString());

        // Portal route contract: the pages exist for GET and the wrong method is a truthful 405.
        using var page = await client.GetAsync("/profiles");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("data-page=\"profiles\"", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var detailPage = await client.GetAsync($"/profiles/{id}");
        Assert.Equal(HttpStatusCode.OK, detailPage.StatusCode);
        Assert.Contains("data-page=\"profile-detail\"", await detailPage.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var wrongMethod = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/profiles"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        Assert.Equal("GET", wrongMethod.Content.Headers.Allow.Single());
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
/// The approve endpoint against a real runtime. WorkerControl gating is proven
/// with the disabled and deliberately-invalid factories, and the successful path
/// seeds the authoritative store host and a verified profile build directly (no
/// SSH or Docker) so the endpoint freezes a real selection, creates a real
/// managed employee and returns the joined request.
/// </summary>
[Collection(LocalPortBindingCollection.Name)]
public sealed class HireApprovalApiRuntimeTests : IClassFixture<WorkerControlValidRuntimeFactory>
{
    private readonly WorkerControlValidRuntimeFactory _valid;

    public HireApprovalApiRuntimeTests(WorkerControlValidRuntimeFactory valid)
    {
        _valid = valid;
    }

    [Fact]
    public async Task ApproveRejectsUnauthenticatedMalformedAndCrossOriginRequests()
    {
        using var client = await ReadyClientAsync(_valid);
        var id = await CreateDevHireAsync(client, "Approval Guard Hire");
        var origin = _valid.ClientOptions.BaseAddress.GetLeftPart(UriPartial.Authority);
        var body = new { expectedRevision = 1, profileRevisionId = "prev-0000000000000000", hostId = "host-a" };

        using var unauthenticated = _valid.CreateClient();
        using var unauthenticatedResponse = await unauthenticated.PostAsJsonAsync($"/api/hire-requests/{id}/approve", body);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticatedResponse.StatusCode);

        using var malformedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/hire-requests/not-a-hire-id/approve") { Content = JsonContent.Create(body) };
        malformedRequest.Headers.Add("Origin", origin);
        using var malformedResponse = await client.SendAsync(malformedRequest);
        Assert.Equal(HttpStatusCode.BadRequest, malformedResponse.StatusCode);
        Assert.Equal("Invalid hire request id.", (await ReadProblemAsync(malformedResponse)).GetProperty("title").GetString());

        using var crossOriginRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/hire-requests/{id}/approve") { Content = JsonContent.Create(body) };
        crossOriginRequest.Headers.Add("Origin", "https://other.example");
        using var crossOriginResponse = await client.SendAsync(crossOriginRequest);
        Assert.Equal(HttpStatusCode.Forbidden, crossOriginResponse.StatusCode);

        using var unknownRequest = new HttpRequestMessage(HttpMethod.Post, "/api/hire-requests/hire-0000000000000000/approve") { Content = JsonContent.Create(body) };
        unknownRequest.Headers.Add("Origin", origin);
        using var unknownResponse = await client.SendAsync(unknownRequest);
        Assert.Equal(HttpStatusCode.NotFound, unknownResponse.StatusCode);
    }

    [Fact]
    public async Task ApproveIs409WhenWorkerControlIsDisabled()
    {
        using var disabled = new EnabledRuntimeFactory();
        using var client = await ReadyClientAsync(disabled);
        var id = await CreateDevHireAsync(client, "Disabled Gate Hire");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/hire-requests/{id}/approve")
        {
            Content = JsonContent.Create(new { expectedRevision = 1, profileRevisionId = "prev-0000000000000000", hostId = "host-a" }),
        };
        request.Headers.Add("Origin", disabled.ClientOptions.BaseAddress.GetLeftPart(UriPartial.Authority));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Worker control is disabled.", (await ReadProblemAsync(response)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task ApproveIs409WhenWorkerControlConfigurationIsInvalid()
    {
        using var invalid = new WorkerControlInvalidConfigFactory();
        using var client = await ReadyClientAsync(invalid);
        var id = await CreateDevHireAsync(client, "Invalid Config Gate Hire");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/hire-requests/{id}/approve")
        {
            Content = JsonContent.Create(new { expectedRevision = 1, profileRevisionId = "prev-0000000000000000", hostId = "host-a" }),
        };
        request.Headers.Add("Origin", invalid.ClientOptions.BaseAddress.GetLeftPart(UriPartial.Authority));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Worker control configuration is invalid.", (await ReadProblemAsync(response)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task ApproveFreezesSelectionCreatesManagedEmployeeAndDurablyQueuesProvisioning()
    {
        using var client = await ReadyClientAsync(_valid);
        var store = _valid.Host.Organization!;
        var build = SeedVerifiedBuild(store);
        var id = await CreateDevHireAsync(client, "Approved API Developer");
        var revisionId = build.ProfileRevisionId;

        using var before = await client.GetAsync($"/api/hire-requests/{id}");
        using var beforeDocument = JsonDocument.Parse(await before.Content.ReadAsStringAsync());
        var expectedRevision = beforeDocument.RootElement.GetProperty("revision").GetInt32();
        Assert.Equal(HireRequestStates.Requested, beforeDocument.RootElement.GetProperty("state").GetString());

        // A caller-supplied hostId is an unknown JSON property the endpoint never
        // reads: placement is always the controller-local Docker target.
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/hire-requests/{id}/approve")
        {
            Content = JsonContent.Create(new { expectedRevision, profileRevisionId = revisionId, hostId = "host-a" }),
        };
        request.Headers.Add("Origin", _valid.ClientOptions.BaseAddress.GetLeftPart(UriPartial.Authority));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var approved = document.RootElement;
        Assert.Equal(HireRequestStates.Provisioning, approved.GetProperty("state").GetString());
        Assert.Equal(revisionId, approved.GetProperty("containerProfileRevisionId").GetString());
        Assert.Equal(build.Id, approved.GetProperty("profileBuildId").GetString());
        Assert.Equal(build.ImageDigest, approved.GetProperty("approvedImageDigest").GetString());
        Assert.Equal(ExecutionHosts.LocalDockerId, approved.GetProperty("approvedHostId").GetString());
        var employeeId = approved.GetProperty("employeeId").GetString();
        var bindingId = approved.GetProperty("runtimeBindingId").GetString();
        Assert.StartsWith("emp-", employeeId, StringComparison.Ordinal);
        Assert.StartsWith("rtb-", bindingId, StringComparison.Ordinal);
        Assert.True(approved.GetProperty("workerId").ValueKind is JsonValueKind.Null);

        // The approval identity is host-derived and never echoed from the body;
        // the immutable record carries the fixed host value.
        var approval = store.GetHireRequestApproval(id)!;
        Assert.Equal(Program.HireApprovalIdentity, approval.ApprovalIdentity);
        Assert.Equal(employeeId, approval.EmployeeId);

        // The employee identity and frozen resources exist and the durable state is
        // queued before the HTTP response. The remote work remains asynchronous: no
        // enrollment or host effect is required for approval to return.
        Assert.Equal(HireRequestStates.Provisioning, store.GetHireRequest(id)!.State);
        Assert.Equal(1, CountRaw(store, "SELECT COUNT(*) FROM employees WHERE id = @id", employeeId!));
        Assert.Equal(1, CountRaw(store, "SELECT COUNT(*) FROM managed_enrollment_resources WHERE runtime_binding_id = @id", bindingId!));
        Assert.Equal(0, CountRaw(store, "SELECT COUNT(*) FROM worker_enrollments", null));
    }

    [Fact]
    public async Task EmployeeDetailIncludesAdditiveManagedProfileStatus()
    {
        // A dedicated runtime so creating a newer profile revision does not
        // disturb the class fixture's shared store used by the other tests.
        using var factory = new WorkerControlValidRuntimeFactory();
        using var client = await ReadyClientAsync(factory);
        var store = factory.Host.Organization!;
        var build = SeedVerifiedBuild(store);
        var id = await CreateDevHireAsync(client, "Profile Status Developer");

        using (var before = await client.GetAsync($"/api/hire-requests/{id}"))
        using (var beforeDocument = JsonDocument.Parse(await before.Content.ReadAsStringAsync()))
        {
            var expectedRevision = beforeDocument.RootElement.GetProperty("revision").GetInt32();
            using var approve = new HttpRequestMessage(HttpMethod.Post, $"/api/hire-requests/{id}/approve")
            {
                Content = JsonContent.Create(new { expectedRevision, profileRevisionId = build.ProfileRevisionId }),
            };
            approve.Headers.Add("Origin", factory.ClientOptions.BaseAddress.GetLeftPart(UriPartial.Authority));
            using var approved = await client.SendAsync(approve);
            Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        }

        var employeeId = store.GetHireRequestApproval(id)!.EmployeeId;
        Assert.NotNull(employeeId);

        // The additive projection reports the frozen revision/digest and no newer
        // target while the profile's current revision is the frozen one.
        using (var response = await client.GetAsync($"/api/employees/{employeeId}"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var status = document.RootElement.GetProperty("profileStatus");
            Assert.Equal(build.ProfileRevisionId, status.GetProperty("currentProfileRevisionId").GetString());
            Assert.Equal(1, status.GetProperty("currentRevisionNumber").GetInt32());
            Assert.Equal(build.ImageDigest, status.GetProperty("currentImageDigest").GetString());
            Assert.False(status.GetProperty("newerRevisionAvailable").GetBoolean());
            Assert.Equal(JsonValueKind.Null, status.GetProperty("newerRevisionNumber").ValueKind);
            Assert.Equal(JsonValueKind.Null, status.GetProperty("activeRebuildState").ValueKind);
        }

        // A newer revision with a verified build on the host becomes the offered
        // ready target; the existing detail fields are unchanged.
        var profile = store.ListContainerProfiles().Single(p => p.Slug == ContainerProfileSeed.GenericEmployeeSlug);
        var newerRevision = store.CreateContainerProfileRevision(profile.Id, new ContainerProfileRevisionCreate(
            store.GetContainerProfile(profile.Id)!.Profile.Revision,
            """{"image":"agentcontrol-worker-base","name":"API Newer Target"}""",
            null));
        var newerBuild = BuildVerifiedFor(store, newerRevision.Id);
        Assert.NotNull(newerBuild);
        using (var response = await client.GetAsync($"/api/employees/{employeeId}"))
        using (var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            var root = document.RootElement;
            Assert.Equal(employeeId, root.GetProperty("id").GetString());
            var status = root.GetProperty("profileStatus");
            // The frozen current revision is unchanged; only the newer target is added.
            Assert.Equal(build.ProfileRevisionId, status.GetProperty("currentProfileRevisionId").GetString());
            Assert.Equal(1, status.GetProperty("currentRevisionNumber").GetInt32());
            Assert.Equal(build.ImageDigest, status.GetProperty("currentImageDigest").GetString());
            Assert.True(status.GetProperty("newerRevisionAvailable").GetBoolean());
            Assert.Equal(2, status.GetProperty("newerRevisionNumber").GetInt32());
        }
    }

    private static ProfileBuildRecord BuildVerifiedFor(OrganizationStore store, string revisionId)
    {
        const string baseDigest = "sha256:" + "4444444444444444444444444444444444444444444444444444444444444444";
        const string builtDigest = "sha256:" + "5555555555555555555555555555555555555555555555555555555555555555";
        const string contextHash = "sha256:" + "6666666666666666666666666666666666666666666666666666666666666666";
        var queued = store.QueueProfileBuild(revisionId, ExecutionHosts.LocalDockerId, baseDigest, "linux/amd64", contextHash, "agentcontrol-profile:api-newer");
        var building = store.TransitionProfileBuild(queued.Id, queued.Revision, ProfileBuildStates.Building);
        var verifying = store.TransitionProfileBuild(building.Id, building.Revision, ProfileBuildStates.Verifying);
        return store.TransitionProfileBuild(verifying.Id, verifying.Revision, ProfileBuildStates.Built, imageDigest: builtDigest, verified: true, evidenceHash: contextHash);
    }

    private static async Task<HttpClient> ReadyClientAsync(EnabledRuntimeFactory factory)
    {
        var client = factory.CreateClient();
        await factory.WaitForReadyAsync(TimeSpan.FromSeconds(45));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"owner:{EnabledRuntimeFactory.OwnerPassword}")));
        return client;
    }

    private static async Task<string> CreateDevHireAsync(HttpClient client, string displayName)
    {
        using var overviewResponse = await client.GetAsync("/api/organization");
        using var overviewDocument = JsonDocument.Parse(await overviewResponse.Content.ReadAsStringAsync());
        var overview = overviewDocument.RootElement;
        var department = overview.GetProperty("departments").EnumerateArray().Single(x => x.GetProperty("slug").GetString() == OrganizationSeed.OperationsSlug);
        var role = Assert.Single(overview.GetProperty("roles").EnumerateArray());
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/hire-requests")
        {
            Content = JsonContent.Create(new
            {
                idempotencyKey = "api-approve-" + Guid.NewGuid().ToString("N"),
                requestedDisplayName = displayName,
                purpose = "Exercise approval through the API.",
                departmentId = department.GetProperty("id").GetString(),
                roleId = role.GetProperty("id").GetString(),
                placement = RuntimePlacements.DeveloperContainer,
                cpuLimit = 2,
                memoryLimitMiB = 2048,
                pidsLimit = 256,
            }),
        };
        create.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        using var response = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static ProfileBuildRecord SeedVerifiedBuild(OrganizationStore store)
    {
        const string baseDigest = "sha256:" + "1111111111111111111111111111111111111111111111111111111111111111";
        const string builtDigest = "sha256:" + "2222222222222222222222222222222222222222222222222222222222222222";
        const string contextHash = "sha256:" + "3333333333333333333333333333333333333333333333333333333333333333";
        // Managed hiring freezes a verified build on the controller-local Docker
        // target, so the build is seeded there.
        var local = store.GetExecutionHost(ExecutionHosts.LocalDockerId)!;
        store.RecordLocalExecutionHostProbe(ExecutionHosts.LocalDockerId, local.Revision, new LocalExecutionHostProbe(
            "29.0", "1.51", "x86_64", "overlay2", "ext4", false, 64L << 30, 16L << 30, 8, true, "linux/amd64", "valid"));
        var profile = store.ListContainerProfiles().Single(p => p.Slug == ContainerProfileSeed.GenericEmployeeSlug);
        var revision = store.GetContainerProfile(profile.Id)!.Revisions.Single();
        var existing = store.GetVerifiedProfileBuild(revision.Id, ExecutionHosts.LocalDockerId);
        if (existing is not null) return existing;
        var queued = store.QueueProfileBuild(revision.Id, ExecutionHosts.LocalDockerId, baseDigest, "linux/amd64", contextHash, "agentcontrol-profile:x");
        var building = store.TransitionProfileBuild(queued.Id, queued.Revision, ProfileBuildStates.Building);
        var verifying = store.TransitionProfileBuild(building.Id, building.Revision, ProfileBuildStates.Verifying);
        return store.TransitionProfileBuild(verifying.Id, verifying.Revision, ProfileBuildStates.Built, imageDigest: builtDigest, verified: true, evidenceHash: contextHash);
    }

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static int CountRaw(OrganizationStore store, string sql, string? parameter)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = store.DatabasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (parameter is not null) command.Parameters.AddWithValue("@id", parameter);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Control and WorkerControl enabled with a usable configuration: temporary
/// controller-owned known_hosts and identity files and the effective test uid as
/// the expected controller uid. Contains no real credential bytes and performs no
/// host operation.
/// </summary>
public sealed class WorkerControlValidRuntimeFactory : EnabledRuntimeFactory
{
    private readonly string _workerRoot;

    public WorkerControlValidRuntimeFactory()
        : base("prompt_fast", workerControlEnabled: true)
    {
        _workerRoot = Path.Combine(Path.GetTempPath(), "agentcontrol-workercontrol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workerRoot);
        KnownHostsPath = Path.Combine(_workerRoot, "known_hosts");
        IdentityFilePath = Path.Combine(_workerRoot, "id");
        File.WriteAllText(KnownHostsPath, "host-a.example ssh-ed25519 AAAA\n");
        File.WriteAllText(IdentityFilePath, "not-a-real-key");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(KnownHostsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(IdentityFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public string KnownHostsPath { get; }

    public string IdentityFilePath { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // The base sets WorkerControl:Enabled because the invalid preset is
        // enabled; these settings replace the deliberately invalid defaults with
        // a usable configuration.
        base.ConfigureWebHost(builder);
        builder.UseSetting("WorkerControl:Enabled", "true");
        builder.UseSetting("WorkerControl:ControllerId", "controller-a");
        builder.UseSetting("WorkerControl:ApprovedImageDigest", "sha256:" + new string('4', 64));
        builder.UseSetting("WorkerControl:ApprovedImagePlatform", "linux/amd64");
        builder.UseSetting("WorkerControl:ExpectedControllerUid", HVO.AgentControl.RemoteWorker.ControllerPrivateFile.EffectiveUid.ToString(CultureInfo.InvariantCulture));
        builder.UseSetting("WorkerControl:ApprovedHosts:0:Id", "host-a");
        builder.UseSetting("WorkerControl:ApprovedHosts:0:Hostname", "host-a.example");
        builder.UseSetting("WorkerControl:ApprovedHosts:0:Port", "22");
        builder.UseSetting("WorkerControl:ApprovedHosts:0:Username", "roys");
        builder.UseSetting("WorkerControl:ApprovedHosts:0:KnownHostsPath", KnownHostsPath);
        builder.UseSetting("WorkerControl:ApprovedHosts:0:IdentityFilePath", IdentityFilePath);
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
            Directory.Delete(_workerRoot, recursive: true);
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
