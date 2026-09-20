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
    public const string FragmentPrefix = "frag-";
    public const string AssignmentPrefix = "ora-";
    public const string EvidencePrefix = "ore-";
    public const string PolicyPrefix = "pol-";
    public const string RestrictionPrefix = "rst-";
    public const string HoldPrefix = "hold-";
    public const string GrantPrefix = "grant-";
    public const string PermissionAuditPrefix = "paud-";
    public const string PermissionRequestPrefix = "preq-";
    public const string HireRequestPrefix = "hire-";
    public const string HireRequestEventPrefix = "hevt-";
    public const string ContainerProfilePrefix = "prof-";
    public const string ContainerProfileRevisionPrefix = "prev-";
    public const string ProfileBuildPrefix = "pbld-";
    public const string RebuildPrefix = "rbld-";

    public static string NewOrganizationId() => NewId(OrganizationPrefix);
    public static string NewDepartmentId() => NewId(DepartmentPrefix);
    public static string NewRoleId() => NewId(RolePrefix);
    public static string NewEmployeeId() => NewId(EmployeePrefix);
    public static string NewRuntimeBindingId() => NewId(RuntimeBindingPrefix);
    public static string NewSessionId() => NewId(SessionPrefix);
    public static string NewAuditId() => NewId(AuditPrefix);
    public static string NewFragmentId() => NewId(FragmentPrefix);
    public static string NewAssignmentId() => NewId(AssignmentPrefix);
    public static string NewEvidenceId() => NewId(EvidencePrefix);
    public static string NewPolicyId() => NewId(PolicyPrefix);
    public static string NewRestrictionId() => NewId(RestrictionPrefix);
    public static string NewHoldId() => NewId(HoldPrefix);
    public static string NewGrantId() => NewId(GrantPrefix);
    public static string NewPermissionAuditId() => NewId(PermissionAuditPrefix);
    public static string NewPermissionRequestId() => NewId(PermissionRequestPrefix);
    public static string NewHireRequestId() => NewId(HireRequestPrefix);
    public static string NewHireRequestEventId() => NewId(HireRequestEventPrefix);
    public static string NewContainerProfileId() => NewId(ContainerProfilePrefix);
    public static string NewContainerProfileRevisionId() => NewId(ContainerProfileRevisionPrefix);
    public static string NewProfileBuildId() => NewId(ProfileBuildPrefix);
    public static string NewEmployeeRebuildId() => NewId(RebuildPrefix);

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
    public const string DeveloperContainer = "DeveloperContainer";
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
    public const string DepartmentOrientation = "Department: Operations. Report organizational or privileged changes to the owner. Escalate uncertainty, failed controls, suspected secret exposure, and irreversible effects before retrying.";
    public const string RoleOrientation = "Allowed duties: operate and maintain the control host; inspect runtime health and sanitized diagnostics; explain organization state; request owner-authorized changes. This is not a code worker and has no task-dispatch authority.";
    public const string HostPolicyVersion = "host-policy-phase1-v1";
    public const string HostPolicySummary = "Phase 1 host policy: informational Operations/IT duties only; no secrets, control-state mutation, unrestricted Docker/GitHub/host authority, autonomous hiring, Fleet/V1, or cross-employee history.";
}

public static class OrientationStates
{
    public const string Assigned = "Assigned";
    public const string Delivered = "Delivered";
    public const string Acknowledged = "Acknowledged";
    public const string Comprehended = "Comprehended";
    public const string Failed = "Failed";
    public const string TimedOut = "TimedOut";
    public const string Rejected = "Rejected";
    public const string Stale = "Stale";
    public const string Uncertain = "Uncertain";
}

public static class DispatchHoldReasons
{
    public const string OrientationUnacknowledged = "orientation-unacknowledged";
    public const string OrientationStale = "stale";
    public const string OrientationFailed = "failed";
    public const string PolicyUpdate = "policy-update";
    public const string OrientationReloadRequired = "orientation-reload-required";
    public const string Manual = "manual";
}

public enum OrientationEvidenceSource
{
    OwnerSubmitted,
    LiveModel,
}

public static class OrientationEvidenceSources
{
    public const string OwnerSubmitted = "owner-submitted";
    public const string LiveModel = "live-model";

    public static string ToWireValue(this OrientationEvidenceSource source) => source switch
    {
        OrientationEvidenceSource.OwnerSubmitted => OwnerSubmitted,
        OrientationEvidenceSource.LiveModel => LiveModel,
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };
}

