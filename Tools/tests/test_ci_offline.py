#!/usr/bin/env python3
"""CI-runnable offline checks (no game binaries, no device, no private SDK).

This is the subset of the test suite that can run on a public runner. The game assemblies
and the APK are NOT needed, so nothing copyrighted is processed here:

  1. every tracked Python file compiles;
  2. every tracked PowerShell file parses (when pwsh is available);
  3. the tracked text files are valid UTF-8 and contain no mojibake artefacts;
  4. the manifest fixture set is complete and its index is consistent;
  5. the published JSON Schema is valid JSON and documents every manifest field;
  6. the privacy scan reports nothing above MEDIUM;
  7. the audit documents do not claim a current-run PASS for historical (D grade) evidence.

Tests that DO need the game assemblies are local-only and are reported as SKIPPED by
Tools/tests/test_loader.py when --refs is absent.

Usage (repo root):
    python Tools/tests/test_ci_offline.py
Exit code 0 = every check passed.
"""

import json
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
RESULTS = []

SKIP_EXT = {
    ".apk", ".blob", ".dex", ".so", ".jar", ".png", ".jpg", ".jpeg", ".gif", ".webp",
    ".zip", ".gz", ".xz", ".xwb", ".xsb", ".xgs", ".bnk", ".wav", ".ogg", ".mp3",
    ".bin", ".dat", ".dll", ".pyc", ".keystore", ".jks", ".ico", ".ttf", ".otf",
    ".mp4", ".webm", ".pdf", ".xnb", ".tbin",
}

# Bytes that only appear when UTF-8 text was round-tripped through a GBK/CP936 console.
# Each token is the visible form of "UTF-8 bytes read as GBK" for a common Chinese phrase
# fragment; they are not used by any legitimate document in this repository.
MOJIBAKE_TOKENS = [
    "\u93c8",
    "\u935b",
    "\u6212\u62a4",
    "\u951b",
    "\u7ecb",
    "\u5a34",
    "\u9428",
    "\u6d93",
    "\u93c4",
    "\u935a",
    "\u6234",
    "\u9286",
    "\u74ba",
    "\u5a09",
    "\u59af",
    "\u7f01",
    "\u65b6",
    "\u5bee",
    "\u93c3",
    "\u9354",
    "\u6275",
    "\u7481",
    "\u7efd",
    "\u7f03",
]


def check(name, ok, detail="", skipped=False):
    status = "SKIP" if skipped else ("PASS" if ok else "FAIL")
    RESULTS.append((name, status, detail))
    print("%s %-44s %s" % (status, name, detail))
    return ok


def tracked_files():
    """Files that are (or would become) public: tracked plus not-yet-added, minus gitignored."""
    out = subprocess.run(
        ["git", "-C", ROOT, "ls-files", "-z", "--cached", "--others", "--exclude-standard"],
        capture_output=True, check=True).stdout
    return [p.decode("utf-8", "replace") for p in out.split(b"\x00") if p]


def is_text(rel):
    if os.path.splitext(rel)[1].lower() in SKIP_EXT:
        return False
    norm = rel.replace("\\", "/")
    return not any(part in norm for part in (
        ".git/", ".tmp/", ".work/", "app/src/main/assets/", "app/src/main/apkroot/",
        "app/src/main/jniLibs/", "app/src/main/prebuilt/", "app/src/main/res/",
    ))


# Fixtures whose whole purpose is to be rejected, so the scanners must not flag them.
INTENTIONAL_NON_UTF8 = {
    "Tools/tests/fixtures/manifests/invalid.not-utf8.json",
}
# Files that legitimately contain the tokens below (the scanner itself).
MOJIBAKE_SCAN_EXEMPT = {
    "Tools/tests/test_ci_offline.py",
}


