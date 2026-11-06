#!/usr/bin/env python3
"""Toolchain discovery shared by the local test drivers (Phase 1.1.4).

Why this file exists: the earlier drivers defaulted to `.tmp/phase0.5/dotnet/dotnet.exe`, a
private SDK directory that does not survive a fresh checkout. A missing path there produced
"no .NET SDK available" even on a machine that had one. Nothing here hardcodes a user
directory; every candidate comes from an argument, an environment variable, the PATH, or a
directory under this repository's gitignored `.tmp/`.

Usage:
    from toolchain import find_dotnet, sdk_version
    exe = find_dotnet(args.dotnet)     # "" when nothing usable was found
"""

import glob
import os
import shutil
import subprocess

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))

# Environment variable an operator can set instead of passing --dotnet on every command.
DOTNET_ENV_VAR = "STARDEW_DOTNET"


def _exe_name():
    return "dotnet.exe" if os.name == "nt" else "dotnet"


def candidates(explicit=""):
    """Ordered candidate executables. Real paths first, 'dotnet' (PATH) last."""
    found = []
    if explicit:
        found.append(explicit)
    env = os.environ.get(DOTNET_ENV_VAR, "").strip()
    if env:
        found.append(env)
    # Private SDKs unpacked under .tmp/ by a previous phase (gitignored, machine-local).
    for pattern in (os.path.join(ROOT, ".tmp", "*", "dotnet", _exe_name()),
                    os.path.join(ROOT, ".tmp", "*", "dotnet", "dotnet")):
        for hit in sorted(glob.glob(pattern)):
            found.append(hit)
    found.append("dotnet")
    # De-duplicate while preserving order.
    ordered = []
    for item in found:
        if item and item not in ordered:
            ordered.append(item)
    return ordered


def resolve(explicit="", require_sdk=True):
    """Absolute path of a usable dotnet host, or ''.

    `require_sdk` also demands a non-empty `--list-sdks`, so a machine that only has a runtime
    is reported as unusable instead of failing later inside MSBuild.
    """
    for candidate in candidates(explicit):
        if os.sep in candidate or "/" in candidate:
            if not os.path.exists(candidate):
                continue
            path = os.path.abspath(candidate)
        else:
            located = shutil.which(candidate)
            if not located:
                continue
            path = located
        if require_sdk and not sdk_version(path):
            continue
        return path
    return ""


def sdk_version(dotnet):
    """Version of the newest installed SDK, or '' when the host has none."""
    try:
        proc = subprocess.run([dotnet, "--list-sdks"], capture_output=True, text=True,
                              encoding="utf-8", errors="replace")
    except OSError:
        return ""
    if proc.returncode != 0:
        return ""
    lines = [line.strip() for line in (proc.stdout or "").splitlines() if line.strip()]
    return lines[-1].split(" ")[0] if lines else ""


def describe(dotnet):
    version = sdk_version(dotnet)
    return "dotnet=" + dotnet + " sdk=" + (version or "(none)")
