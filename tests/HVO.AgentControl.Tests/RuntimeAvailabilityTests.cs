using HVO.AgentControl.Runtime;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Contract for <see cref="ControlStatus.CanControl"/>, the single availability
/// boolean the portal gates session-scoped controls on.
/// </summary>
/// <remarks>
/// <para>
/// The rule is deliberately narrow: an established session (non-empty
/// <see cref="ControlStatus.SessionId"/>) on a host whose state is <c>ready</c>
/// or <c>degraded</c>. It is an availability claim about the control plane and
/// nothing else — it does not assert that the model provider works, that the
/// last turn succeeded, or that a cancellation will complete.
/// </para>
/// <para>
/// These are pure snapshot assertions with no OpenCode process, no tmux and no
/// model provider, so the rule is pinned independently of the live host tests in
/// <see cref="AcpControlHostTests"/>.
/// </para>
/// </remarks>
public sealed class RuntimeAvailabilityTests
{
    [Theory]
    [InlineData("ready")]
    [InlineData("degraded")]
    public void EstablishedSessionOnALiveHostCanControl(string state)
    {
        Assert.True(Status(state).CanControl);
    }

    [Fact]
    public void DegradedKeepsItsHonestStateAndErrorWhileStayingControllable()
    {
        // The recovery path must not be bought by lying about readiness: a
        // transient bootstrap/model failure stays reported as degraded with its
        // error intact, and the operator still gets cancel/terminal controls to
        // inspect and recover the session.
        var status = Status("degraded", error: "Bootstrap prompt failed (code -32603).");

        Assert.True(status.CanControl);
        Assert.Equal("degraded", status.State);
        Assert.Equal("Bootstrap prompt failed (code -32603).", status.Error);
        Assert.False(Program.IsRuntimeReady(status));
    }

    [Fact]
    public void DegradedWithAReadyTerminalIsControllableButStillNotReady()
    {
        // TerminalReady is an additional requirement for attaching, never a
        // substitute for an established session, and never a promotion out of
        // degraded. /health/ready stays closed.
        var status = Status("degraded", terminalReady: true, error: "bootstrap degraded");

        Assert.True(status.CanControl);
        Assert.True(status.TerminalReady);
        Assert.Equal("degraded", status.State);
        Assert.NotNull(status.Error);
        Assert.False(Program.IsRuntimeReady(status));
    }

    [Theory]
    // A protocol/schema fault tore the child down. It is never controllable,
    // even though the session id survives in the snapshot as evidence.
    [InlineData("faulted")]
    [InlineData("disabled")]
    [InlineData("stopped")]
    // Starting has no established session to act on yet.
    [InlineData("starting")]
    // An unrecognized state fails closed rather than defaulting to available.
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("Ready")]
    [InlineData("READY")]
    [InlineData("degraded ")]
    public void OnlyReadyOrDegradedCanControl(string state)
    {
        Assert.False(Status(state).CanControl);
    }

    [Fact]
    public void AProtocolFaultIsNeverControllableEvenWithATerminalAndASession()
    {
        // The strongest form of the rule: everything else looks healthy and the
        // fault alone must still close the gate.
        var status = Status("faulted", sessionId: "ses_exact", terminalReady: true, error: "schema fault");

        Assert.False(status.CanControl);
        Assert.Equal("ses_exact", status.SessionId);
        Assert.True(status.TerminalReady);
    }

    [Theory]
    [InlineData("ready", null)]
    [InlineData("ready", "")]
    [InlineData("degraded", null)]
    [InlineData("degraded", "")]
    public void NoEstablishedSessionCannotControl(string state, string? sessionId)
    {
        // A live host without an owned session id has nothing to cancel or
        // attach to; availability must not be inferred from the state alone.
        Assert.False(Status(state, sessionId).CanControl);
    }

    [Fact]
    public void CanControlIsDerivedAndTracksTheStateItWasBuiltFrom()
    {
        // CanControl has no setter, so a caller cannot hand-assert availability;
        // a record copy recomputes it from the copied state.
        var degraded = Status("degraded");
        Assert.True(degraded.CanControl);

        Assert.False((degraded with { State = "faulted" }).CanControl);
        Assert.False((degraded with { SessionId = null }).CanControl);
        Assert.True((degraded with { State = "ready" }).CanControl);
    }

    [Fact]
    public void CanControlIsSerializedAsCanControlAndDoesNotImplyModelSync()
    {
        // The portal reads this field by name, and model writes stay gated by
        // ModelSyncSupported independently of control availability.
        var json = System.Text.Json.JsonSerializer.Serialize(Status("degraded"));
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.True(document.RootElement.GetProperty("canControl").GetBoolean());
        Assert.Equal("degraded", document.RootElement.GetProperty("state").GetString());
        Assert.False(document.RootElement.GetProperty("modelSyncSupported").GetBoolean());
    }

    [Theory]
    [InlineData(ControlState.Ready, true)]
    [InlineData(ControlState.Degraded, true)]
    [InlineData(ControlState.Starting, false)]
    [InlineData(ControlState.Faulted, false)]
    [InlineData(ControlState.Stopped, false)]
    [InlineData(ControlState.Disabled, false)]
    public void EnumAndWireFormsOfTheRuleAgree(ControlState state, bool allowsControl)
    {
        Assert.Equal(allowsControl, state.AllowsControl());
        Assert.Equal(allowsControl, ControlStateExtensions.AllowsControl(state.ToWireValue()));
    }

    private static ControlStatus Status(
        string state = "ready",
        string? sessionId = "ses_exact",
        bool terminalReady = false,
        string? error = null) => new()
        {
            State = state,
            OrganizationName = "AgentControl Development",
            SessionId = sessionId,
            TerminalReady = terminalReady,
            Error = error,
        };
}
