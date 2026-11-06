using System.IO;
using System.Security.Cryptography;

namespace ModFramework.Runtime;

/// <summary>
/// One staged artifact: the file the loader may hand to <c>Assembly.LoadFrom</c>, the length it
/// had when it was written, and the digest that proves it still holds those bytes.
///
/// <para>Phase 1.1.3: a single fingerprint can no longer stand for "one artifact". Several
/// entries may share the same <i>deduplication key</i> while holding different bytes, so the
/// candidate list - not the key - is the unit that a sharing decision is made against.</para>
/// </summary>
internal sealed class SnapshotEntry
{
    internal string Path = "";

    internal long Length;

    /// <summary>
    /// <c>sha256:&lt;hex&gt;</c> over the exact bytes written to <see cref="Path"/>. Never the
    /// deduplication key: the key is allowed to collide, this value is the snapshot's own
    /// integrity record (see <see cref="AssemblyStagingArea.VerifySnapshot"/>).
    /// </summary>
    internal string IntegrityDigest = "";
}

/// <summary>
/// The snapshot area that binds "the bytes the preflight approved" to "the bytes that are
/// loaded" (Phase 1.1.2), with Phase 1.1.3's deduplication-integrity rules.
///
/// <para><b>Why it exists.</b> Phase 1.1.1 read a declared assembly once, derived its identity and
/// its content digest from that buffer, and then loaded the <i>path</i>
/// (<c>Assembly.LoadFrom(claim.CanonicalPath)</c>). Everything between the read and the load was a
/// window in which the file could be replaced or edited in place: the preflight would have
/// approved one assembly and the runtime would have executed another.</para>
///
/// <para><b>What it does.</b> The claim reader hands the exact buffer it just parsed and hashed to
/// this type, which writes it to an application-private file and then <b>reads it back and
/// compares every byte</b>. Only after that comparison succeeds does the claim get a
/// <see cref="AssemblyClaim.StagedPath"/>; <c>Assembly.LoadFrom</c> is given that path and never
/// the mod-declared file again.</para>
///
/// <para><b>Three different questions, three different answers (Phase 1.1.3).</b></para>
/// <list type="number">
///   <item>
///     <b>Deduplication key</b> (<see cref="ComputeDedupKey"/>): a cheap candidate index. It is
///     allowed to collide - both because SHA-256 is only computationally collision resistant and
///     because the runtime may force the weaker SHA-1 form - so a key hit never authorizes
///     sharing. Several artifacts may live under one key.
///   </item>
///   <item>
///     <b>Snapshot integrity</b> (<see cref="VerifySnapshot"/>): proves that a staged file still
///     holds the bytes that were written. SHA-256 only; when the runtime cannot compute it the
///     claim fails closed rather than falling back to a weaker value.
///   </item>
///   <item>
///     <b>Assembly identity</b> (<see cref="AssemblyIdentityComparison.SameLoadedIdentity"/>):
///     checked by the loader against the assembly <c>LoadFrom</c> actually returned, as defence
///     in depth after the byte-level gates above.
///   </item>
/// </list>
///
/// <para><b>What it does not prove, stated plainly.</b> The staged file lives in the same
/// application-private storage as the mods themselves, so code that can write there (same uid)
/// could still replace it. That is why the loader re-verifies the staged file immediately before
/// <c>LoadFrom</c> and fails the mod when the check fails. A residual window between that check
/// and the load remains, and is reported as a known limitation - not as "TOCTOU impossible".
/// Closing it completely would need <c>Assembly.Load(byte[])</c>, which the shipped runtime does
/// not have (see <c>docs/architecture/phase1-runtime-constraints.md</c> §4.1).</para>
///
/// <para><b>Cost.</b> One file per distinct preflighted content, bounded by
/// <see cref="MaxTotalBytes"/> and <see cref="MaxFiles"/>; the in-memory buffer is still held for
/// one claim at a time only. A deduplication hit costs one full byte comparison (16 KiB buffers)
/// against each candidate of the same length, never a whole-file copy.</para>
/// </summary>
internal sealed class AssemblyStagingArea
{
    /// <summary>Directory under the mods root. A <c>.</c> prefix makes discovery ignore it.</summary>
    internal const string DirectoryName = ".staging";

