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
                  project compiles against the game's own assemblies;
  * Phase 1.1.4 - the P114.RuntimeProbe diagnostic mod through the real loader chain, plus its
                  parameterised Assembly.LoadFrom fixtures. It needs NO game assembly, so it
                  always runs (CI included); a build failure is a FAIL, never a SKIP.

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

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import toolchain  # noqa: E402  (local helper module, not a package)

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))

RESULTS = []


def check(name, ok, detail="", skipped=False):
    status = "SKIP" if skipped else ("PASS" if ok else "FAIL")
    RESULTS.append((name, status, detail))
    print("%s %-46s %s" % (status, name, detail))
    return ok


def run(cmd, cwd=None, env=None):
    started = time.time()
    proc = subprocess.run(cmd, cwd=cwd or ROOT, capture_output=True, text=True,
                          encoding="utf-8", errors="replace", env=env)
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

    # Phase 1.1.1: dependency pre-propagation. Each scenario needs assemblies that no other
    # test loads, so "already present in the AppDomain" can never mask the propagation rule.
    ("phase_a1_v1", CONFLICT_LIB,
     {"ConflictAssemblyName": "PhaseA1Lib", "ConflictAssemblyVersion": "1.0.0.0"},
     "PhaseA1Lib", "PhaseA1Lib.v1.dll"),
    ("phase_a1_v2", CONFLICT_LIB,
     {"ConflictAssemblyName": "PhaseA1Lib", "ConflictAssemblyVersion": "2.0.0.0", "Variant": "VARIANT_B"},
     "PhaseA1Lib", "PhaseA1Lib.v2.dll"),
    ("phase_a2_v1", CONFLICT_LIB,
     {"ConflictAssemblyName": "PhaseA2Lib", "ConflictAssemblyVersion": "1.0.0.0"},
     "PhaseA2Lib", "PhaseA2Lib.v1.dll"),
    ("phase_a2_v2", CONFLICT_LIB,
     {"ConflictAssemblyName": "PhaseA2Lib", "ConflictAssemblyVersion": "2.0.0.0", "Variant": "VARIANT_B"},
     "PhaseA2Lib", "PhaseA2Lib.v2.dll"),
    ("phase_a2b_v1", CONFLICT_LIB,
     {"ConflictAssemblyName": "PhaseA2Lib2", "ConflictAssemblyVersion": "1.0.0.0"},
     "PhaseA2Lib2", "PhaseA2Lib2.v1.dll"),
    ("phase_a2b_v2", CONFLICT_LIB,
     {"ConflictAssemblyName": "PhaseA2Lib2", "ConflictAssemblyVersion": "2.0.0.0", "Variant": "VARIANT_B"},
     "PhaseA2Lib2", "PhaseA2Lib2.v2.dll"),
    ("phase_a3_v1", CONFLICT_LIB,
     {"ConflictAssemblyName": "PhaseA3Lib", "ConflictAssemblyVersion": "1.0.0.0"},
     "PhaseA3Lib", "PhaseA3Lib.v1.dll"),
    ("phase_a3_v2", CONFLICT_LIB,
     {"ConflictAssemblyName": "PhaseA3Lib", "ConflictAssemblyVersion": "2.0.0.0", "Variant": "VARIANT_B"},
     "PhaseA3Lib", "PhaseA3Lib.v2.dll"),

    # Phase 1.1.2: snapshot binding (TOCTOU), host positive share and fixed-point fixtures.
    # `P112Entry` and `P112Entry.v2` are the same assembly identity with different content, which
    # is what proves that the bytes loaded are the bytes the preflight approved.
    ("p112_entry", CONFLICT_LIB,
     {"ConflictAssemblyName": "P112Entry", "ConflictAssemblyVersion": "1.0.0.0"},
     "P112Entry", "P112Entry.dll"),
    ("p112_entry_v2", CONFLICT_LIB,
     {"ConflictAssemblyName": "P112Entry", "ConflictAssemblyVersion": "1.0.0.0", "Variant": "VARIANT_B"},
     "P112Entry", "P112Entry.v2.dll"),
    ("p112_priv", PLAIN_LIB,
     {"PlainAssemblyName": "P112Priv", "PlainAssemblyVersion": "1.0.0.0"},
     "P112Priv", "P112Priv.dll"),
    ("p112_host_lib", PLAIN_LIB,
     {"PlainAssemblyName": "P112HostLib", "PlainAssemblyVersion": "1.0.0.0"},
     "P112HostLib", "P112HostLib.dll"),
    ("p112_conflict_v1", CONFLICT_LIB,
     {"ConflictAssemblyName": "P112ConflictLib", "ConflictAssemblyVersion": "1.0.0.0"},
     "P112ConflictLib", "P112ConflictLib.v1.dll"),
    ("p112_conflict_v2", CONFLICT_LIB,
     {"ConflictAssemblyName": "P112ConflictLib", "ConflictAssemblyVersion": "2.0.0.0", "Variant": "VARIANT_B"},
     "P112ConflictLib", "P112ConflictLib.v2.dll"),
    ("p112_shared", CONFLICT_LIB,
     {"ConflictAssemblyName": "P112SharedLib", "ConflictAssemblyVersion": "1.0.0.0"},
     "P112SharedLib", "P112SharedLib.dll"),
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


