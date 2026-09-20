using System.Text;

namespace HVO.AgentControl.Organization;

/// <summary>
/// Renders one bounded, canonical worker prompt from a normalized
/// <see cref="WorkerTaskSpec"/>. The prompt is always host-generated from the
/// durable task contract, never supplied verbatim by a caller, so it can never
/// smuggle an arbitrary shell command, another workspace root or an internal
/// control identifier. The rendered string is deterministic for a given
/// specification and is bounded to <see cref="MaxPromptBytes"/>.
/// </summary>
/// <remarks>
/// The prompt expresses a single bounded phase-1 turn: follow the organization
/// and employee orientation, work only inside the declared workspace root, use
/// only the allowed tools, respect the forbidden actions and budget, and never
/// commit, push, create GitHub resources or touch credentials. It asks the model
/// to return a structured report as a later slice (C) will capture; until then
/// the task description and constraints are what is dispatched.
/// </remarks>
public static class WorkerTaskPrompt
{
    /// <summary>The byte bound one rendered prompt may not exceed.</summary>
    public const int MaxPromptBytes = 16 * 1024;

    public static string Render(WorkerTaskSpec spec)
    {
        var normalized = OrganizationStore.NormalizeWorkerTaskSpec(spec);
        var builder = new StringBuilder();
        builder.AppendLine("You are a managed employee completing exactly one bounded task.");
        builder.AppendLine("Follow the organization and employee orientation you were given; this task is subordinate to it.");
        builder.AppendLine();
        builder.Append("Task: ").AppendLine(normalized.Description);
        builder.Append("Workspace root: ").AppendLine(normalized.WorkspaceRoot);
        builder.Append("Allowed paths (relative to the workspace root): ").AppendLine(string.Join(", ", normalized.AllowedPaths));
        builder.Append("Allowed tools: ").AppendLine(string.Join(", ", normalized.AllowedTools));
        builder.Append("Forbidden actions: ").AppendLine(string.Join(", ", normalized.ForbiddenActions));
        builder.Append("Budget: at most ").Append(normalized.MaximumSeconds).AppendLine(" seconds and exactly one turn.");
        builder.Append("Test recipe: ").AppendLine(normalized.TestRecipeId is null ? "none" : normalized.TestRecipeId + " (the host selects the exact command)");
        builder.AppendLine();
        builder.AppendLine("Hard rules, always in force:");
        builder.AppendLine("- Work only inside the declared workspace root and the allowed paths; never escape it.");
        builder.AppendLine("- Do not run commands outside the allowed tools, the allowed paths and the budget.");
        builder.AppendLine("- Do not commit, do not push, do not create or modify any GitHub resource, and do not access credentials or secrets.");
        builder.AppendLine("- If a request would violate these rules, refuse it and report the denial instead of proceeding.");
        builder.AppendLine();
        builder.AppendLine("When the turn ends, return ONLY one unfenced JSON object with exactly these fields:");
        builder.AppendLine("summary (string), changedPaths (array of safe relative paths), tests (array of {recipeId,status,summary}), deniedAction (null or {requested,action,result,noSideEffect}), limitations (array of strings).");
        builder.AppendLine("Each test status must be passed, failed, or not-run. recipeId must be the declared recipe id or null. A deniedAction must set noSideEffect to true.");
        builder.AppendLine("Do not include markdown fences or any other text. The host verifies the result independently; your report is evidence only and never proves success.");

        var prompt = builder.ToString();
        if (Encoding.UTF8.GetByteCount(prompt) > MaxPromptBytes)
            throw new OrganizationValidationException($"The rendered task prompt must be at most {MaxPromptBytes} bytes.");
        return prompt;
    }
}
