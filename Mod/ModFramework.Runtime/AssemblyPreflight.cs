using System.Reflection;

namespace ModFramework.Runtime;

/// <summary>
/// A rejection decided by one of the two pre-load gates (version compatibility, assembly
/// identity), carried as a stable code plus a path-free message.
/// </summary>
internal sealed class GateProblem
{
    internal GateProblem(string code, string message)
    {
        Code = code;
        Message = message;
    }

    internal string Code { get; }

    internal string Message { get; }
}

/// <summary>
/// Outcome of the global assembly preflight: which mods are rejected, and for the rest,
/// which file (if any) the loader is allowed to load for every declared assembly.
/// </summary>
internal sealed class AssemblyPreflightResult
{
    private readonly Dictionary<string, List<AssemblyClaim>> _claims =
        new(StringComparer.OrdinalIgnoreCase);

    internal Dictionary<string, GateProblem> Rejections { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Report lines, already path-free, emitted before any assembly is loaded.</summary>
    internal List<string> Lines { get; } = new();

    internal int DeclaredFiles { get; set; }

    internal int ConflictGroups { get; set; }

    internal int SharedClaims { get; set; }

    internal int HostSharedClaims { get; set; }

    internal int HostUnverifiedClaims { get; set; }

    internal bool AnyConflict => ConflictGroups > 0;

    internal void SetClaims(string modId, List<AssemblyClaim> claims)
    {
        _claims[modId] = claims;
    }

    internal List<AssemblyClaim>? GetClaims(string modId)
    {
        List<AssemblyClaim>? claims;
        return _claims.TryGetValue(modId, out claims) ? claims : null;
    }

    internal AssemblyClaim? FindClaim(string modId, string declaredName)
    {
        var claims = GetClaims(modId);
        if (claims == null)
        {
            return null;
        }
        for (var i = 0; i < claims.Count; i++)
        {
            if (string.Equals(claims[i].DeclaredName, declaredName, StringComparison.OrdinalIgnoreCase))
            {
                return claims[i];
            }
        }
        return null;
    }

    internal string Summary()
    {
        return "assemblies=" + DeclaredFiles +
               " conflicts=" + ConflictGroups +
               " shared=" + SharedClaims +
               " host-shared=" + HostSharedClaims +
               " host-unverified=" + HostUnverifiedClaims +
               " rejected=" + Rejections.Count;
    }
}

/// <summary>
/// Global assembly identity preflight.
///
/// Runs to completion <b>before</b> the loader calls <c>Assembly.LoadFrom</c> for any mod, and
/// only reads bytes: no assembly is loaded, no type is resolved, no static constructor runs.
///
/// Rules (documented in <c>docs/modding/manifest-spec.md</c> §3.3 and
/// <c>docs/audit/phase1.1-report.md</c>):
///
/// 1. Every declared <c>entryAssembly</c> / <c>privateAssemblies</c> file must exist, be
///    readable and be a managed assembly, otherwise the owning mod is rejected
///    (<c>MF0201</c> / <c>MF0205</c>).
/// 2. A simple name already provided by an assembly the host has loaded is authoritative:
///    same identity and same content -&gt; the mod reuses the host instance (<c>ShareFromHost</c>);
///    same identity but unreadable host bytes -&gt; reuse with an explicit warning; anything
///    else -&gt; rejected (<c>MF0207</c>).
/// 3. Otherwise all claims with the same simple name must agree on identity <i>and</i> content.
///    If they do, one representative is chosen deterministically and loaded once. If they do
///    not, every owner is rejected (<c>MF0206</c>) - never "first loaded wins".
/// </summary>
internal static class AssemblyPreflight
{
    internal static AssemblyPreflightResult Analyze(List<ModManifest> eligible)
    {
        var result = new AssemblyPreflightResult();

        // Deterministic mod order; the loader's own plan is already sorted, but the preflight
        // must not depend on its caller.
        var ordered = new List<ModManifest>(eligible);
        SortMods(ordered);

        var all = new List<AssemblyClaim>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var manifest = ordered[i];
            var claims = new List<AssemblyClaim>();
            if (ReadClaims(manifest, claims, result))
            {
                result.SetClaims(manifest.Id, claims);
                for (var c = 0; c < claims.Count; c++)
                {
                    all.Add(claims[c]);
                }
            }
        }

