using System.Text.Json.Serialization;

namespace HVO.AgentControl.Runtime;

/// <summary>
/// Runtime availability of the background control host. These are intentionally
/// distinct from "the model replied" or "a prompt finished".
/// </summary>
public enum ControlState
{
    /// <summary>The control runtime is configured off; no OpenCode process was launched.</summary>
    Disabled,

    /// <summary>Directories/process/session are being established.</summary>
    Starting,

    /// <summary>The ACP session is established and the host is alive.</summary>
    Ready,

    /// <summary>
    /// The ACP session is established but a non-fatal readiness step failed
    /// (for example the bootstrap turn did not complete normally). The host is
    /// alive and observable but must not be reported as fully ready.
    /// </summary>
    Degraded,

    /// <summary>Startup or supervision failed; the host is not usable.</summary>
    Faulted,

    /// <summary>The host shut down cleanly.</summary>
    Stopped,
}

public static class ControlStateExtensions
{
    /// <summary>Wire value of <see cref="ControlState.Ready"/>.</summary>
    public const string ReadyWireValue = "ready";

    /// <summary>Wire value of <see cref="ControlState.Degraded"/>.</summary>
    public const string DegradedWireValue = "degraded";

    public static string ToWireValue(this ControlState state) => state switch
    {
        ControlState.Disabled => "disabled",
        ControlState.Starting => "starting",
        ControlState.Ready => ReadyWireValue,
        ControlState.Degraded => DegradedWireValue,
        ControlState.Faulted => "faulted",
        ControlState.Stopped => "stopped",
        _ => state.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// The single availability rule shared by the status snapshot and the host
    /// operation: the runtime owns a live ACP child and an established session.
    /// </summary>
    /// <remarks>
    /// <see cref="ControlState.Degraded"/> qualifies deliberately. A degraded
    /// runtime failed a readiness step (typically a transient provider/model
    /// failure during the bootstrap turn) but still owns the session and the
    /// transport, so locking the operator out would remove exactly the controls
    /// needed to inspect and recover it. <see cref="ControlState.Starting"/> has
    /// no established session yet. Disabled/Faulted/Stopped deny control even
    /// while asynchronous teardown still owns a live child.
    /// </remarks>
    public static bool AllowsControl(this ControlState state)
        => state is ControlState.Ready or ControlState.Degraded;

    /// <summary>
    /// Wire-value form of <see cref="AllowsControl(ControlState)"/>, used by the
    /// serialized snapshot so both sides of the contract cannot drift apart.
    /// </summary>
    public static bool AllowsControl(string? wireState)
        => string.Equals(wireState, ReadyWireValue, StringComparison.Ordinal)
            || string.Equals(wireState, DegradedWireValue, StringComparison.Ordinal);
}

/// <summary>
/// One selectable model advertised by a connected native provider. <see cref="Id"/>
/// is the canonical <c>provider/model</c> reference accepted by
/// <see cref="AcpControlHost.SetModelAsync"/>.
/// </summary>
public sealed record ControlModel
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }
}

/// <summary>
/// Immutable snapshot returned by <see cref="AcpControlHost.GetStatus"/>.
/// C# members are PascalCase while the wire serialization is explicitly lowercase.
/// </summary>
public sealed record ControlStatus
{
    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("organizationName")]
    public required string OrganizationName { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    /// <summary>
    /// The session-owned model actually in effect, as <c>provider/model</c>.
    /// <c>"unknown"</c> until the native session has been observed; never the
    /// configured startup default once the session reports its own model.
    /// </summary>
    [JsonPropertyName("model")]
    public string Model { get; init; } = "unknown";

    /// <summary>
    /// Advertised models from native <c>/provider</c>, restricted to connected
    /// providers. Empty when the catalog is unavailable; the UI retains its
    /// current selection in that case.
    /// </summary>
    [JsonPropertyName("models")]
    public IReadOnlyList<ControlModel> Models { get; init; } = [];

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset? StartedAt { get; init; }

    [JsonPropertyName("terminalReady")]
    public bool TerminalReady { get; init; }

    /// <summary>
    /// Session status: "idle", "busy" or null when unknown. The host reports
    /// "busy" whenever it owns an in-flight prompt operation (startup bootstrap or
    /// owner comprehension), even if the best-effort native poll is unknown, so a
    /// caller can wait for the prompt slot to settle without racing it.
    /// </summary>
    [JsonPropertyName("sessionState")]
    public string? SessionState { get; init; }

    /// <summary>
    /// Whether session-scoped control operations (cancel, terminal attach) may
    /// be offered for this snapshot: an established session on a live host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived, never set: it is true only when <see cref="State"/> is
    /// <c>ready</c> or <c>degraded</c> <em>and</em> <see cref="SessionId"/> is
    /// non-empty. It is deliberately not a promotion of <c>degraded</c> to
    /// <c>ready</c> — <see cref="State"/> and <see cref="Error"/> stay honest,
    /// and <see cref="ModelSyncSupported"/> plus the parent's own readiness
    /// predicate continue to gate model writes and <c>/health/ready</c>
    /// separately.
    /// </para>
    /// <para>
    /// This is an availability claim about the control plane, not a claim that
    /// the model provider works, that the last turn succeeded, or that a
    /// cancellation will complete. A caller must still treat
    /// <see cref="AcpControlHost.CancelAsync"/> returning false as "not
    /// accepted", and terminal attach additionally requires
    /// <see cref="TerminalReady"/>.
    /// </para>
    /// </remarks>
    [JsonPropertyName("canControl")]
    public bool CanControl =>
        ControlStateExtensions.AllowsControl(State) && !string.IsNullOrEmpty(SessionId);

    // Stock OpenCode 1.18.30 keeps the attached TUI picker client-local.
    [JsonPropertyName("modelSyncSupported")]
    public bool ModelSyncSupported => false;
}
