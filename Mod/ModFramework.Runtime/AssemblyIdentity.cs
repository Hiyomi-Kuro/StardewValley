using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace ModFramework.Runtime;

/// <summary>Whether a declared file is the mod's entry assembly or one of its private ones.</summary>
internal enum AssemblyClaimRole
{
    Entry = 0,
    Private = 1,
}

/// <summary>
/// How a claim is satisfied at load time.
/// </summary>
internal enum AssemblyResolution
{
    /// <summary>This claim owns the canonical file; the loader loads it once.</summary>
    Load = 0,

    /// <summary>An identical file (same identity, same content) is loaded from another claim's path.</summary>
    ShareFromClaim = 1,

    /// <summary>The host process already has the same identity loaded; nothing is loaded.</summary>
    ShareFromHost = 2,
}

/// <summary>
/// One declared assembly file of one mod, resolved before anything is loaded.
/// Everything here is derived from bytes on disk and, for the host comparison, from
/// already-loaded assembly names - never from loading the candidate file.
/// </summary>
internal sealed class AssemblyClaim
{
    internal string ModId = "";

    internal AssemblyClaimRole Role;

    /// <summary>File name exactly as the manifest declares it.</summary>
    internal string DeclaredName = "";

    internal string Path = "";

    internal AssemblyFileIdentity Identity = new();

    internal long Length;

    /// <summary>
    /// <c>sha256:&lt;hex&gt;</c> when the runtime provides SHA-256, otherwise empty. An empty digest
    /// means "no fast filter available" and forces every comparison onto the full byte path; it
    /// is never replaced by a non-cryptographic fingerprint.
    /// </summary>
    internal string Digest = "";

    /// <summary>Path-free identity text, for the report only. Never used to decide equality.</summary>
    internal string IdentityText = "";

    internal AssemblyResolution Resolution = AssemblyResolution.Load;

    /// <summary>Absolute path of the file that will actually be loaded for this claim.</summary>
    internal string CanonicalPath = "";

    internal string CanonicalModId = "";

    /// <summary>
    /// Phase 1.1.2: the immutable staged copy of the exact bytes that were preflighted, or empty
    /// when the claim is satisfied some other way (host reuse). When it is set, this - and never
    /// <see cref="Path"/> - is what <c>Assembly.LoadFrom</c> is given, so a replacement of the
    /// declared file after the preflight cannot change what gets loaded.
    /// </summary>
    internal string StagedPath = "";

    /// <summary>
    /// Integrity record of the staged snapshot: always the <c>sha256:…</c> digest of the exact
    /// bytes that were written, or empty when the claim has no snapshot.
    ///
    /// <para>Phase 1.1.3: this is <b>not</b> the deduplication key. The key may collide (real hash
    /// collision, or the SHA-1 index form on a runtime without SHA-256) and is therefore never
    /// recorded here; recomputing this value immediately before the load is what refuses a
    /// snapshot edited after staging.</para>
    /// </summary>
    internal string SnapshotFingerprint = "";

    internal Assembly? HostAssembly;

    internal string RoleText => Role == AssemblyClaimRole.Entry ? "entry" : "private";

    /// <summary><c>sha256:…</c> or <c>unverified</c>: what the report prints for content.</summary>
    internal string ContentText => Digest.Length == 0 ? "unverified" : Digest;
}

/// <summary>
/// Reads declared assembly files into <see cref="AssemblyClaim"/> values.
///
/// The whole file is read once and kept only for the duration of one claim: the same buffer
/// answers both "is this a managed assembly and what is its identity?" and "what is its content
/// digest?", so a content-hash failure cannot be a separate, silently skipped path.
/// </summary>
internal static class AssemblyClaimReader
{
    /// <summary>
    /// Test seam: replaces the file opener so a short read, an early EOF or a file that grows
    /// while it is being read is reachable deterministically. <c>null</c> in production.
    /// </summary>
    internal static Func<string, Stream>? OpenForTests;

