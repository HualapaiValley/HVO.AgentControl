using HVO.AgentControl.Organization;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Owner approval and managed-employee creation (schema v10). These tests run
/// without Docker or SSH: the verified profile build is created through the real
/// store state machine, and the approval/hire rows are asserted directly.
/// </summary>
public sealed class HireApprovalStoreTests
{
    private const string BaseDigest = "sha256:" + "1111111111111111111111111111111111111111111111111111111111111111";
    private const string BuiltDigest = "sha256:" + "2222222222222222222222222222222222222222222222222222222222222222";
    private const string ContextHash = "sha256:" + "3333333333333333333333333333333333333333333333333333333333333333";

    [Fact]
    public void ApprovalFreezesTheExactSelectionIsImmutableAndReplaysIdempotently()
    {
        using var root = new TempStore();
        using var store = Open(root);
        var seeded = SeedApprovedHire(store, "Developer One", 2, 2048, 256, out var revisionId, out _);

        var request = new HireRequestApprove(seeded.Hire.Revision, revisionId, "host-a");
        var approved = store.ApproveHireRequest(seeded.Hire.Id, request, "owner");

        Assert.Equal(HireRequestStates.Approved, approved.State);
        Assert.Equal(seeded.Hire.Revision + 1, approved.Revision);
        Assert.Equal(revisionId, approved.ContainerProfileRevisionId);
        Assert.NotNull(approved.ApprovedRequestVersion);
        Assert.StartsWith("sha256:", approved.ApprovedRequestVersion, StringComparison.Ordinal);
        Assert.Equal("owner", approved.OwnerApproval);
        Assert.Null(approved.StatusDetail);

        var approval = store.GetHireRequestApproval(seeded.Hire.Id)!;
        Assert.Equal(approved.ApprovedRequestVersion, approval.ApprovedRequestVersion);
        Assert.Equal(seeded.Hire.Revision, approval.ApprovedRequestRevision);
        Assert.Equal(seeded.Hire.RequestVersionHash, approval.RequestVersionHash);
        Assert.Equal(revisionId, approval.ProfileRevisionId);
        Assert.Equal(seeded.Build.Id, approval.ProfileBuildId);
        Assert.Equal(BuiltDigest, approval.ImageDigest);
        Assert.Equal("host-a", approval.HostId);
        Assert.Equal("linux/amd64", approval.Platform);
        Assert.Equal(2, approval.CpuLimit);
        Assert.Equal(2048, approval.MemoryLimitMiB);
        Assert.Equal(256, approval.PidsLimit);
        Assert.Null(approval.EmployeeId);
        Assert.Null(approval.RuntimeBindingId);
        Assert.Null(approval.WorkerId);
        Assert.Equal(1, approval.Revision);

        // The request carries the joined approval fields for the API/UI.
        var summary = store.GetHireRequest(seeded.Hire.Id)!;
        Assert.Equal(seeded.Build.Id, summary.ProfileBuildId);
        Assert.Equal(BuiltDigest, summary.ApprovedImageDigest);
        Assert.Equal("host-a", summary.ApprovedHostId);
        Assert.Equal(2, RawScalar(root.Path, $"SELECT COUNT(*) FROM hire_request_events WHERE hire_request_id = '{seeded.Hire.Id}';"));

        // A replay of the exact same selection and request revision is idempotent.
        var replay = store.ApproveHireRequest(seeded.Hire.Id, request, "owner");
        Assert.Equal(approved, replay);

        // A different selection or a changed request is a conflict, never a second freeze.
        Assert.Throws<OrganizationConcurrencyException>(() =>
            store.ApproveHireRequest(seeded.Hire.Id, request with { ProfileRevisionId = "prev-0000000000000000" }, "owner"));
        Assert.Throws<OrganizationConcurrencyException>(() =>
            store.ApproveHireRequest(seeded.Hire.Id, request with { ExpectedRevision = seeded.Hire.Revision + 5 }, "owner"));
        Assert.Throws<OrganizationValidationException>(() =>
            store.ApproveHireRequest(seeded.Hire.Id, request, ""));
        Assert.Equal(1, RawScalar(root.Path, $"SELECT COUNT(*) FROM hire_request_approvals WHERE hire_request_id = '{seeded.Hire.Id}';"));
    }

