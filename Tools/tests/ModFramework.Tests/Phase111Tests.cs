using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using ModFramework.Runtime;

namespace ModFramework.Tests;

/// <summary>
/// Phase 1.1.1 regression tests: assembly preflight correctness and security hardening.
///
/// Every test here is written against the phase-1.1 implementation first and is expected to
/// FAIL on it (see <c>docs/audit/phase1.1.1-report.md</c> §"Red 基线"). The failures are
/// behavioural: a mod that must be rejected initializes, a malformed image that must be refused
/// is accepted, and a report that must be path-free contains absolute paths.
///
/// Nothing here loads a malformed image: <c>SyntheticPe</c> bytes only ever reach the metadata
/// reader.
/// </summary>
internal static class Phase111Tests
{
    internal static void Run(TestRunner t, string fixtureDir)
    {
        var fixtureDll = Path.Combine(fixtureDir, "FixtureMod.dll");

        DependencyPrePropagation(t, fixtureDir, fixtureDll);
        ContentIdentity(t, fixtureDir, fixtureDll);
        HostIdentity(t, fixtureDir, fixtureDll);
        ParserBoundaries(t, fixtureDir);
        FrameworkVersionState(t, fixtureDir, fixtureDll);
        PathRedaction(t, fixtureDir, fixtureDll);
    }

    // =========================================================== A. dependencies

    /// <summary>
    /// A.1: mod A depends on B; B fails its explicit <c>gameVersion</c> gate; A's declared
    /// assembly collides with healthy mod C's. A is doomed either way, so it must never reach
    /// the assembly-conflict map - C has to initialize.
    /// </summary>
    private static void DependencyPrePropagation(TestRunner t, string fixtureDir, string fixtureDll)
    {
        var a1v1 = Path.Combine(fixtureDir, "PhaseA1Lib.v1.dll");
        var a1v2 = Path.Combine(fixtureDir, "PhaseA1Lib.v2.dll");
        var a2v1 = Path.Combine(fixtureDir, "PhaseA2Lib.v1.dll");
        var a2v2 = Path.Combine(fixtureDir, "PhaseA2Lib.v2.dll");
        var a2bv1 = Path.Combine(fixtureDir, "PhaseA2Lib2.v1.dll");
        var a2bv2 = Path.Combine(fixtureDir, "PhaseA2Lib2.v2.dll");
        var a3v1 = Path.Combine(fixtureDir, "PhaseA3Lib.v1.dll");
        var a3v2 = Path.Combine(fixtureDir, "PhaseA3Lib.v2.dll");
        foreach (var required in new[] { a1v1, a1v2, a2v1, a2v2, a2bv1, a2bv2, a3v1, a3v2 })
        {
            if (!File.Exists(required))
            {
                t.Check("p111_a1_fixtures_present", false, "missing " + Path.GetFileName(required));
                return;
            }
        }

        var previousGameVersion = Loader.GameVersionOverride;
        try
        {
            // A known game version makes "unsupported range" the deterministic failure of B.
            Loader.GameVersionOverride = "1.6.15";

            A1(t, fixtureDir, fixtureDll, a1v1, a1v2);
            A2(t, fixtureDir, fixtureDll, a2v1, a2v2, a2bv1, a2bv2);
            A3(t, fixtureDir, fixtureDll, a3v1, a3v2);
            A4(t, fixtureDir, fixtureDll);
            A5(t, fixtureDir, fixtureDll, a1v1, a1v2);
        }
        finally
        {
            Loader.GameVersionOverride = previousGameVersion;
        }
    }

    private static void A1(TestRunner t, string fixtureDir, string fixtureDll, string v1, string v2)
    {
        var root = Temp.NewDirectory("p111-a1");
        Package(root, "A1.B", "P111.A1.B", "FixtureMod.GoodEntry", new[] { fixtureDll }, "FixtureMod.dll",
            "\"gameVersion\": \">=9.0.0\"");
        Package(root, "A1.A", "P111.A1.A", "FixtureMod.GoodEntry", new[] { fixtureDll, v1 }, "FixtureMod.dll",
            "\"privateAssemblies\": [ \"PhaseA1Lib.v1.dll\" ], \"dependencies\": [ { \"id\": \"P111.A1.B\", \"version\": \"*\" } ]");
        Package(root, "A1.C", "P111.A1.C", "FixtureMod.GoodEntry", new[] { fixtureDll, v2 }, "FixtureMod.dll",
            "\"privateAssemblies\": [ \"PhaseA1Lib.v2.dll\" ]");

        var returned = Loader.Run(root);
        var report = Report(root);

        t.Check("p111_a1_run_completed", returned == null, returned ?? "(null)");
        t.Check("p111_a1_failed_dependency_root_cause",
            report.Contains("id=P111.A1.B state=Failed code=MF0501"), Line(report, "id=P111.A1.B "));
        t.Check("p111_a1_downstream_is_dependency_failed",
            report.Contains("id=P111.A1.A state=Failed code=MF0404"), Line(report, "id=P111.A1.A "));
        t.Check("p111_a1_unrelated_conflict_mod_initializes",
            report.Contains("id=P111.A1.C state=Initialized"), Line(report, "id=P111.A1.C "));
        t.Check("p111_a1_doomed_mod_absent_from_conflict_map",
            !report.Contains("preflight-conflict=PhaseA1Lib"), ConflictLines(report));
        t.Check("p111_a1_healthy_mod_entry_ran",
            File.Exists(Path.Combine(root, "A1.C", "entry-ran.txt")), "A1.C entry-ran.txt");
        t.Check("p111_a1_doomed_mod_entry_never_ran",
            !File.Exists(Path.Combine(root, "A1.A", "entry-ran.txt")), "A1.A entry-ran.txt");
    }

