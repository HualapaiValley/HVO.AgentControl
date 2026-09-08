using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HVO.AgentControl.Provisioning;

// Host-side only. No registration in the web process and no ambient Docker context.
public sealed partial class LocalDevContainerRunner(ProvisionerHostAuthority? authority,
    IProvisionAttemptLedger? ledger, IProvisionProcessRunner processes)
{
    public const string CliVersion = "0.89.0";
    public const string CliSha256 = "22b5b3a7345608f552db2f1af589ca06c7274d116778d00aa67cbfe1e1cafc65";
    private const int JsonLimit = 262144;
    private static readonly JsonDocumentOptions ConfigJson = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 64 };

    internal Func<string, CancellationToken, Task<string>> ReadCliHash { get; init; } = async (path, token) =>
    { await using var stream = File.OpenRead(path); return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token)); };

    public Task<HostProvisionResult> Provision(HostProvisionRequest request, CancellationToken token = default, Action<ProvisionProgress>? progress = null) => Run(request, ProvisionAction.CreateOrObserve, null, token, progress);
    public Task<HostProvisionResult> Reconcile(HostProvisionRequest request, CancellationToken token = default, Action<ProvisionProgress>? progress = null) => Run(request, ProvisionAction.Observe, null, token, progress);
    // The ledger must grant separate, drained retirement authority. This does not
    // purge volumes, images, caches or the host checkout.
    public Task<HostProvisionResult> RemoveOwned(HostProvisionRequest request, string containerId, CancellationToken token = default, Action<ProvisionProgress>? progress = null) => Run(request, ProvisionAction.Remove, containerId, token, progress);

    private async Task<HostProvisionResult> Run(HostProvisionRequest request, ProvisionAction action, string? removeId, CancellationToken token, Action<ProvisionProgress>? progress)
    {
        var context = new RunContext(request.OperationId, progress);
        try
        {
            if (authority is null || ledger is null) return context.Result("Unsupported", "missing_provisioner_authority");
            if (!OperatingSystem.IsLinux() || authority.Transport != "LocalLinux") return context.Result("Unsupported", "unsupported_provisioner_transport");
            var intent = ResolveIntent(request);
            context.Intent = intent;
            await using var admission = await ledger.Acquire(intent, action, token);
            // Docker ownership remains recoverable after the CLI installation or
            // checkout disappears. Neither is authority for finding/removing owners.
            await VerifyDockerHost(intent, token);
            var owners = await ObserveOwners(intent, token);
            context.Observed = owners;
            if (action == ProvisionAction.Remove)
            {
                if (removeId is null || !ContainerId().IsMatch(removeId)) throw new ProvisionFault("Failed", "invalid_container_id");
                if (owners is null) return context.Result("AbsentObserved", "no_owned_container_observed");
                if (owners.ContainerId != removeId) throw new ProvisionFault("Unknown", "removal_identity_mismatch");
                if (!await admission.TryBeginEffect("remove", removeId, token)) return context.Result("Unknown", "removal_attempt_already_recorded");
                context.EffectStarted = true;
                var removed = await Docker(intent, ["container", "rm", "--force", removeId], token);
                context.EffectStarted = removed.Started;
                if (!Success(removed)) return context.Result("Unknown", "removal_not_confirmed");
                if (await ObserveOwners(intent, token) is not null) return context.Result("Unknown", "container_still_observed");
                return context.Result("Removed", WorkspacePresent(intent)
                    ? "owned_container_removed_retained_data_preserved"
                    : "owned_container_removed_workspace_retention_unverified");
            }
            if (owners is not null)
            {
                try
                {
                    await VerifyCliHost(intent, token);
                    await VerifySource(intent, token, allowUntracked: true);
                    if (owners.Running) context.Executed = await VerifyExecution(intent, owners, context, token);
                }
                catch (Exception error) when (error is ProvisionFault or IOException or UnauthorizedAccessException)
                {
                    context.Add("reconcile", "Owner confirmed; environment probe unavailable: " + (error is ProvisionFault fault ? fault.Code : "approved_path_unavailable"));
                    return context.Result("Observed", "owned_container_observed_environment_unverified");
                }
                return context.Result("Observed", "owned_container_observed_hooks_not_inferred");
            }
            if (action == ProvisionAction.Observe) return context.Result("Unknown", "zero_owners_observed_no_retry_authority");
            if (await admission.HasEffect("up", intent.Workspace.Id, token)) return context.Result("Unknown", "up_attempt_already_recorded_zero_owners_no_retry");
            await VerifyCliHost(intent, token);
            await VerifySource(intent, token);
            var config = ReadApprovedConfig(intent);
            ValidateSupportedConfig(intent, config);
            await VerifyCreationInputs(intent, token);
            var resolved = await Cli(intent, "read-configuration", ["--include-merged-configuration"], context, token);
            if (!Success(resolved)) throw new ProvisionFault("Failed", "configuration_resolution_failed");
            context.Configuration = ParseObject(resolved.StandardOutput, "invalid_resolved_configuration");
            if (!context.Configuration.Value.TryGetProperty("configuration", out var raw) || raw.ValueKind != JsonValueKind.Object)
                throw new ProvisionFault("Failed", "missing_resolved_configuration");
            if (!context.Configuration.Value.TryGetProperty("mergedConfiguration", out var effective) || effective.ValueKind != JsonValueKind.Object)
                throw new ProvisionFault("Unsupported", "missing_effective_configuration");
            ValidateConfigurationPolicy(intent, effective, resolved: true);
            // A configuration may be changed by another process while resolution runs.
            await VerifySource(intent, token);
            await VerifyCreationInputs(intent, token);
            if (!await admission.TryBeginEffect("up", intent.Workspace.Id, token)) return context.Result("Unknown", "up_attempt_already_recorded_zero_owners_no_retry");
            context.EffectStarted = true;
            var extra = new List<string> { "--include-configuration", "--include-merged-configuration", "--frozen-lockfile" };
            if (request.ColdBuild) extra.Add("--build-no-cache");
            var up = await Cli(intent, "up", extra, context, token, TimeSpan.FromMinutes(12));
            context.EffectStarted = up.Started;
            if (!up.Started) return context.Result("Failed", "cli_did_not_start_attempt_remains_consumed");
            if (up.Interrupted || up.Truncated) return context.Result("Unknown", up.Interrupted ? "up_interrupted_reconcile_required" : "up_output_limit_reconcile_required");
            context.Observed = await ObserveOwners(intent, token);
            if (up.ExitCode != 0) return context.Result("Failed", "cli_failed_owned_resources_retained");
            var final = ParseObject(up.StandardOutput, "malformed_up_result_reconcile_required");
            if (Text(final, "outcome") != "success" || context.Observed is null || Text(final, "containerId") != context.Observed.ContainerId)
                return context.Result("Unknown", "up_result_not_confirmed_by_docker");
            if (Text(final, "remoteUser") != intent.Workspace.RemoteUser || Text(final, "remoteWorkspaceFolder") != intent.Workspace.ContainerWorkspace)
                return context.Result("Failed", "resolved_user_or_workspace_mismatch");
            if (final.TryGetProperty("mergedConfiguration", out var merged)) context.Configuration = merged.Clone();
            await VerifySource(intent, token, allowUntracked: true);
            if (!context.Observed.Running) return context.Result("Failed", "container_not_running");
            context.Executed = await VerifyExecution(intent, context.Observed, context, token);
            return context.Result("VerifiedEnvironment", "cli_hooks_and_environment_observed_enrollment_not_performed");
        }
        catch (ProvisionIntentConflictException) { return context.Result("Failed", "operation_intent_conflict"); }
        catch (ProvisionFault error) { return context.Result(error.State, error.Code); }
        catch (OperationCanceledException) { return context.Result(context.EffectStarted ? "Unknown" : "Failed", context.EffectStarted ? "cancelled_reconcile_required" : "cancelled_before_effect"); }
        catch (Exception error) when (error is KeyNotFoundException or JsonException) { return context.Result("Unknown", "malformed_external_evidence"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { return context.Result(context.EffectStarted ? "Unknown" : "Failed", "invalid_or_unavailable_provisioning_evidence"); }
    }

    private ProvisionIntent ResolveIntent(HostProvisionRequest request)
    {
        if (!Guid.TryParseExact(request.OperationId, "D", out _) || string.IsNullOrEmpty(authority!.HostId) || authority.Revision < 1 ||
            !authority.Workspaces.TryGetValue(request.WorkspaceId, out var workspace)) throw new ProvisionFault("Failed", "unapproved_operation_or_workspace");
        if (workspace.Id != request.WorkspaceId || !Revision().IsMatch(workspace.SourceRevision) || !Digest().IsMatch(workspace.ConfigurationSha256) ||
            string.IsNullOrWhiteSpace(workspace.Repository) || workspace.Tools.IsDefault || workspace.Tools.Length > 20 || !UserName().IsMatch(workspace.RemoteUser)) throw new ProvisionFault("Failed", "invalid_approved_workspace");
        LexicalPath(authority.WorkspaceRoot);
        var directory = LexicalPath(workspace.Directory);
        if (!Within(directory, authority.WorkspaceRoot) || directory == authority.WorkspaceRoot) throw new ProvisionFault("Failed", "workspace_outside_authority");
        if (Path.IsPathRooted(workspace.ConfigurationPath)) throw new ProvisionFault("Failed", "configuration_must_be_relative");
        var config = LexicalPath(Path.Combine(directory, workspace.ConfigurationPath));
        if (!Within(config, directory)) throw new ProvisionFault("Failed", "configuration_outside_workspace");
        if (!workspace.ContainerWorkspace.StartsWith('/') || workspace.ContainerWorkspace.Contains("..", StringComparison.Ordinal) || workspace.ContainerWorkspace.Any(char.IsControl))
            throw new ProvisionFault("Failed", "invalid_container_workspace");
        var immutable = new
        {
            request.OperationId,
            authority.HostId,
            authority.Revision,
            authority.DockerEngineId,
            authority.DockerSocket,
            authority.NodePath,
            authority.CliBundlePath,
            authority.DockerPath,
            authority.GitPath,
            authority.WorkspaceRoot,
            authority.ToolStateDirectory,
            workspace,
            CliVersion,
            CliSha256,
            request.ColdBuild
        };
        var digest = Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(immutable)));
        var labels = new Dictionary<string, string>
        {
            ["hvo.agentcontrol.provisioner"] = "official-cli-v1",
            ["hvo.agentcontrol.operation"] = request.OperationId,
            ["hvo.agentcontrol.host"] = authority.HostId,
            ["hvo.agentcontrol.workspace"] = workspace.Id,
            ["hvo.agentcontrol.intent"] = digest,
            ["hvo.agentcontrol.source"] = workspace.SourceRevision,
            ["hvo.agentcontrol.config"] = workspace.ConfigurationSha256
        }.ToImmutableDictionary(StringComparer.Ordinal);
        return new(request.OperationId, authority.HostId, authority.Revision, workspace, CliVersion, CliSha256, request.ColdBuild, digest, labels);
    }

    private async Task VerifyDockerHost(ProvisionIntent intent, CancellationToken token)
    {
        CanonicalPath(authority!.DockerPath, false);
        CanonicalPath(authority.ToolStateDirectory, true);
        if (!Path.IsPathFullyQualified(authority.DockerSocket) || !File.Exists(authority.DockerSocket)) throw new ProvisionFault("Unsupported", "configured_docker_socket_unavailable");
        var engine = await Docker(intent, ["info", "--format", "{{json .}}"], token);
        if (!Success(engine)) throw new ProvisionFault("Unknown", "docker_engine_unavailable");
        var info = ParseObject(engine.StandardOutput, "malformed_docker_engine_identity");
        if (Text(info, "ID") != authority.DockerEngineId || Text(info, "OSType") != "linux") throw new ProvisionFault("Unknown", "docker_engine_identity_mismatch");
    }

    private async Task VerifyCliHost(ProvisionIntent intent, CancellationToken token)
    {
        foreach (var path in new[] { authority!.NodePath, authority.CliBundlePath, authority.GitPath }) CanonicalPath(path, false);
        CanonicalPath(authority.WorkspaceRoot, true);
        CanonicalPath(intent.Workspace.Directory, true);
        CanonicalPath(ConfigPath(intent), false);
        if (await ReadCliHash(authority.CliBundlePath, token) != CliSha256) throw new ProvisionFault("Failed", "cli_bundle_identity_mismatch");
        var version = await Command(intent, authority.NodePath, [authority.CliBundlePath, "--version"], token);
        if (!Success(version) || version.StandardOutput.Trim() != CliVersion) throw new ProvisionFault("Unsupported", "pinned_cli_unavailable");
    }

    private async Task VerifySource(ProvisionIntent intent, CancellationToken token, bool allowUntracked = false)
    {
        if (new FileInfo(ConfigPath(intent)).Length > JsonLimit) throw new ProvisionFault("Failed", "configuration_too_large");
        if (Hash(await File.ReadAllBytesAsync(ConfigPath(intent), token)) != intent.Workspace.ConfigurationSha256) throw new ProvisionFault("Failed", "configuration_digest_changed");
        var head = await Command(intent, authority!.GitPath, ["rev-parse", "HEAD"], token);
        var root = await Command(intent, authority.GitPath, ["rev-parse", "--show-toplevel"], token);
        var remote = await Command(intent, authority.GitPath, ["remote", "get-url", "origin"], token);
        var status = await Command(intent, authority.GitPath, ["status", "--porcelain=v1", allowUntracked ? "--untracked-files=no" : "--untracked-files=all"], token);
        if (!Success(head) || head.StandardOutput.Trim() != intent.Workspace.SourceRevision || !Success(root) || root.StandardOutput.Trim() != intent.Workspace.Directory ||
            !Success(remote) || remote.StandardOutput.Trim() != intent.Workspace.Repository || !Success(status) || status.StandardOutput.Trim().Length != 0)
            throw new ProvisionFault("Failed", "source_identity_or_clean_checkout_changed");
    }

    private async Task VerifyCreationInputs(ProvisionIntent intent, CancellationToken token)
    {
        // Status omits ignored inputs and can trust index flags. Compare the fresh
        // checkout with committed blobs, including the complete local Feature and
        // build context. Existing owners never repeat up or pass through this gate.
        var tree = await Command(intent, authority!.GitPath, ["ls-tree", "-r", "-z", "--full-tree", intent.Workspace.SourceRevision], token);
        if (!Success(tree)) throw new ProvisionFault("Failed", "source_tree_evidence_unavailable");
        var expected = new Dictionary<string, (string Mode, string Hash)>(StringComparer.Ordinal);
        foreach (var entry in tree.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = entry.IndexOf('\t');
            var fields = tab < 0 ? [] : entry[..tab].Split(' ');
            if (fields.Length != 3 || fields[0] is not ("100644" or "100755") || fields[1] != "blob" || !Revision().IsMatch(fields[2]))
                throw new ProvisionFault("Unsupported", "unsupported_source_tree_entry");
            var relative = entry[(tab + 1)..];
            var path = LexicalPath(Path.Combine(intent.Workspace.Directory, relative));
            if (!Within(path, intent.Workspace.Directory) || relative == ".git" || relative.StartsWith(".git/", StringComparison.Ordinal) ||
                !expected.TryAdd(path, (fields[0], fields[2])) || expected.Count > 4096)
                throw new ProvisionFault("Failed", "invalid_or_excessive_source_tree");
        }
        if (expected.Count == 0) throw new ProvisionFault("Failed", "empty_source_tree");
        var pending = new Stack<string>(); pending.Push(intent.Workspace.Directory);
        var visited = 0;
        long totalBytes = 0;
        var buffer = new byte[65536];
        while (pending.TryPop(out var directory))
            foreach (var item in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if (item == Path.Combine(intent.Workspace.Directory, ".git")) continue;
                if (++visited > 8192) throw new ProvisionFault("Failed", "source_tree_limit_exceeded");
                var attributes = File.GetAttributes(item);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new ProvisionFault("Unsupported", "source_symlink_not_authorized");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!expected.Keys.Any(path => Within(path, item))) throw new ProvisionFault("Failed", "uncommitted_source_input");
                    pending.Push(item); continue;
                }
                if (!expected.Remove(item, out var committed)) throw new ProvisionFault("Failed", "uncommitted_source_input");
                await using var stream = File.OpenRead(item);
                totalBytes += stream.Length;
                if (totalBytes > 256L * 1024 * 1024) throw new ProvisionFault("Failed", "source_tree_limit_exceeded");
                // SHA-1 is this approved Git repository's object format. Config
                // and CLI identities are independently pinned with SHA-256.
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                hash.AppendData(Encoding.UTF8.GetBytes("blob " + stream.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0"));
                int read;
                while ((read = await stream.ReadAsync(buffer, token)) != 0) hash.AppendData(buffer.AsSpan(0, read));
                var executable = !OperatingSystem.IsWindows() && (File.GetUnixFileMode(item) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
                if (Convert.ToHexStringLower(hash.GetHashAndReset()) != committed.Hash || executable != (committed.Mode == "100755"))
                    throw new ProvisionFault("Failed", "committed_source_input_changed");
            }
        if (expected.Count != 0) throw new ProvisionFault("Failed", "committed_source_input_missing");
    }

    private JsonElement ReadApprovedConfig(ProvisionIntent intent)
    {
        var file = new FileInfo(ConfigPath(intent));
        if (file.Length > JsonLimit) throw new ProvisionFault("Failed", "configuration_too_large");
        JsonDocument document;
        try { document = JsonDocument.Parse(File.ReadAllText(file.FullName), ConfigJson); }
        catch (JsonException) { throw new ProvisionFault("Failed", "invalid_configuration_json"); }
        using var dispose = document;
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ProvisionFault("Failed", "invalid_configuration");
        return document.RootElement.Clone();
    }

    private static void ValidateSupportedConfig(ProvisionIntent intent, JsonElement config)
    {
        ValidateConfigurationPolicy(intent, config, resolved: false);
        var configDirectory = Path.GetDirectoryName(ConfigPath(intent))!;
        if (config.TryGetProperty("build", out var build))
        {
            foreach (var key in new[] { "context", "dockerfile" })
            {
                var path = Text(build, key);
                if (path is null) throw new ProvisionFault("Unsupported", "explicit_build_paths_required");
                var resolved = CanonicalPath(Path.GetFullPath(Path.Combine(configDirectory, path)), key == "context");
                if (!Within(resolved, intent.Workspace.Directory)) throw new ProvisionFault("Failed", "build_input_outside_workspace");
            }
        }
        else if (Text(config, "image") is not { } image || !image.Contains("@sha256:", StringComparison.Ordinal)) throw new ProvisionFault("Unsupported", "dockerfile_or_immutable_image_required");
        if (config.TryGetProperty("features", out var features))
        {
            if (features.ValueKind != JsonValueKind.Object) throw new ProvisionFault("Failed", "invalid_features");
            foreach (var feature in features.EnumerateObject())
            {
                if (!feature.Name.StartsWith("./", StringComparison.Ordinal)) throw new ProvisionFault("Unsupported", "only_source_pinned_local_features_supported");
                if (!Within(CanonicalPath(Path.GetFullPath(Path.Combine(configDirectory, feature.Name)), true), intent.Workspace.Directory)) throw new ProvisionFault("Failed", "feature_outside_workspace");
            }
        }
    }

    private static void ValidateConfigurationPolicy(ProvisionIntent intent, JsonElement config, bool resolved)
    {
        // Reviewed Dockerfile/image configurations only in this slice. Host hooks,
        // external binds, Compose and elevated Docker capabilities need a separate
        // explicit authority model, rather than inheriting the host's privileges.
        foreach (var key in new[] { "dockerComposeFile", "initializeCommand" })
            if (config.TryGetProperty(key, out _)) throw new ProvisionFault("Unsupported", "unsupported_configuration_capability_" + key);
        foreach (var key in new[] { "mounts", "capAdd", "securityOpt" })
            if (config.TryGetProperty(key, out var list) && (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() != 0))
                throw new ProvisionFault("Unsupported", "unsupported_configuration_capability_" + key);
        if (config.TryGetProperty("privileged", out var privileged) && privileged.ValueKind != JsonValueKind.False) throw new ProvisionFault("Unsupported", "privileged_configuration_not_authorized");
        if (Text(config, "remoteUser") != intent.Workspace.RemoteUser || Text(config, "workspaceFolder") != intent.Workspace.ContainerWorkspace ||
            Text(config, "workspaceMount") != "source=" + (resolved ? intent.Workspace.Directory : "${localWorkspaceFolder}") + ",target=" + intent.Workspace.ContainerWorkspace + ",type=bind" ||
            (config.TryGetProperty("containerUser", out _) && Text(config, "containerUser") != intent.Workspace.RemoteUser))
            throw new ProvisionFault("Failed", "requested_configuration_identity_mismatch");
        if (config.TryGetProperty("runArgs", out var arguments) && (arguments.ValueKind != JsonValueKind.Array ||
            arguments.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || !ResourceArgument().IsMatch(x.GetString()!))))
            throw new ProvisionFault("Unsupported", "unsupported_docker_run_arguments");
    }

    private async Task<ProvisionContainerObservation?> ObserveOwners(ProvisionIntent intent, CancellationToken token)
    {
        try
        {
            // Query the operation first, not the full digest: a same-ID/different-intent
            // container must be reported as a conflict, never mistaken for zero owners.
            var found = await Docker(intent, ["container", "ls", "--all", "--no-trunc", "--filter", "label=hvo.agentcontrol.operation=" + intent.OperationId, "--format", "{{.ID}}"], token);
            if (!Success(found)) throw new ProvisionFault("Unknown", "docker_ownership_query_failed");
            var ids = found.StandardOutput.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (ids.Length == 0) return null;
            if (ids.Length != 1 || !ContainerId().IsMatch(ids[0])) throw new ProvisionFault("Unknown", "multiple_or_malformed_owners");
            var inspected = await Docker(intent, ["container", "inspect", ids[0]], token);
            if (!Success(inspected)) throw new ProvisionFault("Unknown", "docker_owner_inspection_failed");
            using var document = JsonDocument.Parse(inspected.StandardOutput, new JsonDocumentOptions { MaxDepth = 64 });
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != 1) throw new ProvisionFault("Unknown", "malformed_owner_inspection");
            var item = document.RootElement[0];
            var config = item.GetProperty("Config");
            var labelJson = config.GetProperty("Labels");
            if (labelJson.ValueKind != JsonValueKind.Object) throw new ProvisionFault("Unknown", "missing_owner_labels");
            var labels = labelJson.EnumerateObject().ToImmutableDictionary(x => x.Name, x => x.Value.GetString() ?? "", StringComparer.Ordinal);
            if (intent.Labels.Any(x => !labels.TryGetValue(x.Key, out var value) || value != x.Value)) throw new ProvisionFault("Unknown", "observed_intent_conflict");
            if (Text(item, "Id") != ids[0] || Text(item, "Image") is not { } image || !ImageId().IsMatch(image)) throw new ProvisionFault("Unknown", "invalid_observed_container_identity");
            var mounts = item.GetProperty("Mounts").EnumerateArray().Select(x => new ProvisionMount(Text(x, "Type")!, Text(x, "Source")!, Text(x, "Destination")!, Text(x, "Name"), x.GetProperty("RW").GetBoolean())).ToImmutableArray();
            var hostConfig = item.GetProperty("HostConfig");
            if (hostConfig.GetProperty("Privileged").GetBoolean() ||
                !NullOrEmptyArray(hostConfig.GetProperty("CapAdd")) || !NullOrEmptyArray(hostConfig.GetProperty("SecurityOpt")) ||
                mounts.Any(x => x.Type == "bind" && x.Source != intent.Workspace.Directory) ||
                !mounts.Any(x => x.Type == "bind" && x.Source == intent.Workspace.Directory && x.Destination == intent.Workspace.ContainerWorkspace && x.Writable))
                throw new ProvisionFault("Unknown", "observed_mount_or_privilege_mismatch");
            return new(ids[0], image, Text(config, "Image") ?? "", Text(config, "User") ?? "", item.GetProperty("State").GetProperty("Running").GetBoolean(), labels, mounts);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { throw new ProvisionFault("Unknown", "malformed_owner_inspection"); }
    }

    private async Task<ProvisionExecutionEvidence> VerifyExecution(ProvisionIntent intent, ProvisionContainerObservation owner, RunContext context, CancellationToken token)
    {
        async Task<string> Probe(IEnumerable<string> command)
        {
            var args = new[] { "--container-id", owner.ContainerId }.Concat(command);
            var result = await Cli(intent, "exec", args, context, token);
            if (!Success(result)) throw new ProvisionFault("Failed", "environment_probe_failed");
            return ExecOutput(result).Trim();
        }
        var user = await Probe(["id", "-un"]);
        var uid = await Probe(["id", "-u"]);
        var workspace = await Probe(["pwd"]);
        context.Executed = new(user, uid, workspace, ImmutableDictionary<string, string>.Empty);
        if (user != intent.Workspace.RemoteUser || workspace != intent.Workspace.ContainerWorkspace || !int.TryParse(uid, out _)) throw new ProvisionFault("Failed", "observed_execution_identity_mismatch");
        var tools = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        context.Executed = new(user, uid, workspace, tools.ToImmutable());
        foreach (var probe in intent.Workspace.Tools)
        {
            if (probe.Command.IsDefaultOrEmpty || probe.Command.Any(x => x.Contains('\0'))) throw new ProvisionFault("Failed", "invalid_trusted_tool_probe");
            var output = await Probe(probe.Command);
            tools.Add(probe.Name, output.Length <= 4096 ? output : output[..4096]);
            context.Executed = new(user, uid, workspace, tools.ToImmutable());
            if (!output.Contains(probe.ExpectedOutput, StringComparison.Ordinal)) throw new ProvisionFault("Failed", "expected_tool_evidence_missing");
        }
        return new(user, uid, workspace, tools.ToImmutable());
    }

    private Task<ProvisionProcessResult> Cli(ProvisionIntent intent, string verb, IEnumerable<string> extra, RunContext context, CancellationToken token, TimeSpan? timeout = null)
    {
        var args = new List<string> { authority!.CliBundlePath, verb, "--workspace-folder", intent.Workspace.Directory, "--config", ConfigPath(intent),
            "--docker-path", authority.DockerPath, "--user-data-folder", Path.Combine(authority.ToolStateDirectory, "cli"), "--log-format", "json" };
        foreach (var pair in intent.Labels.OrderBy(x => x.Key, StringComparer.Ordinal)) { args.Add("--id-label"); args.Add(pair.Key + "=" + pair.Value); }
        args.AddRange(extra);
        context.Add(verb, "Official Dev Container CLI " + CliVersion + " " + verb);
        return Command(intent, authority.NodePath, args, token, text => context.Add(verb, text), timeout);
    }
    private Task<ProvisionProcessResult> Docker(ProvisionIntent intent, IEnumerable<string> args, CancellationToken token) => Command(intent, authority!.DockerPath, args, token, workingDirectory: authority.ToolStateDirectory);
    private Task<ProvisionProcessResult> Command(ProvisionIntent intent, string executable, IEnumerable<string> args, CancellationToken token, Action<string>? progress = null, TimeSpan? timeout = null, string? workingDirectory = null)
    {
        var environment = new Dictionary<string, string>
        {
            ["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
            ["LANG"] = "C.UTF-8",
            ["HOME"] = authority!.ToolStateDirectory,
            ["DOCKER_CONFIG"] = Path.Combine(authority.ToolStateDirectory, "docker"),
            ["DOCKER_HOST"] = "unix://" + authority.DockerSocket,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = "/dev/null",
            ["GIT_CONFIG_COUNT"] = "2",
            ["GIT_CONFIG_KEY_0"] = "core.fsmonitor",
            ["GIT_CONFIG_VALUE_0"] = "false",
            ["GIT_CONFIG_KEY_1"] = "core.hooksPath",
            ["GIT_CONFIG_VALUE_1"] = "/dev/null"
        }.ToImmutableDictionary(StringComparer.Ordinal);
        return processes.Run(new(executable, args.ToImmutableArray(), workingDirectory ?? intent.Workspace.Directory, environment, timeout ?? TimeSpan.FromSeconds(45), JsonLimit), progress, token);
    }

    // CLI 0.89.0 --log-format json emits exec output as JSONL "raw"
    // messages on stderr, not stdout. Bootstrap diagnostics are separate "text"
    // events and must never be mistaken for successful tool/user observations.
    private static string ExecOutput(ProvisionProcessResult result)
    {
        if (result.ProgressTruncated) throw new ProvisionFault("Unknown", "cli_exec_evidence_truncated");
        if (result.StandardOutput.Length != 0) throw new ProvisionFault("Unknown", "unexpected_cli_exec_stdout");
        var output = new StringBuilder();
        foreach (var line in result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var record = ParseObject(line, "malformed_cli_exec_jsonl");
            if (Text(record, "type") == "raw")
            {
                var text = Text(record, "text") ?? throw new ProvisionFault("Unknown", "malformed_cli_exec_raw");
                if (output.Length + text.Length > JsonLimit) throw new ProvisionFault("Unknown", "exec_output_limit");
                output.Append(text);
            }
        }
        return output.ToString();
    }

    private static bool Success(ProvisionProcessResult result) => result.Started && result.ExitCode == 0 && !result.Interrupted && !result.Truncated;
    private static bool NullOrEmptyArray(JsonElement value) => value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0;
    private static string ConfigPath(ProvisionIntent intent) => Path.GetFullPath(Path.Combine(intent.Workspace.Directory, intent.Workspace.ConfigurationPath));
    public static string Hash(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
    private static JsonElement ParseObject(string json, string code)
    {
        try { using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 }); if (document.RootElement.ValueKind == JsonValueKind.Object) return document.RootElement.Clone(); }
        catch (JsonException) { }
        throw new ProvisionFault("Unknown", code);
    }
    private static string? Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Within(string path, string root) => path == root || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    private static string LexicalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path || path.Any(char.IsControl)) throw new ProvisionFault("Failed", "noncanonical_host_path");
        return path;
    }
    private static string CanonicalPath(string path, bool directory)
    {
        LexicalPath(path);
        FileSystemInfo item = directory ? new DirectoryInfo(path) : new FileInfo(path);
        if (!item.Exists) throw new ProvisionFault("Failed", "approved_path_missing");
        for (var current = path; current != "/"; current = Path.GetDirectoryName(current)!)
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ProvisionFault("Failed", "symlink_host_path_not_authorized");
        return path;
    }
    private static bool WorkspacePresent(ProvisionIntent intent)
    {
        try { CanonicalPath(intent.Workspace.Directory, true); return true; }
        catch (Exception error) when (error is ProvisionFault or IOException or UnauthorizedAccessException) { return false; }
    }
    private sealed class ProvisionFault(string state, string code) : Exception(code) { public string State => state; public string Code => Message; }
    private sealed class RunContext(string operationId, Action<ProvisionProgress>? observer)
    {
        private readonly List<ProvisionProgress> progress = [];
        private long sequence;
        public ProvisionIntent? Intent;
        public JsonElement? Configuration;
        public ProvisionContainerObservation? Observed;
        public ProvisionExecutionEvidence? Executed;
        public bool EffectStarted;
        public void Add(string stage, string text)
        {
            var bounded = new string(text.Where(c => !char.IsControl(c) || c is '\n' or '\t').Take(1024).ToArray());
            ProvisionProgress item;
            lock (progress) { if (progress.Count == 64) progress.RemoveAt(0); item = new(++sequence, stage, bounded); progress.Add(item); }
            try { observer?.Invoke(item); } catch { /* advisory progress never invalidates a persisted attempt */ }
        }
        public HostProvisionResult Result(string state, string code)
        {
            var retained = new List<string>();
            if (Intent is not null) retained.Add((WorkspacePresent(Intent) ? "workspace:" : "workspace-missing-or-unverified:") + Intent.Workspace.Directory);
            retained.Add("docker-image-and-build-cache:retained");
            if (Observed is not null) retained.AddRange(Observed.Mounts.Where(x => x.Type == "volume").Select(x => "volume:" + x.VolumeName));
            lock (progress) return new(operationId, state, code, EffectStarted, Intent, Configuration, Observed, Executed, progress.ToImmutableArray(), retained.ToImmutableArray());
        }
    }
    [GeneratedRegex("^[0-9a-f]{40}$")] private static partial Regex Revision();
    [GeneratedRegex("^[0-9a-f]{64}$")] private static partial Regex Digest();
    [GeneratedRegex("^[0-9a-f]{64}$")] private static partial Regex ContainerId();
    [GeneratedRegex("^sha256:[0-9a-f]{64}$")] private static partial Regex ImageId();
    [GeneratedRegex("^[a-z_][a-z0-9_-]{0,31}$")] private static partial Regex UserName();
    [GeneratedRegex("^(--cpus=[0-9]+(\\.[0-9]+)?|--memory=[0-9]+[mg]|--pids-limit=[0-9]+|--network=bridge)$")] private static partial Regex ResourceArgument();
}
