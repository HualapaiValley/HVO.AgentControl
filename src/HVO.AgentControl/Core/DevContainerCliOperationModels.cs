namespace HVO.AgentControl.Core;

// This is an intent and evidence contract, not lifecycle authority or a command to execute.
public sealed record DevContainerCliOperationRequest(
    string RequestId,
    HostRecord Host,
    ProjectRecord Project,
    RuntimeEnvironmentView Environment,
    string CliVersion = "0.89.0");

public sealed record DevContainerCliEvidence(
    string HostId,
    string ProjectId,
    string RuntimeId,
    string ConfigurationProjectId,
    string DevcontainerPath,
    string CliVersion,
    IReadOnlyDictionary<string, string> Labels);

public sealed record DevContainerCliObservation(
    string? ContainerId,
    IReadOnlyDictionary<string, string> Labels,
    string Output = "");

public sealed record DevContainerCliOperationResult(
    string RequestId,
    string Status,
    string Receipt,
    DevContainerCliEvidence? Requested,
    DevContainerCliEvidence? Resolved,
    DevContainerCliObservation? Observed,
    string Output);
