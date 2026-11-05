using System.IO;

namespace ModFramework.Runtime;

/// <summary>
/// The snapshot area that binds "the bytes the preflight approved" to "the bytes that are
/// loaded" (Phase 1.1.2).
///
/// <para><b>Why it exists.</b> Phase 1.1.1 read a declared assembly once, derived its identity and
/// its content digest from that buffer, and then loaded the <i>path</i>
/// (<c>Assembly.LoadFrom(claim.CanonicalPath)</c>). Everything between the read and the load was a
/// window in which the file could be replaced or edited in place: the preflight would have
/// approved one assembly and the runtime would have executed another. Re-checking the digest
/// immediately before <c>LoadFrom</c> only narrows that window; it cannot close it.</para>
///
/// <para><b>What it does.</b> The claim reader hands the exact buffer it just parsed and hashed to
/// this type, which writes it to an application-private file and then <b>reads it back and
/// compares every byte</b>. Only after that comparison succeeds does the claim get a
/// <see cref="AssemblyClaim.StagedPath"/>; <c>Assembly.LoadFrom</c> is given that path and never
/// the mod-declared file again. Replacing the declared file afterwards therefore has no effect on
/// what is loaded.</para>
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
/// one claim at a time only. Nothing here keeps a whole mod tree in memory.</para>
/// </summary>
internal sealed class AssemblyStagingArea
{
    /// <summary>Directory under the mods root. A <c>.</c> prefix makes discovery ignore it.</summary>
    internal const string DirectoryName = ".staging";

    /// <summary>Total bytes this run may stage. A preflight that would exceed it fails closed.</summary>
    internal const long MaxTotalBytes = 256L * 1024 * 1024;

    /// <summary>Total files this run may stage (one mod has at most 1 + 32 declared assemblies).</summary>
    internal const int MaxFiles = 4096;

    private readonly string _runDirectory;
    private readonly Dictionary<string, string> _byFingerprint = new(StringComparer.Ordinal);
    private long _totalBytes;
    private int _fileCount;

    private AssemblyStagingArea(string runDirectory)
    {
        _runDirectory = runDirectory;
    }

    internal string RunDirectory => _runDirectory;

    internal long TotalBytes => _totalBytes;

    internal int FileCount => _fileCount;

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
            var baseDirectory = Path.Combine(modsRoot, DirectoryName);
            Directory.CreateDirectory(baseDirectory);
            RemoveOlderRuns(baseDirectory);
            var runDirectory = Path.Combine(baseDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runDirectory);
            return new AssemblyStagingArea(runDirectory);
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
    /// </summary>
    internal bool TryStage(AssemblyClaim claim, byte[] bytes, out string error)
    {
        error = "";
        var fingerprint = ComputeFingerprint(bytes, claim.Digest.StartsWith("sha256:", StringComparison.Ordinal));

        string? existing;
        if (_byFingerprint.TryGetValue(fingerprint, out existing) && existing != null)
        {
            // Identical preflighted content: one file, several claims. Sharing the snapshot is
            // the point - all of them must be handed the same bytes.
            claim.StagedPath = existing;
            claim.SnapshotFingerprint = fingerprint;
            return true;
        }

        if (_fileCount >= MaxFiles)
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
        if (_totalBytes + bytes.Length > MaxTotalBytes)
        {
            error = "the assembly snapshot area is full (" + (MaxTotalBytes / (1024 * 1024)) + " MiB)";
            return false;
        }

        var directory = Path.Combine(_runDirectory, claim.ModId);
        var path = Path.Combine(directory, claim.DeclaredName);
        if (!PathRules.IsInside(_runDirectory, path))
        {
            error = "the declared file name does not resolve inside the snapshot area";
            return false;
        }

        try
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
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

        _byFingerprint[fingerprint] = path;
        _totalBytes += bytes.Length;
        _fileCount++;
        claim.StagedPath = path;
        claim.SnapshotFingerprint = fingerprint;
        return true;
    }

    /// <summary>
    /// Re-checks a staged file immediately before it is loaded. This is a consistency check, not
    /// an authorization: the authorization happened in the preflight. It exists so that a
    /// snapshot modified after staging is refused instead of silently loaded.
    /// </summary>
    internal static bool VerifyStagedFile(string path, string fingerprint, long length, out string error)
    {
        error = "";
        if (fingerprint.Length == 0)
        {
            // Fail closed: without a fingerprint the snapshot cannot be shown to be the one that
            // was approved.
            error = "the snapshot has no recorded fingerprint";
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

        var actual = fingerprint.StartsWith("sha256:", StringComparison.Ordinal)
            ? ComputeFingerprint(bytes, true)
            : ComputeFingerprint(bytes, false);
        if (!string.Equals(actual, fingerprint, StringComparison.Ordinal))
        {
            error = "the snapshot file no longer matches the preflighted content";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Content fingerprint of a byte sequence: <c>sha256:…</c> when requested and available,
    /// otherwise the built-in <c>sha1:…</c> form. The prefix says which function was used, so
    /// verification always recomputes the same one. SHA-1 appears here only because the snapshot
    /// check must also work when the trimmed runtime has no SHA-256; it is not the authorization
    /// for sharing (that is <see cref="ContentIdentity"/>'s full byte comparison).
    /// </summary>
    internal static string ComputeFingerprint(byte[] bytes, bool useSha256)
    {
        if (useSha256)
        {
            try
            {
                return "sha256:" + AssemblyClaimReader.ToHex(
                    System.Security.Cryptography.SHA256.HashData(bytes));
            }
            catch (Exception)
            {
                // Fall through to the always-available form.
            }
        }
        return "sha1:" + AssemblyClaimReader.ToHex(PublicKeyToken.Sha1.Hash(bytes));
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
