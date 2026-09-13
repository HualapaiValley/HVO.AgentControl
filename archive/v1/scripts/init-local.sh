#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
umask 077
mkdir -p .secrets data
chmod 700 .secrets data
for name in owner-password server-password; do
  if [[ ! -f .secrets/$name ]]; then head -c 32 /dev/urandom | base64 > ".secrets/$name"; fi
done
printf 'Created missing local secrets without replacing existing credentials. Read .secrets/owner-password for sign-in.\n'
