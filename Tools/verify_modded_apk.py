#!/usr/bin/env python3
"""Static acceptance for the mod-enabled APK (Phase 1.1.7).

Proves, without a device and without running the app:

  * the candidate really carries the injected StardewValley assembly (re-extracted from the
    APK and re-verified with ModInjector);
  * every other container entry is byte-identical to the source blob, and the architecture
    blob / manifest / native libraries / DEX / game resources are unchanged from the
    baseline APK (zip CRC + size comparison, entry by entry);
  * package id and version are still com.chucklefish.stardewvalley / 239 / 1.6.15.0;
  * zipalign and apksigner accept the file;
  * the candidate signer is compared with the baseline signer, and the result is classified
    as an install-admission status. A mismatch is reported as UPDATE_INCOMPATIBLE_RISK, never
    smoothed over: a different key cannot replace an installed package.

Usage (repo root):
    python Tools/verify_modded_apk.py \
        --candidate app/build/outputs/apk/modded/app-modded-debug.apk \
        --baseline  app/build/outputs/apk/debug/app-debug.apk \
        --blob-manifest .tmp/build-modded/modded-build-manifest.json \
        --report .tmp/build-modded/modded-apk-verify.json

Exit codes: 0 = every check passed (an install-admission BLOCKED is *not* a check failure),
3 = missing input, 4 = a check failed.
"""

import argparse
import hashlib
import json
import os
import platform
import subprocess
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, os.path.join(HERE, "AssemblyStore"))
import store as store_mod  # noqa: E402

PACKAGE = "com.chucklefish.stardewvalley"
VERSION_CODE = "239"
VERSION_NAME = "1.6.15.0"
BASELINE_APK_SHA256 = "65e1e2af5583b1e765ec58df7e19264dd08dc8c8b0cf67072c39f69925051f9d"
BASELINE_SIGNER_SHA256 = "611c8fa4eb0fdb6fe79f56da0e06f661269232483698d0d972d57f088524c5de"
TARGET_ASSEMBLY = "StardewValley"
SIGNATURE_PREFIX = "META-INF/"


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


class Checks(object):
    """Records PASS / FAIL / BLOCKED.

    BLOCKED is deliberately not FAIL: a candidate that is built and verified but cannot be
    installed over the existing package (different signing key) is a gate that stays shut,
    not a broken artifact. Only FAIL fails the run.
    """

    def __init__(self):
        self.items = []

    def check(self, name, ok, detail="", blocked_if_false=False):
        status = "PASS" if ok else ("BLOCKED" if blocked_if_false else "FAIL")
        self.items.append({"name": name, "status": status, "ok": bool(ok), "detail": str(detail)})
        print("%s %-44s %s" % ("ok   " if ok else ("BLOCK " if blocked_if_false else "FAIL  "),
                               name, detail))
        return ok

    @property
    def failed(self):
        return [c for c in self.items if c["status"] == "FAIL"]

    @property
    def blocked(self):
        return [c for c in self.items if c["status"] == "BLOCKED"]


def run(cmd):
    print("$ " + " ".join(cmd))
    proc = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                          universal_newlines=True, encoding="utf-8", errors="replace")
    return proc.returncode, proc.stdout


def find_build_tools(explicit):
    if explicit:
        return explicit
    roots = [os.environ.get("ANDROID_HOME"), os.environ.get("ANDROID_SDK_ROOT"),
             os.path.join(os.environ.get("LOCALAPPDATA", ""), "Android", "Sdk")]
    candidates = []
    for root in roots:
        if not root:
            continue
        base = os.path.join(root, "build-tools")
        if os.path.isdir(base):
            for name in os.listdir(base):
                folder = os.path.join(base, name)
                if os.path.isdir(folder):
                    candidates.append((name, folder))
    if not candidates:
        return ""
    candidates.sort(key=lambda pair: [int(p) if p.isdigit() else 0 for p in pair[0].split(".")])
    return candidates[-1][1]


def tool(build_tools, name):
    for suffix in (".exe", ".bat", ".cmd", ""):
        path = os.path.join(build_tools, name + suffix)
        if os.path.isfile(path):
            return path
    return ""


def parse_certs(text):
    """Returns the SHA-256 digests apksigner printed for the signers."""
    digests = []
    for line in text.splitlines():
        if "certificate SHA-256 digest:" in line:
            digests.append(line.split(":", 2)[-1].strip())
    return digests


def zip_index(path):
    with zipfile.ZipFile(path) as zf:
        return {info.filename: (info.CRC, info.file_size) for info in zf.infolist()}


def read_zip_entry(path, name):
    with zipfile.ZipFile(path) as zf:
        return zf.read(name)


