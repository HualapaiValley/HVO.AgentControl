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
