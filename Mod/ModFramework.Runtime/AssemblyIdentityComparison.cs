namespace ModFramework.Runtime;

/// <summary>
/// The one identity rule used everywhere a "is this the same assembly?" question is asked.
///
/// An assembly identity is <c>(Name, Version, Culture, public-key identity)</c>, per ECMA-335
/// II.6.2.1.1 and the CLR binder:
/// <list type="bullet">
///   <item><b>Name</b> compares case-insensitively (the binder does).</item>
///   <item><b>Version</b> compares as the exact four-part value; a different version is a
///     different identity, never a compatible one.</item>
///   <item><b>Culture</b> compares case-insensitively with <c>neutral</c> normalized to empty. A
///     French satellite is not the neutral assembly.</item>
///   <item><b>Public key</b> compares <i>bytes</i>. A full key is compared with a full key, a
///     token with a token. A mixed pair is <b>not</b> "probably equal": the token cannot be
///     derived from the key without SHA-1 and guessing is exactly what must not happen, so a
///     mixed pair is reported as "not proven equal" (and therefore not shareable).</item>
/// </list>
///
/// Phase 1.1 compared only <c>has a public key</c> on both sides, which makes two assemblies
/// signed with different keys indistinguishable. That is the defect this type removes.
/// </summary>
internal static class AssemblyIdentityComparison
{
    internal static bool SameIdentity(AssemblyFileIdentity left, AssemblyFileIdentity right, out string difference)
    {
        if (!SameName(left.Name, right.Name))
        {
            difference = "different simple name";
            return false;
        }
        if (!string.Equals(left.VersionText, right.VersionText, StringComparison.Ordinal))
        {
            difference = "different version";
            return false;
        }
        if (!SameCulture(left.Culture, right.Culture))
        {
            difference = "different culture";
            return false;
        }
        return SameFileKey(left, right, out difference);
    }

    internal static bool SameIdentity(AssemblyFileIdentity left, HostAssemblyIdentity right, out string difference)
    {
        if (!SameName(left.Name, right.SimpleName))
        {
            difference = "different simple name";
            return false;
        }
        if (!string.Equals(left.VersionText, right.VersionText, StringComparison.Ordinal))
        {
            difference = "different version";
            return false;
        }
        if (!SameCulture(left.Culture, right.Culture))
        {
            difference = "different culture";
            return false;
        }

        // Prefer full-key comparison; fall back to tokens only when neither side has a full key.
        if (left.PublicKey.Length > 0 && right.PublicKey.Length > 0)
        {
            if (BytesEqual(left.PublicKey, right.PublicKey))
            {
                difference = "";
                return true;
            }
            difference = "different public key";
            return false;
        }
        if (left.PublicKey.Length == 0 && left.PublicKeyToken.Length == 0 &&
            right.PublicKey.Length == 0 && right.PublicKeyToken.Length == 0)
        {
            difference = "";
            return true;        // both unsigned
        }
        if (left.PublicKeyToken.Length > 0 && right.PublicKeyToken.Length > 0)
        {
            if (BytesEqual(left.PublicKeyToken, right.PublicKeyToken))
            {
                difference = "";
                return true;
            }
            difference = "different public key token";
            return false;
        }

        // One side has a full key and the other only a token, or a representation is missing
        // entirely: equality cannot be proven, and an unproven identity is not shared.
        difference = "public key representation cannot be compared (full key vs token)";
        return false;
    }

    private static bool SameFileKey(AssemblyFileIdentity left, AssemblyFileIdentity right, out string difference)
    {
        if (left.PublicKey.Length > 0 && right.PublicKey.Length > 0)
        {
            if (BytesEqual(left.PublicKey, right.PublicKey))
            {
                difference = "";
                return true;
            }
            difference = "different public key";
            return false;
        }
        if (left.PublicKey.Length == 0 && left.PublicKeyToken.Length == 0 &&
            right.PublicKey.Length == 0 && right.PublicKeyToken.Length == 0)
        {
            difference = "";
            return true;
        }
        if (left.PublicKeyToken.Length > 0 && right.PublicKeyToken.Length > 0)
        {
            if (BytesEqual(left.PublicKeyToken, right.PublicKeyToken))
            {
                difference = "";
                return true;
            }
            difference = "different public key token";
            return false;
        }

        difference = "public key representation cannot be compared (full key vs token)";
        return false;
    }

    private static bool SameName(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameCulture(string left, string right)
    {
        return string.Equals(NormalizeCulture(left), NormalizeCulture(right), StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizeCulture(string culture)
    {
        if (string.IsNullOrEmpty(culture) || string.Equals(culture, "neutral", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }
        return culture;
    }

    internal static bool BytesEqual(byte[] left, byte[] right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left == null || right == null || left.Length != right.Length)
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
