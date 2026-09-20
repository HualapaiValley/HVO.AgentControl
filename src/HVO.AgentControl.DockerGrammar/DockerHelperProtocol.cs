using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.AgentControl.DockerHelper.Protocol;

public static class DockerHelperProtocol
{
    public const string DefaultSocketPath = "/run/agentcontrol-docker-helper/helper.sock";
    public const int MaxOutputBytes = 1024 * 1024;
    public const int MaxBinaryBytes = 64 * 1024 * 1024;
    public const int MaxTimeoutSeconds = 1800;
    /// <summary>Bound on how long an accepted connection may take to deliver its request envelope.</summary>
    public const int EnvelopeTimeoutSeconds = 30;
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false, MaxDepth = 64, Converters = { new JsonStringEnumConverter() } };
}

public sealed record DockerHelperRequest(
    string Type,
    string Id,
    HVO.AgentControl.RemoteWorker.DockerOperation Operation,
    IReadOnlyList<string>? Tokens = null,
    HVO.AgentControl.RemoteWorker.VolumeCreateSpec? VolumeCreate = null,
    HVO.AgentControl.RemoteWorker.ContainerCreateSpec? ContainerCreate = null,
    HVO.AgentControl.RemoteWorker.BootstrapSpec? Bootstrap = null,
    HVO.AgentControl.RemoteWorker.ImageBuildSpec? ImageBuild = null,
    HVO.AgentControl.RemoteWorker.WorkspaceVerifySpec? WorkspaceVerify = null,
    int BinaryLength = 0,
    int TimeoutSeconds = 60);

public sealed record DockerHelperResult(string Type, string Id, int ExitCode, string Stdout, string ErrorCategory);
public sealed record DockerHelperError(string Type, string Id, string Error);
public sealed record DockerHelperStreamOpen(string Type, string Id);
