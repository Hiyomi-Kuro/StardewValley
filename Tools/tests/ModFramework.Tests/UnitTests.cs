using ModFramework.Runtime;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ModFramework.Tests;

internal static class UnitTests
{
    internal static void Run(TestRunner t, string repoRoot)
    {
        SemVerTests(t);
        VersionRangeTests(t);
        JsonTests(t);
        ManifestFixtureTests(t, repoRoot);
        DiscoveryTests(t);
        DependencyTests(t);
        MetadataTests(t);
    }

    // ------------------------------------------------------- assembly metadata

    /// <summary>
    /// The preflight reads assembly identity from file bytes because the trimmed runtime has no
    /// <c>AssemblyName.GetAssemblyName(string)</c>. These checks prove that reader against a real
    /// assembly, and that it refuses everything that is not a managed assembly.
    /// </summary>
    private static void MetadataTests(TestRunner t)
    {
        var runtimePath = typeof(Loader).Assembly.Location;
        t.Check("metadata_runtime_path_available", runtimePath != null && File.Exists(runtimePath),
            runtimePath == null ? "(null)" : runtimePath);

        byte[] bytes;
        string readError;
        var readOk = AssemblyClaimReader.TryReadAll(runtimePath, out bytes, out readError);
        t.Check("metadata_read_all", readOk, readError);

        AssemblyFileIdentity identity;
        string error;
        var parsed = ManagedAssemblyReader.TryReadIdentity(bytes, out identity, out error);
        t.Check("metadata_reads_managed_identity", parsed, error);

        var loadedName = typeof(Loader).Assembly.GetName();
        t.Check("metadata_name_matches_loaded",
            string.Equals(identity.Name, loadedName.Name, StringComparison.Ordinal),
            identity.Name + " vs " + loadedName.Name);
        t.Check("metadata_version_matches_loaded",
            string.Equals(identity.VersionText, HostAssemblyIdentity.NormalizeVersion(loadedName.Version), StringComparison.Ordinal),
            identity.VersionText + " vs " + HostAssemblyIdentity.NormalizeVersion(loadedName.Version));
        t.Check("metadata_unsigned_fixture", !identity.IsStrongNamed, "the framework assembly is not strong-name signed");
        t.Check("metadata_identity_text_is_path_free",
            identity.Describe().IndexOf('/') < 0 && identity.Describe().IndexOf('\\') < 0,
            identity.Describe());

        AssemblyFileIdentity ignored;
        t.Check("metadata_rejects_empty",
            !ManagedAssemblyReader.TryReadIdentity(new byte[0], out ignored, out error), error);
        t.Check("metadata_rejects_text",
            !ManagedAssemblyReader.TryReadIdentity(Encoding.UTF8.GetBytes("not an assembly at all"), out ignored, out error), error);

        var truncated = new byte[300];
        Array.Copy(bytes, truncated, truncated.Length);
        t.Check("metadata_rejects_truncated",
            !ManagedAssemblyReader.TryReadIdentity(truncated, out ignored, out error), error);

        var mzOnly = new byte[2048];
        mzOnly[0] = (byte)'M';
        mzOnly[1] = (byte)'Z';
        mzOnly[0x3C] = 0x40;
        t.Check("metadata_rejects_mz_without_pe",
            !ManagedAssemblyReader.TryReadIdentity(mzOnly, out ignored, out error), error);

        var digest = AssemblyClaimReader.ComputeDigest(bytes);
        t.Check("metadata_digest_is_sha256",
            digest.StartsWith("sha256:", StringComparison.Ordinal) && digest.Length == 7 + 64,
            digest.Length + " chars");
        t.Check("metadata_digest_is_stable",
            string.Equals(digest, AssemblyClaimReader.ComputeDigest(bytes), StringComparison.Ordinal), digest);

        var previous = AssemblyClaimReader.DisableSha256ForTests;
        try
        {
            AssemblyClaimReader.DisableSha256ForTests = true;
            // Phase 1.1.1: when SHA-256 is unavailable the digest is *empty*, never a weaker
            // fingerprint. Content equality then falls back to the full byte comparison, which
            // is the only thing allowed to prove that two files are the same.
            var unavailable = AssemblyClaimReader.ComputeDigest(bytes);
            t.Check("metadata_digest_unavailable_is_empty",
                unavailable.Length == 0, unavailable.Length == 0 ? "(empty)" : unavailable);
            t.Check("metadata_digest_has_no_weak_fallback",
                !unavailable.StartsWith("fnv1a64", StringComparison.Ordinal) &&
                !unavailable.StartsWith("crc", StringComparison.OrdinalIgnoreCase),
                unavailable);
            t.Check("metadata_digest_unavailable_is_stable",
                string.Equals(unavailable, AssemblyClaimReader.ComputeDigest(bytes), StringComparison.Ordinal),
                unavailable);
        }
        finally
        {
            AssemblyClaimReader.DisableSha256ForTests = previous;
        }

        t.Check("metadata_identity_text_marks_unsigned",
            identity.Describe().IndexOf("PublicKey=none", StringComparison.Ordinal) >= 0,
            identity.Describe());

        t.Check("path_rules_inside", PathRules.IsInside("/mods/A", "/mods/A/x.dll"), "same directory prefix");
        t.Check("path_rules_rejects_sibling_prefix", !PathRules.IsInside("/mods/A", "/mods/AB/x.dll"), "AB is not inside A/");
        t.Check("path_rules_accepts_subdirectory", PathRules.IsInside("/mods/A", "/mods/A/sub/x.dll"), "subdirectory");

        var codes = new[]
        {
            ErrorCodes.AssemblyIdentityUnreadable, ErrorCodes.AssemblyIdentityConflict,
            ErrorCodes.HostAssemblyConflict, ErrorCodes.GameVersionUnknown,
            ErrorCodes.GameVersionInvalid, ErrorCodes.FrameworkVersionInvalid,
            ErrorCodes.ReportWriteFailed,
        };
        var described = 0;
        for (var i = 0; i < codes.Length; i++)
        {
            if (!string.Equals(ErrorCodes.Describe(codes[i]), "unknown error", StringComparison.Ordinal))
            {
                described++;
            }
        }
        t.Check("errorcodes_new_codes_have_text", described == codes.Length,
            described + "/" + codes.Length + " new codes documented in Describe()");
    }

