#!/usr/bin/env python3
"""Privacy / secret scan for the tracked files of this repository.

What it checks (only files that git would publish):

  CRITICAL  private keys, cloud API tokens, hardcoded passwords
  HIGH      absolute user-profile paths (C:\\Users\\<name>, /home/<name>, /Users/<name>)
  MEDIUM    e-mail addresses, device serials supplied via SV_DEVICE_SERIAL
  INFO      machine-specific absolute paths (repo location) - reported, not fatal

Design notes:

* Only git-tracked files are scanned: that is exactly what becomes public.
  `.tmp/`, `.work/` and `AGENTS.md` are gitignored and are not published.
* Binary/asset extensions are skipped (the game content lives in the repo).
* `Tools/privacy-allowlist.json` lists accepted occurrences with a reason, so a
  deliberate, reviewed exception is visible instead of silently ignored.
* Exit code 0 = nothing above MEDIUM; 1 = at least one unresolved HIGH/CRITICAL;
  2 = the scan could not run (e.g. not a git repository).

Usage:
    python Tools/privacy-scan.py
    python Tools/privacy-scan.py --include-untracked
    python Tools/privacy-scan.py --json out.json
"""

import argparse
import json
import os
import re
import subprocess
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
ALLOWLIST = os.path.join(ROOT, "Tools", "privacy-allowlist.json")

SKIP_EXT = {
    ".apk", ".blob", ".dex", ".so", ".jar", ".png", ".jpg", ".jpeg", ".gif", ".webp",
    ".zip", ".gz", ".xz", ".xwb", ".xsb", ".xgs", ".bnk", ".wav", ".ogg", ".mp3",
    ".bin", ".dat", ".dll", ".pyc", ".keystore", ".jks", ".ico", ".ttf", ".otf",
    ".mp4", ".webm", ".pdf", ".xnb", ".tbin", ".yaml.bin",
}
SKIP_PATH_PARTS = (".git/", ".tmp/", ".work/", "app/src/main/assets/", "app/src/main/apkroot/",
                   "app/src/main/jniLibs/", "app/src/main/prebuilt/", "app/src/main/res/",
                   # The allow-list quotes the patterns it permits, so it must not scan itself.
                   "Tools/privacy-allowlist.json")

RULES = [
    ("CRITICAL", "private-key", re.compile(r"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----")),
    ("CRITICAL", "aws-access-key", re.compile(r"\bAKIA[0-9A-Z]{16}\b")),
    ("CRITICAL", "github-token", re.compile(r"\bgh[pousr]_[A-Za-z0-9]{20,}\b")),
    ("CRITICAL", "openai-style-key", re.compile(r"\bsk-[A-Za-z0-9]{32,}\b")),
    ("CRITICAL", "slack-token", re.compile(r"\bxox[baprs]-[A-Za-z0-9-]{10,}\b")),
    ("CRITICAL", "hardcoded-password",
     re.compile(r"(?i)\b(password|passwd|pwd)\s*[:=]\s*[\"']?[^\s\"'<>]{6,}")),
    ("HIGH", "windows-user-path",
     re.compile(r"(?i)\b[A-Z]:\\\\?Users\\\\?[^\\/\s\"'|)<>]+")),
    ("HIGH", "posix-home-path",
     re.compile(r"(?<![\w/])/(?:home|Users)/[A-Za-z0-9._-]+")),
    ("MEDIUM", "email", re.compile(r"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b")),
]

INFO_RULES = [
    ("INFO", "repo-absolute-path",
     re.compile(r"(?i)\b[A-Z]:\\+(?:Download|Projects?|src|code|work)\\[^\s\"'|)<>]+")),
]


def git_files(include_untracked):
    cmd = ["git", "-C", ROOT, "ls-files", "-z"]
    if include_untracked:
        cmd = ["git", "-C", ROOT, "ls-files", "-z", "--cached", "--others", "--exclude-standard"]
    try:
        out = subprocess.run(cmd, capture_output=True, check=True).stdout
    except (OSError, subprocess.CalledProcessError) as exc:
        print("cannot list git files: %s" % exc)
        return None
    return [p.decode("utf-8", "replace") for p in out.split(b"\x00") if p]