    private static void A2(TestRunner t, string fixtureDir, string fixtureDll, string v1, string v2, string v1B, string v2B)
    {
        var root = Temp.NewDirectory("p111-a2");
        // B and C are the direct conflict owners.
        Package(root, "A2.B", "P111.A2.B", "FixtureMod.GoodEntry", new[] { fixtureDll, v1 }, "FixtureMod.dll",
            "\"privateAssemblies\": [ \"PhaseA2Lib.v1.dll\" ]");
        Package(root, "A2.C", "P111.A2.C", "FixtureMod.GoodEntry", new[] { fixtureDll, v2 }, "FixtureMod.dll",
            "\"privateAssemblies\": [ \"PhaseA2Lib.v2.dll\" ]");
        // A depends on B and additionally collides with healthy D on a second assembly.
        Package(root, "A2.A", "P111.A2.A", "FixtureMod.GoodEntry", new[] { fixtureDll, v1B }, "FixtureMod.dll",
            "\"privateAssemblies\": [ \"PhaseA2Lib2.v1.dll\" ], \"dependencies\": [ { \"id\": \"P111.A2.B\", \"version\": \"*\" } ]");
        Package(root, "A2.D", "P111.A2.D", "FixtureMod.GoodEntry", new[] { fixtureDll, v2B }, "FixtureMod.dll",
            "\"privateAssemblies\": [ \"PhaseA2Lib2.v2.dll\" ]");

        var returned = Loader.Run(root);
        var report = Report(root);

        t.Check("p111_a2_run_completed", returned == null, returned ?? "(null)");
        t.Check("p111_a2_both_direct_conflict_owners_rejected",
            report.Contains("id=P111.A2.B state=Failed code=MF0206") &&
            report.Contains("id=P111.A2.C state=Failed code=MF0206"),
            Line(report, "id=P111.A2.B ") + " / " + Line(report, "id=P111.A2.C "));
        t.Check("p111_a2_downstream_classified_as_dependency_failure",
            report.Contains("id=P111.A2.A state=Failed code=MF0404"), Line(report, "id=P111.A2.A "));
        t.Check("p111_a2_unrelated_mod_survives_after_repreflight",
            report.Contains("id=P111.A2.D state=Initialized"), Line(report, "id=P111.A2.D "));
        t.Check("p111_a2_removed_downstream_no_longer_forms_a_conflict",
            !report.Contains("preflight-conflict=PhaseA2Lib2"), ConflictLines(report));
        t.Check("p111_a2_direct_conflict_still_reported",
            report.Contains("preflight-conflict=PhaseA2Lib kind=mod"), ConflictLines(report));
    }

    private static void A3(TestRunner t, string fixtureDir, string fixtureDll, string v1, string v2)
    {
        var root = Temp.NewDirectory("p111-a3");
        Package(root, "A3.C", "P111.A3.C", "FixtureMod.GoodEntry", new[] { fixtureDll, v1 }, "FixtureMod.dll",
            "\"privateAssemblies\": [ \"PhaseA3Lib.v1.dll\" ]");
        Package(root, "A3.E", "P111.A3.E", "FixtureMod.GoodEntry", new[] { fixtureDll, v2 }, "FixtureMod.dll",
            "\"privateAssemblies\": [ \"PhaseA3Lib.v2.dll\" ]");
        Package(root, "A3.B", "P111.A3.B", "FixtureMod.DependentEntry", new[] { fixtureDll }, "FixtureMod.dll",
            "\"dependencies\": [ { \"id\": \"P111.A3.C\", \"version\": \"*\" } ]");
        Package(root, "A3.A", "P111.A3.A", "FixtureMod.DependentEntry", new[] { fixtureDll }, "FixtureMod.dll",
            "\"dependencies\": [ { \"id\": \"P111.A3.B\", \"version\": \"*\" } ]");
        Package(root, "A3.U", "P111.A3.U", "FixtureMod.GoodEntry", new[] { fixtureDll }, "FixtureMod.dll");

        var returned = Loader.Run(root);
        var report = Report(root);

        t.Check("p111_a3_run_completed", returned == null, returned ?? "(null)");
        t.Check("p111_a3_conflict_root_rejected",
            report.Contains("id=P111.A3.C state=Failed code=MF0206") &&
            report.Contains("id=P111.A3.E state=Failed code=MF0206"),
            Line(report, "id=P111.A3.C ") + " / " + Line(report, "id=P111.A3.E "));
        t.Check("p111_a3_multi_level_chain_blocked",
            report.Contains("id=P111.A3.B state=Failed code=MF0404") &&
            report.Contains("id=P111.A3.A state=Failed code=MF0404"),
            Line(report, "id=P111.A3.B ") + " / " + Line(report, "id=P111.A3.A "));
        t.Check("p111_a3_unrelated_mod_initializes",
            report.Contains("id=P111.A3.U state=Initialized"), Line(report, "id=P111.A3.U "));
    }

    private static void A4(TestRunner t, string fixtureDir, string fixtureDll)
    {
        var root = Temp.NewDirectory("p111-a4");
        Package(root, "A4.B", "P111.A4.B", "FixtureMod.GoodEntry", new[] { fixtureDll }, "FixtureMod.dll",
            "\"gameVersion\": \">=9.0.0\"");
        Package(root, "A4.A", "P111.A4.A", "FixtureMod.GoodEntry", new[] { fixtureDll }, "FixtureMod.dll",
            "\"dependencies\": [ { \"id\": \"P111.A4.B\", \"version\": \"*\", \"optional\": true } ]");

        var returned = Loader.Run(root);
        var report = Report(root);

        t.Check("p111_a4_run_completed", returned == null, returned ?? "(null)");
        t.Check("p111_a4_optional_dependency_still_fails",
            report.Contains("id=P111.A4.B state=Failed code=MF0501"), Line(report, "id=P111.A4.B "));
        t.Check("p111_a4_optional_dependency_does_not_block",
            report.Contains("id=P111.A4.A state=Initialized"), Line(report, "id=P111.A4.A "));
    }

    private static void A5(TestRunner t, string fixtureDir, string fixtureDll, string v1, string v2)
    {
        // Same mod set, opposite creation order: the classification must be byte-identical.
        var forward = Temp.NewDirectory("p111-a5-fwd");
        BuildA1(forward, fixtureDll, v1, v2, false);
        var reverse = Temp.NewDirectory("p111-a5-rev");
        BuildA1(reverse, fixtureDll, v1, v2, true);

        Loader.Run(forward);
        Loader.Run(reverse);

        var forwardSignature = Signature(Report(forward));
        var reverseSignature = Signature(Report(reverse));
        t.Check("p111_a5_classification_independent_of_discovery_order",
            forwardSignature.Length > 0 &&
            string.Equals(forwardSignature, reverseSignature, StringComparison.Ordinal),
            "forward=" + forwardSignature + " reverse=" + reverseSignature);
    }

