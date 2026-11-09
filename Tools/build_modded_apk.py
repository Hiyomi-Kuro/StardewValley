#!/usr/bin/env python3
"""Prepare a Bootstrap-injected assemblies blob for the mod-enabled APK build.

Phase 1.1.7. The default ``assembleDebug`` build stays a baseline build: it packages
``app/src/main/apkroot/assemblies/assemblies.blob`` exactly as it is. This script is the
*only* producer of the mod-enabled blob, and it never writes into the source tree:

    app/src/main/apkroot/assemblies/assemblies.blob   (read only)
        -> extract 58 + 7 assemblies  -> 65 refs      (Tools/AssemblyStore/store.py)
        -> Tools/ModInjector patch    -> patched StardewValley.dll
        -> Tools/ModInjector verify   -> hook/API/Mods-root/MVID gate
        -> Tools/AssemblyStore/store.py replace       -> new blob
        -> validate every untouched entry and the size budget
        -> <work>/modded-assemblies.blob  +  <work>/modded-build-manifest.json

Every parser used here already exists and is unit tested; this module only orchestrates
them, so there is no second XABA/LZ4 implementation to keep in sync.

Requires the real game assemblies, so it is local-only: CI runs the static checks in
Tools/tests/test_modded_build.py instead of this script.

Exit codes: 0 = blob ready, 2 = usage, 3 = missing local input, 4 = validation failed.
"""

import argparse
import datetime
import hashlib
import json
import os
import platform
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
STORE_PY = os.path.join(HERE, "AssemblyStore", "store.py")
INJECTOR_PROJ = os.path.join(HERE, "ModInjector", "ModInjector.csproj")

sys.path.insert(0, os.path.join(HERE, "AssemblyStore"))
import store as store_mod  # noqa: E402  (path set above)

PACKAGE = "com.chucklefish.stardewvalley"
EXTERNAL_MODS_DIR = "/sdcard/Android/data/%s/files/Mods" % PACKAGE
TARGET_ASSEMBLY = "StardewValley"
EXPECTED_ENTRY_COUNT = 58
EXPECTED_TOTAL_COUNT = 65
ARCH_BLOB_ENTRIES = 7


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def sha256_bytes(data):
    return hashlib.sha256(data).hexdigest()


class Checks(object):
    """Collects named pass/fail facts so the report can quote them verbatim."""

    def __init__(self):
        self.items = []

    def check(self, name, ok, detail=""):
        self.items.append({"name": name, "ok": bool(ok), "detail": str(detail)})
        print("%s %-42s %s" % ("ok  " if ok else "FAIL", name, detail))
        return ok

    @property
    def failed(self):
        return [c for c in self.items if not c["ok"]]


def run(cmd, cwd=None, capture=True):
    print("$ " + " ".join(cmd))
    proc = subprocess.run(
        cmd, cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        universal_newlines=True, encoding="utf-8", errors="replace")
    if capture:
        sys.stdout.write(proc.stdout)
    return proc.returncode, proc.stdout


def parse_kv(text, key):
    """Reads ``key : value`` (or ``key = value``) from ModInjector output."""
    for line in text.splitlines():
        cuts = [p for p in (line.find(":"), line.find("=")) if p >= 0]
        if not cuts:
            continue
        cut = min(cuts)
        if line[:cut].strip() == key:
            return line[cut + 1:].strip()
    return ""


def size_table_from_so(so_path, target_raw_size, descriptor_index):
    """Locates the per-assembly size table inside libxamarin-app.so.

    The table is an array of 16-byte records indexed by ``descriptor_index`` (the second
    u32 of the XALZ header). The shipped entry's own uncompressed size is the budget the
    runtime enforces, so the table value for this assembly must equal that size.

    Returns (expected_size, table_offset, matched_entries, total_entries, mismatches).
    """
    with open(so_path, "rb") as f:
        data = f.read()

    needle = target_raw_size.to_bytes(4, "little")
    occurrences = [i for i in range(0, len(data) - 4) if data[i:i + 4] == needle]
    if len(occurrences) != 1:
        raise ValueError("expected exactly one u32 %d in %s, found %d"
                         % (target_raw_size, os.path.basename(so_path), len(occurrences)))

    table_offset = occurrences[0] - descriptor_index * 16
    if table_offset < 0:
        raise ValueError("size table offset would be negative (%d)" % table_offset)
    return table_offset, data