def main(argv):
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--candidate", required=True)
    ap.add_argument("--baseline", required=True)
    ap.add_argument("--blob-manifest", required=True,
                    help="modded-build-manifest.json written by Tools/build_modded_apk.py")
    ap.add_argument("--report", required=True)
    ap.add_argument("--source-apkroot", default=os.path.join(
        ROOT, "app", "src", "main", "apkroot", "assemblies"))
    ap.add_argument("--build-tools", default="")
    ap.add_argument("--keystore", default="")
    ap.add_argument("--installed-apk-sha256", default="",
                    help="device sha256 of the installed base.apk, if E0 measured it")
    ap.add_argument("--dotnet", default="dotnet",
                    help="SDK used to run ModInjector verify on the re-extracted assembly")
    args = ap.parse_args(argv)

    for path in (args.candidate, args.baseline, args.blob_manifest):
        if not os.path.isfile(path):
            print("missing input: " + path)
            return 3

    checks = Checks()
    report = {"schema": 1, "phase": "1.1.7", "host": platform.platform(),
              "checks": checks.items}

    with open(args.blob_manifest, "r", encoding="utf-8") as f:
        blob_manifest = json.load(f)

    # -------------------------------------------------------------- file facts
    print("\n== 1. artifact facts ==")
    cand_sha = sha256_file(args.candidate)
    cand_size = os.path.getsize(args.candidate)
    base_sha = sha256_file(args.baseline)
    base_size = os.path.getsize(args.baseline)
    report["candidate"] = {
        "path": os.path.relpath(args.candidate, ROOT).replace("\\", "/"),
        "sha256": cand_sha, "bytes": cand_size,
    }
    report["baseline"] = {
        "path": os.path.relpath(args.baseline, ROOT).replace("\\", "/"),
        "sha256": base_sha, "bytes": base_size,
    }
    print("  candidate %s  %d bytes" % (cand_sha, cand_size))
    print("  baseline  %s  %d bytes" % (base_sha, base_size))

    checks.check("baseline_apk_unchanged", base_sha == BASELINE_APK_SHA256, base_sha)
    checks.check("candidate_differs_from_baseline", cand_sha != base_sha, cand_sha)

    # ------------------------------------------------------------ package/version
    print("\n== 2. package and version ==")
    build_tools = find_build_tools(args.build_tools)
    checks.check("build_tools_found", bool(build_tools), build_tools)
    if not build_tools:
        return 4
    aapt2 = tool(build_tools, "aapt2")
    rc, badging = run([aapt2, "dump", "badging", args.candidate])
    checks.check("aapt2_badging", rc == 0, "rc=%d" % rc)
    package_line = ""
    version_line = ""
    for line in badging.splitlines():
        if line.startswith("package:"):
            package_line = line
        if line.startswith("sdkVersion:") or line.startswith("targetSdkVersion:"):
            pass
    import re
    m = re.search(r"name='([^']+)' versionCode='([^']*)' versionName='([^']*)'", badging)
    if m:
        package_line = m.group(1)
        report["package"] = {"name": m.group(1), "versionCode": m.group(2),
                             "versionName": m.group(3)}
        checks.check("package_name", m.group(1) == PACKAGE, m.group(1))
        checks.check("version_code", m.group(2) == VERSION_CODE, m.group(2))
        checks.check("version_name", m.group(3) == VERSION_NAME, m.group(3))
    else:
        checks.check("package_parsed", False, package_line or "no package: line")

    # ------------------------------------------------------ container comparison
    print("\n== 3. every other APK entry is unchanged ==")
    cand_zip = zip_index(args.candidate)
    base_zip = zip_index(args.baseline)
    report["zip"] = {"candidate_entries": len(cand_zip), "baseline_entries": len(base_zip)}
    checks.check("entry_count_matches", len(cand_zip) == len(base_zip),
                 "%d vs %d" % (len(cand_zip), len(base_zip)))

    added = sorted(set(cand_zip) - set(base_zip))
    removed = sorted(set(base_zip) - set(cand_zip))
    changed = sorted(n for n in set(cand_zip) & set(base_zip) if cand_zip[n] != base_zip[n])

    unexpected_added = [n for n in added if not n.startswith(SIGNATURE_PREFIX)]
    unexpected_removed = [n for n in removed if not n.startswith(SIGNATURE_PREFIX)]
    allowed_changed = ["assemblies/assemblies.blob"]
    unexpected_changed = [n for n in changed
                          if n not in allowed_changed and not n.startswith(SIGNATURE_PREFIX)]
    checks.check("no_unexpected_entries_added", not unexpected_added, unexpected_added[:5])
    checks.check("no_unexpected_entries_removed", not unexpected_removed, unexpected_removed[:5])
    checks.check("only_blob_and_signature_changed", not unexpected_changed,
                 unexpected_changed[:8] or "only assemblies.blob + META-INF")
    report["zip"]["changed_entries"] = changed
    report["zip"]["added_entries"] = added
    report["zip"]["removed_entries"] = removed

    # Named entries that must exist and must be identical to the baseline.
    for name in ("classes.dex", "classes2.dex", "assemblies/assemblies.manifest",
                 "assemblies/assemblies.arm64_v8a.blob",
                 "lib/arm64-v8a/libxamarin-app.so", "lib/arm64-v8a/libmonodroid.so",
                 "assets/Content/XACT/music.xwb"):
        same = cand_zip.get(name) == base_zip.get(name)
        checks.check("unchanged_" + name.replace("/", "_"), same,
                     "crc=%s" % (cand_zip.get(name),))

    # --------------------------------------------------- injected container check
    print("\n== 4. the APK carries the injected assemblies blob ==")
    apk_blob = read_zip_entry(args.candidate, "assemblies/assemblies.blob")
    apk_blob_sha = hashlib.sha256(apk_blob).hexdigest()
    expected_blob_sha = blob_manifest["output_blob"]["sha256"]
    checks.check("apk_blob_is_built_blob", apk_blob_sha == expected_blob_sha,
                 "%s vs %s" % (apk_blob_sha, expected_blob_sha))
    report["apk_blob"] = {"sha256": apk_blob_sha, "bytes": len(apk_blob)}

    tmp_blob = os.path.join(os.path.dirname(os.path.abspath(args.report)), "apk-assemblies.blob")
    os.makedirs(os.path.dirname(tmp_blob), exist_ok=True)
    with open(tmp_blob, "wb") as f:
        f.write(apk_blob)

    apk_store = store_mod.Store(tmp_blob, os.path.join(args.source_apkroot, "assemblies.manifest"),
                                "000")
    checks.check("apk_entry_count", apk_store.entry_count == 58, apk_store.entry_count)
    checks.check("apk_total_count", apk_store.total_count == 65, apk_store.total_count)

    source_store = store_mod.Store(os.path.join(args.source_apkroot, "assemblies.blob"),
                                   os.path.join(args.source_apkroot, "assemblies.manifest"), "000")
    names = source_store.names()
    order = source_store.names()
    injected_ok = False
    unchanged = 0
    desc_ok = False
    for e in apk_store.entries:
        raw, desc = apk_store.decode(e)
        name = order[e["index"]]
        if name == TARGET_ASSEMBLY:
            with open(os.path.join(os.path.dirname(tmp_blob), "apk-" + TARGET_ASSEMBLY + ".dll"),
                      "wb") as f:
                f.write(raw)
            injected_ok = hashlib.sha256(raw).hexdigest() == blob_manifest["patched"]["sha256"]
            desc_ok = desc == 7
        else:
            old, old_desc = source_store.decode(source_store.entries[e["index"]])
            if hashlib.sha256(raw).hexdigest() == hashlib.sha256(old).hexdigest() \
                    and desc == old_desc:
                unchanged += 1
    checks.check("apk_target_is_patched_dll", injected_ok, blob_manifest["patched"]["sha256"])
    checks.check("apk_target_descriptor_7", desc_ok, "descriptor_index=7")
    checks.check("apk_other_entries_unchanged", unchanged == 57, "%d/57" % unchanged)

    # Re-verify the assembly that came out of the APK, not the one we put in.
    injector_dll = os.path.join(os.path.dirname(os.path.abspath(args.blob_manifest)),
                                "injector", "ModInjector.dll")
    if os.path.isfile(injector_dll):
        rc, out = run([args.dotnet, injector_dll, "verify",
                       os.path.join(os.path.dirname(tmp_blob), "apk-" + TARGET_ASSEMBLY + ".dll"),
                       "--mods-dir", blob_manifest["injector"]["mods_dir"]])
        checks.check("modinjector_verify_from_apk", rc == 0 and "RESULT: OK" in out, "rc=%d" % rc)
    else:
        checks.check("modinjector_verify_from_apk", False, "ModInjector.dll not found next to manifest")

    # Source tree must still be untouched.
    for name, path in (("source_blob", os.path.join(args.source_apkroot, "assemblies.blob")),
                       ("source_arch_blob",
                        os.path.join(args.source_apkroot, "assemblies.arm64_v8a.blob")),
                       ("source_manifest",
                        os.path.join(args.source_apkroot, "assemblies.manifest"))):
        expected = blob_manifest["inputs"].get("blob" if name == "source_blob" else
                                              "arch_blob" if name == "source_arch_blob" else
                                              "manifest", {}).get("sha256", "")
        checks.check(name + "_unchanged", sha256_file(path) == expected, sha256_file(path))

    # ----------------------------------------------------------- zipalign/signing
    print("\n== 5. zipalign and signature ==")
    zipalign = tool(build_tools, "zipalign")
    rc, align_out = run([zipalign, "-c", "-p", "4", args.candidate])
    checks.check("zipalign_verify", rc == 0, "rc=%d %s" % (rc, align_out.strip()[:60]))

    apksigner = tool(build_tools, "apksigner")
    rc, cand_certs = run([apksigner, "verify", "--print-certs", "--verbose", args.candidate])
    checks.check("apksigner_candidate", rc == 0, "rc=%d" % rc)
    rc, base_certs = run([apksigner, "verify", "--print-certs", "--verbose", args.baseline])
    checks.check("apksigner_baseline", rc == 0, "rc=%d" % rc)

    cand_digests = parse_certs(cand_certs)
    base_digests = parse_certs(base_certs)
    report["signature"] = {
        "candidate_signer_sha256": cand_digests[0] if cand_digests else "",
        "baseline_signer_sha256": base_digests[0] if base_digests else "",
        "baseline_recorded_signer_sha256": BASELINE_SIGNER_SHA256,
        "keystore": args.keystore,
        "keystore_exists": os.path.isfile(args.keystore) if args.keystore else None,
        "scheme_v1": "v1 scheme (JAR signing): true" in cand_certs,
        "scheme_v2": "v2 scheme (APK Signature Scheme v2): true" in cand_certs,
        "scheme_v3": "v3 scheme (APK Signature Scheme v3): true" in cand_certs,
        "installed_apk_sha256": args.installed_apk_sha256,
    }
    checks.check("signer_reported", bool(cand_digests) and bool(base_digests),
                 "%s vs %s" % (cand_digests[:1], base_digests[:1]))
    checks.check("baseline_signer_matches_record", base_digests[:1] == [BASELINE_SIGNER_SHA256],
                 base_digests[:1])
    checks.check("signing_schemes_v1v2v3",
                 report["signature"]["scheme_v1"] and report["signature"]["scheme_v2"]
                 and report["signature"]["scheme_v3"],
                 "v1/v2/v3")

    installed_matches = None
    if args.installed_apk_sha256:
        installed_matches = args.installed_apk_sha256 == base_sha
        checks.check("installed_apk_equals_baseline", installed_matches,
                     args.installed_apk_sha256)
    report["signature"]["installed_equals_baseline"] = installed_matches

    same_signer = bool(cand_digests) and bool(base_digests) and cand_digests[0] == base_digests[0]
    checks.check("candidate_signer_equals_baseline", same_signer,
                 cand_digests[0] if cand_digests else "", blocked_if_false=True)

    # Install admission. A mismatch is not a build failure - it is a BLOCKED admission,
    # because replacing the installed package would need a different key it does not have.
    if same_signer:
        admission = "INSTALL_CANDIDATE"
        reason = "candidate signer equals the baseline/installed signer"
    else:
        admission = "UPDATE_INCOMPATIBLE_RISK"
        reason = ("candidate signer %s differs from the installed/baseline signer %s; "
                  "adb install -r would fail with INSTALL_FAILED_UPDATE_INCOMPATIBLE and an "
                  "uninstall is not permitted (it deletes saves)"
                  % ((cand_digests[0] if cand_digests else "?"),
                     (base_digests[0] if base_digests else "?")))
    if args.keystore and not os.path.isfile(args.keystore):
        admission = "SIGNING_KEY_UNAVAILABLE"
        reason = "keystore %s does not exist" % args.keystore
    report["install_admission"] = {"status": admission, "reason": reason,
                                   "adb_install_allowed": same_signer}

    print("\n== 6. result ==")
    failed = checks.failed
    blocked = checks.blocked
    report["result"] = "ok" if not failed else "failed"
    report["failed_checks"] = [c["name"] for c in failed]
    report["blocked_checks"] = [c["name"] for c in blocked]
    os.makedirs(os.path.dirname(os.path.abspath(args.report)), exist_ok=True)
    with open(args.report, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2)
        f.write("\n")

    print("  checks     : %d passed, %d failed, %d blocked"
          % (len(checks.items) - len(failed) - len(blocked), len(failed), len(blocked)))
    print("  admission  : %s" % admission)
    print("  reason     : %s" % reason)
    print("  report     : %s" % args.report)
    if failed:
        print("  FAILED: " + ", ".join(c["name"] for c in failed))
        return 4
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