    // ---------------------------------------------------------------- SemVer

    private static void SemVerTests(TestRunner t)
    {
        SemVersion v;
        string error;

        t.Check("semver_parse_basic", SemVersion.TryParse("1.2.3", out v, out error) && v.Major == 1 && v.Minor == 2 && v.Patch == 3,
            "1.2.3");
        t.Check("semver_parse_prerelease", SemVersion.TryParse("1.2.3-alpha.1", out v, out error) && v.IsPrerelease && v.PreRelease.Length == 2,
            "1.2.3-alpha.1");
        t.Check("semver_parse_build", SemVersion.TryParse("1.2.3+build.5", out v, out error) && v.Build == "build.5" && !v.IsPrerelease,
            "1.2.3+build.5");
        t.Check("semver_parse_prerelease_and_build", SemVersion.TryParse("1.2.3-rc.1+sha.abc", out v, out error) && v.IsPrerelease && v.Build == "sha.abc",
            "1.2.3-rc.1+sha.abc");

        t.Check("semver_reject_two_components", !SemVersion.TryParse("1.2", out v, out error), error);
        t.Check("semver_reject_four_components", !SemVersion.TryParse("1.2.3.4", out v, out error), error);
        t.Check("semver_reject_leading_zero", !SemVersion.TryParse("01.2.3", out v, out error), error);
        t.Check("semver_reject_prerelease_leading_zero", !SemVersion.TryParse("1.2.3-01", out v, out error), error);
        t.Check("semver_reject_empty", !SemVersion.TryParse("", out v, out error), error);
        t.Check("semver_reject_garbage", !SemVersion.TryParse("v1.2.3", out v, out error), error);
        t.Check("semver_reject_bad_build", !SemVersion.TryParse("1.2.3+a..b", out v, out error), error);

        t.Check("semver_compare_core", Compare("1.0.0", "1.0.1") < 0 && Compare("1.1.0", "2.0.0") < 0, "1.0.0<1.0.1, 1.1.0<2.0.0");
        t.Check("semver_compare_build_ignored", Compare("1.0.0+a", "1.0.0+b") == 0, "build metadata never participates");

        var prereleaseOrder = new[]
        {
            "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta",
            "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0",
        };
        var ordered = true;
        for (var i = 1; i < prereleaseOrder.Length; i++)
        {
            if (Compare(prereleaseOrder[i - 1], prereleaseOrder[i]) >= 0)
            {
                ordered = false;
            }
        }
        t.Check("semver_prerelease_precedence", ordered, "SemVer 2.0.0 section 11 example order");
    }

