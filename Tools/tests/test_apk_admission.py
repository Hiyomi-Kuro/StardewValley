#!/usr/bin/env python3
"""Synthetic evidence tests for the APK install-admission model (Tools/apk_admission.py).

No APK, no device, no game binaries: every case is plain data. The four scenarios required by
the Phase 2.0.1 review are covered explicitly, including the two that the old predicate (compare
candidate signer with the content baseline) got backwards.

Usage (repo root):
    python Tools/tests/test_apk_admission.py
Exit code 0 = every check passed.
"""

import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "Tools"))
import apk_admission as admission  # noqa: E402

RESULTS = []
PACKAGE = "com.chucklefish.stardewvalley"
SIGNER_A = "a" * 64
SIGNER_B = "b" * 64
SIGNER_C = "c" * 64


def check(name, ok, detail=""):
    RESULTS.append((name, bool(ok), detail))
    print("%s %-52s %s" % ("PASS" if ok else "FAIL", name, detail))
    return ok


def candidate(signer=SIGNER_A, sha="1" * 64, package=PACKAGE, code="239", name="1.6.15.0"):
    return {"signer_digests": [signer], "apk_sha256": sha, "package": package,
            "version_code": code, "version_name": name}


def baseline(signer=SIGNER_A, sha="0" * 64):
    return {"signer_digests": [signer], "apk_sha256": sha}


def installed(signer=SIGNER_A, sha="e" * 64, package=PACKAGE, code="239"):
    return {"present": True, "signer_digests": [signer], "apk_sha256": sha,
            "package": package, "version_code": code}


