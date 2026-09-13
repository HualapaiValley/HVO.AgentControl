namespace HVO.AgentControl.Runtime;

/// <summary>
/// Builds the child process environment for the control runtime.
/// The parent environment is inherited selectively: known credential and
/// Fleet/upstream control variables are stripped so the OpenCode process cannot
/// read the host's GitHub/Fleet/cliproxy credentials.
/// </summary>
public static class ChildEnvironment
{
    // Credential/control families are matched by prefix (case-insensitively) so
    // a newly added token in a known family is stripped without a code change.
    // Config locations are included so a child cannot re-open the host's
    // OpenCode or XDG configuration; callers re-add the private locations they
    // own through overrides.
    private static readonly string[] DeniedPrefixes =
    [
        // Upstream/control plane and harness runtime.
        "FLEET_",
        "CLIPROXY_",
        "HERDR_",
        "Control__",
        "CONTROL__",
        // OpenCode harness configuration and server credentials.
        "OPENCODE_",
        // Source-control credentials.
        "GITHUB_",
        "GH_",
        "GIT_",
        // Model provider credentials.
        "ANTHROPIC_",
        "OPENAI_",
        "GEMINI_",
        "GOOGLE_",
        "AZURE_",
        "AWS_",
        "GCP_",
        "HF_",
        "HUGGINGFACE_",
        "MISTRAL_",
        "GROQ_",
        "OPENROUTER_",
        "XAI_",
        "DEEPSEEK_",
        "PERPLEXITY_",
        "COHERE_",
        "REPLICATE_",
        "TOGETHER_",
        "FIREWORKS_",
        "CEREBRAS_",
        "SAMBANOVA_",
        // Package-registry and infrastructure credentials.
        "NPM_",
        "NODE_AUTH_",
        "YARN_NPM_AUTH",
        "PIP_",
        "PYPI_",
        "TWINE_",
        "CARGO_REGISTRIES_",
        "NUGET_",
        "DOCKER_",
        "KUBE",
        "VAULT_",
        "CONSUL_",
        "NOMAD_",
        "SSH_",
    ];

    private static readonly HashSet<string> DeniedExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "OPENCODE",
        "OPENCODE_PID",
        "NETRC",
        "XDG_CONFIG_HOME",
        "XDG_DATA_HOME",
        "XDG_STATE_HOME",
        "XDG_CACHE_HOME",
    };

    /// <summary>
    /// Returns a filtered copy of <paramref name="baseEnvironment"/> (defaults to
    /// the current process environment) with <paramref name="overrides"/> applied last.
    /// </summary>
    public static Dictionary<string, string> Build(
        IReadOnlyDictionary<string, string>? overrides = null,
        IReadOnlyDictionary<string, string?>? baseEnvironment = null)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, value) in baseEnvironment ?? CurrentEnvironment())
        {
            if (value is null || IsDenied(key))
            {
                continue;
            }

            result[key] = value;
        }

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                result[key] = value;
            }
        }

        return result;
    }

    public static bool IsDenied(string key)
    {
        if (DeniedExact.Contains(key))
        {
            return true;
        }

        foreach (var prefix in DeniedPrefixes)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, string?> CurrentEnvironment()
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key)
            {
                result[key] = entry.Value as string;
            }
        }

        return result;
    }
}
