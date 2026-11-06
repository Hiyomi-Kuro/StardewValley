using System;
using System.Collections.Generic;
using System.IO;
using ModFramework.Runtime;

namespace ModFramework.Tests;

/// <summary>
/// Phase 1.1.3: assembly snapshot deduplication integrity and fingerprint collision resistance.
///
/// The scenarios here are the ones Phase 1.1.2 could not express, because its snapshot area keyed
/// one fingerprint to exactly one file and treated a key hit as proof of equal content:
///
/// <list type="bullet">
///   <item><b>A</b> - two different byte sequences with the same deduplication key must not share
///     a staged artifact, must both be storable, and a third claim must find the candidate that
///     actually matches it.</item>
///   <item><b>B</b> - the same collision, carried through preflight and <c>Assembly.LoadFrom</c>,
///     including two different simple names (which the preflight never compares with each other)
///     and private assemblies.</item>
///   <item><b>C</b> - the snapshot integrity verifier is independent of the deduplication key and
///     fails closed on tampering, truncation, growth, deletion and a runtime without SHA-256.</item>
/// </list>
///
/// Every collision is produced by an internal test seam
/// (<see cref="AssemblyStagingArea.DedupKeyForTests"/>), never by timing or by editing files
/// behind the loader's back, and every seam is restored in a <c>finally</c> block.
/// </summary>
internal static class Phase113Tests
{
    /// <summary>Simple name of the compiled ConflictLib fixture the entry images are derived from.</summary>
    private const string EntrySource = "P112Entry";

    /// <summary>Simple name of the compiled PlainLib fixture the private images are derived from.</summary>
    private const string PrivateSource = "P112Priv";

    /// <summary>The value the deduplication seam returns for every byte sequence.</summary>
    private const string ForcedKey = "forced:collision";

    internal static void Run(TestRunner t, string fixtureDir)
    {
        var entryPath = Path.Combine(fixtureDir, "P112Entry.dll");
        var entryV2Path = Path.Combine(fixtureDir, "P112Entry.v2.dll");
        var privPath = Path.Combine(fixtureDir, "P112Priv.dll");
        if (!File.Exists(entryPath) || !File.Exists(entryV2Path) || !File.Exists(privPath))
        {
            t.Check("p113_fixtures_present", false, "missing P112Entry.dll / P112Entry.v2.dll / P112Priv.dll");
            return;
        }

        var entryBytes = File.ReadAllBytes(entryPath);
        var entryV2Bytes = File.ReadAllBytes(entryV2Path);
        var privBytes = File.ReadAllBytes(privPath);

        StagingDeduplication(t, entryBytes, privBytes);
        PreflightAndLoader(t, entryBytes, entryV2Bytes, privBytes);
        SnapshotIntegrity(t, entryBytes, entryV2Bytes);
    }

    // ================================================================ A. staging dedup

    /// <summary>
    /// A: a fingerprint is a candidate index, not a sharing permission.
    ///
    /// The two images are both valid, readable, loadable managed assemblies with the same length
    /// and different simple names - they are produced by renaming the compiled fixture, exactly as
    /// Phase 1.1.2 produced its same-length replacement files. Under the seam below they also get
    /// the same deduplication key, which is the situation a real collision would create.
    /// </summary>
    private static void StagingDeduplication(TestRunner t, byte[] entryBytes, byte[] privBytes)
    {
        var bytesA = Rename(entryBytes, EntrySource, "Q113EntA1");
        var bytesB = Rename(entryBytes, EntrySource, "R113EntB2");

        var root = Temp.NewDirectory("p113-a-collision");
        var staging = AssemblyStagingArea.TryCreate(root, out var stagingError);
        t.Check("p113_a_staging_area_is_created", staging != null, stagingError);
        if (staging == null)
        {
            return;
        }

        var fileA = WriteImage(root, "one", "Q113EntA1.dll", bytesA);
        var fileB = WriteImage(root, "two", "R113EntB2.dll", bytesB);

        AssemblyClaim claimA;
        AssemblyClaim claimB;
        bool stagedA;
        bool stagedB;
        var previousKey = AssemblyStagingArea.DedupKeyForTests;
        try
        {
            AssemblyStagingArea.DedupKeyForTests = _ => ForcedKey;
            stagedA = AssemblyClaimReader.TryRead("P113.A.One", AssemblyClaimRole.Entry,
                "Q113EntA1.dll", fileA, staging, out claimA, out _, out _);
            stagedB = AssemblyClaimReader.TryRead("P113.A.Two", AssemblyClaimRole.Entry,
                "R113EntB2.dll", fileB, staging, out claimB, out _, out _);
        }
        finally
        {
            AssemblyStagingArea.DedupKeyForTests = previousKey;
        }

        t.Check("p113_a_colliding_claims_are_both_staged", stagedA && stagedB,
            "first=" + stagedA + " second=" + stagedB);
        if (!stagedA || !stagedB)
        {
            return;
        }

        // ---- P113-01 / P113-02: a key hit is not a content proof ------------------------
        t.Check("p113_a_collision_does_not_share_a_snapshot",
            !Same(claimA.StagedPath, claimB.StagedPath),
            "first=" + claimA.StagedPath + " second=" + claimB.StagedPath);
        t.Check("p113_a_first_snapshot_is_its_own_bytes",
            BytesEqual(File.ReadAllBytes(claimA.StagedPath), bytesA),
            Sha256(File.ReadAllBytes(claimA.StagedPath)) + " vs " + Sha256(bytesA));
        t.Check("p113_a_second_snapshot_is_its_own_bytes",
            BytesEqual(File.ReadAllBytes(claimB.StagedPath), bytesB),
            Sha256(File.ReadAllBytes(claimB.StagedPath)) + " vs " + Sha256(bytesB));
        t.Check("p113_a_snapshot_names_follow_the_declarations",
            Same(Path.GetFileName(claimA.StagedPath), "Q113EntA1.dll") &&
            Same(Path.GetFileName(claimB.StagedPath), "R113EntB2.dll"),
            Path.GetFileName(claimA.StagedPath) + "/" + Path.GetFileName(claimB.StagedPath));
        t.Check("p113_a_snapshots_are_not_the_declared_files",
            !Same(claimA.StagedPath, claimA.Path) && !Same(claimB.StagedPath, claimB.Path),
            "a snapshot must never be the mod's own file");

        // ---- P113-03: one key may hold several different artifacts ----------------------
        t.Check("p113_a_bucket_holds_two_artifacts",
            staging.CandidateCount(ForcedKey) == 2 && staging.FileCount == 2,
            "candidates=" + staging.CandidateCount(ForcedKey) + " files=" + staging.FileCount +
            " bytes=" + staging.TotalBytes);

        // ---- P113-07 (index half): the recorded integrity value is not the dedup key -----
        t.Check("p113_a_snapshot_digest_is_not_the_dedup_key",
            claimA.SnapshotFingerprint.StartsWith("sha256:", StringComparison.Ordinal) &&
            claimB.SnapshotFingerprint.StartsWith("sha256:", StringComparison.Ordinal) &&
            !Same(claimA.SnapshotFingerprint, ForcedKey) &&
            !Same(claimB.SnapshotFingerprint, ForcedKey),
            claimA.SnapshotFingerprint + " / " + claimB.SnapshotFingerprint);

        // ---- P113-03 (second half): the third claim finds the *matching* candidate ------
        AssemblyClaim claimC;
        var previousKeyC = AssemblyStagingArea.DedupKeyForTests;
        bool stagedC;
        try
        {
            AssemblyStagingArea.DedupKeyForTests = _ => ForcedKey;
            stagedC = AssemblyClaimReader.TryRead("P113.A.Third", AssemblyClaimRole.Entry,
                "T113EntC3.dll", WriteImage(root, "three", "T113EntC3.dll", bytesB), staging,
                out claimC, out _, out _);
        }
        finally
        {
            AssemblyStagingArea.DedupKeyForTests = previousKeyC;
        }
        t.Check("p113_a_third_claim_finds_the_matching_candidate",
            stagedC && Same(claimC.StagedPath, claimB.StagedPath) && staging.FileCount == 2,
            "staged=" + stagedC + " path=" + (stagedC ? claimC.StagedPath : "(none)") +
            " files=" + staging.FileCount);
        t.Check("p113_a_third_claim_did_not_reuse_the_first_candidate",
            stagedC && !Same(claimC.StagedPath, claimA.StagedPath),
            stagedC ? claimC.StagedPath : "(none)");

        // ---- a different length can never be the same content ---------------------------
        var longerBytes = Append(bytesB, 4);
        AssemblyClaim claimD;
        var previousKeyD = AssemblyStagingArea.DedupKeyForTests;
        bool stagedD;
        try
        {
            AssemblyStagingArea.DedupKeyForTests = _ => ForcedKey;
            stagedD = AssemblyClaimReader.TryRead("P113.A.Longer", AssemblyClaimRole.Entry,
                "U113EntD4.dll", WriteImage(root, "four", "U113EntD4.dll", longerBytes), staging,
                out claimD, out _, out _);
        }
        finally
        {
            AssemblyStagingArea.DedupKeyForTests = previousKeyD;
        }
        t.Check("p113_a_a_different_length_is_never_shared",
            stagedD && !Same(claimD.StagedPath, claimA.StagedPath) && !Same(claimD.StagedPath, claimB.StagedPath),
            stagedD ? claimD.StagedPath : "(not staged)");
        t.Check("p113_a_a_different_length_is_staged_verbatim",
            stagedD && BytesEqual(File.ReadAllBytes(claimD.StagedPath), longerBytes),
            stagedD ? Sha256(File.ReadAllBytes(claimD.StagedPath)) : "(not staged)");

        // ---- P113-09: identical bytes still share one artifact, once ---------------------
        IdenticalSharing(t, bytesA);

        // ---- an unreadable candidate must fail closed, not be guessed --------------------
        UnreadableCandidate(t, bytesA);

        // ---- P113-10: the budgets still bound a multi-candidate bucket ------------------
        ResourceBounds(t, entryBytes, bytesB);

        // ---- path safety: declared names and mod ids never overwrite each other --------
        PathSeparation(t, entryBytes, privBytes);
    }

