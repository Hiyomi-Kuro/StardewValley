#!/usr/bin/env python3
"""Static checks for the mod-enabled APK build path (Phase 1.1.7).

CI-runnable: no game assemblies, no APK, no device, no SDK. It reads the tracked sources and
asserts the invariants that would be expensive to discover on a device:

  1. `assembleDebug` is still a *baseline* build: three positional arguments, no injection;
  2. the mod-enabled entry point is explicit (`assembleModdedDebug`), has its own output path
     and its own work directory, and is not wired into `assemble`;
  3. the mod-enabled build cannot generate a signing key (that would silently produce an
     APK that cannot replace the installed package);
  4. Bootstrap and the Loader resolve the *same* external Mods root, and neither falls back to
     the app-private `files/Mods`;
  5. the injector verifies the baked Mods path and rejects the private one by default;
  6. the XALZ descriptor index is preserved on rebuild (synthetic container, real store.py),
     which is the one invariant that has already caused a device-side failure;
  7. nothing in the build path writes back into `app/src/main/apkroot`.

Usage (repo root):
    python Tools/tests/test_modded_build.py
Exit code 0 = every check passed.
"""

import os
import re
import struct
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "Tools", "AssemblyStore"))
import store as store_mod  # noqa: E402

RESULTS = []

PACKAGE = "com.chucklefish.stardewvalley"
EXTERNAL_MODS_ROOT = "/sdcard/Android/data/%s/files/Mods" % PACKAGE
PRIVATE_MODS_ROOT = "/data/data/%s/files/Mods" % PACKAGE


def check(name, ok, detail="", skipped=False):
    status = "SKIP" if skipped else ("PASS" if ok else "FAIL")
    RESULTS.append((name, status, detail))
    print("%s %-52s %s" % (status, name, detail))
    return ok


def read(rel):
    path = os.path.join(ROOT, rel)
    with open(path, "r", encoding="utf-8") as f:
        return f.read()


def strip_comments(text):
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    text = re.sub(r"//[^\n]*", "", text)
    return text


def cs_const(text, name):
    match = re.search(r'const\s+string\s+%s\s*=\s*"([^"]+)"' % re.escape(name), text)
    return match.group(1) if match else ""


def block_after(text, opener, length=None):
    """Returns the text from `opener` up to its matching closing brace."""
    start = text.find(opener)
    if start < 0:
        return ""
    depth = 0
    for i in range(start, len(text)):
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                return text[start:i + 1]
    return text[start:]


def gradle_checks():
    gradle = read("app/build.gradle.kts")

    baseline = block_after(gradle, 'tasks.register<JavaExec>("assembleDebug")')
    check("gradle_baseline_task_present", bool(baseline), "assembleDebug")
    check("gradle_baseline_has_no_injection", "--bootstrap-blob" not in baseline,
          "assembleDebug passes no injection flag")
    check("gradle_baseline_three_positional_args", baseline.count("absolutePath") == 3,
          "%d absolutePath arguments" % baseline.count("absolutePath"))
    check("gradle_baseline_output_path", "outputs/apk/debug/app-debug.apk" in gradle,
          "app-debug.apk")

    # Phase 2.0: two mod builds share one helper, so the assertions below target the helper plus
    # the two explicit task registrations and their distinct output/work directories.
    mod_apk_helper = block_after(gradle, 'fun modApkTask(')
    prepare_helper = block_after(gradle, 'fun prepareAssembliesTask(')
    check("gradle_modded_task_present", "modApkTask(" in gradle and "assembleModdedDebug" in gradle,
          "assembleModdedDebug")
    check("gradle_modded_not_dependent_on_baseline",
          'dependsOn("assembleDebug")' not in mod_apk_helper, "no assembleDebug dependency")
    check("gradle_modded_depends_on_blob_prep",
          "prepareModdedAssemblies" in gradle and "dependsOn(tasks.named(\"classes\"), prepare)" in mod_apk_helper,
          "prepareModdedAssemblies is a task dependency")
    check("gradle_modded_output_separate",
          "outputs/apk/modded/app-modded-debug.apk" in gradle, "app-modded-debug.apk")
    check("gradle_modded_workdir_separate", ".tmp/build-modded" in gradle, ".tmp/build-modded")
    check("gradle_modded_passes_bootstrap_blob", "--bootstrap-blob" in mod_apk_helper,
          "--bootstrap-blob")
    check("gradle_modded_requires_existing_keystore",
          "--require-existing-keystore" in mod_apk_helper, "--require-existing-keystore")
    check("gradle_modded_not_in_assemble",
          'dependsOn("assembleModdedDebug")' not in gradle,
          "assemble stays baseline-only")

    # Phase 2.0 rollback artifact: loader only, no event hooks, isolated directories.
    check("gradle_loader_only_task_present", "assembleLoaderOnlyDebug" in gradle,
          "assembleLoaderOnlyDebug")
    check("gradle_loader_only_output_separate",
          "outputs/apk/loader-only/app-loader-only-debug.apk" in gradle, "app-loader-only-debug.apk")
    check("gradle_loader_only_workdir_separate", ".tmp/build-loader-only" in gradle,
          ".tmp/build-loader-only")
    check("gradle_event_switch_wired", '--event-hooks' in prepare_helper,
          "--event-hooks passed to build_modded_apk.py")
    check("gradle_event_switch_declared_as_input", 'inputs.property("eventHooks"' in prepare_helper,
          "the switch is a declared Gradle input")
    check("gradle_loader_only_disables_events",
          'prepareLoaderOnlyAssemblies' in gradle and "eventHooks = false" in gradle,
          "loader-only build passes --event-hooks disabled")


