using System;
using System.IO;
using System.Reflection;
using ModFramework.Runtime;

namespace ModFramework.Tests;

/// <summary>
/// Phase 1.1.4: the P114.RuntimeProbe diagnostic mod, run through the <b>real</b> loader chain
/// (manifest discovery -> validation -> dependency resolution -> global assembly preflight ->
/// snapshot staging -> <c>Assembly.LoadFrom</c> -> <c>IModEntry.Initialize</c>).
///
/// Evidence grade of everything here: **Desktop .NET host**. It proves that the probe code and
/// the loader path work and that the observations are recorded; it does NOT prove MonoVM-on-
/// Android behaviour. Emulator and ARM64 physical device results are separate grades and are
/// reported separately (see docs/audit/phase1.1.4-report.md).
/// </summary>
internal static class Phase114Tests
{
    private const string ModId = "P114.RuntimeProbe";
    private const string EntryAssemblyFileName = "P114.RuntimeProbe.dll";
    private const string ReportRelativePath = "probe-output/phase1.1.4.txt";

    internal static void Run(TestRunner t, string packageDir)
    {
        if (!PackagePresent(t, packageDir))
        {
            return;
        }

        var root = Temp.NewDirectory("p114");
        var package = Path.Combine(root, ModId);
        CopyTree(packageDir, package);

        var returned = Loader.Run(root);
        var loaderReport = Temp.ReadText(Path.Combine(root, "loader-report.txt"));
        var probeReport = Temp.ReadText(Path.Combine(package, "probe-output", "phase1.1.4.txt"));

        t.Check("p114_loader_returns_null", returned == null, returned ?? "(null)");
        t.Check("p114_probe_report_written", probeReport.Length > 0,
            Path.Combine(package, "probe-output", "phase1.1.4.txt"));
        if (probeReport.Length == 0)
        {
            t.Check("p114_probe_report_readable", false, FirstLines(loaderReport, 6));
            return;
        }

        ReportShape(t, probeReport);
        Cases(t, probeReport, package);
        LoaderEvidence(t, loaderReport);
        StagingEvidence(t);
    }

    // ---------------------------------------------------------------- package

    private static bool PackagePresent(TestRunner t, string packageDir)
    {
        var manifest = Path.Combine(packageDir, "manifest.json");
        var entry = Path.Combine(packageDir, EntryAssemblyFileName);
        var dependency = Path.Combine(packageDir, "P114.RuntimeProbe.Dependency.dll");
        var fixtures = Path.Combine(packageDir, "probe-fixtures");
        var ok = Directory.Exists(packageDir) && File.Exists(manifest) && File.Exists(entry) &&
                 File.Exists(dependency) && Directory.Exists(fixtures);
        t.Check("p114_package_present", ok, packageDir);
        return ok;
    }

    // ---------------------------------------------------------------- report shape

    private static void ReportShape(TestRunner t, string report)
    {
        t.Check("p114_report_schema", report.Contains("schema      : phase1.1.4/safe/1"),
            TestSupport.Line(report, "schema"));
        t.Check("p114_report_safe_mode", report.Contains("mode        : safe-only"), TestSupport.Line(report, "mode"));
        t.Check("p114_report_no_native_marker",
            report.Contains("no P/Invoke, no mprotect, no RWX, no detour"), TestSupport.Line(report, "mode"));
        t.Check("p114_report_declares_loader_observed_cases",
            report.Contains("R-03 NOT RUN") && report.Contains("R-12 NOT RUN"),
            "R-03/R-12 are loader-observed, never claimed by the mod itself");

        // R-09 / P114-07: the probe report must never contain an absolute path.
        var hasDrive = report.IndexOf(":\\", StringComparison.Ordinal) >= 0;
        var hasUnixData = report.IndexOf("/data/", StringComparison.Ordinal) >= 0;
        t.Check("p114_report_has_no_absolute_path", !hasDrive && !hasUnixData,
            "drive-letter=" + hasDrive + " /data/=" + hasUnixData);
    }

    // ---------------------------------------------------------------- cases

