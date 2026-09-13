using System.Diagnostics;
using HVO.AgentControl.Runtime;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Exercises <see cref="TmuxAttachLauncher"/> against a real tmux binary.
///
/// Issue #238: the fake tmux used by the unit suite accepted the '=name' exact
/// target for every command, but real tmux 3.4 only honours it where a session
/// target flows through the fuzzy matcher. <c>set-option -t =name</c> fails with
/// "no such session: =name" and <c>show-options -qv -t =name</c> returns nothing
/// with exit code 0, so the deployed image never persisted the attach-pane
/// marker and reported terminalReady=false. Only a real tmux can defend that.
///
/// Isolation: every test runs its own tmux server on a private socket inside a
/// temporary directory. The launcher reaches it through a per-test symlink to a
/// checked-in wrapper fixture plus a non-executable sidecar that names the real
/// tmux binary and the private socket. There is no global socket, no
/// $TMUX_TMPDIR default and no interaction with the developer's or the CI
/// runner's own tmux sessions. The server is killed on dispose.
///
/// The wrapper and the attach-client stand-in are copied from
/// <c>Fixtures/</c> and executed only through those symlinks; the test never
/// materializes an executable by writing it, which would reintroduce the
/// ETXTBSY race of issue #232 (see <see cref="AcpFakeServer"/>).
/// No OpenCode binary, model provider, network call or inference is involved:
/// the "attach client" is a local fixture that records its argv/environment and
/// then sleeps.
/// </summary>
public sealed class TmuxRealIntegrationTests
{
    [Fact]
    public async Task NewSessionBecomesReadyAndPersistsTheOwnerQualifiedPaneMarker()
    {
        using var tmux = RealTmux.Create();
        var launcher = tmux.Launcher();

        var result = await launcher.EnsureAsync(tmux.Request, CancellationToken.None);
        Assert.Null(result.Error);
        Assert.True(result.Started);
        Assert.True(result.Owned);
        Assert.True(launcher.IsOwned);

        // The marker really round-trips through real tmux (the #238 failure).
        var sessionId = tmux.SessionId();
        Assert.StartsWith("$", sessionId, StringComparison.Ordinal);
        var pane = tmux.PaneIds(sessionId).Single();
        Assert.Equal($"owner-{tmux.Token}:{pane}", tmux.ShowOption(sessionId));

        // The child was launched with a sanitized, private environment.
        var childEnvironment = await tmux.ChildEnvironmentAsync();
        Assert.Equal(tmux.Request.Home, childEnvironment["HOME"]);
        Assert.Equal("tmux-256color", childEnvironment["TERM"]);
        Assert.Equal(tmux.Request.OwnerToken, childEnvironment[TmuxAttachLauncher.OwnerEnvironmentVariable]);
        Assert.DoesNotContain(childEnvironment.Keys, key =>
            key.StartsWith("GH_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("GITHUB_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("Control__", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            new[] { "attach", tmux.Request.NativeUrl, "--dir", tmux.Request.Workspace, "--session", tmux.Request.SessionId },
            await tmux.ChildArgumentsAsync());
    }

    [Fact]
    public async Task HealthyReprobeAdoptsThePersistedPaneWithoutStealingTheUserSelection()
    {
        using var tmux = RealTmux.Create();
        var clock = new Clock();
        Assert.True((await tmux.Launcher(clock).EnsureAsync(tmux.Request, CancellationToken.None)).Started);
        var sessionId = tmux.SessionId();

        // The operator opens and selects their own window.
        var userPane = tmux.Run("new-window", "-d", "-t", sessionId + ":", "-P", "-F", "#{pane_id}", "/bin/sleep", "300").Trim();
        tmux.Run("select-window", "-t", userPane);
        var selectedBefore = tmux.Run("display-message", "-p", "-t", sessionId, "#{window_id}").Trim();

        // A fresh launcher (process restart) adopts the persisted marker only.
        var adopted = tmux.Launcher(clock);
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(5);
            var result = await adopted.EnsureAsync(tmux.Request, CancellationToken.None);
            Assert.Null(result.Error);
            Assert.True(result.Started);
        }

        Assert.Equal(selectedBefore, tmux.Run("display-message", "-p", "-t", sessionId, "#{window_id}").Trim());
        Assert.Equal(2, tmux.PaneIds(sessionId).Length);
        Assert.Contains(userPane, tmux.PaneIds(sessionId));
    }

    [Fact]
    public async Task ForeignOwnerTokenIsNeverAdoptedReplacedOrKilled()
    {
        using var tmux = RealTmux.Create();

        // A session with our exact name but a different owner token already exists.
        tmux.Run("new-session", "-d", "-s", tmux.SessionName, "-e",
            $"{TmuxAttachLauncher.OwnerEnvironmentVariable}=someone-else", "/bin/sleep", "300");
        var foreignId = tmux.SessionId();
        var foreignPanes = tmux.PaneIds(foreignId);

        var launcher = tmux.Launcher();
        var result = await launcher.EnsureAsync(tmux.Request, CancellationToken.None);
        Assert.False(result.Started);
        Assert.False(result.Owned);
        Assert.False(launcher.IsOwned);
        Assert.Contains("not owned by this runtime", result.Error);

        // Nothing was created, marked or killed in the foreign session.
        Assert.Equal(foreignPanes, tmux.PaneIds(foreignId));
        Assert.Equal(string.Empty, tmux.ShowOption(foreignId));
        Assert.False(await launcher.KillOwnedAsync(tmux.Request.OwnerToken, CancellationToken.None));
        Assert.Single(tmux.SessionNames());
        Assert.Equal(0, tmux.ExitCodeOf("has-session", "-t", foreignId));
    }

    [Fact]
    public async Task KillOwnedRemovesOnlyTheOwnedSessionAndRefusesAForeignToken()
    {
        using var tmux = RealTmux.Create();
        var launcher = tmux.Launcher();
        Assert.True((await launcher.EnsureAsync(tmux.Request, CancellationToken.None)).Started);

        // A wrong owner token must never kill the session.
        Assert.False(await launcher.KillOwnedAsync("not-the-owner", CancellationToken.None));
        Assert.Contains(tmux.SessionName, tmux.SessionNames());

        // Ownership was dropped by the failed recheck; re-establish it, then kill.
        Assert.True((await launcher.EnsureAsync(tmux.Request, CancellationToken.None)).Started);
        Assert.True(await launcher.KillOwnedAsync(tmux.Request.OwnerToken, CancellationToken.None));
        Assert.False(launcher.IsOwned);
        Assert.DoesNotContain(tmux.SessionName, tmux.SessionNames());
    }

    /// <summary>
    /// Real tmux resolves a bare session target by unique prefix. A session whose
    /// name merely starts with (or is a prefix of) ours must never be resolved,
    /// probed, marked, selected or killed.
    /// </summary>
    [Theory]
    [InlineData("{0}-overlap")]
    [InlineData("{0}x")]
    public async Task PrefixOverlappingSessionIsNeverResolvedOrModified(string template)
    {
        using var tmux = RealTmux.Create();
        var neighbour = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, tmux.SessionName);
        tmux.Run("new-session", "-d", "-s", neighbour, "-e",
            $"{TmuxAttachLauncher.OwnerEnvironmentVariable}={tmux.Request.OwnerToken}", "/bin/sleep", "300");
        var neighbourId = tmux.SessionId(neighbour);
        var neighbourPanes = tmux.PaneIds(neighbourId);
        tmux.Run("set-option", "-t", neighbourId, "@agentcontrol_attach_pane", "untouched");

        var launcher = tmux.Launcher();
        var result = await launcher.EnsureAsync(tmux.Request, CancellationToken.None);
        Assert.Null(result.Error);
        Assert.True(result.Started);

        // Our own session was created alongside the neighbour, not adopted from it.
        var ownId = tmux.SessionId();
        Assert.NotEqual(neighbourId, ownId);
        Assert.Equal(2, tmux.SessionNames().Length);
        Assert.Equal(neighbourPanes, tmux.PaneIds(neighbourId));
        Assert.Equal("untouched", tmux.ShowOption(neighbourId));
        Assert.Equal($"owner-{tmux.Token}:{tmux.PaneIds(ownId).Single()}", tmux.ShowOption(ownId));

        // Shutdown kills only ours.
        Assert.True(await launcher.KillOwnedAsync(tmux.Request.OwnerToken, CancellationToken.None));
        Assert.Equal(new[] { neighbour }, tmux.SessionNames());
        Assert.Equal(neighbourPanes, tmux.PaneIds(neighbourId));
        Assert.Equal("untouched", tmux.ShowOption(neighbourId));
    }

