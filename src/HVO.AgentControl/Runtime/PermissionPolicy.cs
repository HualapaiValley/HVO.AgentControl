using System.Text.Json;

namespace HVO.AgentControl.Runtime;

/// <summary>
/// Conservative permission handling for inbound ACP permission requests. The
/// pinned OpenCode 1.18.30 request exposes a model-facing title and tool kind,
/// but no host-derived canonical path/resource. Phase 1 therefore never selects
/// an allow option; the title is retained only as untrusted, bounded audit input.
/// </summary>
public static class PermissionPolicy
{
    public const string UnrecognizedTool = "unrecognized";
    public const string UntrustedResource = "acp:untrusted-claim";

    /// <summary>Returns the ACP result payload rejecting, or safely cancelling, the request.</summary>
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
    /// Extracts bounded audit metadata without treating the model-authored title
    /// as an authorization resource. The returned resource is a fixed host value.
    /// </summary>
    public static bool TryParseAuditClaim(JsonElement? parameters, out string tool, out string resource)
    {
        tool = UnrecognizedTool;
        resource = UntrustedResource;
        if (parameters is not { } root
            || root.ValueKind != JsonValueKind.Object
            || root.GetRawText().Length > 16 * 1024
            || !root.TryGetProperty("toolCall", out var call)
            || call.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var kind = ReadBounded(call, "kind") ?? ReadBounded(call, "tool");
        if (kind is null || kind.Length > 128)
        {
            return false;
        }

        tool = kind;
        return true;
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

            var optionId = ReadBounded(option, "optionId");
            var kind = ReadBounded(option, "kind");
            var name = ReadBounded(option, "name");
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

    private static string? ReadBounded(JsonElement value, string property)
    {
        return value.TryGetProperty(property, out var element)
            && element.ValueKind == JsonValueKind.String
            && element.GetString() is { Length: > 0 and <= 512 } text
            && !text.Any(char.IsControl)
                ? text
                : null;
    }
}