        result.DeclaredFiles = all.Count;

        var host = HostAssemblyIdentity.Snapshot();
        var hostByName = new Dictionary<string, List<HostAssemblyIdentity>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < host.Count; i++)
        {
            var identity = host[i];
            List<HostAssemblyIdentity>? list;
            if (!hostByName.TryGetValue(identity.SimpleName, out list))
            {
                list = new List<HostAssemblyIdentity>();
                hostByName[identity.SimpleName] = list;
            }
            list.Add(identity);
        }
        foreach (var pair in hostByName)
        {
            pair.Value.Sort(CompareHostIdentities);
        }

        var groups = new Dictionary<string, List<AssemblyClaim>>(StringComparer.OrdinalIgnoreCase);
        var groupNames = new List<string>();
        for (var i = 0; i < all.Count; i++)
        {
            var claim = all[i];
            List<AssemblyClaim>? list;
            if (!groups.TryGetValue(claim.Identity.Name, out list))
            {
                list = new List<AssemblyClaim>();
                groups[claim.Identity.Name] = list;
                groupNames.Add(claim.Identity.Name);
            }
            list.Add(claim);
        }
        groupNames.Sort(StringComparer.Ordinal);

        for (var i = 0; i < groupNames.Count; i++)
        {
            var name = groupNames[i];
            var group = groups[name];
            List<HostAssemblyIdentity>? hostMatches;
            if (hostByName.TryGetValue(name, out hostMatches) && hostMatches != null && hostMatches.Count > 0)
            {
                ResolveAgainstHost(name, group, hostMatches, result);
            }
            else
            {
                ResolveWithinMods(name, group, result);
            }
        }

