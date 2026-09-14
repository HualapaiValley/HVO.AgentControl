namespace HVO.AgentControl.Runtime;

/// <summary>
/// Deterministic Phase 1 control/employee exposure profile.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CliProxyModelCatalog"/> is the full, sanitized description of every
/// policy lane the proxy advertises. Only a small, explicitly chosen subset is
/// ever exposed to the generated OpenCode config. The proxy's internal fallback
/// source aliases (Qwen, Grok, GLM, Kimi, Muse, Nemotron) exist so a policy lane
/// can fail over; they are not task lanes a managed employee may select and are
/// deliberately not generated.
/// </para>
/// <para>
/// A named alias describes a requested policy lane, not a serving model. The
/// profile therefore exposes lanes, not model identities, and the review pool is
/// an explicit selection rule rather than a generated agent that would silently
/// satisfy independent review.
/// </para>
/// </remarks>
public static class CliProxyProfile
{
    /// <summary>
    /// Committed exposure/config version recorded in runtime binding metadata
    /// alongside the catalog version. Bump when the exposed set or task classes
    /// change.
    /// </summary>
    public const string Version = "agentcontrol-control-phase1-v1";

    /// <summary>Focused-correction agent name (a named, read-only task agent).</summary>
    public const string CorrectionAgentName = "correction";

    /// <summary>
    /// Directly selectable policy lanes, in deterministic order. This is a
    /// strict subset of <see cref="CliProxyModelCatalog.Models"/>: the fallback
    /// internals and the image model are never exposed.
    /// </summary>
    private static readonly string[] SelectableLaneIds =
    [
        "default",
        "free",
        "deepseek-v4.1-flash",
        "gpt-5.6-luna",
        "gpt-5.6-terra",
        "big-pickle",
        "gpt-6-astra",
        "gpt-5.6-sol",
        "claude-fable-5.1",
        "claude-opus-5",
        "claude-sonnet-5",
        "claude-opus-5-1m",
    ];

    /// <summary>
    /// Explicit review pool. Reviewers select exactly one of these; there is no
    /// generated review agent, so no implicit model can satisfy the requirement.
    /// </summary>
    private static readonly string[] ReviewLaneIds =
    [
        "gpt-6-astra",
        "gpt-5.6-sol",
        "claude-fable-5.1",
        "claude-opus-5",
        "deepseek-v4.1-flash",
        "claude-opus-5-1m",
    ];

    /// <summary>
    /// Catalog lanes that exist only so an exposed policy lane can fail over.
    /// They are never generated and must never be selected as a task lane.
    /// </summary>
    public static IReadOnlyList<string> NonExposedSourceAliases { get; } =
    [
        "qwen3.8-max",
        "grok-4.6",
        "glm-5.3",
        "kimi-k3",
        "muse-spark-1.3-contributor-free",
        "nemotron-3-ultra-free",
    ];

    /// <summary>Lanes the generated provider map advertises.</summary>
    public static IReadOnlyList<CliProxyModelLane> SelectableLanes { get; } =
        SelectableLaneIds.Select(RequireLane).ToArray();

    /// <summary>Lanes eligible for an explicit attributability-qualified review draw.</summary>
    public static IReadOnlyList<CliProxyModelLane> ReviewLanes { get; } =
        ReviewLaneIds.Select(RequireLane).ToArray();

    /// <summary>
    /// Deterministic task classes. Each maps a task to one lane and one explicit
    /// variant; the generated config turns each into a bounded, read-only
    /// subagent. <c>default</c> is the non-attributable availability lane and is
    /// never usable as an independent named review.
    /// </summary>
    public static IReadOnlyList<CliProxyTaskClass> TaskClasses { get; } =
    [
        new(
            "heavy",
            "Heavy",
            "default",
            "medium",
            "Heavy availability work on the non-attributable default policy lane; never an independent named review."),
        new(
            "workhorse",
            "Workhorse",
            "gpt-5.6-terra",
            "medium",
            "Primary workhorse work on the Terra implementation policy lane."),
        new(
            "cheap",
            "Cheap",
            "gpt-5.6-luna",
            "low",
            "Low-cost work on the Luna implementation policy lane."),
        new(
            CorrectionAgentName,
            "Focused correction",
            "claude-sonnet-5",
            "medium",
            "Focused correction work on the Sonnet focused-correction policy lane."),
    ];

    private static CliProxyModelLane RequireLane(string id) =>
        CliProxyModelCatalog.Find(id)
        ?? throw new InvalidOperationException(
            $"The control exposure profile references unknown policy lane '{id}'.");
}

/// <summary>
/// One deterministic task class: a stable agent name mapped to one exposed
/// policy lane and one explicit advertised variant.
/// </summary>
public sealed record CliProxyTaskClass(
    string AgentName,
    string DisplayName,
    string LaneId,
    string Variant,
    string Description);
