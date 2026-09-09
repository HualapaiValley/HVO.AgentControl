namespace HVO.AgentControl.Core;

public static class TaskRiskLevels
{
    public const string Low = "low", Medium = "medium", High = "high", Critical = "critical";

    public static int Rank(string? value) => value switch
    {
        Low => 0,
        Medium => 1,
        High => 2,
        Critical => 3,
        _ => -1
    };
}

public sealed class TaskRiskFloorOptions
{
    public string Version { get; set; } = "risk-floor-v1";
    public string UnclassifiedRouteMaximum { get; set; } = TaskRiskLevels.Low;
    public Dictionary<string, string> RouteMaximums { get; set; } = new(StringComparer.Ordinal)
    {
        ["openai/gpt-5.6-luna"] = TaskRiskLevels.Low,
        ["openai/gpt-5.6-terra"] = TaskRiskLevels.Medium,
        ["openai/gpt-5.6-sol"] = TaskRiskLevels.High,
        ["openai/gpt-6-astra"] = TaskRiskLevels.Critical,
        ["opencode-go/gpt-5.6-luna"] = TaskRiskLevels.Low
    };
}

public sealed record TaskRiskErrorDetails(string RiskLevel, string ProviderId, string ModelId,
    string RouteMaximum, string PolicyVersion);

public sealed record TaskRiskRejection(string Error, string Code, TaskRiskErrorDetails Details);

public static class TaskRiskPolicy
{
    public static void Validate(TaskRiskFloorOptions policy)
    {
        if (string.IsNullOrWhiteSpace(policy.Version) || TaskRiskLevels.Rank(policy.UnclassifiedRouteMaximum) < 0 ||
            policy.RouteMaximums.Any(x => string.IsNullOrWhiteSpace(x.Key) || !x.Key.Contains('/', StringComparison.Ordinal) ||
                TaskRiskLevels.Rank(x.Value) < 0))
            throw new InvalidOperationException("Task risk floor policy is invalid; inspect Control:TaskRiskFloor configuration.");
    }

    public static TaskRiskRejection? Evaluate(TaskRiskFloorOptions policy, PromptInput prompt)
    {
        var riskRank = TaskRiskLevels.Rank(prompt.RiskLevel);
        var provider = prompt.ProviderId ?? "";
        var model = prompt.ModelId ?? "";
        var maximum = policy.RouteMaximums.GetValueOrDefault(Route(provider, model), policy.UnclassifiedRouteMaximum);
        var details = new TaskRiskErrorDetails(prompt.RiskLevel ?? "", provider, model, maximum, policy.Version);
        if (string.IsNullOrWhiteSpace(prompt.RiskLevel))
            return new("Task riskLevel is required before dispatch.", "risk_level_required", details);
        if (riskRank < 0)
            return new("Task riskLevel must be low, medium, high, or critical.", "risk_level_invalid", details);
        if (riskRank > TaskRiskLevels.Rank(maximum))
            return new($"The selected provider/model is below the configured {prompt.RiskLevel} task risk floor.", "risk_floor_not_met", details);
        return null;
    }

    public static PromptInput Admit(TaskRiskFloorOptions policy, PromptInput prompt)
    {
        Require(policy, prompt);
        var routeMaximum = policy.RouteMaximums.GetValueOrDefault(
            Route(prompt.ProviderId ?? "", prompt.ModelId ?? ""), policy.UnclassifiedRouteMaximum);
        return prompt with { RiskPolicyVersion = policy.Version, RiskRouteMaximum = routeMaximum };
    }

    public static bool TryReadAndEvaluate(TaskRiskFloorOptions policy, string payload,
        out PromptInput? prompt, out TaskRiskRejection? rejection)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("riskLevel", out var risk) ||
                risk.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                prompt = null;
                rejection = Required(policy);
                return false;
            }
            prompt = Json.Read<PromptInput>(payload);
            rejection = Evaluate(policy, prompt);
            if (rejection is null)
            {
                var routeMaximum = policy.RouteMaximums.GetValueOrDefault(
                    Route(prompt.ProviderId ?? "", prompt.ModelId ?? ""), policy.UnclassifiedRouteMaximum);
                if (prompt.RiskPolicyVersion != policy.Version || prompt.RiskRouteMaximum != routeMaximum)
                    rejection = new("Task risk admission provenance is missing or no longer matches the active policy.",
                        "risk_admission_provenance_invalid",
                        new(prompt.RiskLevel ?? "", prompt.ProviderId ?? "", prompt.ModelId ?? "", routeMaximum, policy.Version));
            }
            return rejection is null;
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException)
        {
            prompt = null;
            rejection = new("Stored task payload is invalid and cannot be dispatched.", "task_payload_invalid",
                new("", "", "", policy.UnclassifiedRouteMaximum, policy.Version));
            return false;
        }
    }

    public static void Require(TaskRiskFloorOptions policy, PromptInput prompt)
    {
        if (Evaluate(policy, prompt) is not { } rejection) return;
        var status = rejection.Code is "risk_level_required" or "risk_level_invalid" ? 400 : 409;
        throw new ControlException(rejection.Error, status, rejection.Code, rejection.Details);
    }

    private static string Route(string providerId, string modelId) => providerId + "/" + modelId;

    private static TaskRiskRejection Required(TaskRiskFloorOptions policy) => new(
        "Task riskLevel is required before dispatch.", "risk_level_required",
        new("", "", "", policy.UnclassifiedRouteMaximum, policy.Version));
}