    private static void Cases(TestRunner t, string report, string package)
    {
        foreach (var id in new[] { "R-01", "R-02", "R-04", "R-05", "R-06", "R-07", "R-08",
                                   "R-09", "R-10", "R-11", "R-13", "R-14" })
        {
            t.Check("p114_case_present_" + id, report.Contains(id + " PASS") || report.Contains(id + " FAIL") ||
                                              report.Contains(id + " BLOCKED"), id);
        }

        var r01 = TestSupport.Line(report, "R-01");
        t.Check("p114_r01_sha256_empty_vector",
            r01.Contains("R-01 PASS") && r01.Contains("len=32") && r01.Contains("matches-vector=yes"), r01);

        var r02 = TestSupport.Line(report, "R-02");
        t.Check("p114_r02_sha256_abc_vector",
            r02.Contains("R-02 PASS") && r02.Contains("ba7816bf8f01cfea414140de5dae2223") &&
            r02.Contains("matches-vector=yes"), r02);

        var r04 = TestSupport.Line(report, "R-04");
        t.Check("p114_r04_same_path_load_twice",
            r04.Contains("first=ok") && r04.Contains("second=ok"), r04);
        t.Check("p114_r04_same_path_same_reference", r04.Contains("same-reference=True"),
            "desktop .NET caches by path; a device may differ and that is recorded separately");

        var r05 = TestSupport.Line(report, "R-05");
        t.Check("p114_r05_identical_bytes_two_paths", r05.Contains("same-bytes=yes") && r05.Contains("load-a=ok"),
            r05);
        t.Check("p114_r05_appdomain_delta_recorded", r05.Contains("appdomain-before=") && r05.Contains("delta="), r05);

        var r06 = TestSupport.Line(report, "R-06");
        t.Check("p114_r06_same_identity_different_bytes",
            r06.Contains("same-bytes=no") && r06.Contains("load-a=ok") && r06.Contains("marker-a=variant=A"), r06);
        t.Check("p114_r06_records_second_load_outcome",
            r06.Contains("load-b=ok") || r06.Contains("load-b=FileLoadException") ||
            r06.Contains("load-b=BadImageFormatException") ||
            r06.Contains("load-b=IOException"), r06);

        var r07 = TestSupport.Line(report, "R-07");
        t.Check("p114_r07_different_simple_names",
            r07.Contains("name-alpha=P114Alpha") && r07.Contains("name-beta=P114Beta") &&
            r07.Contains("independent-reference=True"), r07);

        var r08 = TestSupport.Line(report, "R-08");
        t.Check("p114_r08_identity_fields",
            r08.Contains("entry=name=P114.RuntimeProbe") && r08.Contains("version=") &&
            r08.Contains("culture=") && r08.Contains("token="), r08);

        var r09 = TestSupport.Line(report, "R-09");
        t.Check("p114_r09_location_redacted",
            r09.Contains("leaf=") && r09.Contains("absolute-path-redacted=yes") &&
            r09.Contains(EntryAssemblyFileName), r09);

        var r10 = TestSupport.Line(report, "R-10");
        t.Check("p114_r10_appdomain_enumeration",
            r10.Contains("count=") && r10.Contains("entry-index=") && r10.Contains("names="), r10);
        t.Check("p114_r10_entry_assembly_enumerated", IsIndexAtLeast(r10, "entry-index=", 0), r10);

        var r11 = TestSupport.Line(report, "R-11");
        t.Check("p114_r11_private_dependency_callable",
            r11.Contains("p114-private-dependency-alive") && r11.Contains("preloaded-before-entry=True"), r11);

        var r13 = TestSupport.Line(report, "R-13");
        t.Check("p114_r13_production_hash",
            r13.Contains("loaded-from=" + EntryAssemblyFileName) && r13.Contains("sha256=") &&
            r13.Contains("bytes="), r13);

        var r14 = TestSupport.Line(report, "R-14");
        t.Check("p114_r14_isolation_recorded",
            r14.Contains("failed=0") && r14.Contains("corrupt-load=") && r14.Contains("corrupt-loaded=False"), r14);
        t.Check("p114_r14_deliberate_throw_caught", r14.Contains("deliberate-throw-caught=InvalidOperationException"),
            r14);

        t.Check("p114_report_has_no_room_for_package_path",
            report.IndexOf(package, StringComparison.Ordinal) < 0, "the report must not echo the package path");
    }

    // ---------------------------------------------------------------- loader evidence

