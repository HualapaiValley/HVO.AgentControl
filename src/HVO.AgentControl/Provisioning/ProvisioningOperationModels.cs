using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace HVO.AgentControl.Provisioning;

public static class ProvisionOperationState
{
    public const string AwaitingHostAuthority = "AwaitingHostAuthority";
    public const string AwaitingCapacity = "AwaitingCapacity";
    public const string AwaitingExecution = "AwaitingExecution";
    public const string Unknown = "Unknown";
    public const string AwaitingEnrollment = "AwaitingEnrollment";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}

public sealed class ProvisionOperationRecord
{
    public long Sequence { get; set; }
    public string Id { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string RequestedJson { get; set; } = "{}";
    public string ApprovedIntentJson { get; set; } = "";
    public string IntentDigest { get; set; } = "";
    public string HostId { get; set; } = "";
    public long HostRevision { get; set; }
    public string RuntimeId { get; set; } = "";
    public long RuntimeRevision { get; set; }
    public long EnvironmentRevision { get; set; }
    public string ProjectId { get; set; } = "";
    public long ProjectRevision { get; set; }
    public string WorkspaceId { get; set; } = "";
    public string SourceRevision { get; set; } = "";
    public string ConfigurationPath { get; set; } = "";
    public string ConfigurationSha256 { get; set; } = "";
    public string ApprovedWorkspaceIdentity { get; set; } = "";
    public bool ColdBuild { get; set; }
    public long RequestedBuildCpuMillis { get; set; }
    public long RequestedBuildMemoryBytes { get; set; }
    public long RequestedRuntimeCpuMillis { get; set; }
    public long RequestedRuntimeMemoryBytes { get; set; }
    public long? AuthorityRevision { get; set; }
    public string CapacityReservationId { get; set; } = "";
    public long? CapacityRevision { get; set; }
    public long? CapacityValidUntil { get; set; }
    public long? ReservedBuildCpuMillis { get; set; }
    public long? ReservedBuildMemoryBytes { get; set; }
    public long? ReservedRuntimeCpuMillis { get; set; }
    public long? ReservedRuntimeMemoryBytes { get; set; }
    public string State { get; set; } = ProvisionOperationState.AwaitingHostAuthority;
    public string Code { get; set; } = "trusted_host_executor_required";
    public bool CancelRequested { get; set; }
    public bool ReconcileRequested { get; set; }
    public bool EffectStarted { get; set; }
    public string ProgressJson { get; set; } = "[]";
    public string ResultJson { get; set; } = "";
    public string ObservedContainerId { get; set; } = "";
    public string ObservedImageId { get; set; } = "";
    public long Revision { get; set; } = 1;
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

public sealed class ProvisionAttemptRecord
{
    public string OperationId { get; set; } = "";
    public string IntentDigest { get; set; } = "";
    public string IntentJson { get; set; } = "";
    public string HostId { get; set; } = "";
    public string WorkspaceId { get; set; } = "";
    public string WorkspaceIdentity { get; set; } = "";
    public long AuthorityRevision { get; set; }
    public string CapacityReservationId { get; set; } = "";
    public long? CapacityRevision { get; set; }
    public string CapacityFingerprint { get; set; } = "";
    public long Revision { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

public sealed class ProvisionEffectRecord
{
    public string OperationId { get; set; } = "";
    public string Effect { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public long StartedAt { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateProvisionOperationInput(string RequestId, string HostId, long ExpectedHostRevision,
    string RuntimeId, long ExpectedRuntimeRevision, long ExpectedEnvironmentRevision,
    string ProjectId, long ExpectedProjectRevision, string WorkspaceId, string SourceRevision,
    string ConfigurationPath, string ConfigurationSha256, long RequestedBuildCpuMillis,
    long RequestedBuildMemoryBytes, long RequestedRuntimeCpuMillis, long RequestedRuntimeMemoryBytes,
    bool ColdBuild = false);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProvisionOperationControlInput(long ExpectedRevision);

public sealed record ProvisionEffectView(string Effect, string ResourceId, long StartedAt);
public sealed record ProvisionOperationView(long Sequence, string Id, string HostId, long HostRevision,
    string RuntimeId, long RuntimeRevision, long EnvironmentRevision, string ProjectId, long ProjectRevision,
    string WorkspaceId, string SourceRevision, string ConfigurationPath, string ConfigurationSha256,
    long RequestedBuildCpuMillis, long RequestedBuildMemoryBytes, long RequestedRuntimeCpuMillis,
    long RequestedRuntimeMemoryBytes, bool ColdBuild, long? AuthorityRevision, string? IntentDigest,
    string? CapacityReservationId, long? CapacityRevision, long? CapacityValidUntil,
    string State, string Code, bool CancelRequested, bool ReconcileRequested,
    bool EffectStarted, long Revision, long CreatedAt, long UpdatedAt, IReadOnlyList<ProvisionProgress> Progress,
    IReadOnlyList<ProvisionEffectView> Effects, string? ObservedContainerId, string? ObservedImageId);
public sealed record ProvisionOperationPage(List<ProvisionOperationView> Items, long? NextAfter);

// Trusted executor-only evidence. Never bind this type from an HTTP request.
internal sealed record ProvisionCapacityGrant(string ReservationId, long Revision, long ValidUntil,
    long BuildCpuMillis, long BuildMemoryBytes, long RuntimeCpuMillis, long RuntimeMemoryBytes);

internal sealed record ProvisionResultReceipt(string OperationId, string State, string Code, bool EffectStarted,
    string? IntentDigest, ProvisionContainerObservation? Observed, ProvisionExecutionEvidence? Executed,
    ImmutableArray<string> RetainedResources);

public sealed class ProvisionAdmissionException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
