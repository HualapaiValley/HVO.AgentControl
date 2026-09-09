using System.Text.Json;
using System.Text.RegularExpressions;

namespace HVO.AgentControl.GitHub;

// This is the deliberately narrow single-App profile emitted by AgentControl,
// not a general YAML parser. gh may remove quotes from safe scalar values when
// it reads/migrates its configuration; that does not change credential identity.
internal static class ManagedGitHubHosts
{
    private const string PlainActor = "^[A-Za-z0-9_-]+\\[bot\\]$";
    // Installation tokens can contain dot-separated segments. gh emits these as plain
    // YAML strings; dots after a letter/underscore do not introduce YAML scalar syntax.
    private const string PlainToken = "^[A-Za-z_][A-Za-z0-9_.-]*$";
    private const string ReservedToken = "^(null|true|false|yes|no|on|off)$";

    public static string Format(string actor, string token)
    {
        var actorYaml = JsonSerializer.Serialize(actor);
        var tokenYaml = JsonSerializer.Serialize(token);
        return $"github.com:\n    user: {actorYaml}\n    oauth_token: {tokenYaml}\n    git_protocol: https\n    users:\n        {actorYaml}:\n            oauth_token: {tokenYaml}\n";
    }

    public static bool TryCanonicalize(string hosts, string? expectedActor, out string canonical)
    {
        canonical = "";
        // Cover two 8192-character tokens and two 205-character actors,
        // including the writer's worst-case six-character JSON escaping.
        if (hosts.Length > 128 * 1024 || hosts.Contains('\r')) return false;
        var lines = hosts.Split('\n');
        if (lines.Length != 8 || lines[7].Length != 0 || lines[0] != "github.com:" ||
            lines[3] != "    git_protocol: https" || lines[4] != "    users:" ||
            !lines[1].StartsWith("    user: ", StringComparison.Ordinal) ||
            !lines[2].StartsWith("    oauth_token: ", StringComparison.Ordinal) ||
            !lines[5].StartsWith("        ", StringComparison.Ordinal) || !lines[5].EndsWith(':') ||
            !lines[6].StartsWith("            oauth_token: ", StringComparison.Ordinal)) return false;
        if (!TryScalar(lines[1][10..], true, out var actor) ||
            !TryScalar(lines[5][8..^1], true, out var user) || actor != user ||
            expectedActor is not null && actor != expectedActor ||
            !TryScalar(lines[2][17..], false, out var primaryToken) ||
            !TryScalar(lines[6][25..], false, out var userToken) || primaryToken != userToken) return false;
        canonical = Format(actor, primaryToken);
        return true;
    }

    private static bool TryScalar(string value, bool actor, out string scalar)
    {
        scalar = "";
        if (value.StartsWith('"'))
        {
            try
            {
                scalar = JsonSerializer.Deserialize<string>(value) ?? "";
                return scalar.Length > 0 && JsonSerializer.Serialize(scalar) == value;
            }
            catch (JsonException) { return false; }
        }
        if (!Regex.IsMatch(value, actor ? PlainActor : PlainToken, RegexOptions.CultureInvariant) ||
            !actor && Regex.IsMatch(value, ReservedToken, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        scalar = value;
        return true;
    }

    // Keep plain scalar rules in parity with TryScalar. This only quotes those
    // safe scalars; extra hosts/keys, changed tokens and unsupported forms still
    // change the canonical file hash. No Python/node/YAML dependency on workers.
    // The output contains credentials and must only feed the hash pipeline.
    internal const string FingerprintNormalizationScript = """
        if test "$(wc -l < "$config/hosts.yml")" -eq 7; then
        LC_ALL=C awk '
        function actor(v) { return v ~ /^[A-Za-z0-9_-]+\[bot\]$/ }
        function token(v) { return v ~ /^[A-Za-z_][A-Za-z0-9_.-]*$/ && tolower(v) !~ /^(null|true|false|yes|no|on|off)$/ }
        {
          line=$0
          if (line ~ /^    user: /) {
            value=substr(line, 11)
            if (actor(value)) line="    user: \"" value "\""
          }
          if (line ~ /^        .*:$/) {
            value=substr(line, 9, length(line)-9)
            if (actor(value)) line="        \"" value "\":"
          }
          if (match(line, /^(    |            )oauth_token: /)) {
            prefix=substr(line, 1, RLENGTH); value=substr(line, RLENGTH+1)
            if (token(value)) line=prefix "\"" value "\""
          }
          print line
        }' "$config/hosts.yml"
        else
          cat "$config/hosts.yml"
        fi
        """;
}
