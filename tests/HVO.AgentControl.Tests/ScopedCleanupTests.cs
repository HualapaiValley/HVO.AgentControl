using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HVO.AgentControl.Organization;
using HVO.AgentControl.RemoteWorker;
using HVO.AgentControl.Runtime;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Scoped cleanup for failed profile builds and failed provisioning attempts
/// (#261 slice D). These run without SSH or Docker: the store and a recording
/// provisioner are enough to prove the exact references requested, the refusals,
/// and the state left behind.
/// </summary>
public sealed class ScopedCleanupTests : IAsyncLifetime
{
    private const string FromDigest = "sha256:" + "1111111111111111111111111111111111111111111111111111111111111111";
    private const string ToDigest = "sha256:" + "2222222222222222222222222222222222222222222222222222222222222222";
    private const string BaseDigest = "sha256:" + "3333333333333333333333333333333333333333333333333333333333333333";
    private const string ContextHash = "sha256:" + "4444444444444444444444444444444444444444444444444444444444444444";
    private const string OrphanDigest = "sha256:" + "5555555555555555555555555555555555555555555555555555555555555555";
    private const string OrphanTag = "agentcontrol-profile:prev-orphan-000000000000";
    private const string OrphanRevision = "prev-0000000000000001";

    private readonly TempDirectory _temp = new();
    private OrganizationStore _store = null!;
    private ContainerProfileRevisionSummary _cleanupRevision = null!;
    private string _fromRevisionId = null!;
    private ManagedWorker _managed = null!;

    public Task InitializeAsync()
    {
        _store = Open(_temp.Path);
        var host = _store.GetExecutionHost(ExecutionHosts.LocalDockerId)!;
        _store.RecordLocalExecutionHostProbe(ExecutionHosts.LocalDockerId, host.Revision, new LocalExecutionHostProbe(
            "29.0", "1.51", "x86_64", "overlay2", "ext4", false, 64L << 30, 16L << 30, 8, true, "linux/amd64", "valid"));
        var profile = _store.ListContainerProfiles().Single(p => p.Slug == ContainerProfileSeed.GenericEmployeeSlug);
        _fromRevisionId = _store.GetContainerProfile(profile.Id)!.Revisions.Single().Id;
        _managed = AddManagedWorker(_store, _temp.Path, FromDigest, "Cleanup Fixture Worker", "wrk-cleanup-1");

        // A dedicated single-revision profile is the current revision for the
        // build-cleanup cases, so queueing never collides with the managed
        // worker's verified target build.
        var cleanup = _store.CreateContainerProfile(new ContainerProfileCreate(
            Guid.NewGuid().ToString("N"),
            "cleanup-" + Guid.NewGuid().ToString("N")[..8],
            "Cleanup Profile",
            "Scoped cleanup fixtures.",
            """{"image":"agentcontrol-worker-base","name":"Cleanup"}""",
            null), null);
        _cleanupRevision = _store.GetContainerProfile(cleanup.Id)!.Revisions.Single();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _store.Dispose();
        _temp.Dispose();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------- store transitions

    [Fact]
    public void TransitionToRemovedWorksOnlyFromFailedOrRejected()
    {
        var failed = FailedBuild(OrphanDigest);
        var removed = _store.TransitionProfileBuildToRemoved(failed.Id, failed.Revision, "owner", ContextHash);
        Assert.Equal(ProfileBuildStates.Removed, removed.State);
        Assert.Equal(ContextHash, removed.EvidenceHash);
        Assert.NotNull(removed.FinishedAt);

        var rejected = RejectedBuild("agentcontrol-profile:prev-rejected-000000000000");
        Assert.Equal(ProfileBuildStates.Removed, _store.TransitionProfileBuildToRemoved(rejected.Id, rejected.Revision, "owner").State);
        // A second removal of an already-removed row is refused: only failed/rejected are allowed.
        Assert.Throws<OrganizationConcurrencyException>(() => _store.TransitionProfileBuildToRemoved(removed.Id, removed.Revision, "owner"));
    }

    [Fact]
    public void TransitionToRemovedRefusesQueuedBuildingVerifyingBuiltAndUncertain()
    {
        var queued = _store.QueueProfileBuild(_cleanupRevision.Id, ExecutionHosts.LocalDockerId, BaseDigest, "linux/amd64", ContextHash, "agentcontrol-profile:prev-queued-000000000000");
        Assert.Throws<OrganizationConcurrencyException>(() => _store.TransitionProfileBuildToRemoved(queued.Id, queued.Revision, "owner"));

        var building = _store.TransitionProfileBuild(queued.Id, queued.Revision, ProfileBuildStates.Building);
        Assert.Throws<OrganizationConcurrencyException>(() => _store.TransitionProfileBuildToRemoved(building.Id, building.Revision, "owner"));

        var verifying = _store.TransitionProfileBuild(building.Id, building.Revision, ProfileBuildStates.Verifying);
        Assert.Throws<OrganizationConcurrencyException>(() => _store.TransitionProfileBuildToRemoved(verifying.Id, verifying.Revision, "owner"));

        var built = _store.TransitionProfileBuild(verifying.Id, verifying.Revision, ProfileBuildStates.Built, imageDigest: OrphanDigest, verified: true, evidenceHash: ContextHash);
        Assert.Throws<OrganizationConcurrencyException>(() => _store.TransitionProfileBuildToRemoved(built.Id, built.Revision, "owner"));

        // Uncertain is refused at the store boundary so an unknown result is always
        // reconciled before any image is deleted. A fresh revision keeps the live
        // slot from colliding with the built row above.
        var uncertain = UncertainBuild(NewCleanupRevision().Id, "agentcontrol-profile:prev-uncertain-000000000");
        Assert.Throws<OrganizationConcurrencyException>(() => _store.TransitionProfileBuildToRemoved(uncertain.Id, uncertain.Revision, "owner"));
    }

    [Fact]
    public void TransitionToRemovedRequiresTheOwnerAndCurrentRevision()
    {
        var failed = FailedBuild(OrphanDigest);
        Assert.Throws<OrganizationValidationException>(() => _store.TransitionProfileBuildToRemoved(failed.Id, failed.Revision, "employee"));
        Assert.Throws<OrganizationValidationException>(() => _store.TransitionProfileBuildToRemoved(failed.Id, failed.Revision, "owner", "not-a-hash"));
        Assert.Throws<OrganizationConcurrencyException>(() => _store.TransitionProfileBuildToRemoved(failed.Id, failed.Revision + 1, "owner"));
        Assert.Equal(ProfileBuildStates.Failed, _store.GetProfileBuild(failed.Id)!.State);
    }

    [Fact]
    public void ListRemovableReturnsOnlyUnremovedFailedAndRejectedBuilds()
    {
        var failed = FailedBuild(OrphanDigest);
        var rejected = RejectedBuild("agentcontrol-profile:prev-rejected-list-00000000000");
        _ = BuiltBuild(_cleanupRevision.Id, "agentcontrol-profile:prev-built-list-0000000000000");
        Assert.Equal(new[] { failed.Id, rejected.Id }.OrderBy(x => x, StringComparer.Ordinal), _store.ListRemovableProfileBuilds(_cleanupRevision.Id).Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal));
        _store.TransitionProfileBuildToRemoved(failed.Id, failed.Revision, "owner");
        Assert.Equal([rejected.Id], _store.ListRemovableProfileBuilds(_cleanupRevision.Id).Select(x => x.Id).ToArray());
    }

