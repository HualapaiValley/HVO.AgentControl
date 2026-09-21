using HVO.AgentControl.RemoteWorker;
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
    public const string Provisioning = "provisioning";
    public const string ReconciliationRequired = "reconciliation-required";
    public const string Interrupted = "interrupted";
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
    bool RemoteOwned,
    string? RemoteHostId,
    string? NativeSessionId,
    string? SessionTitle,
    string? ControlModel,
    string? ControlStatus,
    string? SessionState,
    bool TerminalAvailable,
    string? SanitizedError);

public sealed record WorkerPermissionsProjection(bool Supported, IReadOnlyList<WorkerPendingPermissionRecord> Items, string Reason);

/// <summary>
/// The owner-facing profile revision status appended to one employee detail. All
/// fields are null/false for a non-managed employee or the internal seed
/// employee, which has no managed enrollment resources.
/// </summary>
public sealed record EmployeeProfileStatusDetail(
    string? CurrentProfileId,
    string? CurrentProfileDisplayName,
    string? CurrentProfileRevisionId,
    int? CurrentRevisionNumber,
    string? CurrentImageDigest,
    string? CurrentPlatform,
    string? WorkerId,
    string? HostId,
    bool NewerRevisionAvailable,
    string? NewerRevisionId,
    int? NewerRevisionNumber,
    string? ActiveRebuildState,
    string? ActiveRebuildId);

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
    int Revision,
    string Availability,
    EmployeeRuntimeDetail Runtime,
    OrientationStatus? Orientation,
    TerminalDescriptor Terminal,
    WorkerPermissionsProjection PendingWorkerPermissions,
    UnsupportedFeature RecentLogs,
    EmployeeProfileStatusDetail ProfileStatus)
{
    /// <summary>
    /// Additive owner-facing recent tasks for this employee, newest first. It is
    /// empty for a non-managed employee (including the internal seed), which has
    /// no durable worker tasks.
    /// </summary>
    public IReadOnlyList<EmployeeTaskDetail> RecentTasks { get; init; } = [];
}

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

/// <summary>
/// One authoritative department: identity and revision, the persisted standing
/// instructions when a department orientation fragment exists, the roles
/// assigned to it, and the scoped employee roster with host-computed
/// availability. The roster is the department hierarchy, not an employee-filter
/// view.
/// </summary>
public sealed record PortalDepartmentDetail(
    string Id,
    string Slug,
    string DisplayName,
    string OrganizationId,
    string OrganizationDisplayName,
    int Revision,
    string? StandingInstructions,
    string HireUrl,
    IReadOnlyList<RoleSummary> Roles,
    IReadOnlyList<PortalEmployeeDetail> Employees,
    IReadOnlyList<AvailabilityCount> Availability,
    IReadOnlyList<OwnerAttentionItem> FailuresNeedingAttention);

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
    IReadOnlyList<HireRequestSummary> HireRequests,
    IReadOnlyList<AvailabilityCount> Availability,
    PendingApprovalsSummary PendingApprovals,
    PendingApprovalsSummary PendingWorkerPermissions,
    IReadOnlyList<OwnerAttentionItem> FailuresNeedingAttention);

/// <summary>Builds owner-facing diagnostics from one authoritative store read and one exact host snapshot.</summary>
public static class PortalOrganizationReadModel
{
    private static readonly string[] CategoryOrder =
    [
        EmployeeAvailabilityCategories.Ready,
        EmployeeAvailabilityCategories.Provisioning,
        EmployeeAvailabilityCategories.Held,
        EmployeeAvailabilityCategories.ReconciliationRequired,
        EmployeeAvailabilityCategories.Interrupted,
        EmployeeAvailabilityCategories.ReloadRequired,
        EmployeeAvailabilityCategories.OrientationFailed,
        EmployeeAvailabilityCategories.OrientationStale,
        EmployeeAvailabilityCategories.RuntimeUnavailable,
    ];

