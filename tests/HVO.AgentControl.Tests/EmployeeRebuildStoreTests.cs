using HVO.AgentControl.Organization;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Durable employee-rebuild operation records (schema v12). These tests run
/// without Docker or SSH: the verified target build is created through the real
/// profile-build state machine, the managed employee/binding through the real
/// approval path, and the enrollment is seeded as an operator-level row. No
/// image or container work is exercised because this slice records operations
/// only.
/// </summary>
public sealed class EmployeeRebuildStoreTests
{
    private const string FromDigest = "sha256:" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ToDigest = "sha256:" + "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string BaseDigest = "sha256:" + "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string ContextHash = "sha256:" + "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string EvidenceHash = "sha256:" + "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    [Fact]
    public void BeginFreezesTheExactIntentIsIdempotentAndIsQueryable()
    {
        using var root = new TempStore();
        using var fixture = SeedFixture(root);

        var created = fixture.Store.BeginEmployeeRebuild(Create(fixture));

        Assert.StartsWith(OrganizationIds.RebuildPrefix, created.Id, StringComparison.Ordinal);
        Assert.Equal(EmployeeRebuildStates.Intent, created.State);
        Assert.Equal(1, created.Revision);
        Assert.Equal(fixture.EmployeeId, created.EmployeeId);
        Assert.Equal(fixture.BindingId, created.RuntimeBindingId);
        Assert.Equal(fixture.WorkerId, created.WorkerId);
        Assert.Equal(ExecutionHosts.LocalDockerId, created.HostId);
        Assert.Equal(fixture.FromRevisionId, created.FromProfileRevisionId);
        Assert.Equal(FromDigest, created.FromImageDigest);
        Assert.Equal(1, created.FromRevisionNumber);
        Assert.Equal(fixture.ToRevisionId, created.ToProfileRevisionId);
        Assert.Equal(fixture.ToBuildId, created.ToProfileBuildId);
        Assert.Equal(ToDigest, created.ToImageDigest);
        Assert.Equal("linux/amd64", created.ToPlatform);
        Assert.False(created.ResetWorkspace);
        Assert.False(created.ResetHome);
        Assert.Null(created.ResetConfirmation);
        Assert.Equal(0, created.OwnershipEpochBefore);
        Assert.Null(created.OwnershipEpochAfter);
        Assert.Equal(DispatchHoldReasons.Manual, created.DispatchHoldReason);
        Assert.Equal("owner", created.RequestedBy);
        Assert.Null(created.EvidenceHash);
        Assert.Null(created.FailureSummary);

        Assert.Equal(created, fixture.Store.GetEmployeeRebuild(created.Id));
        Assert.Equal(created, fixture.Store.GetActiveEmployeeRebuild(fixture.WorkerId));
        Assert.Equal(created, Assert.Single(fixture.Store.ListEmployeeRebuilds(fixture.WorkerId)));
        Assert.Equal(created, Assert.Single(fixture.Store.ListEmployeeRebuilds()));

        // An identical replay returns the exact same active Intent rather than a
        // second row.
        var replay = fixture.Store.BeginEmployeeRebuild(Create(fixture));
        Assert.Equal(created, replay);
        Assert.Single(fixture.Store.ListEmployeeRebuilds(fixture.WorkerId));
    }

