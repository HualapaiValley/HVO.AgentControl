namespace HVO.AgentControl.Runtime;

/// <summary>
/// Builds the child process environment for the control runtime.
/// The parent environment is inherited selectively: known credential and
/// Fleet/upstream control variables are stripped so the OpenCode process cannot
/// read the host's GitHub/Fleet/cliproxy credentials.
/// </summary>
public static class ChildEnvironment
{
    private static readonly string[] DeniedPrefixes =
    [
        "FLEET_",
        "CLIPROXY_",
        "HERDR_",
        "OPENCODE_CONFIG",
        "OPENCODE_SERVER_",
        "GITHUB_",
        "GH_",
        "Control__",
        "CONTROL__",
    ];

    private static readonly HashSet<string> DeniedExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "OPENCODE",
        "OPENCODE_PID",
        "GH_TOKEN",
        "GITHUB_TOKEN",
        "GITHUB_APP_ID",
        "GITHUB_APP_PRIVATE_KEY",
        "GITHUB_APP_INSTALLATION_ID",
        "GITHUB_WEBHOOK_SECRET",
        "AWS_ACCESS_KEY_ID",
        "AWS_SECRET_ACCESS_KEY",
        "AWS_SESSION_TOKEN",
        "AZURE_CLIENT_SECRET",
        "GOOGLE_APPLICATION_CREDENTIALS",
        "NPM_TOKEN",
        "NODE_AUTH_TOKEN",
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