def main_kt_checks():
    main_kt = read("app/src/main/kotlin/com/kaori/svpatch/Main.kt")

    check("main_kt_accepts_bootstrap_flag", '"--bootstrap-blob"' in main_kt, "--bootstrap-blob")
    check("main_kt_accepts_keystore_flag", '"--keystore"' in main_kt, "--keystore")
    check("main_kt_accepts_require_existing", '"--require-existing-keystore"' in main_kt,
          "--require-existing-keystore")
    check("main_kt_rejects_unknown_flags", "未知参数" in main_kt, "unknown flag rejected")

    guard = 'if (bootstrapBlob != null)'
    check("main_kt_injection_guarded", guard in main_kt, guard)
    if guard in main_kt:
        after = main_kt[main_kt.find(guard):]
        # The call must be inside the guarded branch, before the baseline else.
        else_at = after.find("} else {")
        call_at = after.find("injectAssembliesBlob(decoded")
        check("main_kt_injection_only_when_flag_given",
              0 <= call_at and (else_at < 0 or call_at < else_at),
              "call index %d, else index %d" % (call_at, else_at))
        check("main_kt_baseline_branch_logged", "baseline" in after[:max(else_at, 0) + 200],
              "baseline branch logs no injection")

    body = strip_comments(block_after(main_kt, "private fun injectAssembliesBlob"))
    check("main_kt_staging_target_only", 'resolve("unknown")' in body, "decoded/unknown")
    check("main_kt_never_writes_apkroot", "apkroot" not in body, "no apkroot write")
    check("main_kt_checks_xaba_magic", "XABA" in body, "rejects non-XABA blob")

    signing = read("app/src/main/kotlin/com/kaori/svpatch/Signing.kt")
    check("signing_has_require_existing_flag", "requireExistingKeystore" in signing,
          "requireExistingKeystore")
    check("signing_refuses_to_generate_key", "check(!requireExistingKeystore)" in signing,
          "fails instead of generating a new key")


def mods_root_checks():
    loader = read("Mod/ModFramework.Runtime/Loader.cs")
    injector = read("Tools/ModInjector/Program.cs")
    builder = read("Tools/build_modded_apk.py")
    verifier = read("Tools/verify_modded_apk.py")

    loader_root = cs_const(loader, "ExternalModsRoot")
    injector_root = cs_const(injector, "DefaultModsDir")

    match = re.search(r'EXTERNAL_MODS_DIR\s*=\s*"([^"]*)"\s*%\s*PACKAGE', builder)
    builder_root = (match.group(1) % PACKAGE) if match else ""
    verifier_pkg = re.search(r'^PACKAGE\s*=\s*"([^"]+)"', verifier, re.M)

    check("loader_external_root_constant", loader_root == EXTERNAL_MODS_ROOT, loader_root)
    check("injector_default_mods_dir", injector_root == EXTERNAL_MODS_ROOT, injector_root)
    check("builder_external_mods_dir", builder_root == EXTERNAL_MODS_ROOT, builder_root)
    check("verifier_package_id", bool(verifier_pkg) and verifier_pkg.group(1) == PACKAGE,
          verifier_pkg.group(1) if verifier_pkg else "")
    check("mods_root_agrees_everywhere",
          loader_root == injector_root == builder_root == EXTERNAL_MODS_ROOT,
          "%s / %s / %s" % (loader_root, injector_root, builder_root))

    check("loader_has_no_private_fallback", PRIVATE_MODS_ROOT not in loader,
          "no /data/data fallback")
    check("loader_resolver_seam", "internal static string ResolveModsRoot" in loader,
          "ResolveModsRoot")
    check("loader_fallback_used_on_all_failure_paths",
          loader.count("return ExternalModsRoot;") >= 2,
          "%d returns of the fallback" % loader.count("return ExternalModsRoot;"))

    check("injector_rejects_private_mods_dir", "--allow-private-mods-dir" in injector,
          "explicit opt-in required")
    check("injector_verify_reports_mods_dir", "mods-dir-ok" in injector, "mods-dir-ok")
    check("injector_default_api_loadfrom", '"loadfrom"' in injector, "loadfrom default")