    [Fact]
    public void SecondActiveRebuildForTheSameWorkerIsRefusedAtTheStoreAndByTheUniqueIndex()
    {
        using var root = new TempStore();
        using var fixture = SeedFixture(root);
        var created = fixture.Store.BeginEmployeeRebuild(Create(fixture));

        // A different active intent is a conflict, never a second row.
        var different = Create(fixture, resetWorkspace: true, resetConfirmation: OrganizationStore.EmployeeRebuildResetConfirmation.Workspace);
        Assert.Throws<OrganizationConcurrencyException>(() => fixture.Store.BeginEmployeeRebuild(different));
        Assert.Single(fixture.Store.ListEmployeeRebuilds(fixture.WorkerId));

        // The durable boundary: the partial unique index refuses a second active
        // row even when the store check is bypassed.
        Assert.Throws<SqliteException>(() => ExecuteRaw(
            root.Path,
            $"""
            INSERT INTO employee_rebuilds (
                id, employee_id, runtime_binding_id, worker_id, host_id,
                from_profile_revision_id, from_image_digest, from_revision_number,
                to_profile_revision_id, to_profile_build_id, to_image_digest, to_platform,
                reset_workspace, reset_home, reset_confirmation, state,
                ownership_epoch_before, ownership_epoch_after, dispatch_hold_reason,
                requested_by, evidence_hash, failure_summary, revision, created_at, updated_at)
            SELECT 'rbld-00000000000000000000000000000001', employee_id, runtime_binding_id, worker_id, host_id,
                   from_profile_revision_id, from_image_digest, from_revision_number,
                   to_profile_revision_id, to_profile_build_id, to_image_digest, to_platform,
                   reset_workspace, reset_home, reset_confirmation, 'Intent',
                   ownership_epoch_before, NULL, 'manual', 'owner', NULL, NULL, 1, created_at, updated_at
            FROM employee_rebuilds WHERE id = '{created.Id}'
            """));

        // A terminal rebuild frees the slot for the next one.
        fixture.Store.TransitionEmployeeRebuild(created.Id, created.Revision, EmployeeRebuildStates.Intent, EmployeeRebuildStates.Failed, failureSummary: "not needed");
        Assert.Null(fixture.Store.GetActiveEmployeeRebuild(fixture.WorkerId));
        var next = fixture.Store.BeginEmployeeRebuild(Create(fixture));
        Assert.Equal(EmployeeRebuildStates.Intent, next.State);
        Assert.Equal(2, fixture.Store.ListEmployeeRebuilds(fixture.WorkerId).Count);
    }

    [Fact]
    public void ResetFlagsRequireTheExactConfirmationPhrase()
    {
        using (var root = new TempStore())
        using (var fixture = SeedFixture(root))
        {
            Assert.Throws<OrganizationValidationException>(() => fixture.Store.BeginEmployeeRebuild(Create(fixture, resetWorkspace: true)));
            Assert.Throws<OrganizationValidationException>(() => fixture.Store.BeginEmployeeRebuild(Create(fixture, resetWorkspace: true, resetConfirmation: "something-else")));
            Assert.Throws<OrganizationValidationException>(() => fixture.Store.BeginEmployeeRebuild(Create(fixture, resetHome: true, resetConfirmation: OrganizationStore.EmployeeRebuildResetConfirmation.Workspace)));
            Assert.Empty(fixture.Store.ListEmployeeRebuilds(fixture.WorkerId));
        }

        using (var root = new TempStore())
        using (var fixture = SeedFixture(root))
        {
            var created = fixture.Store.BeginEmployeeRebuild(Create(fixture, resetWorkspace: true, resetConfirmation: OrganizationStore.EmployeeRebuildResetConfirmation.Workspace));
            Assert.True(created.ResetWorkspace);
            Assert.False(created.ResetHome);
            Assert.Equal(OrganizationStore.EmployeeRebuildResetConfirmation.Workspace, created.ResetConfirmation);
        }

        using (var root = new TempStore())
        using (var fixture = SeedFixture(root))
        {
            var created = fixture.Store.BeginEmployeeRebuild(Create(fixture, resetWorkspace: true, resetHome: true, resetConfirmation: OrganizationStore.EmployeeRebuildResetConfirmation.WorkspaceAndHome));
            Assert.True(created.ResetWorkspace);
            Assert.True(created.ResetHome);
            Assert.Equal(OrganizationStore.EmployeeRebuildResetConfirmation.WorkspaceAndHome, created.ResetConfirmation);
        }
    }

