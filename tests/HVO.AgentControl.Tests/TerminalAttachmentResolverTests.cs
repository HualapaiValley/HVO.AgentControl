using HVO.AgentControl.Organization;
using HVO.AgentControl.RemoteWorker;
using HVO.AgentControl.Runtime;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class TerminalAttachmentResolverTests
{
    [Fact]
    public void ExactHostOwnedTupleRoutesLocalEligible() => Assert.Equal(TerminalAttachmentRoute.LocalEligible, Resolve(Employee()).Route);

    [Fact]
    public void MissingOverviewFailsClosed() => Assert.Equal(TerminalAttachmentRoute.Unavailable, TerminalAttachmentResolver.Resolve(null, Identity(), Status(), null, false, "emp-test").Route);

    [Fact]
    public void UnknownEmployeeIsNotFound() => Assert.Equal(TerminalAttachmentRoute.EmployeeNotFound, TerminalAttachmentResolver.Resolve(Overview(Employee()), Identity(), Status(), null, false, "emp-unknown").Route);

    [Theory]
    [InlineData("rtb-other", "native-session", "ready", true)]
    [InlineData("rtb-test", "native-other", "ready", true)]
    [InlineData("rtb-test", "", "ready", true)]
    [InlineData("rtb-test", "native-session", "faulted", true)]
    [InlineData("rtb-test", "native-session", "ready", false)]
    public void LocalMismatchNeverFallsBack(string binding, string session, string state, bool terminal)
    {
        var employee = Employee(bindingId: binding, sessionId: session);
        var result = TerminalAttachmentResolver.Resolve(Overview(employee), Identity(), Status() with { State = state, TerminalReady = terminal }, new Dictionary<string, RemoteWorkerSnapshot>(), true, employee.Id);
        Assert.Equal(TerminalAttachmentRoute.Unavailable, result.Route);
    }

    [Fact]
    public void ExactRemoteTupleRoutesRemoteEligible()
    {
        var employee = Employee(placement: RuntimePlacements.DeveloperContainer, sessionId: "remote-session");
        var remote = Snapshot(employee) with { ViewerSupported = true, ViewerAvailable = true };
        var result = TerminalAttachmentResolver.Resolve(Overview(employee), Identity(), Status(), new Dictionary<string, RemoteWorkerSnapshot> { [employee.Id] = remote }, true, employee.Id);
        Assert.Equal(TerminalAttachmentRoute.RemoteEligible, result.Route);
        Assert.Equal("wrk-test", result.Remote!.WorkerId);
    }

    [Theory]
    [InlineData(false, true, false, "authenticated", "running", "enrolled")]
    [InlineData(true, false, false, "authenticated", "running", "enrolled")]
    [InlineData(true, true, true, "authenticated", "running", "enrolled")]
    [InlineData(true, true, false, "disconnected", "running", "enrolled")]
    [InlineData(true, true, false, "authenticated", "exited", "enrolled")]
    [InlineData(true, true, false, "authenticated", "running", "held")]
    public void RemoteRequiresEveryExactCapability(bool supported, bool available, bool held, string connection, string process, string lifecycle)
    {
        var employee = Employee(placement: RuntimePlacements.DeveloperContainer, sessionId: "remote-session");
        var remote = Snapshot(employee) with { ViewerSupported = supported, ViewerAvailable = available, Held = held, ConnectionState = connection, ProcessState = process, LifecycleStatus = lifecycle };
        var result = TerminalAttachmentResolver.Resolve(Overview(employee), Identity(), Status(), new Dictionary<string, RemoteWorkerSnapshot> { [employee.Id] = remote }, true, employee.Id);
        Assert.Equal(TerminalAttachmentRoute.RemoteUnavailable, result.Route);
    }

    [Fact]
    public void DisabledRemoteEnrollmentIsUnavailable()
    {
        var employee = Employee(placement: RuntimePlacements.DeveloperContainer, sessionId: "remote-session");
        var remote = Snapshot(employee) with { EnrollmentEnabled = false, ViewerSupported = true, ViewerAvailable = true };
        Assert.Equal(TerminalAttachmentRoute.RemoteUnavailable, TerminalAttachmentResolver.Resolve(Overview(employee), Identity(), Status(), new Dictionary<string, RemoteWorkerSnapshot> { [employee.Id] = remote }, true, employee.Id).Route);
    }

    [Theory]
    [InlineData("rtb-other", "remote-session", 1)]
    [InlineData("rtb-test", "other-session", 1)]
    [InlineData("rtb-test", "remote-session", 0)]
    [InlineData("rtb-test", null, 1)]
    public void RemoteIdentityMismatchNeverFallsBack(string bindingId, string? sessionId, long epoch)
    {
        var employee = Employee(placement: RuntimePlacements.DeveloperContainer, sessionId: "remote-session");
        var remote = Snapshot(employee) with { RuntimeBindingId = bindingId, SessionId = sessionId, OwnershipEpoch = epoch, ViewerSupported = true, ViewerAvailable = true };
        Assert.Equal(TerminalAttachmentRoute.RemoteUnavailable, TerminalAttachmentResolver.Resolve(Overview(employee), Identity(), Status(), new Dictionary<string, RemoteWorkerSnapshot> { [employee.Id] = remote }, true, employee.Id).Route);
    }

    [Fact]
    public void ProductionBackendUnavailableKeepsExactRemoteUnavailable()
    {
        var employee = Employee(placement: RuntimePlacements.DeveloperContainer, sessionId: "remote-session");
        var remote = Snapshot(employee) with { ViewerSupported = true, ViewerAvailable = true };
        Assert.Equal(TerminalAttachmentRoute.RemoteUnavailable, TerminalAttachmentResolver.Resolve(Overview(employee), Identity(), Status(), new Dictionary<string, RemoteWorkerSnapshot> { [employee.Id] = remote }, false, employee.Id).Route);
    }

    private static TerminalTarget Resolve(EmployeeSummary employee) => TerminalAttachmentResolver.Resolve(Overview(employee), Identity(), Status(), null, false, employee.Id);
    private static RemoteWorkerSnapshot Snapshot(EmployeeSummary employee) => new(employee.Id, employee.RuntimeBindingId, "wrk-test", "host-test", "enrolled", true, "authenticated", "running", employee.SessionId, 1, 1, false, false, false, [], null);
    private static OrganizationOverview Overview(params EmployeeSummary[] employees) => new("org-test", "org", "Organization", "Description", "Instructions", 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, [new DepartmentSummary("dept-test", "operations", "Operations", employees.Length, 1, "Operations standing instructions")], [new RoleSummary("role-test", "dept-test", "operations-it", "Operations / IT", "role@1", "policy@1", "Standing", 1)], employees, []);
    private static EmployeeSummary Employee(string id = "emp-test", string bindingId = "rtb-test", string? sessionId = "native-session", string placement = RuntimePlacements.InternalSharedContainer) => new(id, "operations-it", "Operations / IT", "Purpose", "Instructions", "Rules", "Restrictions", "org-test", "dept-test", "operations", "Operations", "role-test", "operations-it", "Operations / IT", bindingId, placement, sessionId, "Session", null);
    private static OrganizationRuntimeIdentity Identity() => new("org-test", "org", "Organization", "emp-test", "Operations / IT", "rtb-test", "secret-token", "native-session", "Session", false, false);
    private static ControlStatus Status() => new() { State = "ready", OrganizationName = "Organization", SessionId = "native-session", Model = "provider/model", TerminalReady = true, SessionState = "idle" };
}
