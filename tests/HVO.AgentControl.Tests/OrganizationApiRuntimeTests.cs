using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Organization;
using HVO.AgentControl.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// The organization API against a real enabled runtime: the seeded SQLite store
/// is opened, the fake ACP process establishes the session, and HTTP requests
/// exercise the real endpoint pipeline (owner Basic auth, same-origin checks and
/// RFC 9457 ProblemDetails). No provider or credential is touched.
/// </summary>
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
public sealed class EnabledRuntimeFactory : WebApplicationFactory<Program>, IDisposable
{
    public const string OwnerPassword = "enabled-runtime-owner-password-000000";

    private readonly string _root;
    private readonly string _passwordPath;

    public EnabledRuntimeFactory()
    {
        _root = Path.Combine(Path.GetTempPath(), "agentcontrol-enabled-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataDirectory);
        _passwordPath = Path.Combine(_root, "owner-password");
        File.WriteAllText(_passwordPath, OwnerPassword);
        OpenCodeExecutable = AcpFakeServer.CreateExecutable("prompt_fast");
        NativePort = GetFreePort();
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
            if (string.Equals(status.State, "ready", StringComparison.Ordinal))
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