    private static void BuildA1(string root, string fixtureDll, string v1, string v2, bool reversed)
    {
        if (!reversed)
        {
            Package(root, "A5.B", "P111.A5.B", "FixtureMod.GoodEntry", new[] { fixtureDll }, "FixtureMod.dll",
                "\"gameVersion\": \">=9.0.0\"");
            Package(root, "A5.A", "P111.A5.A", "FixtureMod.GoodEntry", new[] { fixtureDll, v1 }, "FixtureMod.dll",
                "\"privateAssemblies\": [ \"PhaseA1Lib.v1.dll\" ], \"dependencies\": [ { \"id\": \"P111.A5.B\", \"version\": \"*\" } ]");
            Package(root, "A5.C", "P111.A5.C", "FixtureMod.GoodEntry", new[] { fixtureDll, v2 }, "FixtureMod.dll",
                "\"privateAssemblies\": [ \"PhaseA1Lib.v2.dll\" ]");
            return;
        }
        Package(root, "A5.C", "P111.A5.C", "FixtureMod.GoodEntry", new[] { fixtureDll, v2 }, "FixtureMod.dll",
            "\"privateAssemblies\": [ \"PhaseA1Lib.v2.dll\" ]");
        Package(root, "A5.A", "P111.A5.A", "FixtureMod.GoodEntry", new[] { fixtureDll, v1 }, "FixtureMod.dll",
            "\"privateAssemblies\": [ \"PhaseA1Lib.v1.dll\" ], \"dependencies\": [ { \"id\": \"P111.A5.B\", \"version\": \"*\" } ]");
        Package(root, "A5.B", "P111.A5.B", "FixtureMod.GoodEntry", new[] { fixtureDll }, "FixtureMod.dll",
            "\"gameVersion\": \">=9.0.0\"");
    }

    // ============================================================ B. content identity

    /// <summary>
    /// B: content equality must never rest on a non-cryptographic digest. A digest may only be
    /// used as a fast <i>negative</i> filter; equality always needs a full byte comparison.
    /// </summary>
    private static void ContentIdentity(TestRunner t, string fixtureDir, string fixtureDll)
    {
        var bytes = File.ReadAllBytes(fixtureDll);

        var production = AssemblyClaimReader.ComputeDigest(bytes);
        t.Check("p111_b_digest_is_cryptographic_in_production",
            production.StartsWith("sha256:", StringComparison.Ordinal), production);

        var previousDisable = AssemblyClaimReader.DisableSha256ForTests;
        try
        {
            AssemblyClaimReader.DisableSha256ForTests = true;
            var withoutSha = AssemblyClaimReader.ComputeDigest(bytes);
            t.Check("p111_b_no_weak_digest_when_sha256_unavailable",
                !withoutSha.StartsWith("fnv1a64", StringComparison.Ordinal) &&
                !withoutSha.StartsWith("crc", StringComparison.OrdinalIgnoreCase),
                withoutSha);
        }
        finally
        {
            AssemblyClaimReader.DisableSha256ForTests = previousDisable;
        }

        var x = Path.Combine(fixtureDir, "DiffContentLib.x.dll");
        var y = Path.Combine(fixtureDir, "DiffContentLib.y.dll");
        var shared = Path.Combine(fixtureDir, "SharedLib.dll");
        if (!File.Exists(x) || !File.Exists(y) || !File.Exists(shared))
        {
            t.Check("p111_b_fixtures_present", false, "missing DiffContentLib/SharedLib fixtures");
            return;
        }

        // An injected digest collision: different bytes, identical digest. A digest can never be
        // the reason two different files are considered equal.
        var previousOverride = AssemblyClaimReader.DigestOverrideForTests;
        try
        {
            AssemblyClaimReader.DigestOverrideForTests = _ => "sha256:0000000000000000000000000000000000000000000000000000000000000000";

            var collisionRoot = Temp.NewDirectory("p111-b-collision");
            Package(collisionRoot, "B.CollideX", "P111.B.CollideX", "FixtureMod.GoodEntry",
                new[] { fixtureDll, x }, "FixtureMod.dll", "\"privateAssemblies\": [ \"DiffContentLib.x.dll\" ]");
            Package(collisionRoot, "B.CollideY", "P111.B.CollideY", "FixtureMod.GoodEntry",
                new[] { fixtureDll, y }, "FixtureMod.dll", "\"privateAssemblies\": [ \"DiffContentLib.y.dll\" ]");
            var returned = Loader.Run(collisionRoot);
            var report = Report(collisionRoot);
            t.Check("p111_b_digest_collision_does_not_permit_sharing",
                returned == null &&
                report.Contains("id=P111.B.CollideX state=Failed code=MF0206") &&
                report.Contains("id=P111.B.CollideY state=Failed code=MF0206"),
                Line(report, "id=P111.B.CollideX ") + " / " + Line(report, "id=P111.B.CollideY "));

            // The same colliding digest must still allow genuinely identical bytes to be shared.
            var identicalRoot = Temp.NewDirectory("p111-b-identical");
            Package(identicalRoot, "B.SameA", "P111.B.SameA", "FixtureMod.GoodEntry",
                new[] { fixtureDll, shared }, "FixtureMod.dll", "\"privateAssemblies\": [ \"SharedLib.dll\" ]");
            Package(identicalRoot, "B.SameB", "P111.B.SameB", "FixtureMod.GoodEntry",
                new[] { fixtureDll, shared }, "FixtureMod.dll", "\"privateAssemblies\": [ \"SharedLib.dll\" ]");
            var identicalReturned = Loader.Run(identicalRoot);
            var identicalReport = Report(identicalRoot);
            t.Check("p111_b_identical_bytes_share_even_with_weak_digest",
                identicalReturned == null &&
                identicalReport.Contains("id=P111.B.SameA state=Initialized") &&
                identicalReport.Contains("id=P111.B.SameB state=Initialized"),
                Line(identicalReport, "id=P111.B.SameA ") + " / " + Line(identicalReport, "id=P111.B.SameB "));
        }
        finally
        {
            AssemblyClaimReader.DigestOverrideForTests = previousOverride;
        }

        // SHA-256 unavailable: correctness still holds, decided by bytes.
        var previousDisable2 = AssemblyClaimReader.DisableSha256ForTests;
        try
        {
            AssemblyClaimReader.DisableSha256ForTests = true;

            var sameRoot = Temp.NewDirectory("p111-b-nosha-same");
            Package(sameRoot, "B.NoShaA", "P111.B.NoShaA", "FixtureMod.GoodEntry",
                new[] { fixtureDll, shared }, "FixtureMod.dll", "\"privateAssemblies\": [ \"SharedLib.dll\" ]");
            Package(sameRoot, "B.NoShaB", "P111.B.NoShaB", "FixtureMod.GoodEntry",
                new[] { fixtureDll, shared }, "FixtureMod.dll", "\"privateAssemblies\": [ \"SharedLib.dll\" ]");
            Loader.Run(sameRoot);
            var sameReport = Report(sameRoot);
            t.Check("p111_b_no_sha256_identical_content_still_shares",
                sameReport.Contains("id=P111.B.NoShaA state=Initialized") &&
                sameReport.Contains("id=P111.B.NoShaB state=Initialized"),
                Line(sameReport, "id=P111.B.NoShaA ") + " / " + Line(sameReport, "id=P111.B.NoShaB "));

            var diffRoot = Temp.NewDirectory("p111-b-nosha-diff");
            Package(diffRoot, "B.NoShaX", "P111.B.NoShaX", "FixtureMod.GoodEntry",
                new[] { fixtureDll, x }, "FixtureMod.dll", "\"privateAssemblies\": [ \"DiffContentLib.x.dll\" ]");
            Package(diffRoot, "B.NoShaY", "P111.B.NoShaY", "FixtureMod.GoodEntry",
                new[] { fixtureDll, y }, "FixtureMod.dll", "\"privateAssemblies\": [ \"DiffContentLib.y.dll\" ]");
            Loader.Run(diffRoot);
            var diffReport = Report(diffRoot);
            // Either a mod-vs-mod conflict (MF0206) or a conflict with a copy a previous
            // scenario left in the AppDomain (MF0207) is a correct "different content" refusal;
            // both being initialized would be the defect.
            t.Check("p111_b_no_sha256_different_content_rejected",
                FailedWith(diffReport, "P111.B.NoShaX", "MF0206", "MF0207") &&
                FailedWith(diffReport, "P111.B.NoShaY", "MF0206", "MF0207") &&
                !File.Exists(Path.Combine(diffRoot, "B.NoShaX", "entry-ran.txt")),
                Line(diffReport, "id=P111.B.NoShaX ") + " / " + Line(diffReport, "id=P111.B.NoShaY "));
        }
        finally
        {
            AssemblyClaimReader.DisableSha256ForTests = previousDisable2;
        }
    }

