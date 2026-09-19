using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Organization;
using HVO.AgentControl.RemoteWorker;
using HVO.AgentControl.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Hermetic coverage for #259: the deterministic build context, the fixed
/// build/verify command grammar, the contract verifier over captured inspect
/// shapes, the per-host build state machine, and the coordinator driving a
/// scripted host. No SSH, no Docker.
/// </summary>
public sealed class ProfileBuildTests : IDisposable
{
    private const string Base = "sha256:" + "1111111111111111111111111111111111111111111111111111111111111111";
    private const string Built = "sha256:" + "2222222222222222222222222222222222222222222222222222222222222222";
    private static readonly string[] BaseLayers = ["sha256:" + new string('a', 64), "sha256:" + new string('b', 64)];

    private readonly TempDirectory _temp = new();
    private readonly OrganizationStore _store;
    private readonly ContainerProfileRevisionSummary _generic;

    public ProfileBuildTests()
    {
        _store = new OrganizationStore(Path.Combine(_temp.Path, "control.db"));
        _store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        var profile = _store.ListContainerProfiles().Single();
        _generic = _store.GetContainerProfile(profile.Id)!.Revisions.Single();
        _store.RegisterExecutionHost("host-a", "host-a.example", 22, "roys", Path.Combine(_temp.Path, "known_hosts"), new ExecutionHostRegistration("host-a", "host-a", "Host A"));
        var host = _store.GetExecutionHost("host-a")!;
        _store.RecordExecutionHostProbe("host-a", host.Revision, new ExecutionHostProbe("ssh-ed25519", "SHA256:x", "sha256:" + new string('c', 64), "29.0", "1.51", "x86_64", "overlay2", "extfs", false, 50L << 30, 16L << 30, 8, true, "linux/amd64", "valid"));
    }

    public void Dispose() { _store.Dispose(); _temp.Dispose(); }

    // ---------------------------------------------------------------- renderer

