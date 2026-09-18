using System.Text.Json;
using HVO.AgentControl.RemoteWorker;
using HVO.AgentControl.Worker;

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
    /// Selects the reject option from the exact offered option IDs, preferring the
    /// one-shot <c>reject_once</c>, then generic <c>reject</c>, then
    /// <c>reject_always</c>. IDs are compared with ordinal equality against the
    /// fixed reject allowlist, so <c>once</c>, <c>always</c>, <c>allow</c> and any
    /// other unknown or malicious name are never selected.
    /// </summary>
    /// <exception cref="WorkerPermissionOptionsUnsupportedException">
    /// No offered option ID is a known reject option.
    /// </exception>
    public static string SelectRejectPermissionOption(IReadOnlyList<string> optionIds)
    {
        ArgumentNullException.ThrowIfNull(optionIds);
        if (!WorkerProtocol.TrySelectRejectOption(optionIds, out var optionId))
        {
            throw new WorkerPermissionOptionsUnsupportedException();
        }

        return optionId!;
    }

    /// <summary>
    /// Picks a reject option from a local ACP frame using the shared full-object
    /// selector. Only exact fixed reject IDs are eligible, in one-shot-to-persistent
    /// priority. A missing kind permits compatibility matching; a present kind must
    /// be exactly compatible. Allow, unknown, malformed, and contradictory kinds
    /// make the option ineligible, and kind never authorizes a vendor ID.
    /// </summary>
    public static string? SelectRejectOption(JsonElement? parameters) =>
        parameters is { } root
            ? WorkerProtocol.SelectRejectOptionFromPermissionFrame(root, oneShotOnly: false)
            : null;

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
