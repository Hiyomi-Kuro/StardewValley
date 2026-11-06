#!/usr/bin/env python3
"""Static safety audit for the Phase 1.1.4 probe and the non-destructive deploy gate.

This file checks SOURCE TEXT only: no build, no device, no .NET SDK, no game binaries. It is
the guard that keeps two promises which cannot be re-checked at runtime on this machine:

  A. the default probe path contains no native experiment
     (no P/Invoke, no mprotect, no RWX, no machine-code writes, no detour, no unsafe) and the
     historical T11/T12 code is compilable only behind an explicit opt-in that this phase never
     builds;
  B. the deployment scripts have no automatic uninstall / clear-data path and the new gate is
     install-on-approval + same-signature only.

The runtime half of the evidence (the probe actually running through the real loader, and the
fake-adb scenarios) lives in:
  * Tools/tests/test_loader.py         -> builds the P114 package and runs Phase114Tests
  * Tools/tests/test_deploy_safe.ps1   -> fake adb / fake apksigner scenarios

Usage (repo root):
    python Tools/tests/test_p114_probe.py
Exit code 0 = every check passed.
"""

import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
RESULTS = []


def check(name, ok, detail=""):
    status = "PASS" if ok else "FAIL"
    RESULTS.append((name, status, detail))
    print("%s %-52s %s" % (status, name, detail))
    return ok


def read(rel):
    path = os.path.join(ROOT, rel)
    if not os.path.exists(path):
        return ""
    with open(path, "rb") as fh:
        return fh.read().decode("utf-8", "replace")


def code_only_csharp(text):
    """Strip comments and string literals so the audit matches CODE, not prose.

    Prose is allowed to say "no mprotect, no detour"; a declaration or a call is not.
    """
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    lines = []
    for line in text.splitlines():
        line = line.split("//", 1)[0]
        line = re.sub(r'@"(?:[^"]|"")*"', '""', line)
        line = re.sub(r'"(?:\\.|[^"\\])*"', '""', line)
        lines.append(line)
    return "\n".join(lines)


def code_only_powershell(text):
    """Strip '#' comments (whole-line and trailing) so the audit matches CODE, not prose."""
    return "\n".join(line.split("#", 1)[0] for line in text.splitlines())



def walk(rel_dir, suffixes):
    """Relative paths under rel_dir with one of the suffixes ('.ps1', ...)."""
    found = []
    base = os.path.join(ROOT, rel_dir)
    for directory, _dirs, files in os.walk(base):
        for name in files:
            if name.lower().endswith(tuple(suffixes)):
                found.append(os.path.relpath(os.path.join(directory, name), ROOT).replace("\\", "/"))
    return sorted(found)


# Tokens that must not appear in the safe probe's source at all.
PROBE_FORBIDDEN = [
    "DllImport", "mprotect", "PROT_EXEC", "Reflection.Emit", "DynamicMethod",
    "Harmony", "Marshal.WriteByte", "VirtualProtect",
]


def audit_mono_runtime_probe():
    entry_raw = read("Mod/MonoRuntimeProbe/ProbeEntry.cs")
    entry = code_only_csharp(entry_raw)
    detour = read("Mod/MonoRuntimeProbe/DetourProbe.cs")
    native = read("Mod/MonoRuntimeProbe/NativePageProbe.cs")
    project = read("Mod/MonoRuntimeProbe/MonoRuntimeProbe.csproj")

    check("probe_entry_present", len(entry_raw) > 0, "Mod/MonoRuntimeProbe/ProbeEntry.cs")
    check("probe_safe_entry_exists", "public static string RunSafe(string outputDirectory)" in entry,
          "RunSafe is the only regular entry")
    check("probe_legacy_run_delegates",
          re.search(r"public static string Run\(string outputDirectory, bool allowGameInvocation\)\s*\{[^}]*"
                    r"return RunSafe\(outputDirectory\);", entry, re.S) is not None,
          "Probe.Run must only delegate to RunSafe")

    offenders = [token for token in PROBE_FORBIDDEN if token in entry]
    check("probe_entry_has_no_native_code", not offenders, ",".join(offenders))
    check("probe_entry_has_no_unsafe_block", "unsafe" not in entry, "no 'unsafe' in ProbeEntry.cs")
    check("probe_entry_does_not_reference_experiments",
          "DetourProbe" not in entry and "NativePageProbe" not in entry and "LegacyNativeExperiments" not in entry,
          "the safe aggregate must not reach T11/T12")
    check("probe_entry_has_no_loadfrom", "Assembly.LoadFrom" not in entry,
          "the safe probe loads nothing from disk")
    check("probe_entry_redacts_paths", "BaseDirLeaf" in entry and "BaseDirLeaf)" in entry,
          "the report keeps only the last path segment")

    for name, text in (("detour", detour), ("native_page", native)):
        guarded = text.lstrip().startswith("//") and "#if LEGACY_NATIVE_EXPERIMENTS" in text and "#endif" in text
        # the guard must come before any real code: the first '#if' precedes the first 'using'
        first_guard = text.find("#if LEGACY_NATIVE_EXPERIMENTS")
        first_using = text.find("using ")
        check("probe_%s_compiled_out_by_default" % name,
              guarded and first_guard >= 0 and (first_using < 0 or first_guard < first_using),
              "#if LEGACY_NATIVE_EXPERIMENTS wraps the whole file")

    check("probe_csproj_unsafe_off_by_default",
          "<AllowUnsafeBlocks>false</AllowUnsafeBlocks>" in project, "default build has no unsafe")
    check("probe_csproj_legacy_opt_in",
          condition_present(project), "the legacy build needs -p:LegacyNativeExperiments=true")


