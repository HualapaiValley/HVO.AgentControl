using HVO.AgentControl.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class AcpControlHostTests
{
    [Fact]
    public async Task DisabledConfigurationLaunchesNothingAndReportsDisabled()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-off-").FullName;
        using var host = CreateHost(new ControlOptions { Enabled = false, DataDirectory = data, TmuxSessionName = "agentcontrol-test" });

        await host.StartAsync(CancellationToken.None);

        var status = host.GetStatus();
        Assert.Equal("disabled", status.State);
        Assert.Null(status.SessionId);
        Assert.Null(status.Error);
        Assert.False(status.TerminalReady);
        Assert.Equal("agentcontrol-test", host.TmuxSessionName);
        Assert.False(await host.CancelAsync(CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(data, "home")));

        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CancelAsyncReturnsTrueWhenSessionIsEstablished()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-cancel-").FullName;
        using var host = CreateHost(ReadyOptions(data));

        await host.StartAsync(CancellationToken.None);
        await WaitForStateAsync(host, "ready", TimeSpan.FromSeconds(30));

        Assert.True(await host.CancelAsync(CancellationToken.None));

        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task BootstrapNonEndTurnStopReasonIsDegradedAndNotBusy()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-stop-").FullName;
        using var host = CreateHost(ReadyOptions(data, scenario: "prompt_stop"));

        await host.StartAsync(CancellationToken.None);
        var status = await WaitForStateAsync(host, "degraded", TimeSpan.FromSeconds(30));

        Assert.NotEqual("ready", status.State);
        Assert.NotEqual("busy", status.SessionState);
        Assert.NotNull(status.Error);

        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task BootstrapRemoteErrorIsDegradedAndClearsBusy()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-promptfail-").FullName;
        using var host = CreateHost(ReadyOptions(data, scenario: "prompt_error"));

        await host.StartAsync(CancellationToken.None);
        var status = await WaitForStateAsync(host, "degraded", TimeSpan.FromSeconds(30));

        Assert.Null(status.SessionState);
        Assert.NotNull(status.Error);

        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task BootstrapSchemaFaultReportsFaultedNotReady()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-prompt-schema-").FullName;
        using var host = CreateHost(ReadyOptions(data, scenario: "prompt_schema_error"));

        await host.StartAsync(CancellationToken.None);
        var status = await WaitForStateAsync(host, "faulted", TimeSpan.FromSeconds(30));

        Assert.NotEqual("ready", status.State);
        Assert.NotNull(status.Error);

        await host.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// A transient provider/model failure during bootstrap leaves the host
    /// degraded. The session and transport are still owned, so cancellation must
    /// keep working: the degraded state has to stay recoverable and inspectable
    /// rather than becoming a dead end that only a restart clears.
    /// </summary>
    [Theory]
    [InlineData("prompt_error")]
    [InlineData("prompt_stop")]
    public async Task DegradedHostStillAcceptsCancelWithoutClaimingReady(string scenario)
    {
        var data = Directory.CreateTempSubdirectory("acp-host-degraded-cancel-").FullName;
        using var host = CreateHost(ReadyOptions(data, scenario));

        await host.StartAsync(CancellationToken.None);
        var status = await WaitForStateAsync(host, "degraded", TimeSpan.FromSeconds(30));

        // The failure stays visible: no false promotion to ready.
        Assert.Equal("degraded", status.State);
        Assert.NotNull(status.Error);
        Assert.True(status.CanControl);

        // Recovery is available precisely because the child is still alive.
        Assert.True(await host.CancelAsync(CancellationToken.None));

        // Accepting a cancel is not a readiness claim; the state is unchanged.
        var after = host.GetStatus();
        Assert.Equal("degraded", after.State);
        Assert.NotNull(after.Error);
        Assert.False(Program.IsRuntimeReady(after));

        await host.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// The same degraded runtime with an attached terminal: the controls the
    /// parent gates on (cancel + terminal attach) are all available, while the
    /// status keeps reporting degraded with its error as evidence.
    /// </summary>
    [Fact]
    public async Task DegradedHostWithReadyTerminalKeepsControlsAndDegradedEvidence()
    {
        using var fake = new FakeTmuxSession();
        var data = Directory.CreateTempSubdirectory("acp-host-degraded-term-").FullName;
        var options = ReadyOptions(data, scenario: "prompt_error");
        options.EnableTerminal = true;
        using var host = CreateHost(options);

        // Inject the fake launcher so terminal readiness is exercised without a
        // real tmux server or a real OpenCode attach client.
        typeof(AcpControlHost)
            .GetField("_terminal", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(host, fake.Launcher());

        await host.StartAsync(CancellationToken.None);
        await WaitForStateAsync(host, "degraded", TimeSpan.FromSeconds(30));
        var status = await WaitForAsync(
            host,
            candidate => candidate.TerminalReady,
            "a ready terminal",
            TimeSpan.FromSeconds(30));

        // Controls are allowed...
        Assert.True(status.CanControl);
        Assert.True(status.TerminalReady);
        Assert.True(await host.CancelAsync(CancellationToken.None));

        // ...but the degraded outcome and its error remain the reported truth,
        // and a ready terminal never makes the runtime ready.
        Assert.Equal("degraded", status.State);
        Assert.NotNull(status.Error);
        Assert.False(Program.IsRuntimeReady(status));

        await host.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// A protocol/schema fault is categorically different from a degraded
    /// provider failure: the host tears the child down, so no control is offered
    /// even though the snapshot still carries the session id as evidence.
    /// </summary>
    [Fact]
    public async Task ProtocolFaultNeverAllowsControl()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-fault-nocontrol-").FullName;
        using var host = CreateHost(ReadyOptions(data, scenario: "prompt_schema_error"));

        await host.StartAsync(CancellationToken.None);
        var status = await WaitForStateAsync(host, "faulted", TimeSpan.FromSeconds(30));

        Assert.False(status.CanControl);
        Assert.False(status.TerminalReady);
        Assert.False(Program.IsRuntimeReady(status));

        // Fault publication denies cancellation even before teardown completes.
        Assert.False(await host.CancelAsync(CancellationToken.None));

        await host.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(ControlState.Faulted)]
    [InlineData(ControlState.Stopped)]
    [InlineData(ControlState.Disabled)]
    public async Task PublishedUnavailableStateRejectsCancelBeforeChildTeardown(ControlState state)
    {
        var data = Directory.CreateTempSubdirectory("acp-host-cancel-state-").FullName;
        using var host = CreateHost(ReadyOptions(data));
        await host.StartAsync(CancellationToken.None);
        await WaitForStateAsync(host, "ready", TimeSpan.FromSeconds(30));

        try
        {
            // Hold the publication-before-teardown condition without a timing race.
            // Existing tests exercise the real Fault path and degraded recovery.
            typeof(AcpControlHost)
                .GetMethod("SetStatus", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(host, [state, "Test state published before teardown."]);
            var process = (System.Diagnostics.Process)typeof(AcpControlHost)
                .GetField("_process", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(host)!;

            Assert.False(process.HasExited);
            Assert.False(host.GetStatus().CanControl);
            Assert.False(await host.CancelAsync(CancellationToken.None));
            Assert.False(process.HasExited);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A bootstrap prompt that never returns must not hold the host in a
    /// permanently busy state. The prompt deadline bounds the wait, the host
    /// reconciles by cancelling the turn it abandoned, and the outcome is a
    /// degraded-but-controllable runtime rather than a silent success.
    /// </summary>
    [Fact]
    public async Task BootstrapPromptTimeoutIsBoundedDegradedAndCancelsTheAbandonedTurn()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-prompt-hang-").FullName;
        var options = ReadyOptions(data, scenario: "prompt_hang");
        options.PromptTimeoutSeconds = 2;
        using var host = CreateHost(options);

        await host.StartAsync(CancellationToken.None);
        var status = await WaitForStateAsync(host, "degraded", TimeSpan.FromSeconds(30));

        Assert.Contains("timed out", status.Error!, StringComparison.Ordinal);
        // Uncertainty is reported honestly: a timeout is not proof the turn did
        // not run, and the session state is unknown rather than a stale "busy".
        Assert.Contains("may still have run", status.Error!, StringComparison.Ordinal);
        Assert.NotEqual("busy", status.SessionState);

        // The abandoned turn is reconciled, not left running.
        var callsPath = Path.Combine(data, "home", "calls.log");
        await WaitForFileContainsAsync(callsPath, "session/cancel", TimeSpan.FromSeconds(15));

        // The runtime stays recoverable after the timeout.
        Assert.True(host.GetStatus().CanControl);
        Assert.True(await host.CancelAsync(CancellationToken.None));

        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ReadySessionCanControlAndDisabledRuntimeCannot()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-cancontrol-").FullName;
        using var host = CreateHost(ReadyOptions(data, scenario: "prompt_fast"));

        await host.StartAsync(CancellationToken.None);
        var status = await WaitForStateAsync(host, "ready", TimeSpan.FromSeconds(30));
        Assert.True(status.CanControl);
        await host.StopAsync(CancellationToken.None);

        var offData = Directory.CreateTempSubdirectory("acp-host-cancontrol-off-").FullName;
        using var disabled = CreateHost(new ControlOptions { Enabled = false, DataDirectory = offData });
        await disabled.StartAsync(CancellationToken.None);
        Assert.False(disabled.GetStatus().CanControl);
        await disabled.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NewSessionBecomesReadyAndPersistsBeforeBootstrap()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-new-").FullName;
        var options = ReadyOptions(data);
        using var host = CreateHost(options);

        await host.StartAsync(CancellationToken.None);
        var status = await WaitForStateAsync(host, "ready", TimeSpan.FromSeconds(30));

        Assert.Equal(AcpFakeServer.DefaultSessionId, status.SessionId);
        Assert.Null(status.Error);
        Assert.False(status.TerminalReady);

        var persisted = RuntimeStateStore.Load(Path.Combine(data, "runtime.json"));
        Assert.NotNull(persisted);
        Assert.Equal(AcpFakeServer.DefaultSessionId, persisted!.SessionId);
        Assert.False(string.IsNullOrWhiteSpace(persisted.OrganizationId));
        Assert.False(string.IsNullOrWhiteSpace(persisted.TmuxOwnerToken));

        var callsPath = Path.Combine(data, "home", "calls.log");
        await WaitForFileContainsAsync(callsPath, "session/prompt", TimeSpan.FromSeconds(15));

        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RecordedSessionLoadsWithoutSecondBootstrap()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-load-").FullName;
        var options = ReadyOptions(data);

        using (var first = CreateHost(options))
        {
            await first.StartAsync(CancellationToken.None);
            await WaitForStateAsync(first, "ready", TimeSpan.FromSeconds(30));
            var callsPath = Path.Combine(data, "home", "calls.log");
            await WaitForFileContainsAsync(callsPath, "session/prompt", TimeSpan.FromSeconds(15));
            await first.StopAsync(CancellationToken.None);
        }

        var callsFile = Path.Combine(data, "home", "calls.log");
        var promptsBefore = CountOccurrences(File.ReadAllText(callsFile), "session/prompt");

        using var second = CreateHost(options);
        await second.StartAsync(CancellationToken.None);
        var status = await WaitForStateAsync(second, "ready", TimeSpan.FromSeconds(30));

        Assert.Equal(AcpFakeServer.DefaultSessionId, status.SessionId);
        await WaitForFileContainsAsync(callsFile, "session/load", TimeSpan.FromSeconds(15));
        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.Equal(promptsBefore, CountOccurrences(File.ReadAllText(callsFile), "session/prompt"));

        await second.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LoadFailureFaultsWithoutCreatingNewSession()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-loadfail-").FullName;
        Directory.CreateDirectory(data);

        var state = RuntimeStateStore.CreateNew("AgentControl Development", () => "org-recorded");
        state.SessionId = "ses_recorded";
        RuntimeStateStore.Save(Path.Combine(data, "runtime.json"), state);

        var options = ReadyOptions(data, scenario: "load_error");
        using var host = CreateHost(options);

        await host.StartAsync(CancellationToken.None);
        var status = await WaitForStateAsync(host, "faulted", TimeSpan.FromSeconds(30));

        Assert.NotNull(status.Error);
        Assert.Contains("could not be loaded", status.Error!, StringComparison.Ordinal);

        var callsFile = Path.Combine(data, "home", "calls.log");
        var calls = File.Exists(callsFile) ? File.ReadAllText(callsFile) : string.Empty;
        Assert.Contains("session/load", calls, StringComparison.Ordinal);
        Assert.DoesNotContain("session/new", calls, StringComparison.Ordinal);

        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task InitializeToolSchemaFaultReportsFaultedNotReady()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-initerr-").FullName;
        var options = ReadyOptions(data, scenario: "init_error");
        using var host = CreateHost(options);

        await host.StartAsync(CancellationToken.None);
        var status = await WaitForStateAsync(host, "faulted", TimeSpan.FromSeconds(30));

        Assert.NotEqual("ready", status.State);
        Assert.NotNull(status.Error);
        Assert.Contains("initialize exploded", status.Error!, StringComparison.Ordinal);

        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StatusModelIsUnknownUntilObservedAndNeverTheStartupDefault()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-model-unknown-").FullName;
        using var host = CreateHost(ReadyOptions(data));

        await host.StartAsync(CancellationToken.None);
        var status = await WaitForStateAsync(host, "ready", TimeSpan.FromSeconds(30));

        // The native HTTP read path is unreachable for the fake ACP server, so
        // the configured startup model must never be reported as the live model.
        Assert.Equal("unknown", status.Model);
        Assert.NotEqual("opencode/big-pickle", status.Model);
        Assert.Empty(status.Models);

        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SetModelAsyncRejectsMalformedReference()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-model-invalid-").FullName;
        using var host = CreateHost(new ControlOptions { Enabled = false, DataDirectory = data });

        await Assert.ThrowsAsync<ArgumentException>(
            () => host.SetModelAsync("not-a-model", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => host.SetModelAsync("opencode/", CancellationToken.None));
    }

    [Fact]
    public async Task SetModelAsyncReturnsFalseWithoutEstablishedSession()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-model-absent-").FullName;
        using var host = CreateHost(new ControlOptions { Enabled = false, DataDirectory = data });

        Assert.False(await host.SetModelAsync("opencode/big-pickle", CancellationToken.None));
    }

    [Fact]
    public async Task SetModelAsyncDoesNotClaimConfirmationWhenReadbackIsUnavailable()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-model-set-").FullName;
        var options = ReadyOptions(data, scenario: "prompt_fast");
        options.SessionStatePollSeconds = 1;
        using var host = CreateHost(options);

        await host.StartAsync(CancellationToken.None);
        await WaitForStateAsync(host, "ready", TimeSpan.FromSeconds(30));

        var callsPath = Path.Combine(data, "home", "calls.log");
        await WaitForFileContainsAsync(callsPath, "session/prompt", TimeSpan.FromSeconds(15));
        await Task.Delay(300);

        Assert.False(await host.SetModelAsync("opencode/big-pickle", CancellationToken.None));
        await WaitForFileContainsAsync(callsPath, "session/set_config_option", TimeSpan.FromSeconds(5));
        Assert.Equal("unknown", host.GetStatus().Model);

        // Receipt without authoritative readback is not confirmed session state.
        await Task.Delay(1500);
        Assert.Equal("unknown", host.GetStatus().Model);

        await host.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("string")]
    [InlineData("2")]
    [InlineData("true")]
    public async Task InvalidInitializeVersionIsRejectedBeforeAnySessionCall(string version)
    {
        foreach (var recorded in new[] { false, true })
        {
            var data = Directory.CreateTempSubdirectory("acp-version-").FullName;
            if (recorded)
            {
                var state = RuntimeStateStore.CreateNew("AgentControl Development");
                state.SessionId = "ses_recorded";
                RuntimeStateStore.Save(Path.Combine(data, "runtime.json"), state);
            }
            using var host = CreateHost(ReadyOptions(data, "init_version_" + version));
            await host.StartAsync(CancellationToken.None);
            var status = await WaitForStateAsync(host, "faulted", TimeSpan.FromSeconds(30));
            Assert.Contains("numeric protocolVersion 1", status.Error, StringComparison.Ordinal);
            Assert.Null(status.SessionId);
            Assert.False(status.TerminalReady);
            await host.StopAsync(CancellationToken.None);
            var calls = File.ReadAllLines(Path.Combine(data, "home", "calls.log"));
            Assert.Equal(new[] { "initialize" }, calls);
        }
    }

    private static ControlOptions ReadyOptions(string dataDirectory, string scenario = "happy")
    {
        return new ControlOptions
        {
            Enabled = true,
            DataDirectory = dataDirectory,
            OpenCodeExecutable = AcpFakeServer.CreateExecutable(scenario),
            NativePort = GetFreePort(),
            EnableTerminal = false,
            StartupTimeoutSeconds = 15,
            PromptTimeoutSeconds = 15,
        };
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static AcpControlHost CreateHost(ControlOptions options)
    {
        return new AcpControlHost(Options.Create(options), NullLogger<AcpControlHost>.Instance);
    }

    private static async Task<ControlStatus> WaitForStateAsync(AcpControlHost host, string expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = host.GetStatus();
            if (string.Equals(status.State, expected, StringComparison.Ordinal))
            {
                return status;
            }

            if (string.Equals(status.State, "faulted", StringComparison.Ordinal) && expected != "faulted")
            {
                throw new Xunit.Sdk.XunitException($"Host faulted while waiting for '{expected}': {status.Error}");
            }

            await Task.Delay(100);
        }

        var final = host.GetStatus();
        throw new Xunit.Sdk.XunitException($"Timed out waiting for state '{expected}'. Current: {final.State} ({final.Error}).");
    }

    private static async Task<ControlStatus> WaitForAsync(
        AcpControlHost host,
        Func<ControlStatus, bool> predicate,
        string description,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = host.GetStatus();
            if (predicate(status))
            {
                return status;
            }

            await Task.Delay(100);
        }

        var final = host.GetStatus();
        throw new Xunit.Sdk.XunitException(
            $"Timed out waiting for {description}. Current: {final.State} (terminalReady={final.TerminalReady}, {final.Error}).");
    }

    /// <summary>
    /// Minimal scripted tmux stand-in: enough for the launcher to report an
    /// owned, live attach pane so terminal readiness can be exercised without a
    /// real tmux server or a real OpenCode attach client.
    /// </summary>
    private sealed class FakeTmuxSession : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("acp-host-tmux-").FullName;
        private readonly string _executable;

        public FakeTmuxSession()
        {
            _executable = Path.Combine(_directory, "tmux");
            File.WriteAllText(_executable, "#!/usr/bin/python3\n" + "ROOT = " + System.Text.Json.JsonSerializer.Serialize(_directory) + "\n" + """
                import json, os, sys
                args = sys.argv[1:]
                path = os.path.join(ROOT, 'state.json')
                state = json.load(open(path)) if os.path.exists(path) else None
                command = args[0]
                if command == 'has-session':
                    sys.exit(0 if state else 1)
                elif command == 'show-environment':
                    if not state: sys.exit(1)
                    print('AGENTCONTROL_OWNER=' + state['owner'])
                elif command == 'show-options':
                    print(state.get('identity', ''))
                elif command == 'set-option':
                    state['identity'] = args[-1]
                    json.dump(state, open(path, 'w'))
                elif command in ('select-window', 'select-pane'):
                    if args[-1] not in state['panes']: sys.exit(1)
                elif command == 'list-panes':
                    if not state: sys.exit(1)
                    for pane, dead in state['panes'].items(): print(pane + ' ' + str(dead))
                elif command == 'new-session':
                    if state: sys.exit(1)
                    owner = args[args.index('-e') + 1].split('=', 1)[1]
                    json.dump({'owner': owner, 'panes': {'%1': 0}, 'identity': ''}, open(path, 'w'))
                    print('%1')
                elif command == 'kill-session':
                    os.remove(path)
                else:
                    sys.exit(2)
                """);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        public TmuxAttachLauncher Launcher() => new("agentcontrol-test", _executable);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task WaitForFileContainsAsync(string path, string value, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path) && File.ReadAllText(path).Contains(value, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException($"Timed out waiting for '{value}' in '{path}'.");
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