    // ------------------------------------------------------- usage guard

    [Fact]
    public void ImageDigestUsageNamesEnrollmentManagedAndRebuildCategories()
    {
        // The fixture enrollment expects FromDigest; the frozen managed approval
        // pins ToDigest.
        Assert.Contains("enrollment", _store.ImageDigestUsage(FromDigest));
        Assert.Contains("managed-enrollment", _store.ImageDigestUsage(ToDigest));

        // A failed rebuild is historical, not in use.
        var rebuild = _store.BeginEmployeeRebuild(CreateRebuild());
        Assert.Contains("employee-rebuild", _store.ImageDigestUsage(ToDigest));
        _store.TransitionEmployeeRebuild(rebuild.Id, rebuild.Revision, EmployeeRebuildStates.Intent, EmployeeRebuildStates.Failed, failureSummary: "deterministic");
        Assert.Empty(_store.ImageDigestUsage(OrphanDigest));
    }

    // ------------------------------------------------------- build cleanup

    [Fact]
    public async Task CleanupRemovesTheResultTagAndDigestAndMarksTheBuildRemoved()
    {
        var build = FailedBuild(OrphanDigest, OrphanTag);
        var remote = new RecordingProvisioner();
        var cleanup = Cleanup(remote);

        var removed = await cleanup.CleanupProfileBuildAsync(build.Id, "owner", CancellationToken.None);

        Assert.Equal(ProfileBuildStates.Removed, removed.State);
        Assert.Equal([OrphanTag, OrphanDigest], remote.RemovedImages);
        Assert.NotNull(removed.EvidenceHash);
        Assert.StartsWith("sha256:", removed.EvidenceHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdapterToleratesNotFoundButRaisesTransport()
    {
        var operations = new FakeImageOperations();
        var adapter = new RemoteWorkerProvisionerAdapter(operations);
        var target = new ExecutionTarget(ExecutionHosts.LocalDockerId, "local-docker", null);

        // A not-found answer means the image is already gone: the adapter treats it
        // as success so cleanup can converge.
        operations.Result = new RemoteOperationResult(1, "", "not-found");
        await adapter.RemoveImageAsync(target, OrphanTag, CancellationToken.None);

        // A transport failure stays an uncertain effect and must be raised.
        operations.Result = new RemoteOperationResult(255, "", "transport");
        var failure = await Assert.ThrowsAsync<RemoteWorkerUnavailableException>(() => adapter.RemoveImageAsync(target, OrphanTag, CancellationToken.None));
        Assert.True(failure.Transport);
    }

    [Fact]
    public async Task CleanupToleratesAnAlreadyAbsentImageThroughTheAdapter()
    {
        // The coordinator uses the adapter, which tolerates not-found; the build row
        // still reaches removed because the image is provably gone.
        var operations = new FakeImageOperations { Result = new RemoteOperationResult(1, "", "not-found") };
        var build = FailedBuild(OrphanDigest, OrphanTag);
        var cleanup = Cleanup(new RemoteWorkerProvisionerAdapter(operations));

        var removed = await cleanup.CleanupProfileBuildAsync(build.Id, "owner", CancellationToken.None);

        Assert.Equal(ProfileBuildStates.Removed, removed.State);
        Assert.Equal([OrphanTag, OrphanDigest], operations.RequestedImages);
    }

    [Fact]
    public async Task CleanupRefusesAnImageStillUsedAndNeverCallsTheTransport()
    {
        // The enrollment expects FromDigest, so a failed build carrying that digest
        // is in use.
        var build = FailedBuild(FromDigest, "agentcontrol-profile:prev-inuse-00000000000000");
        var remote = new RecordingProvisioner();
        var cleanup = Cleanup(remote);

        var refused = await Assert.ThrowsAsync<OrganizationValidationException>(() => cleanup.CleanupProfileBuildAsync(build.Id, "owner", CancellationToken.None));

        Assert.Contains("enrollment", refused.Message, StringComparison.Ordinal);
        Assert.Empty(remote.RemovedImages);
        Assert.Equal(ProfileBuildStates.Failed, _store.GetProfileBuild(build.Id)!.State);
    }

    [Fact]
    public async Task CleanupRefusesAnImagePinnedByTheFrozenManagedApproval()
    {
        var build = FailedBuild(ToDigest, "agentcontrol-profile:prev-managed-0000000000000");
        var remote = new RecordingProvisioner();
        var cleanup = Cleanup(remote);

        var refused = await Assert.ThrowsAsync<OrganizationValidationException>(() => cleanup.CleanupProfileBuildAsync(build.Id, "owner", CancellationToken.None));

        Assert.Contains("managed-enrollment", refused.Message, StringComparison.Ordinal);
        Assert.Empty(remote.RemovedImages);
        Assert.Equal(ProfileBuildStates.Failed, _store.GetProfileBuild(build.Id)!.State);
    }

    [Fact]
    public async Task CleanupLeavesTheBuildUnremovedAndRequiresRecoveryOnUncertainTransport()
    {
        var build = FailedBuild(OrphanDigest, OrphanTag);
        var remote = new RecordingProvisioner { RemovalTransportLoss = true };
        var cleanup = Cleanup(remote);

        var failure = await Assert.ThrowsAsync<WorkerRecoveryRequiredException>(() => cleanup.CleanupProfileBuildAsync(build.Id, "owner", CancellationToken.None));

        Assert.Equal("profile-build-remove-uncertain", failure.Kind);
        Assert.Equal(ProfileBuildStates.Failed, _store.GetProfileBuild(build.Id)!.State);
    }

    [Fact]
    public async Task CleanupRefusesUncertainLiveAndBuiltBuilds()
    {
        var remote = new RecordingProvisioner();
        var cleanup = Cleanup(remote);

        var uncertain = UncertainBuild(NewCleanupRevision().Id, "agentcontrol-profile:prev-unc-000000000000000");
        var recovery = await Assert.ThrowsAsync<WorkerRecoveryRequiredException>(() => cleanup.CleanupProfileBuildAsync(uncertain.Id, "owner", CancellationToken.None));
        Assert.Equal("profile-build-uncertain", recovery.Kind);

        var built = BuiltBuild(NewCleanupRevision().Id, "agentcontrol-profile:prev-built-00000000000000");
        await Assert.ThrowsAsync<OrganizationConcurrencyException>(() => cleanup.CleanupProfileBuildAsync(built.Id, "owner", CancellationToken.None));

        Assert.Empty(remote.RemovedImages);
    }

    [Fact]
    public async Task CleanupClaimsDurablyAndBlocksATagReuseUntilFinalized()
    {
        var failed = FailedBuild(OrphanDigest, OrphanTag);

        // The claim reserves the (host, tag) pair: a second claim for the same tag
        // is refused, so two cleanups cannot race the same image.
        var claim = _store.ClaimProfileBuildRemoval(failed.Id, failed.Revision, "owner");
        Assert.Equal(ProfileBuildStates.Failed, claim.State);

        // A new build that tries to reuse the claimed tag is refused.
        await Assert.ThrowsAsync<OrganizationConcurrencyException>(() =>
            Task.Run(() => _store.QueueProfileBuild(NewCleanupRevision().Id, ExecutionHosts.LocalDockerId, BaseDigest, "linux/amd64", ContextHash, OrphanTag)));

        // Finalizing the removal releases the claim and marks the build removed.
        var finalized = _store.FinalizeProfileBuildRemoval(failed.Id, claim.Revision, "owner", ContextHash);
        Assert.Equal(ProfileBuildStates.Removed, finalized.State);
        Assert.False(_store.IsResultTagClaimedForRemoval(ExecutionHosts.LocalDockerId, OrphanTag));
    }

    [Fact]
    public async Task CleanupReleasedClaimAllowsARetryAfterARefusedGuard()
    {
        // The build's recorded digest is in use, so the coordinator's post-claim
        // guard refuses after the claim exists; the claim must be released so the
        // state stays retryable and the build is not marked removed.
        var build = FailedBuild(FromDigest, "agentcontrol-profile:prev-guard-0000000000000");
        var remote = new RecordingProvisioner();
        var cleanup = Cleanup(remote);

        await Assert.ThrowsAsync<OrganizationValidationException>(() => cleanup.CleanupProfileBuildAsync(build.Id, "owner", CancellationToken.None));

        Assert.Equal(ProfileBuildStates.Failed, _store.GetProfileBuild(build.Id)!.State);
        Assert.False(_store.IsResultTagClaimedForRemoval(ExecutionHosts.LocalDockerId, build.ResultTag));
        Assert.Empty(remote.RemovedImages);
    }

    [Fact]
    public async Task CleanupRefusesUnknownBuildAndNonOwner()
    {
        await Assert.ThrowsAsync<OrganizationNotFoundException>(() => Cleanup(new RecordingProvisioner()).CleanupProfileBuildAsync("pbld-0000000000000000", "owner", CancellationToken.None));
        // A non-owner is refused by the store transition, after the digest guard is
        // satisfied; use a not-in-use digest so the guard passes.
        var failed = FailedBuild(OrphanDigest, OrphanTag);
        await Assert.ThrowsAsync<OrganizationValidationException>(() => Cleanup(new RecordingProvisioner()).CleanupProfileBuildAsync(failed.Id, "employee", CancellationToken.None));
    }

    // -------------------------------------------------- provisioning cleanup

    [Fact]
    public async Task ProvisioningCleanupRefusesWhileAnEmployeeRebuildIsActive()
    {
        var remote = new RecordingProvisioner();
        var coordinator = CreateProvisioningCoordinator(remote);
        var cleanup = new CleanupCoordinator(Control(), remote, coordinator, Options.Create(ProvisioningOptions()), NullLogger<CleanupCoordinator>.Instance);

        var active = _store.BeginEmployeeRebuild(CreateRebuild());
        var refused = await Assert.ThrowsAsync<OrganizationConcurrencyException>(() => cleanup.CleanupFailedProvisioningAsync(_managed.WorkerId, CancellationToken.None));
        Assert.Contains("active employee rebuild", refused.Message, StringComparison.Ordinal);
        Assert.Empty(remote.Effects);

        // Once the rebuild is terminal, cleanup proceeds.
        _store.TransitionEmployeeRebuild(active.Id, active.Revision, EmployeeRebuildStates.Intent, EmployeeRebuildStates.Failed, failureSummary: "deterministic");
        await cleanup.CleanupFailedProvisioningAsync(_managed.WorkerId, CancellationToken.None);
        Assert.DoesNotContain(remote.Effects, effect => effect.StartsWith("remove-image:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProvisioningCleanupRemovesOnlyTheOwnedResourceAndNeverAnImage()
    {
        var remote = new RecordingProvisioner();
        var coordinator = CreateProvisioningCoordinator(remote);
        var cleanup = new CleanupCoordinator(Control(), remote, coordinator, Options.Create(ProvisioningOptions()), NullLogger<CleanupCoordinator>.Instance);
        var owned = _seedOwnedResource(remote);

        await cleanup.CleanupFailedProvisioningAsync(_managed.WorkerId, CancellationToken.None);

        Assert.Contains("remove-volume:" + owned, remote.Effects);
        Assert.DoesNotContain(remote.Effects, effect => effect.StartsWith("remove-image:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProvisioningCleanupRefusesAForeignResourceAndRemovesNothingElse()
    {
        var remote = new RecordingProvisioner();
        var coordinator = CreateProvisioningCoordinator(remote);
        var cleanup = new CleanupCoordinator(Control(), remote, coordinator, Options.Create(ProvisioningOptions()), NullLogger<CleanupCoordinator>.Instance);
        var owned = _seedOwnedResource(remote);
        var foreign = "foreign-volume-" + _managed.WorkerId;
        remote.Seed(foreign, new Dictionary<string, string> { ["agentcontrol.owner"] = "other-organization/controller-a" });
        var foreignOp = _store.AddProvisioningIntent(_managed.WorkerId, ExecutionHosts.LocalDockerId, _managed.BindingId, "volume-create", Hash("foreign"));
        _store.AddResourceIntent(foreignOp.Id, ExecutionHosts.LocalDockerId, _managed.WorkerId, "volume", foreign, Hash("foreign"));

        var failure = await Assert.ThrowsAsync<WorkerRecoveryRequiredException>(() => cleanup.CleanupFailedProvisioningAsync(_managed.WorkerId, CancellationToken.None));

        Assert.Equal("cleanup-unverified", failure.Kind);
        Assert.Contains("remove-volume:" + owned, remote.Effects);
        Assert.DoesNotContain(remote.Effects, effect => effect.Contains(foreign, StringComparison.Ordinal));
        Assert.DoesNotContain(remote.Effects, effect => effect.StartsWith("remove-image:", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- helpers

    private ContainerProfileRevisionSummary NewCleanupRevision()
    {
        var profile = _store.CreateContainerProfile(new ContainerProfileCreate(
            Guid.NewGuid().ToString("N"),
            "cleanup-" + Guid.NewGuid().ToString("N")[..8],
            "Cleanup Profile",
            "Scoped cleanup fixtures.",
            """{"image":"agentcontrol-worker-base","name":"Cleanup"}""",
            null), null);
        return _store.GetContainerProfile(profile.Id)!.Revisions.Single();
    }

    private ProfileBuildRecord FailedBuild(string imageDigest, string? resultTag = null)
    {
        var queued = _store.QueueProfileBuild(_cleanupRevision.Id, ExecutionHosts.LocalDockerId, BaseDigest, "linux/amd64", ContextHash, resultTag ?? "agentcontrol-profile:prev-failed-000000000000");
        var building = _store.TransitionProfileBuild(queued.Id, queued.Revision, ProfileBuildStates.Building);
        var verifying = _store.TransitionProfileBuild(building.Id, building.Revision, ProfileBuildStates.Verifying);
        return _store.TransitionProfileBuild(verifying.Id, verifying.Revision, ProfileBuildStates.Failed, imageDigest: imageDigest, failureSummary: "the build failed (remote-command-failed)");
    }

    private ProfileBuildRecord RejectedBuild(string resultTag)
    {
        var queued = _store.QueueProfileBuild(_cleanupRevision.Id, ExecutionHosts.LocalDockerId, BaseDigest, "linux/amd64", ContextHash, resultTag);
        var building = _store.TransitionProfileBuild(queued.Id, queued.Revision, ProfileBuildStates.Building);
        var verifying = _store.TransitionProfileBuild(building.Id, building.Revision, ProfileBuildStates.Verifying);
        return _store.TransitionProfileBuild(verifying.Id, verifying.Revision, ProfileBuildStates.Rejected, failureSummary: "contract violated");
    }

    private ProfileBuildRecord BuiltBuild(string revisionId, string resultTag)
    {
        var queued = _store.QueueProfileBuild(revisionId, ExecutionHosts.LocalDockerId, BaseDigest, "linux/amd64", ContextHash, resultTag);
        var building = _store.TransitionProfileBuild(queued.Id, queued.Revision, ProfileBuildStates.Building);
        var verifying = _store.TransitionProfileBuild(building.Id, building.Revision, ProfileBuildStates.Verifying);
        return _store.TransitionProfileBuild(verifying.Id, verifying.Revision, ProfileBuildStates.Built, imageDigest: OrphanDigest, verified: true, evidenceHash: ContextHash);
    }

    private ProfileBuildRecord UncertainBuild(string revisionId, string resultTag)
    {
        var queued = _store.QueueProfileBuild(revisionId, ExecutionHosts.LocalDockerId, BaseDigest, "linux/amd64", ContextHash, resultTag);
        var building = _store.TransitionProfileBuild(queued.Id, queued.Revision, ProfileBuildStates.Building);
        return _store.TransitionProfileBuild(building.Id, building.Revision, ProfileBuildStates.Uncertain, failureSummary: "lost");
    }

    private EmployeeRebuildCreate CreateRebuild() => new(
        _managed.EmployeeId,
        _managed.BindingId,
        _managed.WorkerId,
        ExecutionHosts.LocalDockerId,
        _fromRevisionId,
        FromDigest,
        1,
        _managed.ToRevisionId,
        _managed.ToBuildId,
        ToDigest,
        "linux/amd64",
        false,
        false,
        null,
        0);

    /// <summary>Seeds one owned volume in the resource table and in the fake host, returning its name.</summary>
    private string _seedOwnedResource(RecordingProvisioner remote)
    {
        var enrollment = _store.GetWorkerEnrollment(_managed.WorkerId)!;
        var operation = _store.AddProvisioningIntent(_managed.WorkerId, enrollment.HostId, _managed.BindingId, "volume-create", Hash("owned"));
        var identity = new WorkerResourceIdentity(enrollment.OrganizationId, enrollment.ControllerId, enrollment.HostId, enrollment.WorkerId, enrollment.RuntimeBindingId, operation.Id);
        var owned = "owned-volume-" + _managed.WorkerId;
        _store.AddResourceIntent(operation.Id, enrollment.HostId, _managed.WorkerId, "volume", owned, Hash(string.Join('\n', identity.Labels.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value))));
        remote.Seed(owned, identity.Labels);
        return owned;
    }

    private CleanupCoordinator Cleanup(IRemoteWorkerProvisioner remote) =>
        new(Control(), remote, CreateProvisioningCoordinator(remote), Options.Create(WorkerOptions()), NullLogger<CleanupCoordinator>.Instance);

    private RemoteWorkerProvisioningCoordinator CreateProvisioningCoordinator(IRemoteWorkerProvisioner remote) =>
        new(Control(), remote, Options.Create(ProvisioningOptions()), new StubVerificationFactory());

    private AcpControlHost Control()
    {
        var options = new ControlOptions { DataDirectory = _temp.Path, PrivateDataDirectory = Path.Combine(_temp.Path, "private") };
        var control = new AcpControlHost(Options.Create(options), NullLogger<AcpControlHost>.Instance);
        typeof(AcpControlHost).GetField("_organization", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(control, _store);
        return control;
    }

    private WorkerControlOptions WorkerOptions() => new()
    {
        Enabled = true,
        ControllerId = "controller-a",
        ApprovedImageDigest = BaseDigest,
        ApprovedImagePlatform = "linux/amd64",
        ExpectedControllerUid = ControllerPrivateFile.EffectiveUid,
    };

    /// <summary>The provisioning coordinator derives the operation identity from the enrollment's frozen digest, so the approved base must match it to yield a null profile revision.</summary>
    private WorkerControlOptions ProvisioningOptions() => new()
    {
        Enabled = true,
        ControllerId = "controller-a",
        ApprovedImageDigest = FromDigest,
        ApprovedImagePlatform = "linux/amd64",
        ExpectedControllerUid = ControllerPrivateFile.EffectiveUid,
    };

    private sealed class StubVerificationFactory : IWorkerBridgeSessionFactory
    {
        public Task<IWorkerBridgeSession> ConnectAsync(WorkerEnrollmentRecord enrollment, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static string Hash(string value) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record ManagedWorker(string EmployeeId, string BindingId, string WorkerId, string ToRevisionId, string ToBuildId);

    private static ManagedWorker AddManagedWorker(OrganizationStore store, string path, string fromDigest, string displayName, string workerId)
    {
        var profile = store.ListContainerProfiles().Single(p => p.Slug == ContainerProfileSeed.GenericEmployeeSlug);
        var toRevision = store.CreateContainerProfileRevision(profile.Id, new ContainerProfileRevisionCreate(
            store.GetContainerProfile(profile.Id)!.Profile.Revision,
            """{"image":"agentcontrol-worker-base","name":"Cleanup Target"}""",
            null));
        _ = BuildVerified(store, toRevision.Id, ToDigest);
        var overview = store.GetOverview();
        var department = overview.Departments.Single(d => d.Slug == OrganizationSeed.OperationsSlug);
        var role = Assert.Single(overview.Roles);
        var hire = store.CreateHireRequest(new HireRequestCreate(
            Guid.NewGuid().ToString("N"), displayName, "Scoped cleanup fixture.", department.Id, role.Id,
            RuntimePlacements.DeveloperContainer, 2, 2048, 256), null);
        store.ApproveHireRequest(hire.Id, new HireRequestApprove(hire.Revision, toRevision.Id), "owner");
        var creation = store.CreateManagedEmployeeFromHire(hire.Id);
        SeedWorkerEnrollment(path, creation.RuntimeBindingId, workerId, fromDigest);
        var build = store.GetVerifiedProfileBuild(toRevision.Id, ExecutionHosts.LocalDockerId)!;
        return new ManagedWorker(creation.EmployeeId, creation.RuntimeBindingId, workerId, toRevision.Id, build.Id);
    }

    private static ProfileBuildRecord BuildVerified(OrganizationStore store, string revisionId, string imageDigest)
    {
        var queued = store.QueueProfileBuild(revisionId, ExecutionHosts.LocalDockerId, BaseDigest, "linux/amd64", ContextHash, "agentcontrol-profile:prev-fixture-00000000000");
        var building = store.TransitionProfileBuild(queued.Id, queued.Revision, ProfileBuildStates.Building);
        var verifying = store.TransitionProfileBuild(building.Id, building.Revision, ProfileBuildStates.Verifying);
        return store.TransitionProfileBuild(verifying.Id, verifying.Revision, ProfileBuildStates.Built, imageDigest: imageDigest, verified: true, evidenceHash: ContextHash);
    }

    private static void SeedWorkerEnrollment(string path, string bindingId, string workerId, string expectedImageDigest)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var keyPath = Path.Combine(path, "worker.key").Replace("'", "''", StringComparison.Ordinal);
        ExecuteRaw(path,
            $"""
            INSERT INTO worker_enrollments (
                worker_id, runtime_binding_id, host_id, organization_id,
                container_name, control_volume_name, home_volume_name, workspace_volume_name, session_volume_name,
                resource_labels_hash, expected_image_digest, expected_platform, controller_id, key_file_path, key_id,
                bridge_socket_path, lifecycle_status, worker_generation, process_generation, ownership_epoch, enabled,
                created_at, updated_at, revision)
            VALUES ('{workerId}', '{bindingId}', '{ExecutionHosts.LocalDockerId}', (SELECT id FROM organizations LIMIT 1),
                'container-{workerId}', 'control-{workerId}', 'home-{workerId}', 'workspace-{workerId}', 'session-{workerId}',
                'sha256:{new string('a', 64)}', '{expectedImageDigest}', 'linux/amd64', 'controller-a', '{keyPath}',
                'sha256:{new string('c', 64)}', '/control/bridge.sock', 'enrolled', 0, 0, 0, 1, '{now}', '{now}', 1)
            """);
    }

    private static OrganizationStore Open(string directory)
    {
        var store = new OrganizationStore(Path.Combine(directory, "control.db"), lockTimeout: TimeSpan.FromSeconds(5));
        store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        return store;
    }

    private static void ExecuteRaw(string directory, string sql)
    {
        var path = Path.Combine(directory, "control.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Records exact image and resource removal effects. Inspections return the
    /// exact labels of the operation identity computed the same way the coordinator
    /// does, except that the resource seeded as unrelated carries a different owner
    /// label so cleanup must treat it as foreign.
    /// </summary>
    private sealed class RecordingProvisioner : IRemoteWorkerProvisioner
    {
        private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _labels = new(StringComparer.Ordinal);
        public List<string> Effects { get; } = [];
        public List<string> RemovedImages { get; } = [];
        public bool RemovalTransportLoss { get; set; }
        /// <summary>Map of result tag to the digest it currently resolves to, as the host would report.</summary>
        public Dictionary<string, string> TagDigests { get; } = new(StringComparer.Ordinal);

        public void Seed(string name, IReadOnlyDictionary<string, string> labels) => _labels[name] = labels;

        public Task<string?> InspectImageAsync(ExecutionTarget host, string imageReference, CancellationToken token)
        {
            if (RemovalTransportLoss) throw new RemoteWorkerUnavailableException("injected transport loss", transport: true);
            return Task.FromResult(TagDigests.TryGetValue(imageReference, out var digest) ? digest : imageReference.StartsWith("sha256:", StringComparison.Ordinal) ? imageReference : null);
        }

        public Task<HostProbePayload> ProbeAsync(ExecutionTarget host, CancellationToken token) => throw new NotSupportedException();
        public Task<string> CreateVolumeAsync(ExecutionTarget host, VolumeCreateSpec spec, CancellationToken token) => throw new NotSupportedException();
        public Task<string> CreateContainerAsync(ExecutionTarget host, ContainerCreateSpec spec, CancellationToken token) => throw new NotSupportedException();
        public Task BootstrapAsync(ExecutionTarget host, BootstrapSpec spec, byte[] key, CancellationToken token) => throw new NotSupportedException();

        public Task RemoveContainerAsync(ExecutionTarget host, string container, CancellationToken token) { Effects.Add("remove-container:" + container); return Task.CompletedTask; }
        public Task RemoveVolumeAsync(ExecutionTarget host, string volume, CancellationToken token) { Effects.Add("remove-volume:" + volume); return Task.CompletedTask; }
        public Task StartAsync(ExecutionTarget host, string container, CancellationToken token) => throw new NotSupportedException();
        public Task StopAsync(ExecutionTarget host, string container, CancellationToken token) => throw new NotSupportedException();

        public Task RemoveImageAsync(ExecutionTarget host, string imageReference, CancellationToken token)
        {
            RemovedImages.Add(imageReference);
            Effects.Add("remove-image:" + imageReference);
            if (RemovalTransportLoss) throw new RemoteWorkerUnavailableException("injected transport loss", transport: true);
            // ImagesAbsent models the adapter's tolerated not-found answer.
            return Task.CompletedTask;
        }

        public Task<RemoteResourceInspection> InspectVolumeAsync(ExecutionTarget host, string name, CancellationToken token) => Inspect(name);
        public Task<RemoteResourceInspection> InspectContainerAsync(ExecutionTarget host, string name, CancellationToken token) => Inspect(name);

        private Task<RemoteResourceInspection> Inspect(string name)
        {
            if (!_labels.TryGetValue(name, out var labels)) return Task.FromResult(new RemoteResourceInspection(false, null, new Dictionary<string, string>(), "absent"));
            return Task.FromResult(new RemoteResourceInspection(true, "ref-" + name, labels, "running"));
        }
    }

    private sealed class FakeImageOperations : IRemoteWorkerOperations
    {
        public RemoteOperationResult Result { get; set; } = new(0, "", "none");
        public List<string> RequestedImages { get; } = [];
        /// <summary>ImageInspect result per reference; a missing key answers not-found.</summary>
        public Dictionary<string, RemoteOperationResult> Inspects { get; } = new(StringComparer.Ordinal);
        public Task<RemoteOperationResult> RemoveImageAsync(ExecutionTarget target, string imageReference, CancellationToken cancellationToken) { RequestedImages.Add(imageReference); return Task.FromResult(Result); }
        public Task<RemoteOperationResult> ExecuteAsync(ExecutionTarget target, RemoteDockerOperation operation, IReadOnlyList<string> tokens, byte[]? standardInput, CancellationToken cancellationToken)
        {
            if (operation != RemoteDockerOperation.ImageInspect) throw new NotSupportedException();
            // A digest reference always resolves to itself; a tag resolves only when
            // the test registered it, otherwise the image is provably absent.
            if (tokens.Count == 1 && Inspects.TryGetValue(tokens[0], out var configured)) return Task.FromResult(configured);
            if (tokens.Count == 1 && tokens[0].StartsWith("sha256:", StringComparison.Ordinal))
                return Task.FromResult(new RemoteOperationResult(0, $"[{{\"Id\":\"{tokens[0]}\"}}]", "none"));
            return Task.FromResult(new RemoteOperationResult(1, "", "not-found"));
        }
        public Task<RemoteOperationResult> CreateVolumeAsync(ExecutionTarget target, VolumeCreateSpec specification, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RemoteOperationResult> CreateContainerAsync(ExecutionTarget target, ContainerCreateSpec specification, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RemoteOperationResult> BootstrapAsync(ExecutionTarget target, BootstrapSpec specification, byte[] standardInput, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RemoteOperationResult> BuildImageAsync(ExecutionTarget target, ImageBuildSpec specification, byte[] contextTar, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<HostProbePayload> ProbeHostAsync(ExecutionTarget target, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agentcontrol-cleanup-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Path, true); } catch (IOException) { }
        }
    }
}