# ------------------------------------------------------------------ Phase 1.1.4
# The probe package is an ordinary mod package. The probe-fixtures/ subdirectory is NEVER
# declared in the manifest: the loader must not auto-load it, the probe loads it explicitly.
P114_PROBE_PROJECT = os.path.join(ROOT, "Mod", "P114.RuntimeProbe", "P114.RuntimeProbe.csproj")
P114_FIXTURE_PROJECT = os.path.join(ROOT, "Mod", "P114.RuntimeProbe.Fixtures", "P114Fixture.csproj")

# key, MSBuild properties, assembly name that must be produced
P114_FIXTURE_VARIANTS = [
    ("p114_same", {"FixtureAssemblyName": "P114Same"}, "P114Same"),
    ("p114_diff_a", {"FixtureAssemblyName": "P114Diff"}, "P114Diff"),
    ("p114_diff_b", {"FixtureAssemblyName": "P114Diff", "Variant": "VARIANT_B"}, "P114Diff"),
    ("p114_alpha", {"FixtureAssemblyName": "P114Alpha"}, "P114Alpha"),
    ("p114_beta", {"FixtureAssemblyName": "P114Beta"}, "P114Beta"),
]

# package-relative destination for each variant. same-a/same-b receive the SAME bytes.
P114_FIXTURE_LAYOUT = {
    "p114_same": ["same-a", "same-b"],
    "p114_diff_a": ["diff-a"],
    "p114_diff_b": ["diff-b"],
    "p114_alpha": ["alpha"],
    "p114_beta": ["beta"],
}


