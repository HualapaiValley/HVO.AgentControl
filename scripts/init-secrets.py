#!/usr/bin/env python3
"""Create an owner password on the selected Docker host without printing it."""
import argparse
import subprocess
import sys

VOLUME = "agentcontrol-v2-secrets"
TARGET = "/secrets/owner-password"

# Runs inside the secrets volume's container. It never prints the credential.
# A generated password is written to a private temporary file, assigned the
# final ownership and permissions, and only then hard-linked to the known path,
# so the known path is never a partially written file. Any existing file is
# preserved: a usable one is accepted, and a blank/short one aborts.
REMOTE_CODE = '''\
import os
import secrets
import sys
import tempfile

path = sys.argv[1]
minimum = 24
directory = os.path.dirname(path) or "."

if os.path.lexists(path):
    try:
        with open(path, "r", encoding="utf-8") as handle:
            existing = handle.read().strip()
    except OSError:
        print("existing owner password file could not be read; refusing to overwrite", file=sys.stderr)
        raise SystemExit(1)
    if len(existing) < minimum:
        print("existing owner password file is blank or shorter than 24 characters; refusing to overwrite", file=sys.stderr)
        raise SystemExit(1)
    print("Existing owner password preserved.")
    raise SystemExit(0)

fd, temp_path = tempfile.mkstemp(prefix=".owner-password.", dir=directory)
try:
    with os.fdopen(fd, "w", encoding="utf-8") as stream:
        stream.write(secrets.token_urlsafe(32) + "\\n")
    os.chmod(temp_path, 0o600)
    if os.name == "posix" and os.geteuid() == 0:
        os.chown(temp_path, 1000, 1000)
    try:
        os.link(temp_path, path)
    except FileExistsError:
        print("owner password file appeared while creating; refusing to overwrite", file=sys.stderr)
        raise SystemExit(1)
finally:
    try:
        os.unlink(temp_path)
    except OSError:
        pass

print("Created owner password (contents not printed).")
'''


def parse_args(argv):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--context", default="default")
    return parser.parse_args(argv)


def main(argv):
    args = parse_args(argv)
    docker = ["docker", "--context", args.context]
    subprocess.run(docker + ["volume", "create", VOLUME], check=True)
    try:
        subprocess.run(
            docker + [
                "run", "--rm", "--user", "root", "--entrypoint", "python3",
                "--mount", f"type=volume,source={VOLUME},target=/secrets",
                "agentcontrol-v2-control", "-c", REMOTE_CODE, TARGET,
            ],
            check=True,
        )
    except subprocess.CalledProcessError:
        print(
            "owner password initialization failed; no existing file was overwritten.",
            file=sys.stderr,
        )
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
