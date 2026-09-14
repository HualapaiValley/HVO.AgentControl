using System.Diagnostics;
using HVO.AgentControl.Runtime;
using HVO.AgentControl.Terminal;
using Microsoft.Extensions.Logging;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// The terminal teardown's time bound, exercised against a real child process
/// that refuses to exit and a real <see cref="AgentProcessLauncher"/> whose
/// privileged signal helper is scripted.
/// </summary>
/// <remarks>
/// <para>
/// This is a liveness property, not a cosmetic one. <c>StopBridgeAsync</c> runs
/// inside the request's <c>finally</c>, ahead of the single-viewer semaphore
/// release, so an unbounded wait there does not merely hang one request: it
/// permanently strands <c>/terminal</c> behind a 409 for every later viewer,
/// with no recovery short of restarting the controller. The previous code
/// waited on <c>CancellationToken.None</c> after a forced termination, which a
/// cross-UID launcher failure (or a launcher that reports success while the
/// child survives) turns into exactly that.
/// </para>
/// <para>
/// The waits are parameterized so the bound can be measured in a fraction of a
/// second instead of the production 5 + 5 s. Nothing sleeps: the assertions are
/// that the call <em>returns</em>, and the child's own liveness is read from the
/// OS rather than inferred from a delay.
/// </para>
/// </remarks>
public sealed class TerminalBridgeTeardownTests
{
    /// <summary>
    /// Graces used where the test asserts that a wait <em>ends</em>. They are
    /// short so the bound is measured in a fraction of a second rather than the
    /// production 5 + 5 s; nothing sleeps for them.
    /// </summary>
    private static readonly TimeSpan ShortGrace = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Grace used where the test asserts that an exit <em>is observed</em>. It
    /// has to be generous: a tight bound there would turn an ordinary scheduling
    /// delay into a spurious "did not exit" failure, which is a flake, not a
    /// stronger assertion.
    /// </summary>
    private static readonly TimeSpan PatientGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Generous upper bound on the whole teardown. It only has to be far below
    /// "forever" to distinguish a bounded wait from an unbounded one; a machine
    /// slow enough to exceed it would fail the rest of the suite first.
    /// </summary>
    private static readonly TimeSpan TestBound = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The launcher refused (or failed to run) the privileged signal, so no
    /// termination was ever issued. Waiting for an exit nobody requested is
    /// waiting forever: the teardown must log a category-only warning and return.
    /// </summary>
    [Fact]
    public async Task ARefusedForcedTerminationReturnsWithoutWaitingForTheChild()
    {
        using var launcher = new FakeLauncher("refuse");
        using var child = new StubbornChild();
        var logger = new CategoryLogger();

        await StopWithinBoundAsync(child.Process, launcher.Launcher, logger);

        // The child is still running, and that is precisely why the teardown had
        // to return instead of waiting: it is the container init's to reap.
        Assert.True(child.IsAlive);

        // A termination really was attempted, and really was refused.
        Assert.Single(launcher.Invocations);
        Assert.StartsWith("signal ", launcher.Invocations[0], StringComparison.Ordinal);
        Assert.EndsWith(" KILL", launcher.Invocations[0], StringComparison.Ordinal);

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("termination was not issued", warning, StringComparison.Ordinal);
        AssertNoTerminalContentLeaked(logger, child);
    }

    /// <summary>
    /// The launcher reported success but the child is still there - a signal the
    /// kernel accepted for a process that ignores or outlives it. The second
    /// wait must be bounded too, or a "successful" termination hangs forever.
    /// </summary>
    [Fact]
    public async Task AForcedTerminationThatDoesNotTakeEffectStillReturnsWithinTheGrace()
    {
        using var launcher = new FakeLauncher("swallow");
        using var child = new StubbornChild();
        var logger = new CategoryLogger();

        await StopWithinBoundAsync(child.Process, launcher.Launcher, logger);

        Assert.True(child.IsAlive);
        Assert.Single(launcher.Invocations);

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("did not exit within the forced grace", warning, StringComparison.Ordinal);
        AssertNoTerminalContentLeaked(logger, child);
    }

    /// <summary>
    /// The ordinary cross-UID path: the graceful close does not end the child,
    /// the launcher's signal does, and the teardown observes the exit rather
    /// than timing out.
    /// </summary>
    [Fact]
    public async Task AnEffectiveForcedTerminationIsAwaitedAndLogsNothing()
    {
        using var launcher = new FakeLauncher("deliver");
        using var child = new StubbornChild();
        var logger = new CategoryLogger();
        var process = child.Process;

        // The forced wait is the one that legitimately waits here, so it gets a
        // generous grace: a tight bound would turn an ordinary scheduling delay
        // into a spurious "did not exit" warning.
        await StopWithinBoundAsync(process, launcher.Launcher, logger, forcedGrace: PatientGrace);

        Assert.False(child.IsAlive);
        Assert.Single(launcher.Invocations);
        Assert.Empty(logger.Warnings);
    }

    /// <summary>
    /// A bridge that exits on the stdin close is the normal case: no signal is
    /// issued at all, and nothing is logged.
    /// </summary>
    [Fact]
    public async Task AGracefulExitIssuesNoSignalAndLogsNothing()
    {
        using var launcher = new FakeLauncher("refuse");
        using var child = new StubbornChild(exitsOnStdinClose: true);
        var logger = new CategoryLogger();

        // The graceful branch is the one that legitimately waits, so it gets the
        // production-shaped grace: shortening it would turn an ordinary slow
        // scheduler into a spurious "did not exit" failure.
        await StopWithinBoundAsync(child.Process, launcher.Launcher, logger, PatientGrace);

        Assert.False(child.IsAlive);
        Assert.Empty(launcher.Invocations);
        Assert.Empty(logger.Warnings);
    }

