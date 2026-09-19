#!/usr/bin/python3 -I -S
"""Verify a candidate profile rootfs mounted read-only at /candidate.

This program always executes from the approved worker base image, never the
candidate. It compares candidate files, metadata and xattrs to the base and
emits one JSON object for the controller's pure verifier.
"""

import hashlib
import json
import os
import stat
import subprocess

CANDIDATE = "/candidate"
MOUNTPOINTS = ("/control", "/home/worker", "/workspace", "/session")


def xattrs(path):
    try:
        return [(name, os.getxattr(path, name, follow_symlinks=False).hex())
                for name in sorted(os.listxattr(path, follow_symlinks=False))]
    except OSError:
        return [("!unreadable", "")]


def metadata(path):
    value = os.lstat(path)
    return value.st_uid, value.st_gid, stat.S_IMODE(value.st_mode), xattrs(path)


def mode(path):
    uid, gid, permissions, attrs = metadata(CANDIDATE + path)
    return {"uid": uid, "gid": gid, "mode": permissions, "xattrs": attrs}


def digest(path):
    value = os.lstat(path)
    result = hashlib.sha256(
        f"{value.st_uid}:{value.st_gid}:{stat.S_IMODE(value.st_mode)}:{xattrs(path)}\n".encode())
    try:
        with open(path, "rb") as stream:
            for block in iter(lambda: stream.read(1 << 20), b""):
                result.update(block)
    except OSError:
        return "unreadable:" + path
    return result.hexdigest()


def tree_digest(path):
    result = hashlib.sha256(f"D{metadata(path)}\n".encode())
    for root, directories, files in os.walk(path):
        directories.sort()
        for name in directories:
            item = os.path.join(root, name)
            result.update(os.path.relpath(item, path).encode())
            result.update((f"L{os.readlink(item)}" if os.path.islink(item)
                           else f"D{metadata(item)}").encode())
        for name in sorted(files):
            item = os.path.join(root, name)
            result.update(os.path.relpath(item, path).encode())
            result.update((f"L{os.readlink(item)}" if os.path.islink(item)
                           else digest(item)).encode())
    return result.hexdigest()


def same(path):
    candidate = CANDIDATE + path
    if not os.path.lexists(candidate) or not os.path.lexists(path):
        return False
    if os.path.islink(candidate) or os.path.islink(path):
        return (os.path.islink(candidate) and os.path.islink(path)
                and os.readlink(candidate) == os.readlink(path)
                and xattrs(candidate) == xattrs(path))
    if os.path.isdir(candidate) != os.path.isdir(path):
        return False
    return (tree_digest(candidate) == tree_digest(path)
            and metadata(candidate) == metadata(path)) if os.path.isdir(candidate) else digest(candidate) == digest(path)


def superset(path):
    candidate = CANDIDATE + path
    if (not os.path.isdir(candidate) or not os.path.isdir(path)
            or metadata(candidate) != metadata(path)):
        return False
    for root, directories, files in os.walk(path):
        for name in directories:
            base_item = os.path.join(root, name)
            candidate_item = CANDIDATE + base_item
            if os.path.islink(base_item):
                if (not os.path.islink(candidate_item)
                        or os.readlink(candidate_item) != os.readlink(base_item)
                        or xattrs(candidate_item) != xattrs(base_item)):
                    return False
            elif (not os.path.isdir(candidate_item) or os.path.islink(candidate_item)
                  or metadata(candidate_item) != metadata(base_item)):
                return False
        for name in files:
            base_item = os.path.join(root, name)
            candidate_item = CANDIDATE + base_item
            if os.path.islink(base_item):
                if (not os.path.islink(candidate_item)
                        or os.readlink(candidate_item) != os.readlink(base_item)
                        or xattrs(candidate_item) != xattrs(base_item)):
                    return False
            elif (not os.path.isfile(candidate_item) or os.path.islink(candidate_item)
                  or digest(candidate_item) != digest(base_item)):
                return False
    return True


def account(name):
    try:
        with open(CANDIDATE + "/etc/passwd", encoding="utf-8") as stream:
            for line in stream:
                fields = line.rstrip("\n").split(":")
                if fields[0] == name:
                    return {"uid": int(fields[2]), "gid": int(fields[3]),
                            "home": fields[5], "shell": fields[6]}
    except OSError:
        pass
    return None


