#!/usr/bin/env python3
"""Offline regression tests for the build-time mod pipeline (no device needed).

Covers the two build-time tools whose output must be verifiable without a phone:

  * Tools/ModInjector  - merges ModFramework.Bootstrap into StardewValley.dll and
                         hooks MainActivity.OnCreatePartTwo.
                         Regression: the default --loader-api must emit
                         Assembly.LoadFrom(string).  Assembly.Load(byte[]) does not
                         exist in this trimmed runtime, so a build made with the old
                         default fails at JIT time inside Bootstrap.Start() and is
                         swallowed by its own catch handler -> mods silently never
                         load.
  * Tools/AssemblyStore - replaces one entry inside assemblies.blob in place.
                         Invariants: entry_count / total_count / gap / version and
                         every XALZ descriptor_index stay identical, all untouched
                         entries stay byte-identical, and the replaced entry reads
                         back as exactly the patched file.

Usage (repo root, requires a .NET SDK):
    python Tools/tests/test_mod_pipeline.py
    python Tools/tests/test_mod_pipeline.py --dotnet .tmp/phase0.5/dotnet/dotnet.exe

Exit code 0 = every check passed.
"""

import argparse
import hashlib
import os
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
ASM_DIR = os.path.join(ROOT, "app", "src", "main", "apkroot", "assemblies")
BLOB = os.path.join(ASM_DIR, "assemblies.blob")
MANIFEST = os.path.join(ASM_DIR, "assemblies.manifest")
ARCH_BLOB = os.path.join(ASM_DIR, "assemblies.arm64_v8a.blob")

sys.path.insert(0, os.path.join(ROOT, "Tools", "AssemblyStore"))
import store as storelib  # noqa: E402

RESULTS = []


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def check(name, ok, detail=""):
    RESULTS.append((name, bool(ok), detail))
    print("%s %-38s %s" % ("PASS" if ok else "FAIL", name, detail))
    return bool(ok)


def run(cmd, cwd=None):
    started = time.time()
    proc = subprocess.run(cmd, cwd=cwd or ROOT, capture_output=True, text=True,
                          encoding="utf-8", errors="replace")
    elapsed = time.time() - started
    return proc.returncode, (proc.stdout or "") + (proc.stderr or ""), elapsed


def parse_verify(out):
    fields = {}
    for line in out.splitlines():
        if ":" in line:
            key, _, value = line.partition(":")
            fields[key.strip()] = value.strip()
    return fields


