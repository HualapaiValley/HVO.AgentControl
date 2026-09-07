using System.Text.Json;
using System.Diagnostics;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Ssh;
using Microsoft.Extensions.Options;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class NativeFactAttribute : FactAttribute
{
    public NativeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("HVO_NATIVE_FIXTURE") != "1") Skip = "Set HVO_NATIVE_FIXTURE=1 after starting SSH fixtures; downloads and installs the actual pinned OpenCode release.";
    }
}

public sealed class NativeReleaseTests
{
    [NativeFact]
    public async Task RealProviderTwoSshTargetsAndBackendRestartPreserveConversation()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var directories = new[] { "/home/agent/workspaces/live-a-" + suffix, "/home/agent/workspaces/live-b-" + suffix, "/home/agent/workspaces/live-c-" + suffix };
        for (var i = 0; i < directories.Length; i++)
        {
            var target = i == 2 ? "b" : "a";
            await Docker(target, "mkdir", "-p", directories[i]);
            await Docker(target, "git", "-C", directories[i], "init", "-q");
        }
        var data = Path.Combine(Path.GetTempPath(), "hvo-native-control-" + suffix);
        var app = new TestApp(data, SshIntegrationTests.FixtureSecrets);
        var a = SshIntegrationTests.Profile("a", "Live OpenCode A", 5096);
        var b = SshIntegrationTests.Profile("b", "Live OpenCode B", 5096);
        a.Executable = ""; b.Executable = "/home/agent/native-release-evidence/bin/opencode";
        // If this test runs before the explicit install test, install a separate copy on B.
        b.Executable = "";
        var evidence = new List<object>();
        try
        {
            var store = app.Store;
            a = await store.SaveRuntime(a); b = await store.SaveRuntime(b);
            await store.RuntimeCommand(a.Id, "EnsureServer", Guid.NewGuid().ToString());
            await store.RuntimeCommand(b.Id, "EnsureServer", Guid.NewGuid().ToString());
            await TestApp.Wait(async () => (await store.Snapshot()).Runtimes.All(x => x.Health == "Healthy"), "Real OpenCode runtimes did not become healthy", 240);
            var workers = new List<WorkerRecord>();
            for (var i = 0; i < directories.Length; i++)
            {
                var runtime = i == 2 ? b : a;
                var create = await store.CreateWorker(new(Guid.NewGuid().ToString(), runtime.Id, "Live worker " + i, "native-validation", directories[i], "opencode", "big-pickle"));
                await SshIntegrationTests.Finished(store, create);
                workers.Add((await store.Snapshot()).Workers.Single(x => x.Name == "Live worker " + i));
            }
            await TestApp.Wait(async () => (await store.Snapshot()).Workers.All(x => !x.Stale), "Native workers did not reconcile");
            var commands = new List<CommandRecord>();
            for (var i = 0; i < workers.Count; i++)
                commands.Add(await SshIntegrationTests.Prompt(store, workers[i],
                    $"In this disposable workspace run exactly this bash command: `sleep {(i == 0 ? 12 : 2)}; printf 'NATIVE_{i}_{suffix}\\n' > evidence.txt; cat evidence.txt`. Then report the output and stop. Do not commit, access other directories, or create other files."));
            await TestApp.Wait(async () => (await store.Detail(workers[0].Id)).Messages.Any(x => x.Json.Contains("\"running\"", StringComparison.Ordinal) && x.Json.Contains("sleep", StringComparison.Ordinal)), "Real provider did not start the requested tool", 120);
            await store.RuntimeCommand(a.Id, "DisconnectRuntime", Guid.NewGuid().ToString());
            await TestApp.Wait(async () => (await store.Snapshot()).Runtimes.Single(x => x.Id == a.Id).Transport == "Disconnected", "Native SSH disconnect not observed");
            await store.RuntimeCommand(a.Id, "EnsureServer", Guid.NewGuid().ToString());
            await app.DisposeAsync();
            app = new TestApp(data, SshIntegrationTests.FixtureSecrets); store = app.Store;
            foreach (var command in commands) await SshIntegrationTests.Finished(store, command);
            var follow = await SshIntegrationTests.Prompt(store, workers[0], "In the same workspace append exactly FOLLOWUP to evidence.txt on a new line. Run cat evidence.txt and report both lines. Stop without committing.");
            await SshIntegrationTests.Finished(store, follow);
            for (var i = 0; i < workers.Count; i++)
            {
                var file = await Docker(i == 2 ? "b" : "a", "cat", directories[i] + "/evidence.txt");
                Assert.Contains($"NATIVE_{i}_{suffix}", file);
                if (i == 0) Assert.Contains("FOLLOWUP", file);
                var detail = await store.Detail(workers[i].Id);
                Assert.Equal(workers[i].NativeSessionId, detail.Worker.NativeSessionId);
                Assert.All(detail.Commands.Where(x => x.Kind == "Prompt"), x => Assert.Equal(1, x.Attempts));
                evidence.Add(new { target = i == 2 ? "b" : "a", workspace = directories[i], nativeSessionId = detail.Worker.NativeSessionId, file, messages = detail.Messages.Select(x => JsonDocument.Parse(x.Json).RootElement.Clone()).ToArray() });
            }
            File.WriteAllText(Path.Combine(SshIntegrationTests.Root, ".fixture/native-live-evidence.json"), Json.Write(new { version = "1.18.29", provider = "opencode", model = "big-pickle", sshDropDuringTool = true, backendRestart = true, evidence }));
        }
        finally
        {
            var factory = new SshRuntimeTransportFactory(new Secrets(Options.Create(new ControlOptions { SecretsDirectory = SshIntegrationTests.FixtureSecrets })));
            foreach (var runtime in new[] { a, b })
                try { await using var connection = await factory.Connect(runtime, CancellationToken.None); await connection.StopOwnedServer(CancellationToken.None); } catch (Exception) { }
            await app.DisposeAsync();
        }
    }

    private static async Task<string> Docker(string target, params string[] args)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "exec", "-u", "agent", "hvo-agentcontrol-fixture-" + target }.Concat(args)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("Fixture Docker operation failed: " + await stderr);
        return await stdout;
    }

    [NativeFact]
    public async Task InstallMissingReleaseInspectSchemaAndReuseOwnedNativeServer()
    {
        var factory = new SshRuntimeTransportFactory(new Secrets(Options.Create(new ControlOptions { SecretsDirectory = SshIntegrationTests.FixtureSecrets })));
        var runtime = SshIntegrationTests.Profile("b", "Native release evidence", 9496);
        runtime.Executable = ""; runtime.InstallIfMissing = true;
        runtime.Id = "11111111111111111111111111111111";
        runtime.ManagedServerId = "22222222222222222222222222222222";
        runtime.StateDirectory = "/home/agent/native-release-evidence";
        File.WriteAllText(Path.Combine(SshIntegrationTests.Root, ".fixture/native-runtime.json"), Json.Write(runtime));
        await using var first = await factory.Connect(runtime, CancellationToken.None);
        Assert.Equal("1.18.29", await first.Api.Verify(CancellationToken.None));
        var sessionA = await first.Api.CreateSession("/home/agent/workspaces/a", "Native contract A", CancellationToken.None);
        var sessionB = await first.Api.CreateSession("/home/agent/workspaces/b", "Native contract B", CancellationToken.None);
        Assert.NotEqual(sessionA.GetProperty("id").GetString(), sessionB.GetProperty("id").GetString());
        Assert.Equal("/home/agent/workspaces/a", sessionA.GetProperty("directory").GetString());
        Assert.Equal("/home/agent/workspaces/b", sessionB.GetProperty("directory").GetString());
        await using var second = await factory.Connect(runtime, CancellationToken.None);
        Assert.Equal("1.18.29", await second.Api.Verify(CancellationToken.None));
        runtime.PureMode = true; runtime.PrintLogs = true; runtime.LogLevel = "WARN";
        var requiresStop = await Assert.ThrowsAsync<ControlException>(() => factory.Connect(runtime, CancellationToken.None));
        Assert.Contains("STARTUP_OPTIONS_CHANGED_STOP_REQUIRED", requiresStop.Message);
        Assert.Equal("1.18.29", await second.Api.Verify(CancellationToken.None));
        await second.StopOwnedServer(CancellationToken.None);
        await using var restarted = await factory.Connect(runtime, CancellationToken.None);
        Assert.Equal("1.18.29", await restarted.Api.Verify(CancellationToken.None));
        var restored = await restarted.Api.Get("/session/" + sessionA.GetProperty("id").GetString() + "?directory=%2Fhome%2Fagent%2Fworkspaces%2Fa", CancellationToken.None);
        Assert.Equal(sessionA.GetProperty("id").GetString(), restored.GetProperty("id").GetString());
        var arguments = await Docker("b", "sh", "-c", "tr '\\0' ' ' < /proc/$(tmux -L " + runtime.TmuxName + " display-message -p -t managed '#{pane_pid}')/cmdline");
        Assert.Contains("--pure --print-logs --log-level WARN", arguments);
        File.WriteAllText(Path.Combine(SshIntegrationTests.Root, ".fixture/native-sessions.json"), JsonSerializer.Serialize(new { sessionA, sessionB }));
        // Return the owned fixture to default options for focused native/provider probes.
        await restarted.StopOwnedServer(CancellationToken.None);
        runtime.PureMode = false; runtime.PrintLogs = false; runtime.LogLevel = "";
        await using var defaults = await factory.Connect(runtime, CancellationToken.None);
        // Disposal closes forwarding only; the tmux process remains alive by design.
    }
}