def container_checks():
    builder = read("Tools/build_modded_apk.py")
    store_py = read("Tools/AssemblyStore/store.py")

    check("builder_uses_store_py", "STORE_PY" in builder and '"replace"' in builder,
          "store.py replace")
    check("builder_validates_descriptor", "target_descriptor_preserved" in builder,
          "descriptor preserved")
    check("builder_checks_size_budget", "expected_size" in builder, "expected_size")
    check("builder_checks_other_entries", "other_entries_unchanged" in builder,
          "57 untouched entries")
    check("builder_reextracts_and_verifies", "verify_reextracted_from_blob" in builder,
          "verify after replace")
    check("store_preserves_descriptor_index_on_rebuild",
          'desc = e.get("desc")' in store_py and 'struct.pack("<II", desc, len(raw))' in store_py,
          "rebuild writes the original descriptor")
    check("store_keeps_descriptor_at_parse", "self.entries.append" in store_py,
          "descriptor read from XALZ header")

    # Nothing in the build path may write back into the source tree.
    writes = re.findall(r'open\(([^,\n]+),\s*"wb"\)', builder + store_py)
    offenders = [w for w in writes if "apkroot" in w]
    check("no_write_back_into_apkroot", not offenders, offenders[:3])
    check("builder_out_blob_required",
          re.search(r'add_argument\("--out-blob",\s*required=True\)', builder) is not None,
          "--out-blob has no default")


def synthetic_descriptor_roundtrip():
    """Real store.py, synthetic container: the descriptor index must survive a rebuild.

    This is the invariant that produced "expected at most 13824" on device when the index was
    rewritten to 0, so it is checked here without any game content.
    """
    raw = b"MZ" + bytes(range(256)) * 2
    descriptor = 7
    chunk = b"XALZ" + struct.pack("<II", descriptor, len(raw)) + store_mod.lz4_compress(raw)
    header = struct.pack("<IIIII", store_mod.MAGIC, 1, 1, 65, 0)
    gap = b"\x00" * 40
    cursor = len(header) + 24 + len(gap)
    table = struct.pack("<IIIIII", cursor, len(chunk), 0, 0, 0, 0)
    blob = header + table + gap + chunk

    tmp = tempfile.mkdtemp(prefix="modded-desc-")
    path = os.path.join(tmp, "synthetic.blob")
    with open(path, "wb") as f:
        f.write(blob)

    try:
        original = store_mod.Store(path)
        got_raw, got_desc = original.decode(original.entries[0])
        check("synthetic_parse_descriptor", got_desc == descriptor and got_raw == raw,
              "descriptor=%s bytes=%d" % (got_desc, len(got_raw)))

        replacement = b"MZ" + b"\xcc" * (len(raw) + 512)
        rebuilt = original.rebuild({0: replacement})
        path2 = os.path.join(tmp, "rebuilt.blob")
        with open(path2, "wb") as f:
            f.write(rebuilt)
        after = store_mod.Store(path2)
        new_raw, new_desc = after.decode(after.entries[0])
        check("synthetic_rebuild_keeps_descriptor", new_desc == descriptor,
              "descriptor %s -> %s" % (got_desc, new_desc))
        check("synthetic_rebuild_roundtrip", new_raw == replacement,
              "%d bytes" % len(new_raw))
        check("synthetic_rebuild_preserves_gap", after.gap == gap, "%d bytes" % len(after.gap))
        check("synthetic_rebuild_preserves_counts",
              (after.entry_count, after.total_count) == (1, 65),
              "%d/%d" % (after.entry_count, after.total_count))
    finally:
        for name in ("synthetic.blob", "rebuilt.blob"):
            try:
                os.remove(os.path.join(tmp, name))
            except OSError:
                pass
        try:
            os.rmdir(tmp)
        except OSError:
            pass


def main():
    print("== build entry points ==")
    gradle_checks()
    print("\n== build entry (Kotlin) ==")
    main_kt_checks()
    print("\n== one Mods root ==")
    mods_root_checks()
    print("\n== container invariants ==")
    container_checks()
    print("\n== descriptor index (synthetic container) ==")
    synthetic_descriptor_roundtrip()

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
