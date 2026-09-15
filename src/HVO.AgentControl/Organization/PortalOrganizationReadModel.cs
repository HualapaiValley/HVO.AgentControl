using HVO.AgentControl.Runtime;

namespace HVO.AgentControl.Organization;

/// <summary>Host-computed availability categories. These are not worker lifecycle states.</summary>
public static class EmployeeAvailabilityCategories
{
    public const string Ready = "ready";
    public const string Held = "held";
    public const string ReloadRequired = "reload-required";
    public const string OrientationFailed = "orientation-failed";
    public const string OrientationStale = "orientation-stale";
    public const string RuntimeUnavailable = "runtime-unavailable";
}

public sealed record AvailabilityCount(string Category, int Count);

public sealed record PortalDepartmentSummary(
    string Id,
    string Slug,
    string DisplayName,
    int EmployeeCount,
    IReadOnlyList<AvailabilityCount> Availability);

public sealed record UnsupportedFeature(bool Supported, string Reason);

public sealed record TerminalDescriptor(bool Supported, bool Available, string Reason, string? Url);

public sealed record EmployeeRuntimeDetail(
    string BindingId,
    string Placement,
    bool HostOwned,
    string? NativeSessionId,
    string? SessionTitle,
    string? ControlModel,
    string? ControlStatus,
    string? SessionState,
    bool TerminalAvailable,
    string? SanitizedError);

public sealed record PortalEmployeeDetail(
    string Id,
    string Slug,
    string DisplayName,
    string OrganizationId,
    string DepartmentId,
    string DepartmentSlug,
    string DepartmentDisplayName,
    string RoleId,
    string RoleSlug,
    string RoleDisplayName,
    string Purpose,
    string Instructions,
    string Rules,
    string Restrictions,
    string Availability,
    EmployeeRuntimeDetail Runtime,
    OrientationStatus? Orientation,
    TerminalDescriptor Terminal,
    UnsupportedFeature RecentLogs);

public sealed record OwnerAttentionItem(
    string EmployeeId,
    string EmployeeDisplayName,
    string Category,
    string Summary,
    string Url);

public sealed record PendingApprovalsSummary(
    bool Supported,
    int Count,
    IReadOnlyList<object> Items,
    string Reason);

public sealed record PortalOrganizationOverview(
    string Id,
    string Slug,
    string DisplayName,
    string Description,
    string BasicInstructions,
    int Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<PortalDepartmentSummary> Departments,
    IReadOnlyList<RoleSummary> Roles,
    IReadOnlyList<PortalEmployeeDetail> Employees,
    IReadOnlyList<AvailabilityCount> Availability,
    PendingApprovalsSummary PendingApprovals,
    IReadOnlyList<OwnerAttentionItem> FailuresNeedingAttention);

/// <summary>Builds owner-facing diagnostics from one authoritative store read and one exact host snapshot.</summary>
public static class PortalOrganizationReadModel
{
    private static readonly string[] CategoryOrder =
    [
        EmployeeAvailabilityCategories.Ready,
        EmployeeAvailabilityCategories.Held,
        EmployeeAvailabilityCategories.ReloadRequired,
        EmployeeAvailabilityCategories.OrientationFailed,
        EmployeeAvailabilityCategories.OrientationStale,
        EmployeeAvailabilityCategories.RuntimeUnavailable,
    ];

    public static PortalOrganizationOverview Build(
        OrganizationOverview overview,
        OrganizationRuntimeIdentity? identity,
        ControlStatus status)
    {
        ArgumentNullException.ThrowIfNull(overview);
        ArgumentNullException.ThrowIfNull(status);

        var employees = overview.Employees
            .Select(employee => BuildEmployee(employee, identity, status))
            .ToArray();
        var departments = overview.Departments
            .Select(department => new PortalDepartmentSummary(
                department.Id,
                department.Slug,
                department.DisplayName,
                department.EmployeeCount,
                Counts(employees.Where(employee => employee.DepartmentId == department.Id))))
            .ToArray();
        var failures = employees
            .Where(NeedsAttention)
            .Select(employee => new OwnerAttentionItem(
                employee.Id,
                employee.DisplayName,
                employee.Availability,
                AttentionSummary(employee),
                $"/#employee/{Uri.EscapeDataString(employee.Id)}"))
            .ToArray();

        return new PortalOrganizationOverview(
            overview.Id,
            overview.Slug,
            overview.DisplayName,
            overview.Description,
            overview.BasicInstructions,
            overview.Revision,
            overview.CreatedAt,
            overview.UpdatedAt,
            departments,
            overview.Roles,
            employees,
            Counts(employees),
            new PendingApprovalsSummary(
                Supported: false,
                Count: 0,
                Items: [],
                Reason: "Owner approval workflow is not implemented; issue #219 is outside this baseline."),
            failures);
    }

    public static PortalEmployeeDetail? FindEmployee(
        OrganizationOverview overview,
        OrganizationRuntimeIdentity? identity,
        ControlStatus status,
        string employeeId)
    {
        var employee = overview.Employees.SingleOrDefault(item => item.Id == employeeId);
        return employee is null ? null : BuildEmployee(employee, identity, status);
    }

