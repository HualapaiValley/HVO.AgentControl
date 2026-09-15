using HVO.AgentControl.Runtime;

namespace HVO.AgentControl.Organization;

/// <summary>Host-side classification for a validated <c>/terminal</c> request.</summary>
public enum TerminalAttachmentRoute
{
    /// <summary>
    /// The employee, authoritative runtime binding, persisted native session and
    /// current host status all match exactly and both control and terminal
    /// readiness are true. Only this route attaches the host-owned TUI in the
    /// host data directory.
    /// </summary>
    Eligible,

    /// <summary>No employee with the requested stable id exists in the overview.</summary>
    EmployeeNotFound,

    /// <summary>
    /// The store, identity or status snapshot is missing, or the employee is not
    /// the exact host-owned binding/session or control/terminal is not ready.
    /// </summary>
    Unavailable,
}

/// <summary>
/// Pure routing for <c>/terminal</c>. It joins one authoritative store snapshot
/// with the exact host identity and status and never falls back to a different
/// employee, binding or session.
/// </summary>
public static class TerminalAttachmentResolver
{
    /// <summary>
    /// Classifies one already-validated employee id. Unknown ids are only
    /// reported as <see cref="TerminalAttachmentRoute.EmployeeNotFound"/> when
    /// the store and host identity are available; a missing store/identity/status
    /// fails closed as <see cref="TerminalAttachmentRoute.Unavailable"/> so an
    /// unknown id can never mask an unavailable host.
    /// </summary>
    public static TerminalAttachmentRoute Resolve(
        OrganizationOverview? overview,
        OrganizationRuntimeIdentity? identity,
        ControlStatus? status,
        string? employeeId)
    {
        if (overview is null || identity is null || status is null)
        {
            return TerminalAttachmentRoute.Unavailable;
        }

        var employee = overview.Employees.SingleOrDefault(item => item.Id == employeeId);
        if (employee is null)
        {
            return TerminalAttachmentRoute.EmployeeNotFound;
        }

        return IsExactHostOwnedSession(employee, identity, status)
            ? TerminalAttachmentRoute.Eligible
            : TerminalAttachmentRoute.Unavailable;
    }

    /// <summary>
    /// True only when the employee identity, authoritative binding, persisted
    /// native session and current host status all match and both control and
    /// terminal attachment are ready. No field is optional and there is no
    /// fallback candidate.
    /// </summary>
    public static bool IsExactHostOwnedSession(
        EmployeeSummary employee,
        OrganizationRuntimeIdentity identity,
        ControlStatus status)
    {
        ArgumentNullException.ThrowIfNull(employee);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(status);

        return string.Equals(employee.Id, identity.EmployeeId, StringComparison.Ordinal)
            && string.Equals(employee.RuntimeBindingId, identity.RuntimeBindingId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(employee.SessionId)
            && string.Equals(employee.SessionId, status.SessionId, StringComparison.Ordinal)
            && status.CanControl
            && status.TerminalReady;
    }
}
