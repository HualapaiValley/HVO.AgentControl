using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Organization;
using HVO.AgentControl.Runtime;
using Microsoft.Extensions.Options;

namespace HVO.AgentControl.RemoteWorker;

/// <summary>
/// Turns one immutable profile revision into a verified image on one approved
/// host. Every step is durable and intent-first: the build row is queued before
/// anything touches the host, each transition is recorded before the next
/// command runs, and a lost result leaves the row <c>uncertain</c> to be
/// reconciled by the image labels rather than retried blindly.
/// </summary>
/// <remarks>
/// Sequence: <c>ImageTag</c> pins the configured base digest under the
/// controller-owned tag the generated Dockerfile names; <c>ImageBuild</c> streams
/// the deterministic tar context to <c>docker build -</c>; <c>ImageInspect</c>
/// reads the result's id, labels and root filesystem; <c>ImageVerify</c> runs the
/// fixed contract program inside the image with no network and no capabilities.
/// Only when the inspect proves the labels, the base-layer prefix and the
/// platform, and the verify output proves the worker contract, does the build
/// become <c>built</c>/verified and its digest join the host's approved set.
/// </remarks>
public sealed class ProfileBuildCoordinator(AcpControlHost control, IRemoteWorkerOperations operations, IOptions<WorkerControlOptions> configured, ILogger<ProfileBuildCoordinator> logger)
{
    private readonly WorkerControlOptions _options = configured.Value;

    public ProfileBuildRecord Queue(string profileRevisionId, string hostId)
    {
        RequireEnabled();
        var store = Store();
        var host = Approved(hostId);
        var profile = FindRevision(store, profileRevisionId) ?? throw new OrganizationNotFoundException($"Container profile revision '{profileRevisionId}' does not exist.");
        var (_, contextHash, _) = ProfileBuildContext.Render(profile, _options.ApprovedImageDigest, _options.ApprovedImagePlatform);
        var tag = ResultTag(profile.Id, contextHash);
        return store.QueueProfileBuild(profile.Id, host.Id, _options.ApprovedImageDigest, _options.ApprovedImagePlatform, contextHash, tag);
    }