def load_allowlist():
    if not os.path.exists(ALLOWLIST):
        return []
    with open(ALLOWLIST, "r", encoding="utf-8") as fh:
        data = json.load(fh)
    return data.get("allow", [])


def is_allowed(entry, rule, rel_path, line_no, text):
    for item in entry:
        if item.get("rule") != rule:
            continue
        if item.get("path") and item["path"] != rel_path:
            continue
        if item.get("line") and int(item["line"]) != line_no:
            continue
        if item.get("contains") and item["contains"] not in text:
            continue
        return item
    return None


def skip_file(rel_path):
    ext = os.path.splitext(rel_path)[1].lower()
    if ext in SKIP_EXT:
        return True
    norm = rel_path.replace("\\", "/")
    return any(part in norm for part in SKIP_PATH_PARTS)


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    ap = argparse.ArgumentParser()
    ap.add_argument("--include-untracked", action="store_true",
                    help="also scan files that are not tracked yet (they would be published once added)")
    ap.add_argument("--json", default="", help="write findings as JSON to this path")
    ap.add_argument("--max-report", type=int, default=40)
    args = ap.parse_args()

    files = git_files(args.include_untracked)
    if files is None:
        return 2

    allow = load_allowlist()
    serial = os.environ.get("SV_DEVICE_SERIAL", "").strip()
    if serial:
        RULES.append(("MEDIUM", "device-serial", re.compile(re.escape(serial))))

    findings = []
    scanned = 0
    for rel in files:
        if skip_file(rel):
            continue
        full = os.path.join(ROOT, rel)
        if not os.path.isfile(full):
            continue
        try:
            with open(full, "rb") as fh:
                raw = fh.read()
            if b"\x00" in raw[:4096]:
                continue  # binary
            text = raw.decode("utf-8", "replace")
        except OSError:
            continue
        scanned += 1
        for line_no, line in enumerate(text.splitlines(), 1):
            for severity, rule, rx in RULES:
                m = rx.search(line)
                if not m:
                    continue
                if is_allowed(allow, rule, rel, line_no, line):
                    findings.append({"severity": "ALLOWED", "rule": rule, "path": rel,
                                     "line": line_no, "match": m.group(0),
                                     "text": line.strip()[:200]})
                    continue
                findings.append({"severity": severity, "rule": rule, "path": rel,
                                 "line": line_no, "match": m.group(0),
                                 "text": line.strip()[:200]})
            for severity, rule, rx in INFO_RULES:
                m = rx.search(line)
                if m:
                    findings.append({"severity": severity, "rule": rule, "path": rel,
                                     "line": line_no, "match": m.group(0),
                                     "text": line.strip()[:200]})

    real = [f for f in findings if f["severity"] in ("CRITICAL", "HIGH")]
    medium = [f for f in findings if f["severity"] == "MEDIUM"]
    info = [f for f in findings if f["severity"] == "INFO"]
    allowed = [f for f in findings if f["severity"] == "ALLOWED"]

    print("privacy-scan: %d tracked text files scanned" % scanned)
    print("  CRITICAL/HIGH : %d" % len(real))
    print("  MEDIUM        : %d" % len(medium))
    print("  INFO          : %d (machine-specific paths; not a secret)" % len(info))
    print("  allow-listed  : %d (reviewed exceptions, see Tools/privacy-allowlist.json)" % len(allowed))

    def show(items, label, limit):
        if not items:
            return
        print("\n%s" % label)
        for f in items[:limit]:
            print("  [%s] %s:%d %s" % (f["rule"], f["path"], f["line"], f["match"]))
            print("      %s" % f["text"])
        if len(items) > limit:
            print("  ... %d more" % (len(items) - limit))

    show(real, "UNRESOLVED (must be fixed or allow-listed with a reason)", args.max_report)
    show(medium, "MEDIUM (review: is this a real identifier?)", args.max_report)
    show(info, "INFO (machine-specific, informational)", 5)

    if args.json:
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump({"scanned": scanned, "findings": findings}, fh, ensure_ascii=False, indent=2)
        print("\njson: %s" % args.json)

    if real:
        print("\nRESULT: FAILED (%d unresolved CRITICAL/HIGH)" % len(real))
        return 1
    print("\nRESULT: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
