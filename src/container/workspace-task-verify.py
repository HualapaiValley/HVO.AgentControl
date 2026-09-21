#!/usr/bin/python3 -I -S
"""Independently copy, manifest, and test a bounded read-only worker workspace."""

import argparse
import hashlib
import json
import os
import shutil
import stat
import subprocess

WORKSPACE = "/workspace"
COPY_ROOT = "/tmp/workspace-copy"
DOTNET = "/usr/bin/dotnet"
OUTPUT_LIMIT = 64 * 1024
MANIFEST_LIMIT = 64 * 1024
MAX_FILES_LIMIT = 384
EXCLUDED_DIRECTORIES = {".git", "bin", "obj"}


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


def regular_reader(path):
    flags = os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
    descriptor = os.open(path, flags)
    value = os.fstat(descriptor)
    if not stat.S_ISREG(value.st_mode):
        os.close(descriptor)
        raise ValueError("non-regular-path-refused")
    return descriptor, value


def candidates(root, allowed_paths):
    names = set()
    for allowed in sorted(set(allowed_paths)):
        path = checked_join(root, allowed)
        value = os.lstat(path)
        if stat.S_ISREG(value.st_mode):
            relative_name = os.path.relpath(path, root)
            if not any(part in EXCLUDED_DIRECTORIES for part in relative_name.split("/")):
                names.add(relative_name)
            continue
        if not stat.S_ISDIR(value.st_mode):
            raise ValueError("non-regular-path-refused")
        for directory, directories, files in os.walk(path, followlinks=False):
            directories.sort()
            files.sort()
            retained = []
            for name in directories:
                item = os.path.join(directory, name)
                if stat.S_ISLNK(os.lstat(item).st_mode):
                    raise ValueError("symlink-refused")
                if name not in EXCLUDED_DIRECTORIES:
                    retained.append(name)
            directories[:] = retained
            for name in files:
                names.add(os.path.relpath(os.path.join(directory, name), root))
    return sorted(names)


def copy_and_manifest(root, destination, allowed_paths, max_files, max_bytes):
    if os.path.lexists(destination):
        shutil.rmtree(destination)
    os.makedirs(destination, mode=0o700)
    entries = []
    total = 0
    for relative_name in candidates(root, allowed_paths):
        relative(relative_name)
        if len(entries) >= max_files:
            raise ValueError("file-count-exceeded")
        source = checked_join(root, relative_name)
        descriptor, before = regular_reader(source)
        try:
            total += before.st_size
            if total > max_bytes:
                raise ValueError("file-bytes-exceeded")
            target = os.path.join(destination, relative_name)
            os.makedirs(os.path.dirname(target), mode=0o700, exist_ok=True)
            digest = hashlib.sha256()
            copied = 0
            with os.fdopen(descriptor, "rb", closefd=False) as stream, open(target, "xb") as output:
                for block in iter(lambda: stream.read(1 << 20), b""):
                    copied += len(block)
                    if copied > before.st_size:
                        raise ValueError("source-changed")
                    digest.update(block)
                    output.write(block)
            after = os.fstat(descriptor)
            if copied != before.st_size or (before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns) != (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns):
                raise ValueError("source-changed")
            entries.append({"path": relative_name, "bytes": copied, "sha256": digest.hexdigest()})
        finally:
            os.close(descriptor)
    manifest = {"files": entries, "fileCount": len(entries), "totalBytes": total}
    if len(canonical(manifest).encode("utf-8")) > MANIFEST_LIMIT:
        raise ValueError("manifest-json-exceeded")
    return manifest


def run_test(root, recipe, maximum_seconds):
    if recipe != "dotnet-test-release":
        raise ValueError("unsupported-recipe")
    value = os.stat(DOTNET)
    if not stat.S_ISREG(value.st_mode) or value.st_uid != 0 or value.st_mode & 0o022:
        raise ValueError("dotnet-contract-invalid")
    environment = {
        "HOME": "/tmp",
        "PATH": "/usr/bin:/bin",
        "DOTNET_ROOT": "/usr/share/dotnet",
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_NOLOGO": "1",
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
        "DOTNET_MULTILEVEL_LOOKUP": "0",
        "NUGET_PACKAGES": "/tmp/nuget-packages",
        "TMPDIR": "/tmp",
        "LANG": "C.UTF-8",
    }
    argv = [DOTNET, "test", "--configuration", "Release"]
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
        if args.maximum_seconds < 1 or args.maximum_seconds > 1800 or args.max_files < 1 or args.max_files > MAX_FILES_LIMIT or args.max_bytes < 1 or args.max_bytes > 64 * 1024 * 1024:
            raise ValueError("invalid-bounds")
        allowed = [relative(path, allow_dot=True) for path in args.allowed_path]
        if len(allowed) != len(set(allowed)) or len(allowed) > 32:
            raise ValueError("invalid-allowed-paths")
        root = checked_join(WORKSPACE, root_relative)
        if not stat.S_ISDIR(os.lstat(root).st_mode):
            raise ValueError("workspace-root-not-directory")
        copy_root = os.path.join(COPY_ROOT, root_relative)
        manifest = copy_and_manifest(root, copy_root, allowed, args.max_files, args.max_bytes)
        test = run_test(copy_root, args.recipe, args.maximum_seconds)
        state = "passed" if test["status"] == "passed" else "failed"
        result = {"state": state, "manifest": manifest, "testSummary": test,
                  "changedPaths": [entry["path"] for entry in manifest["files"]],
                  "failureDetail": None if state == "passed" else ("test-timeout" if test["timedOut"] else "test-failed")}
        encoded = canonical(result)
        if len(encoded.encode("utf-8")) > 1024 * 1024:
            raise ValueError("verification-output-exceeded")
        print(encoded)
    except (OSError, ValueError) as exception:
        fail(str(exception)[:256])


if __name__ == "__main__":
    main()
