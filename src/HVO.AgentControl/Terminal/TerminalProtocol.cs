using System.Text.Json;

namespace HVO.AgentControl.Terminal;

/// <summary>
/// The kind of a controller-to-bridge terminal command.
/// </summary>
public enum TerminalFrameKind
{
    Input,
    Resize,
}

/// <summary>
/// A validated, normalized browser terminal command.
/// </summary>
public readonly record struct TerminalFrame(TerminalFrameKind Kind, string Data, int Columns, int Rows);

/// <summary>
/// Pure validation and normalization rules shared by the websocket endpoint and
/// its tests. Keeping these decisions in one place lets the security-relevant
/// checks be exercised without a live socket or subprocess.
/// </summary>
public static class TerminalProtocol
{
    public const int MinColumns = 20;
    public const int MaxColumns = 300;
    public const int MinRows = 5;
    public const int MaxRows = 100;

    /// <summary>Maximum accepted size of one browser websocket text message.</summary>
    public const int MaxClientMessageBytes = 64 * 1024;

    /// <summary>Maximum accepted characters in one <c>input</c> command's data.</summary>
    public const int MaxInputDataCharacters = 16 * 1024;

    /// <summary>Maximum accepted length of a configured tmux session name.</summary>
    public const int MaxSessionNameLength = 64;

    /// <summary>
    /// Clamps terminal dimensions into the supported range. Invalid values are
    /// clamped rather than rejected so a stale resize cannot close a session.
    /// </summary>
    public static (int Columns, int Rows) ClampSize(int columns, int rows)
    {
        return (Math.Clamp(columns, MinColumns, MaxColumns), Math.Clamp(rows, MinRows, MaxRows));
    }

    /// <summary>
    /// Returns <see langword="true"/> only when the request <c>Origin</c> has the
    /// same scheme and authority as the request itself. This blocks cross-site
    /// websocket hijacking (CSWSH); authentication remains the host's job.
    /// </summary>
    public static bool IsSameOrigin(string? originHeader, string? requestScheme, string? requestHost)
    {
        if (string.IsNullOrWhiteSpace(originHeader) || string.IsNullOrWhiteSpace(requestScheme) || string.IsNullOrWhiteSpace(requestHost))
        {
            return false;
        }

        if (!Uri.TryCreate(originHeader, UriKind.Absolute, out var origin))
        {
            return false;
        }

        if (origin.Scheme is not ("http" or "https"))
        {
            return false;
        }

        return string.Equals(origin.Scheme, requestScheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(origin.Authority, requestHost, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates a configured tmux session name. Only characters that cannot
    /// change tmux target syntax are accepted, so a missing or malformed name
    /// fails closed instead of attaching to an unintended target or shell.
    /// </summary>
    public static bool IsValidSessionName(string? sessionName)
    {
        if (string.IsNullOrWhiteSpace(sessionName) || sessionName.Length > MaxSessionNameLength)
        {
            return false;
        }

        foreach (var character in sessionName)
        {
            var valid = character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '_'
                or '-';
            if (!valid)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Parses and validates one newline-delimited browser command. Returns
    /// <see langword="false"/> for anything outside the fixed schema so callers
    /// can fail closed without propagating attacker-controlled text.
    /// </summary>
    public static bool TryParseClientFrame(string? json, out TerminalFrame frame)
    {
        frame = default;
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxClientMessageBytes)
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            switch (typeElement.GetString())
            {
                case "input":
                    if (!root.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.String)
                    {
                        return false;
                    }

                    var data = dataElement.GetString() ?? string.Empty;
                    if (data.Length > MaxInputDataCharacters)
                    {
                        return false;
                    }

                    frame = new TerminalFrame(TerminalFrameKind.Input, data, 0, 0);
                    return true;

                case "resize":
                    if (!root.TryGetProperty("cols", out var columnsElement)
                        || columnsElement.ValueKind != JsonValueKind.Number
                        || !columnsElement.TryGetInt32(out var columns)
                        || !root.TryGetProperty("rows", out var rowsElement)
                        || rowsElement.ValueKind != JsonValueKind.Number
                        || !rowsElement.TryGetInt32(out var rows))
                    {
                        return false;
                    }

                    var (clampedColumns, clampedRows) = ClampSize(columns, rows);
                    frame = new TerminalFrame(TerminalFrameKind.Resize, string.Empty, clampedColumns, clampedRows);
                    return true;

                default:
                    return false;
            }
        }
    }
}
