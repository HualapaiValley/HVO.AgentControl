using HVO.AgentControl.RemoteWorker;
using HVO.AgentControl.Runtime;

namespace HVO.AgentControl.Organization;

public enum TerminalAttachmentRoute
{
    LocalEligible,
    RemoteEligible,
    RemoteUnavailable,
    EmployeeNotFound,
    Unavailable,
}

public sealed record TerminalTarget(TerminalAttachmentRoute Route, EmployeeSummary? Employee, RemoteWorkerSnapshot? Remote);

/// <summary>Exact local/remote routing for /terminal with no fallback between ownership domains.</summary>
public static class TerminalAttachmentResolver
{
    public static TerminalTarget Resolve(
        OrganizationOverview? overview,
        OrganizationRuntimeIdentity? identity,
        ControlStatus? status,
        IReadOnlyDictionary<string, RemoteWorkerSnapshot>? remote,
        bool remoteBackendAvailable,
        string? employeeId)
    {
        if (overview is null || status is null) return new(TerminalAttachmentRoute.Unavailable, null, null);
        var employee = overview.Employees.SingleOrDefault(item => item.Id == employeeId);
        if (employee is null) return new(TerminalAttachmentRoute.EmployeeNotFound, null, null);

        if (employee.Placement == RuntimePlacements.InternalSharedContainer)
            return new(IsExactHostOwnedSession(employee, identity, status) ? TerminalAttachmentRoute.LocalEligible : TerminalAttachmentRoute.Unavailable, employee, null);

        if (employee.Placement != RuntimePlacements.DeveloperContainer || remote is null || !remote.TryGetValue(employee.Id, out var target))
            return new(TerminalAttachmentRoute.RemoteUnavailable, employee, null);

        var exact = target.RuntimeBindingId == employee.RuntimeBindingId
            && target.SessionRecordId is not null
            && target.SessionRecordId == employee.SessionRecordId
            && target.NativeSessionId is not null
            && target.NativeSessionId == employee.NativeSessionId
            && target.EnrollmentEnabled
            && target.LifecycleStatus == "enrolled"
            && target.ConnectionState == "authenticated"
            && target.ProcessState == "running"
            && target.OwnershipEpoch > 0
            && !target.Held
            && target.ViewerSupported
            && target.ViewerAvailable
            && remoteBackendAvailable;
        return new(exact ? TerminalAttachmentRoute.RemoteEligible : TerminalAttachmentRoute.RemoteUnavailable, employee, target);
    }

    public static bool IsExactHostOwnedSession(EmployeeSummary employee, OrganizationRuntimeIdentity? identity, ControlStatus status)
    {
        ArgumentNullException.ThrowIfNull(employee);
        ArgumentNullException.ThrowIfNull(status);
        return identity is not null
            && string.Equals(employee.Id, identity.EmployeeId, StringComparison.Ordinal)
            && string.Equals(employee.RuntimeBindingId, identity.RuntimeBindingId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(employee.SessionId)
            && string.Equals(employee.SessionId, status.SessionId, StringComparison.Ordinal)
            && status.CanControl
            && status.TerminalReady;
    }
}