    [Fact]
    public void BeginRejectsAStaleSourceWrongEpochAndIneligibleTarget()
    {
        using (var root = new TempStore())
        using (var fixture = SeedFixture(root))
        {
            // The frozen source digest must match the enrollment's current image.
            Assert.Throws<OrganizationConcurrencyException>(() => fixture.Store.BeginEmployeeRebuild(Create(fixture, fromDigest: ToDigest)));
        }

        using (var root = new TempStore())
        using (var fixture = SeedFixture(root))
        {
            // The frozen ownership epoch must match the enrollment.
            Assert.Throws<OrganizationConcurrencyException>(() => fixture.Store.BeginEmployeeRebuild(Create(fixture, epoch: 7)));
        }

        using (var root = new TempStore())
        using (var fixture = SeedFixture(root))
        {
            // A build that is not built and verified is not a target.
            var profile = fixture.Store.ListContainerProfiles().Single(p => p.Slug == ContainerProfileSeed.GenericEmployeeSlug);
            var queuedRevision = fixture.Store.CreateContainerProfileRevision(profile.Id, new ContainerProfileRevisionCreate(
                fixture.Store.GetContainerProfile(profile.Id)!.Profile.Revision,
                """{"image":"agentcontrol-worker-base","name":"Queued Target"}""",
                null));
            var live = fixture.Store.QueueProfileBuild(queuedRevision.Id, ExecutionHosts.LocalDockerId, BaseDigest, "linux/amd64", ContextHash, "agentcontrol-profile:queued");
            Assert.Equal(ProfileBuildStates.Queued, live.State);
            Assert.Throws<OrganizationValidationException>(() => fixture.Store.BeginEmployeeRebuild(Create(fixture, toRevisionId: queuedRevision.Id, toBuildId: live.Id, toDigest: ToDigest)));
        }

        using (var root = new TempStore())
        using (var fixture = SeedFixture(root))
        {
            // A platform mismatch on the target build is rejected.
            Assert.Throws<OrganizationValidationException>(() => fixture.Store.BeginEmployeeRebuild(Create(fixture, toPlatform: "linux/arm64")));
        }

        using (var root = new TempStore())
        using (var fixture = SeedFixture(root))
        {
            // A target build that lives on another host is rejected: the worker is
            // re-homed to an ssh host while the verified target stays local.
            fixture.Store.RegisterExecutionHost("ssh-other", "other.example", 22, "docker", "/known", new ExecutionHostRegistration("ssh-other", "ssh-other", "SSH Other"));
            ExecuteRaw(root.Path, $"UPDATE worker_enrollments SET host_id = 'ssh-other' WHERE worker_id = '{fixture.WorkerId}'");
            Assert.Throws<OrganizationValidationException>(() => fixture.Store.BeginEmployeeRebuild(Create(fixture, hostId: "ssh-other")));
        }
    }