        result.Lines.Insert(0, "preflight=" + result.Summary());
        return result;
    }

    // --------------------------------------------------------------- reading

    private static bool ReadClaims(ModManifest manifest, List<AssemblyClaim> claims, AssemblyPreflightResult result)
    {
        var entryPath = Path.Combine(manifest.Directory, manifest.EntryAssembly);
        if (!PathRules.IsInside(manifest.Directory, entryPath))
        {
            Reject(result, manifest.Id, ErrorCodes.ManifestEntryOutsidePackage,
                "entryAssembly resolves outside the mod package");
            return false;
        }

        AssemblyClaim claim;
        string code;
        string message;
        if (!AssemblyClaimReader.TryRead(manifest.Id, AssemblyClaimRole.Entry, manifest.EntryAssembly,
                entryPath, out claim, out code, out message))
        {
            Reject(result, manifest.Id, code, message);
            return false;
        }
        claims.Add(claim);

        for (var i = 0; i < manifest.PrivateAssemblies.Count; i++)
        {
            var declared = manifest.PrivateAssemblies[i];
            var path = Path.Combine(manifest.Directory, declared);
            if (!PathRules.IsInside(manifest.Directory, path))
            {
                Reject(result, manifest.Id, ErrorCodes.ManifestEntryOutsidePackage,
                    "private assembly \"" + declared + "\" resolves outside the mod package");
                return false;
            }
            if (!AssemblyClaimReader.TryRead(manifest.Id, AssemblyClaimRole.Private, declared, path,
                    out claim, out code, out message))
            {
                Reject(result, manifest.Id, code, message);
                return false;
            }
            claims.Add(claim);
        }
        return true;
    }

    // ------------------------------------------------------ host comparison

    private static void ResolveAgainstHost(
        string simpleName,
        List<AssemblyClaim> group,
        List<HostAssemblyIdentity> hostMatches,
        AssemblyPreflightResult result)
    {
        for (var i = 0; i < group.Count; i++)
        {
            var claim = group[i];
            HostAssemblyIdentity? match = FindHostMatch(hostMatches, claim);
            if (match == null)
            {
                var hostVersions = DescribeHostVersions(hostMatches);
                Reject(result, claim.ModId, ErrorCodes.HostAssemblyConflict,
                    claim.RoleText + " assembly \"" + claim.DeclaredName + "\" is \"" + claim.IdentityText +
                    "\", which collides with the already loaded host assembly " +
                    "\"" + simpleName + "\" (" + hostVersions + ")");
                result.Lines.Add("preflight-conflict=" + simpleName + " kind=host owner=" + claim.ModId +
                                 " role=" + claim.RoleText + " identity=" + claim.IdentityText);
                continue;
            }

            var content = VerifyHostContent(match, claim);
            if (content == HostContentCheck.Different)
            {
                Reject(result, claim.ModId, ErrorCodes.HostAssemblyConflict,
                    claim.RoleText + " assembly \"" + claim.DeclaredName + "\" has the same identity \"" +
                    claim.IdentityText + "\" as a host assembly but different content (" +
                    claim.Digest + ")");
                result.Lines.Add("preflight-conflict=" + simpleName + " kind=host-content owner=" + claim.ModId +
                                 " role=" + claim.RoleText + " identity=" + claim.IdentityText);
                continue;
            }

            claim.Resolution = AssemblyResolution.ShareFromHost;
            // Point at the file that is actually in use (the host's own image) when it is
            // known, so the report's assembly= line never names a file that was not loaded.
            claim.CanonicalPath = string.IsNullOrEmpty(match.Location) ? claim.Path : match.Location;
            claim.CanonicalModId = "host";
            claim.HostAssembly = FindLoadedAssembly(match);
            result.HostSharedClaims++;
            result.Lines.Add("preflight-host-share=" + simpleName + " owner=" + claim.ModId +
                             " role=" + claim.RoleText + " content=" + (content == HostContentCheck.Same ? "same" : "unverified"));
            if (content == HostContentCheck.Unverifiable)
            {
                result.HostUnverifiedClaims++;
                result.Lines.Add("preflight-warning=" + simpleName +
                                 " the loaded host assembly has no readable file, so content equality could not be proven");
            }
        }
    }

    private enum HostContentCheck
    {
        Same = 0,
        Different = 1,
        Unverifiable = 2,
    }

    private static HostContentCheck VerifyHostContent(HostAssemblyIdentity host, AssemblyClaim claim)
    {
        var location = host.Location;
        if (string.IsNullOrEmpty(location) || !File.Exists(location))
        {
            return HostContentCheck.Unverifiable;
        }
        try
        {
            byte[] bytes;
            string error;
            if (!AssemblyClaimReader.TryReadAll(location, out bytes, out error))
            {
                return HostContentCheck.Unverifiable;
            }
            var digest = AssemblyClaimReader.ComputeDigest(bytes);
            return string.Equals(digest, claim.Digest, StringComparison.Ordinal)
                ? HostContentCheck.Same
                : HostContentCheck.Different;
        }
        catch (Exception)
        {
            return HostContentCheck.Unverifiable;
        }
    }

    private static Assembly? FindLoadedAssembly(HostAssemblyIdentity match)
    {
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
            if (!string.Equals(name.Name, match.SimpleName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!string.Equals(HostAssemblyIdentity.NormalizeVersion(name.Version), match.VersionText,
                    StringComparison.Ordinal))
            {
                continue;
            }
            return assembly;
        }
        return null;
    }

    private static HostAssemblyIdentity? FindHostMatch(List<HostAssemblyIdentity> hostMatches, AssemblyClaim claim)
    {
        for (var i = 0; i < hostMatches.Count; i++)
        {
            var host = hostMatches[i];
            if (!string.Equals(host.SimpleName, claim.Identity.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!string.Equals(host.VersionText, claim.Identity.VersionText, StringComparison.Ordinal))
            {
                continue;
            }
            if (!SameCulture(host.Culture, claim.Identity.Culture))
            {
                continue;
            }
            // A strong-name mismatch is a hard difference; two signed sides cannot be compared
            // for *token* equality without SHA-1, so they are treated as matching here and the
            // content check above decides. Both-unsigned is the normal mod case.
            if (host.HasPublicKey != claim.Identity.IsStrongNamed)
            {
                continue;
            }
            return host;
        }
        return null;
    }

    private static string DescribeHostVersions(List<HostAssemblyIdentity> hostMatches)
    {
        var text = "";
        for (var i = 0; i < hostMatches.Count; i++)
        {
            if (i > 0)
            {
                text += "; ";
            }
            text += "Version=" + hostMatches[i].VersionText;
        }
        return text.Length == 0 ? "unknown version" : text;
    }

    private static int CompareHostIdentities(HostAssemblyIdentity a, HostAssemblyIdentity b)
    {
        var byVersion = string.Compare(a.VersionText, b.VersionText, StringComparison.Ordinal);
        if (byVersion != 0)
        {
            return byVersion;
        }
        return string.Compare(a.Location, b.Location, StringComparison.Ordinal);
    }

    // ----------------------------------------------------- cross-mod compare

    private static void ResolveWithinMods(string simpleName, List<AssemblyClaim> group, AssemblyPreflightResult result)
    {
        if (group.Count <= 1)
        {
            return;
        }

        var same = true;
        var first = group[0];
        for (var i = 1; i < group.Count; i++)
        {
            if (!string.Equals(group[i].IdentityText, first.IdentityText, StringComparison.Ordinal) ||
                !string.Equals(group[i].Digest, first.Digest, StringComparison.Ordinal))
            {
                same = false;
                break;
            }
        }

        if (same)
        {
            var ordered = new List<AssemblyClaim>(group);
            ordered.Sort(CompareClaims);
            var canonical = ordered[0];
            for (var i = 0; i < group.Count; i++)
            {
                var claim = group[i];
                claim.CanonicalPath = canonical.Path;
                claim.CanonicalModId = canonical.ModId;
                claim.Resolution = ReferenceEquals(claim, canonical)
                    ? AssemblyResolution.Load
                    : AssemblyResolution.ShareFromClaim;
                if (claim.Resolution == AssemblyResolution.ShareFromClaim)
                {
                    result.SharedClaims++;
                }
            }
            result.Lines.Add("preflight-shared=" + simpleName + " canonical=" + canonical.ModId +
                             " representative=" + canonical.DeclaredName + " owners=" + group.Count);
            return;
        }

        result.ConflictGroups++;
        var owners = "";
        var identities = "";
        for (var i = 0; i < group.Count; i++)
        {
            var claim = group[i];
            if (i > 0)
            {
                owners += ",";
                identities += ",";
            }
            owners += claim.ModId + "(" + claim.RoleText + ":" + claim.DeclaredName + ")";
            identities += claim.IdentityText + " content=" + claim.Digest;
            Reject(result, claim.ModId, ErrorCodes.AssemblyIdentityConflict,
                "assembly \"" + simpleName + "\" is declared by more than one mod with a different " +
                "identity or different content: " + claim.IdentityText + " (" + claim.Digest + ")");
        }
        result.Lines.Add("preflight-conflict=" + simpleName + " kind=mod owners=" + owners +
                         " identities=" + identities);
    }

    private static int CompareClaims(AssemblyClaim a, AssemblyClaim b)
    {
        var byMod = string.Compare(a.ModId, b.ModId, StringComparison.Ordinal);
        if (byMod != 0)
        {
            return byMod;
        }
        var byName = string.Compare(a.DeclaredName, b.DeclaredName, StringComparison.Ordinal);
        if (byName != 0)
        {
            return byName;
        }
        return string.Compare(PathRules.Normalize(a.Path), PathRules.Normalize(b.Path), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private static void SortMods(List<ModManifest> mods)
    {
        mods.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.Ordinal));
    }

    private static bool SameCulture(string a, string b)
    {
        var left = a.Length == 0 || a == "neutral" ? "" : a;
        var right = b.Length == 0 || b == "neutral" ? "" : b;
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static void Reject(AssemblyPreflightResult result, string modId, string code, string message)
    {
        if (result.Rejections.ContainsKey(modId))
        {
            return;
        }
        result.Rejections[modId] = new GateProblem(code, message);
    }
}