    /// <summary>
    /// A prefix-overlapping session that exists *before* ours must not satisfy
    /// the existence probe, and must not be extended with our attach window.
    /// </summary>
    [Fact]
    public async Task OverlappingNeighbourDoesNotSatisfyTheExistenceProbe()
    {
        using var tmux = RealTmux.Create();
        var neighbour = tmux.SessionName + "-neighbour";
        tmux.Run("new-session", "-d", "-s", neighbour, "/bin/sleep", "300");

        // Real tmux would resolve the bare name by unique prefix; '=' must not.
        Assert.Equal(0, tmux.ExitCodeOf("has-session", "-t", tmux.SessionName));
        Assert.Equal(1, tmux.ExitCodeOf("has-session", "-t", "=" + tmux.SessionName));

        Assert.True((await tmux.Launcher().EnsureAsync(tmux.Request, CancellationToken.None)).Started);
        Assert.Single(tmux.PaneIds(tmux.SessionId(neighbour)));
        Assert.Equal(string.Empty, tmux.ShowOption(tmux.SessionId(neighbour)));
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }

    private sealed class RealTmux : IDisposable
    {
        private const int CommandTimeoutMilliseconds = 15_000;
        private const string ReadyMarker = "child.ready";

        private static readonly string CanonicalTmuxWrapper =
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "tmux-wrapper.sh");

