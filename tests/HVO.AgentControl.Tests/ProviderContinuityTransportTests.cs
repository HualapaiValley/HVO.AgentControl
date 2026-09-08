using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.OpenCode;
using HVO.AgentControl.Services;
using HVO.AgentControl.Ssh;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class ProviderContinuityTransportTests
{
    [LinuxFact]
    public async Task SshLiveScriptObservesKernelProcessInsteadOfBootstrapReceipt()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("LinuxFact must skip this kernel fixture.");
        var root = Path.Combine(Path.GetTempPath(), "hvo-live-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var first = Process.Start("/bin/sleep", "30")!;
        using var second = Process.Start("/bin/sleep", "30")!;
        try
        {
            var runtime = new RuntimeRecord { ManagedServerId = "managed", StateDirectory = root, ApiPort = 4096 };
            File.WriteAllText(Path.Combine(root, "owner"), "managed:4096");
            File.WriteAllText(Path.Combine(root, "native-process-current"), "Observed\tLinux\t999999\tstale-bootstrap\n");
            var pane = Path.Combine(root, "pane");
            File.WriteAllText(pane, first.Id + " 0");
            var tmux = Path.Combine(root, "tmux");
            File.WriteAllText(tmux, "#!/bin/sh\ncase \"$*\" in *show-option*) printf managed;; *) cat " + BootstrapScript.Quote(pane) + ";; esac\n");
            File.SetUnixFileMode(tmux, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            async Task<(int ExitCode, string Output)> Run()
            {
                var start = new ProcessStartInfo("/bin/sh") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.Environment["PATH"] = root + ":/usr/bin:/bin";
                using var process = Process.Start(start)!;
                await process.StandardInput.WriteAsync(NativeProcessProbe.LiveScript(runtime));
                process.StandardInput.Close();
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                return (process.ExitCode, output);
            }
            var observed = await Run();
            Assert.Equal(0, observed.ExitCode);
            var firstIdentity = NativeProcessProbe.Parse("managed", observed.Output, 1);
            Assert.Equal(NativeProcessObservationState.Observed, firstIdentity.State);
            Assert.Equal(first.Id, firstIdentity.ProcessId);
            Assert.NotEqual("stale-bootstrap", firstIdentity.Incarnation);
            File.WriteAllText(pane, second.Id + " 0");
            var replacement = NativeProcessProbe.Parse("managed", (await Run()).Output, 2);
            Assert.Equal(second.Id, replacement.ProcessId);
            second.Kill(); await second.WaitForExitAsync();
            Assert.NotEqual(0, (await Run()).ExitCode);
            File.WriteAllText(Path.Combine(root, "owner"), "foreign:4096");
            Assert.NotEqual(0, (await Run()).ExitCode);
        }
        finally
        {
            if (!first.HasExited) { first.Kill(); await first.WaitForExitAsync(); }
            if (!second.HasExited) { second.Kill(); await second.WaitForExitAsync(); }
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DirectHttpProbeRereadsBoundIncarnationWithoutInventingPid()
    {
        await using var app = new TestApp();
        var service = new ControlServiceRecord { Id = "service", InstanceId = Guid.NewGuid().ToString(), Endpoint = "http://fixture" };
        var handler = new IdentityHandler(service.InstanceId);
        await using var transport = new ControlHttpTransport(new(new HttpClient(handler) { BaseAddress = new("http://fixture") }), service, app.Store);
        var before = (await transport.ProbeProcessIdentity(CancellationToken.None))!;
        handler.Incarnation = Guid.NewGuid().ToString();
        var after = (await transport.ProbeProcessIdentity(CancellationToken.None))!;
        Assert.Equal(2, handler.Reads);
        Assert.Equal(RuntimeConnections.ControlHttp, after.ConnectionKind);
        Assert.Equal(service.InstanceId, after.OwnerId);
        Assert.Null(after.ProcessId);
        Assert.False(before.SameProcess(after));
        handler.InstanceId = Guid.NewGuid().ToString();
        await Assert.ThrowsAsync<ControlException>(() => transport.ProbeProcessIdentity(CancellationToken.None));
    }

    [Fact]
    public async Task MigrationKeepsLegacyUnknownAndRefreshingInstancesUnattributed()
    {
        var path = Path.Combine(Path.GetTempPath(), "hvo-legacy-disposal-" + Guid.NewGuid().ToString("N") + ".db");
        var options = new DbContextOptionsBuilder<ControlDb>().UseSqlite("Data Source=" + path).Options;
        await using var db = new ControlDb(options);
        await db.GetService<IMigrator>().MigrateAsync("20260908121625_ProviderReadinessReceipts");
        foreach (var state in new[] { "Unknown", "Refreshing", "RefreshCompleted", "RefreshRequired" })
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ProviderReadinessReceipt (Id, RuntimeId, ProviderId, KeyRevision, State, Detail, UpdatedAt) VALUES ({state}, {"r"}, {"instance"}, {1}, {state}, {"legacy"}, {1});");
        await db.Database.MigrateAsync();
        var rows = await db.Set<ProviderReadinessReceipt>().ToListAsync();
        Assert.All(rows.Where(x => x.State is "Unknown" or "Refreshing"), row => Assert.Equal(ProviderKeyService.LegacyPendingDisposal, row.PendingDisposalJson));
        Assert.All(rows.Where(x => x.State is "RefreshRequired" or "RefreshCompleted"), row => Assert.Empty(row.PendingDisposalJson));
    }

    private sealed class IdentityHandler(string instanceId) : HttpMessageHandler
    {
        public string InstanceId = instanceId, Incarnation = Guid.NewGuid().ToString();
        public int Reads;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("/file/content", request.RequestUri!.AbsolutePath);
            Reads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { type = "text", content = Json.Write(new ControlServiceIdentity(1, InstanceId, Incarnation, DateTimeOffset.UtcNow.ToString("O"), ControlStore.ControlDirectory)) })
            });
        }
    }

    private sealed class LinuxFactAttribute : FactAttribute
    {
        public LinuxFactAttribute() { if (!OperatingSystem.IsLinux()) Skip = "The live SSH process probe requires Linux kernel process evidence."; }
    }
}