    private static int Compare(string a, string b)
    {
        SemVersion left, right;
        string error;
        if (!SemVersion.TryParse(a, out left, out error) || !SemVersion.TryParse(b, out right, out error))
        {
            throw new InvalidOperationException("bad test input: " + a + " / " + b);
        }
        return left.CompareTo(right);
    }

    // ------------------------------------------------------------ version ranges

    private static void VersionRangeTests(TestRunner t)
    {
        t.Check("range_any", Satisfies("*", "0.0.1") && Satisfies("*", "99.0.0"), "*");
        t.Check("range_gte_lt", Satisfies(">=1.0.0 <2.0.0", "1.5.0") && !Satisfies(">=1.0.0 <2.0.0", "2.0.0") && !Satisfies(">=1.0.0 <2.0.0", "0.9.9"),
            ">=1.0.0 <2.0.0");
        t.Check("range_caret_major", Satisfies("^1.2.3", "1.9.9") && !Satisfies("^1.2.3", "2.0.0") && !Satisfies("^1.2.3", "1.2.2"),
            "^1.2.3");
        t.Check("range_caret_zero_major", Satisfies("^0.2.3", "0.2.9") && !Satisfies("^0.2.3", "0.3.0"), "^0.2.3");
        t.Check("range_caret_zero_minor", Satisfies("^0.0.3", "0.0.3") && !Satisfies("^0.0.3", "0.0.4"), "^0.0.3");
        t.Check("range_tilde", Satisfies("~1.2.3", "1.2.9") && !Satisfies("~1.2.3", "1.3.0"), "~1.2.3");
        t.Check("range_exact_bare", Satisfies("1.2.3", "1.2.3") && !Satisfies("1.2.3", "1.2.4"), "bare version means exact");
        t.Check("range_exact_equals", Satisfies("=1.2.3", "1.2.3") && !Satisfies("=1.2.3", "1.2.4"), "=1.2.3");
        t.Check("range_comma_separator", Satisfies(">=1.0.0, <2.0.0", "1.1.0"), "comma is an AND separator");

        VersionRange range;
        string error;
        t.Check("range_reject_partial", !VersionRange.TryParse(">=1.6", out range, out error), error);
        t.Check("range_reject_empty", !VersionRange.TryParse("", out range, out error), error);
        t.Check("range_reject_dangling_operator", !VersionRange.TryParse(">=", out range, out error), error);
        t.Check("range_reject_or", !VersionRange.TryParse("^1.0.0 || ^2.0.0", out range, out error), "disjunction is not supported in this phase");
    }

    private static bool Satisfies(string rangeText, string version)
    {
        VersionRange range;
        SemVersion parsed;
        string error;
        if (!VersionRange.TryParse(rangeText, out range, out error))
        {
            throw new InvalidOperationException("bad range in test: " + rangeText + " (" + error + ")");
        }
        if (!SemVersion.TryParse(version, out parsed, out error))
        {
            throw new InvalidOperationException("bad version in test: " + version);
        }
        return range.IsSatisfiedBy(parsed);
    }

    // ------------------------------------------------------------------ JSON