    /// <summary>Runs one queued (or uncertain) build to a terminal state.</summary>
    public async Task<ProfileBuildRecord> RunAsync(string buildId, CancellationToken cancellationToken)
    {
        RequireEnabled();
        var store = Store();
        var build = store.GetProfileBuild(buildId) ?? throw new OrganizationNotFoundException($"Profile build '{buildId}' does not exist.");
        var host = Approved(build.HostId);
        var revision = FindRevision(store, build.ProfileRevisionId) ?? throw new OrganizationStoreCorruptException("The build's profile revision is missing.");
        if (build.BaseImageDigest != _options.ApprovedImageDigest || build.Platform != _options.ApprovedImagePlatform)
            throw new OrganizationConcurrencyException("The approved base image changed since this build was queued; queue a new build.");

        if (build.State == ProfileBuildStates.Uncertain) return await ReconcileAsync(store, host, build, revision, cancellationToken).ConfigureAwait(false);
        if (build.State != ProfileBuildStates.Queued) throw new OrganizationConcurrencyException($"Profile build '{buildId}' is {build.State}, not queued.");

        var (tar, contextHash, _) = ProfileBuildContext.Render(revision, build.BaseImageDigest, build.Platform);
        if (contextHash != build.ContextHash) throw new OrganizationConcurrencyException("The rendered build context no longer matches the queued build.");

        build = store.TransitionProfileBuild(build.Id, build.Revision, ProfileBuildStates.Building);
        try
        {
            var pinTag = ProfileBuildContext.PinTag(build.BaseImageDigest);
            var pin = await operations.ExecuteAsync(host, RemoteDockerOperation.ImageTag, [build.BaseImageDigest, pinTag], null, cancellationToken).ConfigureAwait(false);
            if (pin.ExitCode != 0)
                return Fail(store, build, pin.ErrorCategory == "not-found" ? "The approved base image is not present on the host." : "The base image could not be pinned on the host.", pin.ErrorCategory);

            var spec = new ImageBuildSpec(build.BaseImageDigest, build.Platform, revision.Id, build.ContextHash, build.ResultTag, NetworkRequired: revision.Definition.Contains("\"features\":{", StringComparison.Ordinal) && !revision.Definition.Contains("\"features\":{}", StringComparison.Ordinal) || revision.DockerfileFragment is not null);
            var result = await operations.BuildImageAsync(host, spec, tar, cancellationToken).ConfigureAwait(false);
            if (result.ErrorCategory == "transport")
            {
                // The command may or may not have completed on the host: leave it for reconciliation.
                return store.TransitionProfileBuild(build.Id, build.Revision, ProfileBuildStates.Uncertain, failureSummary: "Transport failed during the build; the result is unknown until reconciled.");
            }
            if (result.ExitCode != 0) return Fail(store, build, "The image build failed on the host.", result.ErrorCategory);

            build = store.TransitionProfileBuild(build.Id, build.Revision, ProfileBuildStates.Verifying);
            return await VerifyAsync(store, host, build, revision, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var current = store.GetProfileBuild(build.Id)!;
            return store.TransitionProfileBuild(current.Id, current.Revision, ProfileBuildStates.Uncertain, failureSummary: "The build timed out; the result is unknown until reconciled.");
        }
    }

    private async Task<ProfileBuildRecord> ReconcileAsync(OrganizationStore store, ApprovedExecutionHost host, ProfileBuildRecord build, ContainerProfileRevisionSummary revision, CancellationToken cancellationToken)
    {
        var inspect = await operations.ExecuteAsync(host, RemoteDockerOperation.ImageInspect, [build.ResultTag], null, cancellationToken).ConfigureAwait(false);
        if (inspect.ErrorCategory == "not-found")
            return store.TransitionProfileBuild(build.Id, build.Revision, ProfileBuildStates.Failed, failureSummary: "Reconciled: no image carries the build's tag on the host.");
        if (inspect.ExitCode != 0) throw new RemoteWorkerUnavailableException("The build could not be reconciled: the host did not answer the image inspect.", inspect.ErrorCategory == "transport");
        build = store.TransitionProfileBuild(build.Id, build.Revision, ProfileBuildStates.Verifying);
        return await VerifyAsync(store, host, build, revision, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProfileBuildRecord> VerifyAsync(OrganizationStore store, ApprovedExecutionHost host, ProfileBuildRecord build, ContainerProfileRevisionSummary revision, CancellationToken cancellationToken)
    {
        var inspect = await operations.ExecuteAsync(host, RemoteDockerOperation.ImageInspect, [build.ResultTag], null, cancellationToken).ConfigureAwait(false);
        if (inspect.ExitCode != 0) return Fail(store, build, "The built image could not be inspected.", inspect.ErrorCategory);
        var baseInspect = await operations.ExecuteAsync(host, RemoteDockerOperation.ImageInspect, [build.BaseImageDigest], null, cancellationToken).ConfigureAwait(false);
        if (baseInspect.ExitCode != 0) return Fail(store, build, "The approved base image could not be inspected.", baseInspect.ErrorCategory);

        string digest;
        try
        {
            digest = ImageContractVerifier.CheckInspect(inspect.StandardOutput, baseInspect.StandardOutput, revision.Id, build.ContextHash, build.BaseImageDigest, build.Platform);
        }
        catch (ImageContractException exception)
        {
            return Reject(store, build, exception.Message);
        }

        var verify = await operations.ExecuteAsync(host, RemoteDockerOperation.ImageVerify, [build.ResultTag, build.Platform], null, cancellationToken).ConfigureAwait(false);
        if (verify.ErrorCategory == "transport")
            return store.TransitionProfileBuild(build.Id, build.Revision, ProfileBuildStates.Uncertain, failureSummary: "Transport failed during verification.");
        if (verify.ExitCode != 0) return Reject(store, build, "The contract verification program did not complete inside the image.");
        try
        {
            ImageContractVerifier.CheckRuntime(verify.StandardOutput);
        }
        catch (ImageContractException exception)
        {
            return Reject(store, build, exception.Message);
        }

        var evidence = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inspect.StandardOutput + "\n" + verify.StandardOutput))).ToLowerInvariant();
        logger.LogInformation("Profile build {BuildId} produced verified image {Digest} on host {HostId}.", build.Id, digest, host.Id);
        return store.TransitionProfileBuild(build.Id, build.Revision, ProfileBuildStates.Built, imageDigest: digest, verified: true, evidenceHash: evidence);
    }