    // ============================================================== C. host identity

    /// <summary>
    /// C: a host assembly whose content cannot be proven identical must not be reused. The
    /// phase-1.1 code reused it and only wrote a warning.
    /// </summary>
    private static void HostIdentity(TestRunner t, string fixtureDir, string fixtureDll)
    {
        var shared = Path.Combine(fixtureDir, "SharedLib.dll");
        if (!File.Exists(shared))
        {
            t.Check("p111_c_fixtures_present", false, "missing SharedLib.dll");
            return;
        }

        var previousSnapshot = AssemblyPreflight.HostSnapshotForTests;
        try
        {
            // (1) identity matches, but the host image has no readable file -> fail closed.
            AssemblyPreflight.HostSnapshotForTests = new List<HostAssemblyIdentity>
            {
                new HostAssemblyIdentity
                {
                    SimpleName = "SharedLib",
                    VersionText = "1.0.0.0",
                    Culture = "",
                    Location = "",
                },
            };
            var unverifiableRoot = Temp.NewDirectory("p111-c-unverifiable");
            Package(unverifiableRoot, "C.Unverifiable", "P111.C.Unverifiable", "FixtureMod.GoodEntry",
                new[] { fixtureDll, shared }, "FixtureMod.dll", "\"privateAssemblies\": [ \"SharedLib.dll\" ]");
            var returned = Loader.Run(unverifiableRoot);
            var report = Report(unverifiableRoot);
            t.Check("p111_c_unverifiable_host_content_rejected",
                returned == null && report.Contains("id=P111.C.Unverifiable state=Failed code=MF0207"),
                Line(report, "id=P111.C.Unverifiable "));
            t.Check("p111_c_unverifiable_host_not_silently_shared",
                !report.Contains("preflight-host-share=SharedLib"), HostLines(report));
            t.Check("p111_c_unverifiable_host_reported_as_rejected",
                report.Contains("host-unverifiable-rejected"), HostLines(report));

            // (2) two host candidates with the same identity -> ambiguous, must not pick one.
            AssemblyPreflight.HostSnapshotForTests = new List<HostAssemblyIdentity>
            {
                new HostAssemblyIdentity
                {
                    SimpleName = "SharedLib", VersionText = "1.0.0.0", Culture = "", Location = "",
                },
                new HostAssemblyIdentity
                {
                    SimpleName = "SharedLib", VersionText = "1.0.0.0", Culture = "", Location = shared,
                },
            };
            var ambiguousRoot = Temp.NewDirectory("p111-c-ambiguous");
            Package(ambiguousRoot, "C.Ambiguous", "P111.C.Ambiguous", "FixtureMod.GoodEntry",
                new[] { fixtureDll, shared }, "FixtureMod.dll", "\"privateAssemblies\": [ \"SharedLib.dll\" ]");
            Loader.Run(ambiguousRoot);
            var ambiguousReport = Report(ambiguousRoot);
            t.Check("p111_c_ambiguous_host_candidate_rejected",
                ambiguousReport.Contains("id=P111.C.Ambiguous state=Failed code=MF0207"),
                Line(ambiguousReport, "id=P111.C.Ambiguous "));

            // (3) same name/version, different culture -> different identity.
            AssemblyPreflight.HostSnapshotForTests = new List<HostAssemblyIdentity>
            {
                new HostAssemblyIdentity
                {
                    SimpleName = "SharedLib", VersionText = "1.0.0.0", Culture = "fr", Location = shared,
                },
            };
            var cultureRoot = Temp.NewDirectory("p111-c-culture");
            Package(cultureRoot, "C.Culture", "P111.C.Culture", "FixtureMod.GoodEntry",
                new[] { fixtureDll, shared }, "FixtureMod.dll", "\"privateAssemblies\": [ \"SharedLib.dll\" ]");
            Loader.Run(cultureRoot);
            var cultureReport = Report(cultureRoot);
            t.Check("p111_c_host_culture_mismatch_is_a_conflict",
                cultureReport.Contains("id=P111.C.Culture state=Failed code=MF0207"),
                Line(cultureReport, "id=P111.C.Culture "));

            // (4) strong-name identity differs -> not the same identity.
            AssemblyPreflight.HostSnapshotForTests = new List<HostAssemblyIdentity>
            {
                new HostAssemblyIdentity
                {
                    SimpleName = "SharedLib", VersionText = "1.0.0.0", Culture = "",
                    Location = shared, PublicKeyToken = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
                },
            };
            var tokenRoot = Temp.NewDirectory("p111-c-token");
            Package(tokenRoot, "C.Token", "P111.C.Token", "FixtureMod.GoodEntry",
                new[] { fixtureDll, shared }, "FixtureMod.dll", "\"privateAssemblies\": [ \"SharedLib.dll\" ]");
            Loader.Run(tokenRoot);
            var tokenReport = Report(tokenRoot);
            t.Check("p111_c_host_public_key_mismatch_is_a_conflict",
                tokenReport.Contains("id=P111.C.Token state=Failed code=MF0207"),
                Line(tokenReport, "id=P111.C.Token "));
        }
        finally
        {
            AssemblyPreflight.HostSnapshotForTests = previousSnapshot;
        }

        // (5) the real snapshot must expose enough identity to compare keys and cultures.
        var snapshot = HostAssemblyIdentity.Snapshot();
        var exact = false;
        var cultureAware = false;
        var keyBytes = false;
        for (var i = 0; i < snapshot.Count; i++)
        {
            if (snapshot[i].PublicKey != null && snapshot[i].PublicKey.Length > 0)
            {
                keyBytes = true;
            }
            if (snapshot[i].PublicKeyToken != null && snapshot[i].PublicKeyToken.Length > 0)
            {
                keyBytes = true;
            }
            if (snapshot[i].Culture != null)
            {
                cultureAware = true;
            }
            if (snapshot[i].Assembly != null)
            {
                exact = true;
            }
        }
        t.Check("p111_c_snapshot_keeps_the_assembly_instance", exact, "Assembly reference on at least one row");
        t.Check("p111_c_snapshot_keeps_culture", cultureAware, "Culture on at least one row");
        t.Check("p111_c_snapshot_keeps_public_key_bytes", keyBytes,
            "raw public key or token bytes on at least one snapshot row");
    }

