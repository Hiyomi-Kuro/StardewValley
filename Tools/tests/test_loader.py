#!/usr/bin/env python3
"""Offline loader test driver (no Android device, no game installation required).

Builds and runs Tools/tests/ModFramework.Tests, which covers:

  * unit level  - SemVer, version ranges, JSON, manifest validation (fixture set),
                  discovery, dependency graph, determinism, error codes;
  * host level  - a real Manifest -> Loader -> Assembly.LoadFrom -> IModEntry.Initialize
                  chain against Tools/tests/FixtureMods;
  * Phase 1.1   - bare-DLL probe is never loaded, fail-closed game/framework version gates,
                  global assembly identity preflight (same name/different version, same
                  identity/different content, deterministic sharing, host collision, damaged
                  or missing declared files), dependency propagation, and the logger
                  degradation report;
  * example mod - Mod/ExampleMod.HelloWorld, but only when --refs is given, because that
                  project compiles against the game's own assemblies.

The Phase 1.1 identity fixtures are built from two parameterised projects
(Tools/tests/FixtureMods/ConflictLib and PlainLib): one source produces every "same simple
name, different identity/content" shape by passing MSBuild properties. A missing variant is
reported as FAIL, never as SKIP.

The game assemblies are never copied, hashed into the repository or uploaded: when --refs
is absent the example-mod checks are reported as SKIPPED, never as passed.

Usage (repo root):
    python Tools/tests/test_loader.py
    python Tools/tests/test_loader.py --dotnet .tmp/phase0.5/dotnet/dotnet.exe --refs .tmp/phase0.5/refs
    python Tools/tests/test_loader.py --dotnet dotnet          # CI: SDK on PATH, no game refs

Exit code 0 = every executed check passed.
"""

import argparse
import os
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))

RESULTS = []


def check(name, ok, detail="", skipped=False):
    status = "SKIP" if skipped else ("PASS" if ok else "FAIL")
    RESULTS.append((name, status, detail))
    print("%s %-46s %s" % (status, name, detail))
    return ok


def run(cmd, cwd=None):
    started = time.time()
    proc = subprocess.run(cmd, cwd=cwd or ROOT, capture_output=True, text=True,
                          encoding="utf-8", errors="replace")
    elapsed = time.time() - started
    return proc.returncode, (proc.stdout or "") + (proc.stderr or ""), elapsed


def tail(text, lines=12):
    parts = [p for p in text.strip().splitlines() if p.strip()]
    return " | ".join(parts[-lines:])


# Phase 1.1 identity fixtures: one project per shape, driven by MSBuild properties.
CONFLICT_LIB = os.path.join(ROOT, "Tools", "tests", "FixtureMods", "ConflictLib", "ConflictLib.csproj")
PLAIN_LIB = os.path.join(ROOT, "Tools", "tests", "FixtureMods", "PlainLib", "PlainLib.csproj")
FAKE_PROBE = os.path.join(ROOT, "Tools", "tests", "FixtureMods", "FakeMonoRuntimeProbe", "FakeMonoRuntimeProbe.csproj")