    private static void IdenticalSharing(TestRunner t, byte[] image)
    {
        var root = Temp.NewDirectory("p113-a-identical");
        var staging = AssemblyStagingArea.TryCreate(root, out _);
        if (staging == null)
        {
            t.Check("p113_a_identical_bytes_share_one_snapshot", false, "no staging area");
            return;
        }

        var fileOne = WriteImage(root, "one", "S113Same.dll", image);
        var fileTwo = WriteImage(root, "two", "S113Same.dll", image);

        AssemblyClaim first;
        AssemblyClaim second;
        var previous = AssemblyStagingArea.DedupKeyForTests;
        try
        {
            AssemblyStagingArea.DedupKeyForTests = _ => ForcedKey;
            AssemblyClaimReader.TryRead("P113.A.Same1", AssemblyClaimRole.Entry, "S113Same.dll", fileOne,
                staging, out first, out _, out _);
            AssemblyClaimReader.TryRead("P113.A.Same2", AssemblyClaimRole.Private, "S113Same.dll", fileTwo,
                staging, out second, out _, out _);
        }
        finally
        {
            AssemblyStagingArea.DedupKeyForTests = previous;
        }

        t.Check("p113_a_identical_bytes_share_one_snapshot",
            Same(first.StagedPath, second.StagedPath),
            first.StagedPath + " vs " + second.StagedPath);
        t.Check("p113_a_identical_bytes_are_counted_once",
            staging.FileCount == 1 && staging.TotalBytes == image.Length,
            "files=" + staging.FileCount + " bytes=" + staging.TotalBytes + " expected=" + image.Length);
        t.Check("p113_a_shared_snapshot_is_the_exact_bytes",
            BytesEqual(File.ReadAllBytes(first.StagedPath), image),
            Sha256(File.ReadAllBytes(first.StagedPath)));
    }

    private static void UnreadableCandidate(TestRunner t, byte[] image)
    {
        var root = Temp.NewDirectory("p113-a-unreadable");
        var staging = AssemblyStagingArea.TryCreate(root, out _);
        if (staging == null)
        {
            t.Check("p113_a_unreadable_candidate_fails_closed", false, "no staging area");
            return;
        }

        var file = WriteImage(root, "one", "W113EntW7.dll", image);
        AssemblyClaim first;
        var previousKey = AssemblyStagingArea.DedupKeyForTests;
        var previousOpen = ContentIdentity.OpenForTests;
        bool stagedFirst;
        bool stagedSecond;
        string code;
        string message;
        try
        {
            AssemblyStagingArea.DedupKeyForTests = _ => ForcedKey;
            stagedFirst = AssemblyClaimReader.TryRead("P113.A.Unreadable1", AssemblyClaimRole.Entry,
                "W113EntW7.dll", file, staging, out first, out _, out _);

            // The candidate file is there but cannot be read: sharing it would be a guess.
            ContentIdentity.OpenForTests = path =>
            {
                if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && path.IndexOf(".staging", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    throw new IOException("the snapshot cannot be read (injected for the test)");
                }
                return File.OpenRead(path);
            };
            AssemblyClaim second;
            stagedSecond = AssemblyClaimReader.TryRead("P113.A.Unreadable2", AssemblyClaimRole.Entry,
                "W113EntW7.dll", file, staging, out second, out code, out message);
        }
        finally
        {
            ContentIdentity.OpenForTests = previousOpen;
            AssemblyStagingArea.DedupKeyForTests = previousKey;
        }

        t.Check("p113_a_unreadable_candidate_fails_closed", stagedFirst && !stagedSecond,
            "first=" + stagedFirst + " second=" + stagedSecond);
        t.Check("p113_a_unreadable_candidate_reports_staging_failure",
            Same(code, ErrorCodes.AssemblyStagingFailed) &&
            message.IndexOf("could not be compared", StringComparison.Ordinal) >= 0,
            code + ": " + message);
        t.Check("p113_a_unreadable_candidate_created_no_extra_artifact", staging.FileCount == 1,
            "files=" + staging.FileCount);
    }

