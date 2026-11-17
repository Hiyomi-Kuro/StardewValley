#!/usr/bin/env python3
"""Static acceptance for the mod-enabled APK, plus a separate install-admission decision.

Proves, without running the app:

  * the candidate really carries the injected StardewValley assembly (re-extracted from the
    APK and re-verified with ModInjector);
  * every other container entry is byte-identical to the source blob, and the architecture
    blob / manifest / native libraries / DEX / game resources are unchanged from the CONTENT
    BASELINE APK (zip CRC + size comparison, entry by entry);
  * package id and version are still com.chucklefish.stardewvalley / 239 / 1.6.15.0;
  * zipalign and apksigner accept the file.

Three artifacts are kept separate (Phase 2.0.1):

    baseline  = content reference (the unmodified debug build)
    candidate = the APK built now
    installed = the APK actually on the device, with its own signer

Install admission is decided by ``Tools/apk_admission.py`` from candidate + installed evidence;
the baseline signer is recorded but never treated as the device's signer. With no installed
evidence the verdict is NOT_VERIFIED (UNKNOWN), not PASS and not BLOCKED. Use ``--installed-apk``
(a pulled base.apk) or ``--installed-signer-sha256``; ``--installed-absent`` states a first
install explicitly. ``--installed-unreadable`` records why evidence could not be read.

Usage (repo root):
    python Tools/verify_modded_apk.py \
        --candidate app/build/outputs/apk/modded/app-modded-debug.apk \
        --baseline  app/build/outputs/apk/debug/app-debug.apk \
        --blob-manifest .tmp/build-modded/modded-build-manifest.json \
        --installed-apk .tmp/device/installed-base.apk \
        --report .tmp/build-modded/modded-apk-verify.json

Exit codes: 0 = every check passed (BLOCKED/UNKNOWN admission statuses are *not* check
failures), 3 = missing input, 4 = a check failed.
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
sys.path.insert(0, HERE)
import apk_admission  # noqa: E402
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

    def check(self, name, ok, detail="", blocked_if_false=False, unknown_if_false=False):
        if ok:
            status = "PASS"
        elif unknown_if_false:
            # "The evidence is missing" is not the same as "the artifact is wrong": it must be
            # reported as UNKNOWN, never as PASS and never as a check failure.
            status = "UNKNOWN"
        elif blocked_if_false:
            status = "BLOCKED"
        else:
            status = "FAIL"
        self.items.append({"name": name, "status": status, "ok": bool(ok), "detail": str(detail)})
        if status == "PASS":
            tag = "ok   "
        elif status == "UNKNOWN":
            tag = "UNKWN"
        elif status == "BLOCKED":
            tag = "BLOCK"
        else:
            tag = "FAIL "
        print("%s %-44s %s" % (tag, name, detail))
        return ok

    @property
    def failed(self):
        return [c for c in self.items if c["status"] == "FAIL"]

    @property
    def blocked(self):
        return [c for c in self.items if c["status"] == "BLOCKED"]

    @property
    def unknown(self):
        return [c for c in self.items if c["status"] == "UNKNOWN"]


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
    ap.add_argument("--installed-apk", default="",
                    help="local copy of the INSTALLED base.apk pulled from the device; the only "
                         "evidence that can decide signature admission")
    ap.add_argument("--installed-signer-sha256", default="",
                    help="installed signer digest measured another way (comma separated); used "
                         "instead of --installed-apk when the APK itself was not pulled")
    ap.add_argument("--installed-apk-sha256", default="",
                    help="device sha256 of the installed base.apk (plain adb shell sha256sum)")
    ap.add_argument("--installed-absent", action="store_true",
                    help="the package is explicitly not installed (first install)")
    ap.add_argument("--installed-unreadable", default="",
                    help="why the installed signature/hash could not be read; classifies UNKNOWN")
    ap.add_argument("--installed-version-code", default="")
    ap.add_argument("--installed-version-name", default="")
    ap.add_argument("--dotnet", default="dotnet",
                    help="SDK used to run ModInjector verify on the re-extracted assembly")
    ap.add_argument("--expect-events", default="auto", choices=["auto", "enabled", "disabled"],
                    help="Phase 2.0: expected event-hook state; auto reads it from the blob manifest")
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

    expect_events = args.expect_events
    if expect_events == "auto":
        expect_events = blob_manifest.get("event_hooks", "enabled")
    event_flag = [] if expect_events == "enabled" else ["--no-event-hooks"]
    report["expect_events"] = expect_events
    report["blob_manifest_events"] = blob_manifest.get("event_hooks", "(none)")

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
                       "--mods-dir", blob_manifest["injector"]["mods_dir"]] + event_flag)
        checks.check("modinjector_verify_from_apk", rc == 0 and "RESULT: OK" in out, "rc=%d" % rc)
        # The APK's own assembly must carry exactly the hook set the build claimed. These are
        # read from the re-extracted file, not from the build manifest.
        checks.check("blob_manifest_event_mode",
                     blob_manifest.get("event_hooks") == expect_events,
                     "%s vs %s" % (blob_manifest.get("event_hooks"), expect_events))
        reported = parse_kv(out, "event-hooks")
        launched = parse_kv(out, "game-launched-hook")
        tick = parse_kv(out, "tick-hook")
        checks.check("apk_report_event_mode", reported == expect_events, reported)
        if expect_events == "enabled":
            checks.check("apk_has_game_launched_hook", launched.startswith("present"), launched)
            checks.check("apk_has_tick_hook", tick.startswith("present"), tick)
        else:
            checks.check("apk_has_no_game_launched_hook", launched.startswith("absent"), launched)
            checks.check("apk_has_no_tick_hook", tick.startswith("absent"), tick)
        report["apk_injector_verify"] = {
            "event_hooks": reported,
            "game_launched_hook": launched,
            "tick_hook": tick,
        }
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

    cand_digests = apk_admission.normalize_digests(parse_certs(cand_certs))
    base_digests = apk_admission.normalize_digests(parse_certs(base_certs))

    # ---- installed evidence: its own artifact, never the baseline ------------------------
    installed = None
    installed_note = "no installed-APK evidence supplied"
    if args.installed_absent:
        installed = {"present": False}
        installed_note = "explicitly absent (first install)"
    elif args.installed_unreadable:
        installed = {"present": True, "signer_digests": [],
                     "signature_read_error": args.installed_unreadable}
        installed_note = "unreadable: " + args.installed_unreadable
    elif args.installed_apk:
        if not os.path.isfile(args.installed_apk):
            installed = {"present": True, "signer_digests": [],
                         "signature_read_error": "installed APK path does not exist"}
            installed_note = "missing installed APK copy"
        else:
            inst_sha = sha256_file(args.installed_apk)
            rc_i, inst_certs = run([apksigner, "verify", "--print-certs", "--verbose",
                                    args.installed_apk])
            inst_digests = apk_admission.normalize_digests(parse_certs(inst_certs))
            inst_package = ""
            inst_version_code = args.installed_version_code
            inst_version_name = args.installed_version_name
            rc_b, inst_badging = run([aapt2, "dump", "badging", args.installed_apk])
            if rc_b == 0:
                m_inst = re.search(r"name='([^']+)' versionCode='([^']*)' versionName='([^']*)'",
                                   inst_badging)
                if m_inst:
                    inst_package = m_inst.group(1)
                    if not inst_version_code:
                        inst_version_code = m_inst.group(2)
                    if not inst_version_name:
                        inst_version_name = m_inst.group(3)
            installed = {
                "present": True,
                "signer_digests": inst_digests if rc_i == 0 else [],
                "signature_read_error": "" if rc_i == 0 else "apksigner exited %d" % rc_i,
                "apk_sha256": inst_sha,
                "package": inst_package,
                "version_code": inst_version_code,
                "version_name": inst_version_name,
            }
            installed_note = ("pulled installed APK sha256=" + inst_sha[:16] +
                              " signers=" + str(len(inst_digests)))
    elif args.installed_signer_sha256:
        installed = {
            "present": True,
            "signer_digests": apk_admission.normalize_digests(
                args.installed_signer_sha256.split(",")),
            "apk_sha256": args.installed_apk_sha256,
            "package": PACKAGE,
            "version_code": args.installed_version_code,
            "version_name": args.installed_version_name,
        }
        installed_note = "signer supplied by the operator"
    elif args.installed_apk_sha256:
        installed = {
            "present": True,
            "signer_digests": [],
            "signature_read_error": "only the APK hash was supplied; the signer is unknown",
            "apk_sha256": args.installed_apk_sha256,
        }
        installed_note = "hash only (no signer evidence)"

    candidate_evidence = {
        "signer_digests": cand_digests,
        "apk_sha256": cand_sha,
        "package": report.get("package", {}).get("name"),
        "version_code": report.get("package", {}).get("versionCode"),
        "version_name": report.get("package", {}).get("versionName"),
        "signature_read_error": "" if cand_digests else "apksigner produced no certificate digest",
    }
    baseline_evidence = {"signer_digests": base_digests, "apk_sha256": base_sha}

    decision = apk_admission.evaluate(
        candidate_evidence, baseline_evidence, installed, PACKAGE, VERSION_CODE, VERSION_NAME)

    report["signature"] = {
        "candidate_signer_sha256": cand_digests[0] if cand_digests else "",
        "candidate_signer_sha256_all": cand_digests,
        "baseline_signer_sha256": base_digests[0] if base_digests else "",
        "baseline_signer_sha256_all": base_digests,
        "baseline_recorded_signer_sha256": BASELINE_SIGNER_SHA256,
        "keystore": args.keystore,
        "keystore_exists": os.path.isfile(args.keystore) if args.keystore else None,
        "scheme_v1": "v1 scheme (JAR signing): true" in cand_certs,
        "scheme_v2": "v2 scheme (APK Signature Scheme v2): true" in cand_certs,
        "scheme_v3": "v3 scheme (APK Signature Scheme v3): true" in cand_certs,
        "installed_apk_sha256": installed.get("apk_sha256") if installed else "",
        "installed_signer_sha256_all": (installed.get("signer_digests") if installed else []) or [],
        "installed_apk_source": installed_note,
        "baseline_matches_candidate": decision["baseline_matches_candidate"],
    }
    checks.check("signer_reported", bool(cand_digests) and bool(base_digests),
                 "%s vs %s" % (cand_digests[:1], base_digests[:1]))
    checks.check("baseline_signer_matches_record", base_digests[:1] == [BASELINE_SIGNER_SHA256],
                 base_digests[:1])
    checks.check("signing_schemes_v1v2v3",
                 report["signature"]["scheme_v1"] and report["signature"]["scheme_v2"]
                 and report["signature"]["scheme_v3"],
                 "v1/v2/v3")

    # The candidate is this build's own artifact: an unreadable candidate certificate is a
    # build failure, unlike missing device evidence (which stays UNKNOWN).
    checks.check("candidate_signature_readable", bool(cand_digests),
                 "%d signer(s)" % len(cand_digests))

    # The baseline signer is a build fact, not device evidence. The comparison is recorded in
    # report["signature"]["baseline_matches_candidate"] and never becomes an install verdict.
    admission_status = decision["status"]
    if admission_status in (apk_admission.CANDIDATE, apk_admission.FIRST_INSTALL):
        checks.check("install_admission", True, admission_status)
    elif admission_status in (apk_admission.NOT_VERIFIED, apk_admission.SIGNATURE_UNREADABLE):
        checks.check("install_admission", False,
                     admission_status + ": " + decision["reason"], unknown_if_false=True)
    else:
        checks.check("install_admission", False,
                     admission_status + ": " + decision["reason"], blocked_if_false=True)

    installed_sha = decision.get("installed_apk_sha256") or ""
    post_install = decision.get("post_install_hash_matches")
    if post_install is True:
        checks.check("post_install_hash_matches_candidate", True, installed_sha)
    elif installed_sha and installed_sha == base_sha:
        checks.check("post_install_hash_matches_candidate", False,
                     "installed APK is still the baseline build (pre-update state)",
                     unknown_if_false=True)
    else:
        checks.check("post_install_hash_matches_candidate", False,
                     installed_sha or "no installed APK hash evidence",
                     unknown_if_false=True)

    report["install_admission"] = {
        "status": admission_status,
        "reason": decision["reason"],
        "adb_install_allowed": decision["adb_install_allowed"],
        "same_signature": decision["same_signature"],
        "candidate_signer_sha256": decision["candidate_signer_sha256"],
        "installed_signer_sha256": decision["installed_signer_sha256"],
        "baseline_signer_sha256": decision["baseline_signer_sha256"],
        "baseline_matches_candidate": decision["baseline_matches_candidate"],
        "installed_apk_sha256": installed_sha,
        "post_install_hash_matches": post_install,
        "installed_evidence": installed_note,
    }

    print("\n== 6. result ==")
    failed = checks.failed
    blocked = checks.blocked
    unknown = checks.unknown
    report["result"] = "ok" if not failed else "failed"
    report["failed_checks"] = [c["name"] for c in failed]
    report["blocked_checks"] = [c["name"] for c in blocked]
    report["unknown_checks"] = [c["name"] for c in unknown]
    os.makedirs(os.path.dirname(os.path.abspath(args.report)), exist_ok=True)
    with open(args.report, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2)
        f.write("\n")

    passed = len(checks.items) - len(failed) - len(blocked) - len(unknown)
    print("  checks     : %d passed, %d failed, %d blocked, %d unknown"
          % (passed, len(failed), len(blocked), len(unknown)))
    print("  admission  : %s" % admission_status)
    print("  reason     : %s" % decision["reason"])
    print("  installed  : %s" % installed_note)
    print("  report     : %s" % args.report)
    if failed:
        print("  FAILED: " + ", ".join(c["name"] for c in failed))
        return 4
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
