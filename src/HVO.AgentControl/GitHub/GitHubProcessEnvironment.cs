using System.Security.Cryptography;
using System.Text;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Ssh;

namespace HVO.AgentControl.GitHub;

internal sealed record GitHubProcessEnvironmentEvidence(
    string Status,
    string Platform,
    int? ProcessId,
    string ProcessIncarnation,
    long ObservedAt,
    int PolicyVersion,
    string PolicyFingerprint,
    string CredentialConfigurationFingerprint)
{
    public string State => Status == GitHubProcessEnvironment.Ready ? "Ready" :
        Status is GitHubProcessEnvironment.ConfigMissing or GitHubProcessEnvironment.ConfigMismatch or GitHubProcessEnvironment.TokenOverride
            ? "MigrationRequired" : "Blocked";

    public string Detail => Status switch
    {
        GitHubProcessEnvironment.Ready => "Managed GitHub CLI credential is stored and effective in the exact owned server process; renewal is automatic while connected.",
        GitHubProcessEnvironment.ConfigMissing => "Credential is stored, but the running owned server does not use the managed GH_CONFIG_DIR. Drain its work, stop the owned server, then reconnect it; AgentControl will not restart it automatically.",
        GitHubProcessEnvironment.ConfigMismatch => "Credential is stored, but the running owned server uses a different GH_CONFIG_DIR. Drain its work, correct the launch environment, stop the owned server, then reconnect it; AgentControl will not restart it automatically.",
        GitHubProcessEnvironment.TokenOverride => "Credential is stored, but the running owned server has a GH_TOKEN or GITHUB_TOKEN override. Drain its work, remove the override, stop the owned server, then reconnect it; AgentControl will not restart it automatically.",
        GitHubProcessEnvironment.CredentialMismatch => "The AgentControl-owned GitHub CLI configuration changed after credential delivery. Access is not ready, and AgentControl will not overwrite unverified contents.",
        GitHubProcessEnvironment.Unsupported => "Credential is stored, but effective process-environment verification is unsupported on this platform; GitHub access cannot be marked ready.",
        _ => "Credential is stored, but the exact owned server process environment could not be verified; GitHub access is not ready."
    };
}

internal static class GitHubProcessEnvironment
{
    public const int CurrentPolicyVersion = 1;
    internal const long RecheckMilliseconds = 120000;
    internal const string Ready = "Ready", ConfigMissing = "ConfigMissing", ConfigMismatch = "ConfigMismatch",
        TokenOverride = "TokenOverride", CredentialMismatch = "CredentialMismatch",
        Unsupported = "Unsupported", Unavailable = "Unavailable";

