using System.Text.Json;
using HVO.AgentControl.Runtime;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class TmuxAttachLauncherTests
{
    [Fact]
    public async Task LaunchPinsExecutableAndSanitizesEverySubprocessAndPaneEnvironment()
    {
        using var fake = new FakeTmux();
        var launcher = fake.Launcher();
        Assert.True((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        Assert.True((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        Assert.True(await launcher.KillOwnedAsync("owner", CancellationToken.None));
        var calls = fake.Calls();
        var start = Assert.Single(calls, call => call.Args[0] == "new-session");
        var index = Array.IndexOf(start.Args, fake.Request.OpenCodeExecutable);
        Assert.True(index > 0);
        Assert.Equal(new[] { fake.Request.OpenCodeExecutable, "attach", fake.Request.NativeUrl, "--dir", fake.Request.Workspace, "--session", "ses_test" }, start.Args[index..]);
        Assert.Contains("/usr/bin/env", start.Args);
        Assert.Contains("-i", start.Args);
        Assert.Contains("HOME=" + fake.Request.Home, start.Args);
        Assert.Contains("XDG_CONFIG_HOME=" + Path.Combine(fake.Request.Home, "config"), start.Args);
        foreach (var call in calls)
        {
            Assert.Equal(fake.Request.Home, call.Env["HOME"]);
            Assert.Equal(Path.Combine(fake.Request.Home, "data"), call.Env["XDG_DATA_HOME"]);
            Assert.Equal("ephemeral", call.Env["OPENCODE_SERVER_PASSWORD"]);
            Assert.Equal("owner", call.Env[TmuxAttachLauncher.OwnerEnvironmentVariable]);
            Assert.DoesNotContain(call.Env.Keys, key => key.StartsWith("GH_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Control__", StringComparison.OrdinalIgnoreCase));
            if (call.Args.Contains("-t"))
            {
                // Only has-session/show-environment/list-panes/kill-session may use
                // '='; option and window commands must use the immutable session id.
                var value = call.Args[Array.IndexOf(call.Args, "-t") + 1];
                Assert.Contains(value, new[] { "=test", "$0", "$0:", "%1" });
                if (call.Args[0] is "set-option" or "show-options" or "new-window")
                {
                    Assert.StartsWith("$", value, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public async Task ForeignOwnershipIsNeverReplacedIncludingAfterOwnedSessionDisappears()
    {
        using var fake = new FakeTmux();
        var clock = new Clock();
        var launcher = fake.Launcher(clock);
        await launcher.EnsureAsync(fake.Request, CancellationToken.None);
        fake.State("foreign", false);
        clock.Advance(5);
        Assert.False((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        Assert.False(launcher.IsOwned);
        Assert.False(await launcher.KillOwnedAsync("owner", CancellationToken.None));
        Assert.Single(fake.Calls(), call => call.Args[0] == "new-session");
        Assert.DoesNotContain(fake.Calls(), call => call.Args[0] == "kill-session");
    }

    [Theory]
    [InlineData("dead")]
    [InlineData("missing")]
    public async Task ExactPaneRecoveryPreservesUnrelatedLivePanes(string target)
    {
        using var fake = new FakeTmux();
        var clock = new Clock();
        var launcher = fake.Launcher(clock);
        Assert.True((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        fake.State("owner", true, target);
        fake.Fail("new-window");
        clock.Advance(5);
        Assert.False((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        Assert.False((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        fake.Fail(null);
        clock.Advance(2);
        Assert.True((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        using var state = JsonDocument.Parse(File.ReadAllText(fake.StatePath));
        Assert.Equal(0, state.RootElement.GetProperty("panes").GetProperty("%99").GetInt32());
        if (target != "missing")
        {
            Assert.Equal(1, state.RootElement.GetProperty("panes").GetProperty("%1").GetInt32());
        }
        Assert.DoesNotContain(fake.Calls(), call => call.Args[0].StartsWith("kill-", StringComparison.Ordinal));
        Assert.Contains(fake.Calls(), call => call.Args.SequenceEqual(new[] { "select-window", "-t", "%2" }));
        // A fresh launcher adopts only the persisted, owner-qualified exact pane.
        Assert.True((await fake.Launcher(clock).EnsureAsync(fake.Request, CancellationToken.None)).Started);
        Assert.Equal(2, fake.Calls().Count(call => call.Args[0] == "new-window"));
    }

    [Theory]
    [InlineData("foreign", 3)]
    [InlineData("has-session", 1)]
    [InlineData("list-sessions", 2)]
    [InlineData("show-environment", 3)]
    [InlineData("show-options", 4)]
    [InlineData("list-panes", 5)]
    public async Task AllFailedProbesBackOffAndNeverClobber(string failure, int commandsPerAttempt)
    {
        using var fake = new FakeTmux();
        var clock = new Clock();
        var launcher = fake.Launcher(clock);
        Assert.True((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        var healthyCount = fake.Calls().Length;
        clock.Advance(4);
        Assert.True((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        Assert.Equal(healthyCount, fake.Calls().Length);
        if (failure == "foreign") fake.State("foreign", false);
        else fake.Fail(failure);
        clock.Advance(1);
        Assert.False((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        Assert.Equal(healthyCount + commandsPerAttempt, fake.Calls().Length);
        foreach (var delay in new[] { 2, 4, 8, 16, 30, 30 })
        {
            var before = fake.Calls().Length;
            for (var second = 1; second < delay; second++)
            {
                clock.Advance(1);
                Assert.False((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
                Assert.Equal(before, fake.Calls().Length);
            }
            clock.Advance(1);
            Assert.False((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
            Assert.Equal(before + commandsPerAttempt, fake.Calls().Length);
        }
        Assert.Single(fake.Calls(), call => call.Args[0] == "new-session");
        Assert.DoesNotContain(fake.Calls(), call => call.Args[0] is "kill-session" or "kill-pane" or "new-window");
    }

    [Fact]
    public async Task HealthyProbesAndAdoptionDoNotChangeUserSelection()
    {
        using var fake = new FakeTmux();
        var clock = new Clock();
        var launcher = fake.Launcher(clock);
        Assert.True((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        var before = fake.Calls().Length;
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(5);
            Assert.True((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        }
        Assert.True((await fake.Launcher(clock).EnsureAsync(fake.Request, CancellationToken.None)).Started);
        Assert.Equal(4, fake.Calls().Skip(before).Count(call => call.Args[0] == "list-panes"));
        Assert.DoesNotContain(fake.Calls().Skip(before), call => call.Args[0] is "select-window" or "select-pane");
        Assert.Single(fake.Calls(), call => call.Args[0] == "select-window");
        Assert.Single(fake.Calls(), call => call.Args[0] == "select-pane");
    }

    [Theory]
    [InlineData("")]
    [InlineData("foreign:%1")]
    [InlineData("owner:1")]
    [InlineData("owner:%")]
    [InlineData("owner:%1 secret")]
    public async Task UnknownIdentityFailsClosedWithBackoff(string identity)
    {
        using var fake = new FakeTmux();
        fake.State("owner", false);
        var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(fake.StatePath))!;
        state["identity"] = identity;
        File.WriteAllText(fake.StatePath, state.ToJsonString());
        var clock = new Clock();
        var launcher = fake.Launcher(clock);
        const string error = "tmux attach-pane identity is missing or invalid; operator recovery required.";
        var result = await launcher.EnsureAsync(fake.Request, CancellationToken.None);
        Assert.False(result.Started);
        Assert.Equal(error, result.Error);
        foreach (var delay in new[] { 2, 4, 8, 16, 30 })
        {
            var before = fake.Calls().Length;
            clock.Advance(delay - 1);
            Assert.Equal(result, await launcher.EnsureAsync(fake.Request, CancellationToken.None));
            Assert.Equal(before, fake.Calls().Length);
            clock.Advance(1);
            Assert.Equal(result, await launcher.EnsureAsync(fake.Request, CancellationToken.None));
            Assert.Equal(before + 4, fake.Calls().Length);
        }
        Assert.All(fake.Calls(), call => Assert.Contains(call.Args[0], new[] { "has-session", "list-sessions", "show-environment", "show-options" }));
    }

    [Fact]
    public async Task FailedPersistenceRequiresOperatorRecoveryInsteadOfDuplicatePane()
    {
        using var fake = new FakeTmux();
        var clock = new Clock();
        fake.Fail("set-option");
        Assert.False((await fake.Launcher(clock).EnsureAsync(fake.Request, CancellationToken.None)).Started);
        fake.Fail(null);
        // Simulate restart after creation but before the identity was persisted.
        var result = await fake.Launcher(clock).EnsureAsync(fake.Request, CancellationToken.None);
        Assert.False(result.Started);
        Assert.Contains("operator recovery required", result.Error);
        Assert.Single(fake.Calls(), call => call.Args[0] == "new-session");
        Assert.DoesNotContain(fake.Calls(), call => call.Args[0] == "new-window");
    }

    [Theory]
    [InlineData("set-option", false)]
    [InlineData("set-option", true)]
    [InlineData("select-window", false)]
    [InlineData("select-window", true)]
    [InlineData("select-pane", false)]
    [InlineData("select-pane", true)]
    public async Task PartialCreationRetriesSamePaneUntilReady(string failure, bool recovery)
    {
        using var fake = new FakeTmux();
        var clock = new Clock();
        var launcher = fake.Launcher(clock);
        if (recovery)
        {
            Assert.True((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
            fake.State("owner", true);
            clock.Advance(5);
        }
        fake.Fail(failure);
        var startCount = fake.Calls().Length;
        Assert.False((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        var created = Assert.Single(fake.Calls().Skip(startCount), call => call.Args[0] is "new-session" or "new-window");
        Assert.Equal(recovery ? "new-window" : "new-session", created.Args[0]);

        foreach (var delay in new[] { 2, 4, 8, 16, 30 })
        {
            var count = fake.Calls().Length;
            clock.Advance(delay - 1);
            Assert.False((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
            Assert.Equal(count, fake.Calls().Length);
            clock.Advance(1);
            Assert.False((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        }

        fake.Fail(null);
        clock.Advance(30);
        Assert.True((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        var pane = recovery ? "%2" : "%1";
        using var state = JsonDocument.Parse(File.ReadAllText(fake.StatePath));
        Assert.Equal("owner:" + pane, state.RootElement.GetProperty("identity").GetString());
        Assert.Equal(pane, state.RootElement.GetProperty("selectedWindow").GetString());
        Assert.Equal(pane, state.RootElement.GetProperty("selectedPane").GetString());
        if (recovery) Assert.Equal(0, state.RootElement.GetProperty("panes").GetProperty("%99").GetInt32());
        Assert.Single(fake.Calls().Skip(startCount), call => call.Args[0] is "new-session" or "new-window");
        Assert.DoesNotContain(fake.Calls(), call => call.Args[0].StartsWith("kill-", StringComparison.Ordinal));

        var healthyStart = fake.Calls().Length;
        clock.Advance(5);
        Assert.True((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        Assert.DoesNotContain(fake.Calls().Skip(healthyStart), call => call.Args[0] is "select-window" or "select-pane" or "set-option");
    }

    [Fact]
    public async Task PendingCreationDoesNotCreateOrSelectAfterOwnershipChanges()
    {
        using var fake = new FakeTmux();
        var clock = new Clock();
        var launcher = fake.Launcher(clock);
        fake.Fail("set-option");
        Assert.False((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        fake.State("foreign", false);
        fake.Fail(null);
        var before = fake.Calls().Length;
        clock.Advance(2);
        Assert.False((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        Assert.DoesNotContain(fake.Calls().Skip(before), call => call.Args[0] is "new-window" or "new-session" or "select-window" or "select-pane" or "set-option");
    }

    [Fact]
    public async Task PendingPaneThatDiesRemainsNotReadyWithoutCreatingAnother()
    {
        using var fake = new FakeTmux();
        var clock = new Clock();
        var launcher = fake.Launcher(clock);
        Assert.True((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        fake.State("owner", true);
        fake.Fail("set-option");
        clock.Advance(5);
        Assert.False((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(fake.StatePath))!;
        state["panes"]!["%2"] = 1;
        File.WriteAllText(fake.StatePath, state.ToJsonString());
        fake.Fail(null);
        foreach (var delay in new[] { 2, 4, 8 })
        {
            clock.Advance(delay);
            var result = await launcher.EnsureAsync(fake.Request, CancellationToken.None);
            Assert.False(result.Started);
            Assert.Contains("operator recovery required", result.Error);
        }
        Assert.Single(fake.Calls(), call => call.Args[0] == "new-window");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InconclusiveProbePreservesOwnershipForShutdownRecheck(bool foreignAtShutdown)
    {
        using var fake = new FakeTmux();
        var clock = new Clock();
        var launcher = fake.Launcher(clock);
        Assert.True((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        fake.Fail("has-session");
        clock.Advance(5);
        Assert.False((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        Assert.True(launcher.IsOwned);
        if (foreignAtShutdown) fake.State("foreign", false);
        var before = fake.Calls().Length;
        Assert.Equal(!foreignAtShutdown, await launcher.KillOwnedAsync("owner", CancellationToken.None));
        // Shutdown re-resolves the exact name to an id before rechecking the owner.
        Assert.Equal("list-sessions", fake.Calls()[before].Args[0]);
        Assert.Equal("show-environment", fake.Calls()[before + 1].Args[0]);
        Assert.Equal(foreignAtShutdown ? 0 : 1, fake.Calls().Count(call => call.Args[0] == "kill-session"));
    }

    /// <summary>
    /// Regression for #238: real tmux 3.4 rejects '=name' for set-option
    /// ("no such session: =agentcontrol") and returns silent success for
    /// show-options -qv, which left terminalReady=false in the deployed image.
    /// Option and window commands must address the immutable session id.
    /// </summary>
    [Fact]
    public async Task OptionAndWindowCommandsUseTheResolvedSessionIdNotExactNameSyntax()
    {
        using var fake = new FakeTmux();
        var clock = new Clock();
        var launcher = fake.Launcher(clock);
        Assert.True((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        fake.State("owner", true);
        clock.Advance(5);
        Assert.True((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        Assert.True(await launcher.KillOwnedAsync("owner", CancellationToken.None));

        foreach (var call in fake.Calls().Where(call => call.Args.Contains("-t")))
        {
            var value = call.Args[Array.IndexOf(call.Args, "-t") + 1];
            if (call.Args[0] is "set-option" or "show-options" or "new-window")
            {
                Assert.StartsWith("$0", value, StringComparison.Ordinal);
            }
        }
        Assert.Contains(fake.Calls(), call => call.Args.SequenceEqual(new[] { "list-sessions", "-F", "#{session_id} #{session_name}" }));
        Assert.Contains(fake.Calls(), call => call.Args[0] == "new-window" && call.Args[Array.IndexOf(call.Args, "-t") + 1] == "$0:");
        Assert.Contains(fake.Calls(), call => call.Args[0] == "set-option" && call.Args[2] == "$0");
    }

    /// <summary>
    /// A session whose name merely shares our prefix must never be resolved,
    /// probed, marked, or killed, in either direction of the overlap.
    /// </summary>
    [Theory]
    [InlineData("testing")]
    [InlineData("test-other")]
    public async Task PrefixOverlappingSessionsAreNeverResolvedOrTouched(string overlapping)
    {
        using var fake = new FakeTmux();
        fake.AddOverlappingSession(overlapping);
        var clock = new Clock();
        var launcher = fake.Launcher(clock);
        Assert.True((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        clock.Advance(5);
        Assert.True((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        Assert.True(await launcher.KillOwnedAsync("owner", CancellationToken.None));

        // Our own session was created and removed; the foreign one is untouched.
        using var others = JsonDocument.Parse(File.ReadAllText(fake.OthersPath));
        var foreign = Assert.Single(others.RootElement.EnumerateArray());
        Assert.Equal(overlapping, foreign.GetProperty("name").GetString());
        Assert.Equal("foreign:%77", foreign.GetProperty("identity").GetString());
        Assert.False(File.Exists(fake.StatePath));
        Assert.All(fake.Calls().Where(call => call.Args.Contains("-t")), call =>
        {
            var value = call.Args[Array.IndexOf(call.Args, "-t") + 1];
            Assert.DoesNotContain("$7", value);
            Assert.DoesNotContain("%77", value);
        });
    }

    /// <summary>
    /// When the exact session name cannot be resolved to exactly one id the
    /// launcher fails closed instead of guessing a target.
    /// </summary>
    [Fact]
    public async Task UnresolvableSessionIdFailsClosedWithoutCreatingOrSelecting()
    {
        using var fake = new FakeTmux();
        var clock = new Clock();
        var launcher = fake.Launcher(clock);
        Assert.True((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        var before = fake.Calls().Length;
        fake.Fail("list-sessions");
        clock.Advance(5);
        var result = await launcher.EnsureAsync(fake.Request, CancellationToken.None);
        Assert.False(result.Started);
        Assert.Equal("tmux session id resolution failed.", result.Error);
        // Ownership is retained for a shutdown recheck; nothing was created.
        Assert.True(launcher.IsOwned);
        Assert.DoesNotContain(fake.Calls().Skip(before), call =>
            call.Args[0] is "new-window" or "new-session" or "set-option" or "select-window" or "select-pane" or "kill-session");
    }

    /// <summary>A kill after a tmux server restart must not reuse a stale id.</summary>
    [Fact]
    public async Task KillReResolvesSessionIdAndRefusesWhenTheNameIsGone()
    {
        using var fake = new FakeTmux();
        var launcher = fake.Launcher();
        Assert.True((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        File.Delete(fake.StatePath);
        var before = fake.Calls().Length;
        Assert.False(await launcher.KillOwnedAsync("owner", CancellationToken.None));
        // A failed listing cannot distinguish a stopped server from an access
        // error; retain ownership but never act on the old session ID.
        Assert.True(launcher.IsOwned);
        Assert.Equal("list-sessions", fake.Calls()[before].Args[0]);
        Assert.DoesNotContain(fake.Calls(), call => call.Args[0] == "kill-session");
    }

    /// <summary>
    /// A launcher that never owned a session (disabled or never launched) must
    /// not spawn tmux at all when asked to kill.
    /// </summary>
    [Fact]
    public async Task NeverOwnedKillSpawnsNoTmuxCommands()
    {
        using var fake = new FakeTmux();
        var launcher = fake.Launcher();
        Assert.False(await launcher.KillOwnedAsync("owner", CancellationToken.None));
        Assert.False(launcher.IsOwned);
        Assert.Empty(fake.Calls());
    }

    /// <summary>
    /// A transient session-id lookup failure is not loss of ownership: the first
    /// kill must leave ownership intact and a later retry must still kill.
    /// </summary>
    [Fact]
    public async Task TransientResolutionFailureRetainsOwnershipForRetry()
    {
        using var fake = new FakeTmux();
        var clock = new Clock();
        var launcher = fake.Launcher(clock);
        Assert.True((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);

        fake.Fail("list-sessions");
        Assert.False(await launcher.KillOwnedAsync("owner", CancellationToken.None));
        Assert.True(launcher.IsOwned);
        Assert.DoesNotContain(fake.Calls(), call => call.Args[0] == "kill-session");

        fake.Fail(null);
        Assert.True(await launcher.KillOwnedAsync("owner", CancellationToken.None));
        Assert.False(launcher.IsOwned);
        Assert.Single(fake.Calls(), call => call.Args[0] == "kill-session");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("has space")]
    [InlineData("has:colon")]
    [InlineData("has.dot")]
    [InlineData("line\nbreak")]
    [InlineData("tab\tchar")]
    [InlineData("/slash")]
    public void ConstructorRejectsUnsafeSessionNames(string sessionName) =>
        Assert.Throws<ArgumentException>(() => new TmuxAttachLauncher(sessionName));

    [Fact]
    public void ConstructorRejectsOverlongSessionName() =>
        Assert.Throws<ArgumentException>(
            () => new TmuxAttachLauncher(new string('a', HVO.AgentControl.Terminal.TerminalProtocol.MaxSessionNameLength + 1)));

    [Theory]
    [InlineData("agentcontrol")]
    [InlineData("a")]
    [InlineData("A-1_")]
    public void ConstructorAcceptsValidSessionNames(string sessionName) =>
        Assert.False(new TmuxAttachLauncher(sessionName).IsOwned);

    [Fact]
    public async Task DisabledTerminalNeverLaunchesOrProbes()
    {
        using var fake = new FakeTmux();
        var launcher = fake.Launcher();
        for (var i = 0; i < 3; i++)
        {
            Assert.False((await launcher.EnsureAsync(fake.Request with { Enabled = false }, CancellationToken.None)).Started);
        }
        Assert.Empty(fake.Calls());
    }

    [Fact]
    public async Task HostMonitorsAndRecoversTerminalWithoutRestartingAcp()
    {
        using var fake = new FakeTmux();
        var data = Directory.CreateTempSubdirectory("tmux-host-").FullName;
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var options = new ControlOptions
        {
            Enabled = true,
            EnableTerminal = true,
            DataDirectory = data,
            OpenCodeExecutable = AcpFakeServer.CreateExecutable("prompt_fast"),
            NativePort = port,
        };
        using var host = new AcpControlHost(Microsoft.Extensions.Options.Options.Create(options), Microsoft.Extensions.Logging.Abstractions.NullLogger<AcpControlHost>.Instance);
        var clock = new Clock();
        typeof(AcpControlHost).GetField("_terminal", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(host, fake.Launcher(clock));
        try
        {
            await host.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => host.GetStatus().TerminalReady);
            File.Delete(fake.StatePath);
            fake.Fail("new-session");
            clock.Advance(5);
            await WaitUntilAsync(() => !host.GetStatus().TerminalReady);
            fake.Fail(null);
            Assert.Equal("ready", host.GetStatus().State);
            Assert.True(await host.CancelAsync(CancellationToken.None));
            clock.Advance(2);
            await WaitUntilAsync(() => host.GetStatus().TerminalReady);
            Assert.Equal("ready", host.GetStatus().State);
            Assert.Equal(3, fake.Calls().Count(call => call.Args[0] == "new-session"));
            Assert.All(fake.Calls().Where(call => call.Args[0] == "new-session"), call => Assert.Contains(options.OpenCodeExecutable, call.Args));
            Assert.Single(File.ReadAllLines(Path.Combine(data, "home", "calls.log")), line => line == "initialize");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public async Task HostLogsOnlyChangedTerminalErrorsAndResetsAfterRecovery()
    {
        using var fake = new FakeTmux();
        var clock = new Clock();
        var logger = new WarningLogger();
        using var host = new AcpControlHost(Microsoft.Extensions.Options.Options.Create(new ControlOptions
        {
            EnableTerminal = true,
            OpenCodeExecutable = fake.Request.OpenCodeExecutable,
        }), logger);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        typeof(AcpControlHost).GetField("_terminal", flags)!.SetValue(host, fake.Launcher(clock));
        typeof(AcpControlHost).GetField("_ownerToken", flags)!.SetValue(host, "owner");
        var method = typeof(AcpControlHost).GetMethod("StartTerminalAsync", flags)!;
        Task Tick() => (Task)method.Invoke(host, new object[] { CancellationToken.None })!;
        fake.State("foreign", false);
        await Tick();
        await Tick();
        clock.Advance(2);
        await Tick();
        Assert.Single(logger.Warnings);
        fake.Fail("has-session");
        clock.Advance(4);
        await Tick();
        Assert.Equal(2, logger.Warnings.Count);
        fake.Fail(null);
        File.Delete(fake.StatePath);
        clock.Advance(8);
        await Tick();
        Assert.True(host.GetStatus().TerminalReady);
        fake.Fail("has-session");
        clock.Advance(5);
        await Tick();
        await Tick();
        Assert.False(host.GetStatus().TerminalReady);
        Assert.Equal(3, logger.Warnings.Count);
    }

    private sealed class WarningLogger : Microsoft.Extensions.Logging.ILogger<AcpControlHost>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!predicate())
        {
            await Task.Delay(50, timeout.Token);
        }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }

    private sealed record Call(string[] Args, Dictionary<string, string> Env);

    private sealed class FakeTmux : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("tmux-fake-").FullName;
        private readonly string _executable;
        public string OthersPath => Path.Combine(_directory, "others.json");
        public string StatePath => Path.Combine(_directory, "state.json");
        public TmuxAttachRequest Request { get; }

        public FakeTmux()
        {
            _executable = Path.Combine(_directory, "fake tmux");
            // This fake reproduces the real tmux 3.4 target grammar that issue #238
            // exposed, verified against tmux 3.4 on a private socket:
            //   * '=' exact-match syntax is honoured only by commands that resolve a
            //     session through the fuzzy target parser (has-session,
            //     show-environment, list-panes, kill-session, new-window).
            //   * set-option treats '=' as part of a literal name and fails with
            //     "no such session: =name".
            //   * show-options -qv treats it the same way but stays SILENT with exit
            //     code 0, which is why the original defect never surfaced in tests.
            // Session ids ("$N") always resolve exactly for every command.
            File.WriteAllText(_executable, "#!/usr/bin/python3\n" + "ROOT = " + JsonSerializer.Serialize(_directory) + "\n" + """
                import json, os, sys
                args = sys.argv[1:]
                with open(os.path.join(ROOT, 'calls.jsonl'), 'a') as log:
                    log.write(json.dumps({'Args': args, 'Env': dict(os.environ)}) + '\n')
                path = os.path.join(ROOT, 'state.json')
                others_path = os.path.join(ROOT, 'others.json')
                state = json.load(open(path)) if os.path.exists(path) else None
                others = json.load(open(others_path)) if os.path.exists(others_path) else []
                command = args[0]
                failure = os.path.join(ROOT, 'failure')
                if os.path.exists(failure) and open(failure).read() == command: sys.exit(2)

                def save():
                    if state is not None: json.dump(state, open(path, 'w'))
                    json.dump(others, open(others_path, 'w'))

                def sessions():
                    return ([state] if state is not None else []) + others

                def target(flag='-t'):
                    return args[args.index(flag) + 1] if flag in args else None

                def resolve(value, exact_ok):
                    # Strip the trailing window component of a 'session:' target.
                    value = value[:-1] if value.endswith(':') else value
                    if value.startswith('$'):
                        found = [s for s in sessions() if s['id'] == value]
                        return found[0] if found else None
                    if value.startswith('='):
                        if not exact_ok: return None  # '=' is a literal name character here.
                        found = [s for s in sessions() if s['name'] == value[1:]]
                        return found[0] if found else None
                    found = [s for s in sessions() if s['name'] == value]
                    if found: return found[0]
                    found = [s for s in sessions() if s['name'].startswith(value)]
                    return found[0] if len(found) == 1 else None

                if command == 'has-session':
                    sys.exit(0 if resolve(target(), True) else 1)
                elif command == 'list-sessions':
                    if not sessions(): sys.exit(1)
                    assert args[args.index('-F') + 1] == '#{session_id} #{session_name}'
                    for s in sessions(): print(s['id'] + ' ' + s['name'])
                elif command == 'show-environment':
                    s = resolve(target(), True)
                    if not s: sys.exit(1)
                    print('AGENTCONTROL_OWNER=' + s['owner'])
                elif command == 'show-options':
                    s = resolve(target(), False)
                    if s is None: sys.exit(0)  # -qv: silent and successful.
                    print(s.get('identity', ''))
                elif command == 'set-option':
                    s = resolve(target(), False)
                    if s is None:
                        sys.stderr.write('no such session: ' + str(target()) + '\n')
                        sys.exit(1)
                    s['identity'] = args[-1]
                    save()
                elif command in ('select-window', 'select-pane'):
                    owner = [s for s in sessions() if args[-1] in s['panes']]
                    if not owner: sys.exit(1)
                    owner[0]['selectedWindow' if command == 'select-window' else 'selectedPane'] = args[-1]
                    save()
                elif command == 'list-panes':
                    s = resolve(target(), True)
                    if not s: sys.exit(1)
                    for pane, dead in s['panes'].items(): print(pane + ' ' + str(dead))
                elif command in ('new-session', 'new-window'):
                    assert args[args.index('-F') + 1] == '#{session_id} #{pane_id}' and '-P' in args
                    if command == 'new-session':
                        name = args[args.index('-s') + 1]
                        if any(s['name'] == name for s in sessions()): sys.exit(1)
                        used = {s['id'] for s in sessions()}
                        ident = next('$' + str(n) for n in range(100) if '$' + str(n) not in used)
                        owner = args[args.index('-e') + 1].split('=', 1)[1]
                        state = {'id': ident, 'name': name, 'owner': owner, 'panes': {}, 'identity': ''}
                        s = state
                        pane = '%1'
                    else:
                        s = resolve(target(), True)
                        if not s: sys.exit(1)
                        pane = '%' + str(s.get('nextPane', 2))
                        s['nextPane'] = int(pane[1:]) + 1
                    s['panes'][pane] = 0
                    save()
                    print(s['id'] + ' ' + pane)
                elif command == 'kill-session':
                    s = resolve(target(), True)
                    if not s: sys.exit(1)
                    if state is not None and s is state:
                        state = None
                        os.remove(path)
                    else:
                        others.remove(s)
                    save()
                else:
                    sys.exit(2)
                """);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            Request = new("http://127.0.0.1:12345", Path.Combine(_directory, "work space"), Path.Combine(_directory, "home"), "ses_test", "opencode", "ephemeral", "owner", true, "/pinned path/opencode");
        }

        public TmuxAttachLauncher Launcher(TimeProvider? clock = null) => new("test", _executable, clock);
        public void State(string owner, bool dead, string target = "dead")
        {
            var panes = new Dictionary<string, int> { ["%99"] = 0 };
            if (target != "missing") panes["%1"] = dead ? 1 : 0;
            File.WriteAllText(StatePath, JsonSerializer.Serialize(new
            {
                id = "$0",
                name = "test",
                owner,
                panes,
                identity = target == "unknown" ? "" : owner + ":%1",
            }));
        }

        /// <summary>Adds an unowned session whose name merely shares our prefix.</summary>
        public void AddOverlappingSession(string name) => File.WriteAllText(
            OthersPath,
            JsonSerializer.Serialize(new[]
            {
                new { id = "$7", name, owner = "foreign", panes = new Dictionary<string, int> { ["%77"] = 0 }, identity = "foreign:%77" },
            }));
        public void Fail(string? command) => File.WriteAllText(Path.Combine(_directory, "failure"), command ?? "");
        public Call[] Calls()
        {
            var path = Path.Combine(_directory, "calls.jsonl");
            return File.Exists(path) ? File.ReadAllLines(path).Select(line => JsonSerializer.Deserialize<Call>(line)!).ToArray() : [];
        }
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