    // ================================================================ D. PE/CLI bounds

    private static void ParserBoundaries(TestRunner t, string fixtureDir)
    {
        // ---- positive baseline: the synthetic image is a valid managed assembly ----------
        var valid = SyntheticPe.Create();
        AssemblyFileIdentity identity;
        string error;
        var ok = ManagedAssemblyReader.TryReadIdentity(valid.Image, out identity, out error);
        t.Check("p111_d_synthetic_image_is_readable", ok, ok ? identity.Describe() : error);
        if (ok)
        {
            t.Equal("p111_d_synthetic_name", "TestAsm", identity.Name);
            t.Equal("p111_d_synthetic_version", "1.0.0.0", identity.VersionText);
            t.Equal("p111_d_synthetic_culture", "",
                NormalizeCulture(identity.Culture));
            t.Check("p111_d_synthetic_unsigned", !identity.IsStrongNamed, "no public key");
        }

        // ---- PE32+ and stream order / index-size variants are all valid -------------------
        var pe32Plus = SyntheticPe.Create(s => s.Pe32Plus = true);
        t.Check("p111_d_pe32_plus_readable", Accepts(pe32Plus.Image, "TestAsm", out var e1), e1);

        var reordered = SyntheticPe.Create(s => s.StreamOrder = new[] { 2, 0, 1 });
        t.Check("p111_d_stream_order_variation_readable", Accepts(reordered.Image, "TestAsm", out var e2), e2);

        var wide = SyntheticPe.Create(s => s.FourByteHeapIndexes = true);
        t.Check("p111_d_four_byte_heap_indexes_readable", Accepts(wide.Image, "TestAsm", out var e3), e3);

        var twoByteBlob = SyntheticPe.Create(s =>
        {
            s.PublicKey = new byte[200];
            s.AssemblyFlags = 0x0001;
        });
        t.Check("p111_d_two_byte_compressed_blob_length_readable",
            Accepts(twoByteBlob.Image, "TestAsm", out var e4), e4);

        // ---- negative: Optional Header / Data Directory --------------------------------
        var rvaCount = SyntheticPe.Create();
        SyntheticPe.WriteU32(rvaCount.Image, rvaCount.A.OptionalRvaCount, 14);
        t.Check("p111_d_cli_directory_absent_from_optional_header_rejected", Rejects(rvaCount.Image, out var e5), e5);

        var shortOptional = SyntheticPe.Create();
        SyntheticPe.WriteU16(shortOptional.Image, shortOptional.A.CoffOptionalHeaderSize, 2);
        t.Check("p111_d_optional_header_too_short_rejected", Rejects(shortOptional.Image, out var e6), e6);

        // ---- negative: CLI header boundaries -------------------------------------------
        var cliTruncated = SyntheticPe.Create();
        SyntheticPe.WriteU32(cliTruncated.Image, cliTruncated.A.CliCb, 8);
        t.Check("p111_d_cli_header_size_truncated_rejected", Rejects(cliTruncated.Image, out var e7), e7);

        var cliOutside = SyntheticPe.Create();
        SyntheticPe.WriteU32(cliOutside.Image, cliOutside.A.CliDirEntry, 0x9000);
        t.Check("p111_d_cli_rva_outside_section_rejected", Rejects(cliOutside.Image, out var e8), e8);

        // CRC: the CLI RVA is mapped but only its first byte is inside SizeOfRawData.
        var partialRaw = SyntheticPe.Create();
        SyntheticPe.WriteU32(partialRaw.Image, partialRaw.A.SectionRawSize, 8);
        t.Check("p111_d_rva_partially_backed_by_raw_data_rejected", Rejects(partialRaw.Image, out var e9), e9);

        var rawOutside = SyntheticPe.Create();
        SyntheticPe.WriteU32(rawOutside.Image, rawOutside.A.SectionRawPointer, 0x5000);
        t.Check("p111_d_section_raw_pointer_outside_file_rejected", Rejects(rawOutside.Image, out var e10), e10);

        var sections = SyntheticPe.Create();
        SyntheticPe.WriteU16(sections.Image, sections.A.CoffSectionCount, 5000);
        t.Check("p111_d_section_table_truncated_rejected", Rejects(sections.Image, out var e11), e11);

        // ---- negative: metadata stream container ---------------------------------------
        // The heap is inside the file but its declared Size runs past the metadata root.
        var streamOutside = SyntheticPe.Create();
        WriteStreamSize(streamOutside, "#Strings", 0x800);
        t.Check("p111_d_metadata_stream_outside_metadata_root_rejected",
            Rejects(streamOutside.Image, out var e12), e12);

        // A stream name with no terminator within the 32-byte limit. "#~" is placed last so the
        // unterminated run reaches the stream container end instead of the next header.
        var unterminated = SyntheticPe.Create(s => s.StreamOrder = new[] { 1, 2, 0 });
        var nameSlot = IndexOfStream(unterminated, "#~");
        for (var i = 0; i < 32; i++)
        {
            unterminated.Image[unterminated.A.StreamNameField[nameSlot] + i] = (byte)'A';
        }
        t.Check("p111_d_unterminated_stream_name_rejected", Rejects(unterminated.Image, out var e13), e13);

        // ---- negative: #Strings heap ---------------------------------------------------
        var shortStrings = SyntheticPe.Create();
        WriteStreamSize(shortStrings, "#Strings", 4);
        t.Check("p111_d_string_terminator_outside_heap_rejected", Rejects(shortStrings.Image, out var e14), e14);

        var invalidUtf8 = SyntheticPe.Create();
        invalidUtf8.Image[invalidUtf8.A.StringsData + 1] = 0xFF;
        t.Check("p111_d_invalid_utf8_assembly_name_rejected", Rejects(invalidUtf8.Image, out var e15), e15);

        var emptyName = SyntheticPe.Create();
        WriteIndex(emptyName.Image, emptyName.A.AssemblyNameIndex, 0, 2);
        t.Check("p111_d_empty_assembly_name_rejected", Rejects(emptyName.Image, out var e16), e16);

        // ---- negative: #Blob heap ------------------------------------------------------
        var blobOverrun = SyntheticPe.Create(s =>
        {
            s.PublicKey = new byte[16];
            s.AssemblyFlags = 0x0001;
        });
        blobOverrun.Image[blobOverrun.A.BlobLengthPrefix] = 0x7E;
        t.Check("p111_d_blob_length_outside_heap_rejected", Rejects(blobOverrun.Image, out var e17), e17);

        // ---- negative: table stream ----------------------------------------------------
        var shortTables = SyntheticPe.Create();
        WriteStreamSize(shortTables, "#~", 40);
        t.Check("p111_d_table_rows_outside_stream_rejected", Rejects(shortTables.Image, out var e18), e18);

        var netmodule = SyntheticPe.Create();
        SyntheticPe.WriteU64(netmodule.Image, netmodule.A.TablesValidMask, 0);
        t.Check("p111_d_netmodule_without_assembly_row_rejected", Rejects(netmodule.Image, out var e19), e19);

        var manyRows = SyntheticPe.Create();
        SyntheticPe.WriteU32(manyRows.Image, manyRows.A.TablesAssemblyRowCount, 0x40000000);
        t.Check("p111_d_assembly_row_count_overflow_rejected", Rejects(manyRows.Image, out var e20), e20);

        // ---- deterministic fuzz: never throws, never hangs, never goes non-deterministic --
        FuzzDeterministic(t, valid.Image);

        // ---- differential check against an independent desktop parser -------------------
        DifferentialAgainstAssemblyName(t, fixtureDir);
    }