    [Fact]
    public void ApprovalRefusesMissingOrIncompatibleBuildsHostsAndRevisions()
    {
        // A current revision with no verified build at all.
        using (var root = new TempStore())
        using (var store = Open(root))
        {
            PrepareHost(store, limitsSupported: true);
            var unbuiltProfile = store.CreateContainerProfile(new ContainerProfileCreate("unbuilt", "unbuilt", "Unbuilt", null, """{"image":"agentcontrol-worker-base","name":"Unbuilt"}""", null), null);
            var unbuiltRevision = store.GetContainerProfile(unbuiltProfile.Id)!.Revisions.Single();
            var unbuiltHire = CreateDevHire(store, "Unbuilt Hire", 1, 512, 64);
            Assert.Throws<OrganizationConcurrencyException>(() =>
                store.ApproveHireRequest(unbuiltHire.Id, new HireRequestApprove(unbuiltHire.Revision, unbuiltRevision.Id, "host-a"), "owner"));
        }

        // Requested resources beyond the probed host capacity.
        using (var root = new TempStore())
        using (var store = Open(root))
        {
            var seed = SeedApprovedHire(store, "Too Big", 64, 2048, 256, out var revisionId, out _);
            Assert.Throws<OrganizationValidationException>(() =>
                store.ApproveHireRequest(seed.Hire.Id, new HireRequestApprove(seed.Hire.Revision, revisionId, "host-a"), "owner"));
        }

        // A host that reports it cannot enforce limits.
        using (var root = new TempStore())
        using (var store = Open(root))
        {
            var build = SeedVerifiedBuild(store, out var revisionId, limitsSupported: false);
            var hire = CreateDevHire(store, "No Limits", 1, 512, 64);
            Assert.Throws<OrganizationValidationException>(() =>
                store.ApproveHireRequest(hire.Id, new HireRequestApprove(hire.Revision, revisionId, "host-a"), "owner"));
            Assert.Equal(1, RawScalar(root.Path, "SELECT COUNT(*) FROM profile_builds WHERE id = '" + build.Id + "';"));
        }

        // A disabled host.
        using (var root = new TempStore())
        using (var store = Open(root))
        {
            SeedVerifiedBuild(store, out var revisionId);
            RawExec(root.Path, "UPDATE execution_hosts SET enabled = 0 WHERE id = 'host-a';");
            var hire = CreateDevHire(store, "Disabled", 1, 512, 64);
            Assert.Throws<OrganizationConcurrencyException>(() =>
                store.ApproveHireRequest(hire.Id, new HireRequestApprove(hire.Revision, revisionId, "host-a"), "owner"));
        }

        // A host whose platform does not match the verified build.
        using (var root = new TempStore())
        using (var store = Open(root))
        {
            store.RegisterExecutionHost("host-a", "host-a.example", 22, "roys", "/known", new ExecutionHostRegistration("host-a", "host-a", "Host A"));
            var host = store.GetExecutionHost("host-a")!;
            store.RecordExecutionHostProbe("host-a", host.Revision, new ExecutionHostProbe(
                "ssh-ed25519", "SHA256:x", "sha256:" + new string('1', 64), "29.0", "1.51", "aarch64", "overlay2", "ext4",
                false, 64L << 30, 16L << 30, 8, true, "linux/arm64", "valid"));
            var profile = store.ListContainerProfiles().Single(p => p.Slug == ContainerProfileSeed.GenericEmployeeSlug);
            var revisionId = store.GetContainerProfile(profile.Id)!.Revisions.Single().Id;
            var queued = store.QueueProfileBuild(revisionId, "host-a", BaseDigest, "linux/amd64", ContextHash, "agentcontrol-profile:x");
            var building = store.TransitionProfileBuild(queued.Id, queued.Revision, ProfileBuildStates.Building);
            var verifying = store.TransitionProfileBuild(building.Id, building.Revision, ProfileBuildStates.Verifying);
            store.TransitionProfileBuild(verifying.Id, verifying.Revision, ProfileBuildStates.Built, imageDigest: BuiltDigest, verified: true, evidenceHash: ContextHash);
            var hire = CreateDevHire(store, "Wrong Platform", 1, 512, 64);
            Assert.Throws<OrganizationValidationException>(() =>
                store.ApproveHireRequest(hire.Id, new HireRequestApprove(hire.Revision, revisionId, "host-a"), "owner"));
        }

        // A retired profile and a non-current revision.
        using (var root = new TempStore())
        using (var store = Open(root))
        {
            var seed = SeedApprovedHire(store, "Retired", 2, 2048, 256, out var revisionId, out var profileId);
            var second = store.CreateContainerProfileRevision(profileId, new ContainerProfileRevisionCreate(
                store.GetContainerProfile(profileId)!.Profile.Revision,
                """{"image":"agentcontrol-worker-base","name":"Second"}""",
                null));

            // The original revision is no longer current.
            var oldHire = CreateDevHire(store, "Old Revision", 1, 512, 64);
            Assert.Throws<OrganizationConcurrencyException>(() =>
                store.ApproveHireRequest(oldHire.Id, new HireRequestApprove(oldHire.Revision, revisionId, "host-a"), "owner"));

            // A retired profile cannot be approved even for its current revision.
            store.RetireContainerProfile(profileId, store.GetContainerProfile(profileId)!.Profile.Revision);
            var retiredHire = CreateDevHire(store, "Retired Hire", 1, 512, 64);
            Assert.Throws<OrganizationConcurrencyException>(() =>
                store.ApproveHireRequest(retiredHire.Id, new HireRequestApprove(retiredHire.Revision, second.Id, "host-a"), "owner"));

            _ = seed;
        }
    }