def cache_map(path):
    output = subprocess.run(
        ["/sbin/ldconfig", "-p", "-C", path], capture_output=True,
        text=True, timeout=30, check=False).stdout
    result = {}
    for line in output.splitlines():
        if "=>" not in line:
            continue
        key, target = line.split("=>", 1)
        soname = key.strip().split(" ", 1)[0]
        result.setdefault(soname, []).append((key.strip(), target.strip()))
    return result


def cache_ok():
    try:
        base = cache_map("/etc/ld.so.cache")
        candidate = cache_map(CANDIDATE + "/etc/ld.so.cache")
    except Exception:
        return False
    for soname, entries in base.items():
        if candidate.get(soname) != entries or not all(same(target) for _, target in entries):
            return False
    for soname, entries in candidate.items():
        if soname in base:
            continue
        for _, target in entries:
            if (not target.startswith("/") or "/../" in target
                    or not os.path.lexists(CANDIDATE + target)):
                return False
    return True


def main():
    setuid = []
    capabilities = []
    for root, _, files in os.walk(CANDIDATE):
        for name in files:
            path = os.path.join(root, name)
            try:
                value = os.lstat(path)
            except OSError:
                continue
            if stat.S_ISREG(value.st_mode) and value.st_mode & 0o6000:
                setuid.append(path[len(CANDIDATE):])
            try:
                if "security.capability" in os.listxattr(path, follow_symlinks=False):
                    capabilities.append(path[len(CANDIDATE):])
            except OSError:
                pass

    artifacts = {path: same(path) for path in (
        "/usr/local/bin/worker-supervisor", "/usr/local/bin/profile-image-verify",
        "/app", "/usr/share/dotnet", "/usr/local/lib/node_modules/opencode-ai",
        "/usr/local/bin/node", "/usr/bin/dotnet", "/usr/local/bin/opencode",
        "/usr/bin/python3", "/usr/bin/python3.12", "/usr/bin/env", "/bin/sh",
        "/usr/bin/dash", "/etc/ld.so.conf", "/etc/ld.so.conf.d",
        "/etc/nsswitch.conf", "/etc/ssl", "/usr/share/ca-certificates",
        "/etc/ca-certificates.conf")}
    artifacts["/etc/ld.so.cache"] = cache_ok()
    artifacts["/usr/lib/python3.12"] = superset("/usr/lib/python3.12")
    artifacts["/lib"] = (os.readlink(CANDIDATE + "/lib") == os.readlink("/lib")
                         if os.path.islink("/lib") else same("/lib"))
    artifacts["/lib64"] = (os.readlink(CANDIDATE + "/lib64") == os.readlink("/lib64")
                           if os.path.islink("/lib64") else same("/lib64"))
    artifacts["/usr/lib/x86_64-linux-gnu"] = (
        superset("/usr/lib/x86_64-linux-gnu") and superset("/usr/lib64")
        if os.path.isdir("/usr/lib64") else superset("/usr/lib/x86_64-linux-gnu"))
    artifacts["/usr/lib/aarch64-linux-gnu"] = (
        superset("/usr/lib/aarch64-linux-gnu")
        if os.path.isdir("/usr/lib/aarch64-linux-gnu") else True)
    artifacts["/etc/ld.so.preload"] = not os.path.lexists(CANDIDATE + "/etc/ld.so.preload")
    artifacts["/lib/python-shadow"] = not any(
        os.path.lexists(CANDIDATE + directory + "/python3")
        for directory in ("/usr/local/bin", "/usr/local/sbin"))

    system_policy = (
        "/etc/opencode", "/opencode.json", "/opencode.jsonc", "/.opencode",
        "/root/.config/opencode", "/root/.opencode", "/etc/dotnet")
    result = {
        "bridge": account("bridge"), "employee": account("employee"),
        "dirs": {path: mode(path) for path in MOUNTPOINTS},
        "app": mode("/app"), "supervisor": mode("/usr/local/bin/worker-supervisor"),
        "artifacts": artifacts, "setuid": setuid[:16],
        "fileCaps": capabilities[:16],
        "dockerSock": os.path.exists(CANDIDATE + "/var/run/docker.sock"),
        "mountpointsEmpty": all(not any(os.scandir(CANDIDATE + path)) for path in MOUNTPOINTS),
        "systemPolicyAbsent": not any(os.path.lexists(CANDIDATE + path) for path in system_policy),
    }
    print(json.dumps(result))


if __name__ == "__main__":
    main()