    /// <summary>Total bytes this run may stage. A preflight that would exceed it fails closed.</summary>
    internal const long MaxTotalBytes = 256L * 1024 * 1024;

    /// <summary>Total files this run may stage (one mod has at most 1 + 32 declared assemblies).</summary>
    internal const int MaxFiles = 4096;

    /// <summary>
    /// The resource bounds of one staging area. Production always uses
    /// <see cref="MaxTotalBytes"/> / <see cref="MaxFiles"/>; the test seam below can shrink them
    /// so the limit behaviour is reachable without staging hundreds of megabytes.
    /// </summary>
    internal struct Limits
    {
        internal long MaxTotalBytes;

        internal int MaxFiles;
    }

    private readonly string _runDirectory;
    private readonly Limits _limits;

    /// <summary>
    /// Deduplication candidates by key. A key maps to a <b>list</b>: two different byte sequences
    /// can produce the same key (a real hash collision, or a runtime that forces the SHA-1 form),
    /// and both must be storable, findable and comparable.
    /// </summary>
    private readonly Dictionary<string, List<SnapshotEntry>> _byDedupKey = new(StringComparer.Ordinal);

    private long _totalBytes;
    private int _fileCount;

    private AssemblyStagingArea(string runDirectory, Limits limits)
    {
        _runDirectory = runDirectory;
        _limits = limits;
    }

    internal string RunDirectory => _runDirectory;

    internal long TotalBytes => _totalBytes;

    internal int FileCount => _fileCount;

    /// <summary>
    /// Test seam: replaces the deduplication key function so a fingerprint collision is reachable
    /// deterministically. It affects <b>only</b> the candidate index - the snapshot integrity
    /// digest is always the real SHA-256 of the bytes, so a forced collision cannot make the
    /// integrity verifier accept a file it would otherwise reject. <c>null</c> in production;
    /// tests restore the previous value in a <c>finally</c> block.
    /// </summary>
    internal static Func<byte[], string>? DedupKeyForTests;

    /// <summary>
    /// Test seam: forces "this runtime cannot compute SHA-256". Used to prove the fallback policy:
    /// the candidate index degrades to SHA-1 (allowed - it only proposes candidates), while the
    /// snapshot integrity digest refuses to fall back and staging fails closed.
    /// </summary>
    internal static bool SnapshotDigestUnavailableForTests;

    /// <summary>
    /// Test seam: shrinks the staging budget so <see cref="MaxFiles"/> / <see cref="MaxTotalBytes"/>
    /// are reachable in a unit test. <c>null</c> means the production constants.
    /// </summary>
    internal static Limits? LimitsForTests;

    /// <summary>
    /// Creates this run's snapshot directory and removes the directories of earlier runs on a
    /// best-effort basis (a file still mapped by a loaded assembly cannot be removed on every OS,
    /// and that is not an error).
    /// </summary>
    internal static AssemblyStagingArea? TryCreate(string modsRoot, out string error)
    {
        error = "";
        try
        {
            var limits = LimitsForTests ?? new Limits { MaxTotalBytes = MaxTotalBytes, MaxFiles = MaxFiles };
            var baseDirectory = Path.Combine(modsRoot, DirectoryName);
            Directory.CreateDirectory(baseDirectory);
            RemoveOlderRuns(baseDirectory);
            var runDirectory = Path.Combine(baseDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runDirectory);
            return new AssemblyStagingArea(runDirectory, limits);
        }
        catch (Exception ex)
        {
            // Nothing is staged: the caller must fail closed instead of loading by path.
            error = PathScrubber.Describe(ex);
            return null;
        }
    }

