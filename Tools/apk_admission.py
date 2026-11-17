#!/usr/bin/env python3
"""Install-admission model for the mod-enabled APK, independent of any device.

Three artifacts are deliberately different things and must not be conflated:

  * ``baseline``  - the content reference (the unmodified debug build). It proves "everything
                    except assemblies.blob is unchanged" and is also the fixed recorded hash
                    used by the build gate. Its signing key is irrelevant to whether the
                    CURRENT device can take the candidate: the baseline may have been built
                    with a different local key.
  * ``candidate`` - the APK produced by this build, signed with this machine's key.
  * ``installed`` - the APK that is actually on the device right now, with its own signing
                    certificate. Android's ``adb install -r`` compares the candidate against
                    THIS one, never against a build artifact.

Everything here is pure data in / data out so it can be unit tested with synthetic evidence.
No APK is opened, no device is contacted.

Statuses (``status`` in the returned dict):

    INSTALL_CANDIDATE          installed signer == candidate signer; -r is admissible
    FIRST_INSTALL              the package is absent on the device
    UPDATE_INCOMPATIBLE_RISK   installed signer != candidate signer; -r would fail
    DOWNGRADE_RISK             installed versionCode > candidate versionCode
    PACKAGE_MISMATCH           package name / expected identity differs
    UNSUPPORTED_MULTI_SIGNER   more than one signing certificate digest was observed
    SIGNATURE_UNREADABLE       the installed certificate (or APK hash) could not be read
    NOT_VERIFIED               no installed evidence was supplied at all

``adb_install_allowed`` is True only for INSTALL_CANDIDATE and FIRST_INSTALL. Every unknown or
unsupported state is False: "not proven" is never "allowed".
"""

CANDIDATE = "INSTALL_CANDIDATE"
FIRST_INSTALL = "FIRST_INSTALL"
INCOMPATIBLE = "UPDATE_INCOMPATIBLE_RISK"
DOWNGRADE = "DOWNGRADE_RISK"
PACKAGE_MISMATCH = "PACKAGE_MISMATCH"
MULTI_SIGNER = "UNSUPPORTED_MULTI_SIGNER"
SIGNATURE_UNREADABLE = "SIGNATURE_UNREADABLE"
NOT_VERIFIED = "NOT_VERIFIED"

_ALLOWED = {CANDIDATE, FIRST_INSTALL}


def normalize_digests(values):
    """Lower-case, validate and de-duplicate certificate SHA-256 digests.

    ``None``/empty entries are dropped. Anything that is not 64 hex characters is kept in the
    returned list as an invalid marker so the caller can classify "unreadable" instead of
    silently comparing an empty list.
    """
    out = []
    for value in values or []:
        text = (value or "").strip().lower()
        if not text:
            continue
        out.append(text)
    # De-duplicate while preserving order: the same signer printed twice is one signer.
    seen = set()
    unique = []
    for item in out:
        if item in seen:
            continue
        seen.add(item)
        unique.append(item)
    return unique


def _valid_digest_list(digests):
    return all(len(d) == 64 and all(c in "0123456789abcdef" for c in d) for d in digests)


def _version_code(value):
    """Version codes are decimal text in the manifest; compare numerically when possible."""
    try:
        return int(str(value))
    except (TypeError, ValueError):
        return None