    [Fact]
    public void RenderIsDeterministicAndPinsTheBaseThroughTheControllerTag()
    {
        var (tar1, hash1, dockerfile) = ProfileBuildContext.Render(_generic, Base, "linux/amd64");
        var (tar2, hash2, _) = ProfileBuildContext.Render(_generic, Base, "linux/amd64");
        Assert.Equal(tar1, tar2);
        Assert.Equal(hash1, hash2);
        Assert.StartsWith("sha256:", hash1, StringComparison.Ordinal);

        Assert.Contains("FROM agentcontrol-worker-base:pin-111111111111\n", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM agentcontrol-worker-base\n", dockerfile, StringComparison.Ordinal);
        Assert.Contains($"LABEL agentcontrol.profile=\"{_generic.Id}\" agentcontrol.base-digest=\"{Base}\"", dockerfile, StringComparison.Ordinal);
        // Features render to fixed recipes, never to network fetches of feature bundles.
        Assert.Contains("dotnet-sdk-10.0", dockerfile, StringComparison.Ordinal);
        Assert.Contains("python3 python3-venv python3-pip", dockerfile, StringComparison.Ordinal);
        Assert.Contains("install -y --no-install-recommends gh", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("ghcr.io", dockerfile.Replace("# ghcr.io", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("ENV DOTNET_CLI_TELEMETRY_OPTOUT=\"1\"", dockerfile, StringComparison.Ordinal);
        // Fixed trailer re-asserts the worker contract and the entrypoint last.
        Assert.Contains("find / -xdev -perm /6000 -type f -exec chmod a-s", dockerfile, StringComparison.Ordinal);
        Assert.Contains("chmod 0700 /control /home/worker /workspace /session", dockerfile, StringComparison.Ordinal);
        Assert.EndsWith("ENTRYPOINT [\"/usr/local/bin/worker-supervisor\"]\n", dockerfile, StringComparison.Ordinal);

        // The tar is exactly one fixed-metadata Dockerfile entry.
        using var reader = new TarReader(new MemoryStream(tar1));
        var entry = reader.GetNextEntry()!;
        Assert.Equal("Dockerfile", entry.Name);
        Assert.Equal(DateTimeOffset.UnixEpoch, entry.ModificationTime);
        Assert.Equal(0, entry.Uid);
        using var content = new StreamReader(entry.DataStream!);
        Assert.Equal(dockerfile, content.ReadToEnd());
        Assert.Null(reader.GetNextEntry());
        Assert.Equal("sha256:" + Convert.ToHexString(SHA256.HashData(tar1)).ToLowerInvariant(), hash1);
    }

    [Fact]
    public void RenderPlacesTheFragmentBeforeTheTrailerAndRecordsLifecycleAsLabels()
    {
        var revision = NewRevision("""{"build":{"dockerfile":"Dockerfile"},"postCreateCommand":["dotnet","--info"],"postStartCommand":"echo hi","remoteEnv":{"EDITOR":"vim"}}""", "FROM agentcontrol-worker-base\nRUN apt-get update && apt-get install -y jq\nENV PATH=\"$PATH:/opt/x\"\n");
        var dockerfile = ProfileBuildContext.RenderDockerfile(revision, Base, "linux/amd64");
        var fragmentAt = dockerfile.IndexOf("RUN apt-get update && apt-get install -y jq", StringComparison.Ordinal);
        var trailerAt = dockerfile.IndexOf("# --- worker contract trailer (fixed) ---", StringComparison.Ordinal);
        Assert.True(fragmentAt > 0 && trailerAt > fragmentAt);
        // The fragment's own FROM is dropped; the controller's FROM is the only one.
        Assert.Equal(1, dockerfile.Split('\n').Count(l => l.StartsWith("FROM ", StringComparison.Ordinal)));
        Assert.Contains("LABEL agentcontrol.post-create=\"[\\\"dotnet\\\",\\\"--info\\\"]\"", dockerfile, StringComparison.Ordinal);
        Assert.Contains("LABEL agentcontrol.post-start=\"\\\"echo hi\\\"\"", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("RUN dotnet --info", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("RUN echo hi", dockerfile, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"image":"agentcontrol-worker-base","features":{"ghcr.io/devcontainers/features/node:1":{"version":"18"}}}""", "not one this build can honour")]
    [InlineData("""{"image":"agentcontrol-worker-base","features":{"ghcr.io/devcontainers/features/dotnet:2":{"version":"8.0"}}}""", "not one this build can honour")]
    public void RenderRefusesFeatureVersionsTheFixedRecipesCannotHonour(string definition, string expected)
    {
        var revision = NewRevision(definition, null);
        var exception = Assert.Throws<OrganizationValidationException>(() => ProfileBuildContext.RenderDockerfile(revision, Base, "linux/amd64"));
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderRefusesATamperedRevisionAndANonDigestBase()
    {
        var tampered = _generic with { Definition = """{"image":"agentcontrol-worker-base","name":"tampered"}""" };
        Assert.Throws<OrganizationStoreCorruptException>(() => ProfileBuildContext.RenderDockerfile(tampered, Base, "linux/amd64"));
        Assert.Throws<WorkerControlConfigurationException>(() => ProfileBuildContext.RenderDockerfile(_generic, "agentcontrol-worker-base:latest", "linux/amd64"));
        Assert.Throws<WorkerControlConfigurationException>(() => ProfileBuildContext.RenderDockerfile(_generic, Base, "windows/amd64"));
    }

    // ---------------------------------------------------------- command builder

    [Fact]
    public void BuildCommandIsFixedAndStreamsOnlyTheContext()
    {
        var host = LocalHost();
        var options = Options();
        var (tar, hash, _) = ProfileBuildContext.Render(_generic, Base, "linux/amd64");
        var spec = new ImageBuildSpec(Base, "linux/amd64", _generic.Id, hash, ProfileBuildCoordinator.ResultTag(_generic.Id, hash), NetworkRequired: true);
        var command = RemoteWorkerCommandBuilder.BuildImageBuild(host, options, spec, tar);
        Assert.Equal("/usr/bin/ssh", command.Executable);
        Assert.Same(tar, command.StandardInput);
        var remote = command.Arguments[^1];
        Assert.StartsWith("docker build --quiet --pull=false --no-cache --network 'default' --platform 'linux/amd64' --label 'agentcontrol.profile=", remote, StringComparison.Ordinal);
        Assert.EndsWith(" -", remote, StringComparison.Ordinal);
        Assert.Contains($"--label 'agentcontrol.context-hash={hash}'", remote, StringComparison.Ordinal);
        Assert.Contains($"--label 'agentcontrol.base-digest={Base}'", remote, StringComparison.Ordinal);
        Assert.DoesNotContain("--build-arg", remote, StringComparison.Ordinal);
        Assert.DoesNotContain("--secret", remote, StringComparison.Ordinal);
        Assert.DoesNotContain("--cache-from", remote, StringComparison.Ordinal);
        Assert.Contains("-o StrictHostKeyChecking=yes", string.Join(' ', command.Arguments), StringComparison.Ordinal);

        var offline = RemoteWorkerCommandBuilder.BuildImageBuild(host, options, spec with { NetworkRequired = false }, tar).Arguments[^1];
        Assert.Contains("--network 'none'", offline, StringComparison.Ordinal);

        Assert.Throws<WorkerControlConfigurationException>(() => RemoteWorkerCommandBuilder.BuildImageBuild(host, options, spec with { ResultTag = "evil; rm -rf /" }, tar));
        Assert.Throws<WorkerControlConfigurationException>(() => RemoteWorkerCommandBuilder.BuildImageBuild(host, options, spec with { ResultTag = "registry.example/x:y" }, tar));
        Assert.Throws<WorkerControlConfigurationException>(() => RemoteWorkerCommandBuilder.BuildImageBuild(host, options, spec with { ProfileRevisionId = "prev-x" }, tar));
        Assert.Throws<WorkerControlConfigurationException>(() => RemoteWorkerCommandBuilder.BuildImageBuild(host, options, spec, []));
        Assert.Throws<WorkerControlConfigurationException>(() => RemoteWorkerCommandBuilder.BuildImageBuild(host, options, spec, new byte[RemoteWorkerCommandBuilder.MaximumContextBytes + 1]));
    }

    [Fact]
    public void TagVerifyAndRemoveCommandsAreFixed()
    {
        var host = LocalHost(); var options = Options();
        var tag = RemoteWorkerCommandBuilder.Build(host, options, RemoteDockerOperation.ImageTag, [Base, ProfileBuildContext.PinTag(Base)]).Arguments[^1];
        Assert.Equal($"docker image tag '{Base}' 'agentcontrol-worker-base:pin-111111111111'", tag);
        var verify = RemoteWorkerCommandBuilder.Build(host, options, RemoteDockerOperation.ImageVerify, ["agentcontrol-profile:prev-x-abc", "linux/amd64"]).Arguments[^1];
        Assert.StartsWith("docker run --rm --network 'none' --read-only --cap-drop 'ALL' --security-opt 'no-new-privileges' --pids-limit '32' --platform 'linux/amd64' --entrypoint '/usr/bin/python3' 'agentcontrol-profile:prev-x-abc' '-c' 'import base64;exec(base64.b64decode(\"", verify, StringComparison.Ordinal);
        Assert.Contains("bridgeUid", RemoteWorkerCommandBuilder.ImageVerifyScript, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", verify, StringComparison.Ordinal);
        var remove = RemoteWorkerCommandBuilder.Build(host, options, RemoteDockerOperation.ImageRemove, ["agentcontrol-profile:prev-x-abc"]).Arguments[^1];
        Assert.Equal("docker image rm --no-prune 'agentcontrol-profile:prev-x-abc'", remove);
        Assert.Throws<WorkerControlConfigurationException>(() => RemoteWorkerCommandBuilder.Build(host, options, RemoteDockerOperation.ImageTag, ["agentcontrol-worker-base:latest", "x:y"]));
        Assert.Throws<WorkerControlConfigurationException>(() => RemoteWorkerCommandBuilder.Build(host, options, RemoteDockerOperation.ImageVerify, ["x:y", "linux/386"]));
    }

    [Fact]
    public void ContainerCreateAcceptsOnlyTheBaseOrAVerifiedDigestForTheHost()
    {
        var host = LocalHost(); var options = Options();
        var identity = new WorkerResourceIdentity("org", "controller-a", "host-a", "wrk-a", "rtb-a", "op-a");
        var mounts = new[] { new NamedVolumeMount("c", "/control"), new("h", "/home/worker"), new("w", "/workspace"), new("s", "/session") };
        ContainerCreateSpec Spec(string digest, IReadOnlyList<string>? approved) => new("agentcontrol-worker-a", digest, "linux/amd64", identity, mounts, options.MemoryBytes, options.CpuLimit, options.PidsLimit, approved);
        Assert.Contains($"'{Base}'", RemoteWorkerCommandBuilder.BuildContainerCreate(host, options, Spec(Base, null)).Arguments[^1], StringComparison.Ordinal);
        Assert.Throws<WorkerControlConfigurationException>(() => RemoteWorkerCommandBuilder.BuildContainerCreate(host, options, Spec(Built, null)));
        Assert.Throws<WorkerControlConfigurationException>(() => RemoteWorkerCommandBuilder.BuildContainerCreate(host, options, Spec(Built, [Base])));
        Assert.Contains($"'{Built}'", RemoteWorkerCommandBuilder.BuildContainerCreate(host, options, Spec(Built, [Base, Built])).Arguments[^1], StringComparison.Ordinal);
        Assert.Throws<WorkerControlConfigurationException>(() => RemoteWorkerCommandBuilder.BuildContainerCreate(host, options, Spec(Built, [Base, "not-a-digest", Built])));
        // The bootstrap never runs anything but the base.
        Assert.Throws<WorkerControlConfigurationException>(() => RemoteWorkerCommandBuilder.BuildBootstrap(host, options, new BootstrapSpec("c", Built, "linux/amd64", identity), null));
    }

    // ------------------------------------------------------------- verifier

    [Fact]
    public void InspectCheckProvesLineageLabelsEntrypointPlatformAndNoPorts()
    {
        var hash = "sha256:" + new string('d', 64);
        Assert.Equal(Built, ImageContractVerifier.CheckInspect(BuiltInspect(hash), BaseInspect(), _generic.Id, hash, Base, "linux/amd64"));
        Assert.Throws<ImageContractException>(() => ImageContractVerifier.CheckInspect(BuiltInspect(hash, layers: ["sha256:" + new string('z', 64)]), BaseInspect(), _generic.Id, hash, Base, "linux/amd64"));
        Assert.Throws<ImageContractException>(() => ImageContractVerifier.CheckInspect(BuiltInspect(hash, profile: "prev-other"), BaseInspect(), _generic.Id, hash, Base, "linux/amd64"));
        Assert.Throws<ImageContractException>(() => ImageContractVerifier.CheckInspect(BuiltInspect(hash), BaseInspect(), _generic.Id, "sha256:" + new string('e', 64), Base, "linux/amd64"));
        Assert.Throws<ImageContractException>(() => ImageContractVerifier.CheckInspect(BuiltInspect(hash, entrypoint: "/bin/sh"), BaseInspect(), _generic.Id, hash, Base, "linux/amd64"));
        Assert.Throws<ImageContractException>(() => ImageContractVerifier.CheckInspect(BuiltInspect(hash, user: "1102"), BaseInspect(), _generic.Id, hash, Base, "linux/amd64"));
        Assert.Throws<ImageContractException>(() => ImageContractVerifier.CheckInspect(BuiltInspect(hash, ports: true), BaseInspect(), _generic.Id, hash, Base, "linux/amd64"));
        Assert.Throws<ImageContractException>(() => ImageContractVerifier.CheckInspect(BuiltInspect(hash, arch: "arm64"), BaseInspect(), _generic.Id, hash, Base, "linux/amd64"));
        Assert.Throws<ImageContractException>(() => ImageContractVerifier.CheckInspect(BuiltInspect(hash), BaseInspect(id: Built), _generic.Id, hash, Base, "linux/amd64"));
        Assert.Throws<ImageContractException>(() => ImageContractVerifier.CheckInspect("not json", BaseInspect(), _generic.Id, hash, Base, "linux/amd64"));
    }

    [Fact]
    public void RuntimeCheckRequiresTheWorkerContractInsideTheImage()
    {
        ImageContractVerifier.CheckRuntime(VerifyOutput());
        Assert.Contains("uid 1101", Assert.Throws<ImageContractException>(() => ImageContractVerifier.CheckRuntime(VerifyOutput(bridgeUid: 1000))).Message, StringComparison.Ordinal);
        Assert.Contains("/control", Assert.Throws<ImageContractException>(() => ImageContractVerifier.CheckRuntime(VerifyOutput(controlMode: 493))).Message, StringComparison.Ordinal);
        Assert.Contains("setuid", Assert.Throws<ImageContractException>(() => ImageContractVerifier.CheckRuntime(VerifyOutput(setuid: ["/usr/bin/sudo"]))).Message, StringComparison.Ordinal);
        Assert.Contains("worker dll", Assert.Throws<ImageContractException>(() => ImageContractVerifier.CheckRuntime(VerifyOutput(dll: false))).Message, StringComparison.Ordinal);
        Assert.Contains("Docker socket", Assert.Throws<ImageContractException>(() => ImageContractVerifier.CheckRuntime(VerifyOutput(sock: true))).Message, StringComparison.Ordinal);
        Assert.Contains("/app", Assert.Throws<ImageContractException>(() => ImageContractVerifier.CheckRuntime(VerifyOutput(appMode: 511))).Message, StringComparison.Ordinal);
        // Trailing apt noise before the JSON line is tolerated; the last line is the record.
        ImageContractVerifier.CheckRuntime("warning: something\n" + VerifyOutput());
    }

    // ---------------------------------------------------------- state machine

    [Fact]
    public void BuildRowsAreQueuedOncePerRevisionHostAndFoldIntoTheRevision()
    {
        var hash = "sha256:" + new string('d', 64);
        var queued = _store.QueueProfileBuild(_generic.Id, "host-a", Base, "linux/amd64", hash, "agentcontrol-profile:x");
        Assert.Equal(ProfileBuildStates.Queued, queued.State);
        Assert.Equal("building", _store.GetContainerProfile(_generic.ProfileId)!.Profile.CurrentBuildStatus);
        Assert.Throws<OrganizationConcurrencyException>(() => _store.QueueProfileBuild(_generic.Id, "host-a", Base, "linux/amd64", hash, "agentcontrol-profile:x"));
        Assert.Throws<OrganizationConcurrencyException>(() => _store.QueueProfileBuild(_generic.Id, "host-missing", Base, "linux/amd64", hash, "t"));

        var building = _store.TransitionProfileBuild(queued.Id, queued.Revision, ProfileBuildStates.Building);
        Assert.NotNull(building.StartedAt);
        Assert.Throws<OrganizationConcurrencyException>(() => _store.TransitionProfileBuild(queued.Id, queued.Revision, ProfileBuildStates.Verifying)); // stale revision
        Assert.Throws<OrganizationConcurrencyException>(() => _store.TransitionProfileBuild(building.Id, building.Revision, ProfileBuildStates.Built, imageDigest: Built)); // skipping verify
        Assert.Throws<OrganizationValidationException>(() => _store.TransitionProfileBuild(building.Id, building.Revision, ProfileBuildStates.Verifying, verified: true));
        var verifying = _store.TransitionProfileBuild(building.Id, building.Revision, ProfileBuildStates.Verifying);
        Assert.Throws<OrganizationValidationException>(() => _store.TransitionProfileBuild(verifying.Id, verifying.Revision, ProfileBuildStates.Built)); // needs digest
        var built = _store.TransitionProfileBuild(verifying.Id, verifying.Revision, ProfileBuildStates.Built, imageDigest: Built, verified: true, evidenceHash: hash);
        Assert.True(built.Verified);
        Assert.NotNull(built.FinishedAt);

        var revision = _store.GetContainerProfile(_generic.ProfileId)!.Revisions.Single();
        Assert.Equal("built", revision.BuildStatus);
        Assert.Equal(Built, revision.BuiltImageDigest);
        Assert.True(revision.Verified);
        Assert.Equal([Base, Built], _store.ListApprovedImageDigests("host-a", Base));
        Assert.Equal([Base], _store.ListApprovedImageDigests("host-b", Base));
        Assert.Equal(built, _store.GetVerifiedProfileBuild(_generic.Id, "host-a"));
        // A second queue for a verified pair returns the verified build instead of a new row.
        Assert.Equal(built.Id, _store.QueueProfileBuild(_generic.Id, "host-a", Base, "linux/amd64", hash, "agentcontrol-profile:x").Id);
        Assert.Single(_store.ListProfileBuilds(_generic.Id, "host-a"));

        // Identity columns and deletion are guarded at the database boundary.
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = _store.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite }.ToString());
        connection.Open();
        foreach (var sql in new[] { $"UPDATE profile_builds SET context_hash = '{hash}' WHERE id = '{built.Id}'", $"UPDATE profile_builds SET host_id = 'host-b' WHERE id = '{built.Id}'", "DELETE FROM profile_builds", $"INSERT OR REPLACE INTO profile_builds SELECT * FROM profile_builds WHERE id = '{built.Id}'" })
        {
            using var command = connection.CreateCommand(); command.CommandText = sql;
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => command.ExecuteNonQuery());
        }
    }

    [Fact]
    public void RejectedAndFailedBuildsFoldTruthfullyAndNeverJoinTheApprovedSet()
    {
        var hash = "sha256:" + new string('d', 64);
        var queued = _store.QueueProfileBuild(_generic.Id, "host-a", Base, "linux/amd64", hash, "agentcontrol-profile:x");
        var b = _store.TransitionProfileBuild(queued.Id, queued.Revision, ProfileBuildStates.Building);
        var v = _store.TransitionProfileBuild(b.Id, b.Revision, ProfileBuildStates.Verifying);
        var rejected = _store.TransitionProfileBuild(v.Id, v.Revision, ProfileBuildStates.Rejected, failureSummary: "setuid present");
        Assert.Equal("rejected", _store.GetContainerProfile(_generic.ProfileId)!.Revisions.Single().BuildStatus);
        Assert.Equal([Base], _store.ListApprovedImageDigests("host-a", Base));
        Assert.Null(_store.GetVerifiedProfileBuild(_generic.Id, "host-a"));
        // A rejected build is terminal for that row; a new build can be queued.
        Assert.Throws<OrganizationConcurrencyException>(() => _store.TransitionProfileBuild(rejected.Id, rejected.Revision, ProfileBuildStates.Building));
        var again = _store.QueueProfileBuild(_generic.Id, "host-a", Base, "linux/amd64", hash, "agentcontrol-profile:x");
        Assert.NotEqual(rejected.Id, again.Id);
        Assert.Equal(2, _store.ListProfileBuilds(_generic.Id, "host-a").Count);
    }

    // ------------------------------------------------------------ coordinator

    [Fact]
    public async Task CoordinatorBuildsVerifiesAndRecordsADigestFromAScriptedHost()
    {
        var host = new ScriptedHost(_generic.Id);
        var coordinator = Coordinator(host);
        var queued = coordinator.Queue(_generic.Id, "host-a");
        var result = await coordinator.RunAsync(queued.Id, CancellationToken.None);
        Assert.Equal(ProfileBuildStates.Built, result.State);
        Assert.True(result.Verified);
        Assert.Equal(Built, result.ImageDigest);
        Assert.Equal(["ImageTag", "ImageBuild", "ImageInspect", "ImageInspect", "ImageVerify"], host.Operations);
        Assert.Equal(queued.ContextHash, host.BuildSpec!.ContextHash);
        Assert.Equal(ProfileBuildCoordinator.ResultTag(_generic.Id, queued.ContextHash), host.BuildSpec.ResultTag);
        Assert.Equal([Base, Built], _store.ListApprovedImageDigests("host-a", Base));
        // Running the same verified pair again is a no-op through Queue (returns the verified build).
        Assert.Equal(result.Id, coordinator.Queue(_generic.Id, "host-a").Id);
    }

    [Fact]
    public async Task CoordinatorRejectsAContractViolationAndFailsAMissingBase()
    {
        var host = new ScriptedHost(_generic.Id) { VerifyOutputOverride = VerifyOutput(setuid: ["/usr/bin/sudo"]) };
        var coordinator = Coordinator(host);
        var queued = coordinator.Queue(_generic.Id, "host-a");
        var rejected = await coordinator.RunAsync(queued.Id, CancellationToken.None);
        Assert.Equal(ProfileBuildStates.Rejected, rejected.State);
        Assert.Contains("setuid", rejected.FailureSummary, StringComparison.Ordinal);
        Assert.Equal([Base], _store.ListApprovedImageDigests("host-a", Base));

        var missing = new ScriptedHost(_generic.Id) { BaseMissing = true };
        var coordinator2 = Coordinator(missing);
        var queued2 = coordinator2.Queue(_generic.Id, "host-a");
        var failed = await coordinator2.RunAsync(queued2.Id, CancellationToken.None);
        Assert.Equal(ProfileBuildStates.Failed, failed.State);
        Assert.Contains("not present on the host", failed.FailureSummary, StringComparison.Ordinal);
        Assert.Equal(["ImageTag"], missing.Operations);
    }

    [Fact]
    public async Task CoordinatorLeavesATransportLossUncertainAndReconcilesByTag()
    {
        var host = new ScriptedHost(_generic.Id) { BuildTransportLoss = true };
        var coordinator = Coordinator(host);
        var queued = coordinator.Queue(_generic.Id, "host-a");
        var uncertain = await coordinator.RunAsync(queued.Id, CancellationToken.None);
        Assert.Equal(ProfileBuildStates.Uncertain, uncertain.State);
        Assert.Equal("building", _store.GetContainerProfile(_generic.ProfileId)!.Revisions.Single().BuildStatus);
        Assert.Throws<OrganizationConcurrencyException>(() => coordinator.Queue(_generic.Id, "host-a")); // no second live build

        // Reconcile: the image turned out to exist on the host → verify it.
        host.BuildTransportLoss = false;
        var reconciled = await coordinator.RunAsync(uncertain.Id, CancellationToken.None);
        Assert.Equal(ProfileBuildStates.Built, reconciled.State);
        Assert.True(reconciled.Verified);

        // Reconcile the other way: nothing on the host → failed, never retried blindly.
        var host2 = new ScriptedHost(_generic.Id) { BuildTransportLoss = true };
        var revision2 = NewRevision("""{"image":"agentcontrol-worker-base","name":"two"}""", null);
        var coordinator2 = Coordinator(host2);
        var q2 = coordinator2.Queue(revision2.Id, "host-a");
        var u2 = await coordinator2.RunAsync(q2.Id, CancellationToken.None);
        host2.BuildTransportLoss = false; host2.ImageMissing = true;
        var f2 = await coordinator2.RunAsync(u2.Id, CancellationToken.None);
        Assert.Equal(ProfileBuildStates.Failed, f2.State);
        Assert.Contains("no image carries the build's tag", f2.FailureSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("ImageBuild", host2.Operations.Skip(2));
    }

    [Fact]
    public void CoordinatorRefusesWhenDisabledOrHostUnapproved()
    {
        var disabled = new ProfileBuildCoordinator(Control(), new ScriptedHost(_generic.Id), Microsoft.Extensions.Options.Options.Create(Options(enabled: false)), NullLogger<ProfileBuildCoordinator>.Instance);
        Assert.Throws<WorkerControlDisabledException>(() => disabled.Queue(_generic.Id, "host-a"));
        Assert.Throws<KeyNotFoundException>(() => Coordinator(new ScriptedHost(_generic.Id)).Queue(_generic.Id, "host-z"));
        Assert.Throws<OrganizationNotFoundException>(() => Coordinator(new ScriptedHost(_generic.Id)).Queue("prev-0000000000000000", "host-a"));
    }

    // ---------------------------------------------------------------- helpers

    private ContainerProfileRevisionSummary NewRevision(string definition, string? fragment)
    {
        var profile = _store.CreateContainerProfile(new ContainerProfileCreate(Guid.NewGuid().ToString("N"), "p-" + Guid.NewGuid().ToString("N")[..8], "P", null, definition, fragment), null);
        return _store.GetContainerProfile(profile.Id)!.Revisions.Single();
    }

    private ProfileBuildCoordinator Coordinator(IRemoteWorkerOperations operations) =>
        new(Control(), operations, Microsoft.Extensions.Options.Options.Create(Options()), NullLogger<ProfileBuildCoordinator>.Instance);

    private AcpControlHost Control()
    {
        var options = new ControlOptions { DataDirectory = _temp.Path, PrivateDataDirectory = Path.Combine(_temp.Path, "private") };
        var control = new AcpControlHost(Microsoft.Extensions.Options.Options.Create(options), NullLogger<AcpControlHost>.Instance);
        typeof(AcpControlHost).GetField("_organization", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(control, _store);
        return control;
    }

    private WorkerControlOptions Options(bool enabled = true)
    {
        var known = Path.Combine(_temp.Path, "known_hosts"); var key = Path.Combine(_temp.Path, "id");
        if (!File.Exists(known))
        {
            File.WriteAllText(known, "host-a.example ssh-ed25519 AAAA\n"); File.WriteAllText(key, "k");
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(known, UnixFileMode.UserRead | UnixFileMode.UserWrite); File.SetUnixFileMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        }
        return new WorkerControlOptions
        {
            Enabled = enabled,
            ControllerId = "controller-a",
            ApprovedImageDigest = Base,
            ApprovedImagePlatform = "linux/amd64",
            ExpectedControllerUid = ControllerPrivateFile.EffectiveUid,
            ApprovedHosts = [new ApprovedExecutionHost { Id = "host-a", Hostname = "host-a.example", Port = 22, Username = "roys", KnownHostsPath = known, IdentityFilePath = key }],
        };
    }

    private static ApprovedExecutionHost LocalHost() => new() { Id = "host-a", Hostname = "host-a.example", Port = 22, Username = "roys", KnownHostsPath = "/dev/null", IdentityFilePath = "/dev/zero" };

    private static string BaseInspect(string id = Base) => JsonSerializer.Serialize(new[] { new { Id = id, Os = "linux", Architecture = "amd64", Config = new { Labels = (object?)null, Entrypoint = new[] { "/usr/local/bin/worker-supervisor" } }, RootFS = new { Type = "layers", Layers = BaseLayers } } });

    private string BuiltInspect(string contextHash, string? profile = null, string[]? layers = null, string entrypoint = "/usr/local/bin/worker-supervisor", string user = "", bool ports = false, string arch = "amd64") =>
        JsonSerializer.Serialize(new[] { new
        {
            Id = Built, Os = "linux", Architecture = arch,
            Config = new
            {
                Labels = new Dictionary<string, string> { [ProfileBuildContext.ProfileLabel] = profile ?? _generic.Id, [ProfileBuildContext.ContextHashLabel] = contextHash, [ProfileBuildContext.BaseDigestLabel] = Base },
                Entrypoint = new[] { entrypoint }, User = user,
                ExposedPorts = ports ? new Dictionary<string, object> { ["8080/tcp"] = new { } } : null,
            },
            RootFS = new { Type = "layers", Layers = (layers ?? [.. BaseLayers, "sha256:" + new string('f', 64)]) },
        } });

    private static string VerifyOutput(int bridgeUid = 1101, int controlMode = 448, string[]? setuid = null, bool dll = true, bool sock = false, int appMode = 493) =>
        JsonSerializer.Serialize(new
        {
            bridgeUid,
            employeeUid = 1102,
            dirs = new Dictionary<string, object>
            {
                ["/control"] = new { uid = 1101, gid = 1101, mode = controlMode },
                ["/home/worker"] = new { uid = 1102, gid = 1102, mode = 448 },
                ["/workspace"] = new { uid = 1102, gid = 1102, mode = 448 },
                ["/session"] = new { uid = 1102, gid = 1102, mode = 448 },
            },
            app = new { uid = 0, gid = 0, mode = appMode },
            supervisor = new { uid = 0, gid = 0, mode = 493 },
            workerDll = dll,
            dotnet = true,
            opencode = true,
            setuid = setuid ?? [],
            dockerSock = sock,
        }) + "\n";

    /// <summary>A host that answers the fixed operations the way a real one would, with scripted faults.</summary>
    private sealed class ScriptedHost(string revisionId) : IRemoteWorkerOperations
    {
        public List<string> Operations { get; } = [];
        public ImageBuildSpec? BuildSpec { get; private set; }
        public bool BaseMissing { get; init; }
        public bool BuildTransportLoss { get; set; }
        public bool ImageMissing { get; set; }
        public string? VerifyOutputOverride { get; init; }
        private string? _contextHash;

        public Task<RemoteOperationResult> ExecuteAsync(ApprovedExecutionHost host, RemoteDockerOperation operation, IReadOnlyList<string> tokens, byte[]? standardInput, CancellationToken cancellationToken)
        {
            Operations.Add(operation.ToString());
            return Task.FromResult(operation switch
            {
                RemoteDockerOperation.ImageTag => BaseMissing ? new RemoteOperationResult(1, "", "not-found") : new(0, "", "none"),
                RemoteDockerOperation.ImageInspect when tokens[0] == Base => new(0, BaseInspect(), "none"),
                RemoteDockerOperation.ImageInspect => ImageMissing ? new(1, "", "not-found") : new(0, BuiltInspectFor(_contextHash ?? "sha256:" + new string('0', 64)), "none"),
                RemoteDockerOperation.ImageVerify => new(0, VerifyOutputOverride ?? VerifyOutput(), "none"),
                _ => throw new NotSupportedException(operation.ToString()),
            });
        }

        public Task<RemoteOperationResult> BuildImageAsync(ApprovedExecutionHost host, ImageBuildSpec specification, byte[] contextTar, CancellationToken cancellationToken)
        {
            Operations.Add("ImageBuild");
            BuildSpec = specification;
            _contextHash = specification.ContextHash;
            Assert.Equal("sha256:" + Convert.ToHexString(SHA256.HashData(contextTar)).ToLowerInvariant(), specification.ContextHash);
            return Task.FromResult(BuildTransportLoss ? new RemoteOperationResult(255, "", "transport") : new(0, Built + "\n", "none"));
        }

        private string BuiltInspectFor(string contextHash) => JsonSerializer.Serialize(new[] { new
        {
            Id = Built, Os = "linux", Architecture = "amd64",
            Config = new { Labels = new Dictionary<string, string> { [ProfileBuildContext.ProfileLabel] = revisionId, [ProfileBuildContext.ContextHashLabel] = contextHash, [ProfileBuildContext.BaseDigestLabel] = Base }, Entrypoint = new[] { "/usr/local/bin/worker-supervisor" }, User = "" },
            RootFS = new { Type = "layers", Layers = new[] { BaseLayers[0], BaseLayers[1], "sha256:" + new string('f', 64) } },
        } });

        public Task<RemoteOperationResult> CreateVolumeAsync(ApprovedExecutionHost host, VolumeCreateSpec specification, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RemoteOperationResult> CreateContainerAsync(ApprovedExecutionHost host, ContainerCreateSpec specification, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RemoteOperationResult> BootstrapAsync(ApprovedExecutionHost host, BootstrapSpec specification, byte[] standardInput, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<HostProbePayload> ProbeHostAsync(ApprovedExecutionHost host, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agentcontrol-builds-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }
}
