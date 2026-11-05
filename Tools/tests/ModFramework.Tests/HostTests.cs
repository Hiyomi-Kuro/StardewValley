using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using ModFramework.Runtime;

namespace ModFramework.Tests;

/// <summary>
/// End-to-end host tests: real mod packages on disk, the real Loader, the real
/// Assembly.LoadFrom path and real IModEntry implementations.
///
/// This is host evidence, not Android/Mono evidence: the trimmed BCL can only be exercised
/// on a device.
/// </summary>
internal static class HostTests
{
    internal static void Run(TestRunner t, string fixtureDir)
    {
        var fixtureDll = Path.Combine(fixtureDir, "FixtureMod.dll");
        var dependencyDll = Path.Combine(fixtureDir, "FixtureMod.Dependency.dll");
        if (!File.Exists(fixtureDll) || !File.Exists(dependencyDll))
        {
            t.Check("host_fixtures_present", false,
                "expected FixtureMod.dll and FixtureMod.Dependency.dll in " + fixtureDir);
            return;
        }

        HappyPathAndIsolation(t, fixtureDll, dependencyDll);
        LoadOrder(t, fixtureDll);
        IdempotentSecondRun(t, fixtureDll, dependencyDll);
        Reentrancy(t, fixtureDll);
        RootHandling(t);
        VersionGateFailClosed(t, fixtureDir);
        AssemblyConflictPreflight(t, fixtureDir);
        LoggerDegraded(t, fixtureDir);
        // Runs last: it intentionally loads MonoRuntimeProbe into the AppDomain.
        LegacyProbeNotExecuted(t, fixtureDir);
    }

    // ---------------------------------------------------------------- scenarios

    private static void HappyPathAndIsolation(TestRunner t, string fixtureDll, string dependencyDll)
    {
        var root = Temp.NewDirectory("happy");
        AddPackage(root, "FixtureMod.Good", "FixtureMod.Good", "FixtureMod.GoodEntry",
            new[] { fixtureDll, dependencyDll }, "FixtureMod.dll",
            "\"privateAssemblies\": [ \"FixtureMod.Dependency.dll\" ]");
        AddPackage(root, "FixtureMod.Throwing", "FixtureMod.Throwing", "FixtureMod.ThrowingEntry", new[] { fixtureDll });
        AddPackage(root, "FixtureMod.NotAnEntry", "FixtureMod.NotAnEntry", "FixtureMod.NotAnEntry", new[] { fixtureDll });
        AddPackage(root, "FixtureMod.Abstract", "FixtureMod.Abstract", "FixtureMod.AbstractEntry", new[] { fixtureDll });
        AddPackage(root, "FixtureMod.NoCtor", "FixtureMod.NoCtor", "FixtureMod.NoDefaultCtorEntry", new[] { fixtureDll });
        AddPackage(root, "FixtureMod.Internal", "FixtureMod.Internal", "FixtureMod.InternalEntry", new[] { fixtureDll });
        AddPackage(root, "FixtureMod.MissingDll", "FixtureMod.MissingDll", "FixtureMod.GoodEntry", new string[0],
            "Missing.dll");
        AddPackage(root, "FixtureMod.NameMismatch", "FixtureMod.NameMismatch", "FixtureMod.GoodEntry",
            new[] { fixtureDll }, "Wrong.dll");

        var missingDep = Path.Combine(root, "FixtureMod.MissingDep");
        WriteManifest(missingDep, Manifest("FixtureMod.MissingDep", "FixtureMod.DependentEntry",
            "\"dependencies\": [ { \"id\": \"FixtureMod.Absent\", \"version\": \"*\" } ]"));
        CopyFile(fixtureDll, Path.Combine(missingDep, "FixtureMod.dll"));

        var dependentOfThrowing = Path.Combine(root, "FixtureMod.DependentOfThrowing");
        WriteManifest(dependentOfThrowing, Manifest("FixtureMod.DependentOfThrowing", "FixtureMod.DependentEntry",
            "\"dependencies\": [ { \"id\": \"FixtureMod.Throwing\", \"version\": \"*\" } ]"));
        CopyFile(fixtureDll, Path.Combine(dependentOfThrowing, "FixtureMod.dll"));

        var badManifest = Path.Combine(root, "FixtureMod.BadManifest");
        Directory.CreateDirectory(badManifest);
        File.WriteAllText(Path.Combine(badManifest, "manifest.json"), "{ this is not json", new UTF8Encoding(false));

        Directory.CreateDirectory(Path.Combine(root, "NoManifestHere"));

        var returned = Loader.Run(root);
        var report = Temp.ReadText(Path.Combine(root, "loader-report.txt"));

        t.Check("host_run_returns_null", returned == null, returned ?? "(null)");
        t.Check("host_report_written", report.Length > 0, Path.Combine(root, "loader-report.txt"));
        t.Contains("host_report_result_ok", report, "result=ok");
        t.Contains("host_report_counts_discovery", report, "mods-discovered=11");

        t.Contains("host_good_initialized", report, "id=FixtureMod.Good state=Initialized");
        t.Contains("host_good_log_line", report, "initialized=FixtureMod.Good");

        t.Contains("host_throwing_initialize", report, "id=FixtureMod.Throwing state=Failed code=" + "MF0309");
        t.Contains("host_missing_dependency", report, "id=FixtureMod.MissingDep state=Failed code=MF0401");
        t.Contains("host_dependent_blocked", report, "id=FixtureMod.DependentOfThrowing state=Failed code=MF0404");
        t.Contains("host_not_an_entry", report, "id=FixtureMod.NotAnEntry state=Failed code=MF0305");
        t.Contains("host_abstract_entry", report, "id=FixtureMod.Abstract state=Failed code=MF0303");
        t.Contains("host_no_default_ctor", report, "id=FixtureMod.NoCtor state=Failed code=MF0307");
        t.Contains("host_internal_entry", report, "id=FixtureMod.Internal state=Failed code=MF0302");
        t.Contains("host_missing_dll", report, "id=FixtureMod.MissingDll state=Failed code=MF0201");
        t.Contains("host_name_mismatch", report, "id=FixtureMod.NameMismatch state=Failed code=MF0203");
        t.Contains("host_bad_manifest_isolated", report, "id=FixtureMod.BadManifest state=Failed code=MF0104");

        var goodLog = Temp.ReadText(Path.Combine(root, "logs", "FixtureMod.Good.log"));
        t.Contains("host_mod_log_has_identity", goodLog, "FixtureMod.Good");
        t.Contains("host_mod_log_info_level", goodLog, "INFO ");
        t.Contains("host_mod_log_has_message", goodLog, "hello from FixtureMod.Good 1.0.0");
        t.Contains("host_mod_log_warn_level", goodLog, "WARN ");
        t.Contains("host_private_dependency_resolved", goodLog, "fixture-dependency-alive");

        var marker = Temp.ReadText(Path.Combine(root, "FixtureMod.Good", "entry-ran.txt"));
        t.Contains("host_entry_really_ran", marker, "FixtureMod.Good|");

        var badLog = Path.Combine(root, "logs", "FixtureMod.Throwing.log");
        t.Check("host_throwing_mod_logged", File.Exists(badLog), badLog);

        // An unrelated mod must survive a neighbour's failure - the whole point of isolation.
        t.Contains("host_unrelated_mod_unaffected", report, "id=FixtureMod.Good state=Initialized");
    }