    [Fact]
    public void StateMachineIsFixedImmutableAndNeverDeletedOrReplaced()
    {
        using var root = new TempStore();
        using var fixture = SeedFixture(root);
        var created = fixture.Store.BeginEmployeeRebuild(Create(fixture));
        var id = created.Id;

        // Frozen columns and identity are immutable.
        Assert.Throws<SqliteException>(() => ExecuteRaw(root.Path, $"UPDATE employee_rebuilds SET employee_id = employee_id WHERE id = '{id}'"));
        Assert.Throws<SqliteException>(() => ExecuteRaw(root.Path, $"UPDATE employee_rebuilds SET from_image_digest = from_image_digest WHERE id = '{id}'"));
        Assert.Throws<SqliteException>(() => ExecuteRaw(root.Path, $"UPDATE employee_rebuilds SET reset_confirmation = reset_confirmation WHERE id = '{id}'"));

        // No delete and no replace.
        Assert.Throws<SqliteException>(() => ExecuteRaw(root.Path, $"DELETE FROM employee_rebuilds WHERE id = '{id}'"));
        Assert.Throws<SqliteException>(() => ExecuteRaw(
            root.Path,
            $"""
            INSERT INTO employee_rebuilds (
                id, employee_id, runtime_binding_id, worker_id, host_id,
                from_profile_revision_id, from_image_digest, from_revision_number,
                to_profile_revision_id, to_profile_build_id, to_image_digest, to_platform,
                reset_workspace, reset_home, reset_confirmation, state,
                ownership_epoch_before, ownership_epoch_after, dispatch_hold_reason,
                requested_by, evidence_hash, failure_summary, revision, created_at, updated_at)
            SELECT id, employee_id, runtime_binding_id, worker_id, host_id,
                   from_profile_revision_id, from_image_digest, from_revision_number,
                   to_profile_revision_id, to_profile_build_id, to_image_digest, to_platform,
                   reset_workspace, reset_home, reset_confirmation, 'Intent',
                   ownership_epoch_before, NULL, 'manual', 'owner', NULL, NULL, 1, created_at, updated_at
            FROM employee_rebuilds WHERE id = '{id}'
            """));

        // Invalid partial transitions are refused before any write.
        Assert.Throws<OrganizationConcurrencyException>(() => fixture.Store.TransitionEmployeeRebuild(id, created.Revision, EmployeeRebuildStates.Intent, EmployeeRebuildStates.Applied, evidenceHash: EvidenceHash, ownershipEpochAfter: 1));
        Assert.Throws<OrganizationConcurrencyException>(() => fixture.Store.TransitionEmployeeRebuild(id, created.Revision, EmployeeRebuildStates.Intent, EmployeeRebuildStates.Verifying));

        // The fixed chain advances one revision at a time.
        var holding = fixture.Store.TransitionEmployeeRebuild(id, created.Revision, EmployeeRebuildStates.Intent, EmployeeRebuildStates.Holding);
        Assert.Equal(EmployeeRebuildStates.Holding, holding.State);
        Assert.Equal(created.Revision + 1, holding.Revision);
        Assert.Throws<OrganizationConcurrencyException>(() => fixture.Store.TransitionEmployeeRebuild(id, created.Revision, EmployeeRebuildStates.Intent, EmployeeRebuildStates.Holding));
        var replacing = fixture.Store.TransitionEmployeeRebuild(id, holding.Revision, EmployeeRebuildStates.Holding, EmployeeRebuildStates.Replacing);
        var verifying = fixture.Store.TransitionEmployeeRebuild(id, replacing.Revision, EmployeeRebuildStates.Replacing, EmployeeRebuildStates.Verifying);
        var applied = fixture.Store.TransitionEmployeeRebuild(id, verifying.Revision, EmployeeRebuildStates.Verifying, EmployeeRebuildStates.Applied, evidenceHash: EvidenceHash, ownershipEpochAfter: 5);
        Assert.Equal(EmployeeRebuildStates.Applied, applied.State);
        Assert.Equal(EvidenceHash, applied.EvidenceHash);
        Assert.Equal(5, applied.OwnershipEpochAfter);
        Assert.Null(applied.FailureSummary);

        // Applied is terminal.
        Assert.Throws<OrganizationConcurrencyException>(() => fixture.Store.TransitionEmployeeRebuild(id, applied.Revision, EmployeeRebuildStates.Applied, EmployeeRebuildStates.Holding));
        Assert.Equal(applied, fixture.Store.GetEmployeeRebuild(id));
        Assert.Equal(1, RawScalar(root.Path, "SELECT COUNT(*) FROM employee_rebuilds;"));
    }

