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
                Assert.Contains("=test", call.Args);
            }
        }
    }

    [Fact]
    public async Task ForeignOwnershipIsNeverReplacedIncludingAfterOwnedSessionDisappears()
    {
        using var fake = new FakeTmux();
        var launcher = fake.Launcher();
        await launcher.EnsureAsync(fake.Request, CancellationToken.None);
        fake.State("foreign", false);
        Assert.False((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        Assert.False(launcher.IsOwned);
        Assert.False(await launcher.KillOwnedAsync("owner", CancellationToken.None));
        Assert.Single(fake.Calls(), call => call.Args[0] == "new-session");
        Assert.DoesNotContain(fake.Calls(), call => call.Args[0] == "kill-session");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExitedPaneRecoversOnceWithBoundedBackoff(bool retainedDeadPane)
    {
        using var fake = new FakeTmux();
        var clock = new Clock();
        var launcher = fake.Launcher(clock);
        Assert.True((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        if (retainedDeadPane)
        {
            fake.State("owner", true);
        }
        else
        {
            File.Delete(fake.StatePath);
        }
        Assert.False((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        Assert.Single(fake.Calls(), call => call.Args[0] == "new-session");
        clock.Advance(2);
        Assert.True((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        Assert.True((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
        Assert.Equal(2, fake.Calls().Count(call => call.Args[0] == "new-session"));
        Assert.Equal(retainedDeadPane ? 1 : 0, fake.Calls().Count(call => call.Args[0] == "kill-session"));

        // Repeated immediate exits cannot create a tight restart loop; delay caps at 30s.
        foreach (var delay in new[] { 4, 2, 4, 8, 16, 30, 30 })
        {
            File.Delete(fake.StatePath);
            var startsBefore = fake.Calls().Count(call => call.Args[0] == "new-session");
            Assert.False((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
            clock.Advance(delay - 1);
            Assert.False((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
            Assert.Equal(startsBefore, fake.Calls().Count(call => call.Args[0] == "new-session"));
            clock.Advance(1);
            Assert.True((await launcher.EnsureAsync(fake.Request, CancellationToken.None)).Started);
            Assert.Equal(startsBefore + 1, fake.Calls().Count(call => call.Args[0] == "new-session"));
        }
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
            await WaitUntilAsync(() => !host.GetStatus().TerminalReady);
            Assert.Equal("ready", host.GetStatus().State);
            Assert.True(await host.CancelAsync(CancellationToken.None));
            clock.Advance(2);
            await WaitUntilAsync(() => host.GetStatus().TerminalReady);
            Assert.Equal("ready", host.GetStatus().State);
            Assert.Equal(2, fake.Calls().Count(call => call.Args[0] == "new-session"));
            Assert.All(fake.Calls().Where(call => call.Args[0] == "new-session"), call => Assert.Contains(options.OpenCodeExecutable, call.Args));
            Assert.Single(File.ReadAllLines(Path.Combine(data, "home", "calls.log")), line => line == "initialize");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            Directory.Delete(data, recursive: true);
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
                if command == 'has-session':
                    sys.exit(0 if state else 1)
                elif command == 'show-environment':
                    if not state: sys.exit(1)
                    print('AGENTCONTROL_OWNER=' + state['owner'])
                elif command == 'list-panes':
                    if not state: sys.exit(1)
                    print('1' if state['dead'] else '0')
                elif command == 'new-session':
                    if state: sys.exit(1)
                    owner = args[args.index('-e') + 1].split('=', 1)[1]
                    json.dump({'owner': owner, 'dead': False}, open(path, 'w'))
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
        public void State(string owner, bool dead) => File.WriteAllText(StatePath, JsonSerializer.Serialize(new { owner, dead }));
        public Call[] Calls()
        {
            var path = Path.Combine(_directory, "calls.jsonl");
            return File.Exists(path) ? File.ReadAllLines(path).Select(line => JsonSerializer.Deserialize<Call>(line)!).ToArray() : [];
        }
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
