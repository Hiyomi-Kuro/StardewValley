using System;
using System.Collections.Generic;
using System.IO;
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
        CompatibilityGate(t, fixtureDll, dependencyDll);
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

    private static void CompatibilityGate(TestRunner t, string fixtureDll, string dependencyDll)
    {
        var root = Temp.NewDirectory("compat");
        AddPackage(root, "FixtureMod.Needs99", "FixtureMod.Needs99", "FixtureMod.GoodEntry",
            new[] { fixtureDll, dependencyDll }, "FixtureMod.dll",
            "\"gameVersion\": \">=99.0.0\", \"privateAssemblies\": [ \"FixtureMod.Dependency.dll\" ]");

        var previous = Loader.GameVersionOverride;
        try
        {
            // Unknown game version: the gate stays open and says so, instead of rejecting the mod.
            Loader.GameVersionOverride = null;
            var unknownRoot = Temp.NewDirectory("compat-unknown");
            AddPackage(unknownRoot, "FixtureMod.Needs99", "FixtureMod.Needs99", "FixtureMod.GoodEntry",
                new[] { fixtureDll, dependencyDll }, "FixtureMod.dll",
                "\"gameVersion\": \">=99.0.0\", \"privateAssemblies\": [ \"FixtureMod.Dependency.dll\" ]");
            var unknownResult = Loader.Run(unknownRoot);
            var unknownReport = Temp.ReadText(Path.Combine(unknownRoot, "loader-report.txt"));
            t.Check("host_game_version_unknown_does_not_block",
                unknownResult == null && unknownReport.IndexOf("state=Initialized", StringComparison.Ordinal) >= 0,
                unknownResult ?? "(null)");

            // Known game version 1.0.0 against ">=99.0.0": rejected with MF0501.
            Loader.GameVersionOverride = "1.0.0";
            var knownResult = Loader.Run(root);
            var knownReport = Temp.ReadText(Path.Combine(root, "loader-report.txt"));
            t.Check("host_game_version_mismatch_rejected",
                knownResult == null && knownReport.IndexOf("code=MF0501", StringComparison.Ordinal) >= 0,
                knownResult ?? "(null)");
        }
        finally
        {
            Loader.GameVersionOverride = previous;
        }
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

        var returned = Loader.Run(root);
        var report = Temp.ReadText(Path.Combine(root, "loader-report.txt"));
        var log = Temp.ReadText(Path.Combine(root, "logs", "ExampleMod.HelloWorld.log"));

        t.Check("hello_world_run_returns_null", returned == null, returned ?? "(null)");
        t.Contains("hello_world_discovered", report, "id=ExampleMod.HelloWorld state=Initialized");
        t.Contains("hello_world_history_complete", report,
            "history=ManifestValidated>DependenciesResolved>AssemblyLoaded>EntryValidated>Instantiated>Initialized");
        t.Contains("hello_world_log_message", log, "Hello World from \"Hello World\" 1.0.0");
        t.Contains("hello_world_log_versions", log, "framework=1.0.0");
    }
    // ---------------------------------------------------------------- helpers

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