    [Fact]
    public void FailedRequiresASanitizedSummaryAndAppliedClearsIt()
    {
        using (var root = new TempStore())
        using (var fixture = SeedFixture(root))
        {
            var created = fixture.Store.BeginEmployeeRebuild(Create(fixture));
            Assert.Throws<OrganizationValidationException>(() => fixture.Store.TransitionEmployeeRebuild(created.Id, created.Revision, EmployeeRebuildStates.Intent, EmployeeRebuildStates.Failed));
            var failed = fixture.Store.TransitionEmployeeRebuild(created.Id, created.Revision, EmployeeRebuildStates.Intent, EmployeeRebuildStates.Failed, failureSummary: "bad\u0001failure ");
            Assert.Equal(EmployeeRebuildStates.Failed, failed.State);
            Assert.Equal("badfailure", failed.FailureSummary);
        }

        using (var root = new TempStore())
        using (var fixture = SeedFixture(root))
        {
            // An applied transition requires evidence and the post-rebuild epoch.
            var created = fixture.Store.BeginEmployeeRebuild(Create(fixture));
            var holding = fixture.Store.TransitionEmployeeRebuild(created.Id, created.Revision, EmployeeRebuildStates.Intent, EmployeeRebuildStates.Holding);
            var replacing = fixture.Store.TransitionEmployeeRebuild(created.Id, holding.Revision, EmployeeRebuildStates.Holding, EmployeeRebuildStates.Replacing);
            var uncertain = fixture.Store.TransitionEmployeeRebuild(created.Id, replacing.Revision, EmployeeRebuildStates.Replacing, EmployeeRebuildStates.Uncertain, failureSummary: "controller stopped during rebuild");
            Assert.Equal(EmployeeRebuildStates.Uncertain, uncertain.State);
            Assert.Equal("controller stopped during rebuild", uncertain.FailureSummary);

            Assert.Throws<OrganizationValidationException>(() => fixture.Store.TransitionEmployeeRebuild(created.Id, uncertain.Revision, EmployeeRebuildStates.Uncertain, EmployeeRebuildStates.Applied, ownershipEpochAfter: 3));
            Assert.Throws<OrganizationValidationException>(() => fixture.Store.TransitionEmployeeRebuild(created.Id, uncertain.Revision, EmployeeRebuildStates.Uncertain, EmployeeRebuildStates.Applied, evidenceHash: EvidenceHash));
            var applied = fixture.Store.TransitionEmployeeRebuild(created.Id, uncertain.Revision, EmployeeRebuildStates.Uncertain, EmployeeRebuildStates.Applied, evidenceHash: EvidenceHash, ownershipEpochAfter: 3);
            Assert.Equal(EmployeeRebuildStates.Applied, applied.State);
            Assert.Null(applied.FailureSummary);
            Assert.Equal(EvidenceHash, applied.EvidenceHash);
            Assert.Equal(3, applied.OwnershipEpochAfter);
        }
    }

    [Fact]
    public void MarkInterruptedRebuildsUncertainIsMarkOnceAndKeepsTheActiveSlot()
    {
        using var root = new TempStore();
        using var fixture = SeedFixture(root);

        // Two workers so two independently interrupted states can be marked.
        var first = fixture.Store.BeginEmployeeRebuild(Create(fixture));
        var firstHolding = fixture.Store.TransitionEmployeeRebuild(first.Id, first.Revision, EmployeeRebuildStates.Intent, EmployeeRebuildStates.Holding);

        var secondWorker = AddManagedWorker(fixture.Store, root.Path, FromDigest, fixture.ToRevisionId, "Rebuild Worker Two", "wrk-rebuild-2");
        var second = fixture.Store.BeginEmployeeRebuild(Create(fixture, worker: secondWorker));
        var secondHolding = fixture.Store.TransitionEmployeeRebuild(second.Id, second.Revision, EmployeeRebuildStates.Intent, EmployeeRebuildStates.Holding);
        var secondReplacing = fixture.Store.TransitionEmployeeRebuild(second.Id, secondHolding.Revision, EmployeeRebuildStates.Holding, EmployeeRebuildStates.Replacing);
        var secondVerifying = fixture.Store.TransitionEmployeeRebuild(second.Id, secondReplacing.Revision, EmployeeRebuildStates.Replacing, EmployeeRebuildStates.Verifying);

        var marked = fixture.Store.MarkInterruptedEmployeeRebuildsUncertain();
        Assert.Equal(2, marked.Count);
        Assert.Contains(first.Id, marked);
        Assert.Contains(second.Id, marked);

        var firstMarked = fixture.Store.GetEmployeeRebuild(first.Id)!;
        Assert.Equal(EmployeeRebuildStates.Uncertain, firstMarked.State);
        Assert.Equal("controller-restarted-during-rebuild", firstMarked.FailureSummary);
        Assert.Equal(firstHolding.Revision + 1, firstMarked.Revision);
        Assert.Equal(EmployeeRebuildStates.Uncertain, fixture.Store.GetEmployeeRebuild(second.Id)!.State);
        _ = secondVerifying;

        // The uncertain rows still hold the single active slot.
        Assert.NotNull(fixture.Store.GetActiveEmployeeRebuild(fixture.WorkerId));
        Assert.NotNull(fixture.Store.GetActiveEmployeeRebuild(secondWorker.WorkerId));

        // Mark-once: a second startup finds nothing and changes nothing.
        var revisionBefore = fixture.Store.GetEmployeeRebuild(first.Id)!.Revision;
        Assert.Empty(fixture.Store.MarkInterruptedEmployeeRebuildsUncertain());
        Assert.Equal(revisionBefore, fixture.Store.GetEmployeeRebuild(first.Id)!.Revision);
    }

