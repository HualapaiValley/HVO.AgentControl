using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;

namespace HVO.AgentControl.Ssh;

public static class BootstrapScript
{
    public const string Version = "1.18.29";

    public static string ManagedGitHubConfigDirectory(RuntimeRecord runtime)
    {
        ControlStore.ValidatePath(runtime.StateDirectory);
        var state = runtime.StateDirectory.TrimEnd('/');
        if (state.Length == 0) throw new ControlException("Bootstrap state must not be the filesystem root.", 400);
        return state + "/gh-config";
    }

    public static string ServerEnvironment(RuntimeRecord runtime, string password) =>
        "export OPENCODE_SERVER_USERNAME=opencode\n" +
        "export OPENCODE_SERVER_PASSWORD=" + Quote(password) + "\n" +
        "export GH_CONFIG_DIR=" + Quote(ManagedGitHubConfigDirectory(runtime)) + "\n";

    public static string Quote(string value)
    {
        if (value.Contains('\0')) throw new ControlException("NUL is not a shell argument.", 400);
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    public static string Create(RuntimeRecord runtime) => $$"""
        #!/bin/sh
        set -eu
        {{RemoteEnvironment.Prelude}}
        umask 077
        state={{Quote(runtime.StateDirectory)}}
        owner={{Quote(runtime.ManagedServerId)}}
        socket={{Quote(runtime.TmuxName)}}
        port={{runtime.ApiPort}}
        configured={{Quote(runtime.Executable)}}
        install={{(runtime.InstallIfMissing ? "yes" : "no")}}
        startup={{Quote(StartupOptions.Fingerprint(runtime))}}
        cd "$state"
        observation=native-process-current
        replacements=native-process-replacements
        observe_native() {
          os=$(uname -s)
          pid=$(tmux -L "$socket" display-message -p -t managed '#{pane_pid}' 2>/dev/null || true)
          case "$pid" in ''|*[!0-9]*) printf 'Unavailable\t%s\t\t\n' "$os"; return ;; esac
          if [ "$os" != Linux ]; then printf 'Unsupported\t%s\t%s\t\n' "$os" "$pid"; return; fi
          if [ ! -r "/proc/$pid/stat" ] || [ ! -r /proc/sys/kernel/random/boot_id ]; then printf 'Unavailable\tLinux\t%s\t\n' "$pid"; return; fi
          stat=$(cat "/proc/$pid/stat")
          rest=${stat##*) }
          index=1; start=''
          for field in $rest; do [ "$index" = 20 ] && start=$field; index=$((index+1)); done
          boot=$(cat /proc/sys/kernel/random/boot_id)
          case "$start:$boot" in :*|*[!0-9a-fA-F:-]*) printf 'Unavailable\tLinux\t%s\t\n' "$pid"; return ;; esac
          printf 'Observed\tLinux\t%s\t%s:%s\n' "$pid" "$boot" "$start"
        }
        remember_observation() {
          printf '%s\n' "$1" > "$observation.new"
          mv "$observation.new" "$observation"
        }
        remember_replacement() {
          old_state=$(printf '%s' "$1" | cut -f 1)
          old_pid=$(printf '%s' "$1" | cut -f 3)
          old_marker=$(printf '%s' "$1" | cut -f 4)
          new_state=$(printf '%s' "$2" | cut -f 1)
          new_pid=$(printf '%s' "$2" | cut -f 3)
          new_marker=$(printf '%s' "$2" | cut -f 4)
          [ "$old_state" = Observed ] && [ "$new_state" = Observed ] &&
            { [ "$old_pid" != "$new_pid" ] || [ "$old_marker" != "$new_marker" ]; } || return 0
          printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\tUnknown\n' "$old_pid" "$old_marker" "$new_pid" "$new_marker" "$3" "$4" "$5" >> "$replacements"
          tail -n 32 "$replacements" > "$replacements.new"
          mv "$replacements.new" "$replacements"
        }
        for utility in tmux lsof curl; do command -v "$utility" >/dev/null 2>&1 || { echo "PREREQUISITE:$utility"; exit 31; }; done
        count=0
        until mkdir bootstrap.lock 2>/dev/null; do
          count=$((count+1)); [ "$count" -lt 30 ] || { echo 'BOOTSTRAP_LOCKED'; exit 32; }; sleep 1
        done
        trap 'rmdir bootstrap.lock' EXIT
        if [ -f owner ] && [ "$(cat owner)" != "$owner:$port" ]; then echo 'OWNERSHIP_CONFLICT'; exit 33; fi
        previous=$(cat "$observation" 2>/dev/null || printf 'Unknown\tUnknown\t\t\n')
        dead_evidence=Unknown
        dead_code=''
        if tmux -L "$socket" has-session -t managed 2>/dev/null; then
          [ -f owner ] && [ "$(tmux -L "$socket" show-option -v -t managed @hvo-owner)" = "$owner" ] || { echo 'OWNERSHIP_CONFLICT'; exit 33; }
          dead=$(tmux -L "$socket" display-message -p -t managed '#{pane_dead}')
          if [ "$dead" != 1 ]; then
            previous_startup=$(cat startup-options 2>/dev/null || printf '{{StartupOptions.Fingerprint(new RuntimeRecord())}}')
            [ "$previous_startup" = "$startup" ] || { echo 'STARTUP_OPTIONS_CHANGED_STOP_REQUIRED'; exit 42; }
            pid=$(tmux -L "$socket" display-message -p -t managed '#{pane_pid}')
            listeners=$(lsof -nP -t -iTCP:"$port" -sTCP:LISTEN || true)
            [ "$listeners" = "$pid" ] || { echo 'LIVE_PROCESS_UNHEALTHY_OR_PORT_CONFLICT'; exit 34; }
            current=$(observe_native)
            remember_replacement "$previous" "$current" IncarnationChanged Unknown ''
            remember_observation "$current"
            printf 'READY\n'; uname -sm
            exit 0
          fi
          signal=$(tmux -L "$socket" display-message -p -t managed '#{pane_dead_signal}' 2>/dev/null || true)
          status=$(tmux -L "$socket" display-message -p -t managed '#{pane_dead_status}' 2>/dev/null || true)
          case "$signal" in ''|0|*[!0-9]*) ;; *) dead_evidence=Signal; dead_code=$signal ;; esac
          if [ "$dead_evidence" = Unknown ]; then case "$status" in ''|*[!0-9]*) ;; *) dead_evidence=ExitStatus; dead_code=$status ;; esac; fi
        fi
        [ -z "$(lsof -nP -t -iTCP:"$port" -sTCP:LISTEN || true)" ] || { echo 'PORT_CONFLICT'; exit 35; }
        binary="$configured"
        if [ -z "$binary" ]; then
          for candidate in "$state/bin/opencode" "$HOME/.opencode/bin/opencode" "$HOME/.local/bin/opencode" /opt/homebrew/bin/opencode /usr/local/bin/opencode; do
            if [ -x "$candidate" ]; then binary="$candidate"; break; fi
          done
          if [ -z "$binary" ]; then binary=$(command -v opencode || true); fi
        fi
        if [ -z "$binary" ] || [ ! -x "$binary" ]; then
          [ "$install" = yes ] && [ -z "$configured" ] || { echo 'OPENCODE_MISSING'; exit 36; }
          case "$(uname -s):$(uname -m)" in
            Linux:x86_64) asset=opencode-linux-x64-baseline.tar.gz; digest=03a3f2f063e23477e3e4c3a738eb389f56c5a6ecf54d6a5a6d91caab557f042d ;;
            Linux:aarch64|Linux:arm64) asset=opencode-linux-arm64.tar.gz; digest=70baf769395ca4e7a68924026530c390eace194f3b7e4919d4efcb2aa2eed3c0 ;;
            Darwin:arm64) asset=opencode-darwin-arm64.zip; digest=fe764f7f360c584a83e18dd5f23fb1a6b2725f5ee8854b0252fe558f7798e946 ;;
            Darwin:x86_64) asset=opencode-darwin-x64-baseline.zip; digest=bb2e943a0c372aa3001e5f50ad7beb79ef7f4734b7e7b4f70fe02a8b6caa43b2 ;;
            *) echo 'UNSUPPORTED_PLATFORM'; exit 37 ;;
          esac
          mkdir -p bin download
          curl --fail --silent --show-error --location --connect-timeout 20 --max-time 180 "https://github.com/anomalyco/opencode/releases/download/v{{Version}}/$asset" -o "download/$asset"
          if command -v sha256sum >/dev/null; then actual=$(sha256sum "download/$asset" | cut -d ' ' -f 1); else actual=$(shasum -a 256 "download/$asset" | cut -d ' ' -f 1); fi
          [ "$actual" = "$digest" ] || { echo 'DOWNLOAD_DIGEST_MISMATCH'; exit 38; }
          case "$asset" in *.zip) unzip -oq "download/$asset" -d bin ;; *) tar -xzf "download/$asset" -C bin ;; esac
          binary="$state/bin/opencode"
          chmod 700 "$binary"
          rm "download/$asset"
        fi
        version=$("$binary" --version)
        [ "$version" = '{{Version}}' ] || { echo 'INCOMPATIBLE_VERSION'; exit 39; }
        printf '%s' "$owner:$port" > owner
        # Paths are transferred as quoted configuration, never prompt text.
        printf '%s' "$binary" > executable
        printf '%s' "$startup" > startup-options
        if [ -f server.log ]; then tail -c 65536 server.log > previous.log; fi
        retries=$(cat restarts 2>/dev/null || printf 0)
        [ "$retries" -lt 3 ] || { echo 'RESTART_LIMIT'; exit 40; }
        printf '%s' "$((retries+1))" > restarts
        # The launcher reads credentials from a mode-600 file; no secret is in a process argument.
        if tmux -L "$socket" has-session -t managed 2>/dev/null; then
          tmux -L "$socket" respawn-pane -t managed /bin/sh "$state/launch.sh"
        else
          tmux -L "$socket" new-session -d -s managed /bin/sh "$state/launch.sh"
          tmux -L "$socket" set-option -t managed @hvo-owner "$owner"
          tmux -L "$socket" set-option -t managed remain-on-exit on
        fi
        for attempt in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15; do
          pid=$(tmux -L "$socket" display-message -p -t managed '#{pane_pid}')
          listeners=$(lsof -nP -t -iTCP:"$port" -sTCP:LISTEN || true)
          if [ "$listeners" = "$pid" ]; then
            current=$(observe_native)
            remember_replacement "$previous" "$current" DeadPaneRespawn "$dead_evidence" "$dead_code"
            remember_observation "$current"
            printf 'READY\n'; uname -sm; exit 0
          fi
          sleep 1
        done
        echo 'SERVER_START_FAILED'; exit 41
        """;

    public static string Launcher(RuntimeRecord runtime) => $$"""
        #!/bin/sh
        set -eu
        {{RemoteEnvironment.Prelude}}
        umask 077
        cd {{Quote(runtime.StateDirectory)}}
        . ./server.env
        binary=$(cat executable)
        exec "$binary" serve --hostname 127.0.0.1 --port {{runtime.ApiPort}}{{StartupOptions.Arguments(runtime)}} > server.log 2>&1
        """;
}