    private static void JsonTests(TestRunner t)
    {
        var root = JsonParser.Parse("{\"a\":1,\"b\":[true,false,null,\"s\"],\"c\":{\"d\":-1.5e2}}");
        t.Check("json_object_shape", root.Kind == JsonKind.Object && root.Count == 3, "3 members");
        t.Check("json_member_order", root.Members[0].Key == "a" && root.Members[1].Key == "b", "document order preserved");
        t.Check("json_array_items", root.Get("b")!.Items.Count == 4 && root.Get("b")!.Items[0].BoolValue, "true,false,null,\"s\"");
        t.Check("json_number_raw", root.Get("c")!.Get("d")!.RawNumber == "-1.5e2", "-1.5e2");

        t.Check("json_escape_unicode", JsonParser.Parse("\"\\u0041\\u00e9\"").StringValue == "A\u00e9", "\\u escapes");
        t.Check("json_escape_newline", JsonParser.Parse("\"a\\nb\"").StringValue == "a\nb", "\\n");

        t.Check("json_reject_duplicate_key", Throws(() => JsonParser.Parse("{\"a\":1,\"a\":2}"), JsonError.DuplicateKey), "duplicate key");
        t.Check("json_reject_trailing_content", Throws(() => JsonParser.Parse("{} x"), JsonError.TrailingContent), "trailing content");
        t.Check("json_reject_trailing_comma", Throws(() => JsonParser.Parse("{\"a\":1,}"), JsonError.UnexpectedCharacter), "trailing comma");
        t.Check("json_reject_unterminated", Throws(() => JsonParser.Parse("\"abc"), JsonError.UnterminatedString), "unterminated string");
        t.Check("json_reject_bad_escape", Throws(() => JsonParser.Parse("\"\\q\""), JsonError.InvalidEscape), "invalid escape");
        t.Check("json_reject_bad_number", Throws(() => JsonParser.Parse("1."), JsonError.InvalidNumber), "1.");
        t.Check("json_reject_depth", Throws(() => JsonParser.Parse(new string('[', 20) + new string(']', 20), 8), JsonError.TooDeep), "depth limit");
        t.Check("json_reject_too_large", Throws(() => JsonParser.Parse(new string(' ', 200), 16, 100), JsonError.TooLarge), "size limit");
        t.Check("json_reject_empty", Throws(() => JsonParser.Parse("   "), JsonError.Empty), "empty document");
    }

    private static bool Throws(Func<object> action, JsonError expected)
    {
        try
        {
            action();
            return false;
        }
        catch (JsonException ex)
        {
            return ex.Error == expected;
        }
    }

    // ------------------------------------------------------- manifest fixtures

    private static void ManifestFixtureTests(TestRunner t, string repoRoot)
    {
        var fixtures = Path.Combine(repoRoot, "Tools", "tests", "fixtures", "manifests");
        var indexPath = Path.Combine(fixtures, "index.json");
        if (!File.Exists(indexPath))
        {
            t.Check("manifest_fixtures_present", false, "missing " + indexPath);
            return;
        }

        JsonValue index;
        try
        {
            index = JsonParser.Parse(File.ReadAllText(indexPath));
        }
        catch (JsonException ex)
        {
            t.Check("manifest_index_parses", false, ex.Message);
            return;
        }
        t.Check("manifest_index_parses", true, indexPath);

        var cases = index.Get("cases")!;
        var temp = Temp.NewDirectory("manifests");
        var ok = 0;
        for (var i = 0; i < cases.Items.Count; i++)
        {
            var item = cases.Items[i];
            var file = item.Get("file")!.StringValue;
            var expect = item.Get("expect")!.StringValue;
            var package = Path.Combine(temp, "case" + i);
            Directory.CreateDirectory(package);
            var source = Path.Combine(fixtures, file);
            File.Copy(source, Path.Combine(package, "manifest.json"), true);

            var result = ManifestReader.Load(package);
            var actual = result.Ok ? "ok" : result.Code;
            var pass = string.Equals(expect, actual, StringComparison.Ordinal);
            if (pass)
            {
                ok++;
            }
            t.Check("manifest_fixture_" + file.Replace(".json", "").Replace(".", "_").Replace("-", "_"), pass,
                "expect=" + expect + " actual=" + actual + (result.Ok ? "" : " (" + result.Message + ")"));
        }
        t.Check("manifest_fixture_count", cases.Items.Count >= 20, cases.Items.Count + " fixtures");
    }