    internal static bool TryRead(
        string modId,
        AssemblyClaimRole role,
        string declaredName,
        string absolutePath,
        AssemblyStagingArea? staging,
        out AssemblyClaim claim,
        out string code,
        out string message)
    {
        claim = new AssemblyClaim();
        code = "";
        message = "";

        if (!File.Exists(absolutePath))
        {
            code = ErrorCodes.EntryAssemblyMissing;
            message = "missing " + (role == AssemblyClaimRole.Entry ? "entry" : "private") +
                      " assembly \"" + declaredName + "\"";
            return false;
        }

        byte[] bytes;
        string readError;
        if (!TryReadAll(absolutePath, out bytes, out readError))
        {
            code = ErrorCodes.AssemblyIdentityUnreadable;
            message = "\"" + declaredName + "\": " + readError;
            return false;
        }

        AssemblyFileIdentity identity;
        string identityError;
        if (!ManagedAssemblyReader.TryReadIdentity(bytes, out identity, out identityError))
        {
            code = ErrorCodes.AssemblyIdentityUnreadable;
            message = "\"" + declaredName + "\" is not a readable managed assembly: " + identityError;
            return false;
        }

        claim.ModId = modId;
        claim.Role = role;
        claim.DeclaredName = declaredName;
        claim.Path = absolutePath;
        claim.Identity = identity;
        claim.Length = bytes.Length;
        claim.Digest = ComputeDigest(bytes);
        claim.IdentityText = identity.Describe();
        claim.Resolution = AssemblyResolution.Load;
        claim.CanonicalPath = absolutePath;
        claim.CanonicalModId = modId;

        // Phase 1.1.2: snapshot the exact buffer that was just parsed and hashed, and prove the
        // staged copy matches it. From here on this claim is loaded from the snapshot, never from
        // `absolutePath`, so a replacement after this point cannot change what runs.
        if (staging != null)
        {
            string stageError;
            if (!staging.TryStage(claim, bytes, out stageError))
            {
                code = ErrorCodes.AssemblyStagingFailed;
                message = "\"" + declaredName + "\": " + stageError;
                return false;
            }
            claim.CanonicalPath = claim.StagedPath;
        }
        return true;
    }

    /// <summary>Bounded whole-file read; the cap keeps one claim in memory at a time.</summary>
    internal static bool TryReadAll(string path, out byte[] bytes, out string error)
    {
        bytes = Array.Empty<byte>();
        error = "";
        try
        {
            using (var stream = OpenRead(path))
            {
                var length = stream.Length;
                if (length < 0 || length > ManagedAssemblyReader.MaxAssemblyBytes)
                {
                    error = "the file is larger than the " +
                            (ManagedAssemblyReader.MaxAssemblyBytes / (1024 * 1024)) + " MiB preflight limit";
                    return false;
                }
                var buffer = new byte[(int)length];
                var total = 0;
                while (total < buffer.Length)
                {
                    var read = stream.Read(buffer, total, buffer.Length - total);
                    if (read <= 0)
                    {
                        break;
                    }
                    total += read;
                }
                if (total != buffer.Length)
                {
                    error = "the file changed while it was being read";
                    return false;
                }
                bytes = buffer;
                return true;
            }
        }
        catch (Exception ex)
        {
            // Type and a path-free reason only: an IO message quotes the absolute file name.
            error = PathScrubber.Describe(ex);
            return false;
        }
    }

    private static Stream OpenRead(string path)
    {
        var open = OpenForTests;
        return open != null ? open(path) : File.OpenRead(path);
    }

    /// <summary>
    /// Content digest - SHA-256 only, or nothing.
    ///
    /// Phase 1.1 fell back to FNV-1a 64 and then used digest-string equality to decide that two
    /// DLLs were "the same content". FNV-1a 64 is not collision resistant, so it could not carry
    /// that decision. This digest is now only ever a fast <i>negative</i> filter: see
    /// <see cref="ContentIdentity"/>, which performs the full byte comparison that actually
    /// authorizes sharing.
    /// </summary>
    internal static string ComputeDigest(byte[] bytes)
    {
        var overrideForTests = DigestOverrideForTests;
        if (overrideForTests != null)
        {
            return overrideForTests(bytes);
        }
        if (DisableSha256ForTests)
        {
            return "";
        }
        try
        {
            var hash = SHA256.HashData(bytes);
            return "sha256:" + ToHex(hash);
        }
        catch (Exception)
        {
            // No cryptographic digest on this runtime: the caller must fall back to the full
            // byte comparison, which is why the return value is empty rather than weaker.
            return "";
        }
    }

    /// <summary>Test seam: forces "SHA-256 unavailable" so the byte-comparison path is covered.</summary>
    internal static bool DisableSha256ForTests;

    /// <summary>
    /// Test seam: replaces the production digest computation with a caller-supplied function so
    /// a digest collision can be injected deterministically. <c>null</c> in production, where the
    /// only digest ever used is SHA-256 (see <see cref="ComputeDigest"/>). Tests must restore the
    /// previous value in a <c>finally</c> block.
    /// </summary>
    internal static Func<byte[], string>? DigestOverrideForTests;