    private static void ResourceBounds(TestRunner t, byte[] entryBytes, byte[] bytesB)
    {
        var bytesA = Rename(entryBytes, EntrySource, "Q113EntA1");
        var root = Temp.NewDirectory("p113-a-bounds");
        var previousLimits = AssemblyStagingArea.LimitsForTests;
        var previousKey = AssemblyStagingArea.DedupKeyForTests;
        bool stagedFirst;
        bool stagedSecond;
        bool stagedThird;
        bool stagedFourth;
        var thirdCode = "";
        var thirdMessage = "";
        try
        {
            AssemblyStagingArea.LimitsForTests = new AssemblyStagingArea.Limits
            {
                MaxFiles = 2,
                MaxTotalBytes = 64L * 1024 * 1024,
            };
            AssemblyStagingArea.DedupKeyForTests = _ => ForcedKey;
            var staging = AssemblyStagingArea.TryCreate(root, out _);

            AssemblyClaim first;
            AssemblyClaim second;
            AssemblyClaim third;
            AssemblyClaim fourth;
            stagedFirst = AssemblyClaimReader.TryRead("P113.A.Bound1", AssemblyClaimRole.Entry,
                "Y113EntY1.dll", WriteImage(root, "one", "Y113EntY1.dll", bytesA), staging,
                out first, out _, out _);
            stagedSecond = AssemblyClaimReader.TryRead("P113.A.Bound2", AssemblyClaimRole.Entry,
                "Y113EntY2.dll", WriteImage(root, "two", "Y113EntY2.dll", bytesB), staging,
                out second, out _, out _);
            stagedThird = AssemblyClaimReader.TryRead("P113.A.Bound3", AssemblyClaimRole.Entry,
                "Y113EntY3.dll", WriteImage(root, "three", "Y113EntY3.dll", Rename(entryBytes, EntrySource, "Z113EntZ3")),
                staging, out third, out thirdCode, out thirdMessage);

            // Sharing an artifact that already exists costs no budget, so it must still work.
            stagedFourth = AssemblyClaimReader.TryRead("P113.A.Bound4", AssemblyClaimRole.Entry,
                "Y113EntY4.dll", WriteImage(root, "four", "Y113EntY4.dll", bytesA), staging,
                out fourth, out _, out _);
        }
        finally
        {
            AssemblyStagingArea.DedupKeyForTests = previousKey;
            AssemblyStagingArea.LimitsForTests = previousLimits;
        }

        t.Check("p113_a_max_files_is_reachable",
            stagedFirst && stagedSecond && !stagedThird,
            "first=" + stagedFirst + " second=" + stagedSecond + " third=" + stagedThird);
        t.Check("p113_a_max_files_rejects_with_a_budget_message",
            Same(thirdCode, ErrorCodes.AssemblyStagingFailed) &&
            thirdMessage.IndexOf("already holds 2 files", StringComparison.Ordinal) >= 0,
            thirdCode + ": " + thirdMessage);
        t.Check("p113_a_sharing_still_works_at_the_file_bound", stagedFourth,
            "an existing artifact must not consume budget");

        var bytesRoot = Temp.NewDirectory("p113-a-bytes");
        var previousLimits2 = AssemblyStagingArea.LimitsForTests;
        var previousKey2 = AssemblyStagingArea.DedupKeyForTests;
        bool fits;
        bool overflows;
        string overflowCode = "";
        string overflowMessage = "";
        try
        {
            AssemblyStagingArea.LimitsForTests = new AssemblyStagingArea.Limits
            {
                MaxFiles = 4096,
                MaxTotalBytes = bytesA.Length + 16,
            };
            AssemblyStagingArea.DedupKeyForTests = _ => ForcedKey;
            var limited = AssemblyStagingArea.TryCreate(bytesRoot, out _)!;
            AssemblyClaim first;
            AssemblyClaim second;
            fits = AssemblyClaimReader.TryRead("P113.A.Total1", AssemblyClaimRole.Entry,
                "A113Tot1.dll", WriteImage(bytesRoot, "one", "A113Tot1.dll", bytesA), limited,
                out first, out _, out _);
            overflows = AssemblyClaimReader.TryRead("P113.A.Total2", AssemblyClaimRole.Entry,
                "A113Tot2.dll", WriteImage(bytesRoot, "two", "A113Tot2.dll", bytesB), limited,
                out second, out overflowCode, out overflowMessage);
        }
        finally
        {
            AssemblyStagingArea.DedupKeyForTests = previousKey2;
            AssemblyStagingArea.LimitsForTests = previousLimits2;
        }

        t.Check("p113_a_max_total_bytes_is_reachable", fits && !overflows,
            "first=" + fits + " second=" + overflows);
        t.Check("p113_a_max_total_bytes_rejects_with_a_budget_message",
            Same(overflowCode, ErrorCodes.AssemblyStagingFailed) &&
            overflowMessage.IndexOf("is full", StringComparison.Ordinal) >= 0,
            overflowCode + ": " + overflowMessage);
    }