public sealed record OrientationStatus(
    string EmployeeId,
    string RuntimeBindingId,
    string? SessionId,
    string AssignmentId,
    int Revision,
    string OrientationVersion,
    string State,
    DateTimeOffset AssignedAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? AcknowledgedAt,
    DateTimeOffset? ComprehendedAt,
    string? EvidenceHash,
    string? EvidenceSummary,
    string? EvidenceSource,
    string ArtifactFileName,
    long ArtifactBytes,
    string PolicyVersion,
    int PolicyRevision,
    long? RequiredRuntimeGeneration,
    long? LoadedRuntimeGeneration,
    bool RestartRequired,
    bool DispatchHeld,
    IReadOnlyList<string> HoldReasons,
    string? LastError)
{
    public bool Ready => State == OrientationStates.Comprehended && !RestartRequired && !DispatchHeld;
}

public sealed record OrientationArtifact(
    string AssignmentId,
    string EmployeeId,
    string RuntimeBindingId,
    string? SessionId,
    string OrientationVersion,
    string Content,
    string ArtifactFileName,
    int AssignmentRevision);

public sealed record OrientationEvidenceRequest(
    string AssignmentId,
    string EmployeeId,
    string SessionId,
    string OrientationVersion,
    string Identity,
    string Department,
    string Reporting,
    IReadOnlyList<string>? Duties,
    IReadOnlyList<string>? Restrictions,
    string Escalation,
    int ExpectedRevision);

public sealed record PermissionGrantRequest(
    string EmployeeId,
    string RestrictionId,
    string Tool,
    string Resource,
    DateTimeOffset ExpiresAt,
    int PolicyRevision,
    string IdempotencyKey);

public sealed record PermissionGrantSummary(
    string Id,
    string EmployeeId,
    string RestrictionId,
    string Tool,
    string Resource,
    int PolicyRevision,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt,
    int Revision);

public sealed record PermissionDecision(
    bool Allowed,
    string Decision,
    string? OptionId,
    string? RestrictionId,
    string Tool,
    string Resource);

public static class HireRequestStates
{
    public const string Requested = "Requested";
    public const string Approved = "Approved";
    public const string Provisioning = "Provisioning";
    public const string Orienting = "Orienting";
    public const string Ready = "Ready";
    public const string Rejected = "Rejected";
    public const string Failed = "Failed";
    public const string Interrupted = "Interrupted";
    public const string Uncertain = "Uncertain";
}

public sealed record HireRequestCreate(
    string? IdempotencyKey,
    string? RequestedDisplayName,
    string? Purpose,
    string? DepartmentId,
    string? RoleId,
    string? Placement,
    int CpuLimit,
    int MemoryLimitMiB,
    int PidsLimit);

public sealed record HireRequestReject(int ExpectedRevision);
public sealed record HireRequestResume(int ExpectedRevision);

/// <summary>
/// An exact owner approval selection for one hire request. The server resolves
/// the unique verified profile build for <see cref="ProfileRevisionId"/> on the
/// controller-local Docker target; the caller never supplies a host or build id.
/// </summary>
public sealed record HireRequestApprove(int ExpectedRevision, string ProfileRevisionId);

/// <summary>A frozen owner approval and its optional managed-employee links.</summary>
public sealed record HireRequestApprovalRecord(
    string HireRequestId,
    string ApprovedRequestVersion,
    int ApprovedRequestRevision,
    string RequestVersionHash,
    string ProfileRevisionId,
    string ProfileBuildId,
    string ImageDigest,
    string HostId,
    string Platform,
    int CpuLimit,
    int MemoryLimitMiB,
    int PidsLimit,
    string ApprovalIdentity,
    DateTimeOffset ApprovedAt,
    string? EmployeeId,
    string? RuntimeBindingId,
    string? WorkerId,
    int Revision);

/// <summary>
/// The frozen, per-binding resources a later provisioning run consumes. This is
/// the durable bridge that avoids rebuilding the v4 <c>worker_enrollments</c>
/// table: the owner approval freezes them once and provisioning reads them here.
/// </summary>
public sealed record ManagedEnrollmentResourcesRecord(
    string RuntimeBindingId,
    int CpuLimit,
    int MemoryLimitMiB,
    int PidsLimit,
    string ApprovedProfileRevisionId,
    string ApprovedProfileBuildId,
    string ApprovedImageDigest,
    string ApprovedHostId,
    string Platform,
    DateTimeOffset CreatedAt,
    int Revision);

