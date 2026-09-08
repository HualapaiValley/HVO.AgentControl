#!/bin/sh
set -eu
umask 077
. /usr/local/lib/opencode-password.sh
export OPENCODE_SERVER_USERNAME=opencode
export OPENCODE_SERVER_PASSWORD="$password"
unset password
exec /usr/local/bin/opencode "$@"