def run(cand, base, inst, **kwargs):
    return admission.evaluate(cand, base, inst, PACKAGE, "239", "1.6.15.0", **kwargs)


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    print("APK install-admission synthetic tests\n")

    # ---- the defect: the old predicate compared the CANDIDATE with the BASELINE ----------
    # 1. baseline key != candidate key, but the device already runs the candidate key.
    #    The old predicate says "incompatible"; the installed evidence says "same as device".
    cand = candidate(SIGNER_C)
    base = baseline(SIGNER_A)
    inst = installed(SIGNER_C)
    legacy = admission.legacy_same_signer_decision(cand, base)
    result = run(cand, base, inst)
    check("baseline_differs_candidate_installed_same_is_installable",
          legacy is False and result["status"] == admission.CANDIDATE and
          result["adb_install_allowed"] is True,
          "legacy=%s -> %s" % (legacy, result["status"]))

    # 2. baseline == candidate, but the device is signed with a DIFFERENT key. The old
    #    predicate would allow an install that Android refuses.
    cand = candidate(SIGNER_A)
    base = baseline(SIGNER_A)
    inst = installed(SIGNER_C)
    legacy = admission.legacy_same_signer_decision(cand, base)
    result = run(cand, base, inst)
    check("baseline_same_candidate_installed_differs_is_blocked",
          legacy is True and result["status"] == admission.INCOMPATIBLE and
          result["adb_install_allowed"] is False,
          "legacy=%s -> %s" % (legacy, result["status"]))

    # 3. No installed evidence at all: never guess, never reuse the baseline as "installed".
    result = run(candidate(), baseline(), None)
    check("no_installed_evidence_is_not_verified",
          result["status"] == admission.NOT_VERIFIED and
          result["adb_install_allowed"] is False and
          result["installed_signer_sha256"] is None,
          result["status"])
    check("no_installed_evidence_does_not_invent_a_signer",
          "baseline_signer_sha256" in result and result["installed_signer_sha256"] is None,
          "installed=None while baseline is recorded separately")

    # 4. The installed certificate cannot be read: unknown, not allowed.
    broken = installed()
    broken["signer_digests"] = []
    broken["signature_read_error"] = "apksigner exited 2"
    result = run(candidate(), baseline(), broken)
    check("installed_signature_unreadable_is_refused",
          result["status"] == admission.SIGNATURE_UNREADABLE and
          result["adb_install_allowed"] is False,
          result["reason"])

    # 5. The installed APK hash cannot be read: same classification, hash stays unknown.
    no_hash = installed()
    no_hash["apk_sha256"] = ""
    result = run(candidate(), baseline(), no_hash)
    check("installed_hash_missing_is_still_decidable_by_signature",
          result["status"] == admission.CANDIDATE and
          result["post_install_hash_matches"] is None,
          "signature decides admission; hash proof is separate")

    # 6. More than one signer digest: multi-signer / rotation lineage is not implemented.
    multi = candidate()
    multi["signer_digests"] = [SIGNER_A, SIGNER_B]
    result = run(multi, baseline(), installed())
    check("candidate_multi_signer_unsupported",
          result["status"] == admission.MULTI_SIGNER and
          result["adb_install_allowed"] is False, result["status"])
    multi_inst = installed()
    multi_inst["signer_digests"] = [SIGNER_A, SIGNER_B]
    result = run(candidate(), baseline(), multi_inst)
    check("installed_multi_signer_unsupported",
          result["status"] == admission.MULTI_SIGNER and
          result["adb_install_allowed"] is False, result["status"])

    # 7. Explicit "package absent" is a first install, not an unknown.
    result = run(candidate(), baseline(), {"present": False})
    check("explicit_absent_package_is_first_install",
          result["status"] == admission.FIRST_INSTALL and result["adb_install_allowed"] is True,
          result["status"])

    # 8. Package / version identity differences fail closed.
    result = run(candidate(package="com.example.other"), baseline(), installed())
    check("candidate_package_mismatch_refused",
          result["status"] == admission.PACKAGE_MISMATCH, result["status"])
    result = run(candidate(sha="1" * 64, code="240", name="1.6.16.0"), baseline(), installed())
    check("candidate_version_mismatch_refused",
          result["status"] == admission.PACKAGE_MISMATCH, result["status"])
    result = run(candidate(), baseline(), installed(package="com.example.other"))
    check("installed_package_mismatch_refused",
          result["status"] == admission.PACKAGE_MISMATCH, result["status"])

    # 9. A downgrade is a separate platform constraint, not a signature problem.
    result = run(candidate(code="239", name="1.6.15.0"), baseline(), installed(code="250"))
    check("installed_newer_version_is_downgrade_risk",
          result["status"] == admission.DOWNGRADE and
          result["adb_install_allowed"] is False, result["status"])

    # 10. The post-install hash proof is independent of admission and never treats the
    #     baseline hash as evidence about the device.
    cand = candidate(sha="2" * 64)
    base = baseline(sha="0" * 64)
    result = run(cand, base, installed(sha="2" * 64))
    check("post_install_hash_equal_to_candidate_is_proven",
          result["status"] == admission.CANDIDATE and
          result["post_install_hash_matches"] is True, str(result["post_install_hash_matches"]))
    result = run(cand, base, installed(sha="0" * 64))
    check("installed_equals_baseline_is_not_candidate_proof",
          result["status"] == admission.CANDIDATE and
          result["post_install_hash_matches"] is False,
          "installed=baseline hash => post-install proof false")

    # 11. Case/spacing normalisation: the same digest in upper case is the same signer.
    upper = candidate()
    upper["signer_digests"] = [SIGNER_A.upper()]
    inst_upper = installed()
    inst_upper["signer_digests"] = ["  " + SIGNER_A + "  "]
    result = run(upper, baseline(), inst_upper)
    check("digest_normalisation_is_case_and_space_insensitive",
          result["status"] == admission.CANDIDATE and result["same_signature"] is True,
          result["status"])

    failed = [r for r in RESULTS if not r[1]]
    print("\n%d checks, %d passed, %d failed" % (len(RESULTS), len(RESULTS) - len(failed), len(failed)))
    if failed:
        for name, _ok, detail in failed:
            print("  FAILED: %s  %s" % (name, detail))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
