using System.Security.Cryptography;

namespace HVO.AgentControl.Organization;

/// <summary>
/// Stable identifier generation for the authoritative store. IDs are generated
/// once, persist for the life of the record, and are never derived from a slug,
/// display name, process id or container id. Slugs and names are mutable labels.
/// </summary>
public static class OrganizationIds
{
    public const string OrganizationPrefix = "org-";
    public const string DepartmentPrefix = "dept-";
    public const string RolePrefix = "role-";
    public const string EmployeePrefix = "emp-";
    public const string RuntimeBindingPrefix = "rtb-";
    public const string SessionPrefix = "acps-";
    public const string AuditPrefix = "audit-";

    public static string NewOrganizationId() => NewId(OrganizationPrefix);
    public static string NewDepartmentId() => NewId(DepartmentPrefix);
    public static string NewRoleId() => NewId(RolePrefix);
    public static string NewEmployeeId() => NewId(EmployeePrefix);
    public static string NewRuntimeBindingId() => NewId(RuntimeBindingPrefix);
    public static string NewSessionId() => NewId(SessionPrefix);
    public static string NewAuditId() => NewId(AuditPrefix);

    /// <summary>Generates a stable random identifier with the supplied prefix.</summary>
    public static string NewId(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        return prefix + RandomNumberGenerator.GetHexString(16).ToLowerInvariant();
    }

    /// <summary>Per-binding tmux owner token, matching the legacy runtime.json token shape.</summary>
    public static string NewOwnerToken() => RandomNumberGenerator.GetHexString(24).ToLowerInvariant();
}

/// <summary>Placement of a runtime binding. Only the internal shared container is seeded in this phase.</summary>
public static class RuntimePlacements
{
    public const string InternalSharedContainer = "InternalSharedContainer";
}

/// <summary>Identifies the single combined Operations/IT seed role and its employee in Operations.</summary>
public static class OrganizationSeed
{
    public const string OrganizationSlug = "agentcontrol-development";
    public const string OrganizationDescription = "Development organization for the AgentControl control host and its managed employees.";
    public const string OrganizationInstructions = "Preserve durable identity and history, follow owner-approved host policy, and fail closed when authority or effects are uncertain.";
    public const string OperationsSlug = "operations";
    public const string OperationsDisplayName = "Operations";
    public const string DevelopmentSlug = "development";
    public const string DevelopmentDisplayName = "Development";
    public const string QaSlug = "qa";
    public const string QaDisplayName = "QA";

    public const string OperationsItRoleSlug = "operations-it";
    public const string OperationsItRoleDisplayName = "Operations / IT";
    public const string OperationsItRoleInstructionProfile = "role/operations-it@1";
    public const string OperationsItRolePermissionProfile = "policy/operations-it@1";

    public const string AdoptedEmployeeSlug = "operations-it";
    public const string AdoptedEmployeeDisplayName = "Operations / IT";
    public const string AdoptedEmployeePurpose = "Operate and maintain the AgentControl control host and its internal runtime.";
    public const string AdoptedEmployeeInstructions = "Request privileged or organizational changes through host-owned operations and report uncertain effects instead of retrying them.";
    public const string AdoptedEmployeeRules = "Use stable persisted identities; preserve existing sessions and history; treat owner approval as a host record, never a model assertion.";
    public const string AdoptedEmployeeRestrictions = "No direct edits to the authoritative store, no autonomous hiring or provisioning, and no access to controller secrets or another employee's history.";
}

/// <summary>A department and its current employee count.</summary>
public sealed record DepartmentSummary(
    string Id,
    string Slug,
    string DisplayName,
    int EmployeeCount);

/// <summary>A role definition with its department and versioned profile references.</summary>
public sealed record RoleSummary(
    string Id,
    string DepartmentId,
    string Slug,
    string DisplayName,
    string InstructionProfile,
    string PermissionProfile);

/// <summary>An employee joined to its department, role and runtime binding.</summary>
public sealed record EmployeeSummary(
    string Id,
    string Slug,
    string DisplayName,
    string Purpose,
    string Instructions,
    string Rules,
    string Restrictions,
    string OrganizationId,
    string DepartmentId,
    string DepartmentSlug,
    string DepartmentDisplayName,
    string RoleId,
    string RoleSlug,
    string RoleDisplayName,
    string RuntimeBindingId,
    string Placement,
    string? SessionId,
    string? SessionTitle);

/// <summary>An adoption audit record: who adopted what, under which authorization.</summary>
public sealed record AdoptionAuditSummary(
    string Id,
    string OrganizationId,
    string? EmployeeId,
    string Source,
    string AuthorizationReference,
    DateTimeOffset AdoptedAt);

/// <summary>Read model for the minimal organization overview.</summary>
public sealed record OrganizationOverview(
    string Id,
    string Slug,
    string DisplayName,
    string Description,
    string BasicInstructions,
    int Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<DepartmentSummary> Departments,
    IReadOnlyList<RoleSummary> Roles,
    IReadOnlyList<EmployeeSummary> Employees,
    IReadOnlyList<AdoptionAuditSummary> AdoptionAudit);

/// <summary>
/// The durable identity the control host uses to start its runtime. It is read
/// from the store before OpenCode starts, and the store is authoritative over
/// the startup configuration for the organization display name.
/// </summary>
public sealed record OrganizationRuntimeIdentity(
    string OrganizationId,
    string OrganizationSlug,
    string OrganizationDisplayName,
    string EmployeeId,
    string EmployeeDisplayName,
    string RuntimeBindingId,
    string TmuxOwnerToken,
    string? SessionId,
    string? SessionTitle,
    bool Created,
    bool DisplayNameDiffersFromConfiguration);