    public static string Classify(EmployeeSummary employee, bool hostOwned, ControlStatus status)
    {
        ArgumentNullException.ThrowIfNull(employee);
        ArgumentNullException.ThrowIfNull(status);

        var orientation = employee.Orientation;
        if (orientation is not null
            && (orientation.State is OrientationStates.Failed
                or OrientationStates.TimedOut
                or OrientationStates.Rejected
                or OrientationStates.Uncertain
                || !string.IsNullOrWhiteSpace(orientation.LastError)))
        {
            return EmployeeAvailabilityCategories.OrientationFailed;
        }

        if (orientation?.State == OrientationStates.Stale)
        {
            return EmployeeAvailabilityCategories.OrientationStale;
        }

        if (orientation?.RestartRequired == true)
        {
            return EmployeeAvailabilityCategories.ReloadRequired;
        }

        // Runtime state is evaluated before a non-critical dispatch hold. A
        // manual or unacknowledged orientation hold must not mask a host that is
        // not the exact owner, has lost the native session, or cannot control it.
        // The critical orientation conditions above still take precedence.
        var exactSession = hostOwned
            && !string.IsNullOrWhiteSpace(employee.SessionId)
            && string.Equals(employee.SessionId, status.SessionId, StringComparison.Ordinal);
        if (!exactSession || !status.CanControl)
        {
            return EmployeeAvailabilityCategories.RuntimeUnavailable;
        }

        if (orientation?.DispatchHeld == true)
        {
            return EmployeeAvailabilityCategories.Held;
        }

        return EmployeeAvailabilityCategories.Ready;
    }

    private static PortalEmployeeDetail BuildEmployee(
        EmployeeSummary employee,
        OrganizationRuntimeIdentity? identity,
        ControlStatus status)
    {
        var hostOwned = identity is not null
            && string.Equals(employee.Id, identity.EmployeeId, StringComparison.Ordinal)
            && string.Equals(employee.RuntimeBindingId, identity.RuntimeBindingId, StringComparison.Ordinal);
        var exactSession = hostOwned
            && !string.IsNullOrWhiteSpace(employee.SessionId)
            && string.Equals(employee.SessionId, status.SessionId, StringComparison.Ordinal);
        var terminalAvailable = exactSession && status.CanControl && status.TerminalReady;
        var terminalReason = !hostOwned
            ? "This employee is not owned by the current control host."
            : !exactSession
                ? "The employee binding does not match the current native session."
                : !status.CanControl
                    ? "The current control session is unavailable."
                    : !status.TerminalReady
                        ? "Terminal attachment is disabled or not ready."
                        : "Exact host-owned terminal attachment is available.";
        var availability = Classify(employee, hostOwned, status);

        return new PortalEmployeeDetail(
            employee.Id,
            employee.Slug,
            employee.DisplayName,
            employee.OrganizationId,
            employee.DepartmentId,
            employee.DepartmentSlug,
            employee.DepartmentDisplayName,
            employee.RoleId,
            employee.RoleSlug,
            employee.RoleDisplayName,
            employee.Purpose,
            employee.Instructions,
            employee.Rules,
            employee.Restrictions,
            availability,
            new EmployeeRuntimeDetail(
                employee.RuntimeBindingId,
                employee.Placement,
                hostOwned,
                employee.SessionId,
                employee.SessionTitle,
                hostOwned ? status.Model : null,
                hostOwned ? status.State : null,
                hostOwned ? status.SessionState : null,
                terminalAvailable,
                hostOwned ? status.Error : null),
            employee.Orientation,
            new TerminalDescriptor(
                Supported: hostOwned,
                Available: terminalAvailable,
                Reason: terminalReason,
                Url: terminalAvailable ? $"/terminal?employeeId={Uri.EscapeDataString(employee.Id)}" : null),
            new UnsupportedFeature(
                Supported: false,
                Reason: "Recent runtime logs are not exposed because a safe employee-scoped log contract is not implemented."));
    }

    private static IReadOnlyList<AvailabilityCount> Counts(IEnumerable<PortalEmployeeDetail> employees)
    {
        var counts = employees.GroupBy(employee => employee.Availability)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return CategoryOrder.Select(category => new AvailabilityCount(
            category,
            counts.GetValueOrDefault(category))).ToArray();
    }

    private static bool NeedsAttention(PortalEmployeeDetail employee) =>
        employee.Availability is EmployeeAvailabilityCategories.ReloadRequired
            or EmployeeAvailabilityCategories.OrientationFailed
            or EmployeeAvailabilityCategories.OrientationStale
        || !string.IsNullOrWhiteSpace(employee.Runtime.SanitizedError);

    private static string AttentionSummary(PortalEmployeeDetail employee)
    {
        if (!string.IsNullOrWhiteSpace(employee.Orientation?.LastError))
        {
            return employee.Orientation.LastError;
        }

        if (!string.IsNullOrWhiteSpace(employee.Runtime.SanitizedError))
        {
            return employee.Runtime.SanitizedError;
        }

        return employee.Availability switch
        {
            EmployeeAvailabilityCategories.ReloadRequired => "Orientation is delivered but the runtime must restart or reload it.",
            EmployeeAvailabilityCategories.OrientationStale => "Standing organization or role instructions changed; orientation is stale.",
            _ => "Orientation requires owner attention.",
        };
    }
}
