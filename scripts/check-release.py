#!/usr/bin/env python3
"""Validate a requested release version before any tag, release, or image push.

This is the only component trusted to interpret the operator-supplied version
string. It never invokes a shell: every subprocess receives an argument list,
so a hostile version value cannot inject commands. The version is parsed and
constrained to a strict, container-tag-safe semantic version before it is used
for a git tag lookup.

Usage:
  scripts/check-release.py --version 0.1.0 [--props Directory.Build.props] [--repo .]
  scripts/check-release.py --extract-notes 0.1.0 CHANGELOG.md

When GITHUB_OUTPUT is set, the validated ``version``, ``tag`` and ``sha`` are
appended for downstream workflow steps. The tag is ``v<version>``.

Exit status:
  0  version is safe to release
  1  validation failed (message on stderr)
  2  usage error
"""

from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import NoReturn

# Strict, container-tag-safe semantic version: MAJOR.MINOR.PATCH with an
# optional dot-separated prerelease (for example 0.1.0-rc.1). Build
# metadata ("+...") is rejected because '+' is not a valid container tag
# character and adds no release value here. Leading zeros are rejected.
SEMVER = re.compile(
    r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)"
    r"(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\Z"
)


def fail(message: str) -> NoReturn:
    print(f"release validation failed: {message}", file=sys.stderr)
    raise SystemExit(1)


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--version", help="release version to validate (for example 0.1.0)")
    parser.add_argument("--props", default="Directory.Build.props", help="path to Directory.Build.props")
    parser.add_argument("--repo", default=".", help="repository root used for git tag lookups")
    parser.add_argument(
        "--extract-notes",
        nargs=2,
        metavar=("VERSION", "CHANGELOG"),
        help="print the CHANGELOG section for VERSION and exit",
    )
    return parser.parse_args(argv)


def validate_semver(version: str) -> str:
    if len(version) > 128 or not SEMVER.match(version):
        fail(
            f"{version!r} is not a strict semantic version "
            "(expected MAJOR.MINOR.PATCH with an optional -prerelease)"
        )
    if "-" in version:
        for identifier in version.split("-", 1)[1].split("."):
            if identifier.isdigit() and len(identifier) > 1 and identifier.startswith("0"):
                fail("numeric prerelease identifiers must not have leading zeros")
    return version


def read_props_version(path: str) -> str:
    if not Path(path).is_file():
        fail(f"version source of truth not found: {path}")
    try:
        root = ET.parse(path).getroot()
    except ET.ParseError as exc:
        fail(f"could not parse {path}: {exc}")
    node = root.find("PropertyGroup/Version")
    if node is None or not (node.text or "").strip():
        fail(f"{path} does not define a non-empty <Version>")
    return node.text.strip()


def tag_exists(repo: str, tag: str) -> bool:
    try:
        result = subprocess.run(
            ["git", "rev-parse", "-q", "--verify", f"refs/tags/{tag}"],
            cwd=repo,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
        )
    except FileNotFoundError:
        fail("git is not available on PATH")
    if result.returncode not in (0, 1):
        fail("could not verify git tag existence")
    return result.returncode == 0


def head_sha(repo: str) -> str:
    result = subprocess.run(
        ["git", "rev-parse", "HEAD"],
        cwd=repo,
        capture_output=True,
        text=True,
        check=False,
    )
    if result.returncode != 0:
        fail("could not resolve the release commit")
    return result.stdout.strip()


def write_github_output(version: str, tag: str, sha: str) -> None:
    target = os.environ.get("GITHUB_OUTPUT")
    if not target:
        return
    with open(target, "a", encoding="utf-8") as handle:
        handle.write(f"version={version}\n")
        handle.write(f"tag={tag}\n")
        handle.write(f"sha={sha}\n")


def extract_notes(version: str, changelog: str) -> None:
    path = Path(changelog)
    if not path.is_file():
        fail(f"CHANGELOG not found: {changelog}")
    text = path.read_text(encoding="utf-8")
    heading = re.compile(r"^##\s+(?:\[v?" + re.escape(version) + r"\]|v?" + re.escape(version) + r")(?=\s|$).*$", re.MULTILINE)
    match = heading.search(text)
    if match is None:
        fail(f"CHANGELOG has no section for version {version}")
    remainder = text[match.end():]
    next_heading = re.search(r"^##\s+", remainder, re.MULTILINE)
    body = remainder[: next_heading.start()] if next_heading else remainder
    if not body.strip():
        fail(f"CHANGELOG section for {version} is empty")
    print(body.strip())


def run_validation(args: argparse.Namespace) -> None:
    if args.version is None:
        fail("--version is required unless --extract-notes is used")
    version = validate_semver(args.version)

    props_version = read_props_version(args.props)
    if props_version != version:
        fail(
            f"requested version {version} does not match "
            f"<Version>{props_version}</Version> in {args.props}"
        )

    tag = f"v{version}"
    if tag_exists(args.repo, tag):
        fail(f"git tag {tag} already exists; refusing to overwrite an existing release")

    sha = head_sha(args.repo)
    write_github_output(version, tag, sha)
    print(f"release version OK: version={version} tag={tag} sha={sha or 'unknown'}")


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    if args.extract_notes:
        version, changelog = args.extract_notes
        extract_notes(version, changelog)
        return 0
    run_validation(args)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
