using System.Diagnostics;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Real alternate-UID isolation tests. These build the container image and run
/// actual processes under the agent and controller identities: the boundary is
/// enforced by the kernel, so asserting it in-process would prove nothing.
/// </summary>
/// <remarks>
/// The suite is skipped when Docker is unavailable (developer machines without
/// a daemon). CI runs Docker in the `docker` job, and
/// <see cref="LauncherSourceIsWiredIntoTheImageAndCi"/> keeps the wiring itself
/// covered by the always-on build-and-test job.
/// </remarks>
[Collection("agent-isolation")]
[Trait("Category", "DockerIsolation")]
public sealed class AgentIsolationContainerTests
{
    private const string ImageTag = "hvo-agentcontrol:isolation-tests";
    private const string Launcher = "/usr/local/bin/agentcontrol-launch";

    /// <summary>
    /// When set, an unavailable daemon fails the suite instead of skipping it.
    /// CI sets it in the Docker job; a skipped security test is not evidence.
    /// </summary>
    private static readonly bool DockerRequired =
        Environment.GetEnvironmentVariable("AGENTCONTROL_DOCKER_REQUIRED") == "1";

    private static readonly Lazy<bool> ImageAvailable = new(BuildImage, isThreadSafe: true);

    /// <summary>Image the suite actually runs; set by <see cref="BuildImage"/>.</summary>
    private static string ResolvedImage = ImageTag;

    // Exactly the capabilities Compose grants, so a test that passes here also
    // passes in the deployed configuration: CHOWN/DAC_OVERRIDE/FOWNER for the
    // root entrypoint's ownership work, SETPCAP so it can drop those three from
    // the bounding set before exec'ing the controller, and SETUID/SETGID/KILL
    // for the launcher. ComposeGrantsExactlyTheCapabilitiesTheseTestsAssume
    // keeps the two lists from drifting apart.
    private static readonly string[] CapabilityFlags =
    [
        "--cap-drop", "ALL",
        "--cap-add", "CHOWN",
        "--cap-add", "DAC_OVERRIDE",
        "--cap-add", "FOWNER",
        "--cap-add", "SETPCAP",
        "--cap-add", "SETUID",
        "--cap-add", "SETGID",
        "--cap-add", "KILL",
    ];

    private static readonly string[] RunFlags =
    [
        "run", "--rm",
        .. CapabilityFlags,
        "--entrypoint", "/bin/bash",
    ];

