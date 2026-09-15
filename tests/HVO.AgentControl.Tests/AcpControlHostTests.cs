using HVO.AgentControl.Organization;
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
        // The shared, checked-in fake tmux fixture: a per-test symlink to a
        // never-rewritten canonical file, because writing an executable while
        // other tests start processes races into ETXTBSY (#232/#233).
        using var fake = new FakeTmux();
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
        Assert.Equal("busy", status.SessionState);

        // The abandoned turn is reconciled, not left running, while the busy
        // fence remains until transport closure or eventual response.
        var callsPath = Path.Combine(data, "home", "calls.log");
        await WaitForFileContainsAsync(callsPath, "session/cancel", TimeSpan.FromSeconds(15));

        // The runtime stays recoverable after the timeout.
        Assert.True(host.GetStatus().CanControl);
        Assert.True(await host.CancelAsync(CancellationToken.None));

        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task BootstrapTimeoutRetainsBusyFenceUntilIgnoredCancelPromptCompletes()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-bootstrap-abandoned-").FullName;
        var options = ReadyOptions(data, scenario: "bootstrap_ignores_cancel");
        options.PromptTimeoutSeconds = 1;
        using var host = CreateHost(options);

        await host.StartAsync(CancellationToken.None);
        var degraded = await WaitForStateAsync(host, "degraded", TimeSpan.FromSeconds(30));
        Assert.Equal("busy", degraded.SessionState);
        await WaitForFileContainsAsync(Path.Combine(data, "home", "calls.log"), "session/cancel", TimeSpan.FromSeconds(15));

        await Assert.ThrowsAsync<OrganizationConcurrencyException>(() =>
            host.RunOrientationComprehensionAsync(CancellationToken.None));
        Assert.Equal("busy", host.GetStatus().SessionState);

        File.WriteAllText(Path.Combine(data, "home", "bootstrap-release"), "release");
        await WaitForAsync(
            host,
            status => !string.Equals(status.SessionState, "busy", StringComparison.Ordinal),
            "the abandoned bootstrap response to release the prompt fence",
            TimeSpan.FromSeconds(30));
        Assert.Equal("degraded", host.GetStatus().State);
        await host.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// The pinned fake's permission request arrives while the session is not the
    /// evaluated one. The handler must return a protocol-level reject option
    /// rather than a JSON-RPC internal error, and must persist no allow.
    /// </summary>
    [Fact]
    public async Task InboundPermissionRequestReturnsRejectOptionNeverInternalError()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-permission-").FullName;
        using var host = CreateHost(ReadyOptions(data, scenario: "happy"));

        await host.StartAsync(CancellationToken.None);
        await WaitForStateAsync(host, "ready", TimeSpan.FromSeconds(30));

        var handler = (Func<AcpEnvelope, CancellationToken, Task<AcpResponse>>)typeof(AcpControlHost)
            .GetMethod("HandleIncomingRequestAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .CreateDelegate(typeof(Func<AcpEnvelope, CancellationToken, Task<AcpResponse>>), host);

        var frame = System.Text.Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":9001,"method":"session/request_permission","params":{"sessionId":"ses_not_current","toolCall":{"toolCallId":"tc-1","title":"diagnostic:public","kind":"read","status":"pending"},"options":[{"optionId":"allow_once","name":"Allow once","kind":"allow_once"},{"optionId":"reject_once","name":"Reject once","kind":"reject_once"}]}}""");
        var response = await handler(AcpEnvelope.Parse(frame.AsSpan()), CancellationToken.None);

        Assert.Null(response.ErrorCode);
        Assert.Null(response.ErrorMessage);
        var result = Assert.IsType<Dictionary<string, object?>>(response.Result);
        var outcome = Assert.IsType<Dictionary<string, object?>>(result["outcome"]);
        Assert.Equal("selected", outcome["outcome"]);
        Assert.Equal("reject_once", outcome["optionId"]);

        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OrientationComprehensionLinkedDeadlinePersistsTimedOutAndFailedHold()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-orientation-hang-").FullName;
        var options = ReadyOptions(data, scenario: "orientation_hang");
        options.PromptTimeoutSeconds = 1;
        using var host = CreateHost(options);

        await host.StartAsync(CancellationToken.None);
        await WaitForStateAsync(host, "ready", TimeSpan.FromSeconds(30));
        var result = await host.RunOrientationComprehensionAsync(CancellationToken.None);

        Assert.Equal(OrientationStates.TimedOut, result.State);
        Assert.Contains(DispatchHoldReasons.OrientationFailed, result.HoldReasons);
        Assert.Contains("timed out", result.LastError!, StringComparison.OrdinalIgnoreCase);
        await host.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("orientation_malformed")]
    [InlineData("orientation_fenced")]
    [InlineData("orientation_empty")]
    [InlineData("orientation_oversized")]
    [InlineData("orientation_non_end")]
    [InlineData("orientation_wrong_session")]
    [InlineData("orientation_wrong_assignment")]
    [InlineData("orientation_wrong_employee")]
    [InlineData("orientation_wrong_version")]
    [InlineData("orientation_null_fields")]
    [InlineData("orientation_empty_object")]
    [InlineData("orientation_bad_facts")]
    [InlineData("orientation_bad_result_shape")]
    public async Task MalformedLiveComprehensionIsValidationFailureAndDoesNotComprehend(string scenario)
    {
        var data = Directory.CreateTempSubdirectory("acp-host-orientation-invalid-").FullName;
        using var host = CreateHost(ReadyOptions(data, scenario));

        await host.StartAsync(CancellationToken.None);
        await WaitForStateAsync(host, "ready", TimeSpan.FromSeconds(30));
        var result = await host.RunOrientationComprehensionAsync(CancellationToken.None);

        Assert.Equal(OrientationStates.Failed, result.State);
        Assert.Contains(DispatchHoldReasons.OrientationFailed, result.HoldReasons);
        Assert.Equal(OrientationEvidenceSources.LiveModel, result.EvidenceSource);
        await host.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// The startup bootstrap prompt holds the single prompt slot. A host that
    /// advertises readiness must also expose that the session is still busy, and
    /// an explicit comprehension request during that window must be a
    /// deterministic conflict rather than a race that either slips through or
    /// reports a generic failure.
    /// </summary>
    [Fact]
    public async Task BootstrapPromptActiveIsObservableAndRejectsComprehension()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-bootstrap-busy-").FullName;
        using var host = CreateHost(ReadyOptions(data, "orientation_gated_bootstrap"));

        await host.StartAsync(CancellationToken.None);
        var busy = await WaitForAsync(
            host,
            status => string.Equals(status.State, "ready", StringComparison.Ordinal)
                && string.Equals(status.SessionState, "busy", StringComparison.Ordinal),
            "a ready host whose bootstrap prompt is still active",
            TimeSpan.FromSeconds(30));

        // The control plane is available (cancel/terminal), but the prompt slot
        // is observably occupied.
        Assert.True(busy.CanControl);
        Assert.Equal("ready", busy.State);
        Assert.Equal("busy", busy.SessionState);

        var conflict = await Assert.ThrowsAsync<OrganizationConcurrencyException>(
            () => host.RunOrientationComprehensionAsync(CancellationToken.None));
        Assert.Contains("prompt operation", conflict.Message, StringComparison.OrdinalIgnoreCase);

        File.WriteAllText(Path.Combine(data, "home", "bootstrap-release"), "release");
        await WaitForStateAsync(host, "ready", TimeSpan.FromSeconds(30));
        await host.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Regression for the PR #246 correction race: waiting for the advertised
    /// ready state and immediately running comprehension must not observe the
    /// bootstrap prompt still holding <c>_promptOperationLock</c>. No sleep and
    /// no retry are used; the bootstrap is only released after its busy hold is
    /// observed.
    /// </summary>
    [Fact]
    public async Task ImmediateComprehensionAfterBootstrapSettlesDoesNotRace()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-bootstrap-settle-").FullName;
        using var host = CreateHost(ReadyOptions(data, "orientation_gated_bootstrap"));

        await host.StartAsync(CancellationToken.None);
        await WaitForAsync(
            host,
            status => string.Equals(status.SessionState, "busy", StringComparison.Ordinal),
            "the gated bootstrap prompt to start",
            TimeSpan.FromSeconds(30));

        File.WriteAllText(Path.Combine(data, "home", "bootstrap-release"), "release");
        var ready = await WaitForStateAsync(host, "ready", TimeSpan.FromSeconds(30));
        Assert.NotEqual("busy", ready.SessionState);

        // Immediate owner comprehension against exactly the advertised state.
        var result = await host.RunOrientationComprehensionAsync(CancellationToken.None);
        Assert.Equal(OrientationStates.Comprehended, result.State);
        Assert.Equal(OrientationEvidenceSources.LiveModel, result.EvidenceSource);
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UnrelatedResponseDoesNotSealPromptCapture()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-orientation-unrelated-").FullName;
        using var host = CreateHost(ReadyOptions(data, "orientation_unrelated_response"));

        await host.StartAsync(CancellationToken.None);
        await WaitForStateAsync(host, "ready", TimeSpan.FromSeconds(30));
        var result = await host.RunOrientationComprehensionAsync(CancellationToken.None);

        Assert.Equal(OrientationStates.Comprehended, result.State);
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task MatchingPromptResponseSealsCaptureBeforeLateChunks()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-orientation-seal-").FullName;
        using var host = CreateHost(ReadyOptions(data, "orientation_post_response"));

        await host.StartAsync(CancellationToken.None);
        await WaitForStateAsync(host, "ready", TimeSpan.FromSeconds(30));
        var result = await host.RunOrientationComprehensionAsync(CancellationToken.None);

        Assert.Equal(OrientationStates.Comprehended, result.State);
        Assert.Equal(OrientationEvidenceSources.LiveModel, result.EvidenceSource);
        await host.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("orientation_gated_malformed")]
    [InlineData("orientation_gated_wrong_employee")]
    [InlineData("orientation_gated_transport_close")]
    public async Task SupersededLiveAttemptPersistsFailureOnOriginalWithoutMutatingReplacement(string scenario)
    {
        var data = Directory.CreateTempSubdirectory("acp-host-orientation-superseded-").FullName;
        using var host = CreateHost(ReadyOptions(data, scenario));

        await host.StartAsync(CancellationToken.None);
        await WaitForStateAsync(host, "ready", TimeSpan.FromSeconds(30));
        var original = host.GetOrientationStatus();
        var operation = host.RunOrientationComprehensionAsync(CancellationToken.None);
        await WaitForFileContainsCountAsync(
            Path.Combine(data, "home", "calls.log"),
            "session/prompt",
            2,
            TimeSpan.FromSeconds(15));

        var overview = host.Organization!.GetOverview();
        host.UpdateOrganizationBasicInstructions(
            overview.Id,
            "Changed while the original live-model attempt was active.",
            overview.Revision);
        var replacement = host.RecomposeAndDeliverOrientation();
        var replacementHolds = replacement.HoldReasons.ToArray();

        File.WriteAllText(Path.Combine(data, "home", "orientation-release"), "release");
        var result = await operation.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(replacement.AssignmentId, result.AssignmentId);
        Assert.Equal(replacement.State, result.State);
        Assert.Equal(replacement.Revision, result.Revision);
        Assert.Equal(replacementHolds, result.HoldReasons);
        var current = host.GetOrientationStatus();
        Assert.Equal(replacement.AssignmentId, current.AssignmentId);
        Assert.Equal(replacement.State, current.State);
        Assert.Equal(replacement.Revision, current.Revision);
        Assert.Equal(replacementHolds, current.HoldReasons);

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(data, OrganizationStore.DatabaseFileName)};Mode=ReadOnly");
        connection.Open();
        using (var assignment = connection.CreateCommand())
        {
            assignment.CommandText =
                "SELECT state, evidence_source, last_error FROM orientation_assignments WHERE id = $id";
            assignment.Parameters.AddWithValue("$id", original.AssignmentId);
            using var reader = assignment.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(OrientationStates.Stale, reader.GetString(0));
            Assert.Equal(OrientationEvidenceSources.LiveModel, reader.GetString(1));
            Assert.False(reader.IsDBNull(2));
        }
        using (var evidence = connection.CreateCommand())
        {
            evidence.CommandText =
                "SELECT COUNT(*) FROM orientation_evidence WHERE assignment_id = $id AND outcome = 'Failed' AND evidence_source = 'live-model'";
            evidence.Parameters.AddWithValue("$id", original.AssignmentId);
            Assert.Equal(1L, (long)evidence.ExecuteScalar()!);
        }

        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AcpErrorLiveComprehensionPersistsLiveModelFailure()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-orientation-error-").FullName;
        using var host = CreateHost(ReadyOptions(data, "orientation_error"));

        await host.StartAsync(CancellationToken.None);
        await WaitForStateAsync(host, "ready", TimeSpan.FromSeconds(30));
        var result = await host.RunOrientationComprehensionAsync(CancellationToken.None);

        Assert.Equal(OrientationStates.Failed, result.State);
        Assert.Equal(OrientationEvidenceSources.LiveModel, result.EvidenceSource);
        Assert.Contains("ACP comprehension request failed", result.LastError, StringComparison.Ordinal);
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TransportClosureDuringLiveComprehensionRetainsFailedEvidence()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-orientation-close-").FullName;
        using var host = CreateHost(ReadyOptions(data, "orientation_transport_close"));

        await host.StartAsync(CancellationToken.None);
        await WaitForStateAsync(host, "ready", TimeSpan.FromSeconds(30));
        var result = await host.RunOrientationComprehensionAsync(CancellationToken.None);

        Assert.Equal(OrientationStates.Failed, result.State);
        Assert.Equal(OrientationEvidenceSources.LiveModel, result.EvidenceSource);
        Assert.Equal("ACP comprehension request failed.", result.LastError);
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AcpErrorTextIsNeverPersistedOrExposed()
    {
        const string sentinel = "SENTINEL_PROVIDER_SECRET_246";
        var data = Directory.CreateTempSubdirectory("acp-host-orientation-secret-").FullName;
        using var host = CreateHost(ReadyOptions(data, "orientation_secret_error"));

        await host.StartAsync(CancellationToken.None);
        await WaitForStateAsync(host, "ready", TimeSpan.FromSeconds(30));
        var result = await host.RunOrientationComprehensionAsync(CancellationToken.None);

        Assert.Equal("ACP comprehension request failed.", result.LastError);
        Assert.DoesNotContain(sentinel, result.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, host.GetOrientationStatus().LastError, StringComparison.Ordinal);
        Assert.DoesNotContain(
            sentinel,
            System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(data, "control.db"))),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            sentinel,
            (string?)typeof(AcpControlHost)
                .GetField("_error", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(host) ?? string.Empty,
            StringComparison.Ordinal);
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LiveComprehensionPersistsLiveModelProvenance()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-orientation-live-").FullName;
        using var host = CreateHost(ReadyOptions(data, "orientation_fast"));

        await host.StartAsync(CancellationToken.None);
        await WaitForStateAsync(host, "ready", TimeSpan.FromSeconds(30));
        var result = await host.RunOrientationComprehensionAsync(CancellationToken.None);

        Assert.Equal(OrientationStates.Comprehended, result.State);
        Assert.Equal(OrientationEvidenceSources.LiveModel, result.EvidenceSource);
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TimedOutRemoteTurnFencesRetryAndLateChunksCannotEnterReplacementAttempt()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-orientation-fence-").FullName;
        var options = ReadyOptions(data, scenario: "orientation_hang");
        options.PromptTimeoutSeconds = 1;
        using var host = CreateHost(options);

        await host.StartAsync(CancellationToken.None);
        await WaitForStateAsync(host, "ready", TimeSpan.FromSeconds(30));
        var first = await host.RunOrientationComprehensionAsync(CancellationToken.None);
        Assert.Equal(OrientationStates.TimedOut, first.State);

        var retry = await Assert.ThrowsAsync<OrganizationConcurrencyException>(() =>
            host.RunOrientationComprehensionAsync(CancellationToken.None));
        Assert.Contains("prompt operation", retry.Message, StringComparison.OrdinalIgnoreCase);
        var redelivery = Assert.Throws<OrganizationConcurrencyException>(() => host.RecomposeAndDeliverOrientation());
        Assert.Contains("remote prompt", redelivery.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(first.AssignmentId, host.GetOrientationStatus().AssignmentId);
        Assert.Equal(OrientationStates.TimedOut, host.GetOrientationStatus().State);
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OrientationComprehensionCallerCancellationIsNotRecordedAsTimeout()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-orientation-cancel-").FullName;
        var options = ReadyOptions(data, scenario: "orientation_hang");
        options.PromptTimeoutSeconds = 20;
        using var host = CreateHost(options);

        await host.StartAsync(CancellationToken.None);
        await WaitForStateAsync(host, "ready", TimeSpan.FromSeconds(30));
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.RunOrientationComprehensionAsync(cancelled.Token));

        Assert.Equal(OrientationStates.Failed, host.GetOrientationStatus().State);
        Assert.Equal(OrientationEvidenceSources.LiveModel, host.GetOrientationStatus().EvidenceSource);
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

        // The authoritative store, not runtime.json, holds the session now.
        var store = host.Organization;
        Assert.NotNull(store);
        var overview = store!.GetOverview();
        var employee = Assert.Single(overview.Employees);
        Assert.Equal(AcpFakeServer.DefaultSessionId, employee.SessionId);
        Assert.False(string.IsNullOrWhiteSpace(overview.Id));
        Assert.False(string.IsNullOrWhiteSpace(host.OrganizationIdentity!.TmuxOwnerToken));
        Assert.True(File.Exists(Path.Combine(data, "control.db")));

        var callsPath = Path.Combine(data, "home", "calls.log");
        await WaitForFileContainsAsync(callsPath, "session/prompt", TimeSpan.FromSeconds(15));

        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task InstructionsChangedAfterLaunchAreDeliveredForNextGenerationNotConfirmedLoaded()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-orientation-handshake-").FullName;
        using var host = CreateHost(ReadyOptions(data, scenario: "orientation_gated_handshake"));

        await host.StartAsync(CancellationToken.None);
        await WaitForFileContainsAsync(Path.Combine(data, "home", "calls.log"), "session/set_mode", TimeSpan.FromSeconds(15));
        var original = host.GetOrientationStatus();
        var overview = host.Organization!.GetOverview();
        host.UpdateOrganizationBasicInstructions(
            overview.Id,
            "Changed after process launch but before session establishment.",
            overview.Revision);
        Assert.Equal(OrientationStates.Stale, host.GetOrientationStatus().State);

        File.WriteAllText(Path.Combine(data, "home", "handshake-release"), "release");
        await WaitForStateAsync(host, "ready", TimeSpan.FromSeconds(30));
        var replacement = host.GetOrientationStatus();

        Assert.NotEqual(original.AssignmentId, replacement.AssignmentId);
        Assert.Equal(OrientationStates.Delivered, replacement.State);
        Assert.True(replacement.RestartRequired);
        Assert.Contains(DispatchHoldReasons.OrientationReloadRequired, replacement.HoldReasons);
        await Assert.ThrowsAsync<OrganizationConcurrencyException>(() =>
            host.RunOrientationComprehensionAsync(CancellationToken.None));
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SameNativeSessionRestartConfirmsDeliveredArtifactGenerationAndAllowsComprehension()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-reload-confirm-").FullName;
        var options = ReadyOptions(data, scenario: "orientation_fast");
        string sessionId;
        string assignmentId;
        string orientationVersion;

        using (var first = CreateHost(options))
        {
            await first.StartAsync(CancellationToken.None);
            await WaitForStateAsync(first, "ready", TimeSpan.FromSeconds(30));
            sessionId = first.GetStatus().SessionId!;
            var overview = first.Organization!.GetOverview();
            first.UpdateOrganizationBasicInstructions(
                overview.Id,
                "Restart-confirmed standing instructions.",
                overview.Revision);
            var delivered = first.RecomposeAndDeliverOrientation();
            assignmentId = delivered.AssignmentId;
            orientationVersion = delivered.OrientationVersion;
            Assert.True(delivered.RestartRequired);
            Assert.Contains(DispatchHoldReasons.OrientationReloadRequired, delivered.HoldReasons);
            await Assert.ThrowsAsync<OrganizationConcurrencyException>(() =>
                first.RunOrientationComprehensionAsync(CancellationToken.None));
            await first.StopAsync(CancellationToken.None);
        }

        using var restarted = CreateHost(options);
        await restarted.StartAsync(CancellationToken.None);
        await WaitForStateAsync(restarted, "ready", TimeSpan.FromSeconds(30));
        var loaded = restarted.GetOrientationStatus();
        Assert.Equal(sessionId, restarted.GetStatus().SessionId);
        Assert.Equal(assignmentId, loaded.AssignmentId);
        Assert.Equal(orientationVersion, loaded.OrientationVersion);
        Assert.False(loaded.RestartRequired);
        Assert.DoesNotContain(DispatchHoldReasons.OrientationReloadRequired, loaded.HoldReasons);

        var comprehended = await restarted.RunOrientationComprehensionAsync(CancellationToken.None);
        Assert.Equal(OrientationStates.Comprehended, comprehended.State);
        Assert.Equal(assignmentId, comprehended.AssignmentId);
        await restarted.StopAsync(CancellationToken.None);
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
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(data, OrganizationStore.DatabaseFileName)};Mode=ReadOnly"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT observed_provider_id, observed_model_id, observed_variant, observed_at FROM runtime_bindings";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.True(reader.IsDBNull(0));
            Assert.True(reader.IsDBNull(1));
            Assert.True(reader.IsDBNull(2));
            Assert.True(reader.IsDBNull(3));
        }

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

    [Fact]
    public async Task PersistedOrganizationNameWinsOverConfiguration()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-orgname-").FullName;
        Directory.CreateDirectory(data);
        var state = RuntimeStateStore.CreateNew("Contoso Development", () => "org-persisted");
        RuntimeStateStore.Save(Path.Combine(data, "runtime.json"), state);

        var options = ReadyOptions(data);
        using var host = CreateHost(options);

        await host.StartAsync(CancellationToken.None);
        var status = await WaitForStateAsync(host, "ready", TimeSpan.FromSeconds(30));

        // Configuration says "AgentControl Development"; the persisted name wins.
        Assert.Equal("Contoso Development", status.OrganizationName);
        Assert.Equal("Contoso Development", host.Organization!.GetOverview().DisplayName);

        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task MalformedRuntimeJsonEvidenceDoesNotBlockAnExistingDatabase()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-evidence-").FullName;

        // First start creates the authoritative database and records the session.
        using (var first = CreateHost(ReadyOptions(data)))
        {
            await first.StartAsync(CancellationToken.None);
            await WaitForStateAsync(first, "ready", TimeSpan.FromSeconds(30));
            await first.StopAsync(CancellationToken.None);
        }

        // The JSON copy is evidence only; a malformed one must not fault the host.
        File.WriteAllText(Path.Combine(data, "runtime.json"), "{ not json");

        using var second = CreateHost(ReadyOptions(data));
        await second.StartAsync(CancellationToken.None);
        var status = await WaitForStateAsync(second, "ready", TimeSpan.FromSeconds(30));
        Assert.Equal(AcpFakeServer.DefaultSessionId, status.SessionId);

        await second.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CorruptAuthoritativeStoreFaultsBeforeOpenCodeStarts()
    {
        var data = Directory.CreateTempSubdirectory("acp-host-corrupt-").FullName;
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "control.db"), "not a database");
        var options = ReadyOptions(data);
        using var host = CreateHost(options);

        await host.StartAsync(CancellationToken.None);
        var status = await WaitForStateAsync(host, "faulted", TimeSpan.FromSeconds(30));

        Assert.NotNull(status.Error);
        Assert.Contains("control.db", status.Error!, StringComparison.Ordinal);

        // The fake ACP server never ran: no session/initialize call was recorded.
        var callsPath = Path.Combine(data, "home", "calls.log");
        Assert.False(File.Exists(callsPath) && File.ReadAllText(callsPath).Contains("initialize", StringComparison.Ordinal));

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
                // "ready" is only a suitable owner-work state once the startup
                // bootstrap prompt has settled: the host serializes all prompt
                // operations, so a busy session means the prompt slot is still
                // held and a comprehension attempt would (correctly) conflict.
                if (!string.Equals(expected, "ready", StringComparison.Ordinal)
                    || !string.Equals(status.SessionState, "busy", StringComparison.Ordinal))
                {
                    return status;
                }
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

    private static async Task WaitForFileContainsCountAsync(
        string path,
        string value,
        int expectedCount,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path)
                && CountOccurrences(File.ReadAllText(path), value) >= expectedCount)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException(
            $"Timed out waiting for {expectedCount} occurrences of '{value}' in '{path}'.");
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