    // -------------------------------------------------------------- discovery

    private static void DiscoveryTests(TestRunner t)
    {
        var root = Temp.NewDirectory("discovery");
        WritePackage(root, "ValidMod", "ExampleMod.Valid", "1.0.0");
        WritePackage(root, "NoManifest", null, null);
        WritePackage(root, ".hidden", "ExampleMod.Hidden", "1.0.0");
        WritePackage(root, "_scratch", "ExampleMod.Scratch", "1.0.0");
        WritePackage(root, "DupA", "ExampleMod.Dup", "1.0.0");
        WritePackage(root, "DupB", "ExampleMod.Dup", "1.0.0");
        WritePackage(root, "BadJson", null, null);
        File.WriteAllText(Path.Combine(root, "BadJson", "manifest.json"), "{ not json");

        var result = ModDiscovery.Discover(root);
        t.Check("discovery_counts", result.Records.Count == 4, result.Records.Count + " candidate packages (hidden/underscore/no-manifest ignored)");

        var valid = FindRecord(result, "ValidMod");
        t.Check("discovery_valid_manifest", valid != null && valid.State == ModState.ManifestValidated, valid == null ? "missing" : valid.State.ToString());

        var bad = FindRecord(result, "BadJson");
        t.Check("discovery_bad_manifest_isolated", bad != null && bad.State == ModState.Failed && bad.ErrorCode == ErrorCodes.ManifestInvalidJson,
            bad == null ? "missing" : bad.ErrorCode);

        var dupA = FindRecord(result, "DupA");
        var dupB = FindRecord(result, "DupB");
        t.Check("discovery_duplicate_id_rejects_both",
            dupA != null && dupB != null && dupA.ErrorCode == ErrorCodes.DuplicateModId && dupB.ErrorCode == ErrorCodes.DuplicateModId,
            (dupA == null ? "?" : dupA.ErrorCode) + "/" + (dupB == null ? "?" : dupB.ErrorCode));

        t.Check("discovery_ignores_hidden", FindRecord(result, ".hidden") == null && FindRecord(result, "_scratch") == null, "names starting with . or _");

        // Determinism: create the same packages in a different order and compare the id list.
        var root2 = Temp.NewDirectory("discovery2");
        WritePackage(root2, "DupB", "ExampleMod.Dup", "1.0.0");
        WritePackage(root2, "BadJson", null, null);
        File.WriteAllText(Path.Combine(root2, "BadJson", "manifest.json"), "{ not json");
        WritePackage(root2, "DupA", "ExampleMod.Dup", "1.0.0");
        WritePackage(root2, "ValidMod", "ExampleMod.Valid", "1.0.0");
        WritePackage(root2, "NoManifest", null, null);
        WritePackage(root2, ".hidden", "ExampleMod.Hidden", "1.0.0");
        WritePackage(root2, "_scratch", "ExampleMod.Scratch", "1.0.0");
        var result2 = ModDiscovery.Discover(root2);
        t.Check("discovery_deterministic", SameRecordOrder(result, result2), DescribeRecords(result));
    }