        private static readonly string CanonicalAttachClient =
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake-opencode.sh");

        private readonly string _root;
        private readonly string _wrapper;
        private readonly string _readyPath;
        private readonly FileSystemWatcher _watcher;
        private readonly TaskCompletionSource<bool> _childReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string SessionName { get; }
        public string Token { get; }
        public TmuxAttachRequest Request { get; }

        private RealTmux(string tmuxBinary)
        {
            _root = Directory.CreateTempSubdirectory("tmux-real-").FullName;
            Token = Guid.NewGuid().ToString("N")[..8];
            SessionName = "acptest-" + Token;

            var workspace = Path.Combine(_root, "workspace");
            var home = Path.Combine(_root, "home");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(home);

            // Both executables are checked-in fixtures reached through a unique
            // per-test symlink. The executable inodes are shared and never
            // rewritten, so a concurrent fork/exec can never observe a
            // half-written image (ETXTBSY, issue #232).
            _wrapper = LinkFixture(CanonicalTmuxWrapper, "tmux");
            var client = LinkFixture(CanonicalAttachClient, "fake-opencode");

            // The wrapper needs per-test values a shared fixture cannot hardcode:
            // the real tmux binary and the private server socket. They go in a
            // non-executable sidecar resolved relative to the symlink's own path.
            File.WriteAllLines(
                Path.Combine(_root, "tmux-wrapper.side"),
                new[] { tmuxBinary, Path.Combine(_root, "socket") });

            // Observe the attach client's readiness through a real filesystem
            // event rather than polling. The client publishes this marker only
            // after its environment and argv records have been written and closed.
            _readyPath = Path.Combine(_root, ReadyMarker);
            _watcher = new FileSystemWatcher(_root)
            {
                Filter = ReadyMarker,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            };
            _watcher.Created += OnChildReady;
            _watcher.Changed += OnChildReady;
            _watcher.Renamed += OnChildReady;
            _watcher.EnableRaisingEvents = true;

            Request = new TmuxAttachRequest(
                "http://127.0.0.1:12345",
                workspace,
                home,
                "ses_" + Token,
                "opencode",
                "ephemeral-" + Token,
                "owner-" + Token,
                true,
                client);
        }

        /// <summary>
        /// Returns a private tmux server on its own socket. A missing tmux is a
        /// hard failure: this coverage is required and must never become a silent
        /// no-op or be switchable off from the environment.
        /// </summary>
        public static RealTmux Create() => new(ResolveTmux());

        private string LinkFixture(string canonical, string name)
        {
            if (!File.Exists(canonical))
            {
                throw new FileNotFoundException(
                    $"Canonical fixture was not copied to '{canonical}'.", canonical);
            }

            var link = Path.Combine(_root, name);
            File.CreateSymbolicLink(link, canonical);
            return link;
        }

        public TmuxAttachLauncher Launcher(TimeProvider? clock = null) => new(SessionName, _wrapper, clock);

        /// <summary>Runs a tmux command and asserts it succeeded.</summary>
        public string Run(params string[] arguments)
        {
            var (exitCode, output) = Capture(arguments);
            Assert.True(exitCode == 0, $"tmux {arguments[0]} failed with exit code {exitCode}.");
            return output;
        }

