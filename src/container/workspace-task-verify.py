#!/usr/bin/python3 -I -S
"""Independently manifest and test a bounded read-only worker workspace."""

import argparse
import hashlib
import json
import os
import stat
import subprocess

WORKSPACE = "/workspace"
OUTPUT_LIMIT = 64 * 1024


def canonical(value):
    return json.dumps(value, ensure_ascii=True, separators=(",", ":"), sort_keys=True)


def fail(detail):
    print(canonical({"state": "failed", "failureDetail": detail}))


def relative(value, allow_dot=False):
    if allow_dot and value == ".":
        return value
    if not value or value.startswith("/") or "\\" in value or len(value) > 512:
        raise ValueError("invalid-relative-path")
    parts = value.split("/")
    if any(part in ("", ".", "..") for part in parts):
        raise ValueError("invalid-relative-path")
    return value


def checked_join(root, relative_path):
    current = root
    if relative_path == ".":
        return current
    for part in relative_path.split("/"):
        current = os.path.join(current, part)
        value = os.lstat(current)
        if stat.S_ISLNK(value.st_mode):
            raise ValueError("symlink-refused")
    return current


def collect(root, allowed_paths, max_files, max_bytes):
    entries = {}
    total = 0
    for allowed in sorted(set(allowed_paths)):
        path = checked_join(root, allowed)
        value = os.lstat(path)
        if stat.S_ISREG(value.st_mode):
            candidates = [path]
        elif stat.S_ISDIR(value.st_mode):
            candidates = []
            for directory, directories, files in os.walk(path, followlinks=False):
                directories.sort()
                files.sort()
                for name in directories:
                    item = os.path.join(directory, name)
                    if stat.S_ISLNK(os.lstat(item).st_mode):
                        raise ValueError("symlink-refused")
                candidates.extend(os.path.join(directory, name) for name in files)
        else:
            raise ValueError("non-regular-path-refused")
        for item in candidates:
            value = os.lstat(item)
            if stat.S_ISLNK(value.st_mode) or not stat.S_ISREG(value.st_mode):
                raise ValueError("non-regular-path-refused")
            relative_name = os.path.relpath(item, root)
            relative(relative_name)
            total += value.st_size
            if len(entries) + 1 > max_files:
                raise ValueError("file-count-exceeded")
            if total > max_bytes:
                raise ValueError("file-bytes-exceeded")
            digest = hashlib.sha256()
            with open(item, "rb") as stream:
                for block in iter(lambda: stream.read(1 << 20), b""):
                    digest.update(block)
            entries[relative_name] = {"bytes": value.st_size, "sha256": digest.hexdigest()}
    files = [{"path": path, **entries[path]} for path in sorted(entries)]
    return {"files": files, "fileCount": len(files), "totalBytes": total}


def run_test(root, recipe, maximum_seconds):
    if recipe != "dotnet-test-release":
        raise ValueError("unsupported-recipe")
    environment = {
        "HOME": "/tmp",
        "PATH": "/opt/dotnet-sdk:/usr/local/bin:/usr/bin:/bin",
        "DOTNET_ROOT": "/opt/dotnet-sdk",
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_NOLOGO": "1",
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
        "TMPDIR": "/tmp",
        "LANG": "C.UTF-8",
    }
    argv = ["/opt/dotnet-sdk/dotnet", "test", "--configuration", "Release", "--no-restore"]
    try:
        completed = subprocess.run(argv, cwd=root, env=environment, stdin=subprocess.DEVNULL,
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                   timeout=maximum_seconds, check=False)
        stdout = completed.stdout[:OUTPUT_LIMIT].decode("utf-8", "replace")
        stderr = completed.stderr[:OUTPUT_LIMIT].decode("utf-8", "replace")
        return {"recipeId": recipe, "status": "passed" if completed.returncode == 0 else "failed",
                "exitCode": completed.returncode, "timedOut": False,
                "stdout": stdout, "stderr": stderr,
                "stdoutTruncated": len(completed.stdout) > OUTPUT_LIMIT,
                "stderrTruncated": len(completed.stderr) > OUTPUT_LIMIT}
    except subprocess.TimeoutExpired as exception:
        stdout = (exception.stdout or b"")[:OUTPUT_LIMIT].decode("utf-8", "replace")
        stderr = (exception.stderr or b"")[:OUTPUT_LIMIT].decode("utf-8", "replace")
        return {"recipeId": recipe, "status": "failed", "exitCode": None, "timedOut": True,
                "stdout": stdout, "stderr": stderr,
                "stdoutTruncated": len(exception.stdout or b"") > OUTPUT_LIMIT,
                "stderrTruncated": len(exception.stderr or b"") > OUTPUT_LIMIT}


def main():
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--root", required=True)
    parser.add_argument("--allowed-path", action="append", required=True)
    parser.add_argument("--recipe", required=True)
    parser.add_argument("--maximum-seconds", type=int, required=True)
    parser.add_argument("--max-files", type=int, required=True)
    parser.add_argument("--max-bytes", type=int, required=True)
    try:
        args = parser.parse_args()
        root_relative = relative(args.root)
        if args.maximum_seconds < 1 or args.maximum_seconds > 1800 or args.max_files < 1 or args.max_files > 1024 or args.max_bytes < 1 or args.max_bytes > 64 * 1024 * 1024:
            raise ValueError("invalid-bounds")
        allowed = [relative(path, allow_dot=True) for path in args.allowed_path]
        if len(allowed) != len(set(allowed)) or len(allowed) > 32:
            raise ValueError("invalid-allowed-paths")
        root = checked_join(WORKSPACE, root_relative)
        if not stat.S_ISDIR(os.lstat(root).st_mode):
            raise ValueError("workspace-root-not-directory")
        manifest = collect(root, allowed, args.max_files, args.max_bytes)
        test = run_test(root, args.recipe, args.maximum_seconds)
        state = "passed" if test["status"] == "passed" else "failed"
        result = {"state": state, "manifest": manifest, "testSummary": test,
                  "changedPaths": [entry["path"] for entry in manifest["files"]],
                  "failureDetail": None if state == "passed" else ("test-timeout" if test["timedOut"] else "test-failed")}
        print(canonical(result))
    except (OSError, ValueError) as exception:
        fail(str(exception)[:256])


if __name__ == "__main__":
    main()