    [Fact]
    public void ProfileStatusIsNullForNonManagedEmployeesAndInvalidIds()
    {
        using var root = new TempStore();
        using var fixture = SeedFixture(root);

        // The internal seed employee is not a managed employee: no managed
        // enrollment resources exist for its binding.
        var seed = fixture.Store.GetOverview().Employees.Single(e => e.Slug == OrganizationSeed.AdoptedEmployeeSlug);
        Assert.Null(fixture.Store.GetEmployeeProfileStatus(seed.Id));

        Assert.Null(fixture.Store.GetEmployeeProfileStatus("not-an-employee"));
        Assert.Null(fixture.Store.GetEmployeeProfileStatus("emp-0000000000000000"));
    }

    [Fact]
    public void ProfileStatusReportsTheFrozenCurrentRevisionAndDigest()
    {
        using var root = new TempStore();
        using var fixture = SeedFixture(root);

        var status = fixture.Store.GetEmployeeProfileStatus(fixture.EmployeeId);

        Assert.NotNull(status);
        Assert.Equal(fixture.EmployeeId, status!.EmployeeId);
        Assert.Equal(fixture.BindingId, status.RuntimeBindingId);
        Assert.Equal(fixture.WorkerId, status.WorkerId);
        Assert.Equal(ExecutionHosts.LocalDockerId, status.HostId);
        Assert.Equal(fixture.ToRevisionId, status.CurrentProfileRevisionId);
        Assert.Equal(2, status.CurrentRevisionNumber);
        Assert.Equal(ToDigest, status.CurrentImageDigest);
        Assert.Equal("linux/amd64", status.CurrentPlatform);
        Assert.False(string.IsNullOrWhiteSpace(status.CurrentProfileId));
        Assert.Equal(ContainerProfileSeed.GenericEmployeeDisplayName, status.CurrentProfileDisplayName);

        // The profile's current revision is the frozen one, so nothing newer.
        Assert.False(status.NewerRevisionAvailable);
        Assert.Null(status.NewerRevisionId);
        Assert.Null(status.NewerRevisionNumber);
        Assert.Null(status.NewerVerifiedBuildId);
        Assert.Null(status.NewerVerifiedImageDigest);
        Assert.Null(status.ActiveRebuild);
    }

    [Fact]
    public void NewerRevisionIsReportedOnlyWhenAVerifiedBuildExistsOnTheEmployeeHost()
    {
        using var root = new TempStore();
        using var fixture = SeedFixture(root);

        // A newer (third) revision exists but has no verified build yet: the
        // status must not offer it as a ready target.
        var newer = fixture.Store.CreateContainerProfileRevision(
            fixture.ProfileId,
            new ContainerProfileRevisionCreate(
                fixture.Store.GetContainerProfile(fixture.ProfileId)!.Profile.Revision,
                """{"image":"agentcontrol-worker-base","name":"Rebuild Target Three"}""",
                null));
        var unbuilt = fixture.Store.GetEmployeeProfileStatus(fixture.EmployeeId)!;
        Assert.False(unbuilt.NewerRevisionAvailable);
        Assert.Null(unbuilt.NewerRevisionId);

        // Once the newer revision has a verified build on the employee's host it
        // becomes the offered target with its exact build id and digest.
        var build = BuildVerified(fixture.Store, newer.Id, ToDigest, "linux/amd64");
        var available = fixture.Store.GetEmployeeProfileStatus(fixture.EmployeeId)!;
        Assert.True(available.NewerRevisionAvailable);
        Assert.Equal(newer.Id, available.NewerRevisionId);
        Assert.Equal(3, available.NewerRevisionNumber);
        Assert.Equal(build.Id, available.NewerVerifiedBuildId);
        Assert.Equal(ToDigest, available.NewerVerifiedImageDigest);
    }