/// <summary>
/// The result of creating the managed employee and binding from an approved
/// hire. <see cref="Created"/> is false when the approval already carried links
/// and the exact existing identities are returned instead (idempotent replay).
/// </summary>
public sealed record ManagedEmployeeCreation(
    string HireRequestId,
    string EmployeeId,
    string RuntimeBindingId,
    string Slug,
    string DisplayName,
    string Placement,
    bool Created,
    string ApprovedProfileRevisionId,
    string ApprovedProfileBuildId,
    string ApprovedImageDigest,
    string ApprovedHostId,
    string Platform,
    int CpuLimit,
    int MemoryLimitMiB,
    int PidsLimit,
    string? WorkerId);

public sealed record HireRequestSummary(
    string Id,
    string OrganizationId,
    string? RequestedByEmployeeId,
    string RequestedByKind,
    string IdempotencyKey,
    string RequestedDisplayName,
    string Purpose,
    string DepartmentId,
    string DepartmentDisplayName,
    string RoleId,
    string RoleDisplayName,
    string Placement,
    int CpuLimit,
    int MemoryLimitMiB,
    int PidsLimit,
    string State,
    string RequestVersionHash,
    string? ApprovedRequestVersion,
    string? OwnerApproval,
    int Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ContainerProfileRevisionId = null,
    string? StatusDetail = null,
    string? ProfileBuildId = null,
    string? ApprovedImageDigest = null,
    string? ApprovedHostId = null,
    string? EmployeeId = null,
    string? RuntimeBindingId = null,
    string? WorkerId = null);

public static class ContainerProfileStatuses
{
    public const string Active = "active";
    public const string Retired = "retired";
}

/// <summary>Build state of one immutable profile revision. Only #259 moves a revision past <see cref="Unbuilt"/>.</summary>
public static class ContainerProfileBuildStatuses
{
    public const string Unbuilt = "unbuilt";
    public const string Building = "building";
    public const string Built = "built";
    public const string Failed = "failed";
    public const string Rejected = "rejected";
}

/// <summary>The seeded first profile: the approved worker base plus the standard toolchain, no project content.</summary>
public static class ContainerProfileSeed
{
    public const string GenericEmployeeSlug = "generic-employee";
    public const string GenericEmployeeDisplayName = "Generic employee";
    public const string GenericEmployeeDescription = "Approved worker base image with the standard development toolchain. No project-specific content; the first profile every managed employee is built from.";
    public const string GenericEmployeeDefinition =
        """
        {
          "name": "Generic employee",
          "image": "agentcontrol-worker-base",
          "features": {
            "ghcr.io/devcontainers/features/dotnet:2": { "version": "10.0" },
            "ghcr.io/devcontainers/features/node:1": { "version": "22" },
            "ghcr.io/devcontainers/features/python:1": { "version": "3.12" },
            "ghcr.io/devcontainers/features/github-cli:1": {}
          },
          "containerEnv": {
            "DOTNET_ROOT": "/opt/dotnet-sdk",
            "PATH": "/opt/dotnet-sdk:/usr/local/bin:/usr/bin:/bin",
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
            "DOTNET_NOLOGO": "1",
            "NPM_CONFIG_UPDATE_NOTIFIER": "false"
          },
          "customizations": {
            "agentcontrol": {
              "summary": "Standard .NET 10, Node 22, Python 3.12 and GitHub CLI toolchain on the approved worker base.",
              "tags": ["generic", "development"]
            }
          }
        }
        """;
}

/// <summary>Fixed state machine of one profile image build on one host.</summary>
public static class ProfileBuildStates
{
    public const string Queued = "queued";
    public const string Building = "building";
    public const string Verifying = "verifying";
    public const string Built = "built";
    public const string Failed = "failed";
    public const string Rejected = "rejected";
    public const string Uncertain = "uncertain";
    public const string Removed = "removed";

    /// <summary>States that hold the per-(revision, host) live slot.</summary>
    public static bool IsLive(string state) => state is Queued or Building or Verifying or Uncertain;

    public static bool CanTransition(string from, string to) => (from, to) switch
    {
        (Queued, Building) => true,
        (Queued, Failed) => true,
        (Building, Verifying) => true,
        (Building, Failed) => true,
        (Building, Uncertain) => true,
        (Verifying, Built) => true,
        (Verifying, Rejected) => true,
        (Verifying, Failed) => true,
        (Verifying, Uncertain) => true,
        // Reconciliation after a lost result: the image is either found by its labels
        // (and re-verified) or provably absent.
        (Uncertain, Verifying) => true,
        (Uncertain, Failed) => true,
        // Explicit scoped cleanup of a non-verified image (#261 exposes it).
        (Failed, Removed) => true,
        (Rejected, Removed) => true,
        _ => false,
    };
}

