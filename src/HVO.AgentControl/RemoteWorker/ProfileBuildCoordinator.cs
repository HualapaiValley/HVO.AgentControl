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
    private readonly ExecutionTargetResolver _targets = new(control, configured);

    /// <summary>
    /// Build ids this process is actively driving. A row that is
    /// <c>building</c>/<c>verifying</c> AND owned here is in flight, not
    /// interrupted; a concurrent request for it waits on nothing and is told to
    /// retry, rather than stealing the row and racing its transitions. Rows in
    /// those states with no in-process owner are, by construction, left over
    /// from a previous process and are safe to reconcile.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _owned = new(StringComparer.Ordinal);

    public ProfileBuildRecord Queue(string profileRevisionId, string hostId)
    {
        RequireEnabled();
        var store = Store();
        // Queueing is a store-level intent for either target kind. Resolving the
        // target still requires the host to be a registered execution host.
        var target = _targets.Resolve(hostId);
        var profile = FindRevision(store, profileRevisionId) ?? throw new OrganizationNotFoundException($"Container profile revision '{profileRevisionId}' does not exist.");
        var (_, contextHash, _) = ProfileBuildContext.Render(profile, _options.ApprovedImageDigest, _options.ApprovedImagePlatform);
        var tag = ResultTag(profile.Id, contextHash);
        return store.QueueProfileBuild(profile.Id, target.Id, _options.ApprovedImageDigest, _options.ApprovedImagePlatform, contextHash, tag);
    }

    /// <summary>
    /// Moves every build a previous controller process left <c>building</c> or
    /// <c>verifying</c> to <c>uncertain</c>. Called once at startup by the hosted
    /// service so stranded rows are always reconcilable through the API.
    /// </summary>
    public IReadOnlyList<string> ReconcileInterruptedOnStartup() => control.Organization?.MarkInterruptedProfileBuildsUncertain() ?? [];

    /// <summary>Runs one queued, uncertain or interrupted build to a terminal state.</summary>
    public async Task<ProfileBuildRecord> RunAsync(string buildId, CancellationToken cancellationToken)
    {
        RequireEnabled();
        if (!_owned.TryAdd(buildId, 0)) throw new OrganizationConcurrencyException($"Profile build '{buildId}' is being run by another request; wait for it to finish.");
        try { return await RunOwnedAsync(buildId, cancellationToken).ConfigureAwait(false); }
        finally { _owned.TryRemove(buildId, out _); }
    }

    private async Task<ProfileBuildRecord> RunOwnedAsync(string buildId, CancellationToken cancellationToken)
    {
        var store = Store();
        var build = store.GetProfileBuild(buildId) ?? throw new OrganizationNotFoundException($"Profile build '{buildId}' does not exist.");
        var host = _targets.Resolve(build.HostId);
        var revision = FindRevision(store, build.ProfileRevisionId) ?? throw new OrganizationStoreCorruptException("The build's profile revision is missing.");
        if (build.BaseImageDigest != _options.ApprovedImageDigest || build.Platform != _options.ApprovedImagePlatform)
            throw new OrganizationConcurrencyException("The approved base image changed since this build was queued; queue a new build.");

        if (build.State is ProfileBuildStates.Building or ProfileBuildStates.Verifying)
        {
            // We hold the in-process lease and the row is still mid-flight, so no live
            // run of this process owns it: a previous process died with it (and the
            // startup pass has not run yet). The host may hold the result, so reconcile
            // by tag rather than build again.
            build = store.TransitionProfileBuild(build.Id, build.Revision, ProfileBuildStates.Uncertain, failureSummary: $"Found {build.State} without an owner; the result is unknown until reconciled.");
        }
        if (build.State != ProfileBuildStates.Uncertain && build.State != ProfileBuildStates.Queued) throw new OrganizationConcurrencyException($"Profile build '{buildId}' is {build.State}, not runnable.");

        try
        {
            if (build.State == ProfileBuildStates.Uncertain) return await ReconcileAsync(store, host, build, revision, cancellationToken).ConfigureAwait(false);

            var (tar, contextHash, _) = ProfileBuildContext.Render(revision, build.BaseImageDigest, build.Platform);
            if (contextHash != build.ContextHash) throw new OrganizationConcurrencyException("The rendered build context no longer matches the queued build.");

            build = store.TransitionProfileBuild(build.Id, build.Revision, ProfileBuildStates.Building);
            var pinTag = ProfileBuildContext.PinTag(build.BaseImageDigest);
            var pin = await operations.ExecuteAsync(host, RemoteDockerOperation.ImageTag, [build.BaseImageDigest, pinTag], null, cancellationToken).ConfigureAwait(false);
            if (pin.ExitCode != 0)
                return Fail(store, build, pin.ErrorCategory == "not-found" ? "The approved base image is not present on the host." : "The base image could not be pinned on the host.", pin.ErrorCategory);

            var spec = new ImageBuildSpec(build.BaseImageDigest, build.Platform, revision.Id, build.ContextHash, build.ResultTag, NetworkRequired: ProfileBuildContext.RequiresNetwork(revision));
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
        catch (OperationCanceledException)
        {
            // Timeout or caller cancellation (a client disconnect) after an intent was
            // recorded — on the initial path or the reconciliation path: the host may
            // have completed the work. Never leave the row mid-flight.
            var current = store.GetProfileBuild(build.Id)!;
            if (current.State is ProfileBuildStates.Building or ProfileBuildStates.Verifying)
                store.TransitionProfileBuild(current.Id, current.Revision, ProfileBuildStates.Uncertain, failureSummary: cancellationToken.IsCancellationRequested ? "The build was cancelled mid-flight; the result is unknown until reconciled." : "The build timed out; the result is unknown until reconciled.");
            throw;
        }
    }

    private async Task<ProfileBuildRecord> ReconcileAsync(OrganizationStore store, ExecutionTarget host, ProfileBuildRecord build, ContainerProfileRevisionSummary revision, CancellationToken cancellationToken)
    {
        var inspect = await operations.ExecuteAsync(host, RemoteDockerOperation.ImageInspect, [build.ResultTag], null, cancellationToken).ConfigureAwait(false);
        if (inspect.ErrorCategory == "not-found")
            return store.TransitionProfileBuild(build.Id, build.Revision, ProfileBuildStates.Failed, failureSummary: "Reconciled: no image carries the build's tag on the host.");
        if (inspect.ExitCode != 0) throw new RemoteWorkerUnavailableException("The build could not be reconciled: the host did not answer the image inspect.", inspect.ErrorCategory == "transport");
        build = store.TransitionProfileBuild(build.Id, build.Revision, ProfileBuildStates.Verifying);
        return await VerifyAsync(store, host, build, revision, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProfileBuildRecord> VerifyAsync(OrganizationStore store, ExecutionTarget host, ProfileBuildRecord build, ContainerProfileRevisionSummary revision, CancellationToken cancellationToken)
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

        var verify = await operations.ExecuteAsync(host, RemoteDockerOperation.ImageVerify, [build.ResultTag, build.BaseImageDigest, build.Platform], null, cancellationToken).ConfigureAwait(false);
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
        var baseConfig = basis.RootElement.GetProperty("Config");
        var labels = config.TryGetProperty("Labels", out var l) && l.ValueKind == JsonValueKind.Object ? l : throw new ImageContractException("The built image carries no labels.");
        Require(labels, ProfileBuildContext.ProfileLabel, revisionId);
        Require(labels, ProfileBuildContext.ContextHashLabel, contextHash);
        Require(labels, ProfileBuildContext.BaseDigestLabel, baseDigest);

        var entrypoint = config.TryGetProperty("Entrypoint", out var e) && e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.GetString()).ToArray() : [];
        var expectedEntrypoint = new[] { "/usr/bin/python3", "-I", "-S", "/usr/local/bin/worker-supervisor" };
        if (!entrypoint.SequenceEqual(expectedEntrypoint)) throw new ImageContractException("The built image does not keep the isolated worker-supervisor entrypoint.");
        RequireSameEnvironment(config, baseConfig);
        if ((config.TryGetProperty("WorkingDir", out var working) ? working.GetString() : null) != "/workspace") throw new ImageContractException("The built image working directory is not /workspace.");
        if (!SameJson(config, baseConfig, "Cmd")) throw new ImageContractException("The built image changes the base command.");
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

    /// <summary>
    /// The verification program ran in the approved base with the candidate
    /// mounted read-only, so nothing here came from a candidate executable. The
    /// artifacts the supervisor and bridge depend on must be identical to the
    /// base's copies in content **and** uid/gid/mode (a chmod 0644 on the
    /// interpreter is as fatal as replacing it); accounts,
    /// directory modes, setuid bits, file capabilities, ld.so.preload and the
    /// socket path are checked on the candidate's files directly.
    /// </summary>
    public static void CheckRuntime(string verifyJson)
    {
        using var document = Parse(verifyJson.Trim().Split('\n').Last(), "verification output");
        var root = document.RootElement;
        Account(root, "bridge", 1101, "/control", "/usr/sbin/nologin");
        Account(root, "employee", 1102, "/home/worker", "/bin/bash");
        var dirs = root.GetProperty("dirs");
        foreach (var (path, uid) in new[] { ("/control", 1101), ("/home/worker", 1102), ("/workspace", 1102), ("/session", 1102) })
        {
            var d = dirs.GetProperty(path);
            if (d.GetProperty("uid").GetInt32() != uid || d.GetProperty("gid").GetInt32() != uid || d.GetProperty("mode").GetInt32() != 448 || d.GetProperty("xattrs").GetArrayLength() != 0)
                throw new ImageContractException($"{path} is not owned {uid}:{uid} with mode 0700 (448).");
        }
        if (root.GetProperty("app").GetProperty("uid").GetInt32() != 0 || (root.GetProperty("app").GetProperty("mode").GetInt32() & 18) != 0) throw new ImageContractException("/app is not root-owned and group/other-unwritable.");
        if (root.GetProperty("supervisor").GetProperty("uid").GetInt32() != 0 || root.GetProperty("supervisor").GetProperty("mode").GetInt32() != 493) throw new ImageContractException("The worker supervisor is not root-owned 0755.");
        var artifacts = root.GetProperty("artifacts");
        foreach (var path in RequiredIdenticalArtifacts)
        {
            if (!artifacts.TryGetProperty(path, out var same) || same.ValueKind != JsonValueKind.True)
                throw new ImageContractException($"{path} differs from the approved base; the worker contract artifacts must be byte-identical.");
        }
        if (root.GetProperty("setuid").GetArrayLength() != 0) throw new ImageContractException("The built image contains setuid or setgid files.");
        if (root.GetProperty("fileCaps").GetArrayLength() != 0) throw new ImageContractException("The built image contains files with capability xattrs.");
        if (root.GetProperty("dockerSock").GetBoolean()) throw new ImageContractException("The built image contains a Docker socket path.");
        if (!root.GetProperty("mountpointsEmpty").GetBoolean()) throw new ImageContractException("A worker volume mountpoint contains image-baked files that could seed persistent state.");
        if (!root.GetProperty("systemPolicyAbsent").GetBoolean()) throw new ImageContractException("The built image contains system or root OpenCode configuration outside employee-owned volumes.");
    }

    /// <summary>
    /// Paths a fragment may not change in any way. They are what PID 1, the
    /// bridge and the ACP process execute or load; a fragment that needs a
    /// different runtime is a different base, not a profile.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredIdenticalArtifacts =
    [
        "/usr/local/bin/worker-supervisor", "/usr/local/bin/profile-image-verify", "/app", "/usr/bin/dotnet", "/usr/share/dotnet",
        "/usr/local/bin/node", "/usr/local/lib/node_modules/opencode-ai", "/usr/local/bin/opencode",
        // PID 1 is `#!/usr/bin/env python3`: env, the interpreter and its symlink must
        // be byte-identical, and every standard-library file the base ships must be
        // unchanged (the python feature may add files such as ensurepip, never alter
        // or remove one). Site packages are not pinned; the supervisor imports only
        // the standard library. /bin/sh (dash) is what subprocess and the fragment's
        // own RUN lines execute.
        "/usr/bin/python3", "/usr/bin/python3.12", "/usr/lib/python3.12", "/usr/bin/env", "/bin/sh", "/usr/bin/dash",
        // The dynamic loader and every shared library the base ships (libc, libm,
        // libstdc++, libz, libexpat, ...): a fragment may add libraries for its own
        // tools but may not alter or remove any the base's binaries load. The loader
        // configuration and NSS configuration are pinned so resolution cannot be
        // redirected to an added path.
        // ld.so.cache is consulted before the default directories, so a fragment
        // running `ldconfig /opt/evil` would redirect a contract binary without
        // touching ld.so.conf. A recipe that installs a library package (libicu for
        // the SDK) legitimately regenerates the cache, so the invariant is on its
        // contents, read with the BASE's ldconfig and keyed by bare SONAME (the
        // loader prefers a hwcap/flags variant of the same SONAME, so variants are
        // the same key): every SONAME the base resolves must have exactly the base's
        // ordered entry list including flags, each pointing at a path identical to
        // the base's file; new SONAMEs may be added (a contract binary's
        // dependencies are all base SONAMEs). A crafted cache can therefore never
        // redirect a contract binary to an added library, by order or by hwcap.
        "/lib", "/lib64", "/usr/lib/x86_64-linux-gnu", "/usr/lib/aarch64-linux-gnu", "/etc/ld.so.conf", "/etc/ld.so.conf.d", "/etc/ld.so.cache", "/etc/nsswitch.conf",
        // Profiles have no approved CA feature: outbound trust stays the
        // approved base's. System/root OpenCode policy locations are required
        // absent below; employee/project configuration lives on private volumes.
        "/etc/ssl", "/usr/share/ca-certificates", "/etc/ca-certificates.conf",
        // Absent in the base and must stay absent: a preload would hijack every process,
        // and a python3 ahead of /usr/bin on the supervisor's PATH would replace PID 1's
        // interpreter (the SDK from apt lives under /usr/lib/dotnet and is allowed).
        "/etc/ld.so.preload", "/lib/python-shadow",
    ];

    private static void RequireSameEnvironment(JsonElement candidate, JsonElement basis)
    {
        var actual = Environment(candidate);
        var expected = Environment(basis);
        if (actual.Count != expected.Count || actual.Any(item => !expected.TryGetValue(item.Key, out var value) || value != item.Value))
            throw new ImageContractException("The built image environment differs from the approved base; profile variables must be applied only to employee processes.");
    }

    private static IReadOnlyDictionary<string, string> Environment(JsonElement config)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!config.TryGetProperty("Env", out var env) || env.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return result;
        if (env.ValueKind != JsonValueKind.Array) throw new ImageContractException("The image environment is not an array.");
        foreach (var item in env.EnumerateArray())
        {
            var text = item.GetString() ?? throw new ImageContractException("The image environment contains a non-string entry.");
            var equals = text.IndexOf('=');
            if (equals <= 0 || !result.TryAdd(text[..equals], text[(equals + 1)..])) throw new ImageContractException("The image environment contains a malformed or duplicate variable.");
        }
        return result;
    }

    private static bool SameJson(JsonElement candidate, JsonElement basis, string property)
    {
        var hasCandidate = candidate.TryGetProperty(property, out var left) && left.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
        var hasBase = basis.TryGetProperty(property, out var right) && right.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
        return hasCandidate == hasBase && (!hasCandidate || JsonElement.DeepEquals(left, right));
    }

    private static void Account(JsonElement root, string name, int uid, string home, string shell)
    {
        if (!root.TryGetProperty(name, out var account) || account.ValueKind != JsonValueKind.Object) throw new ImageContractException($"The {name} account is missing.");
        if (account.GetProperty("uid").GetInt32() != uid || account.GetProperty("gid").GetInt32() != uid) throw new ImageContractException($"The {name} account is not uid/gid {uid}.");
        if (account.GetProperty("home").GetString() != home || account.GetProperty("shell").GetString() != shell) throw new ImageContractException($"The {name} account's home or shell was changed.");
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