    private static void PathSeparation(TestRunner t, byte[] entryBytes, byte[] privBytes)
    {
        var root = Temp.NewDirectory("p113-a-paths");
        var staging = AssemblyStagingArea.TryCreate(root, out _);
        if (staging == null)
        {
            t.Check("p113_a_paths_do_not_overwrite", false, "no staging area");
            return;
        }

        var bytesA = Rename(entryBytes, EntrySource, "Q113EntA1");
        var bytesB = Rename(entryBytes, EntrySource, "B113EntB4");
        var bytesC = Rename(entryBytes, EntrySource, "R113EntB2");
        var bytesD = Rename(privBytes, PrivateSource, "Q113PriA");

        AssemblyClaim one;
        AssemblyClaim two;
        AssemblyClaim three;
        AssemblyClaim four;
        var previous = AssemblyStagingArea.DedupKeyForTests;
        try
        {
            AssemblyStagingArea.DedupKeyForTests = _ => ForcedKey;

            // Same mod id, two declared names, different content.
            AssemblyClaimReader.TryRead("P113.A.Paths", AssemblyClaimRole.Entry, "P113One.dll",
                WriteImage(root, "one", "P113One.dll", bytesA), staging, out one, out _, out _);
            AssemblyClaimReader.TryRead("P113.A.Paths", AssemblyClaimRole.Private, "P113Two.dll",
                WriteImage(root, "two", "P113Two.dll", bytesB), staging, out two, out _, out _);

            // Two mod ids, the same declared name, different content.
            AssemblyClaimReader.TryRead("P113.A.PathX", AssemblyClaimRole.Entry, "P113Same.dll",
                WriteImage(root, "three", "P113Same.dll", bytesC), staging, out three, out _, out _);
            AssemblyClaimReader.TryRead("P113.A.PathY", AssemblyClaimRole.Entry, "P113Same.dll",
                WriteImage(root, "four", "P113Same.dll", bytesD), staging, out four, out _, out _);
        }
        finally
        {
            AssemblyStagingArea.DedupKeyForTests = previous;
        }

        var distinct = !Same(one.StagedPath, two.StagedPath) &&
                       !Same(one.StagedPath, three.StagedPath) &&
                       !Same(one.StagedPath, four.StagedPath) &&
                       !Same(two.StagedPath, three.StagedPath) &&
                       !Same(two.StagedPath, four.StagedPath) &&
                       !Same(three.StagedPath, four.StagedPath);
        t.Check("p113_a_paths_do_not_overwrite", distinct && staging.FileCount == 4,
            "files=" + staging.FileCount + " distinct=" + distinct);
        t.Check("p113_a_same_mod_different_names_keep_their_own_bytes",
            BytesEqual(File.ReadAllBytes(one.StagedPath), bytesA) &&
            BytesEqual(File.ReadAllBytes(two.StagedPath), bytesB),
            Sha256(File.ReadAllBytes(two.StagedPath)) + " vs " + Sha256(bytesB));
        t.Check("p113_a_same_declared_name_in_two_mods_keeps_its_own_bytes",
            BytesEqual(File.ReadAllBytes(three.StagedPath), bytesC) &&
            BytesEqual(File.ReadAllBytes(four.StagedPath), bytesD),
            Sha256(File.ReadAllBytes(four.StagedPath)) + " vs " + Sha256(bytesD));
    }

    // ============================================================ B. preflight + loader

