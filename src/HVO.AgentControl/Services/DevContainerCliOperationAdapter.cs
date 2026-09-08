using HVO.AgentControl.Core;

namespace HVO.AgentControl.Services;

public interface IDevContainerCliOperationAdapter
{
    DevContainerCliOperationResult Execute(DevContainerCliOperationRequest request);
    DevContainerCliOperationResult Reconcile(DevContainerCliOperationRequest request, DevContainerCliObservation? observation);
}

// Deliberately non-executing: host authority will be supplied by a later durable lifecycle service.
public sealed class DevContainerCliOperationAdapter : IDevContainerCliOperationAdapter
{
    private const int MaxOutputLength = 4096;

    public DevContainerCliOperationResult Execute(DevContainerCliOperationRequest request)
    {
        var requested = Requested(request);
        var resolved = Resolve(request, out var error);
        return error is not null
            ? Result(request.RequestId, "Failed", error, requested, null, null, error)
            : Result(request.RequestId, "Unsupported", "missing_provisioner_authority", requested, resolved, null,
                "No provisioner authority is configured; the Dev Container CLI was not invoked.");
    }

    public DevContainerCliOperationResult Reconcile(DevContainerCliOperationRequest request, DevContainerCliObservation? observation)
    {
        var requested = Requested(request);
        var resolved = Resolve(request, out var error);
        if (error is not null) return Result(request.RequestId, "Failed", error, requested, null, observation, error);
        if (observation is null) return Result(request.RequestId, "Uncertain", "observation_missing", requested, resolved, null, "No host observation was supplied.");

        var matches = resolved!.Labels.All(pair => observation.Labels.TryGetValue(pair.Key, out var value) && value == pair.Value);
        if (!matches || string.IsNullOrWhiteSpace(observation.ContainerId))
            return Result(request.RequestId, "Uncertain", "labels_not_observed", requested, resolved, observation, observation.Output);

        return Result(request.RequestId, "Observed", "labels_observed", requested, resolved, observation, observation.Output);
    }

    private static DevContainerCliEvidence Requested(DevContainerCliOperationRequest request) => new(
        request.Host.Id, request.Project.Id, request.Environment.RuntimeId, request.Environment.ConfigurationProjectId ?? "",
        request.Environment.DevcontainerPath ?? "", request.CliVersion, new Dictionary<string, string>(StringComparer.Ordinal));

    private static DevContainerCliEvidence? Resolve(DevContainerCliOperationRequest request, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(request.RequestId)) error = "request_id_required";
        else if (request.Host.Archived || request.Project.Archived) error = "registered_identity_archived";
        else if (request.Environment.RequestedKind != RuntimeEnvironmentKind.ManagedDevcontainer) error = "unsupported_environment_kind";
        else if (request.Environment.RequestedHostId != request.Host.Id) error = "host_identity_mismatch";
        else if (request.Environment.ConfigurationProjectId != request.Project.Id) error = "configuration_project_identity_mismatch";
        else if (string.IsNullOrWhiteSpace(request.Environment.DevcontainerPath)) error = "devcontainer_path_required";
        else if (string.IsNullOrWhiteSpace(request.CliVersion)) error = "cli_version_required";
        if (error is not null) return null;

        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["hvo.agentcontrol.request-id"] = request.RequestId,
            ["hvo.agentcontrol.host-id"] = request.Host.Id,
            ["hvo.agentcontrol.project-id"] = request.Project.Id,
            ["hvo.agentcontrol.runtime-id"] = request.Environment.RuntimeId,
            ["hvo.agentcontrol.configuration-project-id"] = request.Project.Id
        };
        return new(request.Host.Id, request.Project.Id, request.Environment.RuntimeId, request.Project.Id,
            request.Environment.DevcontainerPath!, request.CliVersion, labels);
    }

    private static DevContainerCliOperationResult Result(string requestId, string status, string receipt,
        DevContainerCliEvidence requested, DevContainerCliEvidence? resolved, DevContainerCliObservation? observed, string output) =>
        new(requestId, status, receipt, requested, resolved, observed, Bound(output));

    private static string Bound(string output) => output.Length <= MaxOutputLength ? output : output[..MaxOutputLength];
}
