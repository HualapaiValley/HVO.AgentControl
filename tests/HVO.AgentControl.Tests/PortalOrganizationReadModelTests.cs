using HVO.AgentControl.Organization;
using HVO.AgentControl.RemoteWorker;
using HVO.AgentControl.Runtime;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class PortalOrganizationReadModelTests
{
    [Theory]
    [InlineData(OrientationStates.Comprehended, false, false, "ready")]
    [InlineData(OrientationStates.Comprehended, false, true, "held")]
    [InlineData(OrientationStates.Delivered, true, true, "reload-required")]
    [InlineData(OrientationStates.Failed, false, true, "orientation-failed")]
    [InlineData(OrientationStates.Stale, false, true, "orientation-stale")]
    public void AvailabilityClassificationIsDeterministic(
        string orientationState,
        bool restartRequired,
        bool dispatchHeld,
        string expected)
    {
        var employee = Employee(Orientation(orientationState, restartRequired, dispatchHeld));
        var actual = PortalOrganizationReadModel.Classify(employee, hostOwned: true, Status());
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(OrientationStates.Failed, false, false, "orientation-failed")]
    [InlineData(OrientationStates.Stale, false, false, "orientation-stale")]
    [InlineData(OrientationStates.Comprehended, true, true, "reload-required")]
    [InlineData(OrientationStates.Comprehended, false, true, "runtime-unavailable")]
    public void CriticalOrientationPrecedesRuntimeAndHoldNeverMasksUnavailableRuntime(
        string orientationState,
        bool restartRequired,
        bool hostOwned,
        string expected)
    {
        // The host is faulted (cannot control) while orientation carries a hold.
        // Failed/Stale/reload-required stay critical; a plain manual hold must
        // surface runtime-unavailable rather than held.
        var employee = Employee(Orientation(orientationState, restartRequired, dispatchHeld: true));
        var actual = PortalOrganizationReadModel.Classify(
            employee,
            hostOwned,
            Status() with { State = "faulted" });

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void StaleWithDiagnosticLastErrorRemainsOrientationStale()
    {
        var employee = Employee(Orientation(
            OrientationStates.Stale,
            restartRequired: false,
            dispatchHeld: true,
            error: "Role instructions changed."));

        Assert.Equal(
            EmployeeAvailabilityCategories.OrientationStale,
            PortalOrganizationReadModel.Classify(employee, hostOwned: true, Status()));
    }

    [Fact]
    public void GenericLastErrorOnOtherwiseReadyStateIsOrientationFailed()
    {
        var employee = Employee(Orientation(
            OrientationStates.Comprehended,
            restartRequired: false,
            dispatchHeld: false,
            error: "Unexpected persisted diagnostic."));

        Assert.Equal(
            EmployeeAvailabilityCategories.OrientationFailed,
            PortalOrganizationReadModel.Classify(employee, hostOwned: true, Status()));
    }

    [Fact]
    public void DeliveredUnacknowledgedHoldIsHeldOnlyWhileTheRuntimeIsAvailable()
    {
        var employee = Employee(Orientation(
            OrientationStates.Delivered,
            restartRequired: false,
            dispatchHeld: true,
            holdReason: DispatchHoldReasons.OrientationUnacknowledged));

        Assert.Equal(
            EmployeeAvailabilityCategories.Held,
            PortalOrganizationReadModel.Classify(employee, hostOwned: true, Status()));
        Assert.Equal(
            EmployeeAvailabilityCategories.RuntimeUnavailable,
            PortalOrganizationReadModel.Classify(employee, hostOwned: true, Status() with { State = "faulted" }));
    }

    [Fact]
    public void RuntimeMismatchIsUnavailableAndTerminalFailsClosed()
    {
        var overview = Overview(Employee(Orientation(OrientationStates.Comprehended, false, false)));
        var identity = Identity();
        var detail = PortalOrganizationReadModel.FindEmployee(
            overview,
            identity,
            Status() with { SessionId = "native-other" },
            "emp-test");

        Assert.NotNull(detail);
        Assert.Equal(EmployeeAvailabilityCategories.RuntimeUnavailable, detail!.Availability);
        Assert.True(detail.Terminal.Supported);
        Assert.False(detail.Terminal.Available);
        Assert.Null(detail.Terminal.Url);
        Assert.False(detail.RecentLogs.Supported);
    }

    [Fact]
    public void RemoteSnapshotClassifiesProvisioningAndExposesRemoteOwnershipWithoutTerminalClaim()
    {
        var overview = Overview(Employee(Orientation(OrientationStates.Comprehended, false, false), sessionId: "remote-session"));
        var snapshot = new RemoteWorkerSnapshot("emp-test", "rtb-test", "wrk-test", "host-test", "planned", true, "disconnected", "unknown", "remote-session", 0, 0, false, false, false, [], null);
        var portal = PortalOrganizationReadModel.Build(overview, null, Status() with { State = "faulted" }, new FakeRemoteProvider(snapshot));
        var detail = Assert.Single(portal.Employees); Assert.Equal(EmployeeAvailabilityCategories.Provisioning, detail.Availability); Assert.True(detail.Runtime.RemoteOwned); Assert.Equal("host-test", detail.Runtime.RemoteHostId); Assert.False(detail.Terminal.Supported);
    }

    [Fact]
    public void RemoteRecoveryAndInterruptedCategoriesAreStable()
    {
        var overview = Overview(Employee(Orientation(OrientationStates.Comprehended, false, false), sessionId: "remote-session"));
        var held = new RemoteWorkerSnapshot("emp-test", "rtb-test", "wrk-test", "host-test", "enrolled", true, "held", "running", "remote-session", 2, 1, true, false, false, [], "replay-gap");
        Assert.Equal(EmployeeAvailabilityCategories.ReconciliationRequired, Assert.Single(PortalOrganizationReadModel.Build(overview, null, Status(), new FakeRemoteProvider(held)).Employees).Availability);
        var interrupted = held with { Held = false, ConnectionState = "authenticated", ProcessState = "exited", Detail = null };
        Assert.Equal(EmployeeAvailabilityCategories.Interrupted, Assert.Single(PortalOrganizationReadModel.Build(overview, null, Status(), new FakeRemoteProvider(interrupted)).Employees).Availability);
    }

    [Fact]
    public void PortalSummaryHasHonestHireRequestSupportAndRoutedFailureLink()
    {
        var failed = Employee(Orientation(OrientationStates.Stale, false, true, "instructions changed"));
        var portal = PortalOrganizationReadModel.Build(Overview(failed), Identity(), Status());

        Assert.True(portal.PendingApprovals.Supported);
        Assert.Equal(0, portal.PendingApprovals.Count);
        Assert.Empty(portal.PendingApprovals.Items);
        Assert.Contains("approved", portal.PendingApprovals.Reason);
        Assert.Contains("verified container profile revision build on a ready host", portal.PendingApprovals.Reason);
        Assert.Contains("creates the managed employee identity and runtime binding", portal.PendingApprovals.Reason);
        Assert.Contains("durably queues provisioning and orientation to Ready", portal.PendingApprovals.Reason);
        Assert.Contains("background work", portal.PendingApprovals.Reason);
        // The reason must not retain the superseded gate phrasing. Assert the
        // actual historical phrases that this change replaced.
        Assert.DoesNotContain("lands with #260", portal.PendingApprovals.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("gated by #217", portal.PendingApprovals.Reason, StringComparison.OrdinalIgnoreCase);
        // Approval now triggers the work; the superseded "never provisions" claim
        // must not come back.
        Assert.DoesNotContain("does not provision or orient", portal.PendingApprovals.Reason, StringComparison.Ordinal);
        var failure = Assert.Single(portal.FailuresNeedingAttention);
        Assert.Equal("emp-test", failure.EmployeeId);
        Assert.Equal("/employees/emp-test", failure.Url);
    }

    [Fact]
    public void FindDepartmentScopesRolesRosterCountsFailuresAndHireUrlToTheStableId()
    {
        var failed = Employee(Orientation(OrientationStates.Stale, restartRequired: false, dispatchHeld: true, error: "instructions changed"));
        var overview = new OrganizationOverview(
            "org-test", "org", "Organization", "Description", "Instructions", 1,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            [
                new DepartmentSummary("dept-test", "operations", "Operations", 1, 4, "Operations standing instructions"),
                new DepartmentSummary("dept-dev", "development", "Development", 0, 1, null),
            ],
            [new RoleSummary("role-test", "dept-test", "operations-it", "Operations / IT", "role@1", "policy@1", "Standing", 3)],
            [failed],
            []);

        var operations = PortalOrganizationReadModel.FindDepartment(overview, Identity(), Status(), "dept-test");
        Assert.NotNull(operations);
        Assert.Equal("dept-test", operations!.Id);
        Assert.Equal("org-test", operations.OrganizationId);
        Assert.Equal(4, operations.Revision);
        Assert.Equal("Operations standing instructions", operations.StandingInstructions);
        Assert.Equal("/hiring?departmentId=dept-test", operations.HireUrl);
        Assert.Equal("role-test", Assert.Single(operations.Roles).Id);
        Assert.Equal("emp-test", Assert.Single(operations.Employees).Id);
        Assert.Equal(EmployeeAvailabilityCategories.OrientationStale, Assert.Single(operations.Employees).Availability);
        var failure = Assert.Single(operations.FailuresNeedingAttention);
        Assert.Equal("emp-test", failure.EmployeeId);
        Assert.Equal("/employees/emp-test", failure.Url);
        Assert.Equal(1, operations.Availability.Single(count => count.Category == EmployeeAvailabilityCategories.OrientationStale).Count);

        // The unstaffed department is a real empty state, not a filtered overview.
        var development = PortalOrganizationReadModel.FindDepartment(overview, Identity(), Status(), "dept-dev");
        Assert.NotNull(development);
        Assert.Empty(development!.Roles);
        Assert.Empty(development.Employees);
        Assert.Null(development.StandingInstructions);
        Assert.All(development.Availability, count => Assert.Equal(0, count.Count));
        Assert.Empty(development.FailuresNeedingAttention);

        Assert.Null(PortalOrganizationReadModel.FindDepartment(overview, Identity(), Status(), "dept-missing"));
    }

    [Fact]
    public void ManagedEmployeeProfileStatusReportsCurrentRevisionAndDigest()
    {
        using var root = new TempStore();
        using var store = Open(root);

        var seeded = SeedManagedEmployee(store, root.Path, buildNewerVerified: false);
        var detail = PortalOrganizationReadModel.FindEmployee(
            store.GetOverview(),
            null,
            Status(),
            seeded.EmployeeId,
            remoteProvider: null,
            store);

        Assert.NotNull(detail);
        var status = detail!.ProfileStatus;
        Assert.Equal(seeded.RevisionId, status.CurrentProfileRevisionId);
        Assert.Equal(1, status.CurrentRevisionNumber);
        Assert.Equal(ManagedDigest, status.CurrentImageDigest);
        Assert.False(status.NewerRevisionAvailable);
        Assert.Null(status.NewerRevisionNumber);
        Assert.Null(status.ActiveRebuildState);
        Assert.Null(status.ActiveRebuildId);
    }

    [Fact]
    public void ManagedEmployeeProfileStatusOffersNewerRevisionOnlyWithAVerifiedBuild()
    {
        using var root = new TempStore();
        using var store = Open(root);
        var seeded = SeedManagedEmployee(store, root.Path, buildNewerVerified: false);

        // A newer revision exists but has no verified build: not offered.
        var noBuild = PortalOrganizationReadModel.FindEmployee(store.GetOverview(), null, Status(), seeded.EmployeeId, null, store)!;
        Assert.False(noBuild.ProfileStatus.NewerRevisionAvailable);
        Assert.Null(noBuild.ProfileStatus.NewerRevisionNumber);

        // Building it makes it the offered target with its exact number.
        BuildVerified(store, seeded.NewerRevisionId, NewerDigest);
        var available = PortalOrganizationReadModel.FindEmployee(store.GetOverview(), null, Status(), seeded.EmployeeId, null, store)!;
        Assert.True(available.ProfileStatus.NewerRevisionAvailable);
        Assert.Equal(2, available.ProfileStatus.NewerRevisionNumber);
    }

    [Fact]
    public void SeedEmployeeProfileStatusIsAllNulls()
    {
        using var root = new TempStore();
        using var store = Open(root);
        var seed = store.GetOverview().Employees.Single(e => e.Slug == OrganizationSeed.AdoptedEmployeeSlug);

        var detail = PortalOrganizationReadModel.FindEmployee(store.GetOverview(), null, Status(), seed.Id, null, store);

        Assert.NotNull(detail);
        Assert.Null(detail!.ProfileStatus.CurrentProfileRevisionId);
        Assert.Null(detail.ProfileStatus.CurrentRevisionNumber);
        Assert.Null(detail.ProfileStatus.CurrentImageDigest);
        Assert.False(detail.ProfileStatus.NewerRevisionAvailable);
        Assert.Null(detail.ProfileStatus.ActiveRebuildState);
        Assert.Null(detail.ProfileStatus.ActiveRebuildId);

        // The build also keeps the seed employee null-safe without a store.
        var withoutStore = PortalOrganizationReadModel.FindEmployee(store.GetOverview(), null, Status(), seed.Id);
        Assert.Null(withoutStore!.ProfileStatus.CurrentProfileRevisionId);
    }

    [Fact]
    public void ManagedEmployeeActiveRebuildSurfacesInProfileStatus()
    {
        using var root = new TempStore();
        using var store = Open(root);
        var seeded = SeedManagedEmployee(store, root.Path, buildNewerVerified: true);

        var rebuild = store.BeginEmployeeRebuild(new EmployeeRebuildCreate(
            seeded.EmployeeId,
            seeded.BindingId,
            seeded.WorkerId,
            ExecutionHosts.LocalDockerId,
            seeded.RevisionId,
            ManagedDigest,
            1,
            seeded.NewerRevisionId,
            seeded.NewerBuildId,
            NewerDigest,
            "linux/amd64",
            ResetWorkspace: false,
            ResetHome: false,
            ResetConfirmation: null,
            OwnershipEpochBefore: 0));

        var detail = PortalOrganizationReadModel.FindEmployee(store.GetOverview(), null, Status(), seeded.EmployeeId, null, store)!;
        Assert.Equal(EmployeeRebuildStates.Intent, detail.ProfileStatus.ActiveRebuildState);
        Assert.Equal(rebuild.Id, detail.ProfileStatus.ActiveRebuildId);
    }

    private const string ManagedDigest = "sha256:" + "1111111111111111111111111111111111111111111111111111111111111111";
    private const string NewerDigest = "sha256:" + "2222222222222222222222222222222222222222222222222222222222222222";
    private const string BuildBaseDigest = "sha256:" + "3333333333333333333333333333333333333333333333333333333333333333";
    private const string BuildContextHash = "sha256:" + "4444444444444444444444444444444444444444444444444444444444444444";

    private sealed record SeededManagedEmployee(string EmployeeId, string BindingId, string WorkerId, string RevisionId, string NewerRevisionId, string NewerBuildId);

    private static SeededManagedEmployee SeedManagedEmployee(OrganizationStore store, string path, bool buildNewerVerified)
    {
        var host = store.GetExecutionHost(ExecutionHosts.LocalDockerId)!;
        store.RecordLocalExecutionHostProbe(ExecutionHosts.LocalDockerId, host.Revision, new LocalExecutionHostProbe(
            "29.0", "1.51", "x86_64", "overlay2", "ext4", false, 64L << 30, 16L << 30, 8, true, "linux/amd64", "valid"));

        var profile = store.ListContainerProfiles().Single(p => p.Slug == ContainerProfileSeed.GenericEmployeeSlug);
        var revisionOne = store.GetContainerProfile(profile.Id)!.Revisions.Single();
        BuildVerified(store, revisionOne.Id, ManagedDigest);

        // Freeze the managed employee to revision 1 before the profile moves on,
        // because approval requires the profile's current revision.
        var overview = store.GetOverview();
        var department = overview.Departments.Single(d => d.Slug == OrganizationSeed.OperationsSlug);
        var role = Assert.Single(overview.Roles);
        var hire = store.CreateHireRequest(new HireRequestCreate(
            Guid.NewGuid().ToString("N"), "Portal Managed", "Portal profile status fixture.", department.Id, role.Id,
            RuntimePlacements.DeveloperContainer, 2, 2048, 256), null);
        store.ApproveHireRequest(hire.Id, new HireRequestApprove(hire.Revision, revisionOne.Id), "owner");
        var creation = store.CreateManagedEmployeeFromHire(hire.Id);
        SeedWorkerEnrollment(path, creation.RuntimeBindingId, "wrk-portal-1", ManagedDigest);

        // Now advance the profile. A newer revision with no verified build is not a
        // ready target; a verified build for it is.
        var newerRevision = store.CreateContainerProfileRevision(profile.Id, new ContainerProfileRevisionCreate(
            store.GetContainerProfile(profile.Id)!.Profile.Revision,
            """{"image":"agentcontrol-worker-base","name":"Portal Newer Target"}""",
            null));
        var newerBuild = buildNewerVerified
            ? BuildVerified(store, newerRevision.Id, NewerDigest)
            : QueueOnly(store, newerRevision.Id);

        return new SeededManagedEmployee(creation.EmployeeId, creation.RuntimeBindingId, "wrk-portal-1", revisionOne.Id, newerRevision.Id, newerBuild.Id);
    }

    private static ProfileBuildRecord BuildVerified(OrganizationStore store, string revisionId, string imageDigest)
    {
        var queued = store.QueueProfileBuild(revisionId, ExecutionHosts.LocalDockerId, BuildBaseDigest, "linux/amd64", BuildContextHash, "agentcontrol-profile:portal");
        var building = store.TransitionProfileBuild(queued.Id, queued.Revision, ProfileBuildStates.Building);
        var verifying = store.TransitionProfileBuild(building.Id, building.Revision, ProfileBuildStates.Verifying);
        return store.TransitionProfileBuild(verifying.Id, verifying.Revision, ProfileBuildStates.Built, imageDigest: imageDigest, verified: true, evidenceHash: BuildContextHash);
    }

    private static ProfileBuildRecord QueueOnly(OrganizationStore store, string revisionId) =>
        store.QueueProfileBuild(revisionId, ExecutionHosts.LocalDockerId, BuildBaseDigest, "linux/amd64", BuildContextHash, "agentcontrol-profile:portal-queued");

    private static OrganizationStore Open(TempStore root)
    {
        var store = new OrganizationStore(root.Path, lockTimeout: TimeSpan.FromSeconds(5));
        store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        return store;
    }

    private static void SeedWorkerEnrollment(string path, string bindingId, string workerId, string expectedImageDigest)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }
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
                'sha256:{new string('a', 64)}', '{expectedImageDigest}', 'linux/amd64', 'controller-portal', '/control/key',
                'sha256:{new string('c', 64)}', '/control/bridge.sock', 'planned', 0, 0, 0, 1, '{now}', '{now}', 1)
            """;
        command.ExecuteNonQuery();
    }

    private sealed class TempStore : IDisposable
    {
        public TempStore()
        {
            Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agentcontrol-portal-status-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Path = System.IO.Path.Combine(Directory, OrganizationStore.DatabaseFileName);
        }

        public string Directory { get; }

        public string Path { get; }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static OrganizationOverview Overview(params EmployeeSummary[] employees) => new(
        "org-test", "org", "Organization", "Description", "Instructions", 1,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
        [new DepartmentSummary("dept-test", "operations", "Operations", employees.Length, 1, "Operations standing instructions")],
        [new RoleSummary("role-test", "dept-test", "operations-it", "Operations / IT", "role@1", "policy@1", "Standing", 1)],
        employees, []);

    private static EmployeeSummary Employee(
        OrientationStatus orientation,
        string id = "emp-test",
        string bindingId = "rtb-test",
        string? sessionId = "native-session") => new(
        id, "operations-it", "Operations / IT", "Purpose", "Instructions", "Rules", "Restrictions",
        "org-test", "dept-test", "operations", "Operations", "role-test", "operations-it", "Operations / IT",
        bindingId, RuntimePlacements.InternalSharedContainer, sessionId, "Session", orientation);

    private static OrientationStatus Orientation(
        string state,
        bool restartRequired,
        bool dispatchHeld,
        string? error = null,
        string? holdReason = null) => new(
        "emp-test", "rtb-test", "acps-test", "ora-test", 1, "orientation-v1", state,
        DateTimeOffset.UnixEpoch, null, null, null, null, null, null, "orientation.md", 100,
        "policy-v1", 1, restartRequired ? 2 : 1, 1, restartRequired, dispatchHeld,
        dispatchHeld
            ? [holdReason ?? (restartRequired ? DispatchHoldReasons.OrientationReloadRequired : DispatchHoldReasons.Manual)]
            : [],
        error);

    private static OrganizationRuntimeIdentity Identity(
        string employeeId = "emp-test",
        string bindingId = "rtb-test",
        string? sessionId = "native-session") => new(
        "org-test", "org", "Organization", employeeId, "Operations / IT", bindingId, "secret-token",
        sessionId, "Session", false, false);

    private sealed class FakeRemoteProvider(params RemoteWorkerSnapshot[] snapshots) : IRemoteWorkerStatusProvider
    {
        public IReadOnlyDictionary<string, RemoteWorkerSnapshot> Snapshot(OrganizationOverview overview) => snapshots.ToDictionary(x => x.EmployeeId, StringComparer.Ordinal);
    }

    private static ControlStatus Status() => new()
    {
        State = "ready",
        OrganizationName = "Organization",
        SessionId = "native-session",
        Model = "provider/model",
        TerminalReady = true,
        SessionState = "idle",
    };
}