    /// <summary>
    /// B: the same collision, carried through the real <c>Manifest -&gt; Preflight -&gt; Loader -&gt;
    /// Assembly.LoadFrom -&gt; IModEntry.Initialize</c> chain.
    ///
    /// The two mods declare entry assemblies with <b>different simple names</b>, so the preflight
    /// puts them in different identity groups and never compares them with each other. That is
    /// exactly the shape in which a staging alias could previously survive: the alias was created
    /// before the grouping, and nothing later asked the second claim whether its staged file was
    /// its own bytes.
    /// </summary>
    private static void PreflightAndLoader(TestRunner t, byte[] entryBytes, byte[] entryV2Bytes, byte[] privBytes)
    {
        var bytesA = Rename(entryBytes, EntrySource, "Q113EntA1");
        var bytesB = Rename(entryBytes, EntrySource, "R113EntB2");
        var privA = Rename(privBytes, PrivateSource, "Q113PriA");
        var privB = Rename(privBytes, PrivateSource, "R113PriB");

        var root = Temp.NewDirectory("p113-b-cross");
        var packageA = BuildMod(root, "p113-b-alpha", "P113.B.Alpha",
            "Q113EntA1.dll", bytesA, "Q113PriA.dll", privA);
        var packageB = BuildMod(root, "p113-b-beta", "P113.B.Beta",
            "R113EntB2.dll", bytesB, "R113PriB.dll", privB);

        // ---- claim level: each claim owns a snapshot of its own bytes -------------------
        var claimRoot = Temp.NewDirectory("p113-b-claims");
        var staging = AssemblyStagingArea.TryCreate(claimRoot, out var stagingError);
        t.Check("p113_b_staging_area_is_created", staging != null, stagingError);
        if (staging == null)
        {
            return;
        }

        var manifestA = ManifestReader.Load(packageA);
        var manifestB = ManifestReader.Load(packageB);
        t.Check("p113_b_manifests_are_readable", manifestA.Ok && manifestB.Ok,
            (manifestA.Ok ? "" : manifestA.Code + " ") + (manifestB.Ok ? "" : manifestB.Code));

        var previousKey = AssemblyStagingArea.DedupKeyForTests;
        AssemblyPreflightResult analysis;
        try
        {
            AssemblyStagingArea.DedupKeyForTests = _ => ForcedKey;
            analysis = AssemblyPreflight.Analyze(
                new List<ModManifest> { manifestA.Manifest!, manifestB.Manifest! }, staging);
        }
        finally
        {
            AssemblyStagingArea.DedupKeyForTests = previousKey;
        }

        t.Check("p113_b_preflight_rejects_nothing", analysis.Rejections.Count == 0,
            analysis.Rejections.Count == 0 ? "" : Describe(analysis));
        t.Check("p113_b_both_entries_are_in_different_identity_groups",
            analysis.FindClaim("P113.B.Alpha", "Q113EntA1.dll") != null &&
            analysis.FindClaim("P113.B.Beta", "R113EntB2.dll") != null,
            "declared=" + analysis.DeclaredFiles);

        var entryClaimA = analysis.FindClaim("P113.B.Alpha", "Q113EntA1.dll");
        var entryClaimB = analysis.FindClaim("P113.B.Beta", "R113EntB2.dll");
        var privateClaimA = analysis.FindClaim("P113.B.Alpha", "Q113PriA.dll");
        var privateClaimB = analysis.FindClaim("P113.B.Beta", "R113PriB.dll");
        if (entryClaimA == null || entryClaimB == null || privateClaimA == null || privateClaimB == null)
        {
            t.Check("p113_b_claims_are_present", false, "a claim is missing");
            return;
        }

        // ---- P113-04: different simple names never swap their staged content -----------
        t.Check("p113_b_cross_name_entries_do_not_share_a_snapshot",
            !Same(entryClaimA.StagedPath, entryClaimB.StagedPath),
            entryClaimA.StagedPath + " vs " + entryClaimB.StagedPath);
        t.Check("p113_b_entries_are_staged_verbatim",
            BytesEqual(File.ReadAllBytes(entryClaimA.StagedPath), bytesA) &&
            BytesEqual(File.ReadAllBytes(entryClaimB.StagedPath), bytesB),
            Sha256(File.ReadAllBytes(entryClaimB.StagedPath)) + " vs " + Sha256(bytesB));

        // ---- P113-05: a private assembly's staged bytes are its own --------------------
        t.Check("p113_b_cross_name_privates_do_not_share_a_snapshot",
            !Same(privateClaimA.StagedPath, privateClaimB.StagedPath),
            privateClaimA.StagedPath + " vs " + privateClaimB.StagedPath);
        t.Check("p113_b_privates_are_staged_verbatim",
            BytesEqual(File.ReadAllBytes(privateClaimA.StagedPath), privA) &&
            BytesEqual(File.ReadAllBytes(privateClaimB.StagedPath), privB),
            Sha256(File.ReadAllBytes(privateClaimB.StagedPath)) + " vs " + Sha256(privB));
        t.Check("p113_b_private_snapshot_identity_matches_its_claim",
            IdentityNameOf(privateClaimA.StagedPath) == "Q113PriA" &&
            IdentityNameOf(privateClaimB.StagedPath) == "R113PriB" &&
            Same(privateClaimA.Identity.Name, "Q113PriA") &&
            Same(privateClaimB.Identity.Name, "R113PriB"),
            IdentityNameOf(privateClaimA.StagedPath) + "/" + IdentityNameOf(privateClaimB.StagedPath));
        t.Check("p113_b_entry_snapshot_identity_matches_its_claim",
            IdentityNameOf(entryClaimA.StagedPath) == "Q113EntA1" &&
            IdentityNameOf(entryClaimB.StagedPath) == "R113EntB2",
            IdentityNameOf(entryClaimA.StagedPath) + "/" + IdentityNameOf(entryClaimB.StagedPath));

        // ---- end to end ---------------------------------------------------------------
        var previousRunKey = AssemblyStagingArea.DedupKeyForTests;
        var runResult = (string)null;
        try
        {
            AssemblyStagingArea.DedupKeyForTests = _ => ForcedKey;
            runResult = Loader.Run(root);
        }
        finally
        {
            AssemblyStagingArea.DedupKeyForTests = previousRunKey;
        }
        var report = TestSupport.Report(root);
        t.Check("p113_b_run_succeeds", runResult == null, runResult ?? "(null)");
        t.Check("p113_b_both_mods_initialize",
            report.Contains("id=P113.B.Alpha state=Initialized") &&
            report.Contains("id=P113.B.Beta state=Initialized"),
            TestSupport.Line(report, "id=P113.B.Alpha ") + " / " + TestSupport.Line(report, "id=P113.B.Beta "));
        t.Check("p113_b_each_mod_runs_its_own_entry",
            Marker(packageA) == "P113.B.Alpha|A" && Marker(packageB) == "P113.B.Beta|A",
            "alpha=" + Marker(packageA) + " beta=" + Marker(packageB));
        t.Check("p113_b_the_two_entries_are_loaded_separately",
            CountLines(report, "assembly-load=", "file=Q113EntA1.dll") == 1 &&
            CountLines(report, "assembly-load=", "file=R113EntB2.dll") == 1,
            "alpha=" + CountLines(report, "assembly-load=", "file=Q113EntA1.dll") +
            " beta=" + CountLines(report, "assembly-load=", "file=R113EntB2.dll"));
        t.Check("p113_b_no_identity_mismatch_is_reported",
            report.IndexOf("code=" + ErrorCodes.AssemblyLoadIdentityMismatch, StringComparison.Ordinal) < 0,
            TestSupport.Line(report, "preflight="));

        // ---- P113-13: discovery order changes nothing ---------------------------------
        var orderRoot = Temp.NewDirectory("p113-b-order");
        var orderBytesC = Rename(entryBytes, EntrySource, "S113EntC1");
        var orderBytesD = Rename(entryBytes, EntrySource, "T113EntD2");
        BuildMod(orderRoot, "z-last-alpha", "P113.B.OrderAlpha",
            "S113EntC1.dll", orderBytesC, "S113PriC.dll", Rename(privBytes, PrivateSource, "S113PriC"));
        BuildMod(orderRoot, "a-first-beta", "P113.B.OrderBeta",
            "T113EntD2.dll", orderBytesD, "T113PriD.dll", Rename(privBytes, PrivateSource, "T113PriD"));
        var previousOrderKey = AssemblyStagingArea.DedupKeyForTests;
        try
        {
            AssemblyStagingArea.DedupKeyForTests = _ => ForcedKey;
            Loader.Run(orderRoot);
        }
        finally
        {
            AssemblyStagingArea.DedupKeyForTests = previousOrderKey;
        }
        var orderReport = TestSupport.Report(orderRoot);
        t.Check("p113_b_order_independent_classification",
            SortedSignature(report, "P113.B.Alpha", "A", "P113.B.Beta", "B") ==
            SortedSignature(orderReport, "P113.B.OrderAlpha", "A", "P113.B.OrderBeta", "B") &&
            SortedSignature(orderReport, "P113.B.OrderAlpha", "A", "P113.B.OrderBeta", "B") ==
            "A Initialized|B Initialized",
            SortedSignature(report, "P113.B.Alpha", "A", "P113.B.Beta", "B") + " vs " +
            SortedSignature(orderReport, "P113.B.OrderAlpha", "A", "P113.B.OrderBeta", "B"));
        t.Check("p113_b_order_independent_loading",
            orderReport.Contains("id=P113.B.OrderAlpha state=Initialized") &&
            orderReport.Contains("id=P113.B.OrderBeta state=Initialized") &&
            CountLines(orderReport, "assembly-load=", "file=S113EntC1.dll") == 1 &&
            CountLines(orderReport, "assembly-load=", "file=T113EntD2.dll") == 1,
            TestSupport.Line(orderReport, "id=P113.B.OrderAlpha "));

        // ---- same simple name + same identity + different content is still a conflict --
        SameNameDifferentContent(t, entryBytes, entryV2Bytes);

        // ---- entries only: the case where the alias would really be loaded --------------
        CrossNameEntriesOnly(t, entryBytes);
    }