    /// <summary>
    /// Guards the wiring without Docker: the launcher source, entrypoint and
    /// image stage must stay connected, so the isolation cannot be dropped by a
    /// refactor while the container suite happens to be skipped.
    /// </summary>
    [Fact]
    public void LauncherSourceIsWiredIntoTheImageAndCi()
    {
        var root = ControllerIsolationLayoutTests.RepositoryRoot();
        var dockerfile = File.ReadAllText(Path.Combine(root, "Dockerfile"));
        var launcherSource = File.ReadAllText(Path.Combine(root, "src", "launcher", "agentcontrol-launch.c"));

        Assert.True(File.Exists(Path.Combine(root, "src", "container", "control-entrypoint.sh")));

        // Built from source in a separate stage; the runtime image has no compiler.
        Assert.Contains("FROM debian:trixie-slim AS launcher", dockerfile, StringComparison.Ordinal);
        Assert.Contains("chmod 4750 /usr/local/bin/agentcontrol-launch", dockerfile, StringComparison.Ordinal);
        Assert.Contains("chown root:control /usr/local/bin/agentcontrol-launch", dockerfile, StringComparison.Ordinal);

        // Every other setuid binary must be stripped, because the controller runs
        // without no-new-privileges so the launcher can elevate.
        Assert.Contains("-perm /6000 -type f -exec chmod a-s", dockerfile, StringComparison.Ordinal);
        Assert.Contains("control-entrypoint", dockerfile, StringComparison.Ordinal);

        // The symlink-safe layout helper is the entrypoint's only chown path.
        Assert.True(File.Exists(Path.Combine(root, "src", "container", "prepare-layout.py")));
        Assert.Contains("agentcontrol-prepare-layout", dockerfile, StringComparison.Ordinal);
        var entrypoint = File.ReadAllText(Path.Combine(root, "src", "container", "control-entrypoint.sh"));
        Assert.DoesNotContain("chown ", entrypoint, StringComparison.Ordinal);
        Assert.DoesNotContain("chmod ", entrypoint, StringComparison.Ordinal);

        // The startup capabilities must be dropped before the controller starts.
        Assert.Contains(
            "--bounding-set=-chown,-dac_override,-fowner,-fsetid,-setpcap",
            entrypoint,
            StringComparison.Ordinal);

        // The launcher must keep its mandatory hardening steps.
        foreach (var required in new[]
        {
            "PR_SET_NO_NEW_PRIVS",
            "PR_CAPBSET_DROP",
            "setgroups(0, NULL)",
            "privilege drop was reversible",
            "kEnvironmentAllowList",
        })
        {
            Assert.Contains(required, launcherSource, StringComparison.Ordinal);
        }

        // No parameter for an executable path: the four targets are compiled in.
        Assert.DoesNotContain("execvp", launcherSource, StringComparison.Ordinal);
        Assert.DoesNotContain("system(", launcherSource, StringComparison.Ordinal);

        // The launcher must not claim to bound which *programs* can run: the
        // tmux operation forwards a caller-supplied pane command vector, which
        // AgentIdentityIsTheBoundaryNotAnExecutionAllowList proves runs /bin/sh.
        // A comment that says otherwise is a false security claim, so fail here.
        foreach (var falseClaim in new[]
        {
            "no arbitrary executable",
            "no interface for an arbitrary executable",
            "never another program",
        })
        {
            Assert.DoesNotContain(falseClaim, launcherSource, StringComparison.OrdinalIgnoreCase);
        }

        // Nor may it claim to empty the child's capability BOUNDING set. Its
        // PR_CAPBSET_DROP needs CAP_SETPCAP, which the entrypoint removes before
        // the controller starts, so the call is a no-op in the shipped
        // configuration: StartupCapabilitiesAreRemovedFromTheControllerBoundingSet
        // measures the child's bounding set as 0xE0, not 0. What the launcher
        // does guarantee is empty permitted/effective sets. The distinction is a
        // security claim, so a doc or comment that overstates it fails here.
        foreach (var document in new[]
        {
            launcherSource,
            File.ReadAllText(Path.Combine(root, "docs", "ARCHITECTURE.md")),
            File.ReadAllText(Path.Combine(root, "docs", "PHASE-1-CONTRACTS.md")),
            File.ReadAllText(Path.Combine(root, "CHANGELOG.md")),
            File.ReadAllText(Path.Combine(root, "README.md")),
        })
        {
            Assert.DoesNotContain(
                "empties the capability bounding set",
                document,
                StringComparison.OrdinalIgnoreCase);
        }

        // The pinned SDK has a single source; a doc that names a different one
        // is stale rather than historical unless it is describing a past bump.
        var globalJson = File.ReadAllText(Path.Combine(root, "global.json"));
        Assert.Contains("10.0.401", globalJson, StringComparison.Ordinal);
        foreach (var name in new[] { "CHANGELOG.md", "README.md" })
        {
            Assert.DoesNotContain(
                "SDK 10.0.400",
                File.ReadAllText(Path.Combine(root, name)),
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The container suite runs with a hand-written capability list, so it would
    /// silently stop reflecting the deployment if Compose changed. Both lists
    /// must stay identical or the isolation evidence is about a different
    /// configuration than the one that ships.
    /// </summary>
    [Fact]
    public void ComposeGrantsExactlyTheCapabilitiesTheseTestsAssume()
    {
        var compose = File.ReadAllText(
            Path.Combine(ControllerIsolationLayoutTests.RepositoryRoot(), "compose.yaml"));

        var composed = compose
            .Split('\n')
            .SkipWhile(line => !line.Contains("cap_add:", StringComparison.Ordinal))
            .Skip(1)
            .TakeWhile(line => line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            .Select(line => line.Trim()[2..].Trim())
            .ToArray();

        var asserted = CapabilityFlags
            .Where((_, index) => index > 0 && CapabilityFlags[index - 1] == "--cap-add")
            .ToArray();

        Assert.Equal(asserted.Order(), composed.Order());
        Assert.Contains("- ALL", compose, StringComparison.Ordinal);

        // SETPCAP is what lets the entrypoint remove the startup capabilities
        // from the bounding set; without it the launcher could regain them.
        Assert.Contains("SETPCAP", composed);
    }

    /// <summary>
    /// The honest statement of the launcher contract. The controller CAN execute
    /// a program of its choosing through <c>tmux new-session</c> - that is what
    /// running OpenCode's TUI is - but only ever as the agent identity, which
    /// reaches neither the controller's private state, nor the owner secret, nor
    /// the launcher, nor root-owned paths.
    /// </summary>
    [DockerFact]
    public void AgentIdentityIsTheBoundaryNotAnExecutionAllowList()
    {
        var result = RunInContainer(
            """
            export Control__OwnerPasswordFile=""
            install -o 1001 -g 1001 -m 600 /dev/null /control-data/runtime.json
            echo '{"tmuxOwnerToken":"token-secret"}' > /control-data/runtime.json
            /usr/local/bin/agentcontrol-prepare-layout || echo LAYOUT_FAILED
            setpriv --reuid 1001 --regid 1001 --init-groups env HOME=/data/home \
                agentcontrol-launch tmux new-session -d -s shellprobe /bin/sh -c '
                  { id
                    cat /control-data/runtime.json 2>&1 | head -1
                    cat /usr/local/bin/agentcontrol-launch >/dev/null 2>&1 && echo LAUNCHER_READ || echo LAUNCHER_UNREADABLE
                    /usr/local/bin/agentcontrol-launch signal 1 TERM 2>&1 | head -1
                    touch /data/attacker-file 2>/dev/null && echo DATA_ROOT_WRITE || echo DATA_ROOT_DENIED
                    touch /usr/local/bin/attacker 2>/dev/null && echo BIN_WRITE || echo BIN_DENIED
                  } > /data/home/shell-probe.txt 2>&1; sleep 20'
            sleep 3
            echo PROBE_START
            cat /data/home/shell-probe.txt
            """);

        var output = result.StandardOutput;
        var probe = output[output.IndexOf("PROBE_START", StringComparison.Ordinal)..];

        // /bin/sh really did run: the tmux operation is not an execution allow-list.
        Assert.Contains("uid=1000(agent) gid=1000(agent)", probe, StringComparison.Ordinal);

        // ...and it reaches nothing that matters.
        Assert.DoesNotContain("token-secret", probe, StringComparison.Ordinal);
        Assert.Contains("Permission denied", probe, StringComparison.Ordinal);
        Assert.Contains("LAUNCHER_UNREADABLE", probe, StringComparison.Ordinal);
        Assert.Contains("DATA_ROOT_DENIED", probe, StringComparison.Ordinal);
        Assert.Contains("BIN_DENIED", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("LAUNCHER_READ", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("DATA_ROOT_WRITE", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("BIN_WRITE", probe, StringComparison.Ordinal);
    }

    /// <summary>
    /// tmux resolves <c>default-shell</c> from the account and starts its server
    /// through it; with <c>/usr/sbin/nologin</c> a bare <c>new-session</c> leaves
    /// "no server running" (measured on debian trixie, tmux 3.5a). The agent
    /// therefore needs a real shell. The controller never starts a shell and
    /// keeps <c>nologin</c>, which is what stops a stolen controller credential
    /// from becoming an interactive session.
    /// </summary>
    [DockerFact]
    public void AgentHasALoginShellAndTheControllerDoesNot()
    {
        var result = RunInContainer(
            """
            echo "AGENT_SHELL=$(getent passwd 1000 | cut -d: -f7)"
            echo "CONTROL_SHELL=$(getent passwd 1001 | cut -d: -f7)"
            chown 1000:1000 /data/home
            setpriv --reuid 1000 --regid 1000 --clear-groups env HOME=/data/home TERM=xterm \
                tmux new-session -d -s shellcheck >/dev/null 2>&1
            echo "BARE_SESSION=$?"
            setpriv --reuid 1000 --regid 1000 --clear-groups env HOME=/data/home \
                tmux show-options -g default-shell
            setpriv --reuid 1000 --regid 1000 --clear-groups env HOME=/data/home \
                tmux list-panes -t shellcheck -F "PANE_DEAD=#{pane_dead}"
            """);

        var output = result.StandardOutput;

        Assert.Contains("AGENT_SHELL=/bin/bash", output, StringComparison.Ordinal);
        Assert.Contains("CONTROL_SHELL=/usr/sbin/nologin", output, StringComparison.Ordinal);

        // The live proof: the tmux server actually starts and the pane is alive.
        Assert.Contains("BARE_SESSION=0", output, StringComparison.Ordinal);
        Assert.Contains("default-shell /bin/bash", output, StringComparison.Ordinal);
        Assert.Contains("PANE_DEAD=0", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The startup capabilities must not survive into the controller. The setuid
    /// launcher regains everything in the bounding set while it is root, so
    /// CHOWN/DAC_OVERRIDE/FOWNER left in the set would still be reachable from a
    /// compromised controller through the launcher's root phase.
    /// </summary>
    [DockerFact]
    public void StartupCapabilitiesAreRemovedFromTheControllerBoundingSet()
    {
        var result = RunInContainer(
            """
            export Control__OwnerPasswordFile=""
            # /tmp is the one place both identities can write, so the agent child
            # can report back to a controller that cannot enter /data/home.
            chmod 1777 /tmp
            /usr/local/bin/control-entrypoint /bin/sh -c '
              grep -E "^Cap(Eff|Prm|Bnd):" /proc/self/status
              # The launcher elevates to root before dropping, so it could hand a
              # child anything left in the bounding set. Prove it cannot.
              # The launcher sets umask 0077 on the child, so the child widens the
              # report itself; the controller cannot read an agent-mode-0600 file.
              HOME=/data/home agentcontrol-launch tmux new-session -d -s capprobe /bin/sh -c \
                "grep -E \"^CapBnd:\" /proc/self/status | sed s/CapBnd/ChildBnd/ > /tmp/cap.txt 2>&1; chmod 644 /tmp/cap.txt; sleep 10"
              sleep 3
              echo CHILD_START
              cat /tmp/cap.txt 2>/dev/null || echo CHILD_UNREADABLE
            '
            """);

        var output = result.StandardOutput;

        // The controller itself holds nothing.
        Assert.Contains("CapEff:\t0000000000000000", output, StringComparison.Ordinal);
        Assert.Contains("CapPrm:\t0000000000000000", output, StringComparison.Ordinal);

        // CHOWN(0) | DAC_OVERRIDE(1) | FOWNER(3) | FSETID(4) | SETPCAP(8) are gone;
        // SETUID(7) | SETGID(6) | KILL(5) remain for the launcher. 0xE0 = bits 5,6,7.
        Assert.Equal(0x00000000000000E0UL, ExtractCapability(output, "CapBnd"));

        // The launcher's own child cannot exceed the capped set either. Its
        // PR_CAPBSET_DROP is best-effort and fails without CAP_SETPCAP (which is
        // now gone on purpose), so the cap here is the entrypoint's, not the
        // launcher's - which is exactly why the entrypoint has to do it.
        Assert.DoesNotContain("CHILD_UNREADABLE", output, StringComparison.Ordinal);
        var childBounding = ExtractCapability(output, "ChildBnd");
        Assert.Equal(0UL, childBounding & 0x11BUL); // CHOWN|DAC_OVERRIDE|FOWNER|FSETID|SETPCAP
        Assert.Equal(0x00000000000000E0UL, childBounding);
    }

    /// <summary>
    /// Without <c>CAP_SETPCAP</c> the entrypoint cannot shrink the bounding set,
    /// so the controller would keep CHOWN/DAC_OVERRIDE/FOWNER reachable through
    /// the launcher's root phase. That is a weaker boundary than the image
    /// documents, so it must refuse to start rather than serve it.
    /// </summary>
    [DockerFact]
    public void EntrypointRefusesToStartWithoutTheCapabilityToDropStartupCapabilities()
    {
        RequireDockerWhenMandatory();

        // The deployed flag set with SETPCAP removed, so this stays a test of the
        // missing capability rather than of an independently written flag list.
        var withoutSetpcap = new List<string>(RunFlags);
        var index = withoutSetpcap.IndexOf("SETPCAP");
        Assert.True(index > 0, "SETPCAP is no longer in the deployed capability set");
        withoutSetpcap.RemoveRange(index - 1, 2);

        var arguments = new List<string>(withoutSetpcap)
        {
            ResolvedImage,
            "-c",
            """
            export Control__OwnerPasswordFile=""
            /usr/local/bin/control-entrypoint /bin/true; echo EXIT=$?
            """,
        };

        var result = Run("docker", arguments, TimeSpan.FromMinutes(3));

        Assert.Contains("EXIT=1", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("CAP_SETPCAP is not available", result.StandardError, StringComparison.Ordinal);
    }

    private static ulong ExtractCapability(string output, string name)
    {
        var marker = name + ":\t";
        var start = output.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{name} was not reported:\n{output}");
        start += marker.Length;
        var end = output.IndexOf('\n', start);
        var value = (end < 0 ? output[start..] : output[start..end]).Trim();
        return Convert.ToUInt64(value, 16);
    }

    [DockerFact]
    public void ContainerCarriesExactlyOneSetuidBinary()
    {
        var result = RunInContainer("find / -xdev -perm /6000 -type f 2>/dev/null | sort");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Launcher, result.StandardOutput.Trim());
    }

    [DockerFact]
    public void LauncherIsRootOwnedControllerGroupAndNotAgentExecutable()
    {
        var result = RunInContainer($"stat -c '%U %G %a' {Launcher}");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("root control 4750", result.StandardOutput.Trim());
    }

    /// <summary>
    /// The model-facing identity must not be able to invoke the privileged
    /// launcher. Two independent layers are asserted, because either one alone
    /// could be undone by a packaging mistake:
    /// <list type="number">
    /// <item>the kernel refuses the exec outright for a real agent process,
    /// which holds no capabilities (mode 4750 root:control); and</item>
    /// <item>even when a capability-holding parent forces the exec through
    /// (<c>DAC_OVERRIDE</c> bypasses the mode check), the launcher's own caller
    /// check refuses to perform any action.</item>
    /// </list>
    /// </summary>
    [DockerFact]
    public void AgentIdentityCannotExecuteOrDriveTheLauncher()
    {
        var result = RunInContainer(
            $$"""
            # A real agent process: zero capabilities, exactly like OpenCode/tmux.
            setpriv --reuid 1000 --regid 1000 --clear-groups /bin/sh -c \
                '{{Launcher}} tmux new-session -d -s pwn /bin/sh -c "id > /tmp/pwned"; echo KERNEL_EXIT=$?'
            # Forced exec by a capability-holding parent, bypassing the file mode.
            setpriv --reuid 1000 --regid 1000 --clear-groups \
                {{Launcher}} tmux new-session -d -s pwn2 /bin/sh -c 'id > /tmp/pwned2'
            echo CALLER_CHECK_EXIT=$?
            sleep 1
            test -f /tmp/pwned -o -f /tmp/pwned2 && echo AGENT_GAINED_PRIVILEGE || echo NO_ACTION_PERFORMED
            """);

        var output = result.StandardOutput;

        // Layer 1: the kernel denies the exec (126 = cannot execute).
        Assert.Contains("KERNEL_EXIT=126", output, StringComparison.Ordinal);

        // Layer 2: the launcher refuses a non-controller caller (64 = usage fail).
        Assert.Contains("CALLER_CHECK_EXIT=64", output, StringComparison.Ordinal);
        Assert.Contains(
            "only the controller identity may invoke this launcher",
            result.StandardError + output,
            StringComparison.Ordinal);

        // Neither path performed a privileged action.
        Assert.Contains("NO_ACTION_PERFORMED", output, StringComparison.Ordinal);
        Assert.DoesNotContain("AGENT_GAINED_PRIVILEGE", output, StringComparison.Ordinal);
    }

    [DockerFact]
    public void AgentIdentityCannotReadTheControllerPrivateStore()
    {
        var result = RunInContainer(
            """
            install -o 1001 -g 1001 -m 600 /dev/null /control-data/runtime.json
            echo '{"organizationId":"org-secret"}' > /control-data/runtime.json
            setpriv --reuid 1000 --regid 1000 --clear-groups cat /control-data/runtime.json; echo READ=$?
            setpriv --reuid 1000 --regid 1000 --clear-groups ls /control-data; echo LIST=$?
            setpriv --reuid 1000 --regid 1000 --clear-groups rm -f /control-data/runtime.json; echo UNLINK=$?
            setpriv --reuid 1000 --regid 1000 --clear-groups mv /control-data/runtime.json /tmp/stolen; echo RENAME=$?
            test -f /control-data/runtime.json && echo STATE-INTACT
            """);

        // Reads, directory listing, unlink and rename are all denied: the 0700
        // parent protects the file itself and its directory entry.
        Assert.Contains("READ=1", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("LIST=2", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("UNLINK=1", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("RENAME=1", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("STATE-INTACT", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("org-secret", result.StandardOutput, StringComparison.Ordinal);
    }

    [DockerFact]
    public void AgentIdentityCannotReadTheOwnerSecretOrReplaceIt()
    {
        var result = RunInContainer(
            """
            mkdir -p /run/agentcontrol-secrets
            chown 1001:1001 /run/agentcontrol-secrets && chmod 0700 /run/agentcontrol-secrets
            echo 'owner-password-value-0123456789' > /run/agentcontrol-secrets/owner-password
            chown 1001:1001 /run/agentcontrol-secrets/owner-password
            chmod 0600 /run/agentcontrol-secrets/owner-password
            setpriv --reuid 1000 --regid 1000 --clear-groups cat /run/agentcontrol-secrets/owner-password; echo READ=$?
            setpriv --reuid 1000 --regid 1000 --clear-groups sh -c 'echo attacker > /run/agentcontrol-secrets/owner-password' && echo WRITE_SUCCEEDED || echo WRITE_DENIED
            setpriv --reuid 1000 --regid 1000 --clear-groups rm -f /run/agentcontrol-secrets/owner-password && echo UNLINK_SUCCEEDED || echo UNLINK_DENIED
            setpriv --reuid 1000 --regid 1000 --clear-groups mv /run/agentcontrol-secrets/owner-password /tmp/stolen && echo RENAME_SUCCEEDED || echo RENAME_DENIED
            grep -q 'owner-password-value-0123456789' /run/agentcontrol-secrets/owner-password && echo SECRET_INTACT
            setpriv --reuid 1001 --regid 1001 --clear-groups cat /run/agentcontrol-secrets/owner-password >/dev/null; echo CONTROLLER_READ=$?
            """);

        // Reads, writes and - because the parent directory is 0700 - rename and
        // unlink of the directory entry are all denied to the agent.
        Assert.Contains("READ=1", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("WRITE_DENIED", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("UNLINK_DENIED", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("RENAME_DENIED", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("SECRET_INTACT", result.StandardOutput, StringComparison.Ordinal);

        // The controller must still be able to read it, or the boundary is useless.
        Assert.Contains("CONTROLLER_READ=0", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("owner-password-value", result.StandardOutput, StringComparison.Ordinal);
    }

    [DockerFact]
    public void OrientationIsHostOwnedAndAgentReadableButNotAgentWritable()
    {
        var result = RunInContainer(
            """
            install -o 1001 -g 1001 -m 644 /dev/null /agent-config/agentcontrol-instructions.md
            echo '# orientation' > /agent-config/agentcontrol-instructions.md
            setpriv --reuid 1000 --regid 1000 --clear-groups cat /agent-config/agentcontrol-instructions.md >/dev/null; echo READ=$?
            setpriv --reuid 1000 --regid 1000 --clear-groups sh -c 'echo tampered > /agent-config/agentcontrol-instructions.md' && echo WRITE_SUCCEEDED || echo WRITE_DENIED
            setpriv --reuid 1000 --regid 1000 --clear-groups rm -f /agent-config/agentcontrol-instructions.md && echo UNLINK_SUCCEEDED || echo UNLINK_DENIED
            setpriv --reuid 1000 --regid 1000 --clear-groups mv /agent-config/agentcontrol-instructions.md /tmp/swapped && echo RENAME_SUCCEEDED || echo RENAME_DENIED
            grep -q '# orientation' /agent-config/agentcontrol-instructions.md && echo INTACT
            """);

        // Host-owned means the agent reads its orientation but cannot rewrite,
        // unlink or swap it - the directory is not agent-writable.
        Assert.Contains("READ=0", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("WRITE_DENIED", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("UNLINK_DENIED", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("RENAME_DENIED", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("INTACT", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole point of the launcher: a process the controller starts must run
    /// as the agent, with no capabilities, no_new_privs set, and an environment
    /// rebuilt from the allow-list rather than inherited.
    /// </summary>
    [DockerFact]
    public void LaunchedChildRunsAsTheAgentWithNoPrivilegesOrInjectedEnvironment()
    {
        var result = RunInContainer(
            """
            chown -R 1000:1000 /data
            setpriv --reuid 1001 --regid 1001 --init-groups env \
                LD_PRELOAD=/tmp/evil.so \
                PYTHONPATH=/tmp/evil \
                NODE_OPTIONS=--require=/tmp/evil \
                BASH_ENV=/tmp/evil.sh \
                DOTNET_STARTUP_HOOKS=/tmp/evil.dll \
                Control__OwnerPasswordFile=/run/agentcontrol-secrets/owner-password \
                CONTROLLER_ONLY_VAR=must-not-leak \
                HOME=/data/home \
                agentcontrol-launch tmux new-session -d -s probe /bin/sh -c \
                '{ id; grep -E "^Uid:|^Gid:|^CapEff:|^CapPrm:|NoNewPrivs" /proc/self/status; echo ENVSTART; env | sort; } > /data/probe.txt 2>&1; sleep 30'
            sleep 3
            cat /data/probe.txt
            """);

        var output = result.StandardOutput;

        // Identity and privileges of the launched child.
        Assert.Contains("uid=1000(agent) gid=1000(agent)", output, StringComparison.Ordinal);
        Assert.Contains("Uid:\t1000\t1000\t1000\t1000", output, StringComparison.Ordinal);
        Assert.Contains("Gid:\t1000\t1000\t1000\t1000", output, StringComparison.Ordinal);
        Assert.Contains("CapEff:\t0000000000000000", output, StringComparison.Ordinal);
        Assert.Contains("CapPrm:\t0000000000000000", output, StringComparison.Ordinal);
        Assert.Contains("NoNewPrivs:\t1", output, StringComparison.Ordinal);

        // Loader/interpreter hooks and controller configuration never reach it.
        var environment = output[output.IndexOf("ENVSTART", StringComparison.Ordinal)..];
        foreach (var injected in new[]
        {
            "LD_PRELOAD",
            "PYTHONPATH",
            "NODE_OPTIONS",
            "BASH_ENV",
            "DOTNET_STARTUP_HOOKS",
            "Control__OwnerPasswordFile",
            "CONTROLLER_ONLY_VAR",
        })
        {
            Assert.DoesNotContain(injected, environment, StringComparison.Ordinal);
        }

        Assert.Contains("PATH=/usr/local/bin:/usr/local/sbin:/usr/bin:/usr/sbin:/bin:/sbin", environment, StringComparison.Ordinal);
    }

    /// <summary>
    /// Cross-UID termination and reaping. The controller genuinely cannot signal
    /// its own agent children, which is exactly why the launcher keeps a
    /// verified <c>signal</c> operation.
    /// </summary>
    [DockerFact]
    public void ControllerTerminatesAndReapsAgentChildrenOnlyThroughTheLauncher()
    {
        var result = RunInContainer(
            """
            chown -R 1000:1000 /data
            setpriv --reuid 1001 --regid 1001 --init-groups /bin/bash -c '
              agentcontrol-launch tmux new-session -d -s agentcontrol /bin/sh -c "sleep 60" >/dev/null 2>&1
              sleep 1
              mkfifo /tmp/bridge-in; exec 3<>/tmp/bridge-in
              agentcontrol-launch pty /data/home agentcontrol <&3 >/dev/null 2>&1 &
              CHILD=$!
              sleep 2
              grep -E "^Uid:" /proc/$CHILD/status
              # kill -0 is itself denied across UIDs, so liveness is read from
              # /proc: a running child has a status file and is not a zombie.
              alive() { grep -qE "^State:[[:space:]]+[RSD]" /proc/$1/status 2>/dev/null; }
              kill -TERM $CHILD 2>/dev/null; echo DIRECT_KILL=$?
              sleep 1
              alive $CHILD && echo SURVIVED_DIRECT_KILL || echo DIED_FROM_DIRECT_KILL
              agentcontrol-launch signal $CHILD TERM; echo LAUNCHER_SIGNAL=$?
              sleep 1
              alive $CHILD && echo STILL_ALIVE || echo TERMINATED
              wait $CHILD 2>/dev/null; echo REAPED
            '
            """);

        var output = result.StandardOutput;

        // The child really is the agent identity, and really does survive the
        // controller's own signal: this is why the launcher keeps `signal`.
        Assert.Contains("Uid:\t1000\t1000\t1000\t1000", output, StringComparison.Ordinal);
        Assert.Contains("DIRECT_KILL=1", output, StringComparison.Ordinal);
        Assert.Contains("SURVIVED_DIRECT_KILL", output, StringComparison.Ordinal);
        Assert.DoesNotContain("DIED_FROM_DIRECT_KILL", output, StringComparison.Ordinal);

        // The launcher terminates it, and the controller still reaps it.
        Assert.Contains("LAUNCHER_SIGNAL=0", output, StringComparison.Ordinal);
        Assert.Contains("TERMINATED", output, StringComparison.Ordinal);
        Assert.DoesNotContain("STILL_ALIVE", output, StringComparison.Ordinal);
        Assert.Contains("REAPED", output, StringComparison.Ordinal);
    }

    [DockerFact]
    public void SignalRefusesTargetsThatAreNotOwnedAgentChildren()
    {
        var result = RunInContainer(
            """
            sleep 60 &
            FOREIGN=$!
            setpriv --reuid 1001 --regid 1001 --init-groups agentcontrol-launch signal 1 TERM; echo INIT=$?
            setpriv --reuid 1001 --regid 1001 --init-groups agentcontrol-launch signal $FOREIGN TERM; echo FOREIGN=$?
            setpriv --reuid 1001 --regid 1001 --init-groups agentcontrol-launch signal 4294967295 TERM; echo ABSENT=$?
            setpriv --reuid 1001 --regid 1001 --init-groups agentcontrol-launch signal 2 HUP; echo BADSIG=$?
            setpriv --reuid 1001 --regid 1001 --init-groups agentcontrol-launch signal notanumber TERM; echo NAN=$?
            grep -qE "^State:[[:space:]]+[RSD]" /proc/$FOREIGN/status && echo FOREIGN_ALIVE
            grep -qE "^State:[[:space:]]+[RSD]" /proc/1/status && echo INIT_ALIVE
            """);

        var output = result.StandardOutput;

        // Nothing but a verified agent child of the caller can be signalled, and
        // only TERM/KILL are accepted at all.
        Assert.Contains("INIT=64", output, StringComparison.Ordinal);
        Assert.Contains("FOREIGN=64", output, StringComparison.Ordinal);
        Assert.Contains("BADSIG=64", output, StringComparison.Ordinal);
        Assert.Contains("NAN=64", output, StringComparison.Ordinal);
        Assert.Contains("FOREIGN_ALIVE", output, StringComparison.Ordinal);
        Assert.Contains("INIT_ALIVE", output, StringComparison.Ordinal);
    }

    [DockerFact]
    public void LauncherRejectsUnregisteredOperationsAndOutOfTreePaths()
    {
        var result = RunInContainer(
            """
            run() { setpriv --reuid 1001 --regid 1001 --init-groups agentcontrol-launch "$@" >/dev/null 2>&1; echo "$1:$?"; }
            run exec /bin/sh
            run shell
            run acp 0.0.0.0 4096 /data/workspace
            run acp 127.0.0.1 4096 /etc
            run acp 127.0.0.1 4096 /data/../etc
            run acp 127.0.0.1 99999 /data/workspace
            run pty /etc agentcontrol
            run pty /data/home 'bad;name'
            run tmux kill-server
            run tmux run-shell 'id > /tmp/pwned'
            test -f /tmp/pwned && echo PWNED || echo NO-SHELL-ESCAPE
            """);

        var output = result.StandardOutput;

        // Every unregistered operation, non-loopback bind, escaped path, invalid
        // port and non-allow-listed tmux subcommand is refused with the usage
        // failure code, not silently downgraded to a weaker launch.
        Assert.Contains("exec:64", output, StringComparison.Ordinal);
        Assert.Contains("shell:64", output, StringComparison.Ordinal);
        Assert.Equal(4, Occurrences(output, "acp:64"));
        Assert.Equal(2, Occurrences(output, "pty:64"));
        Assert.Equal(2, Occurrences(output, "tmux:64"));
        Assert.Contains("NO-SHELL-ESCAPE", output, StringComparison.Ordinal);
    }

    [DockerFact]
    public void TmuxLaunchAcceptsTheBoundedLargeEnvironmentUsedByTheAttachClient()
    {
        var result = RunInContainer(
            """
            mkdir -p /data/home && chown 1000:1000 /data/home
            args=""
            for number in $(seq 1 100); do
              args="$args TEST_$number=value_$number"
            done
            setpriv --reuid 1001 --regid 1001 --init-groups /bin/sh -c \
              "agentcontrol-launch tmux new-session -d -s large-env /usr/bin/env -i $args TERM=tmux-256color /bin/sleep 30"
            echo LAUNCH=$?
            setpriv --reuid 1001 --regid 1001 --init-groups agentcontrol-launch tmux has-session -t =large-env
            echo PRESENT=$?
            setpriv --reuid 1001 --regid 1001 --init-groups agentcontrol-launch tmux kill-session -t =large-env
            """);

        Assert.Contains("LAUNCH=0", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("PRESENT=0", result.StandardOutput, StringComparison.Ordinal);
    }

    private static int Occurrences(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;

    /// <summary>
    /// The textual prefix check bounds the request; it does not bound where a
    /// symlink inside the agent tree resolves to. The launcher re-checks the
    /// directory it actually landed in after the chdir, so a working directory
    /// that escaped <c>/data</c> refuses the launch instead of starting there.
    /// </summary>
    [DockerFact]
    public void LauncherRefusesAWorkingDirectoryThatResolvesOutsideTheAgentTree()
    {
        var result = RunInContainer(
            """
            export Control__OwnerPasswordFile=""
            /usr/local/bin/agentcontrol-prepare-layout || echo LAYOUT_FAILED
            # Only the agent can plant this, and only inside its own subtree -
            # which is exactly the residual case the textual check cannot see.
            setpriv --reuid 1000 --regid 1000 --clear-groups \
                ln -s /etc /data/workspace/escape
            echo "PLANTED=$?"

            run() {
              setpriv --reuid 1001 --regid 1001 --init-groups \
                agentcontrol-launch "$@" 2>&1 | tail -1
            }
            # Textually valid (under /data/), resolves to /etc.
            run acp 127.0.0.1 4096 /data/workspace/escape
            run pty /data/workspace/escape agentcontrol
            # The legitimate directory still works: this is a check, not a ban.
            setpriv --reuid 1001 --regid 1001 --init-groups env HOME=/data/home \
                agentcontrol-launch tmux new-session -d -s legit -c /data/workspace \
                /bin/sh -c 'pwd > /data/workspace/where.txt; sleep 10'
            sleep 2
            echo "LEGIT_CWD=$(cat /data/workspace/where.txt 2>/dev/null)"
            """);

        var output = result.StandardOutput;

        Assert.Contains("PLANTED=0", output, StringComparison.Ordinal);

        // Both escape attempts are refused after the chdir resolves them.
        Assert.Equal(
            2,
            Occurrences(output, "the working directory resolved outside the agent data root"));

        // ...and a real agent directory is still usable.
        Assert.Contains("LEGIT_CWD=/data/workspace", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Restart safety: a second container start over the same volumes must be a
    /// no-op that preserves existing agent data and controller state.
    /// </summary>
    [DockerFact]
    public void EntrypointIsIdempotentAndPreservesExistingData()
    {
        var result = RunInContainer(
            """
            export Control__OwnerPasswordFile=""
            # The pre-isolation volume layout: everything under the shared UID.
            chown 1000:1000 /data && chmod 0755 /data
            echo 'session history' > /data/home/history.txt && chown 1000:1000 /data/home/history.txt
            echo '{"organizationId":"org-keep"}' > /data/runtime.json && chown 1000:1000 /data/runtime.json
            LEGACY_INODE=$(stat -c '%i' /data/runtime.json)
            for pass in 1 2; do
              /usr/local/bin/control-entrypoint /bin/true || echo "PASS${pass}_FAILED"
            done
            echo "HISTORY=$(cat /data/home/history.txt)"
            echo "LEGACY=$(cat /data/runtime.json)"
            echo "ADOPTED=$(cat /control-data/runtime.json)"
            stat -c 'LEGACY_MODE=%u:%g:%a' /data/runtime.json
            stat -c 'PRIVATE_MODE=%u:%g:%a' /control-data/runtime.json
            stat -c 'PRIVATE_DIR=%u:%g:%a' /control-data
            stat -c 'AGENT_HOME=%u:%g:%a' /data/home
            stat -c 'AGENT_WORKSPACE=%u:%g:%a' /data/workspace
            stat -c 'DATA_ROOT=%u:%g:%a' /data
            test "$LEGACY_INODE" = "$(stat -c '%i' /data/runtime.json)" && echo LEGACY_INODE_STABLE
            # No leftover temporary from the atomic adoption publish.
            ls /control-data/.runtime.* >/dev/null 2>&1 && echo TEMP_LEAKED || echo NO_TEMP_LEAK
            # The agent can no longer read the tmux owner token in the rollback copy.
            setpriv --reuid 1000 --regid 1000 --clear-groups cat /data/runtime.json >/dev/null 2>&1 \
                && echo AGENT_READ_LEGACY || echo AGENT_CANNOT_READ_LEGACY
            """);

        var output = result.StandardOutput;

        Assert.DoesNotContain("_FAILED", output, StringComparison.Ordinal);

        // Existing agent history and the legacy state survive untouched...
        Assert.Contains("HISTORY=session history", output, StringComparison.Ordinal);
        Assert.Contains("""LEGACY={"organizationId":"org-keep"}""", output, StringComparison.Ordinal);
        Assert.Contains("LEGACY_INODE_STABLE", output, StringComparison.Ordinal);

        // ...and the adoption is a copy, so rollback is possible.
        Assert.Contains("""ADOPTED={"organizationId":"org-keep"}""", output, StringComparison.Ordinal);
        Assert.Contains("NO_TEMP_LEAK", output, StringComparison.Ordinal);

        // The legacy copy is reduced to root-only so the agent loses access to
        // the owner token it carries, without the bytes being destroyed.
        Assert.Contains("LEGACY_MODE=0:0:600", output, StringComparison.Ordinal);
        Assert.Contains("AGENT_CANNOT_READ_LEGACY", output, StringComparison.Ordinal);
        Assert.Contains("PRIVATE_MODE=1001:1001:600", output, StringComparison.Ordinal);
        Assert.Contains("PRIVATE_DIR=1001:1001:700", output, StringComparison.Ordinal);

        // Existing volume directories keep the agent's ownership; only the
        // /data parent moves to root so the rename/symlink swap is impossible.
        Assert.Contains("AGENT_HOME=1000:1000:700", output, StringComparison.Ordinal);
        Assert.Contains("AGENT_WORKSPACE=1000:1000:700", output, StringComparison.Ordinal);
        Assert.Contains("DATA_ROOT=0:0:755", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The decisive rollback case: the isolated controller advanced its private
    /// state, so the legacy file is stale. Handing that stale file back to UID
    /// 1000 would resume the pre-isolation image on a dead session with a dead
    /// tmux owner token; the rollback must republish the <em>current</em>
    /// controller-private bytes to <c>/data/runtime.json</c> instead.
    /// </summary>
    [DockerFact]
    public void RevertIsolationRepublishesTheCurrentStateSoTheOldImageResumesLatest()
    {
        var revertCode = ExtractPythonBlock("REVERT_CODE");
        var result = RunInContainer(
            $$"""
            mkdir -p /run/agentcontrol-secrets && chmod 0755 /run/agentcontrol-secrets
            echo 'owner-password-value-0123456789' > /run/agentcontrol-secrets/owner-password
            chown 1001:1001 /run/agentcontrol-secrets/owner-password
            chmod 0600 /run/agentcontrol-secrets/owner-password
            export Control__OwnerPasswordFile=/run/agentcontrol-secrets/owner-password

            chown 1000:1000 /data && chmod 0755 /data
            echo '{"organizationId":"org-keep","sessionId":"ses_OLD","tmuxOwnerToken":"tok-OLD"}' > /data/runtime.json
            chown 1000:1000 /data/runtime.json
            SECRET_INODE=$(stat -c '%i' /run/agentcontrol-secrets/owner-password)

            /usr/local/bin/control-entrypoint /bin/true || echo ENTRYPOINT_FAILED
            stat -c 'ISOLATED_LEGACY=%u:%g:%a' /data/runtime.json
            stat -c 'ISOLATED_DATA=%u:%g:%a' /data

            # What an isolated run actually does: it advances the private state
            # only. The legacy path is never updated and therefore goes stale.
            echo '{"organizationId":"org-keep","sessionId":"ses_NEW","tmuxOwnerToken":"tok-NEW"}' > /control-data/runtime.json
            chown 1001:1001 /control-data/runtime.json && chmod 0600 /control-data/runtime.json

            cat > /tmp/revert.py <<'REVERT_EOF'
            {{revertCode}}
            REVERT_EOF
            python3 /tmp/revert.py /run/agentcontrol-secrets/owner-password 1000 1000 /data /control-data 0
            echo "REVERT_EXIT=$?"

            stat -c 'REVERTED_SECRET=%u:%g:%a' /run/agentcontrol-secrets/owner-password
            stat -c 'REVERTED_LEGACY=%u:%g:%a' /data/runtime.json
            stat -c 'REVERTED_DATA=%u:%g:%a' /data
            test "$SECRET_INODE" = "$(stat -c '%i' /run/agentcontrol-secrets/owner-password)" && echo SECRET_INODE_STABLE

            # The decisive check: UID 1000 reads the secret, and the state it
            # reads is the LATEST session, not the one frozen at adoption.
            setpriv --reuid 1000 --regid 1000 --clear-groups \
                grep -q 'owner-password-value-0123456789' /run/agentcontrol-secrets/owner-password \
                && echo LEGACY_SECRET_READABLE || echo LEGACY_SECRET_UNREADABLE
            echo "RESUMED_STATE=$(setpriv --reuid 1000 --regid 1000 --clear-groups cat /data/runtime.json)"
            setpriv --reuid 1000 --regid 1000 --clear-groups touch /data/newdir-probe \
                && echo LEGACY_DATA_WRITABLE || echo LEGACY_DATA_READONLY

            # The original pre-isolation state is preserved separately, with
            # hash evidence, because the legacy path is no longer that record.
            echo "SNAPSHOT=$(cat /control-data/runtime.pre-isolation.json)"
            python3 -c "
            import hashlib, json, pathlib
            meta = json.loads(pathlib.Path('/control-data/runtime.pre-isolation.meta.json').read_text())
            body = pathlib.Path('/control-data/runtime.pre-isolation.json').read_bytes()
            print('SNAPSHOT_HASH_MATCHES' if meta['sha256'] == hashlib.sha256(body).hexdigest() else 'SNAPSHOT_HASH_MISMATCH')
            print('SNAPSHOT_SIZE_MATCHES' if meta['bytes'] == len(body) else 'SNAPSHOT_SIZE_MISMATCH')
            print('SNAPSHOT_SOURCE=' + meta['source'])
            "
            stat -c 'SNAPSHOT_META=%u:%g:%a' /control-data/runtime.pre-isolation.json

            # The private copy is retained untouched, and the rollback is recorded.
            stat -c 'PRIVATE_KEPT=%u:%g:%a' /control-data/runtime.json
            echo "PRIVATE_CONTENT=$(cat /control-data/runtime.json)"
            test -f /control-data/rollback.active && echo MARKER_RECORDED || echo MARKER_MISSING
            ls /data/.runtime.json.*.tmp >/dev/null 2>&1 && echo TEMP_LEAKED || echo NO_TEMP_LEAK
            """);

        var output = result.StandardOutput;

        Assert.DoesNotContain("ENTRYPOINT_FAILED", output, StringComparison.Ordinal);

        // While isolated: root-only legacy state the agent cannot read.
        Assert.Contains("ISOLATED_LEGACY=0:0:600", output, StringComparison.Ordinal);
        Assert.Contains("ISOLATED_DATA=0:0:755", output, StringComparison.Ordinal);

        // After the revert: the shared identity owns everything it used to.
        Assert.Contains("REVERT_EXIT=0", output, StringComparison.Ordinal);
        Assert.Contains("REVERTED_SECRET=1000:1000:600", output, StringComparison.Ordinal);
        Assert.Contains("REVERTED_LEGACY=1000:1000:600", output, StringComparison.Ordinal);
        Assert.Contains("REVERTED_DATA=1000:1000:755", output, StringComparison.Ordinal);

        // The secret is still metadata-only: same inode, same bytes.
        Assert.Contains("SECRET_INODE_STABLE", output, StringComparison.Ordinal);
        Assert.Contains("LEGACY_SECRET_READABLE", output, StringComparison.Ordinal);
        Assert.Contains("LEGACY_DATA_WRITABLE", output, StringComparison.Ordinal);

        // The whole point: the old image resumes the latest session and token.
        Assert.Contains("ses_NEW", Extract(output, "RESUMED_STATE="), StringComparison.Ordinal);
        Assert.Contains("tok-NEW", Extract(output, "RESUMED_STATE="), StringComparison.Ordinal);
        Assert.DoesNotContain("ses_OLD", Extract(output, "RESUMED_STATE="), StringComparison.Ordinal);
        Assert.Contains("NO_TEMP_LEAK", output, StringComparison.Ordinal);

        // The original pre-isolation state survives independently, with evidence.
        Assert.Contains("ses_OLD", Extract(output, "SNAPSHOT="), StringComparison.Ordinal);
        Assert.Contains("SNAPSHOT_HASH_MATCHES", output, StringComparison.Ordinal);
        Assert.Contains("SNAPSHOT_SIZE_MATCHES", output, StringComparison.Ordinal);
        Assert.Contains("SNAPSHOT_SOURCE=/data/runtime.json", output, StringComparison.Ordinal);
        Assert.Contains("SNAPSHOT_META=1001:1001:600", output, StringComparison.Ordinal);

        // Rolling forward again is still possible, and is now interlocked.
        Assert.Contains("PRIVATE_KEPT=1001:1001:600", output, StringComparison.Ordinal);
        Assert.Contains("ses_NEW", Extract(output, "PRIVATE_CONTENT="), StringComparison.Ordinal);
        Assert.Contains("MARKER_RECORDED", output, StringComparison.Ordinal);

        // The credential never reaches the operator's terminal.
        Assert.DoesNotContain("owner-password-value", output, StringComparison.Ordinal);
        Assert.DoesNotContain("owner-password-value", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A deployment that was isolated from the start has no legacy file at all.
    /// The rollback must still produce a <c>/data/runtime.json</c> the old image
    /// can read, because otherwise it starts a brand new organization and the
    /// isolated run's history is stranded.
    /// </summary>
    [DockerFact]
    public void RevertIsolationPublishesStateForADeploymentThatNeverHadALegacyFile()
    {
        var revertCode = ExtractPythonBlock("REVERT_CODE");
        var result = RunInContainer(
            $$"""
            mkdir -p /run/agentcontrol-secrets && chmod 0755 /run/agentcontrol-secrets
            echo 'owner-password-value-0123456789' > /run/agentcontrol-secrets/owner-password
            chown 1001:1001 /run/agentcontrol-secrets/owner-password
            chmod 0600 /run/agentcontrol-secrets/owner-password
            export Control__OwnerPasswordFile=/run/agentcontrol-secrets/owner-password

            # A fresh isolated deployment: no /data/runtime.json ever existed.
            rm -f /data/runtime.json
            /usr/local/bin/control-entrypoint /bin/true || echo ENTRYPOINT_FAILED
            test -e /data/runtime.json && echo UNEXPECTED_LEGACY || echo NO_LEGACY_FILE

            echo '{"organizationId":"org-fresh","sessionId":"ses_fresh"}' > /control-data/runtime.json
            chown 1001:1001 /control-data/runtime.json && chmod 0600 /control-data/runtime.json

            cat > /tmp/revert.py <<'REVERT_EOF'
            {{revertCode}}
            REVERT_EOF
            python3 /tmp/revert.py /run/agentcontrol-secrets/owner-password 1000 1000 /data /control-data 0
            echo "FRESH_REVERT_EXIT=$?"
            stat -c 'PUBLISHED=%u:%g:%a' /data/runtime.json
            echo "PUBLISHED_STATE=$(setpriv --reuid 1000 --regid 1000 --clear-groups cat /data/runtime.json)"

            # And with no private state either, it must fail closed rather than
            # quietly hand back a deployment with no runtime identity at all.
            rm -f /control-data/runtime.json /control-data/rollback.active
            python3 /tmp/revert.py /run/agentcontrol-secrets/owner-password 1000 1000 /data /control-data 0
            echo "NO_STATE_EXIT=$?"
            python3 /tmp/revert.py /run/agentcontrol-secrets/owner-password 1000 1000 /data /control-data 1
            echo "NO_STATE_OPTIN_EXIT=$?"
            """);

        var output = result.StandardOutput;

        Assert.DoesNotContain("ENTRYPOINT_FAILED", output, StringComparison.Ordinal);
        Assert.Contains("NO_LEGACY_FILE", output, StringComparison.Ordinal);

        // The private state is published to the legacy path the old image reads.
        Assert.Contains("FRESH_REVERT_EXIT=0", output, StringComparison.Ordinal);
        Assert.Contains("PUBLISHED=1000:1000:600", output, StringComparison.Ordinal);
        Assert.Contains("org-fresh", Extract(output, "PUBLISHED_STATE="), StringComparison.Ordinal);

        // No private state at all is an explicit operator decision, not a default.
        Assert.Contains("NO_STATE_EXIT=1", output, StringComparison.Ordinal);
        Assert.Contains("--accept-missing-runtime-state", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("NO_STATE_OPTIN_EXIT=0", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Three volumes cannot be changed in one transaction, so the rollback is
    /// replayable rather than atomic. Running it twice must converge on the same
    /// result instead of failing on the ownership it already handed back.
    /// </summary>
    [DockerFact]
    public void RevertIsolationIsIdempotentWhenReRunAfterAPartialFailure()
    {
        var revertCode = ExtractPythonBlock("REVERT_CODE");
        var result = RunInContainer(
            $$"""
            mkdir -p /run/agentcontrol-secrets && chmod 0755 /run/agentcontrol-secrets
            echo 'owner-password-value-0123456789' > /run/agentcontrol-secrets/owner-password
            chown 1001:1001 /run/agentcontrol-secrets/owner-password
            chmod 0600 /run/agentcontrol-secrets/owner-password
            export Control__OwnerPasswordFile=/run/agentcontrol-secrets/owner-password

            echo '{"organizationId":"org-keep","sessionId":"ses_adopt"}' > /data/runtime.json
            chown 1000:1000 /data/runtime.json
            /usr/local/bin/control-entrypoint /bin/true || echo ENTRYPOINT_FAILED
            echo '{"organizationId":"org-keep","sessionId":"ses_live"}' > /control-data/runtime.json
            chown 1001:1001 /control-data/runtime.json && chmod 0600 /control-data/runtime.json

            cat > /tmp/revert.py <<'REVERT_EOF'
            {{revertCode}}
            REVERT_EOF
            run() { python3 /tmp/revert.py /run/agentcontrol-secrets/owner-password 1000 1000 /data /control-data 0; }

            # Simulate an interruption after the secret was already handed back:
            # the replay must accept the already-reverted owners, not refuse them.
            chown 1000:1000 /run/agentcontrol-secrets/owner-password
            run; echo "REPLAY1_EXIT=$?"
            run; echo "REPLAY2_EXIT=$?"

            stat -c 'FINAL_SECRET=%u:%g:%a' /run/agentcontrol-secrets/owner-password
            stat -c 'FINAL_LEGACY=%u:%g:%a' /data/runtime.json
            stat -c 'FINAL_DATA=%u:%g:%a' /data
            echo "FINAL_STATE=$(cat /data/runtime.json)"
            echo "SNAPSHOT_STILL=$(cat /control-data/runtime.pre-isolation.json)"
            ls /data/.runtime.json.*.tmp >/dev/null 2>&1 && echo TEMP_LEAKED || echo NO_TEMP_LEAK
            """);

        var output = result.StandardOutput;

        Assert.DoesNotContain("ENTRYPOINT_FAILED", output, StringComparison.Ordinal);
        Assert.Contains("REPLAY1_EXIT=0", output, StringComparison.Ordinal);
        Assert.Contains("REPLAY2_EXIT=0", output, StringComparison.Ordinal);

        // Converged, with the live state published and the original preserved.
        Assert.Contains("FINAL_SECRET=1000:1000:600", output, StringComparison.Ordinal);
        Assert.Contains("FINAL_LEGACY=1000:1000:600", output, StringComparison.Ordinal);
        Assert.Contains("FINAL_DATA=1000:1000:755", output, StringComparison.Ordinal);
        Assert.Contains("ses_live", Extract(output, "FINAL_STATE="), StringComparison.Ordinal);
        Assert.Contains("ses_adopt", Extract(output, "SNAPSHOT_STILL="), StringComparison.Ordinal);
        Assert.Contains("NO_TEMP_LEAK", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bound on "replayable". A rollback is only safe to replay while the
    /// legacy state is still what the rollback put there. Once the pre-isolation
    /// image has actually run and advanced <c>/data/runtime.json</c>, re-running
    /// the identical command would republish the older controller-private bytes
    /// over the newer legacy state — destroying exactly the work the roll-forward
    /// interlock exists to protect, arrived at from the rollback side.
    /// </summary>
    /// <remarks>
    /// The assertion is on bytes <em>and</em> inode: a refusal that rewrote the
    /// file with identical-looking content, or that re-owned it first and then
    /// aborted, would be a different (and still wrong) outcome. The operator is
    /// then steered to <c>--resume-isolation</c>, and the private state is still
    /// selectable, which is what makes the refusal a decision point rather than
    /// a dead end.
    /// </remarks>
    [DockerFact]
    public void ReplayedRevertRefusesToOverwriteALegacyStateTheOldImageAdvanced()
    {
        var revertCode = ExtractPythonBlock("REVERT_CODE");
        var resumeCode = ExtractPythonBlock("RESUME_CODE");
        var result = RunInContainer(
            $$"""
            mkdir -p /run/agentcontrol-secrets && chmod 0755 /run/agentcontrol-secrets
            echo 'owner-password-value-0123456789' > /run/agentcontrol-secrets/owner-password
            chown 1001:1001 /run/agentcontrol-secrets/owner-password
            chmod 0600 /run/agentcontrol-secrets/owner-password
            export Control__OwnerPasswordFile=/run/agentcontrol-secrets/owner-password

            echo '{"organizationId":"org-keep","sessionId":"ses_adopt"}' > /data/runtime.json
            chown 1000:1000 /data/runtime.json
            /usr/local/bin/control-entrypoint /bin/true || echo ENTRYPOINT_FAILED

            # The isolated run advances only the private state.
            echo '{"organizationId":"org-keep","sessionId":"ses_isolated"}' > /control-data/runtime.json
            chown 1001:1001 /control-data/runtime.json && chmod 0600 /control-data/runtime.json

            cat > /tmp/revert.py <<'REVERT_EOF'
            {{revertCode}}
            REVERT_EOF
            cat > /tmp/resume.py <<'RESUME_EOF'
            {{resumeCode}}
            RESUME_EOF
            revert() { python3 /tmp/revert.py /run/agentcontrol-secrets/owner-password 1000 1000 /data /control-data 0; }

            revert; echo "FIRST_REVERT_EXIT=$?"

            # A replay with nothing in between is still a replay: it must succeed
            # and must not churn the inode it already converged on.
            AFTER_FIRST_INODE=$(stat -c '%i' /data/runtime.json)
            revert; echo "CLEAN_REPLAY_EXIT=$?"
            test "$AFTER_FIRST_INODE" = "$(stat -c '%i' /data/runtime.json)" \
                && echo CLEAN_REPLAY_INODE_STABLE || echo CLEAN_REPLAY_INODE_CHANGED

            # Now the pre-isolation image genuinely runs as UID 1000 and advances
            # the legacy state. This is the mutation the replay must not destroy.
            setpriv --reuid 1000 --regid 1000 --clear-groups sh -c \
                'printf "%s" "{\"organizationId\":\"org-keep\",\"sessionId\":\"ses_OLD_IMAGE_WORK\"}" > /data/runtime.json'
            echo "MUTATED=$?"
            ADVANCED_INODE=$(stat -c '%i' /data/runtime.json)
            ADVANCED_BYTES=$(stat -c '%s' /data/runtime.json)
            ADVANCED_SUM=$(sha256sum /data/runtime.json | cut -d' ' -f1)

            revert; echo "UNSAFE_REPLAY_EXIT=$?"

            # Byte-exact and inode-exact preservation: nothing was republished,
            # re-owned, or rewritten with equivalent content.
            echo "AFTER_STATE=$(cat /data/runtime.json)"
            test "$ADVANCED_INODE" = "$(stat -c '%i' /data/runtime.json)" \
                && echo ADVANCED_INODE_STABLE || echo ADVANCED_INODE_CHANGED
            test "$ADVANCED_BYTES" = "$(stat -c '%s' /data/runtime.json)" \
                && echo ADVANCED_BYTES_STABLE || echo ADVANCED_BYTES_CHANGED
            test "$ADVANCED_SUM" = "$(sha256sum /data/runtime.json | cut -d' ' -f1)" \
                && echo ADVANCED_HASH_STABLE || echo ADVANCED_HASH_CHANGED
            echo "PRIVATE_AFTER=$(cat /control-data/runtime.json)"
            ls /data/.runtime.json.*.tmp >/dev/null 2>&1 && echo TEMP_LEAKED || echo NO_TEMP_LEAK

            # The refusal is a decision point, not a dead end: the documented
            # resume still works and can keep the state the old image advanced.
            python3 /tmp/resume.py /run/agentcontrol-secrets/owner-password 1001 1001 /data /control-data legacy >/dev/null
            echo "RESUME_EXIT=$?"
            echo "RESUMED_PRIVATE=$(cat /control-data/runtime.json)"
            test -e /control-data/rollback.active && echo MARKER_STILL_THERE || echo MARKER_CLEARED
            /usr/local/bin/control-entrypoint /bin/true; echo "RESTART_EXIT=$?"
            """);

        var output = result.StandardOutput;

        Assert.DoesNotContain("ENTRYPOINT_FAILED", output, StringComparison.Ordinal);
        Assert.Contains("FIRST_REVERT_EXIT=0", output, StringComparison.Ordinal);

        // A replay of a converged rollback stays idempotent and does not churn.
        Assert.Contains("CLEAN_REPLAY_EXIT=0", output, StringComparison.Ordinal);
        Assert.Contains("CLEAN_REPLAY_INODE_STABLE", output, StringComparison.Ordinal);

        // The unsafe replay is refused.
        Assert.Contains("MUTATED=0", output, StringComparison.Ordinal);
        Assert.Contains("UNSAFE_REPLAY_EXIT=1", output, StringComparison.Ordinal);
        Assert.Contains("no longer matches it", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("--state-source legacy|private", result.StandardError, StringComparison.Ordinal);

        // The old image's work survives exactly: same bytes, same inode.
        Assert.Contains("ses_OLD_IMAGE_WORK", Extract(output, "AFTER_STATE="), StringComparison.Ordinal);
        Assert.Contains("ADVANCED_INODE_STABLE", output, StringComparison.Ordinal);
        Assert.Contains("ADVANCED_BYTES_STABLE", output, StringComparison.Ordinal);
        Assert.Contains("ADVANCED_HASH_STABLE", output, StringComparison.Ordinal);
        Assert.Contains("NO_TEMP_LEAK", output, StringComparison.Ordinal);

        // ...and neither state was lost: the private copy is still selectable.
        Assert.Contains("ses_isolated", Extract(output, "PRIVATE_AFTER="), StringComparison.Ordinal);

        // The documented next step still resolves it.
        Assert.Contains("RESUME_EXIT=0", output, StringComparison.Ordinal);
        Assert.Contains("ses_OLD_IMAGE_WORK", Extract(output, "RESUMED_PRIVATE="), StringComparison.Ordinal);
        Assert.Contains("MARKER_CLEARED", output, StringComparison.Ordinal);
        Assert.Contains("RESTART_EXIT=0", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rollback leaves two independent histories of the same organization: the
    /// one the pre-isolation image advances in <c>/data/runtime.json</c> and the
    /// one the isolated run left in <c>/control-data</c>. They cannot be merged,
    /// so rolling forward must fail closed until the operator names the survivor
    /// — and the losing state must be preserved, not deleted.
    /// </summary>
    [DockerFact]
    public void ARecordedRollbackBlocksRollForwardUntilTheOperatorChoosesTheState()
    {
        var revertCode = ExtractPythonBlock("REVERT_CODE");
        var resumeCode = ExtractPythonBlock("RESUME_CODE");
        var result = RunInContainer(
            $$"""
            mkdir -p /run/agentcontrol-secrets && chmod 0755 /run/agentcontrol-secrets
            echo 'owner-password-value-0123456789' > /run/agentcontrol-secrets/owner-password
            chown 1001:1001 /run/agentcontrol-secrets/owner-password
            chmod 0600 /run/agentcontrol-secrets/owner-password
            export Control__OwnerPasswordFile=/run/agentcontrol-secrets/owner-password

            echo '{"organizationId":"org-keep","sessionId":"ses_adopt"}' > /data/runtime.json
            chown 1000:1000 /data/runtime.json
            /usr/local/bin/control-entrypoint /bin/true || echo ENTRYPOINT_FAILED
            echo '{"organizationId":"org-keep","sessionId":"ses_isolated"}' > /control-data/runtime.json
            chown 1001:1001 /control-data/runtime.json && chmod 0600 /control-data/runtime.json

            cat > /tmp/revert.py <<'REVERT_EOF'
            {{revertCode}}
            REVERT_EOF
            cat > /tmp/resume.py <<'RESUME_EOF'
            {{resumeCode}}
            RESUME_EOF

            python3 /tmp/revert.py /run/agentcontrol-secrets/owner-password 1000 1000 /data /control-data 0 >/dev/null
            echo "REVERT_EXIT=$?"

            # The pre-isolation image runs and advances its own state.
            echo '{"organizationId":"org-keep","sessionId":"ses_after_rollback"}' > /data/runtime.json
            chown 1000:1000 /data/runtime.json

            # Rolling forward without a decision must refuse.
            /usr/local/bin/control-entrypoint /bin/true; echo "BLOCKED_EXIT=$?"

            # The operator keeps the legacy state the old image advanced.
            python3 /tmp/resume.py /run/agentcontrol-secrets/owner-password 1001 1001 /data /control-data legacy
            echo "RESUME_EXIT=$?"
            echo "PRIVATE_NOW=$(cat /control-data/runtime.json)"
            cat /control-data/runtime.superseded-*.json 2>/dev/null | head -1
            test -e /control-data/rollback.active && echo MARKER_STILL_THERE || echo MARKER_CLEARED
            stat -c 'RESUMED_SECRET=%u:%g:%a' /run/agentcontrol-secrets/owner-password

            # And now the isolated image starts again.
            /usr/local/bin/control-entrypoint /bin/true; echo "RESUMED_START_EXIT=$?"
            """);

        var output = result.StandardOutput;

        Assert.DoesNotContain("ENTRYPOINT_FAILED", output, StringComparison.Ordinal);
        Assert.Contains("REVERT_EXIT=0", output, StringComparison.Ordinal);

        // Fail closed rather than silently freezing or discarding either state.
        Assert.Contains("BLOCKED_EXIT=1", output, StringComparison.Ordinal);
        Assert.Contains("rollback to the pre-isolation image is recorded", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("--state-source legacy|private", result.StandardError, StringComparison.Ordinal);

        // The chosen state wins; the superseded one is kept, not deleted.
        Assert.Contains("RESUME_EXIT=0", output, StringComparison.Ordinal);
        Assert.Contains("ses_after_rollback", Extract(output, "PRIVATE_NOW="), StringComparison.Ordinal);
        Assert.Contains("ses_isolated", output, StringComparison.Ordinal);
        Assert.Contains("MARKER_CLEARED", output, StringComparison.Ordinal);

        // The resume also restores the controller's ownership of the secret, so
        // the isolated entrypoint's both-sides check passes again.
        Assert.Contains("RESUMED_SECRET=1001:1001:600", output, StringComparison.Ordinal);
        Assert.Contains("RESUMED_START_EXIT=0", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same divergence can arise without a recorded marker — an operator who
    /// started the old image by hand, for example. A UID 1000 owner on the legacy
    /// state while <em>differing</em> controller-private state exists means it was
    /// written outside isolation, and there is still no automatic merge, so the
    /// start fails.
    /// </summary>
    /// <remarks>
    /// Failing was not enough on its own. <c>--resume-isolation</c> is driven by
    /// the marker, so a refusal that recorded nothing left every later start
    /// failing identically with nothing for the operator to resolve. The
    /// entrypoint therefore writes the reconciliation marker — carrying both
    /// digests so the operator can see which state is which — and only then
    /// fails, which is what turns this into a decision point.
    /// </remarks>
    [DockerFact]
    public void AnUnrecordedLegacyWriteRecordsAReconciliationMarkerAndThenFailsClosed()
    {
        var resumeCode = ExtractPythonBlock("RESUME_CODE");
        var result = RunInContainer(
            $$"""
            mkdir -p /run/agentcontrol-secrets && chmod 0755 /run/agentcontrol-secrets
            echo 'owner-password-value-0123456789' > /run/agentcontrol-secrets/owner-password
            chown 1001:1001 /run/agentcontrol-secrets/owner-password
            chmod 0600 /run/agentcontrol-secrets/owner-password
            export Control__OwnerPasswordFile=/run/agentcontrol-secrets/owner-password

            install -o 1001 -g 1001 -m 600 /dev/null /control-data/runtime.json
            echo '{"organizationId":"org-keep","sessionId":"ses_isolated"}' > /control-data/runtime.json
            echo '{"organizationId":"org-keep","sessionId":"ses_legacy_advanced"}' > /data/runtime.json
            chown 1000:1000 /data/runtime.json
            LEGACY_INODE=$(stat -c '%i' /data/runtime.json)
            LEGACY_SUM=$(sha256sum /data/runtime.json | cut -d' ' -f1)
            PRIVATE_SUM=$(sha256sum /control-data/runtime.json | cut -d' ' -f1)

            /usr/local/bin/control-entrypoint /bin/true; echo "EXIT=$?"
            echo "PRIVATE_INTACT=$(cat /control-data/runtime.json)"
            echo "LEGACY_INTACT=$(cat /data/runtime.json)"
            stat -c 'LEGACY_OWNER=%u:%g' /data/runtime.json
            test "$LEGACY_INODE" = "$(stat -c '%i' /data/runtime.json)" \
                && echo LEGACY_INODE_STABLE || echo LEGACY_INODE_CHANGED

            # The marker is what makes this resolvable, so its content matters.
            test -f /control-data/rollback.active && echo MARKER_RECORDED || echo MARKER_MISSING
            stat -c 'MARKER=%u:%g:%a' /control-data/rollback.active
            python3 -c "
            import json, os, pathlib, sys
            marker = json.loads(pathlib.Path('/control-data/rollback.active').read_text())
            print('MARKER_REASON=' + marker['reason'])
            print('MARKER_LEGACY_SUM=' + marker['legacySha256'])
            print('MARKER_PRIVATE_SUM=' + marker['privateSha256'])
            print('MARKER_LEGACY_UID=%d' % marker['legacyOwnerUid'])
            print('MARKER_NO_PUBLISH' if marker['publishedSha256'] is None else 'MARKER_CLAIMS_PUBLISH')
            "
            echo "EXPECT_LEGACY_SUM=$LEGACY_SUM"
            echo "EXPECT_PRIVATE_SUM=$PRIVATE_SUM"

            # A second start still fails closed, and does not re-record over it.
            /usr/local/bin/control-entrypoint /bin/true; echo "SECOND_EXIT=$?"

            # ...and the recorded marker is exactly what the resume consumes.
            cat > /tmp/resume.py <<'RESUME_EOF'
            {{resumeCode}}
            RESUME_EOF
            python3 /tmp/resume.py /run/agentcontrol-secrets/owner-password 1001 1001 /data /control-data legacy
            echo "RESUME_EXIT=$?"
            echo "RESOLVED_PRIVATE=$(cat /control-data/runtime.json)"
            cat /control-data/runtime.superseded-*.json 2>/dev/null | head -1
            test -e /control-data/rollback.active && echo MARKER_STILL_THERE || echo MARKER_CLEARED
            /usr/local/bin/control-entrypoint /bin/true; echo "RESTART_EXIT=$?"
            """);

        var output = result.StandardOutput;

        Assert.Contains("EXIT=1", output, StringComparison.Ordinal);
        Assert.Contains("written outside isolation", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("--state-source legacy|private", result.StandardError, StringComparison.Ordinal);

        // Refusal is not destruction: both states are still there to choose from,
        // byte-exact and on their original inode.
        Assert.Contains("ses_isolated", Extract(output, "PRIVATE_INTACT="), StringComparison.Ordinal);
        Assert.Contains("ses_legacy_advanced", Extract(output, "LEGACY_INTACT="), StringComparison.Ordinal);
        Assert.Contains("LEGACY_OWNER=1000:1000", output, StringComparison.Ordinal);
        Assert.Contains("LEGACY_INODE_STABLE", output, StringComparison.Ordinal);

        // The divergence is recorded with both digests, so the operator can tell
        // the two states apart before naming a survivor.
        Assert.Contains("MARKER_RECORDED", output, StringComparison.Ordinal);
        Assert.Contains("MARKER=1001:1001:600", output, StringComparison.Ordinal);
        Assert.Contains("MARKER_REASON=unrecorded-legacy-divergence", output, StringComparison.Ordinal);
        Assert.Contains("MARKER_LEGACY_UID=1000", output, StringComparison.Ordinal);
        Assert.Equal(Extract(output, "EXPECT_LEGACY_SUM="), Extract(output, "MARKER_LEGACY_SUM="));
        Assert.Equal(Extract(output, "EXPECT_PRIVATE_SUM="), Extract(output, "MARKER_PRIVATE_SUM="));

        // No republish happened, so the marker must not claim one — otherwise a
        // later rollback replay would validate against a publication that is not
        // on disk.
        Assert.Contains("MARKER_NO_PUBLISH", output, StringComparison.Ordinal);
        Assert.Contains("SECOND_EXIT=1", output, StringComparison.Ordinal);

        // The marker the entrypoint wrote is the one the resume resolves.
        Assert.Contains("RESUME_EXIT=0", output, StringComparison.Ordinal);
        Assert.Contains("ses_legacy_advanced", Extract(output, "RESOLVED_PRIVATE="), StringComparison.Ordinal);
        Assert.Contains("ses_isolated", output, StringComparison.Ordinal);
        Assert.Contains("MARKER_CLEARED", output, StringComparison.Ordinal);
        Assert.Contains("RESTART_EXIT=0", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Adoption is not one write. It publishes the controller-private copy first,
    /// then records the snapshot and its evidence, then re-owns the legacy file to
    /// root. A crash between those steps leaves a private copy beside a still-UID
    /// 1000 legacy file that is <em>byte-identical</em> to it — which the previous
    /// owner-only check reported as a divergence written outside isolation.
    /// </summary>
    /// <remarks>
    /// That was wrong in the worst direction: an interrupted first start would
    /// permanently refuse to boot and demand a state choice between two copies of
    /// the same bytes. Identical bytes are an unfinished adoption, so the start
    /// finishes it. Differing bytes remain a divergence, which the sibling test
    /// above covers.
    /// </remarks>
    [DockerFact]
    public void AnInterruptedAdoptionIsCompletedRatherThanReportedAsDivergence()
    {
        var result = RunInContainer(
            """
            export Control__OwnerPasswordFile=""
            STATE='{"organizationId":"org-keep","sessionId":"ses_adopt","tmuxOwnerToken":"tok-adopt"}'

            # Crash boundary 1: the private copy was published, but the process
            # died before the snapshot, the evidence and the legacy re-own.
            echo "$STATE" > /data/runtime.json && chown 1000:1000 /data/runtime.json
            install -o 1001 -g 1001 -m 600 /dev/null /control-data/runtime.json
            echo "$STATE" > /control-data/runtime.json
            rm -f /control-data/runtime.pre-isolation.json /control-data/runtime.pre-isolation.meta.json
            LEGACY_INODE=$(stat -c '%i' /data/runtime.json)

            /usr/local/bin/control-entrypoint /bin/true; echo "RESUMED_ADOPTION_EXIT=$?"
            stat -c 'LEGACY_AFTER=%u:%g:%a' /data/runtime.json
            test "$LEGACY_INODE" = "$(stat -c '%i' /data/runtime.json)" \
                && echo LEGACY_INODE_STABLE || echo LEGACY_INODE_CHANGED
            echo "LEGACY_BYTES=$(cat /data/runtime.json)"
            echo "PRIVATE_BYTES=$(cat /control-data/runtime.json)"
            echo "SNAPSHOT_BYTES=$(cat /control-data/runtime.pre-isolation.json)"
            test -e /control-data/rollback.active && echo MARKER_WRITTEN || echo NO_MARKER
            python3 -c "
            import hashlib, json, pathlib
            meta = json.loads(pathlib.Path('/control-data/runtime.pre-isolation.meta.json').read_text())
            body = pathlib.Path('/control-data/runtime.pre-isolation.json').read_bytes()
            print('EVIDENCE_HASH_MATCHES' if meta['sha256'] == hashlib.sha256(body).hexdigest() else 'EVIDENCE_HASH_MISMATCH')
            print('EVIDENCE_SIZE_MATCHES' if meta['bytes'] == len(body) else 'EVIDENCE_SIZE_MISMATCH')
            print('EVIDENCE_PROVENANCE=' + meta['provenance'])
            "

            # Crash boundary 2: the snapshot landed but the evidence did not. The
            # evidence is regenerated from the snapshot's own bytes, and the
            # snapshot itself is never rewritten.
            rm -f /control-data/runtime.pre-isolation.meta.json
            SNAPSHOT_INODE=$(stat -c '%i' /control-data/runtime.pre-isolation.json)
            /usr/local/bin/control-entrypoint /bin/true; echo "EVIDENCE_BACKFILL_EXIT=$?"
            test -f /control-data/runtime.pre-isolation.meta.json && echo EVIDENCE_REGENERATED || echo EVIDENCE_MISSING
            test "$SNAPSHOT_INODE" = "$(stat -c '%i' /control-data/runtime.pre-isolation.json)" \
                && echo SNAPSHOT_INODE_STABLE || echo SNAPSHOT_INODE_CHANGED
            echo "SNAPSHOT_AFTER_BACKFILL=$(cat /control-data/runtime.pre-isolation.json)"
            stat -c 'EVIDENCE_MODE=%u:%g:%a' /control-data/runtime.pre-isolation.meta.json
            python3 -c "
            import hashlib, json, pathlib
            meta = json.loads(pathlib.Path('/control-data/runtime.pre-isolation.meta.json').read_text())
            body = pathlib.Path('/control-data/runtime.pre-isolation.json').read_bytes()
            print('BACKFILL_HASH_MATCHES' if meta['sha256'] == hashlib.sha256(body).hexdigest() else 'BACKFILL_HASH_MISMATCH')
            print('BACKFILL_PROVENANCE=' + meta['provenance'])
            "

            # Crash boundary 3: evidence that disagrees with the snapshot is
            # corruption, not a backfill opportunity. It must fail the start.
            python3 -c "
            import json, pathlib
            path = pathlib.Path('/control-data/runtime.pre-isolation.meta.json')
            meta = json.loads(path.read_text())
            meta['sha256'] = '0' * 64
            path.write_text(json.dumps(meta))
            "
            /usr/local/bin/control-entrypoint /bin/true; echo "CORRUPT_EVIDENCE_EXIT=$?"
            echo "SNAPSHOT_AFTER_CORRUPTION=$(cat /control-data/runtime.pre-isolation.json)"
            """);

        var output = result.StandardOutput;

        // Boundary 1: the interrupted adoption completes instead of failing.
        Assert.Contains("RESUMED_ADOPTION_EXIT=0", output, StringComparison.Ordinal);
        Assert.Contains("NO_MARKER", output, StringComparison.Ordinal);

        // The remaining adoption steps actually ran: the legacy file is reduced
        // to the root-owned stale copy, on its original inode with its bytes.
        Assert.Contains("LEGACY_AFTER=0:0:600", output, StringComparison.Ordinal);
        Assert.Contains("LEGACY_INODE_STABLE", output, StringComparison.Ordinal);
        Assert.Contains("ses_adopt", Extract(output, "LEGACY_BYTES="), StringComparison.Ordinal);
        Assert.Contains("ses_adopt", Extract(output, "PRIVATE_BYTES="), StringComparison.Ordinal);

        // ...and the snapshot that the crash skipped is backfilled with evidence.
        Assert.Contains("ses_adopt", Extract(output, "SNAPSHOT_BYTES="), StringComparison.Ordinal);
        Assert.Contains("EVIDENCE_HASH_MATCHES", output, StringComparison.Ordinal);
        Assert.Contains("EVIDENCE_SIZE_MATCHES", output, StringComparison.Ordinal);
        Assert.Contains(
            "EVIDENCE_PROVENANCE=interrupted-adoption-completion",
            output,
            StringComparison.Ordinal);

        // Boundary 2: missing evidence is regenerated from the snapshot bytes,
        // and the snapshot is never overwritten to make them agree.
        Assert.Contains("EVIDENCE_BACKFILL_EXIT=0", output, StringComparison.Ordinal);
        Assert.Contains("EVIDENCE_REGENERATED", output, StringComparison.Ordinal);
        Assert.Contains("SNAPSHOT_INODE_STABLE", output, StringComparison.Ordinal);
        Assert.Contains("ses_adopt", Extract(output, "SNAPSHOT_AFTER_BACKFILL="), StringComparison.Ordinal);
        Assert.Contains("EVIDENCE_MODE=1001:1001:600", output, StringComparison.Ordinal);
        Assert.Contains("BACKFILL_HASH_MATCHES", output, StringComparison.Ordinal);
        Assert.Contains("BACKFILL_PROVENANCE=evidence-backfill", output, StringComparison.Ordinal);

        // Boundary 3: disagreeing evidence fails the start and the snapshot is
        // left alone rather than rewritten to match a corrupted record.
        Assert.Contains("CORRUPT_EVIDENCE_EXIT=1", output, StringComparison.Ordinal);
        Assert.Contains("disagree", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("ses_adopt", Extract(output, "SNAPSHOT_AFTER_CORRUPTION="), StringComparison.Ordinal);
    }

    /// <summary>
    /// A rollback out of an unrecorded divergence must not be a dead end. The
    /// entrypoint's own marker has no <c>publishedSha256</c> (nothing was
    /// republished), so the replay guard would otherwise refuse the very first
    /// rollback attempt. Recognising that marker, the rollback keeps the diverged
    /// legacy bytes — the operator chose them by rolling back — hands ownership
    /// back, and retains the controller-private state.
    /// </summary>
    [DockerFact]
    public void RollingBackOutOfAnUnrecordedDivergenceKeepsTheLegacyBytes()
    {
        var revertCode = ExtractPythonBlock("REVERT_CODE");
        var result = RunInContainer(
            $$"""
            mkdir -p /run/agentcontrol-secrets && chmod 0755 /run/agentcontrol-secrets
            echo 'owner-password-value-0123456789' > /run/agentcontrol-secrets/owner-password
            chown 1001:1001 /run/agentcontrol-secrets/owner-password
            chmod 0600 /run/agentcontrol-secrets/owner-password
            export Control__OwnerPasswordFile=/run/agentcontrol-secrets/owner-password

            install -o 1001 -g 1001 -m 600 /dev/null /control-data/runtime.json
            echo '{"organizationId":"org-keep","sessionId":"ses_isolated"}' > /control-data/runtime.json
            echo '{"organizationId":"org-keep","sessionId":"ses_legacy_advanced"}' > /data/runtime.json
            chown 1000:1000 /data/runtime.json
            LEGACY_INODE=$(stat -c '%i' /data/runtime.json)

            # The entrypoint detects and records the divergence, then fails.
            /usr/local/bin/control-entrypoint /bin/true; echo "DETECT_EXIT=$?"

            cat > /tmp/revert.py <<'REVERT_EOF'
            {{revertCode}}
            REVERT_EOF
            python3 /tmp/revert.py /run/agentcontrol-secrets/owner-password 1000 1000 /data /control-data 0
            echo "REVERT_EXIT=$?"

            echo "LEGACY_AFTER=$(cat /data/runtime.json)"
            test "$LEGACY_INODE" = "$(stat -c '%i' /data/runtime.json)" \
                && echo LEGACY_INODE_STABLE || echo LEGACY_INODE_CHANGED
            stat -c 'LEGACY_MODE=%u:%g:%a' /data/runtime.json
            stat -c 'DATA_MODE=%u:%g:%a' /data
            stat -c 'SECRET_MODE=%u:%g:%a' /run/agentcontrol-secrets/owner-password
            echo "PRIVATE_RETAINED=$(cat /control-data/runtime.json)"
            """);

        var output = result.StandardOutput;

        Assert.Contains("DETECT_EXIT=1", output, StringComparison.Ordinal);

        // The rollback proceeds rather than refusing on its own marker...
        Assert.Contains("REVERT_EXIT=0", output, StringComparison.Ordinal);

        // ...keeping the diverged legacy bytes exactly, on the same inode.
        Assert.Contains("ses_legacy_advanced", Extract(output, "LEGACY_AFTER="), StringComparison.Ordinal);
        Assert.Contains("LEGACY_INODE_STABLE", output, StringComparison.Ordinal);

        // The old image can run: ownership of state, /data and the secret is back.
        Assert.Contains("LEGACY_MODE=1000:1000:600", output, StringComparison.Ordinal);
        Assert.Contains("DATA_MODE=1000:1000:755", output, StringComparison.Ordinal);
        Assert.Contains("SECRET_MODE=1000:1000:600", output, StringComparison.Ordinal);

        // And nothing was discarded: the isolated run's state is still there.
        Assert.Contains("ses_isolated", Extract(output, "PRIVATE_RETAINED="), StringComparison.Ordinal);
    }

    /// <summary>
    /// Operators need the controller's PID to inspect its capability bounding
    /// set. The obvious one-liner is wrong in two independent ways, and both are
    /// asserted here rather than assumed:
    /// <list type="number">
    /// <item><c>/proc/1/status</c> is tini, not the controller. The bounding set
    /// is reduced in the <c>setpriv</c> call that execs the controller, so PID 1
    /// still reports the startup capabilities.</item>
    /// <item><c>pgrep -f HVO.AgentControl.dll</c> matches the inspecting command
    /// itself, because the pattern appears in that shell's own command line — it
    /// reports a PID even when no controller is running at all.</item>
    /// </list>
    /// The entrypoint therefore records its own <c>$$</c> before <c>exec</c>,
    /// which the exec preserves, so the recorded PID really is the controller's.
    /// </summary>
    [DockerFact]
    public void TheEntrypointRecordsTheControllerPidForCapabilityInspection()
    {
        var result = RunInContainer(
            """
            export Control__OwnerPasswordFile=""
            # No controller is running in this shell, yet the usual pattern-match
            # one-liner still reports a PID: its own.
            echo "PGREP_SELF_MATCH=$(sh -c 'pgrep -o -f HVO.AgentControl.dll')"
            echo "INSPECTING_SHELL=$$"

            /usr/local/bin/control-entrypoint /bin/sh -c '
              RECORDED=$(cat /control-data/controller.pid)
              if [ "$RECORDED" = "$$" ]; then echo MATCHES_RUNNING_CONTROLLER; else echo MISMATCH; fi
              if [ "$RECORDED" = "1" ]; then echo RECORDED_IS_INIT; else echo RECORDED_IS_NOT_INIT; fi
              # The documented operator command, run verbatim.
              grep CapBnd /proc/$(cat /control-data/controller.pid)/status \
                | sed s/CapBnd/ControllerBnd/
              grep CapBnd /proc/1/status | sed s/CapBnd/InitBnd/
            '
            stat -c 'PID_FILE=%u:%g:%a' /control-data/controller.pid
            """);

        var output = result.StandardOutput;

        // Hazard 2: pgrep is installed, and it answers with a PID even though no
        // controller exists here - so a documented pgrep recipe would send an
        // operator to inspect the wrong process's capabilities.
        Assert.False(
            string.IsNullOrWhiteSpace(Extract(output, "PGREP_SELF_MATCH=")),
            "pgrep -f matched nothing; the self-match hazard this PID file avoids no longer exists");

        // `exec` keeps the PID, so what the entrypoint recorded is the process
        // that is actually running as the controller.
        Assert.Contains("MATCHES_RUNNING_CONTROLLER", output, StringComparison.Ordinal);
        Assert.DoesNotContain("MISMATCH", output, StringComparison.Ordinal);
        Assert.Contains("RECORDED_IS_NOT_INIT", output, StringComparison.Ordinal);

        // The documented command reports the reduced controller bounding set...
        Assert.Equal(0x00000000000000E0UL, ExtractCapability(output, "ControllerBnd"));

        // ...and PID 1 does not, which is precisely why the old
        // `grep CapBnd /proc/1/status` advice was wrong: the bounding set is
        // reduced in the setpriv call that execs the controller, so whatever
        // holds PID 1 (tini in the shipped image) still carries the startup
        // capabilities and would report a boundary the controller does not have.
        Assert.NotEqual(0x00000000000000E0UL, ExtractCapability(output, "InitBnd"));

        // Controller-owned; readable so an operator shell can use it.
        Assert.Contains("PID_FILE=1001:1001:644", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The revert is held to the same no-follow rules as the forward migration:
    /// a substituted secret or legacy-state path fails instead of handing an
    /// attacker-chosen file to UID 1000.
    /// </summary>
    [DockerFact]
    public void RevertIsolationRefusesSubstitutedPaths()
    {
        var revertCode = ExtractPythonBlock("REVERT_CODE");
        var result = RunInContainer(
            $$"""
            cat > /tmp/revert.py <<'REVERT_EOF'
            {{revertCode}}
            REVERT_EOF
            mkdir -p /secrets /victim-dir && chmod 0755 /victim-dir
            echo 'victim' > /victim-dir/victim && chmod 0644 /victim-dir/victim
            install -o 1001 -g 1001 -m 600 /dev/null /control-data/runtime.json
            echo '{"organizationId":"org-live"}' > /control-data/runtime.json

            ln -s /victim-dir/victim /secrets/owner-password
            python3 /tmp/revert.py /secrets/owner-password 1000 1000 /data /control-data 0
            echo "SYMLINK_SECRET=$?"
            rm -f /secrets/owner-password

            install -o 1001 -g 1001 -m 600 /dev/null /secrets/owner-password
            chown 1000:1000 /data && chmod 0755 /data
            ln -s /victim-dir/victim /data/runtime.json
            python3 /tmp/revert.py /secrets/owner-password 1000 1000 /data /control-data 0
            echo "SYMLINK_STATE=$?"
            rm -f /data/runtime.json

            # A hard-linked legacy state is refused for the same reason: the
            # republish must never be redirected into another inode.
            ln /victim-dir/victim /data/runtime.json
            python3 /tmp/revert.py /secrets/owner-password 1000 1000 /data /control-data 0
            echo "HARDLINK_STATE=$?"
            rm -f /data/runtime.json

            # An unexpected owner on the legacy state is refused too.
            echo '{}' > /data/runtime.json && chown 1234:1234 /data/runtime.json
            python3 /tmp/revert.py /secrets/owner-password 1000 1000 /data /control-data 0
            echo "BAD_OWNER_STATE=$?"

            stat -c 'VICTIM=%u:%g:%a' /victim-dir/victim
            echo "VICTIM_CONTENT=$(cat /victim-dir/victim)"
            stat -c 'SECRET_UNCHANGED=%u:%g:%a' /secrets/owner-password
            test -e /control-data/rollback.active && echo MARKER_WRITTEN || echo NO_MARKER
            """);

        var output = result.StandardOutput;

        Assert.Contains("SYMLINK_SECRET=1", output, StringComparison.Ordinal);
        Assert.Contains("SYMLINK_STATE=1", output, StringComparison.Ordinal);
        Assert.Contains("HARDLINK_STATE=1", output, StringComparison.Ordinal);
        Assert.Contains("BAD_OWNER_STATE=1", output, StringComparison.Ordinal);

        // Root-owned 0644 with its original bytes throughout: nothing followed
        // or overwrote either substitution.
        Assert.Contains("VICTIM=0:0:644", output, StringComparison.Ordinal);
        Assert.Contains("VICTIM_CONTENT=victim", output, StringComparison.Ordinal);

        // All-or-nothing: a bad runtime-state path must not leave the secret
        // already handed back, which would strand the deployment half-reverted,
        // and must not record a rollback that never happened.
        Assert.Contains("SECRET_UNCHANGED=1001:1001:600", output, StringComparison.Ordinal);
        Assert.Contains("NO_MARKER", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads one of the container-side Python blocks out of the operator script,
    /// so the rollback tests exercise the code the operator actually runs rather
    /// than a copy that can drift away from it.
    /// </summary>
    private static string ExtractPythonBlock(string name)
    {
        var script = File.ReadAllText(
            Path.Combine(ControllerIsolationLayoutTests.RepositoryRoot(), "scripts", "init-secrets.py"));
        var marker = name + " = '''\\\n";
        var start = script.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{name} was not found in scripts/init-secrets.py");
        start += marker.Length;
        var end = script.IndexOf("\n'''", start, StringComparison.Ordinal);
        Assert.True(end > start, $"{name} is not terminated in scripts/init-secrets.py");

        // The block is a Python string literal in the source, so unescape the two
        // sequences it actually uses before running it as a file.
        return script[start..end].Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    /// <summary>
    /// The root-escalation case this design must defeat. Before isolation the
    /// agent owned the <c>/data</c> directory entry, so it could rename
    /// <c>home</c>/<c>workspace</c> away and leave a symlink behind for the next
    /// root start to chown. Each variant must fail the entrypoint before any
    /// metadata on the symlink target changes, and the service must stay down.
    /// </summary>
    /// <remarks>
    /// The <c>home</c> variant additionally starts from a non-empty, agent-owned
    /// <c>workspace</c>, so the assertion also covers "the attacker's real data
    /// was not collaterally destroyed while the attack was refused".
    /// </remarks>
    [DockerTheory]
    [InlineData("home", "/usr/local/bin")]
    [InlineData("workspace", "/usr/local/bin")]
    [InlineData("home", "/control-data")]
    [InlineData("workspace", "/control-data")]
    public void EntrypointRefusesAPlantedSymlinkWithoutTouchingItsTarget(string entry, string target)
    {
        var result = RunInContainer(
            $$"""
            export Control__OwnerPasswordFile=""
            # Reproduce the pre-isolation volume exactly: agent-owned /data.
            chown 1000:1000 /data && chmod 0755 /data
            chown 1000:1000 /data/home /data/workspace
            echo 'real work' > /data/workspace/keep.txt && chown 1000:1000 /data/workspace/keep.txt

            # The attack, performed by the agent identity with nothing but its
            # own write permission on the /data directory entry.
            setpriv --reuid 1000 --regid 1000 --clear-groups sh -c \
                'mv /data/{{entry}} /data/{{entry}}.stash && ln -s {{target}} /data/{{entry}}'
            echo "ATTACK_SETUP=$?"

            stat -c 'TARGET_BEFORE=%u:%g:%a' {{target}}
            /usr/local/bin/control-entrypoint /bin/true
            echo "ENTRYPOINT_EXIT=$?"
            stat -c 'TARGET_AFTER=%u:%g:%a' {{target}}
            stat -c 'LINK=%u:%g:%a' /data/{{entry}} 2>/dev/null || echo LINK_GONE
            test -L /data/{{entry}} && echo LINK_INTACT || echo LINK_REPLACED
            cat /data/workspace.stash/keep.txt 2>/dev/null || cat /data/workspace/keep.txt 2>/dev/null
            """);

        var output = result.StandardOutput;

        Assert.Contains("ATTACK_SETUP=0", output, StringComparison.Ordinal);

        // The entrypoint fails, so the service stays down instead of running
        // with an isolation boundary it could not establish.
        Assert.Contains("ENTRYPOINT_EXIT=1", output, StringComparison.Ordinal);
        Assert.Contains("is a symlink", result.StandardError, StringComparison.Ordinal);

        // The symlink target's ownership and mode are unchanged: nothing followed
        // it. /usr/local/bin is root-owned; /control-data belongs to the
        // controller - in both cases the agent gained nothing.
        var before = Extract(output, "TARGET_BEFORE=");
        var after = Extract(output, "TARGET_AFTER=");
        Assert.Equal(before, after);
        Assert.DoesNotContain("1000:1000", after, StringComparison.Ordinal);
        Assert.Equal(target == "/control-data" ? "1001:1001:700" : "0:0:755", after);

        // The planted link itself is neither followed nor silently replaced.
        Assert.Contains("LINK_INTACT", output, StringComparison.Ordinal);

        // And the agent's own real data is still there - refusal is not deletion.
        Assert.Contains("real work", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The structural fix behind the test above: after a successful start the
    /// agent cannot create, rename or unlink anything directly in <c>/data</c>,
    /// so the swap can never be staged again - while it keeps full ownership of
    /// the two subtrees it actually uses.
    /// </summary>
    [DockerFact]
    public void AgentCannotCreateOrRenameEntriesDirectlyInTheDataRoot()
    {
        var result = RunInContainer(
            """
            export Control__OwnerPasswordFile=""
            chown 1000:1000 /data /data/home /data/workspace
            echo 'preserved' > /data/workspace/keep.txt && chown 1000:1000 /data/workspace/keep.txt
            /usr/local/bin/control-entrypoint /bin/true || echo ENTRYPOINT_FAILED

            stat -c 'DATA=%u:%g:%a' /data
            stat -c 'HOME=%u:%g:%a' /data/home
            stat -c 'WORKSPACE=%u:%g:%a' /data/workspace
            stat -c 'KEEP=%u:%g' /data/workspace/keep.txt

            as_agent() { setpriv --reuid 1000 --regid 1000 --clear-groups "$@"; }
            as_agent ln -s /usr/local/bin /data/evil && echo SYMLINK_CREATED || echo SYMLINK_DENIED
            as_agent mv /data/home /data/home.stash && echo RENAME_SUCCEEDED || echo RENAME_DENIED
            as_agent rm -rf /data/workspace && echo UNLINK_SUCCEEDED || echo UNLINK_DENIED
            as_agent touch /data/newfile && echo CREATE_SUCCEEDED || echo CREATE_DENIED
            # The agent still fully owns what it is supposed to own.
            as_agent touch /data/workspace/agent-owned && echo WORKSPACE_WRITABLE || echo WORKSPACE_READONLY
            as_agent touch /data/home/agent-owned && echo HOME_WRITABLE || echo HOME_READONLY
            """);

        var output = result.StandardOutput;

        Assert.DoesNotContain("ENTRYPOINT_FAILED", output, StringComparison.Ordinal);

        // Root-owned parent, agent-owned subtrees, untouched existing content.
        Assert.Contains("DATA=0:0:755", output, StringComparison.Ordinal);
        Assert.Contains("HOME=1000:1000:700", output, StringComparison.Ordinal);
        Assert.Contains("WORKSPACE=1000:1000:700", output, StringComparison.Ordinal);
        Assert.Contains("KEEP=1000:1000", output, StringComparison.Ordinal);

        // Every way of staging the symlink swap is gone.
        Assert.Contains("SYMLINK_DENIED", output, StringComparison.Ordinal);
        Assert.Contains("RENAME_DENIED", output, StringComparison.Ordinal);
        Assert.Contains("UNLINK_DENIED", output, StringComparison.Ordinal);
        Assert.Contains("CREATE_DENIED", output, StringComparison.Ordinal);

        // ...without taking away what the agent legitimately needs.
        Assert.Contains("WORKSPACE_WRITABLE", output, StringComparison.Ordinal);
        Assert.Contains("HOME_WRITABLE", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same no-follow rule for the legacy runtime state: a symlink, a
    /// directory or a hard-linked file in its place must fail the start rather
    /// than redirect the root-owned chown that reduces it to 0600.
    /// </summary>
    [DockerFact]
    public void EntrypointRefusesASubstitutedLegacyRuntimeState()
    {
        var result = RunInContainer(
            """
            export Control__OwnerPasswordFile=""
            # Not /tmp: fs.protected_regular stops even root rewriting another
            # UID's file in a sticky world-writable directory, which would make
            # the second probe's setup - not the entrypoint - the thing that failed.
            mkdir -p /victim-dir && chmod 0755 /victim-dir
            probe() {
              chown 1000:1000 /data && chmod 0755 /data
              rm -f /data/runtime.json /control-data/runtime.json /victim-dir/victim
              echo 'victim' > /victim-dir/victim
              chown 1000:1000 /victim-dir/victim && chmod 0644 /victim-dir/victim
              "$@"
              /usr/local/bin/control-entrypoint /bin/true
              echo "EXIT=$?"
              stat -c 'VICTIM=%u:%g:%a' /victim-dir/victim
              echo "CONTENT=$(cat /victim-dir/victim)"
            }
            echo SYMLINK
            probe setpriv --reuid 1000 --regid 1000 --clear-groups \
                ln -s /victim-dir/victim /data/runtime.json
            echo HARDLINK
            probe ln /victim-dir/victim /data/runtime.json
            """);

        var output = result.StandardOutput;

        // Both attempts fail closed, and the victim keeps its owner, mode and
        // bytes both times: the root-owned chown never followed the substitution.
        Assert.Equal(2, Occurrences(output, "EXIT=1"));
        Assert.Equal(2, Occurrences(output, "VICTIM=1000:1000:644"));
        Assert.Equal(2, Occurrences(output, "CONTENT=victim"));
        Assert.Contains("is a symlink", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("extra hard links", result.StandardError, StringComparison.Ordinal);
    }

    private static string Extract(string output, string marker)
    {
        var start = output.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{marker}' was not reported:\n{output}");
        start += marker.Length;
        var end = output.IndexOf('\n', start);
        return (end < 0 ? output[start..] : output[start..end]).Trim();
    }

    [DockerFact]
    public void EntrypointRefusesToStartWhenTheOwnerSecretIsStillAgentReadable()
    {
        var result = RunInContainer(
            """
            mkdir -p /run/agentcontrol-secrets
            echo 'owner-password-value-0123456789' > /run/agentcontrol-secrets/owner-password
            chown 1000:1000 /run/agentcontrol-secrets/owner-password
            chmod 0644 /run/agentcontrol-secrets/owner-password
            export Control__OwnerPasswordFile=/run/agentcontrol-secrets/owner-password
            /usr/local/bin/control-entrypoint /bin/true; echo EXIT=$?
            """);

        // Failing closed matters more than starting: a shared-UID secret is the
        // exact condition this change exists to remove.
        Assert.Contains("EXIT=1", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("expected the controller UID 1001", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("--migrate-owner", result.StandardError, StringComparison.Ordinal);
    }

    [DockerFact]
    public void EntrypointDropsToTheControllerIdentityIrreversibly()
    {
        var result = RunInContainer(
            """
            export Control__OwnerPasswordFile=""
            /usr/local/bin/control-entrypoint /usr/bin/python3 -c '
            import os
            print("RESUID", os.getresuid(), "RESGID", os.getresgid())
            try:
                os.setuid(0)
                print("REGAINED_ROOT")
            except OSError:
                print("ROOT_UNREACHABLE")
            '
            """);

        Assert.Contains("RESUID (1001, 1001, 1001)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("RESGID (1001, 1001, 1001)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("ROOT_UNREACHABLE", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("REGAINED_ROOT", result.StandardOutput, StringComparison.Ordinal);
    }

    private static CommandResult RunInContainer(string script)
    {
        RequireDockerWhenMandatory();
        var arguments = new List<string>(RunFlags) { ResolvedImage, "-c", script };
        var result = Run("docker", arguments, TimeSpan.FromMinutes(3));
        return result;
    }

    /// <summary>
    /// Resolves the image the suite runs against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>AGENTCONTROL_ISOLATION_IMAGE</c> names an image that is already built -
    /// CI's <c>docker</c> job builds it once and reuses it here instead of paying
    /// for a second full build. When unset the suite builds its own tag.
    /// </para>
    /// <para>
    /// <c>AGENTCONTROL_DOCKER_REQUIRED=1</c> turns an unavailable daemon or a
    /// failed build into a hard failure instead of a skip. CI sets it: an
    /// isolation suite that silently skips proves nothing, and a skipped security
    /// test reads exactly like a passing one in the summary.
    /// </para>
    /// </remarks>
    private static bool BuildImage()
    {
        var prebuilt = Environment.GetEnvironmentVariable("AGENTCONTROL_ISOLATION_IMAGE");
        if (!string.IsNullOrWhiteSpace(prebuilt))
        {
            var inspect = Run("docker", ["image", "inspect", prebuilt], TimeSpan.FromSeconds(60));
            if (inspect.ExitCode == 0)
            {
                ResolvedImage = prebuilt;
                return true;
            }

            RequireOrFail(
                $"AGENTCONTROL_ISOLATION_IMAGE='{prebuilt}' is not present on the daemon: {inspect.StandardError}");
            return false;
        }

        var version = Run("docker", ["version", "--format", "{{.Server.Version}}"], TimeSpan.FromSeconds(30));
        if (version.ExitCode != 0)
        {
            RequireOrFail($"no usable Docker daemon: {version.StandardError}");
            return false;
        }

        var root = ControllerIsolationLayoutTests.RepositoryRoot();
        var build = Run("docker", ["build", "--tag", ImageTag, root], TimeSpan.FromMinutes(30));
        if (build.ExitCode != 0)
        {
            RequireOrFail($"the isolation image failed to build: {build.StandardError}");
            return false;
        }

        ResolvedImage = ImageTag;
        return true;
    }

    /// <summary>
    /// Records why the image is unusable. When Docker is mandatory this is
    /// surfaced as a test failure rather than a skip, so the suite cannot be
    /// reported as green in an environment that is supposed to enforce it.
    /// </summary>
    private static void RequireOrFail(string reason)
    {
        Unavailable = reason;
    }

    /// <summary>Reason the image could not be resolved, or null.</summary>
    private static string? Unavailable;

    /// <summary>
    /// Fails the calling test when Docker was mandatory but unusable. This runs
    /// in the test body, not the attribute constructor, so the run reports a
    /// readable assertion failure instead of an xUnit discovery error.
    /// </summary>
    private static void RequireDockerWhenMandatory()
    {
        if (DockerRequired && Unavailable is { } reason)
        {
            Assert.Fail(
                "AGENTCONTROL_DOCKER_REQUIRED=1, so the alternate-UID isolation suite must actually run: "
                + reason);
        }
    }

    private static CommandResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new CommandResult(-1, string.Empty, "process did not start");
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                process.Kill(entireProcessTree: true);
                return new CommandResult(-1, string.Empty, "timed out");
            }

            return new CommandResult(
                process.ExitCode,
                standardOutput.GetAwaiter().GetResult(),
                standardError.GetAwaiter().GetResult());
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new CommandResult(-1, string.Empty, exception.Message);
        }
    }

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

    /// <summary>
    /// Runs where a Docker daemon can build and run the image. Skipped on a
    /// developer machine without a daemon; with
    /// <c>AGENTCONTROL_DOCKER_REQUIRED=1</c> the unavailability itself fails, so
    /// CI cannot report an unexecuted isolation suite as green.
    /// </summary>
    private sealed class DockerFactAttribute : FactAttribute
    {
        public DockerFactAttribute()
        {
            Skip = SkipReason();
        }
    }

    /// <inheritdoc cref="DockerFactAttribute"/>
    private sealed class DockerTheoryAttribute : TheoryAttribute
    {
        public DockerTheoryAttribute()
        {
            Skip = SkipReason();
        }
    }

    /// <summary>
    /// Null when the suite must run. When Docker is mandatory this stays null
    /// even though the image is unusable, so the tests execute and fail with the
    /// recorded reason instead of quietly reporting as skipped.
    /// </summary>
    private static string? SkipReason() =>
        ImageAvailable.Value || DockerRequired
            ? null
            : "Docker is not available; the alternate-UID isolation suite requires it.";
}
