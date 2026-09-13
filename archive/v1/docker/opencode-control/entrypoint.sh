#!/bin/sh
set -eu
umask 077
. /usr/local/lib/opencode-password.sh
# The lock belongs to the open file description and survives exec. Keep this
# descriptor open in the native process; never delete/replace the lock file.
exec 9>/var/lib/opencode/.agentcontrol-service.lock
if ! flock -n 9; then
    echo 'OpenCode control state is already owned by another process.' >&2
    exit 1
fi
. /usr/local/lib/opencode-identity.sh
export OPENCODE_SERVER_USERNAME=opencode
export OPENCODE_SERVER_PASSWORD="$password"
unset password
exec /usr/local/bin/opencode "$@"