    /// <summary>
    /// The narrowest form of the defect: two mods, one entry assembly each, different simple
    /// names, equal length, forced onto the same deduplication key. Nothing else (no private
    /// assembly whose different length could mask the problem) is involved, so in a build where a
    /// key hit authorizes sharing the second mod's entry claim resolves to the first mod's
    /// artifact and the wrong assembly is what actually enters the process.
    /// </summary>
    private static void CrossNameEntriesOnly(TestRunner t, byte[] entryBytes)
    {
        var root = Temp.NewDirectory("p113-b-entries");
        var alphaBytes = Rename(entryBytes, EntrySource, "E113EntE1");
        var betaBytes = Rename(entryBytes, EntrySource, "F113EntF2");
        var alpha = BuildMod(root, "p113-b-entry-alpha", "P113.B.EntryAlpha", "E113EntE1.dll", alphaBytes,
            "", Array.Empty<byte>());
        var beta = BuildMod(root, "p113-b-entry-beta", "P113.B.EntryBeta", "F113EntF2.dll", betaBytes,
            "", Array.Empty<byte>());

        var previous = AssemblyStagingArea.DedupKeyForTests;
        try
        {
            AssemblyStagingArea.DedupKeyForTests = _ => ForcedKey;
            Loader.Run(root);
        }
        finally
        {
            AssemblyStagingArea.DedupKeyForTests = previous;
        }

        var report = TestSupport.Report(root);
        t.Check("p113_b_entries_only_both_initialize",
            report.Contains("id=P113.B.EntryAlpha state=Initialized") &&
            report.Contains("id=P113.B.EntryBeta state=Initialized"),
            TestSupport.Line(report, "id=P113.B.EntryAlpha ") + " / " + TestSupport.Line(report, "id=P113.B.EntryBeta "));
        t.Check("p113_b_entries_only_never_load_a_foreign_assembly",
            report.IndexOf("code=" + ErrorCodes.AssemblyNameMismatch, StringComparison.Ordinal) < 0 &&
            report.IndexOf("code=" + ErrorCodes.AssemblyLoadIdentityMismatch, StringComparison.Ordinal) < 0,
            TestSupport.Line(report, "id=P113.B.EntryBeta "));
        t.Check("p113_b_entries_only_run_their_own_markers",
            Marker(alpha) == "P113.B.EntryAlpha|A" && Marker(beta) == "P113.B.EntryBeta|A",
            "alpha=" + Marker(alpha) + " beta=" + Marker(beta));
        t.Check("p113_b_entries_only_load_twice",
            CountLines(report, "assembly-load=", "file=E113EntE1.dll") == 1 &&
            CountLines(report, "assembly-load=", "file=F113EntF2.dll") == 1,
            "alpha=" + CountLines(report, "assembly-load=", "file=E113EntE1.dll") +
            " beta=" + CountLines(report, "assembly-load=", "file=F113EntF2.dll"));
    }

    /// <summary>
    /// A deduplication hit must never turn "same simple name, different content" into a shareable
    /// artifact: the preflight's own full-byte comparison still rejects both owners (MF0206).
    /// </summary>
    private static void SameNameDifferentContent(TestRunner t, byte[] entryBytes, byte[] entryV2Bytes)
    {
        var root = Temp.NewDirectory("p113-b-conflict");
        var first = Rename(entryBytes, EntrySource, "V113SameA");
        var second = Rename(entryV2Bytes, EntrySource, "V113SameA");
        BuildMod(root, "p113-b-conflict-one", "P113.B.Conflict1", "V113SameA.dll", first, "", Array.Empty<byte>());
        BuildMod(root, "p113-b-conflict-two", "P113.B.Conflict2", "V113SameA.dll", second, "", Array.Empty<byte>());

        var previous = AssemblyStagingArea.DedupKeyForTests;
        try
        {
            AssemblyStagingArea.DedupKeyForTests = _ => ForcedKey;
            Loader.Run(root);
        }
        finally
        {
            AssemblyStagingArea.DedupKeyForTests = previous;
        }

        var report = TestSupport.Report(root);
        t.Check("p113_b_same_name_different_content_is_still_rejected",
            TestSupport.FailedWith(report, "P113.B.Conflict1", ErrorCodes.AssemblyIdentityConflict) &&
            TestSupport.FailedWith(report, "P113.B.Conflict2", ErrorCodes.AssemblyIdentityConflict),
            TestSupport.Line(report, "id=P113.B.Conflict1 "));
        t.Check("p113_b_a_conflict_still_loads_nothing",
            CountLines(report, "assembly-load=", "file=V113SameA.dll") == 0,
            TestSupport.Line(report, "preflight="));
    }

    // ============================================================= C. snapshot integrity

