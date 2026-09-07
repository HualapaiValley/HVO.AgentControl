using HVO.AgentControl.Core;
using HVO.AgentControl.OpenCode;

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
    string InstalledExecutable => "";
    Task<CapabilitySnapshot> ProbeCapabilities(string directory, CancellationToken cancellationToken) =>
        Task.FromResult(CapabilityProbe.Parse("probe\tunsupported", directory));
    Task<WorkspaceIdentity> Workspace(RuntimeRecord runtime, CreateWorkerInput input, CancellationToken cancellationToken);
    Task StopOwnedServer(CancellationToken cancellationToken);
}