    private static ProfileBuildRecord Fail(OrganizationStore store, ProfileBuildRecord build, string summary, string category) =>
        store.TransitionProfileBuild(build.Id, build.Revision, ProfileBuildStates.Failed, failureSummary: $"{summary} ({category})");

    private static ProfileBuildRecord Reject(OrganizationStore store, ProfileBuildRecord build, string summary) =>
        store.TransitionProfileBuild(build.Id, build.Revision, ProfileBuildStates.Rejected, failureSummary: summary);

    internal static string ResultTag(string revisionId, string contextHash) => $"agentcontrol-profile:{revisionId}-{contextHash.Substring(7, 12)}";

    private static ContainerProfileRevisionSummary? FindRevision(OrganizationStore store, string revisionId)
    {
        foreach (var profile in store.ListContainerProfiles())
        {
            var match = store.ListContainerProfileRevisions(profile.Id)?.FirstOrDefault(r => r.Id == revisionId);
            if (match is not null) return match;
        }
        return null;
    }

    private OrganizationStore Store() => control.Organization ?? throw new OrganizationStoreException("The authoritative organization store is unavailable.");
    private ApprovedExecutionHost Approved(string hostId) => _options.ApprovedHosts.SingleOrDefault(x => x.Id == hostId) ?? throw new KeyNotFoundException("Execution host is not approved.");
    private void RequireEnabled() { if (!_options.Enabled) throw new WorkerControlDisabledException(); if (_options.Validate().Count != 0) throw new WorkerControlConfigurationException("Remote worker configuration is invalid."); }
}

public sealed class ImageContractException(string message) : Exception(message);

/// <summary>
/// Pure checks over what the host printed. <see cref="CheckInspect"/> proves the
/// built image is the child of the exact base (root filesystem prefix), carries
/// the controller's labels for this revision and context, keeps the supervisor
/// entrypoint and the platform, and publishes no ports; <see cref="CheckRuntime"/>
/// proves the fixed verification program observed the worker contract inside it.
/// </summary>
public static class ImageContractVerifier
{
    public static string CheckInspect(string builtJson, string baseJson, string revisionId, string contextHash, string baseDigest, string platform)
    {
        using var built = Parse(builtJson, "built image");
        using var basis = Parse(baseJson, "base image");
        var id = built.RootElement.GetProperty("Id").GetString() ?? string.Empty;
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^sha256:[0-9a-f]{64}$")) throw new ImageContractException("The built image id is not a sha256 digest.");
        if ((basis.RootElement.GetProperty("Id").GetString() ?? string.Empty) != baseDigest) throw new ImageContractException("The base image on the host is not the approved digest.");

        var config = built.RootElement.GetProperty("Config");
        var labels = config.TryGetProperty("Labels", out var l) && l.ValueKind == JsonValueKind.Object ? l : throw new ImageContractException("The built image carries no labels.");
        Require(labels, ProfileBuildContext.ProfileLabel, revisionId);
        Require(labels, ProfileBuildContext.ContextHashLabel, contextHash);
        Require(labels, ProfileBuildContext.BaseDigestLabel, baseDigest);

        var entrypoint = config.TryGetProperty("Entrypoint", out var e) && e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.GetString()).ToArray() : [];
        if (entrypoint.Length != 1 || entrypoint[0] != "/usr/local/bin/worker-supervisor") throw new ImageContractException("The built image does not keep the worker supervisor as its entrypoint.");
        if (config.TryGetProperty("User", out var user) && !string.IsNullOrEmpty(user.GetString()) && user.GetString() != "root" && user.GetString() != "0")
            throw new ImageContractException("The built image sets a non-root default user; the supervisor must start as root.");
        if (config.TryGetProperty("ExposedPorts", out var ports) && ports.ValueKind == JsonValueKind.Object && ports.EnumerateObject().Any())
            throw new ImageContractException("The built image exposes ports.");
        if (config.TryGetProperty("Volumes", out var volumes) && volumes.ValueKind == JsonValueKind.Object && volumes.EnumerateObject().Any())
            throw new ImageContractException("The built image declares anonymous volumes.");