    private static void FuzzDeterministic(TestRunner t, byte[] seedImage)
    {
        var state = 0x2F6E2B1u;
        var threw = false;
        var nondeterministic = false;
        var accepted = 0;
        var detail = "";
        for (var iteration = 0; iteration < 1500 && !threw && !nondeterministic; iteration++)
        {
            var image = new byte[seedImage.Length];
            Array.Copy(seedImage, image, seedImage.Length);
            var mutations = 1 + (int)(Next(ref state) % 6);
            for (var m = 0; m < mutations; m++)
            {
                var offset = (int)(Next(ref state) % (uint)image.Length);
                image[offset] = (byte)Next(ref state);
            }
            try
            {
                AssemblyFileIdentity first;
                string firstError;
                var firstOk = ManagedAssemblyReader.TryReadIdentity(image, out first, out firstError);
                AssemblyFileIdentity second;
                string secondError;
                var secondOk = ManagedAssemblyReader.TryReadIdentity(image, out second, out secondError);
                if (firstOk != secondOk || !string.Equals(first.Name, second.Name, StringComparison.Ordinal) ||
                    !string.Equals(first.VersionText, second.VersionText, StringComparison.Ordinal))
                {
                    nondeterministic = true;
                    detail = "iteration " + iteration;
                }
                if (firstOk)
                {
                    accepted++;
                    if (first.Name.Length == 0)
                    {
                        nondeterministic = true;
                        detail = "empty name accepted at iteration " + iteration;
                    }
                }
            }
            catch (Exception ex)
            {
                threw = true;
                detail = ex.GetType().Name + " at iteration " + iteration;
            }
        }
        t.Check("p111_d_fuzz_never_throws", !threw, detail.Length == 0 ? "1500 mutated images" : detail);
        t.Check("p111_d_fuzz_is_deterministic", !nondeterministic,
            nondeterministic ? detail : "accepted=" + accepted + " of 1500");
    }

    private static uint Next(ref uint state)
    {
        state = state * 1664525u + 1013904223u;
        return state >> 8;
    }

