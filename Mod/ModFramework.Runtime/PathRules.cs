namespace ModFramework.Runtime;

/// <summary>
/// Path helpers shared by the manifest-driven loader and the assembly preflight.
/// Separated so both sides apply the exact same containment rule.
/// </summary>
internal static class PathRules
{
    /// <summary>Normalizes separators; comparison is ordinal so the result cannot depend on the OS.</summary>
    internal static string Normalize(string path)
    {
        return path.Replace('\\', '/');
    }

    /// <summary>True when <paramref name="candidate"/> is inside <paramref name="root"/>.</summary>
    internal static bool IsInside(string root, string candidate)
    {
        var normalizedRoot = Normalize(root);
        var normalizedCandidate = Normalize(candidate);
        if (!normalizedRoot.EndsWith("/", StringComparison.Ordinal))
        {
            normalizedRoot += "/";
        }
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.Ordinal);
    }
}
