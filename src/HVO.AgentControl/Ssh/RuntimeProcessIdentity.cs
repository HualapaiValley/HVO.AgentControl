using HVO.AgentControl.Core;

namespace HVO.AgentControl.Ssh;

// An observation made by the transport's live probe, never a cached bootstrap or
// health/version receipt. HTTP sidecars have an instance/incarnation, not a PID.
public sealed record RuntimeProcessIdentity(string ConnectionKind, string OwnerId, string Incarnation,
    int? ProcessId, long ObservedAt)
{
    public bool MatchesOwner(RuntimeRecord runtime) => ConnectionKind == runtime.ConnectionKind && OwnerId == runtime.ManagedServerId &&
        !string.IsNullOrWhiteSpace(OwnerId) && NativeProcessProbe.ValidMarker(Incarnation) &&
        (ConnectionKind == RuntimeConnections.Ssh && ProcessId > 0 ||
         ConnectionKind == RuntimeConnections.ControlHttp && ProcessId is null && Guid.TryParse(Incarnation, out _));

    public bool SameProcess(RuntimeProcessIdentity other) => ConnectionKind == other.ConnectionKind && OwnerId == other.OwnerId &&
        ProcessId == other.ProcessId && Incarnation == other.Incarnation;
}
