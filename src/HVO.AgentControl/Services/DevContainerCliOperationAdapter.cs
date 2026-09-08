using System.Security.Cryptography;
using System.Text;
using HVO.AgentControl.Core;

namespace HVO.AgentControl.Services;

public interface IDevContainerCliOperationAdapter
{
    DevContainerCliOperationResult Execute(DevContainerCliOperationRequest request);
    DevContainerCliOperationResult Reconcile(DevContainerCliOperationRequest request, IReadOnlyList<DevContainerCliObservation> observations);
}

// Deliberately non-executing: host authority will be supplied by a later durable lifecycle service.
public sealed class DevContainerCliOperationAdapter : IDevContainerCliOperationAdapter
{
    private const int MaxOutputLength = 4096;
    private const int MaxObservations = 100;
    private readonly Dictionary<string, string> requestIntents = new(StringComparer.Ordinal);
    private readonly Lock requestIntentLock = new();

    public DevContainerCliOperationResult Execute(DevContainerCliOperationRequest request)
    {
        var requested = Requested(request);
        var resolved = Resolve(request, out var error);
        if (resolved is not null && !RememberIntent(request, resolved.IntentDigest)) error = "request_intent_conflict";
        return error is not null
            ? Result(request.RequestId, "Failed", error, requested, null, null, error)
            : Result(request.RequestId, "Unsupported", "missing_provisioner_authority", requested, resolved, null,
                "No provisioner authority is configured; the Dev Container CLI was not invoked.");
    }

    public DevContainerCliOperationResult Reconcile(DevContainerCliOperationRequest request, IReadOnlyList<DevContainerCliObservation> observations)
    {
        var requested = Requested(request);
        var resolved = Resolve(request, out var error);
        if (resolved is not null && !RememberIntent(request, resolved.IntentDigest)) error = "request_intent_conflict";
        if (error is not null) return Result(request.RequestId, "Failed", error, requested, null, null, error);
        if (observations.Count > MaxObservations) return Result(request.RequestId, "Failed", "observation_limit_exceeded", requested, resolved, null, "Too many host observations were supplied.");

        var matches = observations.Where(observation =>
            resolved!.Labels.All(pair => observation.Labels.TryGetValue(pair.Key, out var value) && value == pair.Value)).ToList();
        if (matches.Count == 0) return Result(request.RequestId, "Uncertain", "labels_not_observed", requested, resolved, null, "No exact ownership-label observation was supplied.");
        if (matches.Count > 1) return Result(request.RequestId, "Uncertain", "multiple_labels_observed", requested, resolved, null, "Multiple exact ownership-label observations were supplied.");
        if (string.IsNullOrWhiteSpace(matches[0].ContainerId)) return Result(request.RequestId, "Uncertain", "container_identity_missing", requested, resolved, null, "The exact ownership-label observation has no container identity.");

        return Result(request.RequestId, "Observed", "labels_observed", requested, resolved, matches[0], matches[0].Output);
    }

    private static DevContainerCliEvidence Requested(DevContainerCliOperationRequest request) => new(
        request.Host.Id, request.Project.Id, request.Environment.RuntimeId, request.Environment.ConfigurationProjectId ?? "",
        request.Environment.DevcontainerPath ?? "", request.CliVersion, IntentDigest(request), new Dictionary<string, string>(StringComparer.Ordinal));

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
            ["hvo.agentcontrol.configuration-project-id"] = request.Project.Id,
            ["hvo.agentcontrol.intent-digest"] = IntentDigest(request)
        };
        return new(request.Host.Id, request.Project.Id, request.Environment.RuntimeId, request.Project.Id,
            request.Environment.DevcontainerPath!, request.CliVersion, IntentDigest(request), labels);
    }

    private bool RememberIntent(DevContainerCliOperationRequest request, string digest)
    {
        lock (requestIntentLock)
        {
            if (requestIntents.TryGetValue(request.RequestId, out var prior)) return prior == digest;
            requestIntents.Add(request.RequestId, digest);
            return true;
        }
    }

    private static string IntentDigest(DevContainerCliOperationRequest request) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Write(new
    {
        HostId = request.Host.Id,
        ProjectId = request.Project.Id,
        RuntimeId = request.Environment.RuntimeId,
        ConfigurationProjectId = request.Environment.ConfigurationProjectId,
        DevcontainerPath = request.Environment.DevcontainerPath,
        CliVersion = request.CliVersion
    }))));

    private static DevContainerCliOperationResult Result(string requestId, string status, string receipt,
        DevContainerCliEvidence requested, DevContainerCliEvidence? resolved, DevContainerCliObservation? observed, string output) =>
        new(requestId, status, receipt, requested, resolved, observed, resolved?.IntentDigest ?? requested.IntentDigest, Bound(output));

    private static string Bound(string output) => output.Length <= MaxOutputLength ? output : output[..MaxOutputLength];
}