    [Fact]
    public void ActiveRebuildSurfacesAndTerminalRebuildDoesNot()
    {
        using var root = new TempStore();
        using var fixture = SeedFixture(root);

        var created = fixture.Store.BeginEmployeeRebuild(Create(fixture));
        var active = fixture.Store.GetEmployeeProfileStatus(fixture.EmployeeId)!.ActiveRebuild;
        Assert.NotNull(active);
        Assert.Equal(created.Id, active!.Id);
        Assert.Equal(EmployeeRebuildStates.Intent, active.State);

        // A terminal rebuild is historical, not the active operation.
        fixture.Store.TransitionEmployeeRebuild(created.Id, created.Revision, EmployeeRebuildStates.Intent, EmployeeRebuildStates.Failed, failureSummary: "not needed");
        Assert.Null(fixture.Store.GetEmployeeProfileStatus(fixture.EmployeeId)!.ActiveRebuild);
    }

    // ---------------------------------------------------------------- helpers

    private sealed record ManagedWorker(string EmployeeId, string BindingId, string WorkerId);

    private sealed class RebuildFixture : IDisposable
    {
        public RebuildFixture(TempStore root, OrganizationStore store, string employeeId, string bindingId, string workerId, string profileId, string fromRevisionId, string toRevisionId, string toBuildId)
        {
            Root = root;
            Store = store;
            EmployeeId = employeeId;
            BindingId = bindingId;
            WorkerId = workerId;
            ProfileId = profileId;
            FromRevisionId = fromRevisionId;
            ToRevisionId = toRevisionId;
            ToBuildId = toBuildId;
        }

        public TempStore Root { get; }
        public OrganizationStore Store { get; }
        public string EmployeeId { get; }
        public string BindingId { get; }
        public string WorkerId { get; }
        public string ProfileId { get; }
        public string FromRevisionId { get; }
        public string ToRevisionId { get; }
        public string ToBuildId { get; }

        public void Dispose() => Store.Dispose();
    }

    private static RebuildFixture SeedFixture(TempStore root)
    {
        var store = Open(root);
        var host = store.GetExecutionHost(ExecutionHosts.LocalDockerId)!;
        store.RecordLocalExecutionHostProbe(ExecutionHosts.LocalDockerId, host.Revision, new LocalExecutionHostProbe(
            "29.0", "1.51", "x86_64", "overlay2", "ext4", false, 64L << 30, 16L << 30, 8, true, "linux/amd64", "valid"));

        var profile = store.ListContainerProfiles().Single(p => p.Slug == ContainerProfileSeed.GenericEmployeeSlug);
        var fromRevision = store.GetContainerProfile(profile.Id)!.Revisions.Single();
        var toRevision = store.CreateContainerProfileRevision(profile.Id, new ContainerProfileRevisionCreate(
            store.GetContainerProfile(profile.Id)!.Profile.Revision,
            """{"image":"agentcontrol-worker-base","name":"Rebuild Target"}""",
            null));
        var toBuild = BuildVerified(store, toRevision.Id, ToDigest, "linux/amd64");
        var worker = AddManagedWorker(store, root.Path, FromDigest, toRevision.Id, "Rebuild Worker", "wrk-rebuild-1");
        return new RebuildFixture(root, store, worker.EmployeeId, worker.BindingId, worker.WorkerId, profile.Id, fromRevision.Id, toRevision.Id, toBuild.Id);
    }

    private static ProfileBuildRecord BuildVerified(OrganizationStore store, string revisionId, string imageDigest, string platform)
    {
        var queued = store.QueueProfileBuild(revisionId, ExecutionHosts.LocalDockerId, BaseDigest, platform, ContextHash, "agentcontrol-profile:x");
        var building = store.TransitionProfileBuild(queued.Id, queued.Revision, ProfileBuildStates.Building);
        var verifying = store.TransitionProfileBuild(building.Id, building.Revision, ProfileBuildStates.Verifying);
        return store.TransitionProfileBuild(verifying.Id, verifying.Revision, ProfileBuildStates.Built, imageDigest: imageDigest, verified: true, evidenceHash: ContextHash);
    }