def read_size_table_value(so_data, table_offset, descriptor_index):
    import struct
    return struct.unpack_from("<I", so_data, table_offset + descriptor_index * 16)[0]


def main(argv):
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--blob", default=os.path.join(
        ROOT, "app", "src", "main", "apkroot", "assemblies", "assemblies.blob"))
    ap.add_argument("--manifest", default=os.path.join(
        ROOT, "app", "src", "main", "apkroot", "assemblies", "assemblies.manifest"))
    ap.add_argument("--arch-blob", default=os.path.join(
        ROOT, "app", "src", "main", "apkroot", "assemblies", "assemblies.arm64_v8a.blob"))
    ap.add_argument("--app-so", default=os.path.join(
        ROOT, "app", "src", "main", "jniLibs", "arm64-v8a", "libxamarin-app.so"))
    ap.add_argument("--dotnet", default="dotnet")
    ap.add_argument("--work", required=True)
    ap.add_argument("--out-blob", required=True)
    ap.add_argument("--manifest-json", required=True)
    ap.add_argument("--mods-dir", default=EXTERNAL_MODS_DIR)
    ap.add_argument("--loader-api", default="loadfrom", choices=["loadfrom"])
    args = ap.parse_args(argv)

    for path in (args.blob, args.manifest, args.arch_blob):
        if not os.path.isfile(path):
            print("local-only: missing input " + path)
            return 3

    work = os.path.abspath(args.work)
    refs_dir = os.path.join(work, "refs")
    patched_dir = os.path.join(work, "patched")
    injector_out = os.path.join(work, "injector")
    logs_dir = os.path.join(work, "logs")
    for d in (work, refs_dir, patched_dir, injector_out, logs_dir):
        os.makedirs(d, exist_ok=True)

    checks = Checks()
    manifest = {
        "schema": 1,
        "phase": "1.1.7",
        "generated_utc": datetime.datetime.now(datetime.timezone.utc)
                        .strftime("%Y-%m-%dT%H:%M:%SZ"),
        "host": platform.platform(),
        "inputs": {},
        "store": {},
        "refs": {},
        "injector": {},
        "patched": {},
        "output_blob": {},
        "unchanged": {},
        "tools": {},
        "checks": checks.items,
    }

    # ---------------------------------------------------------------- 1. inputs
    print("\n== 1. inputs (read-only) ==")
    input_hashes = {}
    for label, path in (("blob", args.blob), ("manifest", args.manifest),
                        ("arch_blob", args.arch_blob), ("app_so", args.app_so)):
        if not os.path.isfile(path):
            print("local-only: missing input " + path)
            return 3
        digest = sha256_file(path)
        input_hashes[label] = digest
        manifest["inputs"][label] = {
            "path": os.path.relpath(path, ROOT).replace("\\", "/"),
            "sha256": digest,
            "bytes": os.path.getsize(path),
        }
        print("  %-10s %s  %d bytes" % (label, digest, os.path.getsize(path)))

    # ---------------------------------------------------------- 2. extract refs
    print("\n== 2. extract 58 + 7 assemblies (Tools/AssemblyStore/store.py) ==")
    primary = store_mod.Store(args.blob, args.manifest, "000")
    arch = store_mod.Store(args.arch_blob, args.manifest, "001")
    checks.check("store_magic", primary.data[:4] == b"XABA",
                 "entry_count=%d total_count=%d" % (primary.entry_count, primary.total_count))
    checks.check("primary_entry_count", primary.entry_count == EXPECTED_ENTRY_COUNT,
                 primary.entry_count)
    checks.check("store_total_count", primary.total_count == EXPECTED_TOTAL_COUNT,
                 primary.total_count)
    checks.check("arch_entry_count", arch.entry_count == ARCH_BLOB_ENTRIES, arch.entry_count)

    names = primary.names()
    arch_names = arch.names()
    checks.check("manifest_names_primary", len(names) == EXPECTED_ENTRY_COUNT, len(names))
    checks.check("manifest_names_arch", len(arch_names) == ARCH_BLOB_ENTRIES, len(arch_names))
    checks.check("manifest_total_names", len(names) + len(arch_names) == EXPECTED_TOTAL_COUNT,
                 len(names) + len(arch_names))
    checks.check("manifest_no_duplicate_names",
                 len(set(names + arch_names)) == len(names) + len(arch_names),
                 len(set(names + arch_names)))

    baseline_raw = {}
    for label, st, stored_names in (("primary", primary, names), ("arch", arch, arch_names)):
        for e in st.entries:
            raw, desc = st.decode(e)
            name = stored_names[e["index"]]
            if raw[:2] != b"MZ":
                checks.check("pe_" + name, False, "not a PE")
                continue
            baseline_raw[name] = {"raw": raw, "desc": desc, "store": label}
            path = os.path.join(refs_dir, name + ".dll")
            with open(path, "wb") as f:
                f.write(raw)
    print("  wrote %d assemblies into %s" % (len(baseline_raw), refs_dir))
    checks.check("refs_total_65", len(baseline_raw) == EXPECTED_TOTAL_COUNT, len(baseline_raw))
    manifest["refs"] = {
        "count": len(baseline_raw),
        "primary": EXPECTED_ENTRY_COUNT,
        "arch": ARCH_BLOB_ENTRIES,
        "directory": os.path.relpath(refs_dir, ROOT).replace("\\", "/"),
    }

    if TARGET_ASSEMBLY not in baseline_raw:
        print("FAIL target assembly %s missing from store" % TARGET_ASSEMBLY)
        return 4
    target = baseline_raw[TARGET_ASSEMBLY]
    target_desc = target["desc"]
    target_raw_size = len(target["raw"])
    print("  %s: raw=%d descriptor_index=%d" % (TARGET_ASSEMBLY, target_raw_size, target_desc))
    checks.check("target_descriptor_index", target_desc == 7, target_desc)
    manifest["store"] = {
        "magic": "XABA",
        "version": getattr(primary, "version", 1),
        "entry_count": primary.entry_count,
        "total_count": primary.total_count,
        "gap_bytes": len(primary.gap),
        "target_descriptor_index": target_desc,
    }
    checks.check("gap_present", len(primary.gap) > 0, "%d bytes" % len(primary.gap))

    # ------------------------------------------------- 3. authoritative size cap
    print("\n== 3. expected-size budget (libxamarin-app.so table) ==")
    table_offset, so_data = size_table_from_so(args.app_so, target_raw_size, target_desc)
    expected_size = read_size_table_value(so_data, table_offset, target_desc)
    matched, mismatched = 0, []
    for name, info in baseline_raw.items():
        if info["store"] != "primary":
            continue
        value = read_size_table_value(so_data, table_offset, info["desc"])
        if value == len(info["raw"]):
            matched += 1
        else:
            mismatched.append((name, info["desc"], value, len(info["raw"])))
    checks.check("size_table_self_check", matched >= EXPECTED_ENTRY_COUNT - 1,
                 "%d/%d entries match their uncompressed size" % (matched, EXPECTED_ENTRY_COUNT))
    checks.check("size_table_target", expected_size == target_raw_size,
                 "table[%d]=%d raw=%d" % (target_desc, expected_size, target_raw_size))
    print("  size table offset=%d; mismatched entries=%s"
          % (table_offset, [m[0] for m in mismatched] or "none"))
    manifest["size_budget"] = {
        "table_offset": table_offset,
        "descriptor_index": target_desc,
        "expected_size": expected_size,
        "entries_matching_uncompressed_size": matched,
        "entries_mismatched": [m[0] for m in mismatched],
    }

    # ------------------------------------------------------- 4. build injector
    print("\n== 4. build Tools/ModInjector ==")
    rc, out = run([args.dotnet, "build", INJECTOR_PROJ, "-c", "Release", "-v", "m",
                   "-o", injector_out])
    checks.check("injector_build", rc == 0, "rc=%d" % rc)
    if rc != 0:
        return 4
    injector_dll = os.path.join(injector_out, "ModInjector.dll")
    checks.check("injector_dll_present", os.path.isfile(injector_dll), injector_dll)
    if not os.path.isfile(injector_dll):
        return 4
    with open(os.path.join(logs_dir, "injector-build.log"), "w", encoding="utf-8") as f:
        f.write(out)
    manifest["tools"]["dotnet_build"] = subprocess.run(
        [args.dotnet, "--version"], stdout=subprocess.PIPE, universal_newlines=True
    ).stdout.strip()

    # ------------------------------------------------------------- 5. patch IL
    print("\n== 5. patch %s (Bootstrap.Start at OnCreatePartTwo) ==" % TARGET_ASSEMBLY)
    staged_target = os.path.join(refs_dir, TARGET_ASSEMBLY + ".dll")
    patched_target = os.path.join(patched_dir, TARGET_ASSEMBLY + ".dll")
    patch_cmd = [args.dotnet, injector_dll, "patch", staged_target, patched_target,
                 "--refs-dir", refs_dir, "--mods-dir", args.mods_dir,
                 "--loader-api", args.loader_api, "--hook-method", "OnCreatePartTwo"]
    rc, out = run(patch_cmd)
    with open(os.path.join(logs_dir, "injector-patch.log"), "w", encoding="utf-8") as f:
        f.write(out)
    checks.check("injector_patch", rc == 0, "rc=%d" % rc)
    if rc != 0 or not os.path.isfile(patched_target):
        return 4

    mvid_before = parse_kv(out, "old mvid")
    mvid_after = parse_kv(out, "new mvid")
    checks.check("mvid_changed", bool(mvid_before) and bool(mvid_after)
                 and mvid_before != mvid_after, "%s -> %s" % (mvid_before, mvid_after))

    # ------------------------------------------------------------ 6. verify IL
    print("\n== 6. verify patched %s ==" % TARGET_ASSEMBLY)
    verify_cmd = [args.dotnet, injector_dll, "verify", patched_target,
                  "--hook-method", "OnCreatePartTwo", "--mods-dir", args.mods_dir]
    rc, verify_out = run(verify_cmd)
    with open(os.path.join(logs_dir, "injector-verify.log"), "w", encoding="utf-8") as f:
        f.write(verify_out)
    checks.check("injector_verify", rc == 0 and "RESULT: OK" in verify_out, "rc=%d" % rc)
    if rc != 0:
        return 4

    hook_is_first = parse_kv(verify_out, "hook-is-first")
    loader_api = parse_kv(verify_out, "loader-api")
    baked_mods_dir = parse_kv(verify_out, "mods-dir")
    verified_mvid = parse_kv(verify_out, "mvid")
    bootstrap_type = parse_kv(verify_out, "bootstrap-type")
    bootstrap_start = parse_kv(verify_out, "bootstrap-start")
    checks.check("verify_bootstrap_type", bootstrap_type == "present", bootstrap_type)
    checks.check("verify_bootstrap_start", bootstrap_start.startswith("present"), bootstrap_start)
    checks.check("verify_hook_is_first", hook_is_first == "yes", hook_is_first)
    checks.check("verify_loader_api_loadfrom", loader_api.startswith("loadfrom"), loader_api)
    checks.check("verify_loader_api_not_bytes", "byte[]" not in loader_api, loader_api)
    checks.check("verify_mods_dir_external", baked_mods_dir == args.mods_dir, baked_mods_dir)
    checks.check("verify_not_private_mods_dir",
                 not baked_mods_dir.startswith("/data/data/"), baked_mods_dir)

    patched_sha = sha256_file(patched_target)
    patched_size = os.path.getsize(patched_target)
    checks.check("patched_size_within_budget", patched_size <= expected_size,
                 "%d <= %d" % (patched_size, expected_size))
    if patched_size > expected_size:
        print("FAIL patched assembly exceeds the descriptor's size table entry; refusing to "
              "build. Do not pad or change the descriptor index.")
        return 4
    manifest["patched"] = {
        "sha256": patched_sha,
        "bytes": patched_size,
        "mvid_before": mvid_before,
        "mvid_after": verified_mvid or mvid_after,
    }
    manifest["injector"] = {
        "command_patch": patch_cmd,
        "command_verify": verify_cmd,
        "hook": "OnCreatePartTwo",
        "hook_is_first": hook_is_first,
        "loader_api": loader_api,
        "mods_dir": baked_mods_dir,
        "bootstrap_type": bootstrap_type,
        "bootstrap_start": bootstrap_start,
    }

    # ------------------------------------------------------- 7. store replace
    print("\n== 7. replace only the existing %s entry (store.py replace) ==" % TARGET_ASSEMBLY)
    replace_cmd = [sys.executable, STORE_PY, "replace", args.blob, TARGET_ASSEMBLY,
                   patched_target, args.out_blob]
    rc, out = run(replace_cmd)
    with open(os.path.join(logs_dir, "store-replace.log"), "w", encoding="utf-8") as f:
        f.write(out)
    checks.check("store_replace", rc == 0 and os.path.isfile(args.out_blob), "rc=%d" % rc)
    if rc != 0:
        return 4

    # ---------------------------------------------------- 8. validate new blob
    print("\n== 8. validate the new blob ==")
    new_store = store_mod.Store(args.out_blob, args.manifest, "000")
    checks.check("new_entry_count", new_store.entry_count == EXPECTED_ENTRY_COUNT,
                 new_store.entry_count)
    checks.check("new_total_count", new_store.total_count == EXPECTED_TOTAL_COUNT,
                 new_store.total_count)
    checks.check("new_gap_preserved", new_store.gap == primary.gap, len(new_store.gap))
    checks.check("new_header_preserved",
                 (new_store.version, new_store.reserved) == (primary.version, primary.reserved),
                 "version=%s reserved=%s" % (new_store.version, primary.reserved))
    checks.check("new_entry_count_matches_names", len(new_store.names()) == len(names),
                 len(new_store.names()))

    unchanged, changed = 0, None
    for e in new_store.entries:
        raw, desc = new_store.decode(e)
        name = names[e["index"]]
        old = baseline_raw[name]
        if name == TARGET_ASSEMBLY:
            changed = (name, sha256_bytes(raw), desc)
            checks.check("target_sha_patched", sha256_bytes(raw) == patched_sha, sha256_bytes(raw))
            checks.check("target_bytes_patched", len(raw) == patched_size, len(raw))
            checks.check("target_descriptor_preserved", desc == target_desc, desc)
            checks.check("target_pe", raw[:2] == b"MZ", raw[:2])
            with open(os.path.join(patched_dir, "from-blob-" + TARGET_ASSEMBLY + ".dll"),
                      "wb") as f:
                f.write(raw)
        else:
            if sha256_bytes(raw) == sha256_bytes(old["raw"]) and desc == old["desc"]:
                unchanged += 1
            else:
                checks.check("entry_unchanged_" + name, False,
                             "sha or descriptor changed")
    checks.check("other_entries_unchanged", unchanged == EXPECTED_ENTRY_COUNT - 1,
                 "%d/%d" % (unchanged, EXPECTED_ENTRY_COUNT - 1))

    # Re-extract the injected assembly from the new blob and verify it again: verifying the
    # staging copy is not the same claim as verifying what actually landed in the container.
    reextracted = os.path.join(patched_dir, "from-blob-" + TARGET_ASSEMBLY + ".dll")
    rc, verify2 = run([args.dotnet, injector_dll, "verify", reextracted,
                       "--hook-method", "OnCreatePartTwo", "--mods-dir", args.mods_dir])
    with open(os.path.join(logs_dir, "injector-verify-from-blob.log"), "w", encoding="utf-8") as f:
        f.write(verify2)
    checks.check("verify_reextracted_from_blob", rc == 0 and "RESULT: OK" in verify2, "rc=%d" % rc)

    # Architecture blob and manifest are only ever read; prove it by hashing them again.
    checks.check("arch_blob_unchanged", sha256_file(args.arch_blob) == input_hashes["arch_blob"],
                 sha256_file(args.arch_blob))
    checks.check("manifest_unchanged", sha256_file(args.manifest) == input_hashes["manifest"],
                 sha256_file(args.manifest))
    checks.check("source_blob_unchanged", sha256_file(args.blob) == input_hashes["blob"],
                 sha256_file(args.blob))

    # store.py CLI cross-checks (the exact tool, not a re-implementation).
    print("\n== 9. store.py list / verify / selftest on the new blob ==")
    expects = {name: sha256_bytes(info["raw"]) for name, info in baseline_raw.items()
               if info["store"] == "primary"}
    expects[TARGET_ASSEMBLY] = patched_sha
    rc_list, list_out = run([sys.executable, STORE_PY, "list", args.out_blob,
                             "--manifest", args.manifest, "--blob-id", "000"])
    rc_verify, verify_store = run(
        [sys.executable, STORE_PY, "verify", args.out_blob,
         "--manifest", args.manifest, "--blob-id", "000"]
        + ["%s=%s" % (k, v) for k, v in sorted(expects.items())])
    rc_selftest, selftest_out = run([sys.executable, STORE_PY, "selftest", args.out_blob,
                                     "--manifest", args.manifest, "--blob-id", "000"])
    for name, text in (("store-list", list_out), ("store-verify", verify_store),
                       ("store-selftest", selftest_out)):
        with open(os.path.join(logs_dir, name + ".log"), "w", encoding="utf-8") as f:
            f.write(text)
    checks.check("store_cli_list", rc_list == 0, "rc=%d" % rc_list)
    checks.check("store_cli_verify", rc_verify == 0, "rc=%d" % rc_verify)
    checks.check("store_cli_selftest", rc_selftest == 0, "rc=%d" % rc_selftest)

    # ------------------------------------------------------------- 10. output
    out_sha = sha256_file(args.out_blob)
    manifest["output_blob"] = {
        "path": os.path.relpath(args.out_blob, ROOT).replace("\\", "/"),
        "sha256": out_sha,
        "bytes": os.path.getsize(args.out_blob),
    }
    manifest["unchanged"] = {
        "entries_compared": unchanged,
        "arch_blob_sha256": input_hashes["arch_blob"],
        "manifest_sha256": input_hashes["manifest"],
        "source_blob_sha256": input_hashes["blob"],
    }
    manifest["tools"]["python"] = platform.python_version()

    failed = checks.failed
    manifest["result"] = "ok" if not failed else "failed"
    manifest["failed_checks"] = [c["name"] for c in failed]
    with open(args.manifest_json, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, sort_keys=False)
        f.write("\n")

    print("\n== result ==")
    print("  checks      : %d passed, %d failed" % (len(checks.items) - len(failed), len(failed)))
    print("  patched dll : %s  %d bytes  mvid %s"
          % (patched_sha, patched_size, manifest["patched"]["mvid_after"]))
    print("  new blob    : %s  %d bytes  %s"
          % (out_sha, manifest["output_blob"]["bytes"], args.out_blob))
    print("  manifest    : " + args.manifest_json)
    if failed:
        print("  FAILED: " + ", ".join(c["name"] for c in failed))
        return 4
    print("  RESULT: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