    [Fact]
    public void ManagedEmployeeCreationIsAtomicIdempotentAndResolutionSafe()
    {
        using var root = new TempStore();
        using var store = Open(root);
        var first = SeedApprovedHire(store, "Developer One", 2, 2048, 256, out var revisionId, out _);
        store.ApproveHireRequest(first.Hire.Id, new HireRequestApprove(first.Hire.Revision, revisionId, "host-a"), "owner");

        var creation = store.CreateManagedEmployeeFromHire(first.Hire.Id);
        Assert.True(creation.Created);
        Assert.Equal(RuntimePlacements.DeveloperContainer, creation.Placement);
        Assert.Equal(revisionId, creation.ApprovedProfileRevisionId);
        Assert.Equal(first.Build.Id, creation.ApprovedProfileBuildId);
        Assert.Equal(BuiltDigest, creation.ApprovedImageDigest);
        Assert.Equal("host-a", creation.ApprovedHostId);
        Assert.StartsWith("emp-", creation.EmployeeId, StringComparison.Ordinal);
        Assert.StartsWith("rtb-", creation.RuntimeBindingId, StringComparison.Ordinal);
        Assert.Null(creation.WorkerId);

        // Exactly one employee, one binding, one frozen resource row; refs are NULL.
        Assert.Equal(1, RawScalar(root.Path, $"SELECT COUNT(*) FROM employees WHERE id = '{creation.EmployeeId}';"));
        Assert.Equal(1, RawScalar(root.Path, $"SELECT COUNT(*) FROM runtime_bindings WHERE id = '{creation.RuntimeBindingId}';"));
        Assert.Equal(1, RawScalar(root.Path, $"SELECT COUNT(*) FROM managed_enrollment_resources WHERE runtime_binding_id = '{creation.RuntimeBindingId}';"));
        Assert.Equal(1, RawScalar(root.Path, $"SELECT COUNT(*) FROM runtime_bindings WHERE id = '{creation.RuntimeBindingId}' AND placement = 'DeveloperContainer' AND container_ref IS NULL AND volume_ref IS NULL AND home_ref IS NULL AND workspace_ref IS NULL AND session_ref IS NULL;"));
        Assert.Equal(1, RawScalar(root.Path, $"SELECT COUNT(*) FROM employees WHERE id = '{creation.EmployeeId}' AND instructions <> '' AND rules <> '' AND restrictions <> '';"));
        Assert.Equal(1, RawScalar(root.Path, $"SELECT COUNT(*) FROM employees WHERE id = '{creation.EmployeeId}' AND restrictions LIKE '%Docker%';"));

        var approval = store.GetHireRequestApproval(first.Hire.Id)!;
        Assert.Equal(creation.EmployeeId, approval.EmployeeId);
        Assert.Equal(creation.RuntimeBindingId, approval.RuntimeBindingId);
        Assert.Equal(2, approval.Revision);
        var resources = store.GetManagedEnrollmentResources(creation.RuntimeBindingId)!;
        Assert.Equal(approval.CpuLimit, resources.CpuLimit);
        Assert.Equal(approval.ImageDigest, resources.ApprovedImageDigest);
        Assert.Equal(approval.HostId, resources.ApprovedHostId);

        // The hire state is never advanced by employee creation.
        Assert.Equal(HireRequestStates.Approved, store.GetHireRequest(first.Hire.Id)!.State);

        // Replay returns the same identities and creates nothing new.
        var replay = store.CreateManagedEmployeeFromHire(first.Hire.Id);
        Assert.False(replay.Created);
        Assert.Equal(creation.EmployeeId, replay.EmployeeId);
        Assert.Equal(creation.RuntimeBindingId, replay.RuntimeBindingId);
        Assert.Equal(creation.Slug, replay.Slug);
        Assert.Equal(creation.DisplayName, replay.DisplayName);
        Assert.Equal(1, RawScalar(root.Path, "SELECT COUNT(*) FROM employees WHERE slug = '" + creation.Slug + "';"));

        // Two requests for the same display name produce unique slugs and display names.
        var second = SeedApprovedHire(store, "Developer One", 2, 2048, 256, out var secondRevisionId, out _);
        store.ApproveHireRequest(second.Hire.Id, new HireRequestApprove(second.Hire.Revision, secondRevisionId, "host-a"), "owner");
        var secondCreation = store.CreateManagedEmployeeFromHire(second.Hire.Id);
        Assert.True(secondCreation.Created);
        Assert.NotEqual(creation.Slug, secondCreation.Slug);
        Assert.NotEqual(creation.DisplayName, secondCreation.DisplayName);
        Assert.StartsWith("Developer One \u00b7 ", secondCreation.DisplayName, StringComparison.Ordinal);
        Assert.True(secondCreation.DisplayName.Length <= OrganizationStore.MaxDisplayNameLength);

        // Linking a worker is idempotent and revision-bound. A later provisioning
        // run creates the enrollment; the test seeds one row to satisfy the FK.
        const string workerId = "wrk-0000000000000001";
        SeedWorkerEnrollment(root.Path, creation.RuntimeBindingId, workerId);
        var linked = store.LinkHireWorker(first.Hire.Id, creation.EmployeeId, creation.RuntimeBindingId, workerId, approval.Revision);
        Assert.Equal(workerId, linked.WorkerId);
        Assert.Equal(approval.Revision + 1, linked.Revision);
        Assert.Equal(linked, store.LinkHireWorker(first.Hire.Id, creation.EmployeeId, creation.RuntimeBindingId, workerId, linked.Revision));
        Assert.Throws<OrganizationConcurrencyException>(() =>
            store.LinkHireWorker(first.Hire.Id, creation.EmployeeId, creation.RuntimeBindingId, "wrk-0000000000000002", linked.Revision));
        Assert.Throws<OrganizationValidationException>(() =>
            store.LinkHireWorker(first.Hire.Id, creation.EmployeeId, "rtb-0000000000000000", "wrk-0000000000000003", linked.Revision));
    }

