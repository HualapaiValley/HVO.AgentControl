using System.Text.Json;

namespace HVO.AgentControl.Runtime;

/// <summary>
/// Builds the safe OpenCode configuration and instruction payload used by the
/// dedicated control runtime. The generated config intentionally has no Fleet
/// dependency, disables web egress, denies credential material explicitly and
/// exposes a single consolidated manager/operations/IT agent role.
/// </summary>
public static class AgentControlOpenCodeConfig
{
    /// <summary>OpenCode agent (mode) that carries the consolidated control role.</summary>
    public const string RoleName = "agentcontrol";

    /// <summary>Instruction file written into the runtime's private OpenCode home.</summary>
    public const string InstructionsFileName = "agentcontrol-instructions.md";

    // Fleet/V1 tool names are denied explicitly so a stale plugin or environment
    // cannot silently reintroduce the archived control plane. The base OpenCode
    // build does not ship these tools; denying unknown names is harmless.
    private static readonly string[] FleetToolNames =
    [
        "fleet_send", "fleet_inbox", "fleet_roster", "fleet_receipts", "fleet_wake",
        "fleet_wake_list", "fleet_wake_cancel", "fleet_operator_inbox", "fleet_operator_read",
        "fleet_team_create", "fleet_team_remove", "fleet_hire", "fleet_fire", "fleet_repo_add",
        "fleet_worktree_add", "fleet_worktree_remove", "fleet_worktree_list", "fleet_task_run",
        "fleet_task_status", "fleet_task_log", "fleet_task_assign", "fleet_capability_request",
        "fleet_escalations", "fleet_escalation_forward", "fleet_escalation_resolve",
        "fleet_member_restart", "fleet_member_read", "fleet_member_prompt", "fleet_review_request",
        "fleet_reviews", "fleet_review_submit", "fleet_finalize", "fleet_events",
        "fleet_charter_update", "fleet_interactions", "fleet_interaction_reply", "fleet_grant_request",
        "fleet_grants", "fleet_grant", "fleet_privileged", "fleet_member_control",
        "fleet_maintenance", "fleet_pr", "fleet_drain", "fleet_drain_status", "fleet_drain_end",
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>Instruction text that defines the consolidated control role.</summary>
    public static string BuildInstructions(string organizationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationName);

        return $$"""
            # AgentControl Runtime

            You are the consolidated manager, operations and IT role for the
            "{{organizationName}}" AgentControl runtime. This runtime manages itself;
            it is not a code worker.

            Rules:
            - There is no Fleet. Do not call, emulate or reference Fleet/V1 tooling.
            - No worker runtime exists yet. Do not hire, delegate to or dispatch workers.
            - Do not perform code work: no repository edits, builds, migrations or tests.
            - Prefer plain informational answers. Keep responses short and factual.
            - On your bootstrap turn, reply with a single brief readiness line. Do not
              call tools and do not start any long-running work.
            """;
    }

    /// <summary>
    /// Produces the JSON value for <c>OPENCODE_CONFIG_CONTENT</c>.
    /// </summary>
    public static string Build(string model, string instructionsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(instructionsPath);

        var permission = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            // Broad allow first, specific denies after: OpenCode evaluates the last
            // matching permission rule, so explicit denies below always win.
            ["bash"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["*"] = "allow",
                ["*sudo *"] = "deny",
                ["*rm -rf /*"] = "deny",
                ["*rm -rf ~*"] = "deny",
                ["*id_rsa*"] = "deny",
                ["*id_ed25519*"] = "deny",
                ["*GITHUB_TOKEN*"] = "deny",
                ["*GH_TOKEN*"] = "deny",
                ["*OPENCODE_SERVER_PASSWORD*"] = "deny",
                ["*cat *.env*"] = "deny",
                // Defense-in-depth token matching only; same-UID isolation is not
                // a security boundary (the parent process owns authorization).
                ["*agentcontrol-secrets*"] = "deny",
                ["*runtime.json*"] = "deny",
            },
            ["read"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["*"] = "allow",
                ["**/.env"] = "deny",
                ["**/.env.*"] = "deny",
                ["**/*id_rsa*"] = "deny",
                ["**/*id_ed25519*"] = "deny",
                ["**/.git-credentials"] = "deny",
                ["**/.netrc"] = "deny",
                ["**/.aws/**"] = "deny",
                ["**/.ssh/**"] = "deny",
                ["/run/agentcontrol-secrets/**"] = "deny",
                ["/data/runtime.json"] = "deny",
                ["**/runtime.json"] = "deny",
            },
            ["edit"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["*"] = "allow",
                ["**/.env"] = "deny",
                ["**/.env.*"] = "deny",
                ["**/*id_rsa*"] = "deny",
                ["**/*id_ed25519*"] = "deny",
                ["**/.git-credentials"] = "deny",
                ["**/.netrc"] = "deny",
                ["**/.ssh/**"] = "deny",
                ["/run/agentcontrol-secrets/**"] = "deny",
                ["/data/runtime.json"] = "deny",
                ["**/runtime.json"] = "deny",
            },
            ["webfetch"] = "deny",
            ["websearch"] = "deny",
        };

        foreach (var tool in FleetToolNames)
        {
            permission[tool] = "deny";
        }

        var config = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["$schema"] = "https://opencode.ai/config.json",
            ["model"] = model,
            ["instructions"] = new[] { instructionsPath },
            ["permission"] = permission,
            ["agent"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [RoleName] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["description"] = "Consolidated manager/operations/IT control role. No Fleet, no workers, no code work.",
                    ["mode"] = "primary",
                    ["model"] = model,
                },
            },
        };

        return JsonSerializer.Serialize(config, SerializerOptions);
    }
}