    private static void DifferentialAgainstAssemblyName(TestRunner t, string fixtureDir)
    {
        var names = new[]
        {
            "FixtureMod.dll", "FixtureMod.Dependency.dll", "SharedLib.dll",
            "DiffVersionLib.v1.dll", "DiffContentLib.x.dll", "VersionGateLib.dll",
        };
        var compared = 0;
        var mismatches = 0;
        var detail = "";
        for (var i = 0; i < names.Length; i++)
        {
            var path = Path.Combine(fixtureDir, names[i]);
            if (!File.Exists(path))
            {
                continue;
            }
            var reference = AssemblyName.GetAssemblyName(path);
            AssemblyFileIdentity parsed;
            string error;
            if (!ManagedAssemblyReader.TryReadIdentity(File.ReadAllBytes(path), out parsed, out error))
            {
                mismatches++;
                detail = names[i] + " unreadable: " + error;
                continue;
            }
            compared++;
            var referenceCulture = reference.CultureName ?? "";
            var parsedCulture = parsed.Culture ?? "";
            if (!string.Equals(reference.Name, parsed.Name, StringComparison.Ordinal) ||
                !string.Equals(reference.Version.ToString(), parsed.VersionText, StringComparison.Ordinal) ||
                !string.Equals(NormalizeCulture(referenceCulture), NormalizeCulture(parsedCulture), StringComparison.Ordinal))
            {
                mismatches++;
                detail = names[i] + " reference=" + reference.Name + "/" + reference.Version + "/" + referenceCulture +
                         " parsed=" + parsed.Name + "/" + parsed.VersionText + "/" + parsedCulture;
            }
        }
        t.Check("p111_d_differential_matches_assemblyname_api",
            compared >= 4 && mismatches == 0, "compared=" + compared + " mismatches=" + mismatches + " " + detail);
    }

    private static string NormalizeCulture(string value)
    {
        return value.Length == 0 || value == "neutral" ? "" : value;
    }

    // ============================================================ E. framework version

    private static void FrameworkVersionState(TestRunner t, string fixtureDir, string fixtureDll)
    {
        var previousSeam = Loader.FrameworkVersionUnavailableForTests;
        try
        {
            var known = Loader.FrameworkVersion();
            t.Check("p111_e_framework_version_readable_when_available",
                known.Length > 0 && known != "unknown", known);

            Loader.FrameworkVersionUnavailableForTests = true;

            var unknown = Loader.FrameworkVersion();
            t.Check("p111_e_unknown_framework_version_is_not_a_placeholder",
                !string.Equals(unknown, "0.0.0", StringComparison.Ordinal) &&
                !string.Equals(unknown, "1.0.0", StringComparison.Ordinal),
                unknown);

            var root = Temp.NewDirectory("p111-e-unknown");
            Package(root, "E.Range", "P111.E.Range", "FixtureMod.GoodEntry", new[] { fixtureDll }, "FixtureMod.dll",
                "\"frameworkVersion\": \">=1.0.0 <2.0.0\", \"gameVersion\": \"*\"");
            var returned = Loader.Run(root);
            var report = Report(root);
            t.Check("p111_e_unknown_framework_version_fails_closed",
                returned == null && report.Contains("id=P111.E.Range state=Failed code=MF0506"),
                Line(report, "id=P111.E.Range "));
            t.Check("p111_e_report_states_framework_version_state",
                report.Contains("framework-version-state=unknown"), Line(report, "framework-version"));

            var wildcard = Temp.NewDirectory("p111-e-wildcard");
            Package(wildcard, "E.Wildcard", "P111.E.Wildcard", "FixtureMod.GoodEntry", new[] { fixtureDll },
                "FixtureMod.dll", "\"frameworkVersion\": \"*\", \"gameVersion\": \"*\"");
            Loader.Run(wildcard);
            var wildcardReport = Report(wildcard);
            t.Check("p111_e_wildcard_not_constrained_by_unknown_version",
                wildcardReport.Contains("id=P111.E.Wildcard state=Initialized"),
                Line(wildcardReport, "id=P111.E.Wildcard "));

            var marker = Temp.ReadText(Path.Combine(wildcard, "E.Wildcard", "entry-ran.txt"));
            var markerParts = marker.Split('|');
            t.Check("p111_e_mod_context_reports_unknown_framework_version",
                markerParts.Length == 3 &&
                !string.Equals(markerParts[2].Trim(), "0.0.0", StringComparison.Ordinal) &&
                markerParts[2].IndexOf("unknown", StringComparison.OrdinalIgnoreCase) >= 0,
                marker);
        }
        finally
        {
            Loader.FrameworkVersionUnavailableForTests = previousSeam;
        }

        // Known + explicit range keeps working.
        var previousOverride = Loader.FrameworkVersionOverride;
        try
        {
            Loader.FrameworkVersionOverride = "1.0.0";
            var root = Temp.NewDirectory("p111-e-known");
            Package(root, "E.Known", "P111.E.Known", "FixtureMod.GoodEntry", new[] { fixtureDll }, "FixtureMod.dll",
                "\"frameworkVersion\": \"^1.0.0\", \"gameVersion\": \"*\"");
            Loader.Run(root);
            var report = Report(root);
            t.Check("p111_e_known_framework_version_still_satisfies_range",
                report.Contains("id=P111.E.Known state=Initialized"), Line(report, "id=P111.E.Known "));
        }
        finally
        {
            Loader.FrameworkVersionOverride = previousOverride;
        }
    }

    // ================================================================ F. path redaction

    private static void PathRedaction(TestRunner t, string fixtureDir, string fixtureDll)
    {
        var root = Temp.NewDirectory("p111-f");
        Package(root, "F.Good", "P111.F.Good", "FixtureMod.GoodEntry", new[] { fixtureDll }, "FixtureMod.dll");
        Package(root, "F.Throwing", "P111.F.Throwing", "FixtureMod.PathLeakEntry", new[] { fixtureDll }, "FixtureMod.dll");
        Package(root, "F.Missing", "P111.F.Missing", "FixtureMod.GoodEntry", new string[0], "Absent.dll");

        var returned = Loader.Run(root);
        var report = Report(root);
        t.Check("p111_f_run_completed", returned == null, returned ?? "(null)");

        var leak = FirstAbsolutePath(report);
        t.Check("p111_f_report_contains_no_absolute_path", leak.Length == 0, leak);

        t.Check("p111_f_redaction_keeps_diagnostic_value",
            report.Contains("id=P111.F.Missing state=Failed code=MF0201") &&
            report.Contains("Absent.dll"),
            Line(report, "id=P111.F.Missing "));

        var log = Temp.ReadText(Path.Combine(root, "logs", "P111.F.Throwing.log"));
        var logLeak = FirstAbsolutePath(log);
        t.Check("p111_f_mod_log_contains_no_absolute_path", logLeak.Length == 0, logLeak);
        t.Check("p111_f_mod_log_keeps_exception_type", log.Contains("IOException"), "exception type retained");

        // A missing mods root must not echo the directory in the returned diagnostic either.
        var missingRoot = Path.Combine(Temp.NewDirectory("p111-f-missing"), "does-not-exist");
        var missingDiagnostic = Loader.Run(missingRoot) ?? "";
        t.Check("p111_f_missing_root_diagnostic_contains_no_absolute_path",
            missingDiagnostic.Length > 0 && FirstAbsolutePath(missingDiagnostic).Length == 0,
            missingDiagnostic);
    }