    [Fact]
    public void ApprovalResolvesByRuntimeBindingForTheManagedPlan()
    {
        using var root = new TempStore();
        using var store = Open(root);
        var seeded = SeedApprovedHire(store, "Binding Lookup", 2, 2048, 256, out var revisionId, out _);
        store.ApproveHireRequest(seeded.Hire.Id, new HireRequestApprove(seeded.Hire.Revision, revisionId, "host-a"), "owner");
        var creation = store.CreateManagedEmployeeFromHire(seeded.Hire.Id);

        var byBinding = store.GetHireRequestApprovalByBinding(creation.RuntimeBindingId);
        Assert.NotNull(byBinding);
        Assert.Equal(seeded.Hire.Id, byBinding!.HireRequestId);
        Assert.Equal(creation.EmployeeId, byBinding.EmployeeId);
        Assert.Equal(revisionId, byBinding.ProfileRevisionId);
        Assert.Equal(seeded.Build.Id, byBinding.ProfileBuildId);
        Assert.Equal(BuiltDigest, byBinding.ImageDigest);
        Assert.Equal("host-a", byBinding.HostId);
        Assert.Null(byBinding.WorkerId);

        // An unlinked or invalid binding id resolves to nothing rather than faulting.
        Assert.Null(store.GetHireRequestApprovalByBinding("rtb-0000000000000000"));
        Assert.Null(store.GetHireRequestApprovalByBinding("not-a-binding"));
    }

    [Fact]
    public void ManagedEmployeeFromApprovedHireHasExactFourCompositionLayersAndBoundedFacts()
    {
        using var root = new TempStore();
        using var store = Open(root);
        var seeded = SeedApprovedHire(store, "Developer One", 2, 2048, 256, out var revisionId, out _);
        store.ApproveHireRequest(seeded.Hire.Id, new HireRequestApprove(seeded.Hire.Revision, revisionId, "host-a"), "owner");
        var creation = store.CreateManagedEmployeeFromHire(seeded.Hire.Id);

        // Exactly the four layers the composition requires resolve for the managed
        // employee, and its own active employee fragment is unique.
        Assert.Equal(4, RawScalar(root.Path,
            $"""
            SELECT COUNT(*) FROM orientation_fragments f
            JOIN employees e ON e.id = '{creation.EmployeeId}'
            WHERE f.active = 1
              AND ((f.layer = 'organization' AND f.scope_id = e.organization_id)
                OR (f.layer = 'department' AND f.scope_id = e.department_id)
                OR (f.layer = 'role' AND f.scope_id = e.role_id)
                OR (f.layer = 'employee' AND f.scope_id = e.id));
            """));
        Assert.Equal(1, RawScalar(root.Path,
            $"SELECT COUNT(*) FROM orientation_fragments WHERE layer = 'employee' AND scope_id = '{creation.EmployeeId}' AND active = 1;"));

        // The employee fragment owns the single-valued identity/department/reporting
        // facts and adds safe employee-scoped duty and restriction bounds.
        foreach (var (category, expected) in new[]
        {
            ("identity", 1),
            ("department", 1),
            ("reporting", 1),
            ("duty", 1),
            ("restriction", 4),
        })
        {
            Assert.Equal(expected, RawScalar(root.Path,
                $"""
                SELECT COUNT(*) FROM orientation_facts f
                JOIN orientation_fragments g ON g.id = f.fragment_id
                WHERE g.layer = 'employee' AND g.scope_id = '{creation.EmployeeId}'
                  AND g.active = 1 AND f.category = '{category}';
                """));
        }

        // The whole composition resolves; no assignment exists until it is composed.
        var artifact = store.GetOrientationArtifact(creation.EmployeeId);
        Assert.Equal(creation.EmployeeId, artifact.EmployeeId);
        Assert.Equal(creation.RuntimeBindingId, artifact.RuntimeBindingId);
        Assert.Contains("<!-- fragment:organization", artifact.Content, StringComparison.Ordinal);
        Assert.Contains("<!-- fragment:department", artifact.Content, StringComparison.Ordinal);
        Assert.Contains("<!-- fragment:role", artifact.Content, StringComparison.Ordinal);
        Assert.Contains("<!-- fragment:employee", artifact.Content, StringComparison.Ordinal);
        Assert.Contains($"Identity: {creation.DisplayName} ({creation.EmployeeId})", artifact.Content, StringComparison.Ordinal);
        Assert.Throws<OrganizationNotFoundException>(() => store.GetOrientationStatus(creation.EmployeeId));
    }

