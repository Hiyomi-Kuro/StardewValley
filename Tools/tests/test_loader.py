#!/usr/bin/env python3
"""Offline loader test driver (no Android device, no game installation required).

Builds and runs Tools/tests/ModFramework.Tests, which covers:

  * unit level  - SemVer, version ranges, JSON, manifest validation (fixture set),
                  discovery, dependency graph, determinism, error codes;
  * host level  - a real Manifest -> Loader -> Assembly.LoadFrom -> IModEntry.Initialize
                  chain against Tools/tests/FixtureMods;
  * example mod - Mod/ExampleMod.HelloWorld, but only when --refs is given, because that
                  project compiles against the game's own assemblies.

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
