using System.Text.Json;
using System.Diagnostics;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Ssh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class SshFactAttribute : FactAttribute
{
    public SshFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("HVO_SSH_FIXTURES") != "1") Skip = "Run tests/Fixtures/start.sh and set HVO_SSH_FIXTURES=1 for real SSH integration fixtures.";
    }
}

public sealed class SshIntegrationTests
{
    internal static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }
    internal static RuntimeRecord Profile(string target, string name, int port)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, $".fixture/runtime-{target}.json")));
        var runtime = PersistenceTests.Profile();
        runtime.Host = doc.RootElement.GetProperty("host").GetString()!;
        runtime.HostKeySha256 = doc.RootElement.GetProperty("fingerprint").GetString()!;
        runtime.Name = name; runtime.ApiPort = port; runtime.Executable = "/usr/local/bin/fixture-opencode";
        runtime.StateDirectory = "/home/agent/state-" + runtime.ManagedServerId;
        return runtime;
    }
    internal static string FixtureSecrets => Path.Combine(Root, ".fixture/secrets");

    [SshFact]
    public async Task ForwardingBootstrapReuseChangedKeyAndCanonicalWorkspace()
    {
        var secrets = new Secrets(Options.Create(new ControlOptions { SecretsDirectory = FixtureSecrets }));
        var factory = new SshRuntimeTransportFactory(secrets);
        var runtime = Profile("a", "SSH ownership test", Random.Shared.Next(10000, 20000));
        runtime.StateDirectory = "/home/agent/state 'quoted' \"double\" $(touch forbidden);\n" + runtime.ManagedServerId;
        await using var first = await factory.Connect(runtime, CancellationToken.None);
        Assert.Equal("1.18.29", await first.Api.Verify(CancellationToken.None));
        var workspace = await first.Workspace(runtime, new(Guid.NewGuid().ToString(), runtime.Id, "worker", "project", "/home/agent/workspaces/a", "fixture", "deterministic"), CancellationToken.None);
        Assert.Equal("/home/agent/workspaces/a", workspace.Directory);
        await using var second = await factory.Connect(runtime, CancellationToken.None);
        Assert.Equal("1.18.29", await second.Api.Verify(CancellationToken.None));
        Assert.True(first.Connected);
        var prerequisite = await Docker("a", "env", "PATH=/nonexistent", "/bin/sh", runtime.StateDirectory + "/ensure.sh");
        Assert.NotEqual(0, prerequisite.ExitCode); Assert.Contains("PREREQUISITE:tmux", prerequisite.Output);
        var alias = "/home/agent/root-alias-" + Guid.NewGuid().ToString("N");
        Assert.Equal(0, (await Docker("a", "ln", "-s", "/home/agent/workspaces", alias)).ExitCode);
        var aliased = Profile("a", "symlink escape", runtime.ApiPort + 3);
        aliased.StateDirectory = alias + "/forbidden-state";
        await Assert.ThrowsAsync<ControlException>(() => factory.Connect(aliased, CancellationToken.None));
        var simultaneous = await Task.WhenAll(factory.Connect(runtime, CancellationToken.None), factory.Connect(runtime, CancellationToken.None));
        foreach (var connection in simultaneous) { Assert.Equal("1.18.29", await connection.Api.Verify(CancellationToken.None)); await connection.DisposeAsync(); }
        _ = await first.Api.Get("/fixture/no-models", CancellationToken.None);
        Assert.Empty(await first.Api.Models(workspace.Directory, CancellationToken.None));
        _ = await first.Api.Get("/fixture/models", CancellationToken.None);
        var worktreeDirectory = "/home/agent/workspaces/worktree 'quoted' $(touch forbidden);\n" + Guid.NewGuid().ToString("N");
        var worktree = await first.Workspace(runtime, new(Guid.NewGuid().ToString(), runtime.Id, "worktree", "project", worktreeDirectory,
            "fixture", "deterministic", "/home/agent/workspaces/b", "test-" + Guid.NewGuid().ToString("N"), "HEAD"), CancellationToken.None);
        Assert.Equal(worktreeDirectory, worktree.Directory);
        var conflict = Profile("a", "collision", runtime.ApiPort);
        var exception = await Assert.ThrowsAsync<ControlException>(() => factory.Connect(conflict, CancellationToken.None));
        Assert.Contains("PORT_CONFLICT", exception.Message);
        Assert.Equal("1.18.29", await first.Api.Verify(CancellationToken.None));
        var changed = Profile("a", "changed key", runtime.ApiPort + 1);
        changed.HostKeySha256 = "SHA256:" + new string('A', 43);
        await Assert.ThrowsAnyAsync<Exception>(() => factory.Connect(changed, CancellationToken.None));
        var unrelated = Profile("a", "unrelated tmux", runtime.ApiPort + 2);
        Assert.Equal(0, (await Docker("a", "tmux", "-L", unrelated.TmuxName, "new-session", "-d", "-s", "managed", "sleep", "60")).ExitCode);
        await Assert.ThrowsAsync<ControlException>(() => factory.Connect(unrelated, CancellationToken.None));
        Assert.Equal(0, (await Docker("a", "tmux", "-L", unrelated.TmuxName, "has-session", "-t", "managed")).ExitCode);
        _ = await Docker("a", "tmux", "-L", unrelated.TmuxName, "kill-session", "-t", "managed"); // only the unrelated fixture created above
        await Assert.ThrowsAsync<ControlException>(() => first.Workspace(runtime,
            new(Guid.NewGuid().ToString(), runtime.Id, "outside", "", "/tmp", "fixture", "deterministic"), CancellationToken.None));
        await first.StopOwnedServer(CancellationToken.None);
    }

    [SshFact]
    public async Task CanonicalWorkerDirectoriesDoNotBlockUnchangedRemoteRootAliases()
    {
        await using var app = new TestApp(secrets: FixtureSecrets);
        var factory = new SshRuntimeTransportFactory(new Secrets(Options.Create(new ControlOptions { SecretsDirectory = FixtureSecrets })));
        var dotted = Profile("a", "Dotted root profile", Random.Shared.Next(40000, 50000));
        dotted.StateDirectory = "/home/agent/issue-124-state-" + dotted.ManagedServerId;
        dotted.AllowedRoots = "/home/agent/workspaces/.";
        var link = "/home/agent/issue-124-link-" + Guid.NewGuid().ToString("N");
        IRuntimeTransport? connection = null;
        try
        {
            dotted = await app.Store.SaveRuntime(dotted);
            connection = await factory.Connect(dotted, CancellationToken.None);
            var dottedWorkspace = await connection.Workspace(dotted,
                new(Guid.NewGuid().ToString(), dotted.Id, "dotted", "fixture", "/home/agent/workspaces/./a", "fixture", "deterministic"), CancellationToken.None);
            Assert.Equal("/home/agent/workspaces/a", dottedWorkspace.Directory);
            await AddWorker(dotted, dottedWorkspace.Directory, "ses_issue124_dot");
            dotted.Name = "Dotted root renamed";
            dotted = await app.Store.SaveRuntime(dotted);
            Assert.Equal("Dotted root renamed", dotted.Name);

            Assert.Equal(0, (await Docker("a", "ln", "-s", "/home/agent/workspaces", link)).ExitCode);
            var symlink = Profile("a", "Symlink root profile", dotted.ApiPort + 1);
            symlink.StateDirectory = "/home/agent/issue-124-state-" + symlink.ManagedServerId;
            symlink.AllowedRoots = link;
            symlink = await app.Store.SaveRuntime(symlink);
            var linkedWorkspace = await connection.Workspace(symlink,
                new(Guid.NewGuid().ToString(), symlink.Id, "linked", "fixture", link + "/a", "fixture", "deterministic"), CancellationToken.None);
            Assert.Equal("/home/agent/workspaces/a", linkedWorkspace.Directory);
            await AddWorker(symlink, linkedWorkspace.Directory, "ses_issue124_link");
            symlink.Name = "Symlink root renamed";
            symlink = await app.Store.SaveRuntime(symlink);
            Assert.Equal("Symlink root renamed", symlink.Name);

            symlink.AllowedRoots = "/home/agent/workspaces/b";
            await Assert.ThrowsAsync<ControlException>(() => app.Store.SaveRuntime(symlink));
        }
        finally
        {
            if (connection is not null)
            {
                try { await connection.StopOwnedServer(CancellationToken.None); } catch (Exception) { }
                await connection.DisposeAsync();
            }
            var state = BootstrapScript.Quote(dotted.StateDirectory);
            var owner = BootstrapScript.Quote(dotted.ManagedServerId + ":" + dotted.ApiPort);
            var socket = BootstrapScript.Quote(dotted.TmuxName);
            var managedServer = BootstrapScript.Quote(dotted.ManagedServerId);
            var cleanup = await Docker("a", "sh", "-c", $$"""
                if test -f {{state}}/owner; then test "$(cat {{state}}/owner)" = {{owner}} || exit 1; fi
                if tmux -L {{socket}} has-session -t managed 2>/dev/null; then
                  marker=$(tmux -L {{socket}} show-option -v -t managed @hvo-owner 2>/dev/null || true)
                  if test -n "$marker"; then test "$marker" = {{managedServer}} || exit 1; fi
                  tmux -L {{socket}} kill-session -t managed || exit 1
                fi
                rm -rf -- {{BootstrapScript.Quote(link)}} {{state}}
                """);
            Assert.Equal(0, cleanup.ExitCode);
        }

        Task AddWorker(RuntimeRecord runtime, string directory, string nativeSessionId) => app.Store.Write(db =>
        {
            db.Workers.Add(new WorkerRecord
            {
                RuntimeId = runtime.Id,
                ManagedServerId = runtime.ManagedServerId,
                NativeSessionId = nativeSessionId,
                Directory = directory,
                Name = "Canonical worker"
            });
            return Task.FromResult(true);
        });
    }

    [SshFact]
    public async Task PreparedCheckoutVerificationRejectsChangedIdentityAndCanonicalAliases()
    {
        var secrets = new Secrets(Options.Create(new ControlOptions { SecretsDirectory = FixtureSecrets }));
        var factory = new SshRuntimeTransportFactory(secrets);
        var runtime = Profile("a", "Prepared checkout verification", Random.Shared.Next(10000, 20000));
        runtime.StateDirectory = "/home/agent/checkout-verification-state-" + runtime.ManagedServerId;
        var directory = "/home/agent/workspaces/checkout-verification-" + Guid.NewGuid().ToString("N");
        var alias = "/home/agent/checkout-verification-alias-" + Guid.NewGuid().ToString("N");
        var dependencySource = "/home/agent/workspaces/checkout-dependency-" + Guid.NewGuid().ToString("N");
        const string repository = "https://github.com/example/prepared.git";
        const string branch = "feature/prepared";
        IRuntimeTransport? connection = null;
        try
        {
            var setup = await Docker("a", "sh", "-c", $$"""
                set -eu
                mkdir -p -- {{BootstrapScript.Quote(directory)}}
                cd {{BootstrapScript.Quote(directory)}}
                git init -b {{BootstrapScript.Quote(branch)}} >/dev/null
                git config user.name fixture
                git config user.email fixture@example.invalid
                printf content > tracked.txt
                git add tracked.txt
                git commit -m initial >/dev/null
                git remote add origin {{BootstrapScript.Quote(repository)}}
                ln -s {{BootstrapScript.Quote(directory)}} {{BootstrapScript.Quote(alias)}}
                """);
            Assert.Equal(0, setup.ExitCode);
            var head = (await Docker("a", "git", "-C", directory, "rev-parse", "HEAD")).Output.Trim();
            connection = await factory.Connect(runtime, CancellationToken.None);
            VerifyPreparedCheckoutInput Input(string? path = null) => new(Guid.NewGuid().ToString("N"), runtime.Id, runtime.Revision,
                path ?? directory, repository, branch, head);

            Assert.Equal(PreparedCheckoutStatus.Verified, (await connection.VerifyPreparedCheckout(runtime, Input(), CancellationToken.None)).Status);
            Assert.Equal("origin_mismatch", (await connection.VerifyPreparedCheckout(runtime,
                Input() with { Repository = "https://github.com/example/wrong.git" }, CancellationToken.None)).Code);
            Assert.Equal("branch_mismatch", (await connection.VerifyPreparedCheckout(runtime,
                Input() with { Branch = "feature/wrong" }, CancellationToken.None)).Code);
            Assert.Equal("head_mismatch", (await connection.VerifyPreparedCheckout(runtime,
                Input() with { Head = new string('0', 40) }, CancellationToken.None)).Code);
            Assert.Equal("noncanonical_directory", (await connection.VerifyPreparedCheckout(runtime, Input(alias), CancellationToken.None)).Code);
            var allowedRoots = runtime.AllowedRoots;
            runtime.AllowedRoots = "/home/agent/workspaces/a";
            Assert.Equal("outside_allowed_root", (await connection.VerifyPreparedCheckout(runtime, Input(), CancellationToken.None)).Code);
            runtime.AllowedRoots = allowedRoots;
            Assert.Equal(0, (await Docker("a", "touch", directory + "/untracked.txt")).ExitCode);
            Assert.Equal("dirty_worktree", (await connection.VerifyPreparedCheckout(runtime, Input(), CancellationToken.None)).Code);
            Assert.Equal(0, (await Docker("a", "rm", "-f", directory + "/untracked.txt")).ExitCode);

            var marker = "/home/agent/configured-command-ran-" + Guid.NewGuid().ToString("N");
            var helper = "/home/agent/configured-command-" + Guid.NewGuid().ToString("N");
            Assert.Equal(0, (await Docker("a", "sh", "-c", $$"""
                set -eu
                cd {{BootstrapScript.Quote(directory)}}
                printf '#!/bin/sh\ntouch %s\n' {{BootstrapScript.Quote(marker)}} > {{BootstrapScript.Quote(helper)}}
                chmod 700 {{BootstrapScript.Quote(helper)}}
                printf 'tracked.txt filter=fixture\n' > .gitattributes
                git add .gitattributes
                git commit -m attributes >/dev/null
                git config core.fsmonitor {{BootstrapScript.Quote(helper)}}
                git config filter.fixture.clean {{BootstrapScript.Quote(helper)}}
                touch tracked.txt
                """)).ExitCode);
            head = (await Docker("a", "git", "-C", directory, "rev-parse", "HEAD")).Output.Trim();
            Assert.Equal("filter_configured", (await connection.VerifyPreparedCheckout(runtime, Input(), CancellationToken.None)).Code);
            Assert.Equal(0, (await Docker("a", "sh", "-c", "test ! -e " + BootstrapScript.Quote(marker))).ExitCode);
            Assert.Equal(0, (await Docker("a", "rm", "-f", helper)).ExitCode);

            Assert.Equal(0, (await Docker("a", "sh", "-c", $$"""
                set -eu
                cd {{BootstrapScript.Quote(directory)}}
                git config status.showUntrackedFiles no
                touch hidden-untracked.txt
                """)).ExitCode);
            Assert.Equal("dirty_worktree", (await connection.VerifyPreparedCheckout(runtime, Input(), CancellationToken.None)).Code);
            Assert.Equal(0, (await Docker("a", "sh", "-c", "cd " + BootstrapScript.Quote(directory) +
                " && rm -f hidden-untracked.txt && git config --unset status.showUntrackedFiles")).ExitCode);

            Assert.Equal(0, (await Docker("a", "sh", "-c", "cd " + BootstrapScript.Quote(directory) +
                " && printf changed >> tracked.txt && git update-index --assume-unchanged tracked.txt")).ExitCode);
            Assert.Equal("index_flags", (await connection.VerifyPreparedCheckout(runtime, Input(), CancellationToken.None)).Code);
            Assert.Equal(0, (await Docker("a", "sh", "-c", "cd " + BootstrapScript.Quote(directory) +
                " && git update-index --no-assume-unchanged tracked.txt && git checkout -- tracked.txt")).ExitCode);
            Assert.Equal(0, (await Docker("a", "sh", "-c", "cd " + BootstrapScript.Quote(directory) +
                " && printf changed >> tracked.txt && git update-index --skip-worktree tracked.txt")).ExitCode);
            Assert.Equal("index_flags", (await connection.VerifyPreparedCheckout(runtime, Input(), CancellationToken.None)).Code);
            Assert.Equal(0, (await Docker("a", "sh", "-c", "cd " + BootstrapScript.Quote(directory) +
                " && git update-index --no-skip-worktree tracked.txt && git checkout -- tracked.txt")).ExitCode);

            Assert.Equal(0, (await Docker("a", "sh", "-c", $$"""
                set -eu
                git init -b main {{BootstrapScript.Quote(dependencySource)}} >/dev/null
                cd {{BootstrapScript.Quote(dependencySource)}}
                git config user.name fixture
                git config user.email fixture@example.invalid
                printf dependency > dependency.txt
                git add dependency.txt
                git commit -m initial >/dev/null
                cd {{BootstrapScript.Quote(directory)}}
                git -c protocol.file.allow=always submodule add {{BootstrapScript.Quote(dependencySource)}} dependency >/dev/null
                git commit -m dependency >/dev/null
                printf dirty >> dependency/dependency.txt
                git config submodule.dependency.ignore all
                """)).ExitCode);
            head = (await Docker("a", "git", "-C", directory, "rev-parse", "HEAD")).Output.Trim();
            Assert.Equal("dirty_worktree", (await connection.VerifyPreparedCheckout(runtime, Input(), CancellationToken.None)).Code);
        }
        finally
        {
            if (connection is not null)
            {
                try { await connection.StopOwnedServer(CancellationToken.None); } catch (Exception) { }
                await connection.DisposeAsync();
            }
            var cleanup = await Docker("a", "sh", "-c", "rm -rf -- " + BootstrapScript.Quote(directory) + " " +
                BootstrapScript.Quote(alias) + " " + BootstrapScript.Quote(dependencySource) + " " + BootstrapScript.Quote(runtime.StateDirectory));
            Assert.Equal(0, cleanup.ExitCode);
        }
    }

    private static async Task<(int ExitCode, string Output)> Docker(string target, params string[] args)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "exec", "-u", "agent", "hvo-agentcontrol-fixture-" + target }.Concat(args)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }

    [SshFact]
    public async Task BackendStopDuringReadOnlyPreflightKeepsPromptForOneSubmission()
    {
        var data = Path.Combine(Path.GetTempPath(), "hvo-preflight-" + Guid.NewGuid().ToString("N"));
        var app = new TestApp(data, FixtureSecrets);
        var runtime = Profile("a", "Preflight restart", Random.Shared.Next(10001, 20000));
        try
        {
            var store = app.Store;
            runtime = await store.SaveRuntime(runtime);
            await store.RuntimeCommand(runtime.Id, "EnsureServer", Guid.NewGuid().ToString());
            await TestApp.Wait(async () => (await store.Snapshot()).Runtimes.Single().Health == "Healthy", "Preflight runtime failed to connect", 60);
            var worker = await Create(store, runtime, "preflight", "/home/agent/workspaces/a");
            await TestApp.Wait(async () => !(await store.Detail(worker.Id)).Worker.Stale, "Worker did not reconcile");
            var factory = app.Services.GetRequiredService<IRuntimeTransportFactory>();
            await using var probe = await factory.Connect(runtime, CancellationToken.None);
            var before = (await probe.Api.Get("/fixture/stats", CancellationToken.None)).GetProperty("submissions").GetInt32();
            _ = await probe.Api.Get("/fixture/hold-provider", CancellationToken.None);
            var command = await Prompt(store, worker, "Survive cancellation before any native submission.");
            await TestApp.Wait(async () => (await probe.Api.Get("/fixture/stats", CancellationToken.None)).GetProperty("providerWaiting").GetBoolean(),
                "Prompt did not reach the deterministic read-only preflight barrier");
            var blocked = (await store.Detail(worker.Id)).Commands.Single(x => x.Id == command.Id);
            Assert.Equal(Delivery.Dispatching, blocked.State);
            Assert.Null(blocked.NativeMessageId);
            await app.DisposeAsync();
            _ = await probe.Api.Get("/fixture/release-provider", CancellationToken.None);
            app = new TestApp(data, FixtureSecrets); store = app.Store;
            await Finished(store, command);
            var completed = (await store.Detail(worker.Id)).Commands.Single(x => x.Id == command.Id);
            Assert.Equal(1, completed.Attempts);
            Assert.NotNull(completed.NativeMessageId);
            Assert.Equal(before + 1, (await probe.Api.Get("/fixture/stats", CancellationToken.None)).GetProperty("submissions").GetInt32());
            Assert.Equal(worker.NativeSessionId, (await store.Detail(worker.Id)).Worker.NativeSessionId);
        }
        finally
        {
            var factory = app.Services.GetRequiredService<IRuntimeTransportFactory>();
            try { await using var connection = await factory.Connect(runtime, CancellationToken.None); await connection.StopOwnedServer(CancellationToken.None); } catch (Exception) { }
            await app.DisposeAsync();
        }
    }

    [SshFact]
    public async Task TwoRuntimesConversationQueueQuestionsAbortAndRestartRecoverWithoutReplay()
    {
        var data = Path.Combine(Path.GetTempPath(), "hvo-ssh-lifecycle-" + Guid.NewGuid().ToString("N"));
        var app = new TestApp(data, FixtureSecrets);
        var a = Profile("a", "Lifecycle A", Random.Shared.Next(20001, 30000));
        var b = Profile("b", "Lifecycle B", Random.Shared.Next(30001, 40000));
        try
        {
            var store = app.Store;
            a = await store.SaveRuntime(a); b = await store.SaveRuntime(b);
            var ensures = await Task.WhenAll(store.RuntimeCommand(a.Id, "EnsureServer", Guid.NewGuid().ToString()), store.RuntimeCommand(a.Id, "EnsureServer", Guid.NewGuid().ToString()), store.RuntimeCommand(b.Id, "EnsureServer", Guid.NewGuid().ToString()));
            await TestApp.Wait(async () => (await store.Snapshot()).Runtimes.All(x => x.Health == "Healthy"), "Runtimes failed to connect: " + data, 60);
            var w1 = await Create(store, a, "one", "/home/agent/workspaces/a");
            var w2 = await Create(store, a, "two", "/home/agent/workspaces/b");
            var w3 = await Create(store, b, "three", "/home/agent/workspaces/a");
            await TestApp.Wait(async () => (await store.Snapshot()).Workers.All(x => !x.Stale), "Workers did not reconcile");
            var c1 = await Prompt(store, w1, "first [hold] 'quotes' $(touch should-not-exist)\nnext");
            var c2 = await Prompt(store, w2, "parallel second runtime-a [hold]");
            var c3 = await Prompt(store, w3, "independent runtime-b");
            await TestApp.Wait(async () => (await store.Snapshot()).Workers.Count(x => x.Activity == "Active") >= 2, "Independent sessions failed to run concurrently");
            var queued = await Prompt(store, w1, "follow-up in same conversation");
            Assert.Equal(Delivery.Queued, queued.State);
            var same = Json.Read<PromptInput>(queued.Payload);
            Assert.Equal(queued.Id, (await store.Prompt(w1.Id, same)).Id);
            await store.RuntimeCommand(a.Id, "DisconnectRuntime", Guid.NewGuid().ToString());
            await TestApp.Wait(async () => (await store.Snapshot()).Runtimes.Single(x => x.Id == a.Id).Transport == "Disconnected", "Disconnect failed");
            await Finished(store, c3);
            await store.RuntimeCommand(a.Id, "EnsureServer", Guid.NewGuid().ToString());
            await Finished(store, c1); await Finished(store, queued); await Finished(store, c2);
            var history = await store.Detail(w1.Id);
            Assert.Equal(4, history.Messages.Count);
            Assert.DoesNotContain(history.Messages, x => x.Json.Contains("parallel second", StringComparison.Ordinal));
            Assert.Contains(history.Messages, x => x.Json.Contains("café", StringComparison.Ordinal));
            Assert.Equal("NeedsReview", history.Worker.Outcome);
            Assert.True(history.Worker.HistoryGap);

            var factory = app.Services.GetRequiredService<IRuntimeTransportFactory>();
            await using (var probe = await factory.Connect(a, CancellationToken.None))
            {
                var generation = (await store.Snapshot()).Runtimes.Single(x => x.Id == a.Id).Generation;
                _ = await probe.Api.Get("/fixture/drop-sse", CancellationToken.None);
                await Task.Delay(1200);
                Assert.Equal("Healthy", (await store.Snapshot()).Runtimes.Single(x => x.Id == a.Id).Health);
                Assert.Equal(generation, (await store.Snapshot()).Runtimes.Single(x => x.Id == a.Id).Generation);
            }

            var lost = await Prompt(store, w1, "accepted but response lost [drop-response]");
            await Finished(store, lost);
            var unknown = await Prompt(store, w1, "unresolved [unknown]");
            await TestApp.Wait(async () => (await store.Detail(w1.Id)).Commands.Single(x => x.Id == unknown.Id).State == Delivery.Unknown, "Ambiguous delivery was not retained");
            var blocked = await Prompt(store, w1, "must remain queued behind uncertainty");
            await Task.Delay(700);
            Assert.Equal(Delivery.Queued, (await store.Detail(w1.Id)).Commands.Single(x => x.Id == blocked.Id).State);
            await store.EditQueue(blocked.Id, "cancel");
            await store.EditQueue(unknown.Id, "resolveUnknown");

            var questionCommand = await Prompt(store, w1, "ask for a decision [question]");
            await TestApp.Wait(async () => (await store.Snapshot()).Requests.Any(x => x.WorkerId == w1.Id && x.Kind == "question"), "Question missing");
            var question = (await store.Snapshot()).Requests.Single(x => x.WorkerId == w1.Id && x.Kind == "question");
            var pending = await Prompt(store, w2, "survive backend restart [hold]");
            var afterRestart = await Prompt(store, w2, "queued across backend restart");
            await app.DisposeAsync();
            app = new TestApp(data, FixtureSecrets); store = app.Store;
            await TestApp.Wait(async () => (await store.Snapshot()).Workers.All(x => !x.Stale), "Backend recovery did not reconcile", 60);
            Assert.Contains((await store.Snapshot()).Requests, x => x.Id == question.Id);
            var answer = new ReplyInput(Guid.NewGuid().ToString(), question.Id, null, [["One"]]);
            Assert.Equal((await store.Reply(answer)).Id, (await store.Reply(answer)).Id);
            await Finished(store, questionCommand); await Finished(store, pending); await Finished(store, afterRestart);
            Assert.Equal(w1.NativeSessionId, (await store.Detail(w1.Id)).Worker.NativeSessionId);

            var permissionCommand = await Prompt(store, w1, "request native approval [permission]");
            await TestApp.Wait(async () => (await store.Snapshot()).Requests.Any(x => x.WorkerId == w1.Id && x.Kind == "permission"), "Permission missing");
            var permission = (await store.Snapshot()).Requests.Single(x => x.WorkerId == w1.Id && x.Kind == "permission");
            await store.Reply(new(Guid.NewGuid().ToString(), permission.Id, "once", null));
            await Finished(store, permissionCommand);
            var abortable = await Prompt(store, w1, "abort this turn [hold]");
            var unaffected = await Prompt(store, w2, "unrelated worker survives abort [hold]");
            await TestApp.Wait(async () => (await store.Detail(w1.Id)).Worker.Activity == "Active" &&
                (await store.Detail(w2.Id)).Worker.Activity == "Active", "Both workers must be active before testing isolated cancellation");
            var abort = await store.Abort(w1.Id, Guid.NewGuid().ToString());
            await Finished(store, abort);
            await TestApp.Wait(async () => (await store.Snapshot()).Commands.Single(x => x.Id == abortable.Id).State == Delivery.Cancelled,
                "Interrupted prompt did not settle as cancelled");
            var abortCommands = (await store.Snapshot()).Commands;
            Assert.Equal(Delivery.Finished, abortCommands.Single(x => x.Id == abort.Id).State);
            Assert.Equal(Delivery.Cancelled, abortCommands.Single(x => x.Id == abortable.Id).State);
            Assert.NotEqual("Idle", (await store.Detail(w2.Id)).Worker.Activity);
            await Finished(store, unaffected);
            Assert.Equal("Cancelled", (await store.Detail(w1.Id)).Worker.Outcome);
            var failed = await Prompt(store, w1, "native error is not success [error]");
            await Finished(store, failed);
            Assert.Equal("Failed", (await store.Detail(w1.Id)).Worker.Outcome);
            var failedDetail = await store.Detail(w1.Id);
            Assert.Equal(Delivery.Finished, failedDetail.Commands.Single(x => x.Id == failed.Id).State);
            Assert.Equal("Failed", failedDetail.Assignments.Single(x => x.Id == failed.Id).Outcome);
            var snapshot = await store.Snapshot();
            Assert.All(snapshot.Commands.Where(x => x.Kind == "Prompt" && x.State != Delivery.Cancelled), x => Assert.Equal(1, x.Attempts));
            Assert.Equal(3, snapshot.Workers.Count);
            using var client = await app.SignIn();
            var html = await client.GetStringAsync("/");
            Assert.Contains("Overview", html);
            Assert.Contains("Lifecycle A", html);
        }
        finally
        {
            // Explicit cleanup is limited to owned fixture processes; normal backend disposal never stops them.
            var factory = app.Services.GetRequiredService<IRuntimeTransportFactory>();
            foreach (var runtime in new[] { a, b })
                try { await using var connection = await factory.Connect(runtime, CancellationToken.None); await connection.StopOwnedServer(CancellationToken.None); } catch (Exception) { }
            await app.DisposeAsync();
        }
    }

    internal static async Task<WorkerRecord> Create(ControlStore store, RuntimeRecord runtime, string name, string directory)
    {
        var command = await store.CreateWorker(new(Guid.NewGuid().ToString(), runtime.Id, name, "fixture-project", directory, "fixture", "deterministic"));
        await Finished(store, command);
        return (await store.Snapshot()).Workers.Single(x => x.Name == name);
    }
    internal static async Task<CommandRecord> Prompt(ControlStore store, WorkerRecord worker, string text)
    {
        var current = await store.Detail(worker.Id);
        return await store.Prompt(worker.Id, new(Guid.NewGuid().ToString(), text, current.Worker.Revision));
    }
    internal static Task Finished(ControlStore store, CommandRecord command) => TestApp.Wait(async () =>
    {
        var current = (await store.Snapshot()).Commands.Single(x => x.Id == command.Id);
        if (current.State == Delivery.Failed) throw new InvalidOperationException(current.Kind + ": " + current.Detail);
        return current.State == Delivery.Finished;
    }, "Command did not finish: " + command.Kind + " " + command.Id, 60);
}