    private static void LoadOrder(TestRunner t, string fixtureDll)
    {
        var root = Temp.NewDirectory("order");
        // Declared out of order on purpose: the loader must sort by dependency, then by id.
        AddPackage(root, "Chain.C", "Chain.C", "FixtureMod.DependentEntry", new[] { fixtureDll },
            "FixtureMod.dll", "\"dependencies\": [ { \"id\": \"Chain.B\", \"version\": \"*\" } ]");
        AddPackage(root, "Chain.A", "Chain.A", "FixtureMod.DependentEntry", new[] { fixtureDll });
        AddPackage(root, "Chain.B", "Chain.B", "FixtureMod.DependentEntry", new[] { fixtureDll },
            "FixtureMod.dll", "\"dependencies\": [ { \"id\": \"Chain.A\", \"version\": \"*\" } ]");

        var returned = Loader.Run(root);
        var report = Temp.ReadText(Path.Combine(root, "loader-report.txt"));
        var order = InitializedOrder(report);
        t.Check("host_load_order", returned == null && order == "Chain.A,Chain.B,Chain.C", order);
    }

    private static void IdempotentSecondRun(TestRunner t, string fixtureDll, string dependencyDll)
    {
        var root = Temp.NewDirectory("idempotent");
        AddPackage(root, "FixtureMod.Good", "FixtureMod.Good", "FixtureMod.GoodEntry",
            new[] { fixtureDll, dependencyDll }, "FixtureMod.dll",
            "\"privateAssemblies\": [ \"FixtureMod.Dependency.dll\" ]");

        var first = Loader.Run(root);
        var logPath = Path.Combine(root, "logs", "FixtureMod.Good.log");
        var firstCount = CountLines(Temp.ReadText(logPath));

        var second = Loader.Run(root);
        var secondCount = CountLines(Temp.ReadText(logPath));
        var report = Temp.ReadText(Path.Combine(root, "loader-report.txt"));

        t.Check("host_second_run_returns_null", first == null && second == null,
            (first ?? "(null)") + " / " + (second ?? "(null)"));
        t.Check("host_second_run_does_not_reinitialize", firstCount == secondCount && firstCount > 0,
            "log lines " + firstCount + " -> " + secondCount);
        t.Contains("host_second_run_reports_already_initialized", report, "result=already-initialized");
    }

    private static void Reentrancy(TestRunner t, string fixtureDll)
    {
        var root = Temp.NewDirectory("reentrant");
        AddPackage(root, "FixtureMod.Reentrant", "FixtureMod.Reentrant", "FixtureMod.ReentrantEntry", new[] { fixtureDll });

        var returned = Loader.Run(root);
        var marker = Temp.ReadText(Path.Combine(root, "FixtureMod.Reentrant", "reentrant.txt"));
        t.Check("host_reentrancy_guard", returned == null && marker.IndexOf("MF0901", StringComparison.Ordinal) >= 0,
            marker.Length > 0 ? marker : "(no marker)");
    }

