#!/usr/bin/env python3
"""Read-only validator for a device backup produced before install / uninstall.

The backup itself is created with adb pull (see
docs/audit/phase1.1.9-device-acceptance-report.md). This script only READS the
backup directory: it re-hashes every file listed in MANIFEST.json and compares
size + SHA-256, then checks the recorded file count / total bytes.

Why a separate validator: the manifest is the only proof that a restore would
bring back the same bytes. Re-running it after a copy or a reboot is cheap and
turns "I think the backup is fine" into a reproducible statement.

Usage:
    python Tools/validate-test-backup.py --root <backup dir>
    python Tools/validate-test-backup.py --root <backup dir> --quiet

Exit codes: 0 = every file matches, 1 = mismatch/missing, 3 = manifest missing.

The script never prints file contents, never writes to the backup, and never
touches a device.
"""

import argparse
import hashlib
import json
import os
import sys


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", required=True, help="backup directory containing MANIFEST.json")
    ap.add_argument("--quiet", action="store_true")
    args = ap.parse_args(argv)

    root = os.path.abspath(args.root)
    manifest_path = os.path.join(root, "MANIFEST.json")
    if not os.path.isfile(manifest_path):
        print("BLOCKED: no MANIFEST.json in " + root)
        return 3

    with open(manifest_path, "r", encoding="utf-8") as f:
        manifest = json.load(f)

    entries = manifest.get("entries", [])
    problems = []
    total = 0
    for e in entries:
        rel = e["path"]
        full = os.path.join(root, rel.replace("/", os.sep))
        if not os.path.isfile(full):
            problems.append("missing: " + rel)
            continue
        size = os.path.getsize(full)
        total += size
        if size != e["size"]:
            problems.append("size: %s manifest=%d actual=%d" % (rel, e["size"], size))
            continue
        digest = sha256_file(full)
        if digest != e["sha256"]:
            problems.append("sha256: %s manifest=%s actual=%s" % (rel, e["sha256"], digest))

    if manifest.get("file_count") != len(entries):
        problems.append("file_count: manifest=%s entries=%d"
                        % (manifest.get("file_count"), len(entries)))
    if manifest.get("total_bytes") != total:
        problems.append("total_bytes: manifest=%s actual=%d"
                        % (manifest.get("total_bytes"), total))

    if not args.quiet:
        print("root        : " + root)
        print("label       : %s" % manifest.get("label", ""))
        print("serial      : %s" % manifest.get("serial", ""))
        print("created_utc : %s" % manifest.get("created_utc", ""))
        print("files       : %d" % len(entries))
        print("total_bytes : %d" % total)
        for p in problems:
            print("  " + p)
    if problems:
        print("RESULT: FAIL (%d problem(s))" % len(problems))
        return 1
    print("RESULT: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