    [Fact]
    public void ManagedEmployeeComposeAssignsToItsOwnBindingAndHoldsUntilComprehended()
    {
        using var root = new TempStore();
        using var store = Open(root);
        var seeded = SeedApprovedHire(store, "Developer Two", 2, 2048, 256, out var revisionId, out _);
        store.ApproveHireRequest(seeded.Hire.Id, new HireRequestApprove(seeded.Hire.Revision, revisionId, "host-a"), "owner");
        var creation = store.CreateManagedEmployeeFromHire(seeded.Hire.Id);
        SeedManagedSession(root.Path, creation.RuntimeBindingId, creation.EmployeeId, "ses-managed");

        var artifact = store.ComposeAndAssignCurrentOrientation(creation.EmployeeId);
        Assert.Equal(creation.EmployeeId, artifact.EmployeeId);
        Assert.Equal(creation.RuntimeBindingId, artifact.RuntimeBindingId);
        Assert.Equal("ses-managed", artifact.SessionId);
        Assert.Equal(creation.RuntimeBindingId, store.GetOrientationStatus(creation.EmployeeId).RuntimeBindingId);

        // Assigned and delivered both hold dispatch until exact comprehension.
        var assigned = store.GetOrientationStatus(creation.EmployeeId);
        Assert.Equal(OrientationStates.Assigned, assigned.State);
        Assert.True(assigned.DispatchHeld);
        Assert.False(store.CanDispatchEmployee(creation.EmployeeId));

        var delivered = store.MarkOrientationDelivered(
            artifact.AssignmentId, artifact.OrientationVersion, "ses-managed", artifact.AssignmentRevision);
        Assert.Equal(OrientationStates.Delivered, delivered.State);
        Assert.True(delivered.DispatchHeld);
        Assert.False(store.CanDispatchEmployee(creation.EmployeeId));

        var evidence = ReadEvidence(root.Path, delivered.AssignmentId, creation.EmployeeId, "ses-managed", delivered.OrientationVersion, delivered.Revision);
        var comprehended = store.ValidateAndRecordComprehension(evidence);
        Assert.Equal(OrientationStates.Comprehended, comprehended.State);
        Assert.False(comprehended.DispatchHeld);
        Assert.True(store.CanDispatchEmployee(creation.EmployeeId));
    }