    private static void RootHandling(TestRunner t)
    {
        var empty = Temp.NewDirectory("empty");
        var emptyResult = Loader.Run(empty);
        var emptyReport = Temp.ReadText(Path.Combine(empty, "loader-report.txt"));
        t.Check("host_empty_root_ok", emptyResult == null, emptyResult ?? "(null)");
        t.Contains("host_empty_root_counts", emptyReport, "mods-discovered=0");

        var missing = Path.Combine(Path.GetTempPath(), "modfw-missing-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var missingResult = Loader.Run(missing);
        t.Check("host_missing_root_is_reported",
            missingResult != null && missingResult.IndexOf("MF0902", StringComparison.Ordinal) >= 0,
            missingResult ?? "(null)");
    }

    /// <summary>
    /// Phase 1.1 version gates: everything except an explicit <c>"*"</c> is fail-closed.
    ///
    /// Order matters: every rejection case runs before the first case that is allowed to load,
    /// so "VersionGateLib is not in the AppDomain" is a meaningful assertion for the rejections.
    /// </summary>
    private static void VersionGateFailClosed(TestRunner t, string fixtureDir)
    {
        var gateDll = Path.Combine(fixtureDir, "VersionGateLib.dll");
        if (!File.Exists(gateDll))
        {
            t.Check("version_gate_fixture_present", false, "expected VersionGateLib.dll in " + fixtureDir);
            return;
        }

        var previousGame = Loader.GameVersionOverride;
        var previousFramework = Loader.FrameworkVersionOverride;
        try
        {
            // ---- 1. explicit game range + unknown game version -> rejected (MF0503) ------
            Loader.GameVersionOverride = null;
            var root = Temp.NewDirectory("gate-unknown");
            GatePackage(root, "Gate.Unknown", "\"gameVersion\": \">=1.0.0\"", gateDll);
            var returned = Loader.Run(root);
            var report = Temp.ReadText(Path.Combine(root, "loader-report.txt"));
            t.Check("gate_game_version_unknown_rejected",
                returned == null && report.Contains("id=Gate.Unknown state=Failed code=MF0503"),
                Line(report, "id=Gate.Unknown"));
            t.Contains("gate_game_version_unknown_reported", report, "game-version-state=unknown");
            t.Contains("gate_game_version_source_none", report, "game-version-source=none");
            t.Check("gate_game_version_unknown_no_marker", !File.Exists(Path.Combine(root, "Gate.Unknown", "entry-ran.txt")),
                "the entry must not run");
            t.Check("gate_game_version_unknown_not_loaded", !AnyLoaded("VersionGateLib"), "VersionGateLib is not in the AppDomain");
            t.Check("gate_game_version_unknown_history_stops",
                !ModBlock(report, "Gate.Unknown").Contains("AssemblyLoaded"),
                ModBlock(report, "Gate.Unknown"));

            // ---- 2. detected game version is not SemVer -> rejected (MF0504) -------------
            Loader.GameVersionOverride = "not-semver";
            var root2 = Temp.NewDirectory("gate-invalid");
            GatePackage(root2, "Gate.Invalid", "\"gameVersion\": \">=1.0.0\"", gateDll);
            var returned2 = Loader.Run(root2);
            var report2 = Temp.ReadText(Path.Combine(root2, "loader-report.txt"));
            t.Check("gate_game_version_invalid_rejected",
                returned2 == null && report2.Contains("id=Gate.Invalid state=Failed code=MF0504"),
                Line(report2, "id=Gate.Invalid"));
            t.Contains("gate_game_version_invalid_reported", report2, "game-version-state=invalid");
            t.Contains("gate_game_version_invalid_source", report2, "game-version-source=override");
            t.Check("gate_game_version_invalid_not_loaded", !AnyLoaded("VersionGateLib"), "VersionGateLib is not in the AppDomain");

            // ---- 3. framework version unparseable + explicit range -> rejected (MF0505) --
            Loader.GameVersionOverride = "1.0.0";
            Loader.FrameworkVersionOverride = "not-semver";
            var root3 = Temp.NewDirectory("gate-framework-invalid");
            GatePackage(root3, "Gate.FrameworkInvalid", "\"frameworkVersion\": \">=1.0.0\"", gateDll);
            var returned3 = Loader.Run(root3);
            var report3 = Temp.ReadText(Path.Combine(root3, "loader-report.txt"));
            t.Check("gate_framework_version_invalid_rejected",
                returned3 == null && report3.Contains("id=Gate.FrameworkInvalid state=Failed code=MF0505"),
                Line(report3, "id=Gate.FrameworkInvalid"));
            t.Check("gate_framework_version_invalid_not_loaded", !AnyLoaded("VersionGateLib"), "VersionGateLib is not in the AppDomain");
            Loader.FrameworkVersionOverride = previousFramework;

            // ---- 4. known game version outside the range -> MF0501 (no regression) --------
            var root4 = Temp.NewDirectory("gate-mismatch");
            GatePackage(root4, "Gate.Needs99", "\"gameVersion\": \">=99.0.0\"", gateDll);
            var returned4 = Loader.Run(root4);
            var report4 = Temp.ReadText(Path.Combine(root4, "loader-report.txt"));
            t.Check("gate_game_version_mismatch_rejected",
                returned4 == null && report4.Contains("id=Gate.Needs99 state=Failed code=MF0501"),
                Line(report4, "id=Gate.Needs99"));

            // ---- 5. known framework version outside the range -> MF0502 -------------------
            var root5 = Temp.NewDirectory("gate-framework-mismatch");
            GatePackage(root5, "Gate.Framework99", "\"frameworkVersion\": \">=99.0.0\"", gateDll);
            var returned5 = Loader.Run(root5);
            var report5 = Temp.ReadText(Path.Combine(root5, "loader-report.txt"));
            t.Check("gate_framework_version_mismatch_rejected",
                returned5 == null && report5.Contains("id=Gate.Framework99 state=Failed code=MF0502"),
                Line(report5, "id=Gate.Framework99"));

            // ---- 6. wildcard while the game version is unknown -> allowed -----------------
            Loader.GameVersionOverride = null;
            var root6 = Temp.NewDirectory("gate-wildcard");
            GatePackage(root6, "Gate.Wildcard", "\"gameVersion\": \"*\"", gateDll);
            var returned6 = Loader.Run(root6);
            var report6 = Temp.ReadText(Path.Combine(root6, "loader-report.txt"));
            t.Check("gate_game_version_wildcard_allowed",
                returned6 == null && report6.Contains("id=Gate.Wildcard state=Initialized"),
                Line(report6, "id=Gate.Wildcard"));
            t.Check("gate_game_version_wildcard_ran",
                File.Exists(Path.Combine(root6, "Gate.Wildcard", "entry-ran.txt")), "marker written");

            // ---- 7. framework wildcard does not gate anything -----------------------------
            Loader.FrameworkVersionOverride = "not-semver";
            var root7 = Temp.NewDirectory("gate-framework-wildcard");
            GatePackage(root7, "Gate.FrameworkWildcard", "\"frameworkVersion\": \"*\"", gateDll);
            var returned7 = Loader.Run(root7);
            var report7 = Temp.ReadText(Path.Combine(root7, "loader-report.txt"));
            t.Check("gate_framework_version_wildcard_allowed",
                returned7 == null && report7.Contains("id=Gate.FrameworkWildcard state=Initialized"),
                Line(report7, "id=Gate.FrameworkWildcard"));
            Loader.FrameworkVersionOverride = previousFramework;

            // ---- 8. known game version inside the range -> allowed ------------------------
            Loader.GameVersionOverride = "1.0.0";
            var root8 = Temp.NewDirectory("gate-accepted");
            GatePackage(root8, "Gate.Accepted", "\"gameVersion\": \">=1.0.0\"", gateDll);
            var returned8 = Loader.Run(root8);
            var report8 = Temp.ReadText(Path.Combine(root8, "loader-report.txt"));
            t.Check("gate_game_version_satisfied_allowed",
                returned8 == null && report8.Contains("id=Gate.Accepted state=Initialized"),
                Line(report8, "id=Gate.Accepted"));
            t.Check("gate_game_version_satisfied_ran",
                File.Exists(Path.Combine(root8, "Gate.Accepted", "entry-ran.txt")), "marker written");
        }
        finally
        {
            Loader.GameVersionOverride = previousGame;
            Loader.FrameworkVersionOverride = previousFramework;
        }
    }

    // -------------------------------------------------- assembly identity preflight

    /// <summary>
    /// The Phase 1.1 collision matrix. Every case asserts three separate things: the stable
    /// error code, that no marker was produced (no code ran), and that the conflicting file was
    /// never loaded into the AppDomain at all.
    /// </summary>
    private static void AssemblyConflictPreflight(TestRunner t, string fixtureDir)
    {
        foreach (var name in new[]
                 {
                     "DiffVersionLib.v1.dll", "DiffVersionLib.v2.dll",
                     "DiffContentLib.x.dll", "DiffContentLib.y.dll",
                     "SharedLib.dll", "EntryCollisionLib.dll", "EntryCollisionLib.v2.dll",
                     "HostConflict.ModFramework.Runtime.dll",
                 })
        {
            if (!File.Exists(Path.Combine(fixtureDir, name)))
            {
                t.Check("conflict_fixtures_present", false, "missing " + name + " in " + fixtureDir);
                return;
            }
        }

        var fixtureDll = Path.Combine(fixtureDir, "FixtureMod.dll");

        // ---- 1. same simple name, different version -> both rejected ---------------------
        var root1 = Temp.NewDirectory("conflict-version");
        PrivateMod(root1, "Conflict.VersionA", "DiffVersionLib.v1.dll", fixtureDll, fixtureDir, "");
        PrivateMod(root1, "Conflict.VersionB", "DiffVersionLib.v2.dll", fixtureDll, fixtureDir, "");
        var returned1 = Loader.Run(root1);
        var report1 = Temp.ReadText(Path.Combine(root1, "loader-report.txt"));
        t.Check("conflict_version_both_rejected",
            returned1 == null &&
            report1.Contains("id=Conflict.VersionA state=Failed code=MF0206") &&
            report1.Contains("id=Conflict.VersionB state=Failed code=MF0206"),
            Line(report1, "id=Conflict.VersionA") + " / " + Line(report1, "id=Conflict.VersionB"));
        t.Contains("conflict_version_group_reported", report1, "preflight-conflict=DiffVersionLib kind=mod");
        t.Check("conflict_version_neither_loaded", !AnyLoaded("DiffVersionLib"), "DiffVersionLib is not in the AppDomain");
        t.Check("conflict_version_no_markers",
            !File.Exists(Path.Combine(root1, "Conflict.VersionA", "entry-ran.txt")) &&
            !File.Exists(Path.Combine(root1, "Conflict.VersionB", "entry-ran.txt")),
            "neither entry ran");
        t.Check("conflict_preflight_runs_before_first_load", PreflightBeforeLoad(report1),
            "preflight-stage=complete precedes assembly-load=");

        // ---- 2. same identity, different content -> both rejected ------------------------
        var root2 = Temp.NewDirectory("conflict-content");
        PrivateMod(root2, "Conflict.ContentX", "DiffContentLib.x.dll", fixtureDll, fixtureDir, "");
        PrivateMod(root2, "Conflict.ContentY", "DiffContentLib.y.dll", fixtureDll, fixtureDir, "");
        var returned2 = Loader.Run(root2);
        var report2 = Temp.ReadText(Path.Combine(root2, "loader-report.txt"));
        t.Check("conflict_content_both_rejected",
            returned2 == null &&
            report2.Contains("id=Conflict.ContentX state=Failed code=MF0206") &&
            report2.Contains("id=Conflict.ContentY state=Failed code=MF0206"),
            Line(report2, "id=Conflict.ContentX") + " / " + Line(report2, "id=Conflict.ContentY"));
        t.Check("conflict_content_not_loaded", !AnyLoaded("DiffContentLib"), "DiffContentLib is not in the AppDomain");

        // ---- 3. identical copies -> shared, loaded exactly once, both initialize ---------
        var root3 = Temp.NewDirectory("conflict-shared");
        PrivateMod(root3, "Conflict.SharedA", "SharedLib.dll", fixtureDll, fixtureDir, "");
        PrivateMod(root3, "Conflict.SharedB", "SharedLib.dll", fixtureDll, fixtureDir, "");
        var returned3 = Loader.Run(root3);
        var report3 = Temp.ReadText(Path.Combine(root3, "loader-report.txt"));
        t.Check("conflict_shared_both_initialized",
            returned3 == null &&
            report3.Contains("id=Conflict.SharedA state=Initialized") &&
            report3.Contains("id=Conflict.SharedB state=Initialized"),
            Line(report3, "id=Conflict.SharedA") + " / " + Line(report3, "id=Conflict.SharedB"));
        t.Contains("conflict_shared_canonical_reported", report3, "preflight-shared=SharedLib canonical=Conflict.SharedA");
        var sharedLoads = CountAssemblyLoads(report3, "SharedLib.dll");
        t.Check("conflict_shared_loaded_once", sharedLoads == 1, "assembly-load lines for SharedLib=" + sharedLoads);
        t.Check("conflict_shared_owner_noted", ModBlock(report3, "Conflict.SharedB").Contains("shared=SharedLib.dll -> Conflict.SharedA"),
            ModBlock(report3, "Conflict.SharedB"));

        // ---- 4. one mod's entry vs another mod's private assembly ------------------------
        var root4 = Temp.NewDirectory("conflict-entry");
        var entryPackage = Path.Combine(root4, "Conflict.EntryOwner");
        Directory.CreateDirectory(entryPackage);
        File.Copy(Path.Combine(fixtureDir, "EntryCollisionLib.dll"), Path.Combine(entryPackage, "EntryCollisionLib.dll"));
        WriteManifest(entryPackage, Manifest("Conflict.EntryOwner", "ConflictLib.Entry", "", "EntryCollisionLib.dll"));
        PrivateMod(root4, "Conflict.PrivateOwner", "EntryCollisionLib.v2.dll", fixtureDll, fixtureDir, "");
        var returned4 = Loader.Run(root4);
        var report4 = Temp.ReadText(Path.Combine(root4, "loader-report.txt"));
        t.Check("conflict_entry_vs_private_rejected",
            returned4 == null &&
            report4.Contains("id=Conflict.EntryOwner state=Failed code=MF0206") &&
            report4.Contains("id=Conflict.PrivateOwner state=Failed code=MF0206"),
            Line(report4, "id=Conflict.EntryOwner") + " / " + Line(report4, "id=Conflict.PrivateOwner"));
        t.Check("conflict_entry_vs_private_not_loaded", !AnyLoaded("EntryCollisionLib"),
            "EntryCollisionLib is not in the AppDomain");

        // ---- 5. downstream of a conflicted mod is blocked, unrelated mod still runs ------
        var root5 = Temp.NewDirectory("conflict-downstream");
        PrivateMod(root5, "Conflict.Root", "DiffVersionLib.v1.dll", fixtureDll, fixtureDir, "");
        PrivateMod(root5, "Conflict.Rival", "DiffVersionLib.v2.dll", fixtureDll, fixtureDir, "");
        var downstream = Path.Combine(root5, "Conflict.Downstream");
        Directory.CreateDirectory(downstream);
        File.Copy(fixtureDll, Path.Combine(downstream, "FixtureMod.dll"));
        WriteManifest(downstream, Manifest("Conflict.Downstream", "FixtureMod.DependentEntry",
            "\"dependencies\": [ { \"id\": \"Conflict.Root\", \"version\": \"*\" } ]"));
        var unrelated = Path.Combine(root5, "Conflict.Unrelated");
        Directory.CreateDirectory(unrelated);
        File.Copy(fixtureDll, Path.Combine(unrelated, "FixtureMod.dll"));
        WriteManifest(unrelated, Manifest("Conflict.Unrelated", "FixtureMod.GoodEntry"));
        var returned5 = Loader.Run(root5);
        var report5 = Temp.ReadText(Path.Combine(root5, "loader-report.txt"));
        t.Check("conflict_downstream_blocked",
            returned5 == null && report5.Contains("id=Conflict.Downstream state=Failed code=MF0404"),
            Line(report5, "id=Conflict.Downstream"));
        t.Check("conflict_unrelated_still_initialized",
            report5.Contains("id=Conflict.Unrelated state=Initialized"), Line(report5, "id=Conflict.Unrelated"));

        // ---- 6. collision with an assembly the host already loaded ----------------------
        var root6 = Temp.NewDirectory("conflict-host");
        PrivateMod(root6, "Conflict.Host", "HostConflict.ModFramework.Runtime.dll", fixtureDll, fixtureDir, "");
        var returned6 = Loader.Run(root6);
        var report6 = Temp.ReadText(Path.Combine(root6, "loader-report.txt"));
        t.Check("conflict_host_assembly_rejected",
            returned6 == null && report6.Contains("id=Conflict.Host state=Failed code=MF0207"),
            Line(report6, "id=Conflict.Host"));
        t.Contains("conflict_host_group_reported", report6, "preflight-conflict=ModFramework.Runtime kind=host");

        // ---- 7. damaged / non-managed / missing declared files --------------------------
        var root7 = Temp.NewDirectory("conflict-broken");
        var corrupt = Path.Combine(root7, "Conflict.Corrupt");
        Directory.CreateDirectory(corrupt);
        File.Copy(fixtureDll, Path.Combine(corrupt, "FixtureMod.dll"));
        var corruptBytes = new byte[512];
        for (var i = 0; i < corruptBytes.Length; i++)
        {
            corruptBytes[i] = (byte)(i * 31 + 7);
        }
        File.WriteAllBytes(Path.Combine(corrupt, "BrokenLib.dll"), corruptBytes);
        WriteManifest(corrupt, Manifest("Conflict.Corrupt", "FixtureMod.GoodEntry", "\"privateAssemblies\": [ \"BrokenLib.dll\" ]"));

        var notManaged = Path.Combine(root7, "Conflict.NotManaged");
        Directory.CreateDirectory(notManaged);
        File.Copy(fixtureDll, Path.Combine(notManaged, "FixtureMod.dll"));
        var dosOnly = new byte[1024];
        dosOnly[0] = (byte)'M';
        dosOnly[1] = (byte)'Z';
        dosOnly[0x3C] = 0x40;
        File.WriteAllBytes(Path.Combine(notManaged, "NativeLib.dll"), dosOnly);
        WriteManifest(notManaged, Manifest("Conflict.NotManaged", "FixtureMod.GoodEntry", "\"privateAssemblies\": [ \"NativeLib.dll\" ]"));

        var missing = Path.Combine(root7, "Conflict.Missing");
        Directory.CreateDirectory(missing);
        File.Copy(fixtureDll, Path.Combine(missing, "FixtureMod.dll"));
        WriteManifest(missing, Manifest("Conflict.Missing", "FixtureMod.GoodEntry", "\"privateAssemblies\": [ \"Absent.dll\" ]"));

        var healthy = Path.Combine(root7, "Conflict.Healthy");
        Directory.CreateDirectory(healthy);
        File.Copy(fixtureDll, Path.Combine(healthy, "FixtureMod.dll"));
        WriteManifest(healthy, Manifest("Conflict.Healthy", "FixtureMod.GoodEntry"));

        var returned7 = Loader.Run(root7);
        var report7 = Temp.ReadText(Path.Combine(root7, "loader-report.txt"));
        t.Check("conflict_corrupt_dll_rejected",
            returned7 == null && report7.Contains("id=Conflict.Corrupt state=Failed code=MF0205"),
            Line(report7, "id=Conflict.Corrupt"));
        t.Check("conflict_not_managed_dll_rejected",
            report7.Contains("id=Conflict.NotManaged state=Failed code=MF0205"),
            Line(report7, "id=Conflict.NotManaged"));
        t.Check("conflict_missing_private_rejected",
            report7.Contains("id=Conflict.Missing state=Failed code=MF0201"),
            Line(report7, "id=Conflict.Missing"));
        t.Check("conflict_healthy_neighbour_survives",
            report7.Contains("id=Conflict.Healthy state=Initialized"), Line(report7, "id=Conflict.Healthy"));

        // ---- 8. determinism: reversed creation order gives the same conflict set --------
        var forward = Temp.NewDirectory("conflict-order-a");
        PrivateMod(forward, "Conflict.OrderA", "DiffVersionLib.v1.dll", fixtureDll, fixtureDir, "");
        PrivateMod(forward, "Conflict.OrderB", "DiffVersionLib.v2.dll", fixtureDll, fixtureDir, "");
        var reverse = Temp.NewDirectory("conflict-order-b");
        PrivateMod(reverse, "Conflict.OrderB", "DiffVersionLib.v2.dll", fixtureDll, fixtureDir, "");
        PrivateMod(reverse, "Conflict.OrderA", "DiffVersionLib.v1.dll", fixtureDll, fixtureDir, "");
        Loader.Run(forward);
        Loader.Run(reverse);
        var forwardConflicts = ConflictLines(Temp.ReadText(Path.Combine(forward, "loader-report.txt")));
        var reverseConflicts = ConflictLines(Temp.ReadText(Path.Combine(reverse, "loader-report.txt")));
        t.Check("conflict_deterministic_across_enumeration_order",
            forwardConflicts.Length > 0 && string.Equals(forwardConflicts, reverseConflicts, StringComparison.Ordinal),
            "forward=" + forwardConflicts + " reverse=" + reverseConflicts);
    }

    // ------------------------------------------------------------ logging status

    /// <summary>
    /// A log write failure must be visible in the report and must never change a mod's state.
    /// The failure is injected through the internal <c>ModLogger.AppendOverride</c> delegate,
    /// which is deterministic and needs no permissions, locks or a full disk.
    /// </summary>
    private static void LoggerDegraded(TestRunner t, string fixtureDir)
    {
        var fixtureDll = Path.Combine(fixtureDir, "FixtureMod.dll");
        var previous = ModLogger.AppendOverride;
        var calls = 0;
        try
        {
            ModLogger.AppendOverride = (path, text) =>
            {
                calls++;
                throw new IOException("injected log write failure");
            };

            // ---- Initialize succeeds, logging fails -> Initialized + log-status=degraded --
            var root = Temp.NewDirectory("log-degraded");
            AddPackage(root, "FixtureMod.LogDegraded", "FixtureMod.LogDegraded", "FixtureMod.GoodEntry", new[] { fixtureDll });
            var returned = Loader.Run(root);
            var report = Temp.ReadText(Path.Combine(root, "loader-report.txt"));
            t.Check("log_degraded_state_stays_initialized",
                returned == null && report.Contains("id=FixtureMod.LogDegraded state=Initialized"),
                Line(report, "id=FixtureMod.LogDegraded"));
            t.Contains("log_degraded_status_recorded", report, "log-status=degraded log-error=IOException");
            t.Contains("log_degraded_summary", report, "logging=degraded (1 of 1 mods)");
            t.Contains("log_degraded_warning", report, "logging degraded (IOException)");
            t.Check("log_degraded_stops_after_first_failure", calls == 1, "append attempts=" + calls);
            t.Check("log_degraded_entry_still_ran",
                File.Exists(Path.Combine(root, "FixtureMod.LogDegraded", "entry-ran.txt")), "marker written");

            // ---- Initialize throws while logging fails -> both facts are reported --------
            calls = 0;
            var root2 = Temp.NewDirectory("log-degraded-throwing");
            AddPackage(root2, "FixtureMod.LogAndThrow", "FixtureMod.LogAndThrow", "FixtureMod.ThrowingEntry", new[] { fixtureDll });
            var returned2 = Loader.Run(root2);
            var report2 = Temp.ReadText(Path.Combine(root2, "loader-report.txt"));
            t.Check("log_degraded_init_failure_kept",
                returned2 == null && report2.Contains("id=FixtureMod.LogAndThrow state=Failed code=MF0309"),
                Line(report2, "id=FixtureMod.LogAndThrow"));
            t.Contains("log_degraded_on_failure_path", report2, "log-status=degraded log-error=IOException");
            t.Check("log_degraded_failure_path_single_attempt", calls == 1, "append attempts=" + calls);

            // ---- healthy logging -> ok, and the file really receives INFO/WARN -----------
            ModLogger.AppendOverride = null;
            var root3 = Temp.NewDirectory("log-healthy");
            AddPackage(root3, "FixtureMod.LogHealthy", "FixtureMod.LogHealthy", "FixtureMod.GoodEntry", new[] { fixtureDll });
            var returned3 = Loader.Run(root3);
            var report3 = Temp.ReadText(Path.Combine(root3, "loader-report.txt"));
            var log = Temp.ReadText(Path.Combine(root3, "logs", "FixtureMod.LogHealthy.log"));
            t.Contains("log_healthy_status", report3, "log-status=ok");
            t.Contains("log_healthy_summary", report3, "logging=healthy");
            t.Check("log_healthy_file_written", returned3 == null && log.Contains("INFO ") && log.Contains("WARN "), log.Length + " chars");
        }
        finally
        {
            ModLogger.AppendOverride = previous;
        }
    }

    // ------------------------------------------------------ legacy probe removal

    /// <summary>
    /// P11-01: a bare <c>MonoRuntimeProbe.dll</c> in the mods root must never be loaded or
    /// invoked by the normal loader, while a real manifest mod next to it still initializes.
    /// </summary>
    private static void LegacyProbeNotExecuted(TestRunner t, string fixtureDir)
    {
        var probeDll = Path.Combine(fixtureDir, "MonoRuntimeProbe.dll");
        var fixtureDll = Path.Combine(fixtureDir, "FixtureMod.dll");
        if (!File.Exists(probeDll))
        {
            t.Check("legacy_probe_fixture_present", false, "expected MonoRuntimeProbe.dll in " + fixtureDir);
            return;
        }

        var root = Temp.NewDirectory("legacy-probe");
        // Exactly the Phase 0 layout: a bare DLL directly in the mods root, no manifest, no
        // subdirectory - plus one legitimate manifest mod.
        File.Copy(probeDll, Path.Combine(root, "MonoRuntimeProbe.dll"));
        AddPackage(root, "FixtureMod.ProbeNeighbour", "FixtureMod.ProbeNeighbour", "FixtureMod.GoodEntry", new[] { fixtureDll });

        var returned = Loader.Run(root);
        var report = Temp.ReadText(Path.Combine(root, "loader-report.txt"));
        var probeOutput = Path.Combine(root, "probe-output");

        t.Check("legacy_probe_run_returns_null", returned == null, returned ?? "(null)");
        t.Contains("legacy_probe_report_status", report, "probe=not-run-by-loader");
        t.Check("legacy_probe_marker_absent", !File.Exists(Path.Combine(probeOutput, "probe-marker.txt")),
            "no marker in probe-output");
        t.Check("legacy_probe_output_directory_absent", !Directory.Exists(probeOutput), probeOutput);
        t.Check("legacy_probe_assembly_not_loaded", !AnyLoaded("MonoRuntimeProbe"),
            "MonoRuntimeProbe is not in the AppDomain after a normal run");
        t.Check("legacy_probe_neighbour_initialized",
            report.Contains("id=FixtureMod.ProbeNeighbour state=Initialized"),
            Line(report, "id=FixtureMod.ProbeNeighbour"));
        t.Check("legacy_probe_bare_dll_not_a_package", !report.Contains("mods-discovered=2"),
            "the bare DLL is not discovered as a mod package");

        // The decoy is real: an explicit invocation (what a separate diagnostic tool would do)
        // does produce the marker. This proves the negative assertion above is meaningful.
        var explicitRoot = Temp.NewDirectory("legacy-probe-explicit");
        var explicitCopy = Path.Combine(explicitRoot, "MonoRuntimeProbe.dll");
        File.Copy(probeDll, explicitCopy);
        var assembly = Assembly.LoadFrom(explicitCopy);
        var type = assembly.GetType("MonoRuntimeProbe.Probe", false, false);
        var run = type == null ? null : type.GetMethod("Run");
        var explicitOutput = Path.Combine(explicitRoot, "probe-output");
        var invoked = run != null && run.Invoke(null, new object[] { explicitOutput, false }) != null;
        t.Check("legacy_probe_marker_works_when_invoked_explicitly",
            invoked && File.Exists(Path.Combine(explicitOutput, "probe-marker.txt")),
            "only an explicit invocation runs the probe");
    }

    /// <summary>
    /// End-to-end run of the repository's own example mod (Mod/ExampleMod.HelloWorld).
    /// The package directory is the mod project's build output, so this exercises the real
    /// manifest.json that ships with the example.
    /// </summary>
    internal static void HelloWorld(TestRunner t, string packageDirectory)
    {
        if (!File.Exists(Path.Combine(packageDirectory, "manifest.json")) ||
            !File.Exists(Path.Combine(packageDirectory, "ExampleMod.HelloWorld.dll")))
        {
            t.Check("hello_world_package_present", false, "expected manifest.json and ExampleMod.HelloWorld.dll in " + packageDirectory);
            return;
        }

        var root = Temp.NewDirectory("helloworld");
        var package = Path.Combine(root, "ExampleMod.HelloWorld");
        Directory.CreateDirectory(package);
        CopyFile(Path.Combine(packageDirectory, "manifest.json"), Path.Combine(package, "manifest.json"));
        CopyFile(Path.Combine(packageDirectory, "ExampleMod.HelloWorld.dll"), Path.Combine(package, "ExampleMod.HelloWorld.dll"));

        // The example manifest declares "gameVersion": ">=1.6.0 <2.0.0". Phase 1.1 made that
        // constraint fail-closed, and a desktop host has no StardewValley assembly, so the test
        // supplies the version a real device would detect. This is the only way to exercise the
        // example package end-to-end without pretending the desktop is a game process.
        var previousGame = Loader.GameVersionOverride;
        string returned = "";
        var report = "";
        var log = "";
        try
        {
            Loader.GameVersionOverride = "1.6.15";
            returned = Loader.Run(root) ?? "";
            report = Temp.ReadText(Path.Combine(root, "loader-report.txt"));
            log = Temp.ReadText(Path.Combine(root, "logs", "ExampleMod.HelloWorld.log"));
        }
        finally
        {
            Loader.GameVersionOverride = previousGame;
        }

        t.Check("hello_world_run_returns_null", returned.Length == 0, returned.Length == 0 ? "(null)" : returned);
        t.Contains("hello_world_discovered", report, "id=ExampleMod.HelloWorld state=Initialized");
        t.Contains("hello_world_history_complete", report,
            "history=ManifestValidated>DependenciesResolved>AssemblyLoaded>EntryValidated>Instantiated>Initialized");
        t.Contains("hello_world_game_version_applied", report, "game-version-state=known");
        t.Contains("hello_world_log_message", log, "Hello World from \"Hello World\" 1.0.0");
        t.Contains("hello_world_log_versions", log, "framework=1.0.0");
    }
    // ---------------------------------------------------------------- helpers

    /// <summary>A mod whose entry is a version-gate fixture, plus the compatibility range under test.</summary>
    private static void GatePackage(string root, string modId, string extraJson, string gateDll)
    {
        var package = Path.Combine(root, modId);
        Directory.CreateDirectory(package);
        File.Copy(gateDll, Path.Combine(package, "VersionGateLib.dll"));
        WriteManifest(package, Manifest(modId, "ConflictLib.Entry", extraJson, "VersionGateLib.dll"));
    }

    /// <summary>A mod that keeps the normal fixture entry but ships one declared private assembly.</summary>
    private static void PrivateMod(
        string root,
        string modId,
        string privateFileName,
        string entryDll,
        string fixtureDir,
        string extraJson)
    {
        var package = Path.Combine(root, modId);
        Directory.CreateDirectory(package);
        File.Copy(Path.Combine(fixtureDir, privateFileName), Path.Combine(package, privateFileName));
        File.Copy(entryDll, Path.Combine(package, "FixtureMod.dll"));
        var extra = "\"privateAssemblies\": [ \"" + privateFileName + "\" ]";
        if (extraJson.Length > 0)
        {
            extra += ", " + extraJson;
        }
        WriteManifest(package, Manifest(modId, "FixtureMod.GoodEntry", extra));
    }

    private static bool AnyLoaded(string simpleName)
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies();
        for (var i = 0; i < loaded.Length; i++)
        {
            var name = loaded[i].GetName();
            if (name != null && string.Equals(name.Name, simpleName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>First report line that mentions <paramref name="needle"/>, for failure details.</summary>
    private static string Line(string report, string needle)
    {
        var lines = report.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].IndexOf(needle, StringComparison.Ordinal) >= 0)
            {
                return lines[i].Trim();
            }
        }
        return "(no line for " + needle + ")";
    }

    /// <summary>The whole <c>mods:</c> block of one mod id (id line plus its indented lines).</summary>
    private static string ModBlock(string report, string modId)
    {
        var lines = report.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
        var sb = new StringBuilder();
        var inside = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.StartsWith("- id=", StringComparison.Ordinal))
            {
                if (inside)
                {
                    break;
                }
                if (trimmed.IndexOf("id=" + modId + " ", StringComparison.Ordinal) >= 0)
                {
                    inside = true;
                    sb.AppendLine(trimmed);
                }
                continue;
            }
            if (inside)
            {
                sb.AppendLine(trimmed);
            }
        }
        return sb.ToString();
    }

    /// <summary>True when the report shows the preflight finished before the first LoadFrom.</summary>
    private static bool PreflightBeforeLoad(string report)
    {
        var complete = report.IndexOf("preflight-stage=complete", StringComparison.Ordinal);
        if (complete < 0)
        {
            return false;
        }
        var load = report.IndexOf("assembly-load=", StringComparison.Ordinal);
        return load < 0 || complete < load;
    }

    /// <summary>Number of <c>assembly-load=</c> report lines that name this file.</summary>
    private static int CountAssemblyLoads(string report, string fileName)
    {
        var count = 0;
        var lines = report.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("assembly-load=", StringComparison.Ordinal) &&
                trimmed.IndexOf("file=" + fileName, StringComparison.Ordinal) >= 0)
            {
                count++;
            }
        }
        return count;
    }

    private static string ConflictLines(string report)
    {
        var sb = new StringBuilder();
        var lines = report.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (!trimmed.StartsWith("preflight-conflict=", StringComparison.Ordinal))
            {
                continue;
            }
            if (sb.Length > 0)
            {
                sb.Append(" | ");
            }
            sb.Append(trimmed);
        }
        return sb.ToString();
    }

    private static string InitializedOrder(string report)
    {
        var order = "";
        var lines = report.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            const string prefix = "initialized=";
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                if (order.Length > 0)
                {
                    order += ",";
                }
                order += line.Substring(prefix.Length);
            }
        }
        return order;
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                count++;
            }
        }
        return count;
    }

    private static void AddPackage(
        string root,
        string directoryName,
        string id,
        string entryType,
        string[] dlls,
        string entryAssembly = "FixtureMod.dll",
        string extraJson = "")
    {
        var package = Path.Combine(root, directoryName);
        Directory.CreateDirectory(package);
        WriteManifest(package, Manifest(id, entryType, extraJson, entryAssembly));
        for (var i = 0; i < dlls.Length; i++)
        {
            CopyFile(dlls[i], Path.Combine(package, Path.GetFileName(dlls[i])));
        }
        if (!string.Equals(entryAssembly, "FixtureMod.dll", StringComparison.Ordinal) &&
            dlls.Length > 0 &&
            !File.Exists(Path.Combine(package, entryAssembly)))
        {
            CopyFile(dlls[0], Path.Combine(package, entryAssembly));
        }
    }

    private static void WriteManifest(string package, string json)
    {
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "manifest.json"), json, new UTF8Encoding(false));
    }

    private static void CopyFile(string source, string destination)
    {
        File.Copy(source, destination, true);
    }

    private static string Manifest(string id, string entryType, string extraJson = "", string entryAssembly = "FixtureMod.dll")
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"schemaVersion\": 1,\n");
        sb.Append("  \"id\": \"" + id + "\",\n");
        sb.Append("  \"name\": \"" + id + "\",\n");
        sb.Append("  \"version\": \"1.0.0\",\n");
        sb.Append("  \"entryAssembly\": \"" + entryAssembly + "\",\n");
        sb.Append("  \"entryType\": \"" + entryType + "\"");
        if (extraJson.Length > 0)
        {
            sb.Append(",\n  " + extraJson);
        }
        sb.Append("\n}\n");
        return sb.ToString();
    }
}
