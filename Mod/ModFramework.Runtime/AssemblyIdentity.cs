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

    /// <summary><c>sha256:&lt;hex&gt;</c>, or <c>fnv1a64:&lt;len&gt;:&lt;hex&gt;</c> when SHA-256 is unavailable.</summary>
    internal string Digest = "";

    /// <summary>Path-free identity text used in the report and in the comparison.</summary>
    internal string IdentityText = "";

    internal string PublicKeyFingerprint = "";

    internal AssemblyResolution Resolution = AssemblyResolution.Load;

    /// <summary>Absolute path of the file that will actually be loaded for this claim.</summary>
    internal string CanonicalPath = "";

    internal string CanonicalModId = "";

    internal Assembly? HostAssembly;

    internal string RoleText => Role == AssemblyClaimRole.Entry ? "entry" : "private";
}

/// <summary>
/// Reads declared assembly files into <see cref="AssemblyClaim"/> values.
///
/// The whole file is read once and kept only for the duration of one claim: the same buffer
/// answers both "is this a managed assembly and what is its identity?" and "what is its
/// content digest?", so a content-hash failure cannot be a separate, silently skipped path.
/// </summary>
internal static class AssemblyClaimReader
{
    internal static bool TryRead(
        string modId,
        AssemblyClaimRole role,
        string declaredName,
        string absolutePath,
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

        var publicKeyFingerprint = Fingerprint(identity.PublicKey);
        claim.ModId = modId;
        claim.Role = role;
        claim.DeclaredName = declaredName;
        claim.Path = absolutePath;
        claim.Identity = identity;
        claim.Length = bytes.Length;
        claim.Digest = ComputeDigest(bytes);
        claim.PublicKeyFingerprint = publicKeyFingerprint;
        claim.IdentityText = identity.Describe(publicKeyFingerprint);
        claim.Resolution = AssemblyResolution.Load;
        claim.CanonicalPath = absolutePath;
        claim.CanonicalModId = modId;
        return true;
    }

    /// <summary>Bounded whole-file read; the cap keeps one claim in memory at a time.</summary>
    internal static bool TryReadAll(string path, out byte[] bytes, out string error)
    {
        bytes = Array.Empty<byte>();
        error = "";
        try
        {
            using (var stream = File.OpenRead(path))
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
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Content identity. SHA-256 when the runtime provides it, otherwise a deterministic
    /// FNV-1a 64 fingerprint over the same bytes. The fallback is not a weakening of the
    /// policy: content equality is still decided deterministically, and the digest is
    /// prefixed so the report says which one was used.
    /// </summary>
    internal static string ComputeDigest(byte[] bytes)
    {
        if (!DisableSha256ForTests)
        {
            try
            {
                var hash = SHA256.HashData(bytes);
                return "sha256:" + ToHex(hash);
            }
            catch (Exception)
            {
                // fall through to the always-available fingerprint
            }
        }
        return "fnv1a64:" + bytes.Length.ToString() + ":" + Fnv1a64(bytes).ToString("x16");
    }

    /// <summary>Test seam: forces the digest fallback so the fallback path is covered.</summary>
    internal static bool DisableSha256ForTests;

    internal static string Fingerprint(byte[] publicKey)
    {
        if (publicKey.Length == 0)
        {
            return "none";
        }
        if (!DisableSha256ForTests)
        {
            try
            {
                return "sha256:" + ToHex(SHA256.HashData(publicKey));
            }
            catch (Exception)
            {
                // fall through
            }
        }
        return "fnv1a64:" + Fnv1a64(publicKey).ToString("x16");
    }

    internal static string ToHex(byte[] value)
    {
        var sb = new StringBuilder(value.Length * 2);
        for (var i = 0; i < value.Length; i++)
        {
            sb.Append(value[i].ToString("x2"));
        }
        return sb.ToString();
    }

    private static ulong Fnv1a64(byte[] bytes)
    {
        var hash = 14695981039346656037UL;
        for (var i = 0; i < bytes.Length; i++)
        {
            hash ^= bytes[i];
            hash *= 1099511628211UL;
        }
        return hash;
    }
}

/// <summary>
/// The identity of an <b>already loaded</b> assembly, reduced to the same shape as
/// <see cref="AssemblyClaim"/> so the two can be compared without ever loading the file.
/// </summary>
internal sealed class HostAssemblyIdentity
{
    internal string SimpleName = "";

    internal string VersionText = "";

    internal string Culture = "";

    internal bool HasPublicKey;

    internal string Location = "";

    internal string Describe()
    {
        return SimpleName + ", Version=" + VersionText +
               ", Culture=" + (Culture.Length == 0 ? "neutral" : Culture) +
               ", PublicKey=" + (HasPublicKey ? "present" : "none");
    }

    /// <summary>Snapshot of every non-dynamic assembly the process already has loaded.</summary>
    internal static List<HostAssemblyIdentity> Snapshot()
    {
        var result = new List<HostAssemblyIdentity>();
        var loaded = AppDomain.CurrentDomain.GetAssemblies();
        for (var i = 0; i < loaded.Length; i++)
        {
            var assembly = loaded[i];
            if (assembly == null || assembly.IsDynamic)
            {
                continue;
            }
            AssemblyName? name;
            try
            {
                name = assembly.GetName();
            }
            catch (Exception)
            {
                continue;
            }
            if (name == null || string.IsNullOrEmpty(name.Name))
            {
                continue;
            }

            var identity = new HostAssemblyIdentity();
            identity.SimpleName = name.Name!;
            identity.VersionText = NormalizeVersion(name.Version);
            identity.Culture = name.CultureName ?? "";
            if (identity.Culture == "neutral")
            {
                identity.Culture = "";
            }
            try
            {
                var token = name.GetPublicKeyToken();
                identity.HasPublicKey = token != null && token.Length > 0;
            }
            catch (Exception)
            {
                identity.HasPublicKey = false;
            }
            try
            {
                identity.Location = assembly.Location ?? "";
            }
            catch (Exception)
            {
                identity.Location = "";
            }
            result.Add(identity);
        }
        return result;
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