        var os = built.RootElement.TryGetProperty("Os", out var o) ? o.GetString() : null;
        var arch = built.RootElement.TryGetProperty("Architecture", out var a) ? a.GetString() : null;
        if ($"{os}/{arch}" != platform) throw new ImageContractException($"The built image platform is {os}/{arch}, not {platform}.");

        var builtLayers = built.RootElement.GetProperty("RootFS").GetProperty("Layers").EnumerateArray().Select(x => x.GetString()).ToArray();
        var baseLayers = basis.RootElement.GetProperty("RootFS").GetProperty("Layers").EnumerateArray().Select(x => x.GetString()).ToArray();
        if (builtLayers.Length < baseLayers.Length || !baseLayers.SequenceEqual(builtLayers.Take(baseLayers.Length)))
            throw new ImageContractException("The built image's root filesystem does not start with the approved base's layers.");
        return id;
    }

    public static void CheckRuntime(string verifyJson)
    {
        using var document = Parse(verifyJson.Trim().Split('\n').Last(), "verification output");
        var root = document.RootElement;
        if (root.GetProperty("bridgeUid").ValueKind != JsonValueKind.Number || root.GetProperty("bridgeUid").GetInt32() != 1101) throw new ImageContractException("The bridge user is not uid 1101.");
        if (root.GetProperty("employeeUid").ValueKind != JsonValueKind.Number || root.GetProperty("employeeUid").GetInt32() != 1102) throw new ImageContractException("The employee user is not uid 1102.");
        var dirs = root.GetProperty("dirs");
        foreach (var (path, uid) in new[] { ("/control", 1101), ("/home/worker", 1102), ("/workspace", 1102), ("/session", 1102) })
        {
            var d = dirs.GetProperty(path);
            if (d.GetProperty("uid").GetInt32() != uid || d.GetProperty("gid").GetInt32() != uid || d.GetProperty("mode").GetInt32() != 448)
                throw new ImageContractException($"{path} is not owned {uid}:{uid} with mode 0700 (448).");
        }
        if (root.GetProperty("app").GetProperty("uid").GetInt32() != 0 || (root.GetProperty("app").GetProperty("mode").GetInt32() & 18) != 0) throw new ImageContractException("/app is not root-owned and group/other-unwritable.");
        if (root.GetProperty("supervisor").GetProperty("uid").GetInt32() != 0 || root.GetProperty("supervisor").GetProperty("mode").GetInt32() != 493) throw new ImageContractException("The worker supervisor is not root-owned 0755.");
        if (!root.GetProperty("workerDll").GetBoolean()) throw new ImageContractException("The worker dll is missing.");
        if (!root.GetProperty("dotnet").GetBoolean()) throw new ImageContractException("/usr/bin/dotnet is not executable.");
        if (!root.GetProperty("opencode").GetBoolean()) throw new ImageContractException("/usr/local/bin/opencode is not executable.");
        if (root.GetProperty("setuid").GetArrayLength() != 0) throw new ImageContractException("The built image contains setuid or setgid files.");
        if (root.GetProperty("dockerSock").GetBoolean()) throw new ImageContractException("The built image contains a Docker socket path.");
    }

    private static void Require(JsonElement labels, string key, string expected)
    {
        if (!labels.TryGetProperty(key, out var value) || value.GetString() != expected) throw new ImageContractException($"The built image label {key} is missing or wrong.");
    }

    private static JsonDocument Parse(string json, string what)
    {
        try
        {
            var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            // `docker image inspect` prints a one-element array; unwrap it.
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                var single = document.RootElement.EnumerateArray().SingleOrDefault();
                var unwrapped = JsonDocument.Parse(single.GetRawText());
                document.Dispose();
                return unwrapped;
            }
            return document;
        }
        catch (JsonException) { throw new ImageContractException($"The {what} output is not valid JSON."); }
        catch (InvalidOperationException) { throw new ImageContractException($"The {what} output is not a single object."); }
    }
}