def build_p114_package(dotnet, artifacts, work):
    """Build the P114 mod plus its fixture variants and stage a complete mod package."""
    rc, out, secs = run([dotnet, "build", P114_PROBE_PROJECT, "-c", "Release",
                         "--artifacts-path", artifacts, "-v", "q"])
    probe_out = os.path.join(artifacts, "bin", "P114.RuntimeProbe", "release")
    produced = os.path.join(probe_out, "P114.RuntimeProbe.dll")
    if rc != 0 or not os.path.exists(produced):
        check("p114_probe_build", False, "rc=%d %.1fs %s" % (rc, secs, tail(out, 4)))
        return ""
    dependency = os.path.join(probe_out, "P114.RuntimeProbe.Dependency.dll")
    manifest = os.path.join(probe_out, "manifest.json")
    if not os.path.exists(dependency) or not os.path.exists(manifest):
        check("p114_probe_build", False, "missing dependency dll or manifest.json in " + probe_out)
        return ""
    check("p114_probe_build", True, "rc=0 %.1fs" % secs)

    package = os.path.join(work, "p114-package")
    if os.path.isdir(package):
        shutil.rmtree(package)
    os.makedirs(package)
    for name in ("manifest.json", "P114.RuntimeProbe.dll", "P114.RuntimeProbe.Dependency.dll"):
        shutil.copy2(os.path.join(probe_out, name), os.path.join(package, name))

    fixtures_root = os.path.join(package, "probe-fixtures")
    variants_root = os.path.join(work, "p114-variants")
    built = {}
    for key, props, assembly_name in P114_FIXTURE_VARIANTS:
        out_dir = os.path.join(variants_root, key)
        cmd = [dotnet, "build", P114_FIXTURE_PROJECT, "-c", "Release",
               "--artifacts-path", out_dir, "-v", "q"]
        for prop, value in sorted(props.items()):
            cmd.append("-p:%s=%s" % (prop, value))
        rc, out, secs = run(cmd)
        produced = find_artifact(out_dir, assembly_name + ".dll") if rc == 0 else ""
        if rc != 0 or not produced:
            check("p114_fixture_" + key, False, "rc=%d %s" % (rc, tail(out, 4) if rc != 0 else "missing dll"))
            return ""
        built[key] = produced
        check("p114_fixture_" + key, True, assembly_name + ".dll")

    for key, destinations in sorted(P114_FIXTURE_LAYOUT.items()):
        source = built[key]
        for destination in destinations:
            target_dir = os.path.join(fixtures_root, destination)
            os.makedirs(target_dir, exist_ok=True)
            shutil.copy2(source, os.path.join(target_dir, os.path.basename(source)))

    # A deliberately corrupt "assembly": R-14 needs one real load failure that must not abort
    # the remaining safe cases.
    corrupt_dir = os.path.join(fixtures_root, "corrupt")
    os.makedirs(corrupt_dir, exist_ok=True)
    with open(os.path.join(corrupt_dir, "P114Broken.dll"), "wb") as fh:
        fh.write(b"\x00" * 64)

    same_a = os.path.join(fixtures_root, "same-a", "P114Same.dll")
    same_b = os.path.join(fixtures_root, "same-b", "P114Same.dll")
    identical = open(same_a, "rb").read() == open(same_b, "rb").read()
    diff_a = open(os.path.join(fixtures_root, "diff-a", "P114Diff.dll"), "rb").read()
    diff_b = open(os.path.join(fixtures_root, "diff-b", "P114Diff.dll"), "rb").read()
    check("p114_fixture_same_bytes_identical", identical, "same-a == same-b")
    check("p114_fixture_diff_bytes_differ", diff_a != diff_b,
          "same identity, different bytes (%d vs %d)" % (len(diff_a), len(diff_b)))
    return package


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
    # The host prints assembly identities, and a malformed-input test can legitimately surface
    # a replacement character. Never let the console codec turn that into a crash.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    ap = argparse.ArgumentParser()
    ap.add_argument("--dotnet", default="",
                    help="dotnet executable (default: discovered from STARDEW_DOTNET, .tmp/*/dotnet, "
                         "then PATH; the host must have an SDK, not only a runtime)")
    ap.add_argument("--artifacts", default=os.path.join(ROOT, ".tmp", "phase0.5", "artifacts-loader"))
    ap.add_argument("--refs", default="",
                    help="directory with the 65 game assemblies; enables the example-mod checks")
    ap.add_argument("--workdir", default=os.path.join(ROOT, ".tmp", "phase0.5", "work", "loader"))
    args = ap.parse_args()

    dotnet = toolchain.resolve(args.dotnet)
    if not dotnet:
        print("no .NET SDK found.")
        print("  tried: --dotnet, $%s, .tmp/*/dotnet/dotnet[.exe], PATH" % toolchain.DOTNET_ENV_VAR)
        print("  an SDK (not just a runtime) is required; `dotnet --list-sdks` must be non-empty")
        print("RESULT: FAILED (no .NET SDK available)")
        return 1
    print("toolchain: " + toolchain.describe(dotnet))
    artifacts = os.path.abspath(args.artifacts)
    work = os.path.abspath(args.workdir)
    os.makedirs(work, exist_ok=True)

    # Build with one SDK and RUN with another host: a newer SDK (e.g. 10.x) builds net8.0 fine
    # but its apphost may not carry the 8.0 runtime. Roll-forward keeps that working without
    # asking the operator to install a second runtime. Applies to every `dotnet <dll>` run.
    run_env = dict(os.environ)
    run_env.setdefault("DOTNET_ROLL_FORWARD", "LatestMajor")

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

    # ------------------------------------------------- Phase 1.1.4 probe package
    p114_dir = build_p114_package(dotnet, artifacts, work)
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
    if p114_dir:
        cmd += ["--p114-dir", p114_dir]

    rc, out, secs = run(cmd, env=run_env)
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

    # ------------------------------------------- Phase 1.1.4 probe suite outcome
    if p114_dir:
        p114_failures = [line for line in out.splitlines() if line.startswith("FAIL p114_")]
        check("p114_suite_ran", "p114_loader_mod_initialized" in out,
              "the P114 probe went through discovery -> preflight -> staging -> load -> Initialize")
        check("p114_suite_no_failures", not p114_failures,
              "%d P114 checks failed %s" % (len(p114_failures), p114_failures[:2]))
    else:
        check("p114_suite_ran", False, "the P114 package could not be built")

    # ------------------------------------------- Phase 1.1.1 parser differential
    # Read-only: the game's own reference assemblies are parsed and compared against the desktop
    # BCL parser. Only counts and file names are printed; nothing is copied into the repository
    # and no absolute path is echoed.
    if refs:
        rc, diff_out, secs = run([dotnet, test_dll, "--diff-refs", refs], env=run_env)
        detail = ""
        for line in diff_out.splitlines():
            if "compared" in line:
                detail = line.strip().replace("\ufeff", "")
        check("p111_parser_differential_65_refs", rc == 0 and "RESULT: OK" in diff_out,
              "rc=%d %.1fs %s" % (rc, secs, detail or tail(diff_out, 3)))

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
