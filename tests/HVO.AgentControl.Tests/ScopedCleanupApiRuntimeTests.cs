using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Organization;
using HVO.AgentControl.RemoteWorker;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// The scoped-cleanup HTTP contract against a real runtime. WorkerControl is
/// enabled with a usable configuration and the transport is a recording fake, so
/// no SSH or Docker is involved. Seeding uses the authoritative store directly.
/// </summary>
[Collection(LocalPortBindingCollection.Name)]
public sealed class ScopedCleanupApiRuntimeTests : IClassFixture<ScopedCleanupRuntimeFactory>
{
    private const string BaseDigest = "sha256:" + "1111111111111111111111111111111111111111111111111111111111111111";
    private const string ContextHash = "sha256:" + "3333333333333333333333333333333333333333333333333333333333333333";

    private readonly ScopedCleanupRuntimeFactory _factory;

    public ScopedCleanupApiRuntimeTests(ScopedCleanupRuntimeFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RemoveEndpointIsOwnerOnlySameOriginAndRevisionBound()
    {
        using var client = await ReadyClientAsync();
        var store = _factory.Host.Organization!;
        var (profileId, revisionId, build) = SeedFailedBuild(store);
        var origin = _factory.ClientOptions.BaseAddress.GetLeftPart(UriPartial.Authority);
        _factory.Provisioner.Effects.Clear();

        // Auth: no owner credentials -> 401.
        using (var unauthenticated = _factory.CreateClient())
        using (var response = await unauthenticated.PostAsync(RemovePath(profileId, revisionId, build.Id), null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // Same origin: a foreign Origin is rejected before the store is touched.
        using (var request = new HttpRequestMessage(HttpMethod.Post, RemovePath(profileId, revisionId, build.Id)))
        {
            request.Headers.Add("Origin", "https://other.example");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        // Malformed build id -> 400.
        using (var malformed = await SendAsync(client, RemovePath(profileId, revisionId, "not-a-build")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        }

        // Unknown build id -> 404.
        using (var unknown = await SendAsync(client, RemovePath(profileId, revisionId, "pbld-0000000000000000")))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.Equal("Profile build not found.", (await ReadProblemAsync(unknown)).GetProperty("title").GetString());
        }

        // Unknown revision -> 404.
        using (var unknownRevision = await SendAsync(client, RemovePath(profileId, "prev-0000000000000000", build.Id)))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknownRevision.StatusCode);
        }
    }

    [Fact]
    public async Task RemoveEndpointRemovesAFailedBuildAndReturnsTheUpdatedRow()
    {
        using var client = await ReadyClientAsync();
        var store = _factory.Host.Organization!;
        var (profileId, revisionId, build) = SeedFailedBuild(store);
        _factory.Provisioner.Effects.Clear();

        using var response = await SendAsync(client, RemovePath(profileId, revisionId, build.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ProfileBuildStates.Removed, document.RootElement.GetProperty("state").GetString());
        Assert.Equal(build.Id, document.RootElement.GetProperty("id").GetString());
        Assert.Contains("remove-image:" + build.ResultTag, _factory.Provisioner.Effects);
        Assert.Contains("remove-image:" + build.ImageDigest, _factory.Provisioner.Effects);
    }

    [Fact]
    public async Task RemoveEndpointRejectsALiveBuildWith422AndAnUncertainBuildWith409()
    {
        using var client = await ReadyClientAsync();
        var store = _factory.Host.Organization!;
        var live = SeedFailedBuild(store, state: "live");
        _factory.Provisioner.Effects.Clear();

        // A live build cannot be removed: it is a conflict, never a removal.
        using (var response = await SendAsync(client, RemovePath(live.ProfileId, live.RevisionId, live.Build.Id)))
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        Assert.Empty(_factory.Provisioner.Effects);

        var uncertain = SeedFailedBuild(store, state: "uncertain");
        using (var response = await SendAsync(client, RemovePath(uncertain.ProfileId, uncertain.RevisionId, uncertain.Build.Id)))
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var problem = await ReadProblemAsync(response);
            Assert.Equal("Remote worker recovery is required.", problem.GetProperty("title").GetString());
            Assert.Contains("profile-build-uncertain", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        }
        Assert.Empty(_factory.Provisioner.Effects);
    }

    [Fact]
    public async Task RemoveEndpointRefusesAnImageInUseWith422()
    {
        using var client = await ReadyClientAsync();
        var store = _factory.Host.Organization!;
        var build = SeedFailedBuild(store);
        // An enrollment expecting exactly this build's digest makes it in use.
        var bindingId = store.GetOverview().Employees.Single(x => x.RuntimeBindingId is not null).RuntimeBindingId!;
        SeedEnrollment(store.DatabasePath, bindingId, "wrk-inuse-" + Guid.NewGuid().ToString("N")[..8], build.Build.ImageDigest!);
        _factory.Provisioner.Effects.Clear();

        using var response = await SendAsync(client, RemovePath(build.ProfileId, build.RevisionId, build.Build.Id));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("enrollment", (await ReadProblemAsync(response)).GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Empty(_factory.Provisioner.Effects);
        Assert.Equal(ProfileBuildStates.Failed, store.GetProfileBuild(build.Build.Id)!.State);
    }

    [Fact]
    public async Task RemoveEndpointSurfacesRecoveryWhenTheTransportFails()
    {
        using var client = await ReadyClientAsync();
        var store = _factory.Host.Organization!;
        var (profileId, revisionId, build) = SeedFailedBuild(store);
        _factory.Provisioner.Effects.Clear();
        _factory.Provisioner.RemovalTransportLoss = true;

        try
        {
            using var response = await SendAsync(client, RemovePath(profileId, revisionId, build.Id));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("Remote worker recovery is required.", (await ReadProblemAsync(response)).GetProperty("title").GetString());
            Assert.Equal(ProfileBuildStates.Failed, store.GetProfileBuild(build.Id)!.State);
        }
        finally
        {
            _factory.Provisioner.RemovalTransportLoss = false;
        }
    }

    [Fact]
    public async Task WorkerCleanupRefusesDuringAnActiveRebuild()
    {
        using var client = await ReadyClientAsync();
        var store = _factory.Host.Organization!;
        var workerId = "wrk-api-cleanup";
        var (build, enrollmentDigest) = SeedManagedRebuildContext(store, workerId);
        _factory.Provisioner.Effects.Clear();

        // An active rebuild holds the worker, so cleanup is refused before any
        // resource is inspected.
        _ = store.BeginEmployeeRebuild(new EmployeeRebuildCreate(
            build.EmployeeId, build.BindingId, workerId, ExecutionHosts.LocalDockerId,
            build.FromRevisionId, enrollmentDigest, 1, build.ToRevisionId, build.ToBuildId, build.ToDigest, "linux/amd64",
            false, false, null, 0));

        using (var refused = await SendAsync(client, $"/api/workers/{workerId}/cleanup"))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("Remote worker request conflicted.", (await ReadProblemAsync(refused)).GetProperty("title").GetString());
            Assert.Empty(_factory.Provisioner.Effects);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static string RemovePath(string profileId, string revisionId, string buildId) =>
        $"/api/profiles/{profileId}/revisions/{revisionId}/builds/{buildId}/remove";

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        return await client.SendAsync(request);
    }

    private async Task<HttpClient> ReadyClientAsync()
    {
        var client = _factory.CreateClient();
        await _factory.WaitForReadyAsync(TimeSpan.FromSeconds(45));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"owner:{EnabledRuntimeFactory.OwnerPassword}")));
        return client;
    }

    private static (string ProfileId, string RevisionId, ProfileBuildRecord Build) SeedFailedBuild(OrganizationStore store, string? digest = null, string state = "failed")
    {
        EnsureLocalHostReady(store);
        // A fresh single-revision profile per seed keeps the per-(revision, host)
        // live slot from colliding across the live/uncertain/failed cases.
        var profile = store.CreateContainerProfile(new ContainerProfileCreate(
            "cleanup-api-" + Guid.NewGuid().ToString("N"),
            "cleanup-api-" + Guid.NewGuid().ToString("N")[..8],
            "Cleanup API",
            "Failed-build removal fixtures.",
            """{"image":"agentcontrol-worker-base","name":"Cleanup API"}""",
            null), null);
        var revision = store.GetContainerProfile(profile.Id)!.Revisions.Single();
        var tag = "agentcontrol-profile:prev-api-" + Guid.NewGuid().ToString("N")[..8];
        var queued = store.QueueProfileBuild(revision.Id, ExecutionHosts.LocalDockerId, BaseDigest, "linux/amd64", ContextHash, tag);
        if (state == "live")
        {
            _ = store.TransitionProfileBuild(queued.Id, queued.Revision, ProfileBuildStates.Building);
            return (profile.Id, revision.Id, store.GetProfileBuild(queued.Id)!);
        }

        if (state == "uncertain")
        {
            var building = store.TransitionProfileBuild(queued.Id, queued.Revision, ProfileBuildStates.Building);
            var uncertain = store.TransitionProfileBuild(building.Id, building.Revision, ProfileBuildStates.Uncertain, failureSummary: "lost");
            return (profile.Id, revision.Id, uncertain);
        }

        // The remove endpoint needs a terminal failed row, not a simulated build
        // pipeline. Use the shortest valid transition so the optional startup
        // reconciliation service cannot observe and reclassify a transient
        // Building/Verifying row while this shared runtime fixture is seeding it.
        var failed = store.TransitionProfileBuild(queued.Id, queued.Revision, ProfileBuildStates.Failed, imageDigest: digest ?? NewDigest(), failureSummary: "failed");
        return (profile.Id, revision.Id, failed);
    }

    private static void EnsureLocalHostReady(OrganizationStore store)
    {
        var host = store.GetExecutionHost(ExecutionHosts.LocalDockerId)!;
        if (string.Equals(host.Status, "ready", StringComparison.Ordinal)) return;
        store.RecordLocalExecutionHostProbe(ExecutionHosts.LocalDockerId, host.Revision, new LocalExecutionHostProbe(
            "29.0", "1.51", "x86_64", "overlay2", "ext4", false, 64L << 30, 16L << 30, 8, true, "linux/amd64", "valid"));
    }

    private static int _digestCounter;

    /// <summary>A unique well-formed digest so one test's in-use image cannot refuse another test's removal.</summary>
    private static string NewDigest()
    {
        var value = (++_digestCounter).ToString("x", CultureInfo.InvariantCulture);
        return "sha256:" + value.PadLeft(64, '0');
    }

    private sealed record RebuildContext(string EmployeeId, string BindingId, string FromRevisionId, string ToRevisionId, string ToBuildId, string ToDigest);

    private static (RebuildContext Context, string EnrollmentDigest) SeedManagedRebuildContext(OrganizationStore store, string workerId)
    {
        const string fromDigest = "sha256:" + "4444444444444444444444444444444444444444444444444444444444444444";
        const string toDigest = "sha256:" + "5555555555555555555555555555555555555555555555555555555555555555";
        EnsureLocalHostReady(store);
        var profile = store.ListContainerProfiles().Single(p => p.Slug == ContainerProfileSeed.GenericEmployeeSlug);
        var fromRevision = store.GetContainerProfile(profile.Id)!.Revisions.Single(r => r.RevisionNumber == 1);
        var toRevision = store.CreateContainerProfileRevision(profile.Id, new ContainerProfileRevisionCreate(
            store.GetContainerProfile(profile.Id)!.Profile.Revision,
            """{"image":"agentcontrol-worker-base","name":"API cleanup target"}""",
            null));
        var toBuild = BuildVerified(store, toRevision.Id, toDigest);
        var overview = store.GetOverview();
        var department = overview.Departments.Single(d => d.Slug == OrganizationSeed.OperationsSlug);
        var role = Assert.Single(overview.Roles);
        var hire = store.CreateHireRequest(new HireRequestCreate(
            Guid.NewGuid().ToString("N"), "API Cleanup Worker", "Active-rebuild cleanup guard.", department.Id, role.Id,
            RuntimePlacements.DeveloperContainer, 2, 2048, 256), null);
        store.ApproveHireRequest(hire.Id, new HireRequestApprove(hire.Revision, toRevision.Id), "owner");
        var creation = store.CreateManagedEmployeeFromHire(hire.Id);
        SeedEnrollment(store.DatabasePath, creation.RuntimeBindingId, workerId, fromDigest);
        return (new RebuildContext(creation.EmployeeId, creation.RuntimeBindingId, fromRevision.Id, toRevision.Id, toBuild.Id, toDigest), fromDigest);
    }

    private static ProfileBuildRecord BuildVerified(OrganizationStore store, string revisionId, string imageDigest)
    {
        var queued = store.QueueProfileBuild(revisionId, ExecutionHosts.LocalDockerId, BaseDigest, "linux/amd64", ContextHash, "agentcontrol-profile:prev-api-target");
        var building = store.TransitionProfileBuild(queued.Id, queued.Revision, ProfileBuildStates.Building);
        var verifying = store.TransitionProfileBuild(building.Id, building.Revision, ProfileBuildStates.Verifying);
        return store.TransitionProfileBuild(verifying.Id, verifying.Revision, ProfileBuildStates.Built, imageDigest: imageDigest, verified: true, evidenceHash: ContextHash);
    }

    private static void SeedEnrollment(string databasePath, string bindingId, string workerId, string digest)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var keyPath = Path.Combine(Path.GetDirectoryName(databasePath)!, "worker.key").Replace("'", "''", StringComparison.Ordinal);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            INSERT INTO worker_enrollments (
                worker_id, runtime_binding_id, host_id, organization_id,
                container_name, control_volume_name, home_volume_name, workspace_volume_name, session_volume_name,
                resource_labels_hash, expected_image_digest, expected_platform, controller_id, key_file_path, key_id,
                bridge_socket_path, lifecycle_status, worker_generation, process_generation, ownership_epoch, enabled,
                created_at, updated_at, revision)
            VALUES ('{workerId}', '{bindingId}', '{ExecutionHosts.LocalDockerId}', (SELECT id FROM organizations LIMIT 1),
                'container-{workerId}', 'control-{workerId}', 'home-{workerId}', 'workspace-{workerId}', 'session-{workerId}',
                'sha256:{new string('a', 64)}', '{digest}', 'linux/amd64', 'controller-a', '{keyPath}',
                'sha256:{new string('c', 64)}', '/control/bridge.sock', 'enrolled', 0, 0, 0, 1, '{now}', '{now}', 1)
            """;
        command.ExecuteNonQuery();
    }

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}

/// <summary>
/// WorkerControl enabled with a usable configuration, a disposable owner
/// password, and the transport replaced by a recording fake so no SSH or Docker
/// operation runs.
/// </summary>
public sealed class ScopedCleanupRuntimeFactory : EnabledRuntimeFactory
{
    private readonly string _workerRoot;

    public ScopedCleanupRuntimeFactory()
        : base("prompt_fast", workerControlEnabled: true)
    {
        _workerRoot = Path.Combine(Path.GetTempPath(), "agentcontrol-cleanup-api-" + Guid.NewGuid().ToString("N"));
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
    public RecordingCleanupProvisioner Provisioner { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("WorkerControl:Enabled", "true");
        builder.UseSetting("WorkerControl:ControllerId", "controller-a");
        builder.UseSetting("WorkerControl:ApprovedImageDigest", "sha256:" + new string('4', 64));
        builder.UseSetting("WorkerControl:ApprovedImagePlatform", "linux/amd64");
        builder.UseSetting("WorkerControl:ExpectedControllerUid", ControllerPrivateFile.EffectiveUid.ToString(CultureInfo.InvariantCulture));
        builder.UseSetting("WorkerControl:ApprovedHosts:0:Id", "host-a");
        builder.UseSetting("WorkerControl:ApprovedHosts:0:Hostname", "host-a.example");
        builder.UseSetting("WorkerControl:ApprovedHosts:0:Port", "22");
        builder.UseSetting("WorkerControl:ApprovedHosts:0:Username", "roys");
        builder.UseSetting("WorkerControl:ApprovedHosts:0:KnownHostsPath", KnownHostsPath);
        builder.UseSetting("WorkerControl:ApprovedHosts:0:IdentityFilePath", IdentityFilePath);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IRemoteWorkerProvisioner>();
            services.AddSingleton<IRemoteWorkerProvisioner>(Provisioner);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try { Directory.Delete(_workerRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>A recording provisioner that succeeds at remove-image and never reaches a host.</summary>
public sealed class RecordingCleanupProvisioner : IRemoteWorkerProvisioner
{
    public List<string> Effects { get; } = [];
    public bool RemovalTransportLoss { get; set; }

    public Task<HostProbePayload> ProbeAsync(ExecutionTarget target, CancellationToken token) => throw new NotSupportedException();
    public Task<string> CreateVolumeAsync(ExecutionTarget target, VolumeCreateSpec spec, CancellationToken token) => throw new NotSupportedException();
    public Task<string> CreateContainerAsync(ExecutionTarget target, ContainerCreateSpec spec, CancellationToken token) => throw new NotSupportedException();
    public Task BootstrapAsync(ExecutionTarget target, BootstrapSpec spec, byte[] key, CancellationToken token) => throw new NotSupportedException();
    public Task StartAsync(ExecutionTarget target, string container, CancellationToken token) => throw new NotSupportedException();
    public Task StopAsync(ExecutionTarget target, string container, CancellationToken token) => throw new NotSupportedException();
    public Task RemoveContainerAsync(ExecutionTarget target, string container, CancellationToken token) { Effects.Add("remove-container:" + container); return Task.CompletedTask; }
    public Task RemoveVolumeAsync(ExecutionTarget target, string volume, CancellationToken token) { Effects.Add("remove-volume:" + volume); return Task.CompletedTask; }

    public Task RemoveImageAsync(ExecutionTarget target, string imageReference, CancellationToken token)
    {
        Effects.Add("remove-image:" + imageReference);
        if (RemovalTransportLoss) throw new RemoteWorkerUnavailableException("injected transport loss", transport: true);
        return Task.CompletedTask;
    }

    public Task<string?> InspectImageAsync(ExecutionTarget target, string imageReference, CancellationToken token)
    {
        if (RemovalTransportLoss) throw new RemoteWorkerUnavailableException("injected transport loss", transport: true);
        return Task.FromResult(imageReference.StartsWith("sha256:", StringComparison.Ordinal) ? imageReference : null);
    }

    public Task<RemoteResourceInspection> InspectVolumeAsync(ExecutionTarget target, string name, CancellationToken token) => Task.FromResult(new RemoteResourceInspection(false, null, new Dictionary<string, string>(), "absent"));
    public Task<RemoteResourceInspection> InspectContainerAsync(ExecutionTarget target, string name, CancellationToken token) => Task.FromResult(new RemoteResourceInspection(false, null, new Dictionary<string, string>(), "absent"));
}