    [Fact]
    public void ManagedEmployeeRecomposeIsIdempotentAndSeedNoArgPathIsUnchanged()
    {
        using var root = new TempStore();
        using var store = Open(root);
        var seedIdentity = store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        var seeded = SeedApprovedHire(store, "Developer Three", 2, 2048, 256, out var revisionId, out _);
        store.ApproveHireRequest(seeded.Hire.Id, new HireRequestApprove(seeded.Hire.Revision, revisionId, "host-a"), "owner");
        var creation = store.CreateManagedEmployeeFromHire(seeded.Hire.Id);
        SeedManagedSession(root.Path, creation.RuntimeBindingId, creation.EmployeeId, "ses-recompose");

        var first = store.ComposeAndAssignCurrentOrientation(creation.EmployeeId);
        var second = store.ComposeAndAssignCurrentOrientation(creation.EmployeeId);
        Assert.Equal(first.AssignmentId, second.AssignmentId);
        Assert.Equal(first.AssignmentRevision, second.AssignmentRevision);
        Assert.Equal(first.OrientationVersion, second.OrientationVersion);
        Assert.Equal(1, RawScalar(root.Path,
            $"SELECT COUNT(*) FROM orientation_assignments WHERE employee_id = '{creation.EmployeeId}';"));

        // The internal no-argument path still resolves the adopted seed employee and
        // is not affected by the managed employee's fragment.
        var seedArtifact = store.ComposeAndAssignCurrentOrientation();
        Assert.Equal(seedIdentity.EmployeeId, seedArtifact.EmployeeId);
        Assert.Equal(seedIdentity.RuntimeBindingId, seedArtifact.RuntimeBindingId);
        Assert.Equal(4, RawScalar(root.Path,
            $"""
            SELECT COUNT(*) FROM orientation_fragments f
            JOIN employees e ON e.id = '{seedIdentity.EmployeeId}'
            WHERE f.active = 1
              AND ((f.layer = 'organization' AND f.scope_id = e.organization_id)
                OR (f.layer = 'department' AND f.scope_id = e.department_id)
                OR (f.layer = 'role' AND f.scope_id = e.role_id)
                OR (f.layer = 'employee' AND f.scope_id = e.id));
            """));
    }

    [Fact]
    public void StateTransitionsFollowTheFixedMachineAndBoundedDetail()
    {
        using var root = new TempStore();
        using var store = Open(root);
        var seeded = SeedApprovedHire(store, "Transition Hire", 2, 2048, 256, out var revisionId, out _);
        store.ApproveHireRequest(seeded.Hire.Id, new HireRequestApprove(seeded.Hire.Revision, revisionId, "host-a"), "owner");
        var approved = store.GetHireRequest(seeded.Hire.Id)!;

        // Invalid transitions and stale revisions are rejected.
        Assert.Throws<OrganizationValidationException>(() =>
            store.TransitionHireRequestState(approved.Id, approved.Revision, HireRequestStates.Approved, HireRequestStates.Ready));
        Assert.Throws<OrganizationValidationException>(() =>
            store.TransitionHireRequestState(approved.Id, approved.Revision, HireRequestStates.Requested, HireRequestStates.Approved));
        Assert.Throws<OrganizationConcurrencyException>(() =>
            store.TransitionHireRequestState(approved.Id, approved.Revision + 3, HireRequestStates.Approved, HireRequestStates.Provisioning));

        var provisioning = store.TransitionHireRequestState(approved.Id, approved.Revision, HireRequestStates.Approved, HireRequestStates.Provisioning);
        Assert.Equal(HireRequestStates.Provisioning, provisioning.State);
        Assert.Equal(approved.Revision + 1, provisioning.Revision);
        Assert.Null(provisioning.StatusDetail);

        // Detail is sanitized to the bounded, control-free form.
        var noisy = "container start failed\u0001\u0002 " + new string('x', 700);
        var interrupted = store.TransitionHireRequestState(provisioning.Id, provisioning.Revision, HireRequestStates.Provisioning, HireRequestStates.Interrupted, noisy);
        Assert.Equal(HireRequestStates.Interrupted, interrupted.State);
        Assert.NotNull(interrupted.StatusDetail);
        Assert.True(interrupted.StatusDetail!.Length <= OrganizationStore.MaximumStatusDetailLength);
        Assert.DoesNotContain('\u0001', interrupted.StatusDetail);
        Assert.DoesNotContain('\u0002', interrupted.StatusDetail);

        // Interrupted -> Provisioning -> Orienting -> Ready, and Ready clears detail.
        var resumed = store.TransitionHireRequestState(interrupted.Id, interrupted.Revision, HireRequestStates.Interrupted, HireRequestStates.Provisioning);
        var orienting = store.TransitionHireRequestState(resumed.Id, resumed.Revision, HireRequestStates.Provisioning, HireRequestStates.Orienting, "delivering orientation");
        Assert.Equal("delivering orientation", orienting.StatusDetail);
        var ready = store.TransitionHireRequestState(orienting.Id, orienting.Revision, HireRequestStates.Orienting, HireRequestStates.Ready, "should be cleared");
        Assert.Equal(HireRequestStates.Ready, ready.State);
        Assert.Null(ready.StatusDetail);

        // One immutable event per revision from Requested through Ready.
        Assert.Equal(ready.Revision, RawScalar(root.Path, $"SELECT COUNT(*) FROM hire_request_events WHERE hire_request_id = '{approved.Id}';"));

        // Uncertain can return to Provisioning; Requested/Rejected are not reachable here.
        Assert.Throws<OrganizationValidationException>(() =>
            store.TransitionHireRequestState(ready.Id, ready.Revision, HireRequestStates.Ready, HireRequestStates.Rejected));
    }