    private static bool SameRecordOrder(ModDiscovery.Result a, ModDiscovery.Result b)
    {
        if (a.Records.Count != b.Records.Count)
        {
            return false;
        }
        for (var i = 0; i < a.Records.Count; i++)
        {
            if (!string.Equals(a.Records[i].DirectoryName, b.Records[i].DirectoryName, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static string DescribeRecords(ModDiscovery.Result result)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < result.Records.Count; i++)
        {
            if (i > 0) { sb.Append(','); }
            sb.Append(result.Records[i].DirectoryName);
        }
        return sb.ToString();
    }

    private static ModRecord FindRecord(ModDiscovery.Result result, string directoryName)
    {
        for (var i = 0; i < result.Records.Count; i++)
        {
            if (string.Equals(result.Records[i].DirectoryName, directoryName, StringComparison.Ordinal))
            {
                return result.Records[i];
            }
        }
        return null;
    }

    // ------------------------------------------------------------ dependencies

    private static void DependencyTests(TestRunner t)
    {
        // chain: C -> B -> A
        var chain = new List<ModManifest>
        {
            Manifest("ExampleMod.C", "1.0.0", "ExampleMod.B"),
            Manifest("ExampleMod.B", "1.0.0", "ExampleMod.A"),
            Manifest("ExampleMod.A", "1.0.0"),
        };
        var plan = DependencyResolver.Resolve(chain);
        t.Check("dep_chain_order", Ids(plan) == "ExampleMod.A,ExampleMod.B,ExampleMod.C", Ids(plan));
        t.Check("dep_chain_no_problems", plan.Problems.Count == 0, plan.Problems.Count + " problems");

        // missing required dependency
        var missing = DependencyResolver.Resolve(new List<ModManifest> { Manifest("ExampleMod.NeedsMissing", "1.0.0", "ExampleMod.Absent") });
        t.Check("dep_missing_required", ProblemCode(missing, "ExampleMod.NeedsMissing") == ErrorCodes.DependencyMissing,
            ProblemCode(missing, "ExampleMod.NeedsMissing"));

        // version mismatch
        var mismatch = DependencyResolver.Resolve(new List<ModManifest>
        {
            Manifest("ExampleMod.Core", "1.0.0"),
            ManifestWith("ExampleMod.Wants2", "1.0.0", new[] { Dep("ExampleMod.Core", ">=2.0.0") }),
        });
        t.Check("dep_version_mismatch", ProblemCode(mismatch, "ExampleMod.Wants2") == ErrorCodes.DependencyVersionMismatch,
            ProblemCode(mismatch, "ExampleMod.Wants2"));

        // optional dependency missing: allowed, recorded
        var optional = DependencyResolver.Resolve(new List<ModManifest>
        {
            ManifestWith("ExampleMod.OptionalHost", "1.0.0", new[] { Dep("ExampleMod.Absent", "*", true) }),
        });
        List<string> absent;
        t.Check("dep_optional_missing_allowed",
            optional.Problems.Count == 0 && optional.OptionalAbsent.TryGetValue("ExampleMod.OptionalHost", out absent) && absent.Count == 1,
            optional.Problems.Count + " problems");

        // optional dependency present: participates in ordering
        var optionalPresent = DependencyResolver.Resolve(new List<ModManifest>
        {
            ManifestWith("ExampleMod.Host2", "1.0.0", new[] { Dep("ExampleMod.Guest", "*", true) }),
            Manifest("ExampleMod.Guest", "1.0.0"),
        });
        t.Check("dep_optional_present_ordered", Ids(optionalPresent) == "ExampleMod.Guest,ExampleMod.Host2", Ids(optionalPresent));

        // cycle
        var cycle = DependencyResolver.Resolve(new List<ModManifest>
        {
            Manifest("ExampleMod.X", "1.0.0", "ExampleMod.Y"),
            Manifest("ExampleMod.Y", "1.0.0", "ExampleMod.X"),
            Manifest("ExampleMod.Free", "1.0.0"),
        });
        t.Check("dep_cycle_detected",
            ProblemCode(cycle, "ExampleMod.X") == ErrorCodes.DependencyCycle &&
            ProblemCode(cycle, "ExampleMod.Y") == ErrorCodes.DependencyCycle,
            ProblemCode(cycle, "ExampleMod.X") + "/" + ProblemCode(cycle, "ExampleMod.Y"));
        t.Check("dep_cycle_reports_path", cycle.Cycles.Count > 0 && cycle.Cycles[0].IndexOf("ExampleMod.X", StringComparison.Ordinal) >= 0,
            cycle.Cycles.Count > 0 ? cycle.Cycles[0] : "no cycle recorded");
        t.Check("dep_cycle_does_not_block_unrelated", ProblemCode(cycle, "ExampleMod.Free") == "", "independent mod unaffected");

        // diamond: D -> (B, C) -> A
        var diamond = DependencyResolver.Resolve(new List<ModManifest>
        {
            ManifestWith("ExampleMod.D", "1.0.0", new[] { Dep("ExampleMod.B"), Dep("ExampleMod.C") }),
            Manifest("ExampleMod.C", "1.0.0", "ExampleMod.A"),
            Manifest("ExampleMod.B", "1.0.0", "ExampleMod.A"),
            Manifest("ExampleMod.A", "1.0.0"),
        });
        t.Check("dep_diamond_order", Ids(diamond) == "ExampleMod.A,ExampleMod.B,ExampleMod.C,ExampleMod.D", Ids(diamond));

        // determinism: the same set in a different declaration order yields the same order
        var shuffled = new List<ModManifest>
        {
            Manifest("ExampleMod.B", "1.0.0", "ExampleMod.A"),
            ManifestWith("ExampleMod.D", "1.0.0", new[] { Dep("ExampleMod.C"), Dep("ExampleMod.B") }),
            Manifest("ExampleMod.A", "1.0.0"),
            Manifest("ExampleMod.C", "1.0.0", "ExampleMod.A"),
        };
        t.Check("dep_deterministic", Ids(DependencyResolver.Resolve(shuffled)) == Ids(diamond), Ids(DependencyResolver.Resolve(shuffled)));
    }

    private static string ProblemCode(DependencyPlan plan, string id)
    {
        DependencyProblem problem;
        return plan.Problems.TryGetValue(id, out problem) && problem != null ? problem.Code : "";
    }

    private static string Ids(DependencyPlan plan)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < plan.Order.Count; i++)
        {
            if (i > 0) { sb.Append(','); }
            sb.Append(plan.Order[i].Id);
        }
        return sb.ToString();
    }