    /// <summary>
    /// C: the snapshot integrity verifier is independent of the deduplication key, and every way a
    /// staged file can stop being the preflighted content is refused.
    /// </summary>
    private static void SnapshotIntegrity(TestRunner t, byte[] entryBytes, byte[] entryV2Bytes)
    {
        var image = Rename(entryBytes, EntrySource, "K113EntK1");

        // ---- the happy path ------------------------------------------------------------
        var clean = StageOne("p113-c-clean", "K113EntK1.dll", image);
        t.Check("p113_c_a_fresh_snapshot_verifies",
            clean != null && VerifySnapshot(clean, out _),
            clean == null ? "(not staged)" : clean.StagedPath);

        // ---- P113-06: same length, different identity ----------------------------------
        var replaced = StageOne("p113-c-replaced", "K113EntK1.dll", image);
        if (replaced != null)
        {
            File.WriteAllBytes(replaced.StagedPath, Rename(entryBytes, EntrySource, "Q113EntQ1"));
        }
        t.Check("p113_c_same_length_different_identity_is_refused",
            replaced != null && !VerifySnapshot(replaced, out var identityReason) &&
            identityReason.IndexOf("no longer matches", StringComparison.Ordinal) >= 0,
            replaced == null ? "(not staged)" : VerifySnapshotError(replaced));

        // ---- P113-06: same length, same identity, different content --------------------
        var contentReplaced = StageOne("p113-c-content", "K113EntK1.dll", image);
        if (contentReplaced != null)
        {
            File.WriteAllBytes(contentReplaced.StagedPath, Rename(entryV2Bytes, EntrySource, "K113EntK1"));
        }
        t.Check("p113_c_same_identity_different_content_is_refused",
            contentReplaced != null && !VerifySnapshot(contentReplaced, out _),
            contentReplaced == null ? "(not staged)" : VerifySnapshotError(contentReplaced));

        // ---- P113-06: truncation, growth and deletion ----------------------------------
        var truncated = StageOne("p113-c-truncated", "K113EntK1.dll", image);
        if (truncated != null)
        {
            var shortened = new byte[image.Length - 8];
            Array.Copy(image, shortened, shortened.Length);
            File.WriteAllBytes(truncated.StagedPath, shortened);
        }
        t.Check("p113_c_truncated_snapshot_is_refused",
            truncated != null && !VerifySnapshot(truncated, out _),
            truncated == null ? "(not staged)" : VerifySnapshotError(truncated));

        var grown = StageOne("p113-c-grown", "K113EntK1.dll", image);
        if (grown != null)
        {
            var extended = Append(image, 8);
            File.WriteAllBytes(grown.StagedPath, extended);
        }
        t.Check("p113_c_grown_snapshot_is_refused",
            grown != null && !VerifySnapshot(grown, out _),
            grown == null ? "(not staged)" : VerifySnapshotError(grown));

        var deleted = StageOne("p113-c-deleted", "K113EntK1.dll", image);
        if (deleted != null)
        {
            File.Delete(deleted.StagedPath);
        }
        t.Check("p113_c_deleted_snapshot_is_refused",
            deleted != null && !VerifySnapshot(deleted, out var deletedReason) &&
            deletedReason.IndexOf("no longer present", StringComparison.Ordinal) >= 0,
            deleted == null ? "(not staged)" : VerifySnapshotError(deleted));

        // ---- P113-07: a forced dedup key cannot make the verifier accept a change ------
        var collided = StageOne("p113-c-collided", "K113EntK1.dll", image, forcedKey: true);
        bool collidedVerdict;
        if (collided != null)
        {
            File.WriteAllBytes(collided.StagedPath, Rename(entryBytes, EntrySource, "M113EntM2"));
            collidedVerdict = VerifySnapshot(collided, out _);
        }
        else
        {
            collidedVerdict = true;
        }
        t.Check("p113_c_a_forced_collision_does_not_fool_the_verifier", collided != null && !collidedVerdict,
            collided == null ? "(not staged)" : VerifySnapshotError(collided));
        t.Check("p113_c_the_recorded_integrity_value_is_sha256",
            collided != null && collided.SnapshotFingerprint.StartsWith("sha256:", StringComparison.Ordinal),
            collided == null ? "(not staged)" : collided.SnapshotFingerprint);

        // ---- P113-07: no SHA-256 means no snapshot, never a weaker one -----------------
        var previousUnavailable = AssemblyStagingArea.SnapshotDigestUnavailableForTests;
        var previousKey = AssemblyStagingArea.DedupKeyForTests;
        string dedupKey;
        bool stagedWithoutSha256;
        var code = "";
        var message = "";
        try
        {
            AssemblyStagingArea.SnapshotDigestUnavailableForTests = true;
            dedupKey = AssemblyStagingArea.ComputeDedupKey(image);

            var root = Temp.NewDirectory("p113-c-no-sha256");
            var limited = AssemblyStagingArea.TryCreate(root, out _);
            var file = WriteImage(root, "one", "H113EntH1.dll", image);
            AssemblyClaim claim;
            stagedWithoutSha256 = limited != null && AssemblyClaimReader.TryRead(
                "P113.C.NoSha256", AssemblyClaimRole.Entry, "H113EntH1.dll", file, limited,
                out claim, out code, out message);
        }
        finally
        {
            AssemblyStagingArea.SnapshotDigestUnavailableForTests = previousUnavailable;
            AssemblyStagingArea.DedupKeyForTests = previousKey;
        }
        t.Check("p113_c_dedup_key_falls_back_to_sha1_without_sha256",
            dedupKey.StartsWith("sha1:", StringComparison.Ordinal), dedupKey);
        t.Check("p113_c_no_sha256_fails_closed", !stagedWithoutSha256, "staged=" + stagedWithoutSha256);
        t.Check("p113_c_no_sha256_reports_the_missing_strong_digest",
            !stagedWithoutSha256 && message.IndexOf("SHA-256", StringComparison.Ordinal) >= 0,
            message);

        // ---- a verifier that cannot compute must refuse, not pass ----------------------
        var previousUnavailable2 = AssemblyStagingArea.SnapshotDigestUnavailableForTests;
        bool verifiedWithoutSha256;
        string verifyReason;
        try
        {
            AssemblyStagingArea.SnapshotDigestUnavailableForTests = true;
            verifiedWithoutSha256 = clean != null &&
                AssemblyStagingArea.VerifySnapshot(clean.StagedPath, clean.SnapshotFingerprint, clean.Length, out verifyReason);
        }
        finally
        {
            AssemblyStagingArea.SnapshotDigestUnavailableForTests = previousUnavailable2;
        }
        t.Check("p113_c_verifier_fails_closed_without_sha256", !verifiedWithoutSha256,
            "verified=" + verifiedWithoutSha256);

        // ---- P113-11: the loader never falls back to the declared file ----------------
        DeclaredFileIsNeverAFallback(t, image);
        TamperedSnapshotNeverInitializes(t, image);
    }

    /// <summary>
    /// A snapshot that disappears between staging and loading must fail the mod. The declared file
    /// is still present and would load perfectly - which is exactly why the loader must not use it.
    /// </summary>
    private static void DeclaredFileIsNeverAFallback(TestRunner t, byte[] image)
    {
        var root = Temp.NewDirectory("p113-c-gone");
        var package = BuildMod(root, "p113-c-gone-mod", "P113.C.Gone", "G113EntG1.dll", image, "", Array.Empty<byte>());

        var previous = Loader.BeforeLoadForTests;
        try
        {
            Loader.BeforeLoadForTests = claim =>
            {
                if (Same(claim.DeclaredName, "G113EntG1.dll") && File.Exists(claim.StagedPath))
                {
                    File.Delete(claim.StagedPath);
                }
            };
            Loader.Run(root);
        }
        finally
        {
            Loader.BeforeLoadForTests = previous;
        }

        var report = TestSupport.Report(root);
        t.Check("p113_c_a_deleted_snapshot_fails_the_mod",
            TestSupport.FailedWith(report, "P113.C.Gone", ErrorCodes.AssemblyLoadFailed),
            TestSupport.Line(report, "id=P113.C.Gone "));
        t.Check("p113_c_the_declared_file_is_not_loaded_instead",
            CountLines(report, "assembly-load=", "file=G113EntG1.dll") == 0 &&
            Marker(package).Length == 0,
            "marker=" + (Marker(package).Length == 0 ? "(none)" : Marker(package)));
    }