    [Fact]
    public void FrozenApprovalAndResourceRowsAreImmutableAtTheDatabaseBoundary()
    {
        using var root = new TempStore();
        using var store = Open(root);
        var seeded = SeedApprovedHire(store, "Immutable Hire", 2, 2048, 256, out var revisionId, out _);
        store.ApproveHireRequest(seeded.Hire.Id, new HireRequestApprove(seeded.Hire.Revision, revisionId, "host-a"), "owner");
        var creation = store.CreateManagedEmployeeFromHire(seeded.Hire.Id);

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = root.Path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();

        var attempts = new[]
        {
            $"UPDATE hire_request_approvals SET image_digest = '{BuiltDigest}' WHERE hire_request_id = '{seeded.Hire.Id}'",
            $"UPDATE hire_request_approvals SET host_id = 'host-b' WHERE hire_request_id = '{seeded.Hire.Id}'",
            $"UPDATE hire_request_approvals SET approved_request_version = '{ContextHash}' WHERE hire_request_id = '{seeded.Hire.Id}'",
            "DELETE FROM hire_request_approvals",
            "INSERT OR REPLACE INTO hire_request_approvals SELECT hire_request_id, approved_request_version, approved_request_revision, request_version_hash, profile_revision_id, profile_build_id, image_digest, host_id, platform, cpu_limit, memory_limit_mib, pids_limit, approval_identity, approved_at, employee_id, runtime_binding_id, worker_id, revision FROM hire_request_approvals",
            $"UPDATE managed_enrollment_resources SET cpu_limit = 8 WHERE runtime_binding_id = '{creation.RuntimeBindingId}'",
            "DELETE FROM managed_enrollment_resources",
            "INSERT OR REPLACE INTO managed_enrollment_resources SELECT runtime_binding_id, cpu_limit, memory_limit_mib, pids_limit, approved_profile_revision_id, approved_profile_build_id, approved_image_digest, approved_host_id, platform, created_at, revision FROM managed_enrollment_resources",
        };
        foreach (var sql in attempts)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        }

        // The linking columns and the approval revision stay writable.
        using (var link = connection.CreateCommand())
        {
            link.CommandText = $"UPDATE hire_request_approvals SET revision = revision + 1 WHERE hire_request_id = '{seeded.Hire.Id}'";
            Assert.Equal(1, link.ExecuteNonQuery());
        }

