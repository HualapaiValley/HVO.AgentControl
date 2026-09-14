using System.Text;
using System.Text.RegularExpressions;

namespace HVO.AgentControl.Runtime;

/// <summary>
/// Bounded, redacting sink for child stderr. Raw model/provider output is never
/// stored; known credential shapes and the ephemeral server password are removed
/// before anything is retained.
/// </summary>
public sealed partial class SanitizedLogBuffer
{
    private readonly object _gate = new();
    private readonly Queue<string> _lines = new();
    private readonly StringBuilder _partial = new();
    private readonly int _maxLines;
    private readonly int _maxLineLength;
    private readonly int _maxBytes;
    private int _bytes;
    private string[] _secrets = [];

    public SanitizedLogBuffer(int maxLines = 200, int maxLineLength = 2000, int maxBytes = 64 * 1024, string? secret = null)
    {
        _maxLines = Math.Max(1, maxLines);
        _maxLineLength = Math.Max(1, maxLineLength);
        _maxBytes = Math.Max(1, maxBytes);
        SetSecrets(secret is null ? [] : [secret]);
    }

    /// <summary>Replaces the complete set of literal secrets redacted from buffered text.</summary>
    public void SetSecrets(IEnumerable<string?> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        lock (_gate)
        {
            _secrets = secrets
                .Where(secret => !string.IsNullOrEmpty(secret))
                .Select(secret => secret!)
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(secret => secret.Length)
                .ToArray();
        }
    }

    /// <summary>Adds one literal secret without removing secrets already registered.</summary>
    public void AddSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return;
        }

        lock (_gate)
        {
            if (!_secrets.Contains(secret, StringComparer.Ordinal))
            {
                _secrets = _secrets.Append(secret).OrderByDescending(value => value.Length).ToArray();
            }
        }
    }

    /// <summary>Compatibility API: replaces the complete literal-secret set.</summary>
    public void SetSecret(string? secret) => SetSecrets(secret is null ? [] : [secret]);

    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (_gate)
        {
            _partial.Append(text);
            FlushCompleteLines();
            // Guard against an unterminated stream that never emits a newline.
            if (_partial.Length > _maxLineLength)
            {
                AddLine(_partial.ToString());
                _partial.Clear();
            }
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (_partial.Length > 0)
            {
                AddLine(_partial.ToString());
                _partial.Clear();
            }
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            FlushCompleteLines();
            return _lines.ToArray();
        }
    }

    public string SnapshotText(int maxCharacters = 2000)
    {
        var lines = Snapshot();
        var text = string.Join(Environment.NewLine, lines);
        return text.Length <= maxCharacters ? text : text[^maxCharacters..];
    }

    public string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var result = value;
        string[] secrets;
        lock (_gate)
        {
            secrets = _secrets;
        }

        foreach (var secret in secrets)
        {
            result = result.Replace(secret, "***", StringComparison.Ordinal);
        }

        result = BasicAuthRegex().Replace(result, "Basic ***");
        result = GitHubTokenRegex().Replace(result, "***");
        result = AwsKeyRegex().Replace(result, "***");
        result = KeyValueSecretRegex().Replace(result, "$1***");
        return result;
    }

    private void FlushCompleteLines()
    {
        while (true)
        {
            var index = -1;
            for (var i = 0; i < _partial.Length; i++)
            {
                if (_partial[i] == '\n')
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                break;
            }

            var line = _partial.ToString(0, index);
            _partial.Remove(0, index + 1);
            AddLine(line);
        }
    }

    private void AddLine(string line)
    {
        line = Redact(line).TrimEnd('\r');
        if (line.Length > _maxLineLength)
        {
            line = line[.._maxLineLength] + "...";
        }

        if (line.Length == 0)
        {
            return;
        }

        _lines.Enqueue(line);
        _bytes += line.Length + 1;
        while (_lines.Count > _maxLines || _bytes > _maxBytes)
        {
            var removed = _lines.Dequeue();
            _bytes -= removed.Length + 1;
        }
    }

    [GeneratedRegex(@"Basic\s+[A-Za-z0-9+/=]+", RegexOptions.IgnoreCase)]
    private static partial Regex BasicAuthRegex();

    [GeneratedRegex(@"(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})")]
    private static partial Regex GitHubTokenRegex();

    [GeneratedRegex(@"A(?:KIA|SIA)[0-9A-Z]{16}")]
    private static partial Regex AwsKeyRegex();

    [GeneratedRegex(@"(?i)(""(?:token|password|secret|authorization|api[_-]?key)""\s*[:=]\s*"")([^""]+)")]
    private static partial Regex KeyValueSecretRegex();
}
