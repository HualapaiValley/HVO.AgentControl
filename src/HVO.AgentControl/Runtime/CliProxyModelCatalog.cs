namespace HVO.AgentControl.Runtime;

/// <summary>
/// Sanitized, committed CLIProxy policy-lane catalog. Aliases describe requested
/// capability/routing policy; they do not prove which upstream lane served a call.
/// </summary>
public static class CliProxyModelCatalog
{
    public const string Version = "cliproxy-phase1-2026-09-14-v1";
    public const string ProviderConfigVersion = "opencode-1.18.30-openai-compatible-v1";
    public const string ProviderId = "cliproxy";
    public const string ApiKeyEnvironmentVariable = "CLIPROXY_API_KEY";
    public const string CredentialSetId = "agentcontrol-system-phase1";

    private static readonly string[] StandardVariants = ["low", "medium", "high"];

    public static IReadOnlyList<CliProxyModelLane> Models { get; } =
    [
        Lane("default", "Default", StandardVariants, "Opus/Sol priority, then DeepSeek Go/direct.", "mixed-policy"),
        Lane("free", "Free", StandardVariants, "Luna, Luna Go, Muse, Nemotron, then Big Pickle.", "free-policy"),
        Lane("deepseek-v4.1-flash", "DeepSeek v4.1 Flash", StandardVariants, "DeepSeek Go/direct, Luna, Qwen, Grok, GLM, Kimi, Muse, Nemotron, Big Pickle.", "review-policy"),
        Lane("gpt-5.6-luna", "GPT 5.6 Luna", StandardVariants, "Luna, Qwen, Grok, GLM, Kimi, Muse, Nemotron, Big Pickle.", "implementation-policy"),
        Lane("gpt-5.6-terra", "GPT 5.6 Terra", StandardVariants, "DeepSeek Go/direct, Luna, Qwen, Grok, GLM, Kimi, Muse, Nemotron, Big Pickle.", "implementation-policy"),
        Lane("qwen3.8-max", "Qwen 3.8 Max", [], "Qwen, Grok, GLM, Kimi, Muse, Nemotron, Big Pickle.", "fallback-policy"),
        Lane("grok-4.6", "Grok 4.6", StandardVariants, "Grok, GLM, Kimi, Muse, Nemotron, Big Pickle.", "fallback-policy"),
        Lane("glm-5.3", "GLM 5.3", [], "GLM, Kimi, Muse, Nemotron, Big Pickle.", "fallback-policy"),
        Lane("kimi-k3", "Kimi K3", [], "Kimi, Muse, Nemotron, Big Pickle.", "fallback-policy"),
        Lane("muse-spark-1.3-contributor-free", "Muse Spark 1.3 Contributor Free", StandardVariants, "Muse, Nemotron, then Big Pickle.", "free-policy"),
        Lane("nemotron-3-ultra-free", "Nemotron 3 Ultra Free", StandardVariants, "Nemotron, then Big Pickle.", "free-policy"),
        Lane("big-pickle", "Big Pickle", [], "Big Pickle only.", "anonymous-policy"),
        Lane("gpt-6-astra", "Astra", StandardVariants, "Native Fable primary, then DeepSeek Go/direct.", "high-risk-review-policy"),
        Lane("gpt-5.6-sol", "5.6 Sol", StandardVariants, "Native Opus primary, then DeepSeek Go/direct.", "review-policy"),
        Lane("claude-fable-5.1", "Fable 5.1", StandardVariants, "Native Fable primary, then DeepSeek Go/direct.", "review-policy"),
        Lane("claude-opus-5", "Opus 5", StandardVariants, "DeepSeek Go/direct fallback entries; native Opus is exposed through Sol/default.", "review-policy"),
        Lane("claude-sonnet-5", "Sonnet 5", StandardVariants, "DeepSeek Go/direct, then Big Pickle.", "focused-correction-policy"),
        Lane("claude-opus-5-1m", "Opus 5 1M", StandardVariants, "Native Opus primary, then DeepSeek Go/direct.", "high-context-review-policy"),
    ];

    public static CliProxyModelLane? Find(string id) =>
        Models.SingleOrDefault(model => string.Equals(model.Id, id, StringComparison.Ordinal));

    private static CliProxyModelLane Lane(
        string id,
        string displayName,
        IReadOnlyList<string> variants,
        string ladder,
        string attributionClass) =>
        new(id, displayName, variants, ladder, attributionClass);
}

public sealed record CliProxyModelLane(
    string Id,
    string DisplayName,
    IReadOnlyList<string> AllowedVariants,
    string LaneDescription,
    string AttributionClass);