        Assert.Equal(1, RawScalar(root.Path, $"SELECT COUNT(*) FROM hire_request_approvals WHERE hire_request_id = '{seeded.Hire.Id}' AND revision = 3;"));
    }

    // ---------------------------------------------------------------- helpers

    private sealed record Seed(OrganizationStore Store, string ProfileId, string RevisionId, ProfileBuildRecord Build, HireRequestSummary Hire);

    private static Seed SeedApprovedHire(
        OrganizationStore store,
        string displayName,
        int cpu,
        int memory,
        int pids,
        out string revisionId,
        out string profileId)
    {
        var build = SeedVerifiedBuild(store, out revisionId);
        var profile = store.ListContainerProfiles().Single(p => p.Slug == ContainerProfileSeed.GenericEmployeeSlug);
        profileId = profile.Id;
        var hire = CreateDevHire(store, displayName, cpu, memory, pids);
        return new Seed(store, profileId, revisionId, build, hire);
    }

    private static ProfileBuildRecord SeedVerifiedBuild(OrganizationStore store, out string revisionId, bool limitsSupported = true)
    {
        EnsureHost(store, limitsSupported);
        var profile = store.ListContainerProfiles().Single(p => p.Slug == ContainerProfileSeed.GenericEmployeeSlug);
        revisionId = store.GetContainerProfile(profile.Id)!.Revisions.Single().Id;
        var existing = store.GetVerifiedProfileBuild(revisionId, "host-a");
        if (existing is not null) return existing;
        var queued = store.QueueProfileBuild(revisionId, "host-a", BaseDigest, "linux/amd64", ContextHash, "agentcontrol-profile:x");
        var building = store.TransitionProfileBuild(queued.Id, queued.Revision, ProfileBuildStates.Building);
        var verifying = store.TransitionProfileBuild(building.Id, building.Revision, ProfileBuildStates.Verifying);
        return store.TransitionProfileBuild(verifying.Id, verifying.Revision, ProfileBuildStates.Built, imageDigest: BuiltDigest, verified: true, evidenceHash: ContextHash);
    }

    private static void EnsureHost(OrganizationStore store, bool limitsSupported) => PrepareHost(store, limitsSupported);

    private static void PrepareHost(OrganizationStore store, bool limitsSupported)
    {
        store.RegisterExecutionHost("host-a", "host-a.example", 22, "roys", "/known", new ExecutionHostRegistration("host-a", "host-a", "Host A"));
        var host = store.GetExecutionHost("host-a")!;
        store.RecordExecutionHostProbe("host-a", host.Revision, new ExecutionHostProbe(
            "ssh-ed25519", "SHA256:x", "sha256:" + new string('1', 64), "29.0", "1.51", "x86_64", "overlay2", "ext4",
            false, 64L << 30, 16L << 30, 8, limitsSupported, "linux/amd64", "valid"));
    }

    private static HireRequestSummary CreateDevHire(OrganizationStore store, string displayName, int cpu, int memory, int pids)
    {
        var overview = store.GetOverview();
        var department = overview.Departments.Single(d => d.Slug == OrganizationSeed.OperationsSlug);
        var role = Assert.Single(overview.Roles);
        return store.CreateHireRequest(new HireRequestCreate(
            Guid.NewGuid().ToString("N"), displayName, "Managed hire under test.", department.Id, role.Id,
            RuntimePlacements.DeveloperContainer, cpu, memory, pids), null);
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

    /// <summary>
    /// Binds an active ACP session to the managed employee's binding. Managed
    /// employees have no public session-recording path in this phase, so the test
    /// fixture writes the exact persisted rows the runtime would record.
    /// </summary>
    private static void SeedManagedSession(string path, string bindingId, string employeeId, string nativeSessionId)
    {
        var sessionRowId = "ses-" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        RawExec(path,
            $"""
            INSERT INTO acp_sessions (id, employee_id, native_session_id, title, status, created_at, updated_at)
            VALUES ('{sessionRowId}', '{employeeId}', '{nativeSessionId}', NULL, 'active', '{now}', '{now}');
            """);
        RawExec(path,
            $"UPDATE runtime_bindings SET session_ref = '{sessionRowId}', updated_at = '{now}', revision = revision + 1 WHERE id = '{bindingId}';");
    }

    private static List<(string Category, string Value)> RawRows(string path, string sql)
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
        using var reader = command.ExecuteReader();
        var rows = new List<(string, string)>();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }

    /// <summary>
    /// Builds exact comprehension evidence from the persisted expected facts of an
    /// assignment, mirroring the host's own read. This is the same shape an owner
    /// or the live model would submit; only the bounded fact values are read back.
    /// </summary>
    private static OrientationEvidenceRequest ReadEvidence(
        string path,
        string assignmentId,
        string employeeId,
        string sessionId,
        string version,
        int revision)
    {
        var rows = RawRows(path,
            $"""
            SELECT f.category, f.value
            FROM orientation_assignment_fragments af
            JOIN orientation_facts f ON f.fragment_id = af.fragment_id
            WHERE af.assignment_id = '{assignmentId}'
            ORDER BY af.ordinal, f.category, f.ordinal;
            """);
        string Single(string category) => rows.Where(row => row.Category == category).Select(row => row.Value).Single();
        IReadOnlyList<string> Many(string category) => rows.Where(row => row.Category == category).Select(row => row.Value).ToArray();
        return new OrientationEvidenceRequest(
            assignmentId,
            employeeId,
            sessionId,
            version,
            Single("identity"),
            Single("department"),
            Single("reporting"),
            Many("duty"),
            Many("restriction"),
            Single("escalation"),
            revision);
    }

    private static void RawExec(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void SeedWorkerEnrollment(string path, string bindingId, string workerId)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var suffix = workerId.Replace('-', '_');
        RawExec(path,
            $"""
            INSERT INTO worker_enrollments (
                worker_id, runtime_binding_id, host_id, organization_id,
                container_name, control_volume_name, home_volume_name, workspace_volume_name, session_volume_name,
                resource_labels_hash, expected_image_digest, expected_platform, controller_id, key_file_path, key_id,
                bridge_socket_path, lifecycle_status, worker_generation, process_generation, ownership_epoch, enabled,
                created_at, updated_at, revision)
            VALUES ('{workerId}', '{bindingId}', 'host-a', (SELECT id FROM organizations LIMIT 1),
                'container-{suffix}', 'control-{suffix}', 'home-{suffix}', 'workspace-{suffix}', 'session-{suffix}',
                'sha256:{new string('a', 64)}', 'sha256:{new string('b', 64)}', 'linux/amd64', 'controller-a', '/control/key',
                'sha256:{new string('c', 64)}', '/control/bridge.sock', 'planned', 0, 0, 0, 1, '{now}', '{now}', 1)
            """);
    }

    private sealed class TempStore : IDisposable
    {
        public TempStore()
        {
            Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agentcontrol-approval-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Path = System.IO.Path.Combine(Directory, OrganizationStore.DatabaseFileName);
        }

        public string Directory { get; }
        public string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { }
        }
    }
}