    private static void RemoveOlderRuns(string baseDirectory)
    {
        try
        {
            var directories = Directory.GetDirectories(baseDirectory);
            for (var i = 0; i < directories.Length; i++)
            {
                try
                {
                    Directory.Delete(directories[i], true);
                }
                catch (Exception)
                {
                    // Best effort: a snapshot of an earlier run may still be mapped.
                }
            }
        }
        catch (Exception)
        {
            // Best effort only.
        }
    }

    /// <summary>
    /// Stages <paramref name="bytes"/> for <paramref name="claim"/> and proves the staged file
    /// matches the buffer byte for byte. On success <see cref="AssemblyClaim.StagedPath"/> and
    /// <see cref="AssemblyClaim.SnapshotFingerprint"/> are set; on failure both stay empty and the
    /// claim must be rejected.
    ///
    /// <para>A claim only shares an existing artifact when a full byte comparison - against the
    /// <i>current</i> claim's own buffer, never against another claim's staged path - proves the
    /// two contents identical. Everything else (different bytes, different length, unreadable
    /// candidate) creates a separate artifact or fails closed.</para>
    /// </summary>
    internal bool TryStage(AssemblyClaim claim, byte[] bytes, out string error)
    {
        error = "";

        // 1. The key only proposes candidates; it never authorizes sharing.
        var dedupKey = ComputeDedupKey(bytes);

        SnapshotEntry? match;
        if (!TryFindSameContent(dedupKey, bytes, out match, out error))
        {
            // An existing candidate could not be read or compared: fail closed instead of guessing
            // whether it was the same content.
            return false;
        }
        if (match != null)
        {
            // Proven byte-identical: one file, several claims. Sharing is the point - all of them
            // must be handed the same bytes, and the loader loads that path once.
            claim.StagedPath = match.Path;
            claim.SnapshotFingerprint = match.IntegrityDigest;
            return true;
        }

        // 2. A new artifact is needed. Budgets apply to artifacts, not to claims, so a collision
        //    cannot be used to bypass them.
        if (_fileCount >= _limits.MaxFiles)
        {
            error = "the assembly snapshot area already holds " + _fileCount + " files";
            return false;
        }
        if (bytes.Length > ManagedAssemblyReader.MaxAssemblyBytes)
        {
            error = "the assembly is larger than the " +
                    (ManagedAssemblyReader.MaxAssemblyBytes / (1024 * 1024)) + " MiB snapshot limit";
            return false;
        }
        if (_totalBytes + bytes.Length > _limits.MaxTotalBytes)
        {
            error = "the assembly snapshot area is full (" + (_limits.MaxTotalBytes / (1024 * 1024)) + " MiB)";
            return false;
        }

        // 3. The integrity record is computed before anything is written: without a strong digest
        //    the artifact could not be re-verified before it is loaded, so it must not be created.
        string integrityDigest;
        if (!TryComputeSnapshotDigest(bytes, out integrityDigest, out error))
        {
            return false;
        }

        var path = AllocatePath(claim, out error);
        if (path.Length == 0)
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Never overwrite: a path this run already owns may still be referenced by another
            // claim, and replacing it would silently change what that claim loads.
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }

