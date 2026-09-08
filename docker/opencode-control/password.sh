# Sourced by entrypoint and health check. Never echo credentials or enable shell tracing.
password_file=${OPENCODE_SERVER_PASSWORD_FILE:-/run/secrets/opencode-server-password}
if [ ! -r "$password_file" ]; then
    echo 'OpenCode server password file is missing or unreadable.' >&2
    exit 1
fi
password=$(cat "$password_file")
case "$password" in
    ''|*[!a-zA-Z0-9_+/=-]*)
        echo 'OpenCode server password must be a single base64/base64url or hexadecimal value.' >&2
        exit 1
        ;;
esac
if [ "${#password}" -lt 32 ] || [ "${#password}" -gt 256 ]; then
    echo 'OpenCode server password must contain 32 to 256 characters.' >&2
    exit 1
fi