    public static string CredentialFingerprint(string contents) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contents))).ToLowerInvariant();

    public static string Fingerprint(RuntimeRecord runtime)
    {
        var value = string.Join('\n', CurrentPolicyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            runtime.ConnectionKind, runtime.ManagedServerId, runtime.ApiPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            BootstrapScript.ManagedGitHubConfigDirectory(runtime));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    public static bool MustVerify(GitHubAccess grant, RuntimeRecord runtime, long now) =>
        grant.State != "Ready" || grant.EnvironmentPolicyVersion != CurrentPolicyVersion ||
        grant.EnvironmentPolicyFingerprint != Fingerprint(runtime) || grant.EnvironmentProcessId is null or < 1 ||
        !NativeProcessProbe.ValidMarker(grant.EnvironmentProcessIncarnation) ||
        grant.EnvironmentVerifiedAt is null || grant.EnvironmentVerifiedAt < now - RecheckMilliseconds;

    public static bool HasRecordedEvidence(GitHubAccess grant) =>
        grant.EnvironmentPolicyVersion == CurrentPolicyVersion && ValidFingerprint(grant.EnvironmentPolicyFingerprint) &&
        grant.EnvironmentProcessId > 0 && NativeProcessProbe.ValidMarker(grant.EnvironmentProcessIncarnation) &&
        grant.EnvironmentVerifiedAt > 0;

    public static bool HasCurrentEvidence(GitHubAccess grant, long now, RuntimeRecord? runtime = null,
        NativeProcessObservationEvidence? native = null) =>
        grant.CredentialState == GitHubCredentialState.Delivered && grant.ExpiresAt > now &&
        ValidFingerprint(grant.CredentialConfigurationFingerprint) &&
        HasRecordedEvidence(grant) && grant.EnvironmentVerifiedAt >= now - RecheckMilliseconds &&
        (runtime is null || grant.EnvironmentPolicyFingerprint == Fingerprint(runtime)) &&
        !NewerNativeProcessContradicts(grant, native);

    public static bool NewerNativeProcessContradicts(GitHubAccess grant, NativeProcessObservationEvidence? native) =>
        NewerNativeProcessContradicts(grant.EnvironmentProcessId, grant.EnvironmentProcessIncarnation,
            grant.EnvironmentVerifiedAt, native);

    public static bool NewerNativeProcessContradicts(int? processId, string processIncarnation, long? verifiedAt,
        NativeProcessObservationEvidence? native) => native is not null && native.ObservedAt >= verifiedAt &&
        (native.State != NativeProcessObservationState.Observed || native.ProcessId != processId ||
         native.Incarnation != processIncarnation);

    private static bool ValidFingerprint(string value) => value.Length == 64 &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string Script(RuntimeRecord runtime, string credentialConfigurationFingerprint)
    {
        var state = BootstrapScript.Quote(runtime.StateDirectory);
        var config = BootstrapScript.Quote(BootstrapScript.ManagedGitHubConfigDirectory(runtime));
        var owner = BootstrapScript.Quote(runtime.ManagedServerId);
        var ownerAndPort = BootstrapScript.Quote(runtime.ManagedServerId + ":" + runtime.ApiPort);
        var socket = BootstrapScript.Quote(runtime.TmuxName);
        var credentialFingerprint = BootstrapScript.Quote(credentialConfigurationFingerprint);
        return $$"""
            state={{state}}
            config={{config}}
            test ! -L "$state" && test "$(cd "$state" && pwd -P)" = "$state" || exit 33
            test ! -L "$state/owner" && test "$(cat "$state/owner")" = {{ownerAndPort}} || exit 33
            test ! -L "$config" && test "$(cd "$config" && pwd -P)" = "$config" || exit 33
            test ! -L "$config/hosts.yml" && test -f "$config/hosts.yml" || exit 33
            test ! -L "$config/.agentcontrol-owner" && test "$(cat "$config/.agentcontrol-owner")" = {{owner}} || exit 33
            test "$(tmux -L {{socket}} show-option -v -t managed @hvo-owner)" = {{owner}} || exit 33
            pane=$(tmux -L {{socket}} display-message -p -t managed '#{pane_pid} #{pane_dead}') || exit 33
            set -- $pane
            test "$#" = 2 && test "$2" = 0 || exit 33
            pid=$1
            case "$pid" in ''|*[!0-9]*) exit 33;; esac
            if test "$(uname -s)" != Linux; then
              printf 'GITHUB_ENVIRONMENT\tUnsupported\tUnsupported\t\t\n'
              exit 0
            fi
            live_marker() {
              test -r "/proc/$pid/stat" && test -r /proc/sys/kernel/random/boot_id && kill -0 "$pid" || return 33
              stat=$(cat "/proc/$pid/stat") || return 33
              rest=${stat##*) }; index=1; start=''
              process_state=${rest%% *}
              test "$process_state" != Z && test "$process_state" != X || return 33
              for field in $rest; do [ "$index" = 20 ] && start=$field; index=$((index+1)); done
              case "$start" in ''|*[!0-9]*) return 33;; esac
              boot=$(cat /proc/sys/kernel/random/boot_id) || return 33
              printf '%s:%s' "$boot" "$start"
            }
            marker=$(live_marker) || exit 33
            listeners=$(lsof -nP -t -iTCP:{{runtime.ApiPort}} -sTCP:LISTEN || true)
            test "$listeners" = "$pid" && test -r "/proc/$pid/environ" || exit 33
            if ! printf '\0' | grep -zq '^$'; then
              printf 'GITHUB_ENVIRONMENT\tUnavailable\tLinux\t%s\t%s\n' "$pid" "$marker"
              exit 0
            fi
            command -v sha256sum >/dev/null || { printf 'GITHUB_ENVIRONMENT\tUnavailable\tLinux\t%s\t%s\n' "$pid" "$marker"; exit 0; }
            configuration_fingerprint() {
              output=$(sha256sum < "$config/hosts.yml") || return 33
              set -- $output
              test "$#" = 2 && test "$2" = - || return 33
              case "$1" in *[!0-9a-f]*|'') return 33;; esac
              test "${#1}" = 64 || return 33
              printf '%s' "$1"
            }
            configuration=$(configuration_fingerprint) || exit 33
            if test "$configuration" != {{credentialFingerprint}}; then
              status=CredentialMismatch
            elif grep -zEq '^(GH_TOKEN|GITHUB_TOKEN)=.' "/proc/$pid/environ"; then
              status=TokenOverride
            elif grep -zFqx {{BootstrapScript.Quote("GH_CONFIG_DIR=" + BootstrapScript.ManagedGitHubConfigDirectory(runtime))}} "/proc/$pid/environ"; then
              status=Ready
            elif grep -zq '^GH_CONFIG_DIR=' "/proc/$pid/environ"; then
              status=ConfigMismatch
            else
              status=ConfigMissing
            fi
            test "$(tmux -L {{socket}} display-message -p -t managed '#{pane_pid} #{pane_dead}')" = "$pane" || exit 33
            test "$(tmux -L {{socket}} show-option -v -t managed @hvo-owner)" = {{owner}} || exit 33
            current=$(live_marker) && test "$current" = "$marker" || exit 33
            test "$(lsof -nP -t -iTCP:{{runtime.ApiPort}} -sTCP:LISTEN || true)" = "$pid" || exit 33
            current_configuration=$(configuration_fingerprint) && test "$current_configuration" = "$configuration" || exit 33
            printf 'GITHUB_ENVIRONMENT\t%s\tLinux\t%s\t%s\n' "$status" "$pid" "$marker"
            """;
    }

    public static GitHubProcessEnvironmentEvidence Parse(RuntimeRecord runtime, string output, long observedAt,
        string credentialConfigurationFingerprint)
    {
        try
        {
            var lines = output.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var fields = lines.Single().Split('\t');
            if (fields.Length != 5 || fields[0] != "GITHUB_ENVIRONMENT" ||
                fields[1] is not (Ready or ConfigMissing or ConfigMismatch or TokenOverride or CredentialMismatch or Unsupported or Unavailable)) throw new FormatException();
            int? processId = null;
            if (fields[3].Length > 0)
            {
                if (!int.TryParse(fields[3], out var parsed) || parsed < 1) throw new FormatException();
                processId = parsed;
            }
            var hasProcess = processId > 0 && NativeProcessProbe.ValidMarker(fields[4]);
            if (fields[1] == Unsupported ? processId is not null || fields[4].Length > 0 : !hasProcess || fields[2] != "Linux")
                throw new FormatException();
            return new(fields[1], fields[2], processId, fields[4], observedAt, CurrentPolicyVersion, Fingerprint(runtime),
                credentialConfigurationFingerprint);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            return new(Unavailable, "Unknown", null, "", observedAt, CurrentPolicyVersion, Fingerprint(runtime),
                credentialConfigurationFingerprint);
        }
    }
}
