using HVO.AgentControl.Core;
using HVO.AgentControl.OpenCode;
using HVO.AgentControl.Telemetry;

namespace HVO.AgentControl.Ssh;

public sealed record WorkspaceIdentity(string Directory, string Branch);
public interface IRuntimeTransportFactory
{
    Task<IRuntimeTransport> Connect(RuntimeRecord runtime, CancellationToken cancellationToken);
}
public interface IRuntimeTransport : IAsyncDisposable
{
    OpenCodeClient Api { get; }
    bool Connected { get; }
    string Platform { get; }
    Task ValidateConnection(CancellationToken cancellationToken) => Task.CompletedTask;
    string InstalledExecutable => "";
    NativeProcessObservation? NativeProcess => null;
    // Unsupported transports fail closed; Connected and cached NativeProcess are
    // deliberately not substitutes for a fresh incarnation observation.
    Task<RuntimeProcessIdentity?> ProbeProcessIdentity(CancellationToken cancellationToken) => Task.FromResult<RuntimeProcessIdentity?>(null);
    Task<CapabilitySnapshot> ProbeCapabilities(string directory, CancellationToken cancellationToken) =>
        Task.FromResult(CapabilityProbe.Parse("probe\tunsupported", directory));
    Task<RuntimeTelemetrySample?> SampleTelemetry(string identity, CancellationToken cancellationToken) =>
        Task.FromResult<RuntimeTelemetrySample?>(null);
    Task<WorkspaceIdentity> Workspace(RuntimeRecord runtime, CreateWorkerInput input, CancellationToken cancellationToken);
    Task StopOwnedServer(CancellationToken cancellationToken);
    Task StopOwnedServer(OwnedNativeProcess expected, CancellationToken cancellationToken) => StopOwnedServer(cancellationToken);
}