def main():
    files = tracked_files()

    # 1. Python syntax -------------------------------------------------------
    py_files = [f for f in files if f.endswith(".py")]
    failed = []
    for rel in py_files:
        path = os.path.join(ROOT, rel)
        try:
            with open(path, "rb") as fh:
                compile(fh.read(), path, "exec")
        except SyntaxError as exc:
            failed.append("%s:%s %s" % (rel, exc.lineno, exc.msg))
    check("python_syntax", not failed, "%d files %s" % (len(py_files), "; ".join(failed[:3])))

    # 2. PowerShell parse ----------------------------------------------------
    ps_files = [f for f in files if f.endswith(".ps1")]
    pwsh = None
    for candidate in ("pwsh", "powershell"):
        found = subprocess.run(["where" if os.name == "nt" else "which", candidate],
                               capture_output=True, text=True)
        if found.returncode == 0:
            pwsh = candidate
            break
    if pwsh is None:
        check("powershell_syntax", True, "SKIPPED: no PowerShell host available", skipped=True)
    else:
        script = (
            "$bad=0; foreach ($f in @(%s)) { $e=$null; "
            "[System.Management.Automation.Language.Parser]::ParseFile($f,[ref]$null,[ref]$e) | Out-Null; "
            "if ($e -and $e.Count -gt 0) { $bad++; Write-Output ($f + ': ' + $e[0].Message) } }; "
            "exit $bad"
        ) % ",".join("'%s'" % os.path.join(ROOT, f).replace("'", "''") for f in ps_files)
        proc = subprocess.run([pwsh, "-NoProfile", "-Command", script], capture_output=True, text=True)
        check("powershell_syntax", proc.returncode == 0,
              "%d files %s" % (len(ps_files), (proc.stdout or "").strip().splitlines()[:2]))

    # 3. UTF-8 + mojibake ----------------------------------------------------
    text_files = [f for f in files if is_text(f)]
    bad_utf8 = []
    bad_mojibake = []
    for rel in text_files:
        path = os.path.join(ROOT, rel)
        try:
            with open(path, "rb") as fh:
                raw = fh.read()
            text = raw.decode("utf-8")
        except UnicodeDecodeError as exc:
            if rel not in INTENTIONAL_NON_UTF8:
                bad_utf8.append("%s:%s" % (rel, exc.start))
            continue
        if rel in MOJIBAKE_SCAN_EXEMPT:
            continue
        for token in MOJIBAKE_TOKENS:
            if token in text:
                bad_mojibake.append("%s (%s)" % (rel, token))
                break
        for ch in text:
            if 0xE000 <= ord(ch) <= 0xF8FF:
                bad_mojibake.append("%s (private-use U+%04X)" % (rel, ord(ch)))
                break
    check("tracked_text_is_utf8", not bad_utf8, "%d files %s" % (len(text_files), "; ".join(bad_utf8[:3])))
    check("no_mojibake_in_tracked_text", not bad_mojibake, "; ".join(bad_mojibake[:5]))

    # 4. manifest fixture set ------------------------------------------------
    fixtures = os.path.join(ROOT, "Tools", "tests", "fixtures", "manifests")
    index_path = os.path.join(fixtures, "index.json")
    if not os.path.exists(index_path):
        check("manifest_fixture_index", False, "missing " + index_path)
    else:
        with open(index_path, "r", encoding="utf-8") as fh:
            index = json.load(fh)
        cases = index.get("cases", [])
        listed = {c["file"] for c in cases}
        present = {f for f in os.listdir(fixtures) if f.endswith(".json") and f != "index.json"}
        missing = sorted(present - listed)
        absent = sorted(listed - present)
        check("manifest_fixture_index", not missing and not absent and len(cases) >= 20,
              "%d cases, unlisted=%s, missing=%s" % (len(cases), missing, absent))

        bad = []
        for case in cases:
            path = os.path.join(fixtures, case["file"])
            expect = case["expect"]
            try:
                with open(path, "r", encoding="utf-8") as fh:
                    payload = json.load(fh)
            except (json.JSONDecodeError, UnicodeDecodeError):
                payload = None
            if expect == "ok":
                if not isinstance(payload, dict):
                    bad.append(case["file"] + " should be a valid object")
                    continue
                for field in ("schemaVersion", "id", "name", "version", "entryAssembly", "entryType"):
                    if field not in payload:
                        bad.append("%s missing %s" % (case["file"], field))
            else:
                if payload is not None and isinstance(payload, dict):
                    # A rejected fixture must be invalid for a reason, not just "different".
                    if "schemaVersion" not in payload and expect != "MF0108":
                        bad.append("%s is expected to be rejected by %s but looks complete"
                                   % (case["file"], expect))
        check("manifest_fixtures_self_consistent", not bad, "; ".join(bad[:3]))

    # 5. published JSON Schema ----------------------------------------------
    schema_path = os.path.join(ROOT, "docs", "modding", "manifest.schema.json")
    if not os.path.exists(schema_path):
        check("manifest_schema_present", False, "missing " + schema_path)
    else:
        with open(schema_path, "r", encoding="utf-8") as fh:
            schema = json.load(fh)
        props = set(schema.get("properties", {}))
        required = set(schema.get("required", []))
        expected = {
            "schemaVersion", "id", "name", "version", "author", "description", "website",
            "entryAssembly", "entryType", "gameVersion", "frameworkVersion", "dependencies",
            "privateAssemblies",
        }
        check("manifest_schema_present", True, schema_path)
        check("manifest_schema_covers_fields", expected <= props,
              "missing=" + ",".join(sorted(expected - props)))
        check("manifest_schema_required_matches_spec",
              required == {"schemaVersion", "id", "name", "version", "entryAssembly", "entryType"},
              ",".join(sorted(required)))
        check("manifest_schema_forbids_unknown",
              schema.get("additionalProperties") is False, "additionalProperties must be false")

    # 6. privacy ------------------------------------------------------------
    sys.path.insert(0, os.path.join(ROOT, "Tools"))
    import importlib.util
    spec = importlib.util.spec_from_file_location("privacy_scan", os.path.join(ROOT, "Tools", "privacy-scan.py"))
    privacy = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(privacy)
    original_stdout = sys.stdout
    try:
        sys.stdout = open(os.devnull, "w", encoding="utf-8")
        rc = privacy.main()
    finally:
        sys.stdout.close()
        sys.stdout = original_stdout
    check("privacy_scan", rc == 0, "rc=%d (see Tools/privacy-scan.py)" % rc)

    # 7. evidence-grade wording --------------------------------------------
    # Historical (D grade) results must never be presented as a current-run PASS.
    audit_dir = os.path.join(ROOT, "docs", "audit")
    offenders = []
    for name in sorted(os.listdir(audit_dir)):
        if not name.endswith(".md"):
            continue
        with open(os.path.join(audit_dir, name), "r", encoding="utf-8") as fh:
            text = fh.read()
        for line_no, line in enumerate(text.splitlines(), 1):
            if re.search(r"模拟器\s*\**PASS", line) and "RECORDED" not in line and "Phase 0.5.1" not in line:
                offenders.append("%s:%d" % (name, line_no))
    check("no_current_run_pass_for_historical_evidence", not offenders,
          "offenders: " + ", ".join(offenders[:5]))

    # 8. CI workflow static validation -------------------------------------
    workflow_path = os.path.join(ROOT, ".github", "workflows", "ci.yml")
    if not os.path.exists(workflow_path):
        check("ci_workflow_present", False, "missing " + workflow_path)
    else:
        with open(workflow_path, "r", encoding="utf-8") as fh:
            workflow_text = fh.read()
        problems = []
        try:
            import yaml  # optional: full structural validation when available
            doc = yaml.safe_load(workflow_text)
            triggers = doc.get(True, doc.get("on"))
            for trigger in ("push", "pull_request", "workflow_dispatch"):
                if not isinstance(triggers, dict) or trigger not in triggers:
                    problems.append("missing trigger " + trigger)
            if doc.get("permissions", {}).get("contents") != "read":
                problems.append("permissions.contents must be read")
            for name, job in (doc.get("jobs") or {}).items():
                if not job.get("runs-on"):
                    problems.append(name + ": no runs-on")
                if not job.get("timeout-minutes"):
                    problems.append(name + ": no timeout-minutes")
                for step in job.get("steps") or []:
                    uses = step.get("uses")
                    if uses and "@" not in uses:
                        problems.append(name + ": unpinned action " + uses)
            coverage = "yaml"
        except ImportError:
            # No PyYAML: fall back to line-based checks and say so.
            if "permissions:" not in workflow_text or "contents: read" not in workflow_text:
                problems.append("permissions block missing")
            for trigger in ("push:", "pull_request:", "workflow_dispatch:"):
                if trigger not in workflow_text:
                    problems.append("missing trigger " + trigger)
            for line in workflow_text.splitlines():
                stripped = line.strip()
                if stripped.startswith("uses:") and "@" not in stripped:
                    problems.append("unpinned action: " + stripped)
            if "timeout-minutes:" not in workflow_text:
                problems.append("no timeout-minutes")
            coverage = "line-based"
        if "upload-artifact" in workflow_text or "secrets." in workflow_text:
            problems.append("workflow must not upload artifacts or read secrets")
        check("ci_workflow_static", not problems, "%s (%s)" % (workflow_path, coverage))
        if problems:
            for problem in problems[:5]:
                print("      - " + problem)

    return finish()


def finish():
    failed = [r for r in RESULTS if r[1] == "FAIL"]
    skipped = [r for r in RESULTS if r[1] == "SKIP"]
    print("\n%d checks, %d passed, %d failed, %d skipped"
          % (len(RESULTS), len(RESULTS) - len(failed) - len(skipped), len(failed), len(skipped)))
    if failed:
        for name, _, detail in failed:
            print("  FAILED: %s  %s" % (name, detail))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
