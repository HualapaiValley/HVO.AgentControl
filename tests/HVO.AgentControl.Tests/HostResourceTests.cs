using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class HostResourceTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public async Task ExecutorBearerIsSeparatedFromOwnerCookieAndCsrf()
    {
        await using var app = new TestApp();
        var executor = await Enroll(app, activate: false);
        var activation = Activation(executor);
        using var anonymous = app.CreateClient();
        using var anonymousResponse = await anonymous.PostAsJsonAsync("/api/v1/host-executor/activate", activation);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        Assert.Equal("Bearer", anonymousResponse.Headers.WwwAuthenticate.Single().Scheme);

        using var owner = await app.SignIn();
        using var ownerMachineResponse = await owner.PostAsJsonAsync("/api/v1/host-executor/activate", activation);
        Assert.Equal(HttpStatusCode.Unauthorized, ownerMachineResponse.StatusCode);

        using var pendingMachine = MachineClient(app, executor);
        using var pendingObservation = await pendingMachine.PostAsJsonAsync("/api/v1/host-executor/observations", Observation(executor, 1));
        Assert.Equal(HttpStatusCode.Forbidden, pendingObservation.StatusCode);
        using var activated = await pendingMachine.PostAsJsonAsync("/api/v1/host-executor/activate", activation);
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        var body = await activated.Content.ReadAsStringAsync();
        Assert.DoesNotContain(executor.Secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(executor.CredentialDigest, body, StringComparison.OrdinalIgnoreCase);

        using var machineOwnerResponse = await pendingMachine.GetAsync("/api/v1/host-executors");
        Assert.Equal(HttpStatusCode.Unauthorized, machineOwnerResponse.StatusCode);
        using var badMachine = app.CreateClient();
        badMachine.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", executor.Enrollment.Id + "." + Secret(Id()));
        using var badResponse = await badMachine.PostAsJsonAsync("/api/v1/host-executor/activate", activation);
        Assert.Equal(HttpStatusCode.Unauthorized, badResponse.StatusCode);

        var ownerBody = await owner.GetStringAsync("/api/v1/host-executors");
        Assert.DoesNotContain(executor.Secret, ownerBody, StringComparison.Ordinal);
        Assert.DoesNotContain(executor.CredentialDigest, ownerBody, StringComparison.OrdinalIgnoreCase);
        owner.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using var missingCsrf = await owner.PostAsJsonAsync($"/api/v1/host-executors/{executor.Enrollment.Id}/suspend",
            new ChangeHostExecutorStateInput(Id(), 2));
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        Assert.Equal(HostExecutorState.Active, Assert.Single(await app.Store.HostExecutors(executor.Host.Id)).State);
    }

    [Fact]
    public async Task EvidenceMustBeAuthenticatedMonotonicFreshAndLatestAcrossAliases()
    {
        await using var app = new TestApp();
        var physicalHostId = Digest("physical");
        var first = await Enroll(app, physicalHostId);
        var second = await Enroll(app, physicalHostId);
        var policy = await Policy(app, first);
        var firstInput = Observation(first, 1);
        var firstEvidence = await app.Store.SubmitHostResourceObservation(first.Principal, firstInput);
        var replay = await app.Store.SubmitHostResourceObservation(first.Principal, firstInput);
        Assert.Equal(firstEvidence.PhysicalRevision, replay.PhysicalRevision);
        await Assert.ThrowsAsync<ControlException>(() => app.Store.SubmitHostResourceObservation(first.Principal,
            Observation(first, 1) with { Id = Id() }));

        var secondEvidence = await app.Store.SubmitHostResourceObservation(second.Principal, Observation(second, 1));
        Assert.True(secondEvidence.PhysicalRevision > firstEvidence.PhysicalRevision);
        await Assert.ThrowsAsync<ControlException>(() => app.Store.AcquireHostResourceReservation(
            Reservation(first, policy, firstEvidence, port: 4100)));

        var old = ControlStore.Now - 5 * 60_000;
        var delayed = await app.Store.SubmitHostResourceObservation(second.Principal,
            Observation(second, 2) with { Id = Id(), CollectedFrom = old - 1000, CollectedTo = old });
        await Assert.ThrowsAsync<ControlException>(() => app.Store.AcquireHostResourceReservation(
            Reservation(second, policy, delayed, port: 4101)));
    }

    [Fact]
    public async Task ReservationsFencePhysicalResourcesAcrossAliasesAndRequestNamespaces()
    {
        await using var app = new TestApp();
        var physicalHostId = Digest("shared-physical");
        var first = await Enroll(app, physicalHostId);
        var second = await Enroll(app, physicalHostId);
        var policy = await Policy(app, first);
        var firstEvidence = await app.Store.SubmitHostResourceObservation(first.Principal, Observation(first, 1));
        var firstReservationInput = Reservation(first, policy, firstEvidence, port: 4200);
        var firstReservation = await app.Store.AcquireHostResourceReservation(firstReservationInput);
        Assert.Equal(firstReservation.Id, (await app.Store.AcquireHostResourceReservation(firstReservationInput)).Id);

        var secondEvidence = await app.Store.SubmitHostResourceObservation(second.Principal, Observation(second, 1));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.AcquireHostResourceReservation(
            Reservation(second, policy, secondEvidence, port: 4200)));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.MarkHostResourceUnknown(firstReservation.Id,
            new(firstReservation.RequestId, firstReservation.Revision, firstReservation.IntentDigest, "ambiguous response")));

        await app.Store.Write(async db =>
        {
            (await db.HostResourceReservations.FindAsync(firstReservation.Id))!.GrantExpiresAt = 0;
            return true;
        });
        var latest = await app.Store.SubmitHostResourceObservation(second.Principal, Observation(second, 2));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.AcquireHostResourceReservation(
            Reservation(second, policy, latest, cpuMillis: 7500, port: 4201)));
    }

    [Fact]
    public async Task EffectBoundaryAuthorizesOnceAndReleaseNeedsPostEffectIntentBoundAbsence()
    {
        await using var app = new TestApp();
        var executor = await Enroll(app);
        var policy = await Policy(app, executor);
        var intent = Digest("effect-intent");
        var future = ControlStore.Now + 1000;
        var evidence = await app.Store.SubmitHostResourceObservation(executor.Principal,
            Observation(executor, 1, ownership: "Absent", ownershipIntent: intent) with
            {
                CollectedFrom = future,
                CollectedTo = future
            });
        var reservation = await app.Store.AcquireHostResourceReservation(Reservation(executor, policy, evidence, intent: intent));
        var effectInput = new BeginHostResourceEffectInput(reservation.Revision, intent, evidence.Id);
        var first = await app.Store.BeginHostResourceEffect(executor.Principal, reservation.Id, effectInput);
        Assert.True(first.AuthorizedNow);
        var replay = await app.Store.BeginHostResourceEffect(executor.Principal, reservation.Id, effectInput);
        Assert.False(replay.AuthorizedNow);
        Assert.Equal(first.Reservation.EffectCommittedAt, replay.Reservation.EffectCommittedAt);

        var unknown = await app.Store.MarkHostResourceUnknown(reservation.Id,
            new(Id(), first.Reservation.Revision, intent, "effect outcome unavailable"));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.ReleaseHostResourceReservation(reservation.Id,
            new(Id(), unknown.Revision, "ObservedAbsent", evidence.Id)));

        var effectAt = first.Reservation.EffectCommittedAt!.Value;
        var absence = await app.Store.SubmitHostResourceObservation(executor.Principal,
            Observation(executor, 2, evidence.WorkspaceId, "Absent", intent) with
            {
                CollectedFrom = effectAt,
                CollectedTo = Math.Max(effectAt, ControlStore.Now)
            });
        var alias = await Enroll(app, executor.PhysicalHostId);
        await app.Store.SubmitHostResourceObservation(alias.Principal,
            Observation(alias, 1, evidence.WorkspaceId, "Present", intent));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.ReleaseHostResourceReservation(reservation.Id,
            new(Id(), unknown.Revision, "ObservedAbsent", absence.Id)));
        absence = await app.Store.SubmitHostResourceObservation(executor.Principal,
            Observation(executor, 3, evidence.WorkspaceId, "Absent", intent) with
            {
                CollectedFrom = effectAt,
                CollectedTo = Math.Max(effectAt, ControlStore.Now)
            });
        var released = await app.Store.ReleaseHostResourceReservation(reservation.Id,
            new(Id(), unknown.Revision, "ObservedAbsent", absence.Id));
        Assert.Equal(HostReservationState.Released, released.State);
        Assert.Equal(absence.Id, released.ReleaseObservationId);
    }

    [Fact]
    public async Task RotationAndRestartRevokeOldBearerWithoutResettingSequence()
    {
        string data;
        string secrets;
        ExecutorFixture executor;
        SubmitHostResourceObservationInput afterRestart;
        var replacementSecret = Secret("replacement");
        await using (var app = new TestApp())
        {
            executor = await Enroll(app);
            await app.Store.SubmitHostResourceObservation(executor.Principal, Observation(executor, 1));
            var currentRevision = Assert.Single(await app.Store.HostExecutors(executor.Host.Id)).Revision;
            var rotated = await app.Store.RotateHostExecutor(executor.Enrollment.Id,
                new(Id(), currentRevision, Digest("replacement-authority"), CredentialDigest(replacementSecret)));
            var replacementBoot = Digest("replacement-boot");
            var replacementIncarnation = Digest("replacement-incarnation");
            var pendingPrincipal = new HostExecutorPrincipal(rotated.Id, rotated.HostId, rotated.AuthorityGeneration,
                HostExecutorState.Pending, CredentialDigest(replacementSecret));
            var active = await app.Store.ActivateHostExecutor(pendingPrincipal,
                new(rotated.Id, rotated.HostId, rotated.AuthorityGeneration, Digest("replacement-authority"), replacementBoot, replacementIncarnation));
            var replacementPrincipal = pendingPrincipal with { State = HostExecutorState.Active };
            executor = executor with
            {
                Enrollment = active,
                Secret = replacementSecret,
                CredentialDigest = CredentialDigest(replacementSecret),
                Principal = replacementPrincipal,
                AuthorityDigest = Digest("replacement-authority"),
                BootId = replacementBoot,
                IncarnationId = replacementIncarnation
            };
            await app.Store.SubmitHostResourceObservation(replacementPrincipal, Observation(executor, 2));
            await Assert.ThrowsAsync<ControlException>(() => app.Store.SubmitHostResourceObservation(replacementPrincipal,
                Observation(executor, 1) with { Id = Id() }));
            afterRestart = Observation(executor, 3);
            data = app.DataPath;
            secrets = app.SecretPath;
        }

        await using var restarted = new TestApp(data, secrets);
        using var oldMachine = restarted.CreateClient();
        oldMachine.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            executor.Enrollment.Id + "." + Secret(executor.Enrollment.Id));
        using var oldResponse = await oldMachine.PostAsJsonAsync("/api/v1/host-executor/observations", afterRestart);
        Assert.Equal(HttpStatusCode.Unauthorized, oldResponse.StatusCode);
        using var replacementMachine = MachineClient(restarted, executor);
        using var accepted = await replacementMachine.PostAsJsonAsync("/api/v1/host-executor/observations", afterRestart);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task NewPolicyRevisionFencesAnOlderGrantAtEffectStart()
    {
        await using var app = new TestApp();
        var executor = await Enroll(app);
        var policy = await Policy(app, executor);
        var evidence = await app.Store.SubmitHostResourceObservation(executor.Principal, Observation(executor, 1));
        var reservation = await app.Store.AcquireHostResourceReservation(Reservation(executor, policy, evidence));
        await app.Store.ConfigureHostResourcePolicy(new(Id(), policy.Id, executor.Enrollment.Id, policy.Revision,
            8000, 8 * GiB, 100 * GiB, 2, 500, GiB, GiB, 120, 300));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.BeginHostResourceEffect(executor.Principal, reservation.Id,
            new(reservation.Revision, reservation.IntentDigest, evidence.Id)));
        Assert.Equal(HostReservationState.Held,
            Assert.Single(await app.Store.HostResourceReservations(executor.PhysicalHostId)).State);
    }

    [Fact]
    public async Task LaterObservationDoesNotImplicitlyReleaseCommittedCapacity()
    {
        await using var app = new TestApp();
        var executor = await Enroll(app);
        var policy = await Policy(app, executor);
        var evidence = await app.Store.SubmitHostResourceObservation(executor.Principal, Observation(executor, 1));
        var reservation = await app.Store.AcquireHostResourceReservation(Reservation(executor, policy, evidence));
        Assert.True((await app.Store.BeginHostResourceEffect(executor.Principal, reservation.Id,
            new(reservation.Revision, reservation.IntentDigest, evidence.Id))).AuthorizedNow);

        await Task.Delay(5);
        var nextWorkspace = Id();
        var later = await app.Store.SubmitHostResourceObservation(executor.Principal,
            Observation(executor, 2, nextWorkspace) with { AvailableCpuMillis = 1500 });
        await Assert.ThrowsAsync<ControlException>(() => app.Store.AcquireHostResourceReservation(
            Reservation(executor, policy, later)));
        Assert.Equal(HostReservationState.EffectCommitted,
            (await app.Store.HostResourceReservations(executor.PhysicalHostId)).Single().State);
    }

    [Fact]
    public async Task MissingDockerAuthorityBlocksBuildButNotRuntimeAdmission()
    {
        await using var app = new TestApp();
        var executor = await Enroll(app);
        var policy = await Policy(app, executor);
        var unavailable = Observation(executor, 1) with
        {
            DockerFilesystemId = null,
            DockerAvailableBytes = null,
            DockerAvailableInodes = null,
            DockerAvailable = false
        };
        var runtimeEvidence = await app.Store.SubmitHostResourceObservation(executor.Principal, unavailable);
        var runtime = Reservation(executor, policy, runtimeEvidence) with { Kind = "Runtime", BuildSlots = 0 };
        Assert.Equal("Runtime", (await app.Store.AcquireHostResourceReservation(runtime)).Kind);

        var buildEvidence = await app.Store.SubmitHostResourceObservation(executor.Principal,
            unavailable with { Id = Id(), Sequence = 2, WorkspaceId = Id(), CanonicalWorkspaceIdentity = Digest("second-workspace") });
        await Assert.ThrowsAsync<ControlException>(() => app.Store.AcquireHostResourceReservation(
            Reservation(executor, policy, buildEvidence)));
    }

    [Fact]
    public async Task AdditiveMigrationPreservesInventoryWithoutInventingAuthorityOrGrants()
    {
        await using var location = new TestApp();
        var options = new DbContextOptionsBuilder<ControlDb>().UseSqlite($"Data Source={Path.Combine(location.DataPath, "agentcontrol.db")}").Options;
        var hostId = Id();
        await using (var db = new ControlDb(options))
        {
            await db.Database.MigrateAsync("20260908160207_ProviderRecoveryLeaseOwnership");
            db.Hosts.Add(new HostRecord
            {
                Id = hostId,
                Name = "Existing host",
                Kind = "PhysicalMachine",
                CreatedAt = ControlStore.Now,
                UpdatedAt = ControlStore.Now
            });
            await db.SaveChangesAsync();
        }

        await using var upgraded = new TestApp(location.DataPath, location.SecretPath);
        var factory = upgraded.Services.GetRequiredService<IDbContextFactory<ControlDb>>();
        await using var current = await factory.CreateDbContextAsync();
        Assert.Equal(hostId, (await current.Hosts.SingleAsync()).Id);
        Assert.Empty(await current.HostExecutors.ToListAsync());
        Assert.Empty(await current.HostResourceObservations.ToListAsync());
        Assert.Empty(await current.HostResourceReservations.ToListAsync());
        Assert.Empty(await current.HostResourceMutations.ToListAsync());
        Assert.Empty(await current.Database.GetPendingMigrationsAsync());
    }

    private static async Task<ExecutorFixture> Enroll(TestApp app, string? physicalHostId = null, bool activate = true)
    {
        var host = await app.Store.CreateHost(new(Id(), Id(), "Trusted Linux host", "PhysicalMachine"));
        var enrollmentId = Id();
        var secret = Secret(enrollmentId);
        var authority = Digest("authority-" + enrollmentId);
        var fixture = new ExecutorFixture(host, null!, secret, CredentialDigest(secret), Digest("endpoint-" + enrollmentId),
            physicalHostId ?? Digest("physical-" + enrollmentId), Digest("engine-" + enrollmentId), Digest("builder-" + enrollmentId),
            authority, Digest("boot-" + enrollmentId), Digest("incarnation-" + enrollmentId), null!);
        var enrollment = await app.Store.CreateHostExecutor(new(Id(), enrollmentId, host.Id, host.Revision, fixture.EndpointId,
            fixture.PhysicalHostId, fixture.EngineId, fixture.BuilderId, Digest("cli-" + enrollmentId), Digest("root-" + enrollmentId),
            authority, fixture.CredentialDigest));
        var pending = new HostExecutorPrincipal(enrollment.Id, host.Id, enrollment.AuthorityGeneration, HostExecutorState.Pending, fixture.CredentialDigest);
        fixture = fixture with { Enrollment = enrollment, Principal = pending };
        if (!activate) return fixture;
        var active = await app.Store.ActivateHostExecutor(pending, Activation(fixture));
        return fixture with { Enrollment = active, Principal = pending with { State = HostExecutorState.Active } };
    }

    private static ActivateHostExecutorInput Activation(ExecutorFixture executor) => new(executor.Enrollment.Id, executor.Host.Id,
        executor.Enrollment.AuthorityGeneration, executor.AuthorityDigest, executor.BootId, executor.IncarnationId);

    private static async Task<HostResourcePolicy> Policy(TestApp app, ExecutorFixture executor) =>
        await app.Store.ConfigureHostResourcePolicy(new(Id(), Id(), executor.Enrollment.Id, 0,
            8000, 8 * GiB, 100 * GiB, 2, 500, GiB, GiB, 120, 300));

    private static SubmitHostResourceObservationInput Observation(ExecutorFixture executor, long sequence,
        string? workspaceId = null, string ownership = "Unknown", string? ownershipIntent = null)
    {
        var now = ControlStore.Now;
        var workspace = workspaceId ?? Id();
        return new(Id(), executor.Enrollment.Id, executor.Host.Id, executor.EndpointId, executor.PhysicalHostId,
            executor.EngineId, executor.BuilderId, executor.Enrollment.AuthorityGeneration, executor.BootId, executor.IncarnationId,
            sequence, 1, HostObservationState.Complete, now - 1000, now, "x86_64", 8000, 7000, 8 * GiB, 7 * GiB,
            0, 2 * GiB, 512 * 1024 * 1024, GiB, 0, workspace, Digest("workspace-" + workspace), Digest("workspace-fs"),
            80 * GiB, 1_000_000, Digest("docker-fs"), 70 * GiB, 1_000_000, true, 2, ownership, ownershipIntent);
    }

    private static AcquireHostResourceReservationInput Reservation(ExecutorFixture executor, HostResourcePolicy policy,
        HostResourceObservation observation, string? intent = null, long cpuMillis = 1000, int? port = null) =>
        new(Id(), Id(), intent ?? Digest("intent-" + observation.Id), executor.Host.Id, executor.Enrollment.Id, policy.Id,
            policy.Revision, observation.Id, observation.WorkspaceId, Id(), "Build", cpuMillis, GiB, GiB, 1, port);

    private static HttpClient MachineClient(TestApp app, ExecutorFixture executor)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", executor.Enrollment.Id + "." + executor.Secret);
        return client;
    }

    private static string Id() => Guid.NewGuid().ToString("N");
    private static string Secret(string value) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string CredentialDigest(string secret) => Digest(secret);
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record ExecutorFixture(HostRecord Host, HostExecutorEnrollment Enrollment, string Secret,
        string CredentialDigest, string EndpointId, string PhysicalHostId, string EngineId, string BuilderId,
        string AuthorityDigest, string BootId, string IncarnationId, HostExecutorPrincipal Principal);
}
