using System.Text.Json.Serialization;

namespace HVO.AgentControl.Core;

public static class HostExecutorState
{
    public const string Pending = "Pending", Active = "Active", Suspended = "Suspended", Revoked = "Revoked";
}

public static class HostObservationState
{
    public const string Complete = "Complete", Partial = "Partial", Unsupported = "Unsupported";
}

public static class HostReservationState
{
    public const string Held = "Held", EffectCommitted = "EffectCommitted", Unknown = "Unknown", Released = "Released", Revoked = "Revoked";
    public static bool HoldsCapacity(string state) => state is Held or EffectCommitted or Unknown;
}

public sealed class HostExecutorEnrollment
{
    public string Id { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string HostId { get; set; } = "";
    public string EndpointId { get; set; } = "";
    public string PhysicalHostId { get; set; } = "";
    public string EngineId { get; set; } = "";
    public string BuilderId { get; set; } = "";
    public string CliBundleDigest { get; set; } = "";
    public string ProtectedRootDigest { get; set; } = "";
    public string AuthorityDigest { get; set; } = "";
    [JsonIgnore] public string CredentialDigest { get; set; } = "";
    public string State { get; set; } = HostExecutorState.Pending;
    public int AuthorityGeneration { get; set; } = 1;
    public string BootId { get; set; } = "";
    public string IncarnationId { get; set; } = "";
    public long LastSequence { get; set; }
    public long CreatedAt { get; set; }
    public long? ActivatedAt { get; set; }
    public long? LastSeenAt { get; set; }
    public long Revision { get; set; } = 1;
}

public sealed class HostResourcePolicy
{
    public string Id { get; set; } = "";
    public long Revision { get; set; }
    public string RequestId { get; set; } = "";
    public string EnrollmentId { get; set; } = "";
    public string PhysicalHostId { get; set; } = "";
    public string State { get; set; } = "Active";
    public long MaxCpuMillis { get; set; }
    public long MaxMemoryBytes { get; set; }
    public long MaxDiskBytes { get; set; }
    public int BuildSlots { get; set; }
    public long ControllerReserveCpuMillis { get; set; }
    public long ControllerReserveMemoryBytes { get; set; }
    public long ControllerReserveDiskBytes { get; set; }
    public int ObservationMaxAgeSeconds { get; set; }
    public int GrantLifetimeSeconds { get; set; }
    public long CreatedAt { get; set; }
}

public sealed class HostResourceObservation
{
    public string Id { get; set; } = "";
    public string EnrollmentId { get; set; } = "";
    public string HostId { get; set; } = "";
    public string EndpointId { get; set; } = "";
    public string PhysicalHostId { get; set; } = "";
    public string EngineId { get; set; } = "";
    public string BuilderId { get; set; } = "";
    public int AuthorityGeneration { get; set; }
    public string BootId { get; set; } = "";
    public string IncarnationId { get; set; } = "";
    public long Sequence { get; set; }
    public long PhysicalRevision { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public string State { get; set; } = HostObservationState.Partial;
    public long CollectedFrom { get; set; }
    public long CollectedTo { get; set; }
    public long ReceivedAt { get; set; }
    public string Architecture { get; set; } = "";
    public long? EffectiveCpuMillis { get; set; }
    public long? AvailableCpuMillis { get; set; }
    public long? EffectiveMemoryBytes { get; set; }
    public long? AvailableMemoryBytes { get; set; }
    public long? SwapUsedBytes { get; set; }
    public long? SwapLimitBytes { get; set; }
    public long? NativeSessionBytes { get; set; }
    public long? CompilerPeakBytes { get; set; }
    public long? MemoryPressureEvents { get; set; }
    public string WorkspaceId { get; set; } = "";
    public string CanonicalWorkspaceIdentity { get; set; } = "";
    public string WorkspaceFilesystemId { get; set; } = "";
    public long? WorkspaceAvailableBytes { get; set; }
    public long? WorkspaceAvailableInodes { get; set; }
    public string? DockerFilesystemId { get; set; }
    public long? DockerAvailableBytes { get; set; }
    public long? DockerAvailableInodes { get; set; }
    public bool? DockerAvailable { get; set; }
    public int? BuildSlots { get; set; }
    public string ExternalOwnershipState { get; set; } = "Unknown";
    public string? ExternalOwnershipIntentDigest { get; set; }
    public string EvidenceDigest { get; set; } = "";
}

public sealed class HostResourceReservation
{
    public string Id { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string IntentDigest { get; set; } = "";
    public string HostId { get; set; } = "";
    public string PhysicalHostId { get; set; } = "";
    public string EndpointId { get; set; } = "";
    public string EnrollmentId { get; set; } = "";
    public int AuthorityGeneration { get; set; }
    public string IncarnationId { get; set; } = "";
    public string PolicyId { get; set; } = "";
    public long PolicyRevision { get; set; }
    public string ObservationId { get; set; } = "";
    public string WorkspaceId { get; set; } = "";
    public string CanonicalWorkspaceIdentity { get; set; } = "";
    public string WorkspaceFilesystemId { get; set; } = "";
    public string? DockerFilesystemId { get; set; }
    public string OperationId { get; set; } = "";
    public string Kind { get; set; } = "Runtime";
    public long CpuMillis { get; set; }
    public long MemoryBytes { get; set; }
    public long DiskBytes { get; set; }
    public int BuildSlots { get; set; }
    public int? Port { get; set; }
    public string? SharedResourceKey { get; set; }
    public string State { get; set; } = HostReservationState.Held;
    public long GrantGeneration { get; set; } = 1;
    public long GrantedAt { get; set; }
    public long GrantExpiresAt { get; set; }
    public long? EffectCommittedAt { get; set; }
    public long? ReleasedAt { get; set; }
    public string? ReleaseObservationId { get; set; }
    public string ReleaseEvidence { get; set; } = "";
    public long Revision { get; set; } = 1;
}

public sealed class HostResourceMutationReceipt
{
    public string RequestId { get; set; } = "";
    public string Action { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string ResultJson { get; set; } = "{}";
    public long CreatedAt { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateHostExecutorInput(string RequestId, string Id, string HostId, long ExpectedHostRevision,
    string EndpointId, string PhysicalHostId, string EngineId, string BuilderId, string CliBundleDigest,
    string ProtectedRootDigest, string AuthorityDigest, string CredentialDigest);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ActivateHostExecutorInput(string EnrollmentId, string HostId, int AuthorityGeneration,
    string AuthorityDigest, string BootId, string IncarnationId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RotateHostExecutorInput(string RequestId, long ExpectedRevision, string AuthorityDigest, string CredentialDigest);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChangeHostExecutorStateInput(string RequestId, long ExpectedRevision);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConfigureHostResourcePolicyInput(string RequestId, string Id, string EnrollmentId, long ExpectedRevision,
    long MaxCpuMillis, long MaxMemoryBytes, long MaxDiskBytes, int BuildSlots,
    long ControllerReserveCpuMillis, long ControllerReserveMemoryBytes, long ControllerReserveDiskBytes,
    int ObservationMaxAgeSeconds = 120, int GrantLifetimeSeconds = 300, bool Active = true);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubmitHostResourceObservationInput(string Id, string EnrollmentId, string HostId, string EndpointId,
    string PhysicalHostId, string EngineId, string BuilderId, int AuthorityGeneration, string BootId, string IncarnationId,
    long Sequence, int SchemaVersion, string State, long CollectedFrom, long CollectedTo, string Architecture,
    long? EffectiveCpuMillis, long? AvailableCpuMillis, long? EffectiveMemoryBytes, long? AvailableMemoryBytes,
    long? SwapUsedBytes, long? SwapLimitBytes, long? NativeSessionBytes, long? CompilerPeakBytes, long? MemoryPressureEvents,
    string WorkspaceId, string CanonicalWorkspaceIdentity, string WorkspaceFilesystemId,
    long? WorkspaceAvailableBytes, long? WorkspaceAvailableInodes, string? DockerFilesystemId,
    long? DockerAvailableBytes, long? DockerAvailableInodes, bool? DockerAvailable, int? BuildSlots,
    string ExternalOwnershipState = "Unknown", string? ExternalOwnershipIntentDigest = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AcquireHostResourceReservationInput(string RequestId, string Id, string IntentDigest,
    string HostId, string EnrollmentId, string PolicyId, long PolicyRevision, string ObservationId,
    string WorkspaceId, string OperationId, string Kind, long CpuMillis, long MemoryBytes, long DiskBytes,
    int BuildSlots = 0, int? Port = null, string? SharedResourceKey = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BeginHostResourceEffectInput(long ExpectedRevision, string IntentDigest, string ObservationId);
public sealed record HostResourceEffectDecision(HostResourceReservation Reservation, bool AuthorizedNow);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MarkHostResourceUnknownInput(string RequestId, long ExpectedRevision, string IntentDigest, string Detail);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReleaseHostResourceReservationInput(string RequestId, long ExpectedRevision,
    string Evidence, string? ObservationId = null);

public sealed record HostExecutorPrincipal(string EnrollmentId, string HostId, int AuthorityGeneration, string State,
    string PresentedCredentialDigest);
public sealed record HostCapacityView(HostExecutorEnrollment Executor, HostResourcePolicy? Policy,
    HostResourceObservation? Observation, List<HostResourceReservation> Reservations);