    // ======================================================================= helpers

    /// <summary>First absolute path found in the text, or empty. Deliberately OS-agnostic.</summary>
    internal static string FirstAbsolutePath(string text)
    {
        if (text.Length == 0)
        {
            return "";
        }
        for (var i = 0; i + 2 < text.Length; i++)
        {
            var ch = text[i];
            var isDrivePath = i + 2 < text.Length &&
                              ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z')) &&
                              text[i + 1] == ':' &&
                              (text[i + 2] == '\\' || text[i + 2] == '/');
            var isPosixRoot = ch == '/' && (i == 0 || text[i - 1] == ' ' || text[i - 1] == '"' || text[i - 1] == '(') &&
                              i + 1 < text.Length && text[i + 1] != '/' && text[i + 1] != ' ';
            if (isDrivePath || isPosixRoot)
            {
                var start = Math.Max(0, i - 24);
                var length = Math.Min(110, text.Length - start);
                return text.Substring(start, length).Replace("\r", " ").Replace("\n", " ");
            }
        }
        return "";
    }

    private static bool Accepts(byte[] image, string expectedName, out string detail)
    {
        AssemblyFileIdentity identity;
        string error;
        var ok = ManagedAssemblyReader.TryReadIdentity(image, out identity, out error);
        if (!ok)
        {
            detail = "rejected: " + error;
            return false;
        }
        detail = identity.Describe();
        return string.Equals(identity.Name, expectedName, StringComparison.Ordinal);
    }

    private static bool Rejects(byte[] image, out string detail)
    {
        AssemblyFileIdentity identity;
        string error;
        var ok = ManagedAssemblyReader.TryReadIdentity(image, out identity, out error);
        if (ok)
        {
            detail = "accepted: " + identity.Describe();
            return false;
        }
        detail = error;
        return true;
    }

    private static int IndexOfStream(SyntheticPe.Built built, string name)
    {
        for (var i = 0; i < built.A.StreamNames.Length; i++)
        {
            if (string.Equals(built.A.StreamNames[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    private static void WriteStreamSize(SyntheticPe.Built built, string name, uint size)
    {
        var slot = IndexOfStream(built, name);
        if (slot >= 0)
        {
            SyntheticPe.WriteU32(built.Image, built.A.StreamSizeField[slot], size);
        }
    }

    private static void WriteStreamOffset(SyntheticPe.Built built, string name, uint offset)
    {
        var slot = IndexOfStream(built, name);
        if (slot >= 0)
        {
            SyntheticPe.WriteU32(built.Image, built.A.StreamOffsetField[slot], offset);
        }
    }

    private static void WriteIndex(byte[] image, int offset, int value, int size)
    {
        if (size == 4)
        {
            SyntheticPe.WriteU32(image, offset, (uint)value);
            return;
        }
        SyntheticPe.WriteU16(image, offset, (ushort)value);
    }

    private static string Report(string root)
    {
        return Temp.ReadText(Path.Combine(root, "loader-report.txt"));
    }

    /// <summary>Ordered <c>id/state/code</c> signature of the <c>mods:</c> block.</summary>
    internal static string Signature(string report)
    {
        var sb = new StringBuilder();
        var lines = report.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (!trimmed.StartsWith("- id=", StringComparison.Ordinal))
            {
                continue;
            }
            var cut = trimmed.IndexOf(" message=", StringComparison.Ordinal);
            if (cut < 0)
            {
                cut = trimmed.IndexOf(" kind=", StringComparison.Ordinal);
            }
            var segment = cut > 0 ? trimmed.Substring(0, cut) : trimmed;
            if (sb.Length > 0)
            {
                sb.Append('|');
            }
            sb.Append(segment);
        }
        return sb.ToString();
    }

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

    /// <summary>True when this mod is in the Failed state with one of the given stable codes.</summary>
    private static bool FailedWith(string report, string modId, params string[] codes)
    {
        for (var i = 0; i < codes.Length; i++)
        {
            if (report.Contains("id=" + modId + " state=Failed code=" + codes[i]))
            {
                return true;
            }
        }
        return false;
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
        return sb.Length == 0 ? "(no preflight-conflict lines)" : sb.ToString();
    }

    private static string HostLines(string report)
    {
        var sb = new StringBuilder();
        var lines = report.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("preflight-host", StringComparison.Ordinal) ||
                trimmed.StartsWith("preflight-warning=", StringComparison.Ordinal))
            {
                if (sb.Length > 0)
                {
                    sb.Append(" | ");
                }
                sb.Append(trimmed);
            }
        }
        return sb.Length == 0 ? "(no host lines)" : sb.ToString();
    }

    private static void Package(
        string root,
        string directoryName,
        string id,
        string entryType,
        string[] dlls,
        string entryAssembly,
        string extraJson = "")
    {
        var package = Path.Combine(root, directoryName);
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "manifest.json"),
            ManifestJson(id, entryType, extraJson, entryAssembly), new UTF8Encoding(false));
        for (var i = 0; i < dlls.Length; i++)
        {
            var destination = Path.Combine(package, Path.GetFileName(dlls[i]));
            if (!string.Equals(destination, Path.Combine(package, entryAssembly), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(dlls[i], destination, true);
            }
        }
        if (dlls.Length > 0 && !File.Exists(Path.Combine(package, entryAssembly)))
        {
            File.Copy(dlls[0], Path.Combine(package, entryAssembly), true);
        }
    }

    private static string ManifestJson(string id, string entryType, string extraJson, string entryAssembly)
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