    private static ManagedWorker AddManagedWorker(OrganizationStore store, string path, string fromDigest, string toRevisionId, string displayName, string workerId)
    {
        var overview = store.GetOverview();
        var department = overview.Departments.Single(d => d.Slug == OrganizationSeed.OperationsSlug);
        var role = Assert.Single(overview.Roles);
        var hire = store.CreateHireRequest(new HireRequestCreate(
            Guid.NewGuid().ToString("N"), displayName, "Employee rebuild fixture.", department.Id, role.Id,
            RuntimePlacements.DeveloperContainer, 2, 2048, 256), null);
        store.ApproveHireRequest(hire.Id, new HireRequestApprove(hire.Revision, toRevisionId), "owner");
        var creation = store.CreateManagedEmployeeFromHire(hire.Id);
        SeedWorkerEnrollment(path, creation.RuntimeBindingId, workerId, fromDigest);
        return new ManagedWorker(creation.EmployeeId, creation.RuntimeBindingId, workerId);
    }

    private static EmployeeRebuildCreate Create(
        RebuildFixture fixture,
        ManagedWorker? worker = null,
        bool resetWorkspace = false,
        bool resetHome = false,
        string? resetConfirmation = null,
        string? fromDigest = null,
        long epoch = 0,
        string? toBuildId = null,
        string? toRevisionId = null,
        string? toDigest = null,
        string? toPlatform = "linux/amd64",
        string? hostId = null,
        int fromRevisionNumber = 1)
    {
        var target = worker ?? new ManagedWorker(fixture.EmployeeId, fixture.BindingId, fixture.WorkerId);
        return new EmployeeRebuildCreate(
            target.EmployeeId,
            target.BindingId,
            target.WorkerId,
            hostId ?? ExecutionHosts.LocalDockerId,
            fixture.FromRevisionId,
            fromDigest ?? FromDigest,
            fromRevisionNumber,
            toRevisionId ?? fixture.ToRevisionId,
            toBuildId ?? fixture.ToBuildId,
            toDigest ?? ToDigest,
            toPlatform!,
            resetWorkspace,
            resetHome,
            resetConfirmation,
            epoch);
    }

    private static void SeedWorkerEnrollment(string path, string bindingId, string workerId, string expectedImageDigest)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        ExecuteRaw(
            path,
            $"""
            INSERT INTO worker_enrollments (
                worker_id, runtime_binding_id, host_id, organization_id,
                container_name, control_volume_name, home_volume_name, workspace_volume_name, session_volume_name,
                resource_labels_hash, expected_image_digest, expected_platform, controller_id, key_file_path, key_id,
                bridge_socket_path, lifecycle_status, worker_generation, process_generation, ownership_epoch, enabled,
                created_at, updated_at, revision)
            VALUES ('{workerId}', '{bindingId}', '{ExecutionHosts.LocalDockerId}', (SELECT id FROM organizations LIMIT 1),
                'container-{workerId}', 'control-{workerId}', 'home-{workerId}', 'workspace-{workerId}', 'session-{workerId}',
                'sha256:{new string('a', 64)}', '{expectedImageDigest}', 'linux/amd64', 'controller-rebuild', '/control/key',
                'sha256:{new string('c', 64)}', '/control/bridge.sock', 'planned', 0, 0, 0, 1, '{now}', '{now}', 1)
            """);
    }

    private static OrganizationStore Open(TempStore root)
    {
        var store = new OrganizationStore(root.Path, lockTimeout: TimeSpan.FromSeconds(5));
        store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        return store;
    }

    private static int RawScalar(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void ExecuteRaw(string path, string sql)
    {
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

    private sealed class TempStore : IDisposable
    {
        public TempStore()
        {
            Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agentcontrol-rebuild-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Path = System.IO.Path.Combine(Directory, OrganizationStore.DatabaseFileName);
        }

        public string Directory { get; }

        public string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