def evaluate(candidate, baseline, installed, expected_package,
             expected_version_code, expected_version_name=None):
    """Classifies install admission.

    Parameters are plain dicts (all fields optional unless stated):

    candidate: {signer_digests: [str], apk_sha256: str, package: str, version_code: str,
                version_name: str, signature_read_error: str}
    baseline:  {signer_digests: [str], apk_sha256: str}
    installed: None  -> no evidence supplied (NOT_VERIFIED)
               {present: False} -> explicitly absent (FIRST_INSTALL)
               {present: True, signer_digests: [str], apk_sha256: str,
                package: str, version_code: str, signature_read_error: str}
    """
    result = {
        "status": NOT_VERIFIED,
        "adb_install_allowed": False,
        "same_signature": None,
        "reason": "",
        "candidate_signer_sha256": None,
        "baseline_signer_sha256": None,
        "installed_signer_sha256": None,
        "baseline_matches_candidate": None,
        "post_install_hash_matches": None,
        "installed_apk_sha256": None,
        "candidate_apk_sha256": candidate.get("apk_sha256") or None,
    }

    cand_digests = normalize_digests(candidate.get("signer_digests"))
    base_digests = normalize_digests(baseline.get("signer_digests"))
    result["candidate_signer_sha256"] = cand_digests[0] if cand_digests else None
    result["baseline_signer_sha256"] = base_digests[0] if base_digests else None
    if base_digests and cand_digests:
        result["baseline_matches_candidate"] = base_digests == cand_digests

    # ---- candidate identity -------------------------------------------------------------
    cand_error = (candidate.get("signature_read_error") or "").strip()
    if cand_error or not cand_digests or not _valid_digest_list(cand_digests):
        result["status"] = SIGNATURE_UNREADABLE
        result["reason"] = ("the candidate signing certificate could not be read" +
                            ((" (" + cand_error + ")") if cand_error else ""))
        return result
    if len(cand_digests) > 1:
        result["status"] = MULTI_SIGNER
        result["reason"] = ("the candidate carries %d signing certificate digests; multi-signer "
                            "and v3 rotation lineage are not implemented, so admission is refused"
                            % len(cand_digests))
        return result

    if candidate.get("package") and candidate["package"] != expected_package:
        result["status"] = PACKAGE_MISMATCH
        result["reason"] = ("candidate package %r is not %r" %
                            (candidate["package"], expected_package))
        return result
    if expected_version_code is not None and candidate.get("version_code") is not None:
        if str(candidate["version_code"]) != str(expected_version_code):
            result["status"] = PACKAGE_MISMATCH
            result["reason"] = ("candidate versionCode %s is not %s" %
                                (candidate["version_code"], expected_version_code))
            return result
    if expected_version_name and candidate.get("version_name") and \
            candidate["version_name"] != expected_version_name:
        result["status"] = PACKAGE_MISMATCH
        result["reason"] = ("candidate versionName %r is not %r" %
                            (candidate["version_name"], expected_version_name))
        return result

    # ---- installed evidence -------------------------------------------------------------
    if installed is None:
        result["status"] = NOT_VERIFIED
        result["reason"] = ("no installed-APK evidence was supplied: the baseline build is not "
                            "the installed APK, so upgrade admission cannot be decided")
        return result

    if not installed.get("present", True):
        result["status"] = FIRST_INSTALL
        result["adb_install_allowed"] = True
        result["reason"] = "the package is not installed on the device; a first install needs no signature comparison"
        return result

    inst_digests = normalize_digests(installed.get("signer_digests"))
    result["installed_signer_sha256"] = inst_digests[0] if inst_digests else None
    inst_error = (installed.get("signature_read_error") or "").strip()
    if inst_error or not inst_digests or not _valid_digest_list(inst_digests):
        result["status"] = SIGNATURE_UNREADABLE
        result["reason"] = ("the installed APK signing certificate could not be read" +
                            ((" (" + inst_error + ")") if inst_error else ""))
        return result
    if len(inst_digests) > 1:
        result["status"] = MULTI_SIGNER
        result["reason"] = ("the installed APK carries %d signing certificate digests; "
                            "multi-signer and rotation lineage are not implemented" %
                            len(inst_digests))
        return result

    installed_package = installed.get("package")
    if installed_package and installed_package != expected_package:
        result["status"] = PACKAGE_MISMATCH
        result["reason"] = ("installed package %r is not %r" %
                            (installed_package, expected_package))
        return result

    installed_code = _version_code(installed.get("version_code"))
    candidate_code = _version_code(candidate.get("version_code"))
    result["installed_apk_sha256"] = installed.get("apk_sha256") or None
    if installed.get("apk_sha256") and candidate.get("apk_sha256"):
        result["post_install_hash_matches"] = \
            installed["apk_sha256"].lower() == candidate["apk_sha256"].lower()

    if installed_code is not None and candidate_code is not None and installed_code > candidate_code:
        result["status"] = DOWNGRADE
        result["reason"] = ("installed versionCode %s is newer than candidate %s; adb install -r "
                            "would be refused without -d" % (installed_code, candidate_code))
        return result

    same = inst_digests == cand_digests
    result["same_signature"] = same
    if same:
        result["status"] = CANDIDATE
        result["adb_install_allowed"] = True
        result["reason"] = ("candidate signer equals the INSTALLED signer; a same-signature "
                            "in-place replace is admissible")
        if result["post_install_hash_matches"] is True:
            result["reason"] += " (installed bytes already equal the candidate)"
        return result

    result["status"] = INCOMPATIBLE
    result["reason"] = (
        "candidate signer %s differs from the INSTALLED signer %s; adb install -r would fail "
        "with INSTALL_FAILED_UPDATE_INCOMPATIBLE. The baseline signer is irrelevant to this "
        "decision." % (cand_digests[0], inst_digests[0]))
    return result


def legacy_same_signer_decision(candidate, baseline):
    """The Phase 1.1.7 predicate, kept only to demonstrate why it was insufficient.

    It compares the candidate with the CONTENT BASELINE and never looks at the device, so it
    can both refuse a valid upgrade and allow an impossible one. New code must call
    :func:`evaluate` instead.
    """
    cand = normalize_digests(candidate.get("signer_digests"))
    base = normalize_digests(baseline.get("signer_digests"))
    return bool(cand) and bool(base) and cand[0] == base[0]