# key -> (project, properties, assembly name, file name that the test host expects)
VARIANTS = [
    ("probe", FAKE_PROBE, {}, "MonoRuntimeProbe", "MonoRuntimeProbe.dll"),
    ("diff_version_v1", CONFLICT_LIB,
     {"ConflictAssemblyName": "DiffVersionLib", "ConflictAssemblyVersion": "1.0.0.0"},
     "DiffVersionLib", "DiffVersionLib.v1.dll"),
    ("diff_version_v2", CONFLICT_LIB,
     {"ConflictAssemblyName": "DiffVersionLib", "ConflictAssemblyVersion": "2.0.0.0", "Variant": "VARIANT_B"},
     "DiffVersionLib", "DiffVersionLib.v2.dll"),
    ("diff_content_x", CONFLICT_LIB,
     {"ConflictAssemblyName": "DiffContentLib", "ConflictAssemblyVersion": "1.0.0.0", "Variant": "VARIANT_X"},
     "DiffContentLib", "DiffContentLib.x.dll"),
    ("diff_content_y", CONFLICT_LIB,
     {"ConflictAssemblyName": "DiffContentLib", "ConflictAssemblyVersion": "1.0.0.0", "Variant": "VARIANT_Y"},
     "DiffContentLib", "DiffContentLib.y.dll"),
    ("shared", CONFLICT_LIB,
     {"ConflictAssemblyName": "SharedLib", "ConflictAssemblyVersion": "1.0.0.0"},
     "SharedLib", "SharedLib.dll"),
    ("entry_v1", CONFLICT_LIB,
     {"ConflictAssemblyName": "EntryCollisionLib", "ConflictAssemblyVersion": "1.0.0.0"},
     "EntryCollisionLib", "EntryCollisionLib.dll"),
    ("entry_v2", CONFLICT_LIB,
     {"ConflictAssemblyName": "EntryCollisionLib", "ConflictAssemblyVersion": "2.0.0.0", "Variant": "VARIANT_B"},
     "EntryCollisionLib", "EntryCollisionLib.v2.dll"),
    ("gate", CONFLICT_LIB,
     {"ConflictAssemblyName": "VersionGateLib", "ConflictAssemblyVersion": "1.0.0.0"},
     "VersionGateLib", "VersionGateLib.dll"),
    ("host_collision", PLAIN_LIB,
     {"PlainAssemblyName": "ModFramework.Runtime", "PlainAssemblyVersion": "9.9.9.0"},
     "ModFramework.Runtime", "HostConflict.ModFramework.Runtime.dll"),
]


def find_artifact(root, file_name):
    """Locate one produced dll inside an artifacts directory, preferring bin/<project>/<config>."""
    hits = []
    for base, _dirs, files in os.walk(root):
        if file_name in files:
            hits.append(os.path.join(base, file_name))
    hits.sort()
    preferred = [h for h in hits if os.sep + "bin" + os.sep in h]
    chosen = preferred or hits
    return chosen[0] if chosen else ""


