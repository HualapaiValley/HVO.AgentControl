using System.Diagnostics;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Exercises <c>scripts/init-secrets.py</c> without Docker. The container-side
/// initializer is loaded with <c>runpy</c> (the <c>__main__</c> guard keeps the
/// Docker calls from running) and then executed against a temporary directory to
/// prove atomic, non-overwriting publication and that the credential is never
/// printed.
/// </summary>
public sealed class SecretInitializationTests
{
    /// <summary>Controller identity that must own the owner secret.</summary>
    private const int ControlUid = 1001;
    private const int ControlGid = 1001;

    [Fact]
    public void ScriptIsSplitIntoGuardAndRemoteInitializer()
    {
        var script = RequireScript();
        var text = File.ReadAllText(script);

        Assert.Contains("if __name__ == \"__main__\"", text, StringComparison.Ordinal);
        Assert.Contains("REMOTE_CODE", text, StringComparison.Ordinal);
        Assert.Contains("stream.write(secrets.token_urlsafe(32)", text, StringComparison.Ordinal);
        Assert.Contains("os.chmod(temp_path, 0o600)", text, StringComparison.Ordinal);
        Assert.Contains("os.chown(temp_path, control_uid, control_gid)", text, StringComparison.Ordinal);
        Assert.Contains("os.link(temp_path, path)", text, StringComparison.Ordinal);
        Assert.Contains("refusing to overwrite", text, StringComparison.Ordinal);

        // The controller identity, not the shared pre-isolation UID 1000.
        Assert.Contains("CONTROL_UID = 1001", text, StringComparison.Ordinal);
        Assert.Contains("MIGRATE_CODE", text, StringComparison.Ordinal);
        Assert.Contains("--migrate-owner", text, StringComparison.Ordinal);

        // The rollback half of the migration, without which the forward step is
        // a one-way door.
        Assert.Contains("REVERT_CODE", text, StringComparison.Ordinal);
        Assert.Contains("--revert-isolation", text, StringComparison.Ordinal);

        // ...and the roll-forward half, without which a rollback is a one-way
        // door in the other direction.
        Assert.Contains("RESUME_CODE", text, StringComparison.Ordinal);
        Assert.Contains("--resume-isolation", text, StringComparison.Ordinal);
        Assert.Contains("--state-source", text, StringComparison.Ordinal);

        // The CLIProxy key provisioner reads stdin only and never prints the
        // value or a fingerprint.
        Assert.Contains("PROVISION_CODE", text, StringComparison.Ordinal);
        Assert.Contains("--provision-cliproxy-key", text, StringComparison.Ordinal);
        Assert.Contains("CLIPROXY_TARGET = \"/secrets/cliproxy-api-key\"", text, StringComparison.Ordinal);
        Assert.Contains("sys.stdin.buffer.read()", text, StringComparison.Ordinal);
        Assert.Contains("interactive=interactive", text, StringComparison.Ordinal);
        Assert.DoesNotContain("print(key", text, StringComparison.Ordinal);
        Assert.DoesNotContain("print(payload", text, StringComparison.Ordinal);

        var provision = ExtractBlock("PROVISION_CODE");
        Assert.Contains("minimum = int(sys.argv[5])", provision, StringComparison.Ordinal);
        Assert.Contains("target_name = sys.argv[2]", provision, StringComparison.Ordinal);
        Assert.Contains("os.stat(target_name, dir_fd=directory_fd", provision, StringComparison.Ordinal);
        Assert.Contains("os.open(target_name,", provision, StringComparison.Ordinal);
        Assert.Contains("os.replace(temporary, target_name,", provision, StringComparison.Ordinal);
        Assert.DoesNotContain("os.stat(target, dir_fd=directory_fd", provision, StringComparison.Ordinal);
        Assert.DoesNotContain("os.open(target,", provision, StringComparison.Ordinal);
        Assert.DoesNotContain("os.replace(temporary, target,", provision, StringComparison.Ordinal);
        Assert.Contains(
            "arguments = [\"/secrets\", \"cliproxy-api-key\", CONTROL_UID, CONTROL_GID, CLIPROXY_MINIMUM_LENGTH]",
            text,
            StringComparison.Ordinal);
        Assert.Contains("public const int MinimumCliProxySecretLength = 16", File.ReadAllText(RequireControlOptions()), StringComparison.Ordinal);

        // The credential is written to the file stream, never printed.
        Assert.DoesNotContain("print(secrets.token_urlsafe", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Volume and container names follow the Compose project, so a deployment
    /// under a different project name can be operated without editing the
    /// script. A malformed project would silently produce names that do not
    /// exist, and the rollback would then report success against nothing.
    /// </summary>
    [Fact]
    public void VolumeNamesAreDerivedFromTheComposeProject()
    {
        var python = RequirePython();
        if (python is null || OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TempDirectory();
        var result = RunWithFakeDocker(
            python,
            directory,
            inspect: new(0, "false"),
            arguments: ["--revert-isolation", "--project", "tenant-b"]);

        Assert.Equal(0, result.ExitCode);
        var calls = DockerCalls(directory);
        Assert.Contains("tenant-b_control-data", calls, StringComparison.Ordinal);
        Assert.Contains("tenant-b_control-private", calls, StringComparison.Ordinal);
        Assert.Contains("inspect --format {{json .State.Running}} tenant-b-control-1", calls, StringComparison.Ordinal);

        // The owner secret volume is declared `external` in compose.yaml, so it
        // is not project-scoped and must not be renamed with the project.
        Assert.Contains("agentcontrol-v2-secrets", calls, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Bad Project")]
    [InlineData("-leading-dash")]
    [InlineData("has/slash")]
    [InlineData("../escape")]
    [InlineData("")]
    public void AMalformedComposeProjectIsRejected(string project)
    {
        var python = RequirePython();
        if (python is null)
        {
            return;
        }

        // `--project=value` rather than two arguments, so a value that begins
        // with a dash reaches the validation instead of being taken as a flag.
        var result = Run(python, [RequireScript(), "--revert-isolation", $"--project={project}"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--project must start with", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// `docker run` creates a missing named volume silently. Acting on an empty
    /// volume that was just conjured into existence would report a confident
    /// success while the real state sits untouched under another project name.
    /// </summary>
    [Fact]
    public void MissingVolumesFailBeforeAnyContainerRuns()
    {
        var python = RequirePython();
        if (python is null || OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TempDirectory();
        var result = RunWithFakeDocker(
            python,
            directory,
            inspect: new(0, "false"),
            volumeInspect: new(1, StandardError: "Error: No such volume"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("these volumes do not exist", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("--project", result.StandardError, StringComparison.Ordinal);

        // Nothing was mounted or executed against a volume.
        Assert.DoesNotContain(
            "run --rm",
            DockerCalls(directory),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Rolling forward after a rollback must never infer which of the two
    /// diverged runtime states survives. The choice is the operator's, and the
    /// script refuses to run without it.
    /// </summary>
    [Fact]
    public void ResumeRequiresAnExplicitStateSource()
    {
        var python = RequirePython();
        if (python is null)
        {
            return;
        }

        var missing = Run(python, [RequireScript(), "--resume-isolation"]);
        Assert.NotEqual(0, missing.ExitCode);
        Assert.Contains("requires --state-source", missing.StandardError, StringComparison.Ordinal);

        var orphan = Run(python, [RequireScript(), "--state-source", "legacy"]);
        Assert.NotEqual(0, orphan.ExitCode);
        Assert.Contains("only meaningful with --resume-isolation", orphan.StandardError, StringComparison.Ordinal);

        var invalid = Run(python, [RequireScript(), "--resume-isolation", "--state-source", "merge"]);
        Assert.NotEqual(0, invalid.ExitCode);
        Assert.Contains("invalid choice", invalid.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// The resume runs against the same stopped-container precondition as the
    /// rollback: it rewrites the controller-private state, which a live
    /// controller owns.
    /// </summary>
    [Fact]
    public void ResumeRefusesWhileTheControlContainerIsRunning()
    {
        var python = RequirePython();
        if (python is null || OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TempDirectory();
        var result = RunWithFakeDocker(
            python,
            directory,
            inspect: new(0, "true"),
            arguments: ["--resume-isolation", "--state-source", "private"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("stop it before resuming", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "run --rm",
            DockerCalls(directory),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Migration and rollback must be mutually exclusive: running both in one
    /// invocation has no coherent meaning and would end in whichever ownership
    /// the last branch happened to apply.
    /// </summary>
    [Fact]
    public void MigrateAndRevertCannotBeRequestedTogether()
    {
        var python = RequirePython();
        if (python is null)
        {
            return;
        }

        var result = Run(python, new[] { RequireScript(), "--migrate-owner", "--revert-isolation" });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not allowed with argument", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// The owner secret stays metadata only during a rollback: ownership and
    /// mode change, the inode and bytes do not. A rollback that rewrote or
    /// rotated the credential would invalidate a deployed password.
    /// </summary>
    [Fact]
    public void RevertNeverReadsRewritesOrRotatesTheSecret()
    {
        var revert = ExtractBlock("REVERT_CODE");

        // Symlink-safe, hard-link-checked, descriptor-based - same rules as the
        // forward migration, because the volume is equally attacker-influenced.
        Assert.Contains("os.O_NOFOLLOW", revert, StringComparison.Ordinal);
        Assert.Contains("st_nlink != 1", revert, StringComparison.Ordinal);
        Assert.Contains("hand_back_metadata(secret_fd, secret_path", revert, StringComparison.Ordinal);
        Assert.Contains("after.st_ino != before.st_ino", revert, StringComparison.Ordinal);

        // No rotation and no deletion anywhere in the rollback.
        Assert.DoesNotContain("secrets.token_urlsafe", revert, StringComparison.Ordinal);
        Assert.DoesNotContain("os.unlink(secret", revert, StringComparison.Ordinal);
        Assert.DoesNotContain("shutil", revert, StringComparison.Ordinal);
    }

    /// <summary>
    /// The runtime state is the one thing a rollback cannot treat as metadata.
    /// The isolated controller writes only <c>/control-data/runtime.json</c>, so
    /// by rollback time <c>/data/runtime.json</c> holds the organization,
    /// session and tmux owner token as they were at adoption. Handing that stale
    /// file back would resume the old image on a dead session and silently lose
    /// the isolated run, so the rollback republishes the current private bytes.
    /// </summary>
    [Fact]
    public void RevertRepublishesTheCurrentPrivateStateRatherThanReOwningAStaleFile()
    {
        var revert = ExtractBlock("REVERT_CODE");

        // The private state is the source, the legacy path is the destination.
        // The publication is root-owned; the hand-back to the legacy identity is
        // a separate step after the marker, not an attribute of the new inode.
        Assert.Contains("payload = read_all(private_fd)", revert, StringComparison.Ordinal);
        Assert.Contains(
            "publish_bytes(\n            data_fd, data_root, state_name, payload, 0, 0, 0o600\n        )",
            revert,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "data_fd, data_root, state_name, payload, legacy_uid, legacy_gid",
            revert,
            StringComparison.Ordinal);

        // Atomic and symlink-safe: O_EXCL temporary in the destination directory,
        // final ownership/mode before it is visible, fsync, renameat by dir_fd,
        // then an fsync of the directory itself.
        Assert.Contains("os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW", revert, StringComparison.Ordinal);
        Assert.Contains("os.fchown(fd, uid, gid)", revert, StringComparison.Ordinal);
        Assert.Contains("os.fsync(fd)", revert, StringComparison.Ordinal);
        Assert.Contains(
            "os.replace(temporary, name, src_dir_fd=directory_fd, dst_dir_fd=directory_fd)",
            revert,
            StringComparison.Ordinal);
        Assert.Contains("os.fsync(directory_fd)", revert, StringComparison.Ordinal);

        // The rollback marker is what stops a later roll-forward from silently
        // choosing between two diverged states.
        Assert.Contains("rollback.active", revert, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every source path is owner-checked before anything changes. An operator
    /// rollback must not hand an unexpected inode to UID 1000, and the allowed
    /// sets deliberately include the already-reverted owners so that re-running
    /// an interrupted rollback is accepted rather than refused.
    /// </summary>
    [Fact]
    public void RevertValidatesOwnersAndIsReplayable()
    {
        var revert = ExtractBlock("REVERT_CODE");

        Assert.Contains("allowed_owners=(control_uid, legacy_uid, 0)", revert, StringComparison.Ordinal);
        Assert.Contains("allowed_owners=(0, legacy_uid)", revert, StringComparison.Ordinal);
        Assert.Contains("allowed_owners=(control_uid, 0)", revert, StringComparison.Ordinal);
        Assert.Contains("is owned by UID %d; expected one of", revert, StringComparison.Ordinal);

        // The private store is never written by the rollback; it stays the
        // authoritative record of the isolated run.
        Assert.DoesNotContain(
            "publish_bytes(private_dir_fd, private_root, state_name",
            revert,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The rollback marker must be durable before the runtime state is
    /// republished and before every ownership hand-back. It is both the
    /// roll-forward interlock and the evidence a later replay classifies
    /// against, so any effect the pre-isolation identity can observe before the
    /// marker exists is an unbounded window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ordering the <c>chown</c> calls after the marker was not sufficient. The
    /// republish creates a new inode and renames it over
    /// <c>/data/runtime.json</c>, and it used to give that inode its
    /// <c>1000:1000</c> ownership at creation time - so the publication itself
    /// was an ownership hand-back that ran before the marker was written. A
    /// crash in between left a legacy state the old image could read and
    /// advance with no record of it, and the next replay, finding no marker,
    /// treated the advanced state as the ordinary first rollback and overwrote
    /// it. The publication is therefore root-owned and the hand-back is a
    /// separate, explicit step.
    /// </para>
    /// <para>
    /// Asserted on order in the source because the container tests that
    /// exercise it need a Docker daemon. Both cover the same property; this one
    /// cannot be skipped.
    /// </para>
    /// </remarks>
    [Fact]
    public void RevertPublishesTheMarkerBeforeTheRepublishAndEveryHandBack()
    {
        var revert = ExtractBlock("REVERT_CODE");

        var marker = revert.IndexOf(
            "write_marker(\"intent\", None, None, intended_digest, intended_bytes)",
            StringComparison.Ordinal);
        Assert.True(marker > 0, "the intent marker publication was not found");

        // The republish itself is an effect the old image can observe, so it
        // must follow the marker, not precede it.
        var republish = revert.IndexOf(
            "published_digest = publish_bytes(\n            data_fd, data_root, state_name, payload, 0, 0, 0o600\n        )",
            StringComparison.Ordinal);
        Assert.True(
            republish > 0,
            "the runtime-state republish was not found, or no longer publishes root-owned. "
            + "Publishing it already owned by the legacy UID is an ownership hand-back before "
            + "the marker exists.");
        Assert.True(
            republish > marker,
            "the runtime state is republished before the rollback marker is recorded, which "
            + "leaves the pre-isolation image able to read and advance a state with no record "
            + "of the publication.");

        foreach (var handBack in new[]
        {
            "hand_back_metadata(published_fd, legacy_state, legacy_uid, legacy_gid, 0o600)",
            "hand_back_metadata(legacy_fd, legacy_state, legacy_uid, legacy_gid, 0o600)",
            "hand_back_metadata(data_fd, data_root, legacy_uid, legacy_gid, 0o755)",
            "hand_back_metadata(secret_fd, secret_path, legacy_uid, legacy_gid, 0o600)",
        })
        {
            var index = revert.IndexOf(handBack, StringComparison.Ordinal);
            Assert.True(index > 0, $"'{handBack}' was not found in the rollback");
            Assert.True(
                index > marker,
                $"'{handBack}' runs before the rollback marker is recorded, which leaves the "
                + "pre-isolation image runnable with no record of the publication.");
            Assert.True(
                index > republish,
                $"'{handBack}' runs before the republish, so ownership would move to the legacy "
                + "identity on a state this rollback has not finished writing.");
        }

        // The marker is completed after the hand-backs, so a crash in that
        // window leaves the intent marker the replay knows how to finish.
        var complete = revert.IndexOf(
            "write_marker(\"complete\", published_digest, published_bytes",
            StringComparison.Ordinal);
        Assert.True(complete > 0, "the completion marker was not found");
        Assert.True(
            complete > revert.IndexOf(
                "hand_back_metadata(secret_fd, secret_path, legacy_uid, legacy_gid, 0o600)",
                StringComparison.Ordinal),
            "the marker is completed before the hand-backs, so it would claim a rollback that "
            + "had not happened yet");
    }

    /// <summary>
    /// A republish replaces the inode, so the descriptor opened during
    /// validation refers to a superseded one. Handing <em>that</em> back would
    /// give UID 1000 an unreachable orphan while the file the old image actually
    /// reads stayed root-owned — a rollback that reports success and leaves the
    /// old image unable to read its own state.
    /// </summary>
    [Fact]
    public void RevertHandsBackThePublishedInodeRatherThanTheStaleDescriptor()
    {
        var revert = ExtractBlock("REVERT_CODE");

        // After a republish the published name is re-opened by directory
        // descriptor, under the same no-follow/hard-link/owner rules...
        Assert.Contains(
            "published_fd = open_checked(\n            legacy_state, allowed_owners=(0,), parent_fd=data_fd, name=state_name\n        )",
            revert,
            StringComparison.Ordinal);

        // ...its bytes are confirmed to be the ones just published...
        Assert.Contains(
            "if hashlib.sha256(read_all(published_fd)).hexdigest() != published_digest:",
            revert,
            StringComparison.Ordinal);

        // ...and only that descriptor is handed back. The stale `legacy_fd` is
        // used only on the path where nothing was republished.
        Assert.Contains(
            "hand_back_metadata(published_fd, legacy_state, legacy_uid, legacy_gid, 0o600)",
            revert,
            StringComparison.Ordinal);
        Assert.Contains(
            "elif legacy_fd is not None:\n        hand_back_metadata(legacy_fd, legacy_state, legacy_uid, legacy_gid, 0o600)",
            revert,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The replay guard has to classify three distinct situations, because
    /// "this is not what we published" and "we never got as far as publishing"
    /// demand opposite actions. Collapsing them either refuses a recoverable
    /// interrupted rollback or overwrites a legacy state another writer
    /// advanced.
    /// </summary>
    [Fact]
    public void RevertClassifiesTheReplayAgainstBothRecordedDigests()
    {
        var revert = ExtractBlock("REVERT_CODE");

        // Recorded before the publication: proves the publication did not run.
        Assert.Contains(
            "elif recorded_prepublication is not None and legacy_digest == recorded_prepublication:",
            revert,
            StringComparison.Ordinal);

        // Recorded as the intent: proves the publication did run, so the inode
        // must not be replaced.
        Assert.Contains(
            "elif recorded_intended is not None and legacy_digest == recorded_intended:",
            revert,
            StringComparison.Ordinal);
        Assert.Contains("already_published = True", revert, StringComparison.Ordinal);

        // Neither: refuse before the first write or chown. The message is
        // wrapped in the source, so match the distinguishing clause.
        Assert.Contains("neither the state recorded before the publication", revert, StringComparison.Ordinal);
        Assert.Contains("rollback intended to publish", revert, StringComparison.Ordinal);

        // Both digests are written, and the phase distinguishes an intent marker
        // from a completed one.
        foreach (var field in new[]
        {
            "\"prepublicationLegacySha256\": legacy_digest,",
            "\"intendedSha256\": intended_digest,",
            "\"publishedSha256\": published_digest,",
            "\"phase\": phase,",
        })
        {
            Assert.Contains(field, revert, StringComparison.Ordinal);
        }

        Assert.Contains("write_marker(\"intent\"", revert, StringComparison.Ordinal);
        Assert.Contains("write_marker(\"complete\"", revert, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two markers share a file name and are told apart by
    /// <c>reason</c>, explicitly. <c>prepare-layout.py</c>'s
    /// <c>unrecorded-legacy-divergence</c> marker records a divergence that was
    /// only detected — nothing was written to the legacy path — so reading its
    /// <c>legacySha256</c> as a publication record would republish the current
    /// private state over exactly the bytes the operator is rolling back to
    /// keep.
    /// </summary>
    [Fact]
    public void TheTwoMarkerKindsAreDistinguishedByReasonRatherThanInferred()
    {
        var revert = ExtractBlock("REVERT_CODE");
        var layout = File.ReadAllText(Path.Combine(
            ControllerIsolationLayoutTests.RepositoryRoot(), "src", "container", "prepare-layout.py"));

        // The rollback branches on the reason before it looks at any digest.
        var divergence = revert.IndexOf(
            "if recorded_reason == \"unrecorded-legacy-divergence\":", StringComparison.Ordinal);
        Assert.True(divergence > 0, "the rollback does not branch on the divergence reason");
        Assert.True(
            divergence < revert.IndexOf(
                "elif recorded_intended is not None", StringComparison.Ordinal),
            "the divergence marker is classified after the publication digests, so a detection "
            + "could be treated as a resumable publication");

        // Under that reason the diverged bytes are kept, never republished over.
        Assert.Contains("keep_legacy_bytes = True", revert, StringComparison.Ordinal);

        // And the entrypoint's marker records no publication or intent at all.
        Assert.Contains("\"reason\": \"unrecorded-legacy-divergence\",", layout, StringComparison.Ordinal);
        foreach (var nulled in new[]
        {
            "\"prepublicationLegacySha256\": None,",
            "\"intendedSha256\": None,",
            "\"publishedSha256\": None,",
        })
        {
            Assert.Contains(nulled, layout, StringComparison.Ordinal);
        }

        // Both markers carry a schema so a future field change is detectable.
        Assert.Contains("\"schema\": 2,", layout, StringComparison.Ordinal);
        Assert.Contains("\"schema\": 2,", revert, StringComparison.Ordinal);
    }

    /// <summary>
    /// A marker version defines the recovery semantics. Neither rollback replay
    /// nor explicit resume may interpret or remove a marker from another schema.
    /// </summary>
    [Fact]
    public void RollbackAndResumeRequireTheExactSupportedMarkerSchema()
    {
        foreach (var (name, body, objectName) in new[]
        {
            ("REVERT_CODE", ExtractBlock("REVERT_CODE"), "recorded"),
            ("RESUME_CODE", ExtractBlock("RESUME_CODE"), "recorded_marker"),
        })
        {
            Assert.Contains($"{objectName}.get(\"schema\")", body, StringComparison.Ordinal);
            Assert.Contains("type(recorded_schema) is not int", body, StringComparison.Ordinal);
            Assert.Contains("recorded_schema != 2", body, StringComparison.Ordinal);
            Assert.Contains("unsupported rollback marker schema", body, StringComparison.Ordinal);
        }

        var resume = ExtractBlock("RESUME_CODE");
        Assert.Contains("Refusing to resolve or remove", resume, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "resolving it from the explicit --state-source choice",
            resume,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// "The other state is preserved, never deleted" has to survive a name
    /// collision. The archive name carries a one-second timestamp, so two
    /// resumes inside one second - or after a clock step - pick the same name;
    /// replacing what is there would destroy the first archive while reporting
    /// that both were preserved.
    /// </summary>
    [Fact]
    public void ResumeArchivesAreCreatedNeverReplaced()
    {
        var resume = ExtractBlock("RESUME_CODE");

        // Both archive kinds go through the create-never-replace helper...
        Assert.Contains("\"superseded\",", resume, StringComparison.Ordinal);
        Assert.Contains("\"rolled-back\",", resume, StringComparison.Ordinal);
        Assert.Equal(2, Occurrences(resume, "name = archive_bytes("));

        // ...which claims the name with link (EEXIST), never replace/rename.
        Assert.Contains("os.link(", resume, StringComparison.Ordinal);
        Assert.Contains("except FileExistsError:", resume, StringComparison.Ordinal);
        var archive = resume[resume.IndexOf("def archive_bytes(", StringComparison.Ordinal)..];
        var archiveBody = archive[..archive.IndexOf("\nprivate_dir_fd", StringComparison.Ordinal)];
        Assert.DoesNotContain("os.replace(", archiveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("O_TRUNC", archiveBody, StringComparison.Ordinal);

        // The old formatting, which built the name and published over it, must
        // not come back through a later edit.
        Assert.DoesNotContain("\"runtime.superseded-%s.json\" % stamp", resume, StringComparison.Ordinal);
        Assert.DoesNotContain("\"runtime.rolled-back-%s.json\" % stamp", resume, StringComparison.Ordinal);
    }

    /// <summary>
    /// Temporary names must not be derived from the PID. A container restart
    /// reuses low PIDs, so a temporary left behind by an interrupted run would
    /// make the <c>O_EXCL</c> create fail on exactly the same name at every
    /// later attempt - a permanent failure repaired only by hand - and none of
    /// this code may unlink or truncate an entry it did not create.
    /// </summary>
    [Fact]
    public void PublicationTemporariesUseARandomSuffixRatherThanThePid()
    {
        var sources = new List<(string Name, string Body)>
        {
            ("REVERT_CODE", ExtractBlock("REVERT_CODE")),
            ("RESUME_CODE", ExtractBlock("RESUME_CODE")),
            ("src/container/prepare-layout.py", File.ReadAllText(Path.Combine(
                ControllerIsolationLayoutTests.RepositoryRoot(), "src", "container", "prepare-layout.py"))),
        };

        foreach (var (name, body) in sources)
        {
            Assert.DoesNotContain(".tmp\" % (name, os.getpid())", body, StringComparison.Ordinal);
            Assert.Contains("secrets.token_hex(8)", body);
            Assert.Contains("except FileExistsError:", body);
            Assert.True(
                body.Contains("import secrets", StringComparison.Ordinal),
                $"{name} uses secrets.token_hex without importing secrets");
        }
    }

    /// <summary>
    /// Once the authoritative SQLite database exists, the pre-isolation image
    /// cannot read it. Rolling back by republishing runtime.json would run old
    /// JSON-only code against stale session identity while the database held the
    /// real state, so the rollback must fail closed before it changes anything.
    /// </summary>
    [Fact]
    public void DatabaseEraRollbackFailsClosedBeforeAnyWriteOrHandBack()
    {
        var revert = ExtractBlock("REVERT_CODE");

        Assert.Contains("control.db", revert, StringComparison.Ordinal);
        Assert.Contains("authoritative SQLite database", revert, StringComparison.Ordinal);
        Assert.Contains("contract-safe restore", revert, StringComparison.Ordinal);

        // The guard runs before the descriptor validation and before the first
        // publication or hand-back, so nothing is changed on the refusal path.
        var guard = revert.IndexOf("database_path = os.path.join(private_root, \"control.db\")", StringComparison.Ordinal);
        Assert.True(guard > 0, "the database-era guard was not found");
        Assert.True(
            guard < revert.IndexOf("descriptors = []", StringComparison.Ordinal),
            "the database-era guard must run before the rollback validates and changes paths");
        Assert.True(
            guard < revert.IndexOf("write_marker(\"intent\"", StringComparison.Ordinal),
            "the database-era guard must run before the rollback marker or any publication");
    }

    /// <summary>
    /// Executes the rollback block against a controller-private directory that
    /// holds a control.db. The refusal happens before the secret or state paths
    /// are inspected, so it is testable without root or Docker.
    /// </summary>
    [Fact]
    public void DatabaseEraRollbackRefusesWhenTheDatabaseExists()
    {
        var python = RequirePython();
        if (python is null || OperatingSystem.IsWindows())
        {
            return;
        }

        var code = LoadEmbeddedCode(python, "REVERT_CODE");
        Assert.False(string.IsNullOrWhiteSpace(code));

        using var directory = new TempDirectory();
        var dataRoot = Path.Combine(directory.Path, "data");
        var privateRoot = Path.Combine(directory.Path, "private");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(privateRoot);
        File.WriteAllText(Path.Combine(privateRoot, "control.db"), "not really a database");

        var result = Run(python, new[]
        {
            "-c", code,
            Path.Combine(directory.Path, "owner-password"),
            "1000", "1000", dataRoot, privateRoot, "0",
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("authoritative SQLite database", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("control.db", result.StandardError, StringComparison.Ordinal);

        // Nothing was written: no marker, no republished runtime state.
        Assert.False(File.Exists(Path.Combine(privateRoot, "rollback.active")));
        Assert.False(File.Exists(Path.Combine(dataRoot, "runtime.json")));
    }

    private static int Occurrences(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;

    /// <summary>
    /// Three volumes cannot be changed in one transaction, so the rollback is
    /// replayable instead of atomic, and the failure message has to say so
    /// rather than leaving the operator guessing whether a retry is safe.
    /// </summary>
    [Fact]
    public void AFailedRollbackTellsTheOperatorToReRunTheSameCommand()
    {
        var python = RequirePython();
        if (python is null || OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TempDirectory();
        var result = RunWithFakeDocker(
            python,
            directory,
            inspect: new(0, "false"),
            run: new(1, StandardError: "container-side failure"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("every step is idempotent", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("run the exact same command again", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Do not start either image", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// The controller must be stopped before a rollback. A live controller holds
    /// the private state and re-applies the isolation layout on its next restart,
    /// so re-owning underneath it is neither complete nor stable.
    /// </summary>
    [Fact]
    public void RevertRefusesWhileTheControlContainerIsRunning()
    {
        var python = RequirePython();
        if (python is null || OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TempDirectory();
        // What `docker inspect --format '{{json .State.Running}}'` prints for a
        // live container.
        var result = RunWithFakeDocker(python, directory, inspect: new(0, "true"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("stop it before reverting", result.StandardError, StringComparison.Ordinal);

        // The refusal happens before any container is started, so nothing on the
        // volumes was touched.
        Assert.DoesNotContain("run --rm", DockerCalls(directory));
    }

    /// <summary>
    /// A stopped-but-still-present container is the normal `docker compose stop`
    /// state, and must not block the rollback.
    /// </summary>
    [Fact]
    public void RevertProceedsWhenTheControlContainerIsStopped()
    {
        var python = RequirePython();
        if (python is null || OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TempDirectory();
        var result = RunWithFakeDocker(python, directory, inspect: new(0, "false"));

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("stop it before reverting", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(
            "run --rm",
            DockerCalls(directory),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// An inspect failure that is not a plain "no such object" is treated as
    /// running. A rollback must never re-own state underneath a live controller
    /// because the daemon gave an answer we could not interpret.
    /// </summary>
    [Fact]
    public void RevertRefusesWhenContainerStateCannotBeDetermined()
    {
        var python = RequirePython();
        if (python is null || OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TempDirectory();
        var result = RunWithFakeDocker(
            python,
            directory,
            inspect: new(1, StandardError: "daemon unreachable"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("could not determine whether", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// An absent container is not a running container: after `docker compose
    /// down` the rollback must proceed rather than deadlock the operator.
    /// </summary>
    [Fact]
    public void RevertProceedsWhenTheControlContainerDoesNotExist()
    {
        var python = RequirePython();
        if (python is null || OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TempDirectory();
        var result = RunWithFakeDocker(
            python,
            directory,
            inspect: new(1, StandardError: "Error: No such object: agentcontrol-v2-control-1"));

        Assert.Equal(0, result.ExitCode);

        // The precondition passes and the script moves on to the volume work.
        Assert.DoesNotContain("stop it before reverting", result.StandardError, StringComparison.Ordinal);
        var calls = DockerCalls(directory);
        Assert.Contains("run --rm", calls, StringComparison.Ordinal);

        // All three volumes take part: the secret, the agent data and the
        // controller-private store are one rollback unit.
        Assert.Contains("agentcontrol-v2-secrets", calls, StringComparison.Ordinal);
        Assert.Contains("agentcontrol-v2_control-data", calls, StringComparison.Ordinal);
        Assert.Contains("agentcontrol-v2_control-private", calls, StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs the script with a stub `docker` earlier on PATH, so the precondition
    /// logic is exercised without a daemon, an image, or any real volume.
    /// </summary>
    /// <param name="inspect">what `docker inspect &lt;container&gt;` does.</param>
    /// <param name="volumeInspect">what `docker volume inspect` does; the
    /// default reports every volume as present.</param>
    /// <param name="run">what `docker run` does; the default succeeds.</param>
    /// <param name="arguments">script arguments; defaults to a plain rollback.</param>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static (int ExitCode, string StandardOutput, string StandardError) RunWithFakeDocker(
        string python,
        TempDirectory directory,
        DockerResponse inspect,
        DockerResponse? volumeInspect = null,
        DockerResponse? run = null,
        IReadOnlyList<string>? arguments = null)
    {
        // The `docker` stand-in is the checked-in canonical fixture, symlinked
        // per test, with its behavior in non-executable sidecars. Writing an
        // executable here and exec'ing it loses the ETXTBSY race of #232 under
        // parallel test execution.
        var canonical = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake-docker.sh");
        Assert.True(File.Exists(canonical), $"Canonical fake docker fixture was not copied to '{canonical}'.");

        File.WriteAllText(Path.Combine(directory.Path, "calls.log"), string.Empty);
        File.CreateSymbolicLink(Path.Combine(directory.Path, "docker"), canonical);
        WriteResponse(directory, "inspect", inspect);
        WriteResponse(directory, "volume", volumeInspect ?? DockerResponse.Success);
        WriteResponse(directory, "run", run ?? DockerResponse.Success);

        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(RequireScript());
        foreach (var argument in arguments ?? ["--revert-isolation"])
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["PATH"] =
            directory.Path + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("python3 did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, standardOutput.GetAwaiter().GetResult(), standardError.GetAwaiter().GetResult());
    }

    /// <summary>Scripted outcome of one `docker` subcommand family.</summary>
    private sealed record DockerResponse(int ExitCode, string StandardOutput = "", string StandardError = "")
    {
        public static DockerResponse Success { get; } = new(0);
    }

    private static void WriteResponse(TempDirectory directory, string kind, DockerResponse response) =>
        File.WriteAllLines(
            Path.Combine(directory.Path, "response." + kind),
            [response.ExitCode.ToString(), response.StandardOutput, response.StandardError]);

    private static string DockerCalls(TempDirectory directory) =>
        File.ReadAllText(Path.Combine(directory.Path, "calls.log"));

    private static string ExtractBlock(string name)
    {
        var text = File.ReadAllText(RequireScript());
        var marker = name + " = '''";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{name} was not found in the script");
        var end = text.IndexOf("'''\n", start + marker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"{name} is not terminated");
        return text[start..end];
    }

    /// <summary>
    /// The migration is metadata only. It must never read, rewrite, rotate or
    /// relink the credential, so it cannot leak or destroy the deployed secret.
    /// </summary>
    [Fact]
    public void MigrationNeverReadsRewritesOrReplacesTheSecret()
    {
        var migrate = ExtractBlock("MIGRATE_CODE");

        Assert.Contains("os.fchown(fd, control_uid, control_gid)", migrate, StringComparison.Ordinal);
        Assert.Contains("os.O_NOFOLLOW", migrate, StringComparison.Ordinal);
        Assert.Contains("verified.st_ino", migrate, StringComparison.Ordinal);

        // No write path, no rotation, no replacement of the inode.
        Assert.DoesNotContain("secrets.token_urlsafe", migrate, StringComparison.Ordinal);
        Assert.DoesNotContain("os.unlink", migrate, StringComparison.Ordinal);
        Assert.DoesNotContain("os.rename", migrate, StringComparison.Ordinal);
        Assert.DoesNotContain("handle.read()", migrate, StringComparison.Ordinal);
        Assert.DoesNotContain("\"w\"", migrate, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationPreservesContentsAndInodeWhileReOwning()
    {
        var python = RequirePython();
        if (python is null)
        {
            return;
        }

        const string secret = "existing-owner-password-value-0000000000";
        using var directory = new TempDirectory();
        var target = Path.Combine(directory.Path, "owner-password");
        File.WriteAllText(target, secret);
        var before = new FileInfo(target).Length;

        // Unprivileged test runs cannot chown; the migration then fails closed
        // and must still leave the credential byte-identical.
        var result = RunMigration(python, target);

        Assert.Equal(secret, File.ReadAllText(target));
        Assert.Equal(before, new FileInfo(target).Length);
        Assert.DoesNotContain(secret, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationRefusesASymlinkedSecretPath()
    {
        var python = RequirePython();
        if (python is null || OperatingSystem.IsWindows())
        {
            return;
        }

        const string secret = "existing-owner-password-value-0000000000";
        using var directory = new TempDirectory();
        var real = Path.Combine(directory.Path, "real-secret");
        var link = Path.Combine(directory.Path, "owner-password");
        File.WriteAllText(real, secret);
        File.CreateSymbolicLink(link, real);

        var result = RunMigration(python, link);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("symlink", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(secret, File.ReadAllText(real));
    }

    [Fact]
    public void MigrationOnAMissingSecretFailsInsteadOfCreatingOne()
    {
        var python = RequirePython();
        if (python is null)
        {
            return;
        }

        using var directory = new TempDirectory();
        var target = Path.Combine(directory.Path, "owner-password");

        var result = RunMigration(python, target);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(target));
    }

    /// <summary>
    /// The provisioner creates, no-ops on identical input (preserving the inode)
    /// and atomically rotates on change, without ever printing the key or a
    /// fingerprint.
    /// </summary>
    [Fact]
    public void ProvisionCreatesNoOpsIdenticallyAndRotatesAtomicallyWithoutPrintingTheKey()
    {
        var python = RequirePython();
        if (python is null || OperatingSystem.IsWindows())
        {
            return;
        }

        var (uid, gid) = CurrentUidGid(python);
        using var directory = new TempDirectory();
        var target = Path.Combine(directory.Path, "cliproxy-api-key");
        const string first = "first-disposable-provision-key-0001";
        const string second = "second-disposable-provision-key-0002";

        var created = RunProvision(python, target, uid, gid, first);
        Assert.True(created.ExitCode == 0, created.StandardError);
        Assert.Contains("Created", created.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(first + "\n", File.ReadAllText(target));
        Assert.DoesNotContain(first, created.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(first, created.StandardError, StringComparison.Ordinal);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(target));
        var firstInode = Inode(python, target);

        var identical = RunProvision(python, target, uid, gid, first);
        Assert.True(identical.ExitCode == 0, identical.StandardError);
        Assert.Contains("unchanged", identical.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(firstInode, Inode(python, target));
        Assert.Equal(first + "\n", File.ReadAllText(target));

        var rotated = RunProvision(python, target, uid, gid, second);
        Assert.True(rotated.ExitCode == 0, rotated.StandardError);
        Assert.Contains("Rotated", rotated.StandardOutput, StringComparison.Ordinal);
        Assert.NotEqual(firstInode, Inode(python, target));
        Assert.Equal(second + "\n", File.ReadAllText(target));
        Assert.DoesNotContain(second, rotated.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(second, rotated.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("fingerprint", rotated.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("control\u0001character-key")]
    public void ProvisionRejectsAnInvalidStdinKeyWithoutWriting(string input)
    {
        var python = RequirePython();
        if (python is null || OperatingSystem.IsWindows())
        {
            return;
        }

        var (uid, gid) = CurrentUidGid(python);
        using var directory = new TempDirectory();
        var target = Path.Combine(directory.Path, "cliproxy-api-key");

        var result = RunProvision(python, target, uid, gid, input);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(target));
        if (input.Length > 0)
        {
            Assert.DoesNotContain(input, result.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(input, result.StandardError, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A symlink, non-regular file, extra hard link or unexpected owner is
    /// refused before any rotation, so a hostile path cannot redirect or observe
    /// the managed-employee key.
    /// </summary>
    [Fact]
    public void ProvisionFailsClosedOnHostileExistingPaths()
    {
        var python = RequirePython();
        if (python is null || OperatingSystem.IsWindows())
        {
            return;
        }

        var (uid, gid) = CurrentUidGid(python);
        using var directory = new TempDirectory();
        const string key = "disposable-hostile-path-key-0001";

        // Symlink: the write must not follow the link.
        var real = Path.Combine(directory.Path, "real");
        var link = Path.Combine(directory.Path, "linked");
        File.WriteAllText(real, "untouched");
        File.CreateSymbolicLink(link, real);
        var symlink = RunProvision(python, link, uid, gid, key);
        Assert.NotEqual(0, symlink.ExitCode);
        Assert.Contains("symlink", symlink.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("untouched", File.ReadAllText(real));

        // Non-regular file.
        var asDirectory = Path.Combine(directory.Path, "directory");
        Directory.CreateDirectory(asDirectory);
        var nonRegular = RunProvision(python, asDirectory, uid, gid, key);
        Assert.NotEqual(0, nonRegular.ExitCode);
        Assert.Contains("not a regular file", nonRegular.StandardError, StringComparison.Ordinal);

        // Extra hard link.
        var hardA = Path.Combine(directory.Path, "hard-a");
        var hardB = Path.Combine(directory.Path, "hard-b");
        File.WriteAllText(hardA, "untouched");
        Run(python, new[] { "-c", "import os,sys;os.link(sys.argv[1], sys.argv[2])", hardA, hardB });
        var hardLink = RunProvision(python, hardA, uid, gid, key);
        Assert.NotEqual(0, hardLink.ExitCode);
        Assert.Contains("hard links", hardLink.StandardError, StringComparison.Ordinal);
        Assert.Equal("untouched", File.ReadAllText(hardA));

        // Unexpected owner: the file belongs to the test identity, not the
        // requested controller identity.
        var foreign = Path.Combine(directory.Path, "foreign-owner");
        File.WriteAllText(foreign, "untouched");
        var foreignOwner = RunProvision(python, foreign, uid + 1, gid + 1, key);
        Assert.NotEqual(0, foreignOwner.ExitCode);
        Assert.Contains("unexpected identity", foreignOwner.StandardError, StringComparison.Ordinal);
        Assert.Equal("untouched", File.ReadAllText(foreign));
    }

    /// <summary>
    /// A live controller may still hold the previous key, so provisioning refuses
    /// to rotate underneath it. The refusal happens before any container runs.
    /// </summary>
    [Fact]
    public void ProvisionRefusesWhileTheControlContainerIsRunning()
    {
        var python = RequirePython();
        if (python is null || OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TempDirectory();
        var result = RunWithFakeDocker(
            python,
            directory,
            inspect: new(0, "true"),
            arguments: ["--provision-cliproxy-key"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("stop it", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("run --rm", DockerCalls(directory), StringComparison.Ordinal);
    }

    /// <summary>
    /// Provisioning attaches stdin (so the key never becomes an argument or an
    /// environment value) and operates only on the CLIProxy key path, never the
    /// owner password.
    /// </summary>
    [Fact]
    public void ProvisionAttachesStdinAndTargetsOnlyTheCliProxyKey()
    {
        var python = RequirePython();
        if (python is null || OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TempDirectory();
        var result = RunWithFakeDocker(
            python,
            directory,
            inspect: new(0, "false"),
            arguments: ["--provision-cliproxy-key"]);

        Assert.Equal(0, result.ExitCode);
        var calls = DockerCalls(directory);
        Assert.Contains("run --rm --user root --entrypoint python3 -i", calls, StringComparison.Ordinal);
        Assert.Contains("/secrets cliproxy-api-key", calls, StringComparison.Ordinal);
        Assert.DoesNotContain("/secrets/owner-password", calls, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFilePublishesAUsablePasswordAtomically()
    {
        var python = RequirePython();
        if (python is null)
        {
            return; // python3 is provided in CI.
        }

        using var directory = new TempDirectory();
        var target = Path.Combine(directory.Path, "owner-password");

        var result = RunRemoteInitializer(python, target);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.True(File.Exists(target));
        Assert.Contains("Created owner password", result.StandardOutput, StringComparison.Ordinal);

        var password = File.ReadAllText(target).Trim();
        Assert.True(password.Length >= 24, $"generated password was only {password.Length} characters");
        Assert.DoesNotContain(password, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(password, result.StandardError, StringComparison.Ordinal);

        // Ownership/permissions are applied before the file is linked into place.
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(target);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }

        // No partially written temporary file remains at a discoverable path.
        Assert.Empty(Directory.GetFiles(directory.Path, ".owner-password.*"));
    }

    [Fact]
    public void ExistingValidPasswordIsPreserved()
    {
        var python = RequirePython();
        if (python is null)
        {
            return;
        }

        const string existing = "existing-owner-password-value-0000000000";
        using var directory = new TempDirectory();
        var target = Path.Combine(directory.Path, "owner-password");
        File.WriteAllText(target, existing);

        var result = RunRemoteInitializer(python, target);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Contains("preserved", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(existing, File.ReadAllText(target));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData("short")]
    public void ExistingBlankOrShortPasswordFailsWithoutOverwriting(string existing)
    {
        var python = RequirePython();
        if (python is null)
        {
            return;
        }

        using var directory = new TempDirectory();
        var target = Path.Combine(directory.Path, "owner-password");
        File.WriteAllText(target, existing);

        var result = RunRemoteInitializer(python, target);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("refusing to overwrite", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(existing, File.ReadAllText(target));
    }

    [Fact]
    public void ExistingShortSecretIsNeverPrinted()
    {
        var python = RequirePython();
        if (python is null)
        {
            return;
        }

        const string secret = "leaky-existing-secret";
        using var directory = new TempDirectory();
        var target = Path.Combine(directory.Path, "owner-password");
        File.WriteAllText(target, secret);

        var result = RunRemoteInitializer(python, target);

        Assert.DoesNotContain(secret, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, result.StandardError, StringComparison.Ordinal);
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunRemoteInitializer(string python, string target)
    {
        var code = LoadEmbeddedCode(python, "REMOTE_CODE");
        Assert.False(string.IsNullOrWhiteSpace(code));

        return Run(python, new[] { "-c", code, target, ControlUid.ToString(), ControlGid.ToString() });
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunMigration(string python, string target)
    {
        var code = LoadEmbeddedCode(python, "MIGRATE_CODE");
        Assert.False(string.IsNullOrWhiteSpace(code));

        return Run(python, new[] { "-c", code, target, ControlUid.ToString(), ControlGid.ToString() });
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunProvision(
        string python,
        string target,
        int uid,
        int gid,
        string key)
    {
        var code = LoadEmbeddedCode(python, "PROVISION_CODE");
        Assert.False(string.IsNullOrWhiteSpace(code));

        return RunWithInput(
            python,
            ["-c", code, Path.GetDirectoryName(target)!, Path.GetFileName(target), uid.ToString(), gid.ToString(), "16"],
            key);
    }

    private static (int UserId, int GroupId) CurrentUidGid(string python)
    {
        var result = Run(python, ["-c", "import os;print(os.geteuid());print(os.getegid())"]);
        Assert.True(result.ExitCode == 0, result.StandardError);
        var lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return (int.Parse(lines[0], System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(lines[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    private static long Inode(string python, string path)
    {
        var result = Run(python, ["-c", "import os,sys;print(os.stat(sys.argv[1]).st_ino)", path]);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return long.Parse(result.StandardOutput.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunWithInput(
        string python,
        IReadOnlyList<string> arguments,
        string input)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("python3 did not start.");
        process.StandardInput.Write(input);
        process.StandardInput.Close();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, standardOutput.GetAwaiter().GetResult(), standardError.GetAwaiter().GetResult());
    }

    private static string LoadEmbeddedCode(string python, string name)
    {
        const string harness = """
            import runpy
            import sys

            module = runpy.run_path(sys.argv[1])
            sys.stdout.write(module[sys.argv[2]])
            """;

        var result = Run(python, new[] { "-c", harness, RequireScript(), name });
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result.StandardOutput;
    }

    private static (int ExitCode, string StandardOutput, string StandardError) Run(string python, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("python3 did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, standardOutput.GetAwaiter().GetResult(), standardError.GetAwaiter().GetResult());
    }

    private static string RequireScript() =>
        RequireRepositoryRoot() is { } root
            ? Path.Combine(root, "scripts", "init-secrets.py")
            : throw new InvalidOperationException("could not locate scripts/init-secrets.py");

    private static string RequireControlOptions() =>
        RequireRepositoryRoot() is { } root
            ? Path.Combine(root, "src", "HVO.AgentControl", "Runtime", "ControlOptions.cs")
            : throw new InvalidOperationException("could not locate ControlOptions.cs");

    private static string? RequireRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "scripts", "init-secrets.py")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string? RequirePython() => FindOnPath("python3");

    private static string? FindOnPath(string name)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agentcontrol-secrets-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