def parse_kv(out):
    """Parse `key=value` lines (the LoaderContract host format)."""
    fields = {}
    for line in out.splitlines():
        if "=" in line:
            key, _, value = line.partition("=")
            fields[key.strip()] = value.strip()
    return fields


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dotnet", default=os.path.join(ROOT, ".tmp", "phase0.5", "dotnet", "dotnet.exe"))
    ap.add_argument("--artifacts", default=os.path.join(ROOT, ".tmp", "phase0.5", "artifacts"))
    ap.add_argument("--refs", default=os.path.join(ROOT, ".tmp", "phase0.5", "refs"),
                    help="directory holding the 65 assemblies extracted from the game")
    ap.add_argument("--workdir", default=os.path.join(ROOT, ".tmp", "phase0.5", "work", "pipeline"))
    args = ap.parse_args()

    dotnet = os.path.abspath(args.dotnet)
    refs = os.path.abspath(args.refs)
    work = os.path.abspath(args.workdir)
    os.makedirs(work, exist_ok=True)

    if not check("dotnet_sdk_available", os.path.exists(dotnet),
                 dotnet + " (install the private SDK or pass --dotnet)"):
        return finish()
    if not check("game_assemblies_available", os.path.isdir(refs) and len(
            [f for f in os.listdir(refs) if f.endswith(".dll")]) >= 58,
            refs):
        return finish()

    env_note = []
    env_note.append("dotnet=" + dotnet)

    # ---------------------------------------------------------------- build
    rc, out, secs = run([dotnet, "build",
                         os.path.join(ROOT, "Tools", "ModInjector", "ModInjector.csproj"),
                         "-c", "Release", "--artifacts-path", os.path.abspath(args.artifacts), "-v", "q"])
    injector = os.path.join(os.path.abspath(args.artifacts), "bin", "ModInjector", "release", "ModInjector.dll")
    check("build_injector", rc == 0 and os.path.exists(injector),
          "rc=%d %.1fs %s" % (rc, secs, injector if rc == 0 else out.strip().splitlines()[-1:] or ""))

    # ---------------------------------------------------------------- extract
    extract_dir = os.path.join(work, "extract")
    st = storelib.Store(BLOB, MANIFEST)
    names = st.names()
    if not check("manifest_names_match_entries", len(names) == st.entry_count,
                 "%d names / %d entries" % (len(names), st.entry_count)):
        return finish()

    sv_index = names.index("StardewValley")
    sv_raw = st.decode(st.entries[sv_index])[0]
    sv_orig = os.path.join(work, "StardewValley.orig.dll")
    with open(sv_orig, "wb") as f:
        f.write(sv_raw)
    check("extract_stardewvalley", sv_raw[:2] == b"MZ" and len(sv_raw) == 9280000,
          "entry %d, %d bytes" % (sv_index, len(sv_raw)))

    # arm64 store naming regression (store 001 must not borrow store 000 names)
    arch = storelib.Store(ARCH_BLOB, MANIFEST)
    arch_names = arch.names()
    check("arch_store_names", len(arch_names) == arch.entry_count == 7
          and arch_names[0] == "System.Collections.Concurrent"
          and "System.Private.CoreLib" in arch_names,
          "%d names, first=%s" % (len(arch_names), arch_names[0] if arch_names else "-"))

    # an explicitly wrong --blob-id must fail instead of borrowing the other store's names
    rc_bad, out_bad, _ = run([sys.executable, os.path.join(ROOT, "Tools", "AssemblyStore", "store.py"),
                              "list", ARCH_BLOB, "--manifest", MANIFEST, "--blob-id", "000"])
    check("store_wrong_blob_id_rejected", rc_bad != 0 and "wrong --blob-id" in out_bad,
          "rc=%d" % rc_bad)

    # ---------------------------------------------------------------- patch: default
    patched = os.path.join(work, "StardewValley.patched.dll")
    rc, out, secs = run([dotnet, injector, "patch", sv_orig, patched, "--refs-dir", refs])
    default_patch_ok = check("patch_default_exit0", rc == 0, "rc=%d %.1fs" % (rc, secs))
    if not default_patch_ok:
        print(out[-2000:])
        return finish()

    old_mvid = new_mvid = ""
    for line in out.splitlines():
        s = line.strip()
        if s.startswith("old mvid"):
            old_mvid = s.partition("=")[2].strip()
        elif s.startswith("new mvid"):
            new_mvid = s.partition("=")[2].strip()
    check("default_patch_changes_mvid", bool(old_mvid) and old_mvid != new_mvid,
          "%s -> %s" % (old_mvid or "?", new_mvid or "?"))

    rc, vout, _ = run([dotnet, injector, "verify", patched])
    fields = parse_verify(vout)
    api = fields.get("loader-api", "(missing)")
    check("default_loader_api_is_loadfrom", api.startswith("loadfrom"),
          "verify reports loader-api=%r" % api)
    check("default_patch_verify_ok", rc == 0 and fields.get("RESULT") == "OK",
          "rc=%d RESULT=%s hook-is-first=%s" % (rc, fields.get("RESULT"), fields.get("hook-is-first")))
    check("default_patch_instrumented", "present" in fields.get("bootstrap-start", ""),
          fields.get("bootstrap-start", "missing"))

    # ---------------------------------------------------------------- patch: idempotent
    twice = os.path.join(work, "StardewValley.patched.twice.dll")
    rc, out2, _ = run([dotnet, injector, "patch", patched, twice, "--refs-dir", refs])
    rc_v, vout2, _ = run([dotnet, injector, "verify", twice])
    f2 = parse_verify(vout2)
    check("patch_is_idempotent", rc == 0 and rc_v == 0 and f2.get("hook-is-first") == "yes",
          "rerun rc=%d verify=%s (idempotent mode expected)" % (rc, f2.get("RESULT")))

    # ---------------------------------------------------------------- negative control
    legacy = os.path.join(work, "StardewValley.legacy-loadbytes.dll")
    rc, out3, _ = run([dotnet, injector, "patch", sv_orig, legacy, "--loader-api", "load", "--refs-dir", refs])
    rc_v, vout3, _ = run([dotnet, injector, "verify", legacy])
    f3 = parse_verify(vout3)
    api3 = f3.get("loader-api", "(missing)")
    check("legacy_api_switch_reproduces_bytes", rc == 0 and "byte[]" in api3,
          "--loader-api load -> loader-api=%r (proves the loadfrom assertion can fail)" % api3)

    # verification without the escape hatch must reject a byte[]-based artifact
    rc_v2, vout4, _ = run([dotnet, injector, "verify", legacy])
    check("legacy_artifact_rejected_by_verify", rc_v2 != 0 and "RESULT: FAILED" in vout4,
          "verify rc=%d on a load(byte[]) build" % rc_v2)

    # ---------------------------------------------------------------- container rebuild
    before_blob = sha256(BLOB)
    before_manifest = sha256(MANIFEST)
    with open(patched, "rb") as f:
        patched_bytes = f.read()
    new_blob_bytes = st.rebuild({sv_index: patched_bytes})
    new_blob = os.path.join(work, "assemblies.patched.blob")
    with open(new_blob, "wb") as fh:
        fh.write(new_blob_bytes)

    rb = storelib.Store(new_blob, MANIFEST)
    check("rebuild_structure_unchanged",
          (rb.version, rb.entry_count, rb.total_count, rb.reserved)
          == (st.version, st.entry_count, st.total_count, st.reserved)
          and rb.gap == st.gap,
          "version=%d entries=%d total=%d reserved=%d gap=%d"
          % (rb.version, rb.entry_count, rb.total_count, rb.reserved, len(rb.gap)))

    untouched = 0
    mismatched = []
    for i, entry in enumerate(st.entries):
        if i == sv_index:
            continue
        if rb.decode(rb.entries[i])[0] == st.decode(entry)[0]:
            untouched += 1
        else:
            mismatched.append(names[i])
    check("rebuild_leaves_other_entries", untouched == st.entry_count - 1 and not mismatched,
          "%d/%d untouched" % (untouched, st.entry_count - 1))

    desc_ok = all(rb.entries[i]["desc"] == st.entries[i]["desc"] for i in range(st.entry_count))
    check("rebuild_preserves_descriptor_index", desc_ok,
          "all %d XALZ descriptor indices preserved" % st.entry_count)

    back, desc = rb.decode(rb.entries[sv_index])
    check("rebuild_roundtrip_patched_entry",
          back == patched_bytes and hashlib.sha256(back).hexdigest() == hashlib.sha256(patched_bytes).hexdigest(),
          "entry %d reads back %d bytes sha=%s desc=%s"
          % (sv_index, len(back), hashlib.sha256(back).hexdigest()[:16], desc))

    pe_ok = all(rb.decode(e)[0][:2] == b"MZ" for e in rb.entries)
    check("rebuild_all_entries_are_pe", pe_ok, "%d entries" % rb.entry_count)

    # ---------------------------------------------------------------- store CLI verify
    sv_sha = hashlib.sha256(patched_bytes).hexdigest()
    rc, outv, _ = run([sys.executable, os.path.join(ROOT, "Tools", "AssemblyStore", "store.py"),
                       "verify", new_blob, "--manifest", MANIFEST, "StardewValley=" + sv_sha])
    check("store_verify_positive", rc == 0 and "RESULT: OK" in outv, "rc=%d" % rc)
    rc, outv2, _ = run([sys.executable, os.path.join(ROOT, "Tools", "AssemblyStore", "store.py"),
                        "verify", new_blob, "--manifest", MANIFEST, "StardewValley=" + "0" * 64])
    check("store_verify_detects_wrong_hash", rc != 0 and "FAIL" in outv2, "rc=%d" % rc)

    # ---------------------------------------------------------------- Loader contract
    # (a) the device-facing runtime must still compile against the game's own assemblies:
    #     that is what turns "this BCL member was trimmed" into a compile error.
    artifacts = os.path.abspath(args.artifacts)
    rc, out_r, _ = run([dotnet, "build", os.path.join(ROOT, "Mod", "ModFramework.Runtime",
                                                      "ModFramework.Runtime.csproj"),
                        "-c", "Release", "--artifacts-path", artifacts,
                        "-p:AndroidRefsDir=" + refs, "-v", "q"])
    check("runtime_compiles_against_android_refs", rc == 0,
          "rc=%d %s" % (rc, "" if rc == 0 else out_r.strip().splitlines()[-1:]))

    # (b) desktop contract test: a fully successful Loader.Start() must return null and must
    #     leave loader-report.txt behind (a default build swallows the exception, so the file
    #     is the only place the report survives).
    host_dir = os.path.join(ROOT, ".tmp", "phase0.5", "artifacts-host")
    rc, out_h, _ = run([dotnet, "build", os.path.join(ROOT, "Tools", "tests", "LoaderContract",
                                                      "LoaderContract.csproj"),
                        "-c", "Release", "--artifacts-path", host_dir, "-v", "q"])
    host_dll = os.path.join(host_dir, "bin", "LoaderContract", "release", "LoaderContract.dll")
    if check("build_loader_contract_host", rc == 0 and os.path.exists(host_dll), "rc=%d" % rc):
        for junk in ("loader.log", "loader-report.txt"):
            p = os.path.join(os.path.dirname(host_dll), junk)
            if os.path.exists(p):
                os.remove(p)
        rc, out_run, _ = run([dotnet, host_dll])
        fields_h = parse_kv(out_run)
        check("loader_success_returns_null", fields_h.get("return-null") == "true",
              "rc=%d return-null=%s value=%s"
              % (rc, fields_h.get("return-null"), fields_h.get("return-value")))
        check("loader_writes_report_file", fields_h.get("loader-report") == "True"
              and "locate=" in fields_h.get("report-body", ""),
              str(fields_h.get("loader-report")) + " " + fields_h.get("report-body", "")[:80])

    # ---------------------------------------------------------------- inputs untouched
    check("inputs_not_modified", sha256(BLOB) == before_blob and sha256(MANIFEST) == before_manifest,
          "blob=%s" % before_blob[:16])

    print("\nworkdir: " + work)
    print("dotnet : " + dotnet)
    return finish()


def finish():
    failed = [r for r in RESULTS if not r[1]]
    print("\n%d checks, %d passed, %d failed" % (len(RESULTS), len(RESULTS) - len(failed), len(failed)))
    if failed:
        for name, _, detail in failed:
            print("  FAILED: %s  %s" % (name, detail))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