        /// <summary>Runs a tmux command and returns its exit code only.</summary>
        public int ExitCodeOf(params string[] arguments) => Capture(arguments).ExitCode;

        private (int ExitCode, string Output) Capture(params string[] arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _wrapper,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)!;
            // Start draining both pipes before waiting. Reading synchronously
            // first could block forever on a full pipe and never reach the
            // bounded wait, so the timeout must be armed up front.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(CommandTimeoutMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // The process raced to exit between the check and the kill.
                }
                Assert.True(process.WaitForExit(CommandTimeoutMilliseconds), "tmux child was not reaped after kill.");
                Assert.Fail($"tmux {arguments[0]} did not exit within {CommandTimeoutMilliseconds} ms.");
            }
            // The process is reaped; bound the already-started readers in case a
            // descendant inherited the pipe descriptors.
            if (!Task.WaitAll(new Task[] { stdout, stderr }, CommandTimeoutMilliseconds))
            {
                Assert.Fail($"tmux {arguments[0]} left its output streams open after exit.");
            }
            return (process.ExitCode, stdout.Result);
        }

        /// <summary>
        /// Live session names. Killing the last session stops the tmux server
        /// entirely, so "no server running" is reported as an empty list rather
        /// than a command failure.
        /// </summary>
        public string[] SessionNames()
        {
            var (exitCode, output) = Capture("list-sessions", "-F", "#{session_name}");
            return exitCode == 0 ? Lines(output) : [];
        }

        public string SessionId(string? name = null)
        {
            name ??= SessionName;
            var match = Lines(Run("list-sessions", "-F", "#{session_id} #{session_name}"))
                .Where(line => line.Split(' ', 2)[1] == name)
                .Select(line => line.Split(' ', 2)[0])
                .ToArray();
            return Assert.Single(match);
        }

        public string[] PaneIds(string sessionId) =>
            Lines(Run("list-panes", "-s", "-t", sessionId, "-F", "#{pane_id}"));

        public string ShowOption(string sessionId) =>
            Run("show-options", "-qv", "-t", sessionId, "@agentcontrol_attach_pane").Trim();

        public async Task<Dictionary<string, string>> ChildEnvironmentAsync()
        {
            await WaitForChildAsync();
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in Lines(await File.ReadAllTextAsync(Path.Combine(_root, "child.env"))))
            {
                var separator = line.IndexOf('=', StringComparison.Ordinal);
                if (separator > 0)
                {
                    result[line[..separator]] = line[(separator + 1)..];
                }
            }
            return result;
        }

        public async Task<string[]> ChildArgumentsAsync()
        {
            await WaitForChildAsync();
            return Lines(await File.ReadAllTextAsync(Path.Combine(_root, "child.args")));
        }

        /// <summary>
        /// Awaits the attach-client readiness marker. The fixture creates it only
        /// after both records are fully written, so observing it synchronizes the
        /// test on published data instead of sleeping on a guessed delay.
        /// </summary>
        private async Task WaitForChildAsync()
        {
            if (File.Exists(_readyPath))
            {
                return;
            }
            try
            {
                await _childReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (TimeoutException)
            {
                Assert.Fail("Attach client did not publish its environment/argv within 15 s.");
            }
        }

        private void OnChildReady(object sender, FileSystemEventArgs e)
        {
            if (File.Exists(_readyPath))
            {
                _childReady.TrySetResult(true);
            }
        }

        private static string[] Lines(string value) =>
            value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        /// <summary>
        /// Locates the real tmux binary, failing loudly when it is absent. This
        /// suite is required coverage on the supported Linux hosts, so there is
        /// deliberately no environment opt-out and no inconclusive no-op path.
        /// </summary>
        private static string ResolveTmux()
        {
            var found = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory, "tmux"))
                .Concat(new[] { "/usr/bin/tmux", "/bin/tmux", "/usr/local/bin/tmux" })
                .FirstOrDefault(File.Exists);

            if (found is null)
            {
                throw new Xunit.Sdk.XunitException(
                    "Real tmux integration coverage is required, but no tmux binary was found on PATH. "
                    + "Install tmux; the CI build-test job does.");
            }

            return found;
        }

        public void Dispose()
        {
            _watcher.Dispose();
            try
            {
                // Kills the private server and every /bin/sleep it owns.
                Capture("kill-server");
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                // The server may already be gone; the socket lives in _root anyway.
            }

            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: the temporary directory is disposable.
            }
        }
    }
}