    private static ModDependency Dep(string id, string range = "*", bool optional = false)
    {
        VersionRange parsed;
        string error;
        if (!VersionRange.TryParse(range, out parsed, out error))
        {
            throw new InvalidOperationException("bad range in test: " + range);
        }
        return new ModDependency(id, range, parsed, optional);
    }

    private static ModManifest Manifest(string id, string version, params string[] dependencies)
    {
        var deps = new List<ModDependency>();
        for (var i = 0; i < dependencies.Length; i++)
        {
            deps.Add(Dep(dependencies[i]));
        }
        return ManifestWith(id, version, deps.ToArray());
    }

    private static ModManifest ManifestWith(string id, string version, ModDependency[] dependencies)
    {
        SemVersion parsed;
        string error;
        if (!SemVersion.TryParse(version, out parsed, out error))
        {
            throw new InvalidOperationException("bad version in test: " + version);
        }
        var manifest = new ModManifest
        {
            Id = id,
            Name = id,
            VersionText = version,
            Version = parsed,
            EntryAssembly = id + ".dll",
            EntryType = id + ".Entry",
            Directory = "/tmp/" + id,
        };
        for (var i = 0; i < dependencies.Length; i++)
        {
            manifest.Dependencies.Add(dependencies[i]);
        }
        return manifest;
    }

    // ---------------------------------------------------------------- helpers

    internal static void WritePackage(string root, string directoryName, string modId, string version)
    {
        var package = Path.Combine(root, directoryName);
        Directory.CreateDirectory(package);
        if (modId == null)
        {
            return;
        }
        var json = "{\n" +
                   "  \"schemaVersion\": 1,\n" +
                   "  \"id\": \"" + modId + "\",\n" +
                   "  \"name\": \"" + modId + "\",\n" +
                   "  \"version\": \"" + version + "\",\n" +
                   "  \"entryAssembly\": \"" + modId + ".dll\",\n" +
                   "  \"entryType\": \"" + modId + ".Entry\"\n" +
                   "}\n";
        File.WriteAllText(Path.Combine(package, "manifest.json"), json, new UTF8Encoding(false));
    }
}
