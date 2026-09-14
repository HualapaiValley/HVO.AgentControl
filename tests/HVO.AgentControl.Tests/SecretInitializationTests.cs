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
            dockerBehavior: "echo false",
            arguments: ["--revert-isolation", "--project", "tenant-b"]);

        Assert.Equal(0, result.ExitCode);
        var calls = File.ReadAllText(Path.Combine(directory.Path, "docker-calls.log"));
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
            dockerBehavior: "echo false",
            volumeInspectBehavior: "echo 'Error: No such volume' >&2; exit 1");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("these volumes do not exist", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("--project", result.StandardError, StringComparison.Ordinal);

        // Nothing was mounted or executed against a volume.
        Assert.DoesNotContain(
            "run --rm",
            File.ReadAllText(Path.Combine(directory.Path, "docker-calls.log")),
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
            dockerBehavior: "echo true",
            arguments: ["--resume-isolation", "--state-source", "private"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("stop it before resuming", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "run --rm",
            File.ReadAllText(Path.Combine(directory.Path, "docker-calls.log")),
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
        Assert.Contains("payload = read_all(private_fd)", revert, StringComparison.Ordinal);
        Assert.Contains(
            "publish_bytes(\n            data_fd, data_root, state_name, payload, legacy_uid, legacy_gid, 0o600\n        )",
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
            dockerBehavior: "echo false",
            runBehavior: "echo 'container-side failure' >&2; exit 1");

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
        var result = RunWithFakeDocker(python, directory, dockerBehavior: "echo true");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("stop it before reverting", result.StandardError, StringComparison.Ordinal);

        // The refusal happens before any container is started, so nothing on the
        // volumes was touched.
        Assert.DoesNotContain("run --rm", File.ReadAllText(Path.Combine(directory.Path, "docker-calls.log")));
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
        var result = RunWithFakeDocker(python, directory, dockerBehavior: "echo false");

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("stop it before reverting", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(
            "run --rm",
            File.ReadAllText(Path.Combine(directory.Path, "docker-calls.log")),
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
            dockerBehavior: "echo 'daemon unreachable' >&2; exit 1");

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
            dockerBehavior: "echo 'Error: No such object: agentcontrol-v2-control-1' >&2; exit 1");

        Assert.Equal(0, result.ExitCode);

        // The precondition passes and the script moves on to the volume work.
        Assert.DoesNotContain("stop it before reverting", result.StandardError, StringComparison.Ordinal);
        var calls = File.ReadAllText(Path.Combine(directory.Path, "docker-calls.log"));
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
    /// <param name="dockerBehavior">what `docker inspect &lt;container&gt;` does.</param>
    /// <param name="volumeInspectBehavior">what `docker volume inspect` does; the
    /// default reports every volume as present.</param>
    /// <param name="runBehavior">what `docker run` does; the default succeeds.</param>
    /// <param name="arguments">script arguments; defaults to a plain rollback.</param>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static (int ExitCode, string StandardOutput, string StandardError) RunWithFakeDocker(
        string python,
        TempDirectory directory,
        string dockerBehavior,
        string volumeInspectBehavior = "exit 0",
        string runBehavior = "exit 0",
        IReadOnlyList<string>? arguments = null)
    {
        var log = Path.Combine(directory.Path, "docker-calls.log");
        File.WriteAllText(log, string.Empty);

        var stub = Path.Combine(directory.Path, "docker");
        File.WriteAllText(
            stub,
            $"""
            #!/bin/sh
            echo "$@" >> '{log}'
            case "$*" in
              *"volume inspect"*) {volumeInspectBehavior} ;;
              *"run --rm"*) {runBehavior} ;;
              *inspect*) {dockerBehavior} ;;
              *) exit 0 ;;
            esac
            """);
        File.SetUnixFileMode(
            stub,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

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