            string compareError;
            if (!ContentIdentity.VerifyExactBytes(path, bytes, out compareError))
            {
                TryDelete(path);
                error = "the staged copy does not match the preflighted bytes: " + compareError;
                return false;
            }
        }
        catch (Exception ex)
        {
            TryDelete(path);
            error = PathScrubber.Describe(ex);
            return false;
        }

        var entry = new SnapshotEntry
        {
            Path = path,
            Length = bytes.Length,
            IntegrityDigest = integrityDigest,
        };
        AddCandidate(dedupKey, entry);
        _totalBytes += bytes.Length;
        _fileCount++;
        claim.StagedPath = path;
        claim.SnapshotFingerprint = integrityDigest;
        return true;
    }

    /// <summary>
    /// Number of artifacts currently proposed by one deduplication key. Zero when the index is
    /// disabled (no digest function) or when the key is unknown. Diagnostics/test use only.
    /// </summary>
    internal int CandidateCount(string dedupKey)
    {
        if (dedupKey.Length == 0)
        {
            return 0;
        }
        List<SnapshotEntry>? candidates;
        return _byDedupKey.TryGetValue(dedupKey, out candidates) && candidates != null ? candidates.Count : 0;
    }

    /// <summary>
    /// Walks the candidates of one key and returns the one whose file is byte-for-byte the given
    /// buffer. <c>true</c> with <paramref name="match"/> == <c>null</c> means "no candidate has
    /// this content" (a new artifact may be staged); <c>false</c> means the question could not be
    /// answered and the caller must fail closed.
    /// </summary>
    private bool TryFindSameContent(string dedupKey, byte[] bytes, out SnapshotEntry? match, out string error)
    {
        match = null;
        error = "";
        if (dedupKey.Length == 0)
        {
            // No digest function at all: nothing can be looked up. Correctness is unaffected
            // (every claim gets its own artifact); only the deduplication optimization is lost.
            return true;
        }

        List<SnapshotEntry>? candidates;
        if (!_byDedupKey.TryGetValue(dedupKey, out candidates) || candidates == null)
        {
            return true;
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (candidate.Length != bytes.Length)
            {
                // Provably different content: no byte comparison can change that, and a shorter or
                // longer file must never be shared.
                continue;
            }

            ContentVerdict verdict;
            string reason;
            verdict = ContentIdentity.CompareFileToBuffer(candidate.Path, bytes, out reason);
            if (verdict == ContentVerdict.Same)
            {
                match = candidate;
                return true;
            }
            if (verdict == ContentVerdict.Unverifiable)
            {
                // The candidate exists but cannot be read or compared. Sharing it would mean
                // guessing; a fresh artifact would leave an unverifiable file that a later claim
                // might still match. Fail closed for this claim.
                error = "an existing snapshot with the same deduplication key could not be compared: " + reason;
                return false;
            }
            // Different: keep looking. The bucket may hold another candidate with these bytes.
        }
        return true;
    }

    private void AddCandidate(string dedupKey, SnapshotEntry entry)
    {
        if (dedupKey.Length == 0)
        {
            return;
        }
        List<SnapshotEntry>? candidates;
        if (!_byDedupKey.TryGetValue(dedupKey, out candidates) || candidates == null)
        {
            candidates = new List<SnapshotEntry>();
            _byDedupKey[dedupKey] = candidates;
        }
        candidates.Add(entry);
    }

    /// <summary>
    /// Chooses a file name for a new artifact. The declared name is used when it is still free;
    /// otherwise a numeric suffix is appended, so two different contents that share a mod id and a
    /// declared file name never overwrite each other.
    /// </summary>
    private string AllocatePath(AssemblyClaim claim, out string error)
    {
        error = "";
        var directory = Path.Combine(_runDirectory, claim.ModId);
        var declared = claim.DeclaredName;
        var path = Path.Combine(directory, declared);
        if (!PathRules.IsInside(_runDirectory, path))
        {
            error = "the declared file name does not resolve inside the snapshot area";
            return "";
        }
        if (!File.Exists(path))
        {
            return path;
        }

        var stem = Path.GetFileNameWithoutExtension(declared);
        var extension = Path.GetExtension(declared);
        for (var index = 2; index < 4096; index++)
        {
            path = Path.Combine(directory, stem + "~" + index + extension);
            if (!PathRules.IsInside(_runDirectory, path))
            {
                error = "the declared file name does not resolve inside the snapshot area";
                return "";
            }
            if (!File.Exists(path))
            {
                return path;
            }
        }
        error = "no free snapshot file name for \"" + declared + "\"";
        return "";
    }

    /// <summary>
    /// Re-checks a staged file immediately before it is loaded. This is a <b>consistency</b> check,
    /// not an authorization: the authorization happened in the preflight.
    ///
    /// <para>The recorded value must be a SHA-256 digest of the staged bytes. It is deliberately
    /// not the deduplication key: a key that collides must not be able to prove a file intact, and
    /// a runtime that can only provide SHA-1 must not degrade this check (see
    /// <see cref="TryComputeSnapshotDigest"/>).</para>
    /// </summary>
    internal static bool VerifySnapshot(string path, string integrityDigest, long length, out string error)
    {
        error = "";
        if (integrityDigest.Length == 0)
        {
            // Fail closed: without an integrity record the snapshot cannot be shown to be the one
            // that was approved.
            error = "the snapshot has no recorded integrity digest";
            return false;
        }
        if (!integrityDigest.StartsWith("sha256:", StringComparison.Ordinal))
        {
            error = "the snapshot integrity digest is not SHA-256 (" + DescribeAlgorithm(integrityDigest) + ")";
            return false;
        }

        byte[] bytes;
        try
        {
            if (!File.Exists(path))
            {
                error = "the snapshot file is no longer present";
                return false;
            }
            var fileLength = -1L;
            using (var probe = File.OpenRead(path))
            {
                fileLength = probe.Length;
            }
            if (fileLength != length)
            {
                error = "the snapshot file is " + fileLength + " bytes, expected " + length;
                return false;
            }
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            error = PathScrubber.Describe(ex);
            return false;
        }

        if (bytes.Length != length)
        {
            error = "the snapshot file is " + bytes.Length + " bytes, expected " + length;
            return false;
        }

        string actual;
        string digestError;
        if (!TryComputeSnapshotDigest(bytes, out actual, out digestError))
        {
            error = digestError;
            return false;
        }
        if (!string.Equals(actual, integrityDigest, StringComparison.Ordinal))
        {
            error = "the snapshot file no longer matches the preflighted content";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Deduplication key of a byte sequence: <c>sha256:…</c> when the runtime provides SHA-256,
    /// otherwise the built-in <c>sha1:…</c> form, otherwise empty (no index at all).
    ///
    /// <para>SHA-1 is acceptable <b>here and only here</b>: the key proposes candidates and every
    /// hit is decided by a full byte comparison, so a SHA-1 collision costs a comparison, not
    /// correctness. It is never recorded as an integrity value.</para>
    /// </summary>
    internal static string ComputeDedupKey(byte[] bytes)
    {
        var overrideForTests = DedupKeyForTests;
        if (overrideForTests != null)
        {
            return overrideForTests(bytes);
        }

        string sha256;
        if (TryComputeSha256Hex(bytes, out sha256))
        {
            return "sha256:" + sha256;
        }
        try
        {
            return "sha1:" + AssemblyClaimReader.ToHex(PublicKeyToken.Sha1.Hash(bytes));
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// The snapshot's own integrity record: SHA-256, or nothing. There is no SHA-1 fallback -
    /// a weaker value cannot prove a snapshot intact, so callers must fail closed when this
    /// returns <c>false</c>. Verified against the target runtime's BCL by the AndroidRefsDir
    /// build; see <c>docs/architecture/phase1-runtime-constraints.md</c>.
    /// </summary>
    internal static bool TryComputeSnapshotDigest(byte[] bytes, out string digest, out string error)
    {
        digest = "";
        error = "";
        string hex;
        if (!TryComputeSha256Hex(bytes, out hex))
        {
            error = "this runtime provides no SHA-256, so the snapshot integrity of the preflighted " +
                    "bytes cannot be recorded (a weaker digest is not accepted)";
            return false;
        }
        digest = "sha256:" + hex;
        return true;
    }

    private static bool TryComputeSha256Hex(byte[] bytes, out string hex)
    {
        hex = "";
        if (SnapshotDigestUnavailableForTests)
        {
            return false;
        }
        try
        {
            hex = AssemblyClaimReader.ToHex(SHA256.HashData(bytes));
            return true;
        }
        catch (Exception)
        {
            // The trimmed runtime may not expose SHA-256 at all. Report it; never substitute a
            // weaker function for an integrity value.
            return false;
        }
    }

    private static string DescribeAlgorithm(string digest)
    {
        var colon = digest.IndexOf(':');
        return colon > 0 ? digest.Substring(0, colon) : digest;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Best effort: the run directory is removed on the next start.
        }
    }
}
