namespace HVO.AgentControl.Ssh;

public static class RemoteEnvironment
{
    // SSH exec channels on macOS commonly omit Homebrew even when an interactive terminal includes it.
    // Preserve explicitly configured precedence, append only existing standard directories, and never source dotfiles.
    public const string Prelude = """
        PATH="${PATH:-/usr/bin:/bin:/usr/sbin:/sbin}"
        if [ "$(/usr/bin/uname -s 2>/dev/null)" = Darwin ]; then
          case "$(/usr/bin/uname -m 2>/dev/null)" in
            arm64|aarch64) hvo_tool_dirs='/opt/homebrew/bin /opt/homebrew/sbin /usr/local/bin /usr/local/sbin' ;;
            *) hvo_tool_dirs='/usr/local/bin /usr/local/sbin /opt/homebrew/bin /opt/homebrew/sbin' ;;
          esac
          for hvo_tool_dir in $hvo_tool_dirs; do
            if [ -d "$hvo_tool_dir" ]; then
              case ":$PATH:" in
                *":$hvo_tool_dir:"*) ;;
                *) PATH="$PATH:$hvo_tool_dir" ;;
              esac
            fi
          done
          unset hvo_tool_dirs hvo_tool_dir
        fi
        export PATH
        """;

    // The remote account may use zsh/fish; all of our command bodies are POSIX shell scripts.
    public static string Command(string script) => "/bin/sh -c " + BootstrapScript.Quote(Prelude + "\n" + script);
}