def build_variants(dotnet, variants_root, fixtures_dir):
    ok = True
    for key, project, props, assembly_name, target_name in VARIANTS:
        out_dir = os.path.join(variants_root, key)
        cmd = [dotnet, "build", project, "-c", "Release",
               "--artifacts-path", out_dir, "-v", "q"]
        for prop, value in sorted(props.items()):
            cmd.append("-p:%s=%s" % (prop, value))
        rc, out, secs = run(cmd)
        produced = find_artifact(out_dir, assembly_name + ".dll") if rc == 0 else ""
        if rc != 0 or not produced:
            ok = False
            check("fixture_variant_" + key, False,
                  "rc=%d %.1fs %s" % (rc, secs, tail(out, 4) if rc != 0 else "no " + assembly_name + ".dll"))
            continue
        shutil.copy2(produced, os.path.join(fixtures_dir, target_name))
        check("fixture_variant_" + key, True, target_name)
    return ok


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dotnet", default=os.path.join(ROOT, ".tmp", "phase0.5", "dotnet", "dotnet.exe"),
                    help="dotnet executable (default: the private SDK, or pass 'dotnet' for a system SDK)")
    ap.add_argument("--artifacts", default=os.path.join(ROOT, ".tmp", "phase0.5", "artifacts-loader"))
    ap.add_argument("--refs", default="",
                    help="directory with the 65 game assemblies; enables the example-mod checks")
    ap.add_argument("--workdir", default=os.path.join(ROOT, ".tmp", "phase0.5", "work", "loader"))
    args = ap.parse_args()

    dotnet = args.dotnet
    if os.path.sep in dotnet:
        if not os.path.exists(dotnet):
            print("dotnet not found: %s" % dotnet)
            print("RESULT: FAILED (no .NET SDK available)")
            return 1
    elif shutil.which(dotnet) is None:
        print("dotnet not found on PATH: %s" % dotnet)
        print("RESULT: FAILED (no .NET SDK available)")
        return 1
    artifacts = os.path.abspath(args.artifacts)
    work = os.path.abspath(args.workdir)
    os.makedirs(work, exist_ok=True)

    # ---------------------------------------------------------------- build
    targets = [
        ("runtime", os.path.join(ROOT, "Mod", "ModFramework.Runtime", "ModFramework.Runtime.csproj")),
        ("fixture_dependency", os.path.join(ROOT, "Tools", "tests", "FixtureMods", "FixtureMod.Dependency", "FixtureMod.Dependency.csproj")),
        ("fixture_mod", os.path.join(ROOT, "Tools", "tests", "FixtureMods", "FixtureMod", "FixtureMod.csproj")),
        ("tests", os.path.join(ROOT, "Tools", "tests", "ModFramework.Tests", "ModFramework.Tests.csproj")),
    ]
    built = {}
    for name, project in targets:
        rc, out, secs = run([dotnet, "build", project, "-c", "Release", "--artifacts-path", artifacts, "-v", "q"])
        built[name] = rc == 0
        check("build_" + name, rc == 0, "rc=%d %.1fs %s" % (rc, secs, "" if rc == 0 else tail(out, 4)))
    if not all(built.values()):
        return finish()

    # ---------------------------------------------------------------- fixtures
    fixtures = os.path.join(work, "fixtures")
    os.makedirs(fixtures, exist_ok=True)
    for name in ("FixtureMod.dll", "FixtureMod.Dependency.dll"):
        source = os.path.join(artifacts, "bin", os.path.splitext(name)[0], "release", name)
        if not os.path.exists(source):
            check("fixture_artifact_" + name, False, "missing " + source)
            return finish()
        shutil.copy2(source, os.path.join(fixtures, name))
    check("fixture_artifacts_copied", True, fixtures)

    # ------------------------------------------------- Phase 1.1 fixture variants
    # One parameterised project produces every identity shape the preflight tests need.
    # Each variant gets its own artifacts directory so MSBuild cannot reuse a stale build
    # whose properties (assembly name / version / defines) differ.
    if not build_variants(dotnet, os.path.join(work, "variants"), fixtures):
        return finish()

    # ---------------------------------------------------------------- example mod
    hello_world_dir = ""
    refs = os.path.abspath(args.refs) if args.refs else ""
    if refs:
        if not os.path.isdir(refs) or len([f for f in os.listdir(refs) if f.endswith(".dll")]) < 58:
            check("hello_world_build", False, "no usable game assemblies in " + refs)
        else:
            rc, out, secs = run([dotnet, "build",
                                 os.path.join(ROOT, "Mod", "ExampleMod.HelloWorld", "ExampleMod.HelloWorld.csproj"),
                                 "-c", "Release", "--artifacts-path", artifacts,
                                 "-p:AndroidRefsDir=" + refs, "-v", "q"])
            candidate = os.path.join(artifacts, "bin", "ExampleMod.HelloWorld", "release")
            ok = rc == 0 and os.path.exists(os.path.join(candidate, "ExampleMod.HelloWorld.dll"))
            check("hello_world_build", ok, "rc=%d %.1fs %s" % (rc, secs, candidate if ok else tail(out, 4)))
            if ok:
                hello_world_dir = candidate
    else:
        check("hello_world_build", True,
              "SKIPPED: --refs not given (needs the game's own assemblies)", skipped=True)
        check("hello_world_host_run", True,
              "SKIPPED: depends on hello_world_build", skipped=True)

    # ---------------------------------------------------------------- run
    test_dll = os.path.join(artifacts, "bin", "ModFramework.Tests", "release", "ModFramework.Tests.dll")
    if not os.path.exists(test_dll):
        check("test_host_present", False, "missing " + test_dll)
        return finish()

    cmd = [dotnet, test_dll, "--repo-root", ROOT, "--fixture-mod-dir", fixtures]
    if hello_world_dir:
        cmd += ["--hello-world-dir", hello_world_dir]
    rc, out, secs = run(cmd)
    print(out.rstrip())
    summary = ""
    for line in out.splitlines():
        if "checks," in line and "passed" in line:
            summary = line.strip()
    check("test_host_exit_code", rc == 0, "rc=%d %.1fs" % (rc, secs))
    check("test_host_summary", "0 failed" in summary, summary or "(no summary line)")
    if hello_world_dir:
        check("hello_world_host_run", "hello_world_discovered" in out and "FAIL hello_world" not in out,
              "example mod initialized through the real manifest")

    print("\nartifacts: " + artifacts)
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
