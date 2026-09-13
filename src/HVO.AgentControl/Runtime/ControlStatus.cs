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
    public static string ToWireValue(this ControlState state) => state switch
    {
        ControlState.Disabled => "disabled",
        ControlState.Starting => "starting",
        ControlState.Ready => "ready",
        ControlState.Degraded => "degraded",
        ControlState.Faulted => "faulted",
        ControlState.Stopped => "stopped",
        _ => state.ToString().ToLowerInvariant(),
    };
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

    /// <summary>Best-effort native session status: "idle", "busy" or null when unknown.</summary>
    [JsonPropertyName("sessionState")]
    public string? SessionState { get; init; }

    // Stock OpenCode 1.18.30 keeps the attached TUI picker client-local.
    [JsonPropertyName("modelSyncSupported")]
    public bool ModelSyncSupported => false;
}