    /// <summary>
    /// P113-06 / P113-08: a staged file replaced at the latest possible moment (the load hook) must
    /// not reach a successful load or <c>Initialize</c>.
    /// </summary>
    private static void TamperedSnapshotNeverInitializes(TestRunner t, byte[] image)
    {
        var root = Temp.NewDirectory("p113-c-tampered");
        var package = BuildMod(root, "p113-c-tampered-mod", "P113.C.Tampered",
            "N113EntN1.dll", image, "", Array.Empty<byte>());
        var replacement = Rename(image, "K113EntK1", "N113EntN1");

        var previous = Loader.BeforeLoadForTests;
        try
        {
            Loader.BeforeLoadForTests = claim =>
            {
                if (Same(claim.DeclaredName, "N113EntN1.dll") && File.Exists(claim.StagedPath))
                {
                    // Same length, valid managed assembly, different simple name.
                    File.WriteAllBytes(claim.StagedPath, replacement);
                }
            };
            Loader.Run(root);
        }
        finally
        {
            Loader.BeforeLoadForTests = previous;
        }

        var report = TestSupport.Report(root);
        t.Check("p113_c_a_tampered_snapshot_fails_the_mod",
            TestSupport.FailedWith(report, "P113.C.Tampered", ErrorCodes.AssemblyLoadFailed),
            TestSupport.Line(report, "id=P113.C.Tampered "));
        t.Check("p113_c_a_tampered_snapshot_never_initializes",
            report.IndexOf("id=P113.C.Tampered state=Initialized", StringComparison.Ordinal) < 0,
            TestSupport.Line(report, "id=P113.C.Tampered "));
        t.Check("p113_c_a_tampered_snapshot_runs_no_entry",
            Marker(package).Length == 0, "marker=" + (Marker(package).Length == 0 ? "(none)" : Marker(package)));
    }

    // ==================================================================== helpers

    /// <summary>Stages one file in its own staging area and returns the claim (or null).</summary>
    private static AssemblyClaim StageOne(string label, string declaredName, byte[] image, bool forcedKey = false)
    {
        var root = Temp.NewDirectory(label);
        var staging = AssemblyStagingArea.TryCreate(root, out _);
        if (staging == null)
        {
            return null;
        }
        var file = WriteImage(root, "mod", declaredName, image);

        var previous = AssemblyStagingArea.DedupKeyForTests;
        try
        {
            if (forcedKey)
            {
                AssemblyStagingArea.DedupKeyForTests = _ => ForcedKey;
            }
            AssemblyClaim claim;
            if (!AssemblyClaimReader.TryRead("P113.C." + label, AssemblyClaimRole.Entry, declaredName, file,
                    staging, out claim, out _, out _))
            {
                return null;
            }
            return claim;
        }
        finally
        {
            AssemblyStagingArea.DedupKeyForTests = previous;
        }
    }

    private static bool VerifySnapshot(AssemblyClaim claim, out string error)
    {
        return AssemblyStagingArea.VerifySnapshot(claim.StagedPath, claim.SnapshotFingerprint, claim.Length, out error);
    }

    private static string VerifySnapshotError(AssemblyClaim claim)
    {
        if (claim == null)
        {
            return "(no claim)";
        }
        AssemblyStagingArea.VerifySnapshot(claim.StagedPath, claim.SnapshotFingerprint, claim.Length, out var error);
        return error;
    }

    /// <summary>
    /// Builds a well-formed mod package: manifest, renamed entry assembly and (optionally) one
    /// private assembly. The file name, the manifest field and the internal assembly name agree.
    /// </summary>
    private static string BuildMod(
        string root,
        string directoryName,
        string id,
        string entryFileName,
        byte[] entryImage,
        string privateFileName,
        byte[] privateImage)
    {
        var extra = privateFileName.Length > 0 ? TestSupport.PrivateAssemblies(privateFileName) : "";
        var package = TestSupport.CreatePackage(root, directoryName,
            TestSupport.ManifestJson(id, entryFileName, "ConflictLib.Entry", extra));
        File.WriteAllBytes(Path.Combine(package, entryFileName), entryImage);
        if (privateFileName.Length > 0)
        {
            File.WriteAllBytes(Path.Combine(package, privateFileName), privateImage);
        }
        return package;
    }

    private static string WriteImage(string root, string directory, string name, byte[] image)
    {
        var path = Path.Combine(root, directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, image);
        return path;
    }

    /// <summary>
    /// Renames an assembly in place: same length, same PE layout, different identity. This is the
    /// only way two fixtures of equal length with different simple names are produced.
    /// </summary>
    private static byte[] Rename(byte[] image, string from, string to)
    {
        return TestSupport.PatchAscii((byte[])image.Clone(), from, to);
    }

    private static byte[] Append(byte[] image, int extraBytes)
    {
        var result = new byte[image.Length + extraBytes];
        Array.Copy(image, result, image.Length);
        return result;
    }

    private static string IdentityNameOf(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            AssemblyFileIdentity identity;
            string error;
            if (!ManagedAssemblyReader.TryReadIdentity(bytes, out identity, out error))
            {
                return "(unreadable: " + error + ")";
            }
            return identity.Name;
        }
        catch (Exception ex)
        {
            return "(error: " + ex.GetType().Name + ")";
        }
    }

    private static string Marker(string package)
    {
        return Temp.ReadText(Path.Combine(package, "entry-ran.txt"));
    }

    private static int CountLines(string report, string first, string second)
    {
        var count = 0;
        var lines = report.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].IndexOf(first, StringComparison.Ordinal) >= 0 &&
                lines[i].IndexOf(second, StringComparison.Ordinal) >= 0)
            {
                count++;
            }
        }
        return count;
    }

    private static string Describe(AssemblyPreflightResult analysis)
    {
        var text = "";
        foreach (var pair in analysis.Rejections)
        {
            if (text.Length > 0)
            {
                text += " | ";
            }
            text += pair.Key + "=" + pair.Value.Code + " " + pair.Value.Message;
        }
        return text;
    }

    /// <summary>
    /// Order-independent classification signature: every <c>- id=… state=… code=…</c> entry of the
    /// report's mods block, with the mod ids replaced by stable aliases and the entries sorted.
    /// Two runs that differ only in discovery order must produce the same value.
    /// </summary>
    private static string SortedSignature(string report, string firstId, string firstAlias,
        string secondId, string secondAlias)
    {
        var segments = TestSupport.Signature(report).Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
        var normalized = new List<string>();
        for (var i = 0; i < segments.Length; i++)
        {
            var text = segments[i];
            text = text.Replace(firstId, firstAlias).Replace(secondId, secondAlias);
            var cut = text.IndexOf(" kind=", StringComparison.Ordinal);
            if (cut > 0)
            {
                text = text.Substring(0, cut);
            }
            text = text.Replace("- ", "").Replace("id=", "").Replace("state=", "");
            normalized.Add(text.Trim());
        }
        normalized.Sort(StringComparer.Ordinal);
        var result = "";
        for (var i = 0; i < normalized.Count; i++)
        {
            if (i > 0)
            {
                result += "|";
            }
            result += normalized[i];
        }
        return result;
    }

    private static string Sha256(byte[] bytes)
    {
        return "sha256:" + TestSupport.Sha256Hex(bytes);
    }

    private static bool Same(string left, string right)
    {
        return string.Equals(left, right, StringComparison.Ordinal);
    }

    private static bool BytesEqual(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }
        return true;
    }
}