    /// <summary>
    /// The handle is always released, whichever branch ran. The viewer slot is
    /// released immediately afterwards in the endpoint, so a leaked handle here
    /// would accumulate one per attach for the process's lifetime.
    /// </summary>
    [Theory]
    [InlineData("refuse")]
    [InlineData("swallow")]
    [InlineData("deliver")]
    public async Task TheProcessHandleIsDisposedOnEveryPath(string mode)
    {
        using var launcher = new FakeLauncher(mode);
        using var child = new StubbornChild();
        var process = child.Process;

        await StopWithinBoundAsync(process, launcher.Launcher, logger: null);

        // A disposed Process throws on every member that needs its handle.
        Assert.Throws<InvalidOperationException>(() => process.HasExited);
    }

    private static async Task StopWithinBoundAsync(
        Process process,
        AgentProcessLauncher launcher,
        ILogger? logger,
        TimeSpan? stopGrace = null,
        TimeSpan? forcedGrace = null)
    {
        var stop = TerminalEndpoint.StopBridgeAsync(
            process, launcher, logger, stopGrace ?? ShortGrace, forcedGrace ?? ShortGrace);

        // WaitAsync fails the test on an unbounded wait instead of hanging the
        // whole run until the harness times out.
        await stop.WaitAsync(TestBound);
    }

    /// <summary>
    /// Neither the child's output nor its command line may reach the log: the
    /// bridge carries terminal content and the launcher arguments carry a PID
    /// and target detail that is not ours to surface.
    /// </summary>
    private static void AssertNoTerminalContentLeaked(CategoryLogger logger, StubbornChild child)
    {
        foreach (var message in logger.All)
        {
            Assert.DoesNotContain(StubbornChild.Marker, message, StringComparison.Ordinal);
            Assert.DoesNotContain(
                child.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                message,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A real child process standing in for the PTY bridge. It ignores SIGTERM,
    /// so no path can succeed by accident, and it prints a marker so a log leak
    /// of terminal content would be detectable.
    /// </summary>
    private sealed class StubbornChild : IDisposable
    {
        public const string Marker = "terminal-content-marker";

        private readonly int _pid;

        public StubbornChild(bool exitsOnStdinClose = false)
        {
            // `read` returns at EOF when the endpoint closes stdin, which is
            // exactly the graceful bridge shutdown; without it the child stays.
            var script = exitsOnStdinClose
                ? $"printf '%s\\n' {Marker}; read line; exit 0"
                : $"trap '' TERM; printf '%s\\n' {Marker}; while :; do sleep 0.05; done";

            var startInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(script);

            Process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("the stand-in bridge child did not start");
            _pid = Process.Id;

            // Drain like the endpoint does, so a full pipe can never be what
            // keeps the child alive.
            _ = Process.StandardOutput.ReadToEndAsync();
            _ = Process.StandardError.ReadToEndAsync();

            // Observe the child only once it is actually running: a start that
            // has not reached its trap would make these assertions meaningless.
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
            while (State() is null && DateTimeOffset.UtcNow < deadline)
            {
                Thread.Sleep(10);
            }
        }

        public Process Process { get; }

        public int ProcessId => _pid;

        /// <summary>
        /// Liveness read from the OS, not from the (possibly disposed) handle,
        /// and checked immediately rather than after a delay. When the code
        /// under test returns without terminating the child, the child is by
        /// definition still there; when it awaited an exit, the child was
        /// already reaped. Neither case needs to wait for anything.
        /// </summary>
        public bool IsAlive => State() is 'R' or 'S' or 'D' or 'T';

        private char? State()
        {
            try
            {
                // /proc/<pid>/stat is "<pid> (<comm>) <state> ...", and comm can
                // itself contain spaces or parentheses, so the state follows the
                // LAST ')'.
                var status = File.ReadAllText($"/proc/{_pid}/stat");
                var close = status.LastIndexOf(')');
                return close < 0 || close + 2 >= status.Length ? null : status[close + 2];
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            try
            {
                // The handle may already be disposed by the code under test, so
                // clean up by PID.
                using var live = Process.GetProcessById(_pid);
                live.Kill(entireProcessTree: true);
                live.WaitForExit(5_000);
            }
            catch (Exception exception) when (exception is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException)
            {
            }

            try
            {
                Process.Dispose();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    /// <summary>
    /// A real <see cref="AgentProcessLauncher"/> pointed at the checked-in
    /// launcher stand-in. The launcher type is sealed and concrete on purpose -
    /// it is the seam to a setuid binary, not an abstraction - so the scripted
    /// behavior belongs in the helper it executes, not in a substitute type.
    /// </summary>
    private sealed class FakeLauncher : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("terminal-launcher-").FullName;

        public FakeLauncher(string mode)
        {
            var canonical = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake-launcher.sh");
            if (!File.Exists(canonical))
            {
                throw new FileNotFoundException(
                    $"Canonical fake launcher fixture was not copied to '{canonical}'.", canonical);
            }

            var executable = Path.Combine(_directory, "agentcontrol-launch");
            File.CreateSymbolicLink(executable, canonical);
            File.WriteAllText(Path.Combine(_directory, "fake-launcher.mode"), mode + "\n");
            Launcher = new AgentProcessLauncher(executable);
        }

        public AgentProcessLauncher Launcher { get; }

        public string[] Invocations
        {
            get
            {
                var log = Path.Combine(_directory, "fake-launcher.log");
                return File.Exists(log)
                    ? File.ReadAllLines(log).Where(line => line.Length > 0).ToArray()
                    : [];
            }
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class CategoryLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public List<string> All { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            All.Add(message);
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(message);
            }
        }
    }
}
