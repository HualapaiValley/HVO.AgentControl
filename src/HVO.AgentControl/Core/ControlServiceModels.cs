namespace HVO.AgentControl.Core;

public static class RuntimeConnections
{
    public const string Ssh = "Ssh", ControlHttp = "ControlHttp", ManagedDraft = "ManagedDraft";
}

// The runtime and coordinator records are compatibility projections for the existing durable
// command pipeline. This service is owned by the host, never development capacity.
public sealed class ControlServiceRecord
{
    public string Id { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public string IncarnationId { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public long? LastObservedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class ControlSessionBinding
{
    public string Id { get; set; } = "";
    public string ControlServiceId { get; set; } = "";
    public string ScopeKind { get; set; } = "";
    public string ScopeId { get; set; } = "";
    public string WorkerId { get; set; } = "";
    public string NativeSessionId { get; set; } = "";
    public string CreationCommandId { get; set; } = "";
    public int Generation { get; set; }
    public string? PredecessorId { get; set; }
    public bool IsCurrent { get; set; } = true;
    public string State { get; set; } = "Queued";
    public string Detail { get; set; } = "Waiting for the control service.";
    public long Revision { get; set; }
    public string GenerationReason { get; set; } = "Initial";
    public string? RecoveryIntentId { get; set; }
    public string? RecoveryRunId { get; set; }
    public string? RecoverySourceCommandId { get; set; }
    public long? RecoveryOwnerPolicyRevision { get; set; }
    public string? RecoveryInstructionHash { get; set; }
    public long? CreationAuthorizedAt { get; set; }
    public string? ReplacementDecisionCommandId { get; set; }
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public string Title => "agentcontrol-control:" + Id + (Generation == 0 ? "" : ":g" + Generation);
}

public sealed record ControlServiceIdentity(int SchemaVersion, string InstanceId, string IncarnationId, string StartedAt, string Directory);
public sealed record RegisterControlServiceInput(string Id, string Name, string Endpoint, string ExpectedInstanceId, string PasswordReference);
public sealed record CreateControlSessionInput(string Id, string ScopeKind, string ScopeId, string Name,
    string ProviderId = "", string ModelId = "", string Variant = "", string Agent = "");
public sealed record MigrateControlSessionInput(string Id, long ExpectedRevision, string ControlSessionId);
public sealed record ControlServiceView(ControlServiceRecord Service, RuntimeRecord Connection, List<ControlSessionBinding> Sessions);

public sealed record RetryControlSessionInput(string Id, long ExpectedRevision);
public sealed record RenewControlSessionInput(string Id, long ExpectedRevision);
public sealed record ControlSessionRenewalReceipt(string Id, string State, string SuccessorId, long RecordedAt);
public sealed record ControlSessionRenewalResult(ControlSessionBinding Successor, ControlSessionRenewalReceipt Receipt);