    public static PortalOrganizationOverview Build(
        OrganizationOverview overview,
        OrganizationRuntimeIdentity? identity,
        ControlStatus status,
        IRemoteWorkerStatusProvider? remoteProvider = null,
        IReadOnlyList<HireRequestSummary>? hireRequests = null,
        OrganizationStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(overview);
        ArgumentNullException.ThrowIfNull(status);

        var remote = remoteProvider?.Snapshot(overview) ?? new Dictionary<string, RemoteWorkerSnapshot>(StringComparer.Ordinal);
        var employees = overview.Employees
            .Select(employee => BuildEmployee(employee, identity, status, remote.GetValueOrDefault(employee.Id), store))
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
                $"/employees/{Uri.EscapeDataString(employee.Id)}"))
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
            hireRequests ?? [],
            Counts(employees),
            new PendingApprovalsSummary(
                Supported: true,
                Count: (hireRequests ?? []).Count(request => request.State == HireRequestStates.Requested),
                Items: (hireRequests ?? []).Where(request => request.State == HireRequestStates.Requested).Cast<object>().ToArray(),
                Reason: "Hire requests can be requested, approved or rejected. Approval selects a verified container profile revision build on a ready host, requires WorkerControl enabled with a usable configuration and resources within the controller ceilings, and creates the managed employee identity and runtime binding, then durably queues provisioning and orientation to Ready as background work."),
            new PendingApprovalsSummary(
                Supported: employees.Any(x => x.PendingWorkerPermissions.Supported),
                Count: employees.Sum(x => x.PendingWorkerPermissions.Items.Count),
                Items: employees.SelectMany(x => x.PendingWorkerPermissions.Items).Cast<object>().Take(64).ToArray(),
                Reason: "Remote worker permissions are a separate reject-only projection; raw payloads are never exposed."),
            failures);
    }

    public static PortalEmployeeDetail? FindEmployee(
        OrganizationOverview overview,
        OrganizationRuntimeIdentity? identity,
        ControlStatus status,
        string employeeId,
        IRemoteWorkerStatusProvider? remoteProvider = null,
        OrganizationStore? store = null)
    {
        var employee = overview.Employees.SingleOrDefault(item => item.Id == employeeId);
        if (employee is null) return null;
        var remote = remoteProvider?.Snapshot(overview).GetValueOrDefault(employeeId);
        return BuildEmployee(employee, identity, status, remote, store);
    }

    /// <summary>
    /// Builds the authoritative detail for one department selected by stable id,
    /// or null when no department carries that id. The roster, role summaries,
    /// scoped availability counts and attention items are computed from the same
    /// single store snapshot and exact host status as the overview.
    /// </summary>
    public static PortalDepartmentDetail? FindDepartment(
        OrganizationOverview overview,
        OrganizationRuntimeIdentity? identity,
        ControlStatus status,
        string departmentId,
        IRemoteWorkerStatusProvider? remoteProvider = null,
        OrganizationStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(overview);
        ArgumentNullException.ThrowIfNull(status);

        var department = overview.Departments.SingleOrDefault(item => item.Id == departmentId);
        if (department is null) return null;

        var remote = remoteProvider?.Snapshot(overview) ?? new Dictionary<string, RemoteWorkerSnapshot>(StringComparer.Ordinal);
        var employees = overview.Employees
            .Where(employee => employee.DepartmentId == departmentId)
            .Select(employee => BuildEmployee(employee, identity, status, remote.GetValueOrDefault(employee.Id), store))
            .ToArray();
        var roles = overview.Roles
            .Where(role => role.DepartmentId == departmentId)
            .ToArray();
        var failures = employees
            .Where(NeedsAttention)
            .Select(employee => new OwnerAttentionItem(
                employee.Id,
                employee.DisplayName,
                employee.Availability,
                AttentionSummary(employee),
                $"/employees/{Uri.EscapeDataString(employee.Id)}"))
            .ToArray();

        return new PortalDepartmentDetail(
            department.Id,
            department.Slug,
            department.DisplayName,
            overview.Id,
            overview.DisplayName,
            department.Revision,
            department.StandingInstructions,
            $"/hiring?departmentId={Uri.EscapeDataString(department.Id)}",
            roles,
            employees,
            Counts(employees),
            failures);
    }

    public static string Classify(EmployeeSummary employee, bool hostOwned, ControlStatus status)
    {
        ArgumentNullException.ThrowIfNull(employee);
        ArgumentNullException.ThrowIfNull(status);

        var orientation = employee.Orientation;
        if (orientation?.State is OrientationStates.Failed
            or OrientationStates.TimedOut
            or OrientationStates.Rejected
            or OrientationStates.Uncertain)
        {
            return EmployeeAvailabilityCategories.OrientationFailed;
        }

        // A stale assignment normally carries the reason in LastError. State is
        // authoritative, so that diagnostic must not reclassify it as failed.
        if (orientation?.State == OrientationStates.Stale)
        {
            return EmployeeAvailabilityCategories.OrientationStale;
        }

        if (!string.IsNullOrWhiteSpace(orientation?.LastError))
        {
            return EmployeeAvailabilityCategories.OrientationFailed;
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
        ControlStatus status,
        RemoteWorkerSnapshot? remote,
        OrganizationStore? store)
    {
        var hostOwned = identity is not null
            && string.Equals(employee.Id, identity.EmployeeId, StringComparison.Ordinal)
            && string.Equals(employee.RuntimeBindingId, identity.RuntimeBindingId, StringComparison.Ordinal);
        var exactSession = hostOwned
            && !string.IsNullOrWhiteSpace(employee.SessionId)
            && string.Equals(employee.SessionId, status.SessionId, StringComparison.Ordinal);
        var remoteOwned = remote is not null;
        var terminalAvailable = remoteOwned ? remote!.ViewerAvailable : exactSession && status.CanControl && status.TerminalReady;
        var terminalReason = remoteOwned
            ? remote!.ViewerSupported ? terminalAvailable ? "Exact remote worker terminal attachment is available." : "The remote worker session is not authenticated, running, and free of recovery holds." : "Remote viewer protocol code is present, but the production worker terminal backend is unavailable."
            : !hostOwned
            ? "This employee is not owned by the current control host."
            : !exactSession
                ? "The employee binding does not match the current native session."
                : !status.CanControl
                    ? "The current control session is unavailable."
                    : !status.TerminalReady
                        ? "Terminal attachment is disabled or not ready."
                        : "Exact host-owned terminal attachment is available.";
        var availability = remoteOwned ? ClassifyRemote(remote!) : Classify(employee, hostOwned, status);

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
            employee.Revision,
            availability,
            new EmployeeRuntimeDetail(
                employee.RuntimeBindingId,
                employee.Placement,
                hostOwned,
                remoteOwned,
                remote?.HostId,
                employee.SessionId,
                employee.SessionTitle,
                hostOwned ? status.Model : null,
                remoteOwned ? remote!.ConnectionState : hostOwned ? status.State : null,
                remoteOwned ? remote!.ProcessState : hostOwned ? status.SessionState : null,
                terminalAvailable,
                remoteOwned ? remote!.Detail : hostOwned ? status.Error : null),
            employee.Orientation,
            new TerminalDescriptor(
                Supported: remoteOwned ? remote!.ViewerSupported : hostOwned,
                Available: terminalAvailable,
                Reason: terminalReason,
                Url: terminalAvailable ? $"/terminal?employeeId={Uri.EscapeDataString(employee.Id)}" : null),
            new WorkerPermissionsProjection(
                Supported: remoteOwned && remote!.LifecycleStatus == "enrolled",
                Items: remoteOwned ? remote!.PendingWorkerPermissions : [],
                Reason: remoteOwned ? "Reject-only worker permission decisions use the authoritative stored projection." : "Internal runtime pending approvals remain a separate unsupported feature."),
            new UnsupportedFeature(
                Supported: false,
                Reason: "Recent runtime logs are not exposed because a safe employee-scoped log contract is not implemented."),
            BuildProfileStatus(store, employee.Id))
        {
            RecentTasks = BuildRecentTasks(store, employee.Id),
        };
    }

    /// <summary>
    /// The employee-scoped recent tasks for one employee, or an empty list when
    /// the store is unavailable, the employee is not a managed employee, or no
    /// task has been recorded. Never throws for an unknown employee and never
    /// mutates anything.
    /// </summary>
    private static IReadOnlyList<EmployeeTaskDetail> BuildRecentTasks(OrganizationStore? store, string employeeId)
    {
        if (store is null) return [];
        try { return store.ListEmployeeTaskDetails(employeeId, 10); }
        catch (OrganizationStoreCorruptException) { return []; }
    }

    /// <summary>
    /// The profile revision status for one employee, or an all-null projection
    /// when the store is unavailable or the employee is not a managed employee
    /// (including the internal seed employee). Never throws for a non-managed
    /// employee and never mutates anything.
    /// </summary>
    private static EmployeeProfileStatusDetail BuildProfileStatus(OrganizationStore? store, string employeeId)
    {
        if (store is null) return EmptyProfileStatus();
        var status = store.GetEmployeeProfileStatus(employeeId);
        return status is null
            ? EmptyProfileStatus()
            : ToProfileStatusDetail(status);
    }

    public static EmployeeProfileStatusDetail ToProfileStatusDetail(EmployeeProfileStatus status) => new(
        status.CurrentProfileId,
        status.CurrentProfileDisplayName,
        status.CurrentProfileRevisionId,
        status.CurrentRevisionNumber,
        status.CurrentImageDigest,
        status.CurrentPlatform,
        status.WorkerId,
        status.HostId,
        status.NewerRevisionAvailable,
        status.NewerRevisionId,
        status.NewerRevisionNumber,
        status.ActiveRebuild?.State,
        status.ActiveRebuild?.Id);

    private static EmployeeProfileStatusDetail EmptyProfileStatus() => new(
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        NewerRevisionAvailable: false,
        null,
        null,
        null,
        null);

    private static string ClassifyRemote(RemoteWorkerSnapshot remote) => remote.LifecycleStatus switch
    {
        "planned" or "provisioning" => EmployeeAvailabilityCategories.Provisioning,
        "held" or "failed" => EmployeeAvailabilityCategories.ReconciliationRequired,
        _ when remote.Held => EmployeeAvailabilityCategories.ReconciliationRequired,
        _ when remote.ProcessState is "exited" or "protocol-failed" or "transport-uncertain" => EmployeeAvailabilityCategories.Interrupted,
        "enrolled" when remote.ConnectionState == "authenticated" && remote.ProcessState == "running" => EmployeeAvailabilityCategories.Ready,
        _ => EmployeeAvailabilityCategories.RuntimeUnavailable,
    };

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