    private static void LoaderEvidence(TestRunner t, string report)
    {
        var preflight = TestSupport.LineIndex(report, "preflight-stage=complete");
        var entryLoadIndex = IndexOfLineContaining(report, "assembly-load=", "file=" + EntryAssemblyFileName);
        t.Check("p114_loader_preflight_complete", preflight >= 0, "preflight-stage=complete");
        t.Check("p114_loader_load_after_preflight", preflight >= 0 && entryLoadIndex > preflight,
            "preflight=" + preflight + " entry-load=" + entryLoadIndex);

        var loadLine = LineContaining(report, "assembly-load=", "file=" + EntryAssemblyFileName);
        t.Check("p114_loader_entry_loaded_from_snapshot",
            loadLine.Contains("file=" + EntryAssemblyFileName) && loadLine.Contains("snapshot=sha256:"), loadLine);

        // The declared private dependency must have been pre-loaded (its own assembly-load line
        // comes before the entry assembly's), which is what R-11 depends on.
        var dependencyLoadIndex = IndexOfLineContaining(report, "assembly-load=",
            "file=P114.RuntimeProbe.Dependency.dll");
        t.Check("p114_loader_private_dependency_preloaded",
            dependencyLoadIndex >= 0 && entryLoadIndex > dependencyLoadIndex,
            "dependency=" + dependencyLoadIndex + " entry=" + entryLoadIndex);

        var stateLine = TestSupport.Line(report, "- id=" + ModId + " state=");
        t.Check("p114_loader_mod_initialized", stateLine.Contains("state=Initialized"), stateLine);

        var stagingFailures = new[] { "MF0209", "MF0202", "MF0309", "MF0207", "MF0206", "MF0201" };
        var found = "";
        foreach (var code in stagingFailures)
        {
            if (report.Contains(code))
            {
                found += code + " ";
            }
        }
        t.Check("p114_loader_no_staging_or_load_failure", found.Length == 0, found.Length == 0 ? "none" : found);

        t.Check("p114_loader_probe_status_unchanged", report.Contains("probe=not-run-by-loader"),
            "the loader still never auto-runs a probe");
    }

    /// <summary>First line that contains every fragment, or a placeholder when there is none.</summary>
    private static string LineContaining(string report, params string[] fragments)
    {
        var lines = report.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            var all = true;
            for (var f = 0; f < fragments.Length; f++)
            {
                if (line.IndexOf(fragments[f], StringComparison.Ordinal) < 0)
                {
                    all = false;
                    break;
                }
            }
            if (all)
            {
                return line;
            }
        }
        return "(no line for " + string.Join(" + ", fragments) + ")";
    }

    private static int IndexOfLineContaining(string report, params string[] fragments)
    {
        var lines = report.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            var all = true;
            for (var f = 0; f < fragments.Length; f++)
            {
                if (line.IndexOf(fragments[f], StringComparison.Ordinal) < 0)
                {
                    all = false;
                    break;
                }
            }
            if (all)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>R-12: the loaded entry assembly must come from the staged snapshot, not the package.</summary>
    private static void StagingEvidence(TestRunner t)
    {
        Assembly entry = null;
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (var i = 0; i < assemblies.Length; i++)
        {
            var name = assemblies[i].GetName().Name;
            if (string.Equals(name, ModId, StringComparison.Ordinal))
            {
                entry = assemblies[i];
                break;
            }
        }
        if (entry == null)
        {
            t.Check("p114_loaded_from_staging", false, "the entry assembly is not in the AppDomain");
            return;
        }
        var location = entry.Location ?? "";
        var redacted = RedactTemp(location);
        t.Check("p114_loaded_from_staging", location.IndexOf(".staging", StringComparison.Ordinal) >= 0,
            redacted);
    }

    /// <summary>Test logs may be pasted; never echo the operator's temp directory.</summary>
    private static string RedactTemp(string path)
    {
        var temp = Path.GetTempPath();
        var text = path.Replace('\\', '/');
        var root = temp.Replace('\\', '/').TrimEnd('/');
        if (root.Length > 3 && text.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            text = "<tmp>" + text.Substring(root.Length);
        }
        return text;
    }

    // ---------------------------------------------------------------- helpers

    private static bool IsIndexAtLeast(string line, string prefix, int minimum)
    {
        var at = line.IndexOf(prefix, StringComparison.Ordinal);
        if (at < 0)
        {
            return false;
        }
        var start = at + prefix.Length;
        var end = start;
        while (end < line.Length && line[end] >= '0' && line[end] <= '9')
        {
            end++;
        }
        if (end == start)
        {
            return false;
        }
        return int.Parse(line.Substring(start, end - start)) >= minimum;
    }

    private static string FirstLines(string text, int count)
    {
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < lines.Length && i < count; i++)
        {
            sb.Append(lines[i].Trim()).Append(" | ");
        }
        return sb.ToString();
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyTree(dir, Path.Combine(target, Path.GetFileName(dir)));
        }
    }
}
