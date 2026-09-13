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