def condition_present(project):
    return ("<PropertyGroup Condition=\"'$(LegacyNativeExperiments)' == 'true'\">" in project
            and "LEGACY_NATIVE_EXPERIMENTS" in project)


def audit_p114_mod():
    manifest_text = read("Mod/P114.RuntimeProbe/manifest.json")
    entry_raw = read("Mod/P114.RuntimeProbe/P114Entry.cs")
    probes_raw = read("Mod/P114.RuntimeProbe/SafeProbes.cs")
    entry = code_only_csharp(entry_raw)
    probes = code_only_csharp(probes_raw)
    dependency = read("Mod/P114.RuntimeProbe.Dependency/DependencyMarker.cs")

    try:
        manifest = json.loads(manifest_text)
    except ValueError as exc:
        check("p114_manifest_json", False, str(exc))
        return
    check("p114_manifest_json", True, "parsed")

    expected_fields = {"schemaVersion", "id", "name", "version", "author", "description", "website",
                       "entryAssembly", "entryType", "gameVersion", "frameworkVersion",
                       "privateAssemblies"}
    check("p114_manifest_no_unknown_fields", set(manifest) <= expected_fields,
          ",".join(sorted(set(manifest) - expected_fields)))
    check("p114_manifest_schema_v1", manifest.get("schemaVersion") == 1, "schemaVersion=1")
    check("p114_manifest_id", re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,63}", manifest.get("id", "")) is not None,
          manifest.get("id", ""))
    check("p114_manifest_entry_assembly", manifest.get("entryAssembly") == "P114.RuntimeProbe.dll",
          manifest.get("entryAssembly", ""))
    check("p114_manifest_entry_type", manifest.get("entryType") == "P114.RuntimeProbe.P114RuntimeProbeEntry",
          manifest.get("entryType", ""))
    check("p114_manifest_version_gates_open",
          manifest.get("gameVersion") == "*" and manifest.get("frameworkVersion") == "*",
          "a diagnostic mod must run on any build")
    check("p114_manifest_private_dependency_declared",
          manifest.get("privateAssemblies") == ["P114.RuntimeProbe.Dependency.dll"],
          str(manifest.get("privateAssemblies")))
    check("p114_manifest_does_not_declare_fixtures",
          all("P114Same" not in n and "P114Diff" not in n and "P114Alpha" not in n and "P114Beta" not in n
              for n in manifest.get("privateAssemblies", [])),
          "fixtures must never be auto-loaded by the loader")
    check("p114_manifest_does_not_reference_fixture_dir", "probe-fixtures" not in manifest_text,
          "the fixture directory is not part of the manifest contract")

    check("p114_entry_is_a_mod_entry",
          "public sealed class P114RuntimeProbeEntry : IModEntry" in entry and
          "public void Initialize(IModContext context)" in entry, "implements the real contract")

    offenders = [token for token in PROBE_FORBIDDEN + ["StardewValley", "Game1.", "Saves/"]
                 if token in entry or token in probes]
    check("p114_probe_has_no_native_or_game_access", not offenders, ",".join(offenders))

    check("p114_probe_no_unsafe", "unsafe" not in entry and "unsafe" not in probes, "no unsafe blocks")

    case_ids = re.findall(r"Run\(\"(R-\d\d)\",", probes_raw)
    expected_ids = ["R-01", "R-02", "R-04", "R-05", "R-06", "R-07", "R-08", "R-09", "R-10", "R-11", "R-13", "R-14"]
    check("p114_cases_registered_through_the_wrapper", case_ids == expected_ids,
          ",".join(case_ids))
    check("p114_cases_are_individually_wrapped",
          "catch (Exception ex)" in probes and "BlockedException blocked" in probes,
          "one failure is recorded and the loop continues (R-14)")

    check("p114_report_path_is_own_directory",
          'OutputDirectoryName = "probe-output"' in entry_raw and 'OutputFileName = "phase1.1.4.txt"' in entry_raw,
          "probe-output/phase1.1.4.txt")
    check("p114_report_write_failure_is_non_fatal",
          "probe report write failed" in entry_raw and "return null;" in entry_raw,
          "a write failure is logged, never thrown")

    check("p114_dependency_marker_present", "p114-private-dependency-alive" in dependency,
          "R-11 asserts this marker")

    # The loader and the APK build must know nothing about this mod.
    runtime_sources = walk("Mod/ModFramework.Runtime", [".cs"])
    wired = [rel for rel in runtime_sources if "P114" in read(rel)]
    check("p114_not_referenced_by_the_loader", not wired, ",".join(wired))

    app_sources = walk("app/src/main/kotlin", [".kt", ".kts"]) + ["app/build.gradle.kts", "settings.gradle.kts"]
    app_wired = [rel for rel in app_sources if rel and "P114" in read(rel)]
    check("p114_not_part_of_the_apk_build", not app_wired, ",".join(app_wired))
    check("p114_not_shipped_in_apkroot",
          not os.path.exists(os.path.join(ROOT, "app", "src", "main", "apkroot", "P114.RuntimeProbe.dll")),
          "apkroot holds no probe dll")


