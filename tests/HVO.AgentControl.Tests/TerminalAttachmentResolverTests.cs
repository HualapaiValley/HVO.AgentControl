using HVO.AgentControl.Organization;
using HVO.AgentControl.Runtime;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Pure routing contract for <c>/terminal</c>. The exact host-owned tuple
/// (employee, binding, persisted session, control and terminal readiness)
/// resolves eligible; every mismatch or missing snapshot fails closed and never
/// falls back to the host-owned employee.
/// </summary>
public sealed class TerminalAttachmentResolverTests
{
    [Fact]
    public void ExactHostOwnedTupleRoutesEligible()
    {
        var route = TerminalAttachmentResolver.Resolve(
            Overview(Employee("emp-test", "rtb-test", "native-session")),
            Identity("emp-test", "rtb-test", "native-session"),
            Status(),
            "emp-test");

        Assert.Equal(TerminalAttachmentRoute.Eligible, route);
    }

    [Fact]
    public void MissingStoreIdentityOrStatusFailsClosedAsUnavailable()
    {
        var overview = Overview(Employee("emp-test", "rtb-test", "native-session"));
        var identity = Identity("emp-test", "rtb-test", "native-session");
        var status = Status();

        Assert.Equal(
            TerminalAttachmentRoute.Unavailable,
            TerminalAttachmentResolver.Resolve(null, identity, status, "emp-test"));
        Assert.Equal(
            TerminalAttachmentRoute.Unavailable,
            TerminalAttachmentResolver.Resolve(overview, null, status, "emp-test"));
        Assert.Equal(
            TerminalAttachmentRoute.Unavailable,
            TerminalAttachmentResolver.Resolve(overview, identity, null, "emp-test"));
    }

    [Fact]
    public void UnknownEmployeeIsNotFoundWhenTheHostIsAvailable()
    {
        var route = TerminalAttachmentResolver.Resolve(
            Overview(Employee("emp-test", "rtb-test", "native-session")),
            Identity("emp-test", "rtb-test", "native-session"),
            Status(),
            "emp-does-not-exist");

        Assert.Equal(TerminalAttachmentRoute.EmployeeNotFound, route);
    }

    [Fact]
    public void MissingHostIdentityDoesNotTurnAnUnknownIdIntoNotFound()
    {
        var route = TerminalAttachmentResolver.Resolve(
            null,
            null,
            Status(),
            "emp-does-not-exist");

        Assert.Equal(TerminalAttachmentRoute.Unavailable, route);
    }

    [Theory]
    [InlineData("rtb-other", "native-session", "ready", true)]    // binding mismatch
    [InlineData("rtb-test", "native-other", "ready", true)]       // native session mismatch
    [InlineData("rtb-test", "", "ready", true)]                   // persisted session missing
    [InlineData("rtb-test", "native-session", "faulted", true)]   // host cannot control
    [InlineData("rtb-test", "native-session", "ready", false)]    // terminal not ready
    public void MismatchedTupleFailsClosedAsUnavailable(
        string bindingId,
        string? sessionId,
        string state,
        bool terminalReady)
    {
        var route = TerminalAttachmentResolver.Resolve(
            Overview(Employee("emp-test", bindingId, sessionId)),
            Identity("emp-test", "rtb-test", "native-session"),
            Status() with { State = state, TerminalReady = terminalReady },
            "emp-test");

        Assert.Equal(TerminalAttachmentRoute.Unavailable, route);
    }

    [Fact]
    public void MismatchedEmployeeNeverFallsBackToTheHostOwnedEmployee()
    {
        var overview = Overview(
            Employee("emp-test", "rtb-test", "native-session"),
            Employee("emp-other", "rtb-test", "native-session"));
        var identity = Identity("emp-test", "rtb-test", "native-session");

        Assert.Equal(
            TerminalAttachmentRoute.Eligible,
            TerminalAttachmentResolver.Resolve(overview, identity, Status(), "emp-test"));
        Assert.Equal(
            TerminalAttachmentRoute.Unavailable,
            TerminalAttachmentResolver.Resolve(overview, identity, Status(), "emp-other"));
    }

    private static OrganizationOverview Overview(params EmployeeSummary[] employees) => new(
        "org-test", "org", "Organization", "Description", "Instructions", 1,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
        [new DepartmentSummary("dept-test", "operations", "Operations", employees.Length)],
        [new RoleSummary("role-test", "dept-test", "operations-it", "Operations / IT", "role@1", "policy@1", "Standing", 1)],
        employees, []);

    private static EmployeeSummary Employee(string id, string bindingId, string? sessionId) => new(
        id, "operations-it", "Operations / IT", "Purpose", "Instructions", "Rules", "Restrictions",
        "org-test", "dept-test", "operations", "Operations", "role-test", "operations-it", "Operations / IT",
        bindingId, RuntimePlacements.InternalSharedContainer, sessionId, "Session", null);

    private static OrganizationRuntimeIdentity Identity(string employeeId, string bindingId, string? sessionId) => new(
        "org-test", "org", "Organization", employeeId, "Operations / IT", bindingId, "secret-token",
        sessionId, "Session", false, false);

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