    internal static string ToHex(byte[] value)
    {
        var sb = new StringBuilder(value.Length * 2);
        for (var i = 0; i < value.Length; i++)
        {
            sb.Append(value[i].ToString("x2"));
        }
        return sb.ToString();
    }
}

/// <summary>
/// The identity of an <b>already loaded</b> assembly, kept in exactly the shape the comparison
/// needs: simple name, version, culture, public-key bytes and the loaded instance itself.
///
/// Phase 1.1 reduced this to <c>HasPublicKey</c> (a boolean), which cannot distinguish two
/// differently-signed assemblies, and re-searched the AppDomain by name and version, which could
/// return the wrong instance. Both are fixed here: the key is kept as bytes and the instance is
/// carried along.
/// </summary>
internal sealed class HostAssemblyIdentity
{
    internal string SimpleName = "";

    internal string VersionText = "";

    internal string Culture = "";

    internal string Location = "";

    /// <summary>Full public key when the runtime exposes one; empty otherwise.</summary>
    internal byte[] PublicKey = Array.Empty<byte>();

    /// <summary>Public key token when the runtime exposes one; empty otherwise.</summary>
    internal byte[] PublicKeyToken = Array.Empty<byte>();

    /// <summary>The exact loaded instance, so a reuse cannot pick a look-alike assembly.</summary>
    internal Assembly? Assembly;

    internal string Describe()
    {
        return SimpleName + ", Version=" + VersionText +
               ", Culture=" + (Culture.Length == 0 ? "neutral" : Culture) +
               ", PublicKey=" + DescribeKey();
    }

    private string DescribeKey()
    {
        if (PublicKey.Length > 0)
        {
            return "full(" + PublicKey.Length + " bytes)";
        }
        if (PublicKeyToken.Length > 0)
        {
            return "token:" + AssemblyFileIdentity.ToHex(PublicKeyToken);
        }
        return "none";
    }

    /// <summary>Snapshot of every non-dynamic assembly the process already has loaded.</summary>
    internal static List<HostAssemblyIdentity> Snapshot()
    {
        var result = new List<HostAssemblyIdentity>();
        var loaded = AppDomain.CurrentDomain.GetAssemblies();
        for (var i = 0; i < loaded.Length; i++)
        {
            var identity = From(loaded[i]);
            if (identity != null)
            {
                result.Add(identity);
            }
        }
        return result;
    }

    /// <summary>
    /// Identity of one loaded assembly, or <c>null</c> when it is dynamic or its name cannot be
    /// read. Used by <see cref="Snapshot"/> and, in Phase 1.1.3, by the loader to check the
    /// instance <c>Assembly.LoadFrom</c> actually returned against the preflighted claim.
    /// </summary>
    internal static HostAssemblyIdentity? From(Assembly? assembly)
    {
        if (assembly == null || assembly.IsDynamic)
        {
            return null;
        }
        AssemblyName? name;
        try
        {
            name = assembly.GetName();
        }
        catch (Exception)
        {
            return null;
        }
        if (name == null || string.IsNullOrEmpty(name.Name))
        {
            return null;
        }

        var identity = new HostAssemblyIdentity();
        identity.SimpleName = name.Name!;
        identity.VersionText = NormalizeVersion(name.Version);
        identity.Culture = name.CultureName ?? "";
        if (identity.Culture == "neutral")
        {
            identity.Culture = "";
        }
        identity.Assembly = assembly;

        // The trimmed BCL has no AssemblyName.GetPublicKey() (verified by compiling against
        // the game's own reference assemblies), so the token is the only strong-name identity
        // the host can expose. The token is compared as bytes - never as a presence flag.
        try
        {
            var token = name.GetPublicKeyToken();
            identity.PublicKeyToken = token ?? Array.Empty<byte>();
        }
        catch (Exception)
        {
            identity.PublicKeyToken = Array.Empty<byte>();
        }

        try
        {
            identity.Location = assembly.Location ?? "";
        }
        catch (Exception)
        {
            identity.Location = "";
        }
        return identity;
    }

    internal static string NormalizeVersion(Version? version)
    {
        if (version == null)
        {
            return "0.0.0.0";
        }
        var build = version.Build < 0 ? 0 : version.Build;
        var revision = version.Revision < 0 ? 0 : version.Revision;
        return version.Major + "." + version.Minor + "." + build + "." + revision;
    }
}