public sealed record ProfileBuildRecord(
    string Id,
    string ProfileRevisionId,
    string HostId,
    string BaseImageDigest,
    string Platform,
    string ContextHash,
    string ResultTag,
    string State,
    string? ImageDigest,
    bool Verified,
    string? FailureSummary,
    string? EvidenceHash,
    string RequestedBy,
    int Revision,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ProfileBuildRequest(string HostId);

/// <summary>Fixed state machine of one durable employee-rebuild operation.</summary>
public static class EmployeeRebuildStates
{
    public const string Intent = "Intent";
    public const string Holding = "Holding";
    public const string Replacing = "Replacing";
    public const string Verifying = "Verifying";
    public const string Applied = "Applied";
    public const string Uncertain = "Uncertain";
    public const string Failed = "Failed";

    public static bool IsDefined(string state) =>
        state is Intent or Holding or Replacing or Verifying or Applied or Uncertain or Failed;

    /// <summary>States that hold the single active-rebuild slot for a worker.</summary>
    public static bool IsActive(string state) => state is Intent or Holding or Replacing or Verifying or Uncertain;

    public static bool CanTransition(string from, string to) => (from, to) switch
    {
        (Intent, Holding) => true,
        (Intent, Failed) => true,
        (Holding, Replacing) => true,
        (Holding, Failed) => true,
        (Holding, Uncertain) => true,
        (Replacing, Verifying) => true,
        (Replacing, Failed) => true,
        (Replacing, Uncertain) => true,
        (Verifying, Applied) => true,
        (Verifying, Failed) => true,
        (Verifying, Uncertain) => true,
        // Reconciliation after a lost result. Uncertain -> Applied is allowed only
        // when the controller proves the target digest is the one now running; the
        // caller owns that evidence, and the Applied transition requires it.
        (Uncertain, Replacing) => true,
        (Uncertain, Applied) => true,
        (Uncertain, Failed) => true,
        _ => false,
    };
}

/// <summary>
/// The frozen intent to rebuild one managed worker. <c>dispatch_hold_reason</c>
/// is fixed to the existing manual hold and <c>requested_by</c> to the owner, so
/// neither is caller-supplied.
/// </summary>
public sealed record EmployeeRebuildCreate(
    string EmployeeId,
    string RuntimeBindingId,
    string WorkerId,
    string HostId,
    string FromProfileRevisionId,
    string FromImageDigest,
    int FromRevisionNumber,
    string ToProfileRevisionId,
    string ToProfileBuildId,
    string ToImageDigest,
    string ToPlatform,
    bool ResetWorkspace,
    bool ResetHome,
    string? ResetConfirmation,
    long OwnershipEpochBefore,
    bool HoldPreexisting = false);

/// <summary>
/// Owner-facing profile status for one managed employee: the profile revision the
/// enrollment is currently frozen to, its image digest, whether the profile has a
/// newer revision with a verified build ready on the employee's host, and the
/// active rebuild operation when one exists. It is a read-only projection over
/// the immutable approval, profile and worker records; it mutates nothing. A
/// non-managed (or base) employee has no managed enrollment resources and yields
/// null from the store helper.
/// </summary>
public sealed record EmployeeRebuildRequest(
    int ExpectedRevision,
    string? TargetProfileRevisionId,
    bool ResetWorkspace = false,
    bool ResetHome = false,
    string? ResetConfirmation = null);

public sealed record EmployeeRebuildResponse(
    EmployeeRebuildRecord Rebuild,
    EmployeeProfileStatusDetail ProfileStatus);

public sealed record EmployeeProfileStatus(
    string EmployeeId,
    string RuntimeBindingId,
    string? WorkerId,
    string HostId,
    string CurrentProfileRevisionId,
    int CurrentRevisionNumber,
    string CurrentProfileId,
    string CurrentProfileDisplayName,
    string CurrentImageDigest,
    string CurrentPlatform,
    bool NewerRevisionAvailable,
    string? NewerRevisionId,
    int? NewerRevisionNumber,
    string? NewerVerifiedBuildId,
    string? NewerVerifiedImageDigest,
    EmployeeRebuildRecord? ActiveRebuild);

public sealed record EmployeeRebuildRecord(
    string Id,
    string EmployeeId,
    string RuntimeBindingId,
    string WorkerId,
    string HostId,
    string FromProfileRevisionId,
    string FromImageDigest,
    int FromRevisionNumber,
    string ToProfileRevisionId,
    string ToProfileBuildId,
    string ToImageDigest,
    string ToPlatform,
    bool ResetWorkspace,
    bool ResetHome,
    string? ResetConfirmation,
    bool HoldPreexisting,
    string State,
    long OwnershipEpochBefore,
    long? OwnershipEpochAfter,
    string DispatchHoldReason,
    string RequestedBy,
    string? EvidenceHash,
    string? FailureSummary,
    int Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);


public sealed record ContainerProfileCreate(
    string? IdempotencyKey,
    string? Slug,
    string? DisplayName,
    string? Description,
    string? Definition,
    string? DockerfileFragment);

public sealed record ContainerProfileRevisionCreate(
    int ExpectedProfileRevision,
    string? Definition,
    string? DockerfileFragment);

public sealed record ContainerProfileRetire(int ExpectedRevision);

public sealed record ContainerProfileSummary(
    string Id,
    string OrganizationId,
    string Slug,
    string DisplayName,
    string Description,
    string Status,
    int CurrentRevisionNumber,
    string CurrentRevisionId,
    string CurrentContentHash,
    string CurrentBuildStatus,
    int Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ContainerProfileRevisionSummary(
    string Id,
    string ProfileId,
    int RevisionNumber,
    string BaseImageReference,
    string Definition,
    string? DockerfileFragment,
    string ContentHash,
    string BuildStatus,
    string? BuiltImageDigest,
    bool Verified,
    string CreatedBy,
    DateTimeOffset CreatedAt);

public sealed record ContainerProfileDetail(
    ContainerProfileSummary Profile,
    IReadOnlyList<ContainerProfileRevisionSummary> Revisions);

/// <summary>
/// A department, its current employee count and its authoritative revision. The
/// active department orientation fragment, when one exists, is surfaced as
/// standing instructions; departments without a persisted fragment report null
/// rather than fabricated text.
/// </summary>
public sealed record DepartmentSummary(
    string Id,
    string Slug,
    string DisplayName,
    int EmployeeCount,
    int Revision,
    string? StandingInstructions);

/// <summary>A role definition with its department and versioned profile references.</summary>
public sealed record RoleSummary(
    string Id,
    string DepartmentId,
    string Slug,
    string DisplayName,
    string InstructionProfile,
    string PermissionProfile,
    string StandingInstructions,
    int Revision);

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
    string? SessionRecordId,
    string? NativeSessionId,
    string? SessionTitle,
    int Revision,
    OrientationStatus? Orientation = null)
{
    public EmployeeSummary(string id, string slug, string displayName, string purpose, string instructions, string rules, string restrictions, string organizationId, string departmentId, string departmentSlug, string departmentDisplayName, string roleId, string roleSlug, string roleDisplayName, string runtimeBindingId, string placement, string? sessionRecordId, string? nativeSessionId, string? sessionTitle, OrientationStatus? orientation)
        : this(id, slug, displayName, purpose, instructions, rules, restrictions, organizationId, departmentId, departmentSlug, departmentDisplayName, roleId, roleSlug, roleDisplayName, runtimeBindingId, placement, sessionRecordId, nativeSessionId, sessionTitle, 1, orientation) { }

    public EmployeeSummary(string id, string slug, string displayName, string purpose, string instructions, string rules, string restrictions, string organizationId, string departmentId, string departmentSlug, string departmentDisplayName, string roleId, string roleSlug, string roleDisplayName, string runtimeBindingId, string placement, string? sessionId, string? sessionTitle, OrientationStatus? orientation = null)
        : this(id, slug, displayName, purpose, instructions, rules, restrictions, organizationId, departmentId, departmentSlug, departmentDisplayName, roleId, roleSlug, roleDisplayName, runtimeBindingId, placement, sessionId, sessionId, sessionTitle, 1, orientation) { }

    public EmployeeSummary(string id, string slug, string displayName, string purpose, string instructions, string rules, string restrictions, string organizationId, string departmentId, string departmentSlug, string departmentDisplayName, string roleId, string roleSlug, string roleDisplayName, string runtimeBindingId, string placement, string? sessionId, string? sessionTitle, int revision, OrientationStatus? orientation = null)
        : this(id, slug, displayName, purpose, instructions, rules, restrictions, organizationId, departmentId, departmentSlug, departmentDisplayName, roleId, roleSlug, roleDisplayName, runtimeBindingId, placement, sessionId, sessionId, sessionTitle, revision, orientation) { }

    // Backward-compatible API alias. SessionId has always meant the ACP-native
    // identity on the wire; database relationships must use SessionRecordId.
    public string? SessionId => NativeSessionId;
}

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
