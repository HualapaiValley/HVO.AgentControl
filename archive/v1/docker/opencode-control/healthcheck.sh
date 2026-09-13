#!/bin/sh
set -eu
. /usr/local/lib/opencode-password.sh
# Feed authentication over stdin, not process arguments, Docker metadata or health logs.
authorization=$(printf 'opencode:%s' "$password" | base64 | tr -d '\n')
printf 'header = "Authorization: Basic %s"\n' "$authorization" |
    curl --config - --fail --silent --max-time 4 --output /dev/null http://127.0.0.1:4096/global/health
