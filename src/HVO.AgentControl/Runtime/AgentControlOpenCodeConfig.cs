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
    public static string Build(
        string model,
        string instructionsPath,
        CliProxyRuntimeConfiguration? cliProxy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(instructionsPath);
        if (CliProxyRuntimeConfiguration.IsCliProxyModel(model) && cliProxy is null)
        {
            throw new InvalidOperationException("A CLIProxy policy lane requires validated provider configuration.");
        }

        if (!CliProxyRuntimeConfiguration.IsCliProxyModel(model) && cliProxy is not null)
        {
            throw new InvalidOperationException("CLIProxy provider configuration cannot be attached to a non-CLIProxy model.");
        }

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
                // Defense-in-depth token matching only. OS isolation (separate
                // controller/agent UIDs and a 0700 private store) is the actual
                // boundary; these rules just remove the obvious attempts.
                ["*agentcontrol-secrets*"] = "deny",
                ["*runtime.json*"] = "deny",
                ["*/control-data*"] = "deny",
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
                ["/control-data/**"] = "deny",
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
                ["/control-data/**"] = "deny",
                ["/agent-config/**"] = "deny",
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
            ["enabled_providers"] = cliProxy is null ? new[] { "opencode" } : new[] { CliProxyModelCatalog.ProviderId },
            ["permission"] = permission,
            ["agent"] = BuildAgents(model, cliProxy),
        };

        if (cliProxy is not null)
        {
            config["provider"] = BuildProvider(cliProxy);
        }

        return JsonSerializer.Serialize(config, SerializerOptions);
    }

    /// <summary>
    /// Builds the agent map. The control role is always present. When CLIProxy is
    /// configured, one bounded read-only subagent is generated per deterministic
    /// task class. No review agent is generated: an independent review must
    /// explicitly select its policy lane.
    /// </summary>
    private static Dictionary<string, object?> BuildAgents(
        string model,
        CliProxyRuntimeConfiguration? cliProxy)
    {
        var control = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["description"] = "Consolidated manager/operations/IT control role. No Fleet, no workers, no code work.",
            ["mode"] = "primary",
            ["model"] = model,
        };
        if (!string.IsNullOrWhiteSpace(cliProxy?.Variant))
        {
            control["variant"] = cliProxy!.Variant;
        }

        var agents = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [RoleName] = control,
        };

        if (cliProxy is null)
        {
            return agents;
        }

        foreach (var task in CliProxyProfile.TaskClasses)
        {
            agents[task.AgentName] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["description"] = task.Description,
                // `all` keeps task delegation available and also makes explicit
                // `opencode run --agent <task>` selection effective in 1.18.30.
                ["mode"] = "all",
                ["model"] = $"{CliProxyModelCatalog.ProviderId}/{task.LaneId}",
                ["variant"] = task.Variant,
                ["options"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["reasoningEffort"] = task.Variant,
                },
                // OpenCode 1.18.30 accepts `steps`; `maxSteps` is retained for
                // compatibility with the alternate AgentConfig spelling used by
                // clients/schema consumers. Both carry the same conservative cap.
                ["steps"] = task.MaxSteps,
                ["maxSteps"] = task.MaxSteps,
                ["permission"] = BuildReadOnlyPermission(),
            };
        }

        return agents;
    }

    /// <summary>
    /// The direct OpenAI-compatible provider document. It maps only the exposed
    /// policy lanes. Every advertised variant carries its actual
    /// <c>reasoningEffort</c>, and selected primary/task lanes repeat their
    /// effective effort in model-level <c>options</c>. OpenCode 1.18.30 otherwise applies its
    /// built-in low default on the wire even when the selected variant object is
    /// correct; the pinned outbound integration test guards this exact behavior.
    /// </summary>
    private static Dictionary<string, object?> BuildProvider(CliProxyRuntimeConfiguration cliProxy) =>
        new(StringComparer.Ordinal)
        {
            [CliProxyModelCatalog.ProviderId] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = "AgentControl CLIProxy policy lanes",
                ["npm"] = "@ai-sdk/openai-compatible",
                ["env"] = new[] { CliProxyModelCatalog.ApiKeyEnvironmentVariable },
                ["options"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["baseURL"] = cliProxy.Endpoint.ToString().TrimEnd('/'),
                    ["apiKey"] = "{" + "env:" + CliProxyModelCatalog.ApiKeyEnvironmentVariable + "}",
                },
                ["models"] = CliProxyProfile.SelectableLanes.ToDictionary(
                    lane => lane.Id,
                    lane => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["id"] = lane.Id,
                        ["name"] = lane.DisplayName,
                        ["reasoning"] = lane.AllowedVariants.Count > 0,
                        ["options"] = BuildLaneOptions(lane, cliProxy),
                        ["variants"] = lane.AllowedVariants.ToDictionary(
                            variant => variant,
                            variant => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["reasoningEffort"] = variant,
                            },
                            StringComparer.Ordinal),
                    },
                    StringComparer.Ordinal),
            },
        };

    private static Dictionary<string, object?> BuildLaneOptions(
        CliProxyModelLane lane,
        CliProxyRuntimeConfiguration cliProxy)
    {
        var effort = string.Equals(lane.Id, cliProxy.Lane.Id, StringComparison.Ordinal)
            ? cliProxy.Variant
            : CliProxyProfile.TaskClasses
                .SingleOrDefault(task => string.Equals(task.LaneId, lane.Id, StringComparison.Ordinal))
                ?.Variant;
        return string.IsNullOrWhiteSpace(effort)
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["reasoningEffort"] = effort,
            };
    }

    /// <summary>
    /// Bounded, read-only task-agent permission. Task agents may read but not
    /// write, execute, or reach the network; the sensitive-path denies mirror the
    /// control role.
    /// </summary>
    private static Dictionary<string, object?> BuildReadOnlyPermission() =>
        new(StringComparer.Ordinal)
        {
            ["bash"] = "deny",
            ["edit"] = "deny",
            ["webfetch"] = "deny",
            ["websearch"] = "deny",
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
                ["/control-data/**"] = "deny",
                ["/data/runtime.json"] = "deny",
                ["**/runtime.json"] = "deny",
            },
        };
}