def audit_deploy_scripts():
    legacy = read("Tools/deploy-apk.ps1")
    safe_raw = read("Tools/deploy-apk-safe.ps1")
    safe = code_only_powershell(safe_raw)

    check("deploy_legacy_has_danger_banner",
          "DEPRECATED / DESTRUCTIVE" in legacy and "deploy-apk-safe.ps1" in legacy,
          "the old script points at the safe one")
    check("deploy_legacy_requires_explicit_opt_in",
          "LegacyDestructiveInstall" in legacy and
          re.search(r"if \(-not \$LegacyDestructiveInstall\)", legacy) is not None,
          "the uninstall path is behind an explicit switch")

    check("deploy_safe_present", len(safe_raw) > 0, "Tools/deploy-apk-safe.ps1")

    # Look at CODE only, and treat a `Say` line as output rather than an action: the plan block
    # is exactly where the operator is told what will NOT run.
    forbidden_words = ["uninstall", "pm clear", "force-stop", "am start", "clear data"]
    offenders = []
    for line in safe.splitlines():
        stripped = line.strip()
        if not stripped or stripped.lower().startswith("say"):
            continue
        lowered = stripped.lower()
        for word in forbidden_words:
            if word in lowered:
                offenders.append(word + " @ " + stripped[:70])
    check("deploy_safe_has_no_destructive_command", not offenders, "; ".join(offenders[:3]))

    # The install command itself must be the only adb verb that can change app state.
    installing = re.findall(r"AdbBin[^\r\n]*\binstall\b", safe)
    check("deploy_safe_only_installs", len(installing) >= 1, "%d install invocation(s)" % len(installing))
    check("deploy_safe_plan_states_never",
          "uninstall : NEVER" in safe_raw and "pm clear  : NEVER" in safe_raw and
          "force-stop: NEVER" in safe_raw and "launch    : NEVER" in safe_raw,
          "the plan block lists each destructive verb as NEVER")

    check("deploy_safe_default_is_read_only",
          "-InstallApproved" in safe and "-PushModsApproved" in safe and
          "PRECHECK-ONLY" in safe,
          "default run prints the plan and writes nothing")
    check("deploy_safe_signature_mismatch_stops",
          "SIGNATURE_MISMATCH_REQUIRES_USER_ACTION" in safe and
          "no uninstall, no pm clear" in safe,
          "mismatch stops before any write")
    check("deploy_safe_signature_unreadable_stops",
          "SIGNATURE_UNREADABLE_USE_MANUAL_VERIFICATION" in safe,
          "unprovable signature fails closed")
    check("deploy_safe_install_requires_approval",
          re.search(r"if \(\$InstallApproved\)", safe) is not None and
          re.search(r"if \(\$PushModsApproved\)", safe) is not None,
          "both device writes are gated")
    check("deploy_safe_never_guesses_the_device",
          "serial-not-attached" in safe and "ambiguous-device" in safe,
          "device selection is exact or blocked")
    check("deploy_safe_redacts_home_paths", "function Redact" in safe and "<home>" in safe,
          "output never contains a profile path")
    check("deploy_safe_prints_the_plan",
          "planned commands" in safe and "uninstall : NEVER" in safe,
          "the operator sees exactly what would run")


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    print("P114 static safety audit (source text only; no build, no device)\n")
    audit_mono_runtime_probe()
    audit_p114_mod()
    audit_deploy_scripts()

    failed = [r for r in RESULTS if r[1] == "FAIL"]
    print("\n%d checks, %d passed, %d failed" % (len(RESULTS), len(RESULTS) - len(failed), len(failed)))
    if failed:
        for name, _status, detail in failed:
            print("  FAILED: %s  %s" % (name, detail))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
