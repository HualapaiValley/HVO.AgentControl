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
                Assert.Contains(call.Args[Array.IndexOf(call.Args, "-t") + 1], new[] { "=test", "=test:", "%1" });
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
    [InlineData("foreign", 2)]
    [InlineData("has-session", 1)]
    [InlineData("show-environment", 2)]
    [InlineData("show-options", 3)]
    [InlineData("list-panes", 4)]
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
            Assert.Equal(before + 3, fake.Calls().Length);
        }
        Assert.All(fake.Calls(), call => Assert.Contains(call.Args[0], new[] { "has-session", "show-environment", "show-options" }));
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
        Assert.Equal("show-environment", fake.Calls()[before].Args[0]);
        Assert.Equal(foreignAtShutdown ? 0 : 1, fake.Calls().Count(call => call.Args[0] == "kill-session"));
    }

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
        public string StatePath => Path.Combine(_directory, "state.json");
        public TmuxAttachRequest Request { get; }

        public FakeTmux()
        {
            _executable = Path.Combine(_directory, "fake tmux");
            File.WriteAllText(_executable, "#!/usr/bin/python3\n" + "ROOT = " + JsonSerializer.Serialize(_directory) + "\n" + """
                import json, os, sys
                args = sys.argv[1:]
                with open(os.path.join(ROOT, 'calls.jsonl'), 'a') as log:
                    log.write(json.dumps({'Args': args, 'Env': dict(os.environ)}) + '\n')
                path = os.path.join(ROOT, 'state.json')
                state = json.load(open(path)) if os.path.exists(path) else None
                command = args[0]
                failure = os.path.join(ROOT, 'failure')
                if os.path.exists(failure) and open(failure).read() == command: sys.exit(2)
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
                elif command in ('new-session', 'new-window'):
                    assert args[args.index('-F') + 1] == '#{pane_id}' and '-P' in args
                    if command == 'new-session':
                        if state: sys.exit(1)
                        owner = args[args.index('-e') + 1].split('=', 1)[1]
                        state = {'owner': owner, 'panes': {}, 'identity': ''}
                        pane = '%1'
                    else:
                        assert args[args.index('-t') + 1] == '=test:'
                        pane = '%2'
                    state['panes'][pane] = 0
                    json.dump(state, open(path, 'w'))
                    print(pane)
                elif command == 'kill-session':
                    os.remove(path)
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
            File.WriteAllText(StatePath, JsonSerializer.Serialize(new { owner, panes, identity = target == "unknown" ? "" : owner + ":%1" }));
        }
        public void Fail(string? command) => File.WriteAllText(Path.Combine(_directory, "failure"), command ?? "");
        public Call[] Calls()
        {
            var path = Path.Combine(_directory, "calls.jsonl");
            return File.Exists(path) ? File.ReadAllLines(path).Select(line => JsonSerializer.Deserialize<Call>(line)!).ToArray() : [];
        }
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
