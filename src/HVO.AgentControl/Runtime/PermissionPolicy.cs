using System.Text.Json;

namespace HVO.AgentControl.Runtime;

/// <summary>
/// Conservative permission handling for inbound ACP permission requests.
/// The generated OpenCode config already allows ordinary bash/read/edit use and
/// denies credential material, so any permission RPC that still arrives is
/// rejected rather than auto-approved. Unknown options are never selected.
/// </summary>
public static class PermissionPolicy
{
    /// <summary>Returns the ACP result payload rejecting (or cancelling) the request.</summary>
    public static object BuildRejection(JsonElement? parameters)
    {
        var optionId = SelectRejectOption(parameters);
        if (optionId is null)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["outcome"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["outcome"] = "cancelled",
                },
            };
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["outcome"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["outcome"] = "selected",
                ["optionId"] = optionId,
            },
        };
    }

    /// <summary>
    /// Picks a reject option if one exists. Prefers reject_once, then
    /// reject_always, then any option whose id/name mentions reject.
    /// </summary>
    public static string? SelectRejectOption(JsonElement? parameters)
    {
        if (parameters is not { } root
            || root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("options", out var options)
            || options.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? fallback = null;
        string? rejectAlways = null;

        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var optionId = option.TryGetProperty("optionId", out var idElement) ? idElement.GetString() : null;
            var kind = option.TryGetProperty("kind", out var kindElement) ? kindElement.GetString() : null;
            var name = option.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;

            if (string.Equals(kind, "reject_once", StringComparison.OrdinalIgnoreCase))
            {
                return optionId;
            }

            if (string.Equals(kind, "reject_always", StringComparison.OrdinalIgnoreCase))
            {
                rejectAlways ??= optionId;
                continue;
            }

            if (optionId is not null && optionId.Contains("reject", StringComparison.OrdinalIgnoreCase))
            {
                fallback ??= optionId;
            }
            else if (name is not null && name.Contains("reject", StringComparison.OrdinalIgnoreCase))
            {
                fallback ??= optionId;
            }
        }

        return rejectAlways ?? fallback;
    }
}
