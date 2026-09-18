using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Organization;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Orientation, instruction and permission endpoints against the real enabled
/// runtime pipeline: owner Basic auth, same-origin enforcement, RFC 9457
/// ProblemDetails mapping and the no-rebuild update guarantee. No provider
/// credential or live inference is involved.
/// </summary>
[Collection(LocalPortBindingCollection.Name)]
public sealed class OrientationApiRuntimeTests : IClassFixture<EnabledRuntimeFactory>
{
    private readonly EnabledRuntimeFactory _factory;

    public OrientationApiRuntimeTests(EnabledRuntimeFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task OrientationStatusRequiresOwnerAuthentication()
    {
        await _factory.WaitForReadyAsync(TimeSpan.FromSeconds(45));
        using var anonymous = _factory.CreateClient();

        using var response = await anonymous.GetAsync("/api/orientation");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(401, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task OrientationStatusReportsPersistedAssignmentMetadata()
    {
        using var client = await CreateReadyClientAsync();

        using var response = await client.GetAsync("/api/orientation");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("orientation-current.md", root.GetProperty("artifactFileName").GetString());
        Assert.True(root.GetProperty("artifactBytes").GetInt64() > 0);
        Assert.Equal(OrganizationSeed.HostPolicyVersion, root.GetProperty("policyVersion").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("orientationVersion").GetString()));
        Assert.True(root.TryGetProperty("restartRequired", out _));
        Assert.True(root.TryGetProperty("requiredRuntimeGeneration", out _));
        Assert.True(root.TryGetProperty("loadedRuntimeGeneration", out _));
    }

    [Fact]
    public async Task OrientationMutationsRejectCrossOriginRequests()
    {
        using var client = await CreateReadyClientAsync();

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Post, "/api/orientation/deliver"),
            (HttpMethod.Put, "/api/orientation/manual-hold"),
            (HttpMethod.Put, "/api/roles/role-cross-origin/instructions"),
            (HttpMethod.Post, "/api/permissions/grants"),
        })
        {
            using var request = new HttpRequestMessage(method, path)
            {
                Content = JsonContent.Create(new { held = true, detail = "cross origin" }),
            };
            request.Headers.Add("Origin", "https://attacker.example");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task ManualHoldTogglesAndDeliverConvergesUnderParallelCalls()
    {
        using var client = await CreateReadyClientAsync();

        using (var hold = SameOrigin(HttpMethod.Put, "/api/orientation/manual-hold", new { held = true, detail = "api test" }))
        using (var response = await client.SendAsync(hold))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains(
                DispatchHoldReasons.Manual,
                document.RootElement.GetProperty("holdReasons").EnumerateArray().Select(value => value.GetString()));
        }

        // Concurrent deliveries must serialize on the host and converge instead of
        // racing the artifact publication or the assignment transition.
        var deliveries = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            using var request = SameOrigin(HttpMethod.Post, "/api/orientation/deliver", new { });
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }));
        Assert.All(deliveries, status => Assert.Equal(HttpStatusCode.OK, status));

        using var status = await client.GetAsync("/api/orientation");
        using var statusDocument = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        Assert.Equal(OrientationStates.Delivered, statusDocument.RootElement.GetProperty("state").GetString());

        using var clear = SameOrigin(HttpMethod.Put, "/api/orientation/manual-hold", new { held = false, detail = (string?)null });
        using var cleared = await client.SendAsync(clear);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
    }

    [Fact]
    public async Task InvalidEvidenceIs409AndUnknownGrantTargetsAre404()
    {
        using var client = await CreateReadyClientAsync();
        var employeeId = _factory.Host.OrganizationIdentity!.EmployeeId;

        using (var evidence = SameOrigin(HttpMethod.Post, "/api/orientation/comprehension", new
        {
            assignmentId = "ora-not-current",
            employeeId,
            sessionId = "ses-not-current",
            orientationVersion = "0000",
            identity = "x",
            department = "y",
            reporting = "owner",
            duties = new[] { "z" },
            restrictions = new[] { "z" },
            escalation = "z",
            expectedRevision = 99,
        }))
        using (var response = await client.SendAsync(evidence))
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var problem = await ReadProblemAsync(response);
            Assert.Equal(409, problem.GetProperty("status").GetInt32());
        }

        using (var grant = SameOrigin(HttpMethod.Post, "/api/permissions/grants", new
        {
            employeeId,
            restrictionId = "rst-does-not-exist",
            tool = "read",
            resource = "diagnostic:public",
            expiresAt = DateTimeOffset.UtcNow.AddHours(1),
            policyRevision = 1,
            idempotencyKey = "missing-restriction",
        }))
        using (var response = await client.SendAsync(grant))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using (var revoke = SameOrigin(HttpMethod.Post, "/api/permissions/grants/grant-missing/revoke", new { expectedRevision = 1 }))
        using (var response = await client.SendAsync(revoke))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("duties")]
    public async Task NullEvidenceFieldsReturnProblemDetails400WithoutChangingDeliveredState(string field)
    {
        using var client = await CreateReadyClientAsync();
        using var delivery = SameOrigin(HttpMethod.Post, "/api/orientation/deliver", new { });
        using var deliveredResponse = await client.SendAsync(delivery);
        Assert.Equal(HttpStatusCode.OK, deliveredResponse.StatusCode);
        var status = _factory.Host.GetOrientationStatus();

        var body = new Dictionary<string, object?>
        {
            ["assignmentId"] = status.AssignmentId,
            ["employeeId"] = status.EmployeeId,
            ["sessionId"] = status.SessionId,
            ["orientationVersion"] = status.OrientationVersion,
            ["identity"] = OrganizationSeed.AdoptedEmployeeDisplayName,
            ["department"] = OrganizationSeed.OperationsDisplayName,
            ["reporting"] = "owner",
            ["duties"] = new[] { "operate and maintain the control host" },
            ["restrictions"] = new[] { "no secrets or controller-private state" },
            ["escalation"] = "escalate uncertainty",
            ["expectedRevision"] = status.Revision,
        };
        body[field] = null;

        using var evidence = SameOrigin(HttpMethod.Post, "/api/orientation/comprehension", body);
        using var response = await client.SendAsync(evidence);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Equal(OrientationStates.Delivered, _factory.Host.GetOrientationStatus().State);
    }

    [Fact]
    public async Task StaleOrientationRemainsReadableAndOperableUntilFreshDelivery()
    {
        using var client = await CreateReadyClientAsync();
        var host = _factory.Host;
        var before = host.GetOrientationStatus();
        var overview = host.Organization!.GetOverview();

        using (var rename = SameOrigin(HttpMethod.Patch, "/api/organization", new
        {
            organizationId = overview.Id,
            displayName = "Renamed AgentControl Development",
            revision = overview.Revision,
        }))
        using (var response = await client.SendAsync(rename))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var response = await client.GetAsync("/api/orientation"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(OrientationStates.Stale, document.RootElement.GetProperty("state").GetString());
            Assert.False(document.RootElement.GetProperty("ready").GetBoolean());
            Assert.Equal(before.AssignmentId, document.RootElement.GetProperty("assignmentId").GetString());
        }

        using (var hold = SameOrigin(HttpMethod.Put, "/api/orientation/manual-hold", new { held = true, detail = "while stale" }))
        using (var response = await client.SendAsync(hold))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var deliver = SameOrigin(HttpMethod.Post, "/api/orientation/deliver", new { }))
        using (var response = await client.SendAsync(deliver))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(OrientationStates.Delivered, document.RootElement.GetProperty("state").GetString());
            Assert.NotEqual(before.AssignmentId, document.RootElement.GetProperty("assignmentId").GetString());
            Assert.NotEqual(before.OrientationVersion, document.RootElement.GetProperty("orientationVersion").GetString());
        }
    }

    [Fact]
    public async Task MalformedOwnerSubmittedEvidenceIsBadRequestWithoutMutation()
    {
        using var factory = new EnabledRuntimeFactory("orientation_fast");
        await factory.WaitForReadyAsync(TimeSpan.FromSeconds(45));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"owner:{EnabledRuntimeFactory.OwnerPassword}")));
        var before = factory.Host.GetOrientationStatus();

        using var request = SameOrigin(HttpMethod.Post, "/api/orientation/comprehension", new { });
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var after = factory.Host.GetOrientationStatus();
        Assert.Equal(before.AssignmentId, after.AssignmentId);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.State, after.State);
    }

    [Fact]
    public async Task MalformedLiveResponseEndpointPersistsFailureOutcome()
    {
        using var factory = new EnabledRuntimeFactory("orientation_malformed");
        await factory.WaitForReadyAsync(TimeSpan.FromSeconds(45));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"owner:{EnabledRuntimeFactory.OwnerPassword}")));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orientation/comprehension/run");
        request.Headers.Add("Origin", factory.ClientOptions.BaseAddress.GetLeftPart(UriPartial.Authority));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(OrientationStates.Failed, document.RootElement.GetProperty("state").GetString());
        Assert.Equal(OrientationEvidenceSources.LiveModel, document.RootElement.GetProperty("evidenceSource").GetString());
        Assert.Contains(
            DispatchHoldReasons.OrientationFailed,
            document.RootElement.GetProperty("holdReasons").EnumerateArray().Select(value => value.GetString()));
    }

    /// <summary>
    /// The startup bootstrap holds the serialized prompt slot. While that hold is
    /// observable as a busy session state, an explicit owner comprehension request
    /// is a deterministic 409 conflict rather than a race against bootstrap.
    /// </summary>
    [Fact]
    public async Task ComprehensionWhileBootstrapBusyReturnsConflict()
    {
        using var factory = new EnabledRuntimeFactory("orientation_gated_bootstrap");
        var host = factory.Host;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = host.GetStatus();
            if (string.Equals(status.State, "ready", StringComparison.Ordinal)
                && string.Equals(status.SessionState, "busy", StringComparison.Ordinal))
            {
                break;
            }

            await Task.Delay(50);
        }

        Assert.Equal("busy", host.GetStatus().SessionState);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"owner:{EnabledRuntimeFactory.OwnerPassword}")));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orientation/comprehension/run");
        request.Headers.Add("Origin", factory.ClientOptions.BaseAddress.GetLeftPart(UriPartial.Authority));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // Release the gated bootstrap so the runtime shuts down cleanly.
        File.WriteAllText(Path.Combine(factory.DataDirectory, "home", "bootstrap-release"), "release");
    }

    [Fact]
    public async Task NonWaivableGrantIs422AndStagedGrantRoundTrips()
    {
        using var client = await CreateReadyClientAsync();
        var employeeId = _factory.Host.OrganizationIdentity!.EmployeeId;

        using (var forbidden = SameOrigin(HttpMethod.Post, "/api/permissions/grants", new
        {
            employeeId,
            restrictionId = "rst-host-secrets",
            tool = "read",
            resource = "secret:key",
            expiresAt = DateTimeOffset.UtcNow.AddHours(1),
            policyRevision = 1,
            idempotencyKey = "non-waivable-attempt",
        }))
        using (var response = await client.SendAsync(forbidden))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }

        string grantId;
        int revision;
        using (var staged = SameOrigin(HttpMethod.Post, "/api/permissions/grants", new
        {
            employeeId,
            restrictionId = "rst-host-safe-diagnostic-example",
            tool = "read",
            resource = "diagnostic:public",
            expiresAt = DateTimeOffset.UtcNow.AddHours(1),
            policyRevision = 1,
            idempotencyKey = "staged-grant-api",
        }))
        using (var response = await client.SendAsync(staged))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            grantId = document.RootElement.GetProperty("id").GetString()!;
            revision = document.RootElement.GetProperty("revision").GetInt32();
        }

        using (var revoke = SameOrigin(HttpMethod.Post, $"/api/permissions/grants/{grantId}/revoke", new { expectedRevision = revision }))
        using (var response = await client.SendAsync(revoke))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var replay = SameOrigin(HttpMethod.Post, $"/api/permissions/grants/{grantId}/revoke", new { expectedRevision = revision }))
        using (var response = await client.SendAsync(replay))
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
    }

    /// <summary>
    /// A configuration change must update the database and published artifact in
    /// place: the same process, the same session and the same on-disk application
    /// binary, with only the orientation version and artifact advancing.
    /// </summary>
    [Fact]
    public async Task RoleInstructionApiUsesStableRoleRevisionAndRequiresRuntimeRestartAfterDelivery()
    {
        using var client = await CreateReadyClientAsync();
        var host = _factory.Host;
        var before = host.GetOrientationStatus();
        var overview = host.Organization!.GetOverview();
        var role = Assert.Single(overview.Roles);

        using (var update = SameOrigin(HttpMethod.Put, $"/api/roles/{role.Id}/instructions", new
        {
            standingInstructions = "Browser/API role instructions require explicit owner confirmation.",
            revision = role.Revision,
        }))
        using (var response = await client.SendAsync(update))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var updatedRole = Assert.Single(document.RootElement.GetProperty("roles").EnumerateArray().ToArray());
            Assert.Equal(role.Id, updatedRole.GetProperty("id").GetString());
            Assert.Equal(role.Revision + 1, updatedRole.GetProperty("revision").GetInt32());
        }

        using (var stale = await client.GetAsync("/api/orientation"))
        {
            using var document = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
            Assert.Equal(OrientationStates.Stale, document.RootElement.GetProperty("state").GetString());
            Assert.Contains(
                DispatchHoldReasons.PolicyUpdate,
                document.RootElement.GetProperty("holdReasons").EnumerateArray().Select(value => value.GetString()));
        }

        using (var deliver = SameOrigin(HttpMethod.Post, "/api/orientation/deliver", new { }))
        using (var response = await client.SendAsync(deliver))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(document.RootElement.GetProperty("restartRequired").GetBoolean());
            Assert.False(document.RootElement.GetProperty("ready").GetBoolean());
            Assert.NotEqual(before.AssignmentId, document.RootElement.GetProperty("assignmentId").GetString());
        }

        var delivered = host.GetOrientationStatus();
        using var comprehension = SameOrigin(HttpMethod.Post, "/api/orientation/comprehension", new
        {
            assignmentId = delivered.AssignmentId,
            employeeId = delivered.EmployeeId,
            sessionId = delivered.SessionId,
            orientationVersion = delivered.OrientationVersion,
            identity = OrganizationSeed.AdoptedEmployeeDisplayName,
            department = OrganizationSeed.OperationsDisplayName,
            reporting = "owner",
            duties = new[] { "operate and maintain the control host" },
            restrictions = new[] { "no secrets or controller-private state" },
            escalation = "escalate uncertainty",
            expectedRevision = delivered.Revision,
        });
        using var rejected = await client.SendAsync(comprehension);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
    }

    [Fact]
    public async Task BasicInstructionUpdateChangesOrientationWithoutRebuildOrRestart()
    {
        using var client = await CreateReadyClientAsync();
        var host = _factory.Host;

        var assemblyPath = typeof(Program).Assembly.Location;
        var binaryBefore = File.GetLastWriteTimeUtc(assemblyPath);
        var binaryHashBefore = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(assemblyPath)));
        var processBefore = Environment.ProcessId;
        var sessionBefore = host.GetStatus().SessionId;
        var versionBefore = host.GetOrientationStatus().OrientationVersion;
        var artifactPath = Path.Combine(_factory.DataDirectory, "home", "orientation-current.md");
        var artifactBefore = await File.ReadAllTextAsync(artifactPath);

        var overview = host.Organization!.GetOverview();
        using (var update = SameOrigin(HttpMethod.Put, "/api/organization/basic-instructions", new
        {
            organizationId = overview.Id,
            basicInstructions = "Updated standing instructions without an image rebuild.",
            revision = overview.Revision,
        }))
        using (var response = await client.SendAsync(update))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var deliver = SameOrigin(HttpMethod.Post, "/api/orientation/deliver", new { }))
        using (var response = await client.SendAsync(deliver))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var after = host.GetOrientationStatus();
        Assert.NotEqual(versionBefore, after.OrientationVersion);
        Assert.NotEqual(artifactBefore, await File.ReadAllTextAsync(artifactPath));
        Assert.Contains("Updated standing instructions without an image rebuild.", await File.ReadAllTextAsync(artifactPath), StringComparison.Ordinal);

        // Same process, same owned session, byte-identical application binary.
        Assert.Equal(processBefore, Environment.ProcessId);
        Assert.Equal(sessionBefore, host.GetStatus().SessionId);
        Assert.Equal(binaryBefore, File.GetLastWriteTimeUtc(assemblyPath));
        Assert.Equal(
            binaryHashBefore,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(assemblyPath))));
    }

    private async Task<HttpClient> CreateReadyClientAsync()
    {
        var client = _factory.CreateClient();
        await _factory.WaitForReadyAsync(TimeSpan.FromSeconds(45));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"owner:{EnabledRuntimeFactory.OwnerPassword}")));
        return client;
    }

    private HttpRequestMessage SameOrigin(HttpMethod method, string path, object body)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Origin", _factory.ClientOptions.BaseAddress.GetLeftPart(UriPartial.Authority));
        return request;
    }

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
