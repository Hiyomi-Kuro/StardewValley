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

/// <summary>What one preflight finding is about, so the loader can decide whether it still holds.</summary>
internal enum PreflightEventKind
{
    /// <summary>A simple name declared by several mods with different identity or content.</summary>
    ModConflict = 0,

    /// <summary>A declaration whose identity matches no loaded host assembly.</summary>
    HostConflict = 1,

    /// <summary>Content equality with the loaded host assembly could not be proven.</summary>
    HostUnverifiable = 2,

    /// <summary>Several loaded host assemblies share the identity, so the reuse target is ambiguous.</summary>
    HostAmbiguous = 3,

    /// <summary>One representative file is shared by several mods.</summary>
    Shared = 4,

    /// <summary>The mod reuses the host's already loaded instance.</summary>
    HostShare = 5,
}

/// <summary>
/// One preflight finding, kept as data instead of a pre-rendered line.
///
/// The preflight runs several times while the loader removes doomed mods, and a finding from an
/// early round is <b>not</b> automatically still true: a mod rejected as a conflict co-owner can
/// become healthy once the mod it conflicted with is excluded for an unrelated reason. Rendering
/// at the end, filtered by the final outcome, is what keeps the report truthful without also
/// losing the evidence for mods that really were rejected.
/// </summary>
internal sealed class PreflightEvent
{
    internal PreflightEventKind Kind;

    internal string SimpleName = "";

    /// <summary>Owning mod ids, in the preflight's deterministic order.</summary>
    internal List<string> Owners { get; } = new();

    /// <summary>Owner text for the rendered line, parallel to <see cref="Owners"/>.</summary>
    internal List<string> OwnerLabels { get; } = new();

    /// <summary>Identity text per owner, for the rendered line.</summary>
    internal string Identities = "";

    /// <summary>Extra detail: a reason, a candidate count or a content verdict.</summary>
    internal string Detail = "";

    /// <summary>Stable sort key: identical findings from different rounds collapse onto it.</summary>
    internal string Key => Kind + "|" + SimpleName + "|" + Identities + "|" + Join(OwnerLabels);

    private static string Join(List<string> values)
    {
        var text = "";
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                text += ",";
            }
            text += values[i];
        }
        return text;
    }
}

/// <summary>
/// Outcome of one global assembly preflight pass: which mods are rejected, and for the rest,
/// which file (if any) the loader is allowed to load for every declared assembly.
///
/// <see cref="AssemblyPreflight.Analyze"/> is a pure function of its input list: it does no
/// dependency propagation. Turning rejections into "and so are their dependents" - and
/// re-running until stable - is the loader's job (see <c>Loader.RunCore</c>).
/// </summary>
internal sealed class AssemblyPreflightResult
{
    private readonly Dictionary<string, List<AssemblyClaim>> _claims =
        new(StringComparer.OrdinalIgnoreCase);

    internal Dictionary<string, GateProblem> Rejections { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Findings of this pass, rendered later by the loader.</summary>
    internal List<PreflightEvent> Events { get; } = new();

    internal int DeclaredFiles { get; set; }

    /// <summary>The snapshot area this pass staged into, so the report can state its cost.</summary>
    internal AssemblyStagingArea? Staging { get; set; }

    /// <summary><c>sha256+full-bytes</c> or <c>full-bytes-only</c>: what a content match rests on.</summary>
    internal string ContentPolicy { get; set; } = "sha256+full-bytes";

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

    private static string? Join(List<string> values)
    {
        var text = "";
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                text += ",";
            }
            text += values[i];
        }
        return text;
    }
}

/// <summary>
/// Global assembly identity preflight.
///
/// Runs to completion <b>before</b> the loader calls <c>Assembly.LoadFrom</c> for any mod, and
/// only reads bytes: no assembly is loaded, no type is resolved, no static constructor runs.
///
/// Rules (documented in <c>docs/modding/manifest-spec.md</c> §3.4 and
/// <c>docs/audit/phase1.1.1-report.md</c>):
///
/// 1. Every declared <c>entryAssembly</c> / <c>privateAssemblies</c> file must exist, be
///    readable and be a managed assembly, otherwise the owning mod is rejected
///    (<c>MF0201</c> / <c>MF0205</c>).
/// 2. A simple name already provided by an assembly the host has loaded is authoritative, but
///    only when exactly one loaded assembly has the <i>same identity</i> (name, version,
///    culture, public-key bytes) <i>and</i> its file content is proven byte-identical
///    (<c>ShareFromHost</c>). An ambiguous candidate set, an unreadable host image or
///    unprovable content is a rejection (<c>MF0207</c>) - never a silent reuse.
/// 3. Otherwise all claims with the same simple name must agree on identity <b>and</b> content,
///    where "same content" always means a full byte comparison (a digest is only a negative
///    filter). If they do, one representative is chosen deterministically and loaded once. If
///    they do not, every owner is rejected (<c>MF0206</c>) - never "first loaded wins".
/// </summary>
internal static class AssemblyPreflight
{
    /// <summary>
    /// Test seam: replaces the host snapshot with a caller-supplied list so host-identity cases
    /// that cannot be produced in a desktop test process (unreadable location, several loaded
    /// copies, a different public key) become reachable. <c>null</c> in production, where the
    /// snapshot always comes from <see cref="HostAssemblyIdentity.Snapshot"/>. Tests restore it
    /// in a <c>finally</c> block.
    /// </summary>
    internal static List<HostAssemblyIdentity>? HostSnapshotForTests;

    internal static AssemblyPreflightResult Analyze(List<ModManifest> eligible, AssemblyStagingArea? staging = null)
    {
        var result = new AssemblyPreflightResult();
        result.Staging = staging;

        // Deterministic mod order; the loader's own plan is already sorted, but the preflight
        // must not depend on its caller.
        var ordered = new List<ModManifest>(eligible);
        SortMods(ordered);

        var all = new List<AssemblyClaim>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var manifest = ordered[i];
            var claims = new List<AssemblyClaim>();
            if (ReadClaims(manifest, claims, result, staging))
            {
                result.SetClaims(manifest.Id, claims);
                for (var c = 0; c < claims.Count; c++)
                {
                    all.Add(claims[c]);
                }
            }
        }

        result.DeclaredFiles = all.Count;
        result.ContentPolicy = DescribeContentPolicy(all);

        var host = HostSnapshotForTests ?? HostAssemblyIdentity.Snapshot();
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

        return result;
    }

    /// <summary>
    /// What actually authorizes a content match, stated in the report so a reader never has to
    /// guess whether a weak digest was involved.
    /// </summary>
    private static string DescribeContentPolicy(List<AssemblyClaim> claims)
    {
        if (claims.Count == 0)
        {
            return "sha256+full-bytes";
        }
        for (var i = 0; i < claims.Count; i++)
        {
            if (claims[i].Digest.Length == 0)
            {
                return "full-bytes-only";
            }
        }
        // A digest is only a negative filter; equality always needs the byte comparison.
        return "sha256+full-bytes";
    }

    // --------------------------------------------------------------- reading

    private static bool ReadClaims(
        ModManifest manifest,
        List<AssemblyClaim> claims,
        AssemblyPreflightResult result,
        AssemblyStagingArea? staging)
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
                entryPath, staging, out claim, out code, out message))
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
                    staging, out claim, out code, out message))
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

            // 1. Identity: every loaded assembly with the same simple name is a *candidate*;
            //    only one with the exact identity may be reused.
            var candidates = new List<HostAssemblyIdentity>();
            var differences = "";
            for (var h = 0; h < hostMatches.Count; h++)
            {
                string difference;
                if (AssemblyIdentityComparison.SameIdentity(claim.Identity, hostMatches[h], out difference))
                {
                    candidates.Add(hostMatches[h]);
                }
                else if (differences.Length == 0)
                {
                    differences = difference;
                }
            }

            if (candidates.Count == 0)
            {
                Reject(result, claim.ModId, ErrorCodes.HostAssemblyConflict,
                    claim.RoleText + " assembly \"" + claim.DeclaredName + "\" is \"" + claim.IdentityText +
                    "\", which collides with the already loaded host assembly \"" + simpleName + "\" (" +
                    DescribeHostVersions(hostMatches) + "; " +
                    (differences.Length == 0 ? "different identity" : differences) + ")");
                AddEvent(result, PreflightEventKind.HostConflict, simpleName, claim, "", claim.IdentityText);
                continue;
            }

            if (candidates.Count > 1)
            {
                // Several loaded assemblies share this identity. Picking one would make the
                // result depend on enumeration order, and each copy could differ in content.
                Reject(result, claim.ModId, ErrorCodes.HostAssemblyConflict,
                    claim.RoleText + " assembly \"" + claim.DeclaredName + "\" matches " + candidates.Count +
                    " already loaded host assemblies named \"" + simpleName +
                    "\" with the same identity, so the host instance to reuse is ambiguous");
                AddEvent(result, PreflightEventKind.HostAmbiguous, simpleName, claim,
                    "candidates=" + candidates.Count, simpleName);
                continue;
            }

            var match = candidates[0];

            // 2. Content: reuse requires proof, not resemblance.
            string contentReason;
            var verdict = VerifyHostContent(match, claim, out contentReason);
            if (verdict != ContentVerdict.Same)
            {
                Reject(result, claim.ModId, ErrorCodes.HostAssemblyConflict,
                    claim.RoleText + " assembly \"" + claim.DeclaredName + "\" has identity \"" +
                    claim.IdentityText + "\" matching the loaded host assembly \"" + simpleName +
                    "\", but content equality could not be " +
                    (verdict == ContentVerdict.Different ? "ruled out: " : "proven: ") + contentReason);
                AddEvent(result, PreflightEventKind.HostUnverifiable, simpleName, claim,
                    "reason=" + (verdict == ContentVerdict.Different ? "content-differs" : "content-unverifiable"),
                    simpleName);
                continue;
            }

            if (match.Assembly == null)
            {
                // Identity and content agree, but there is no instance to hand to the mod.
                Reject(result, claim.ModId, ErrorCodes.HostAssemblyConflict,
                    claim.RoleText + " assembly \"" + claim.DeclaredName + "\" matches the loaded host assembly \"" +
                    simpleName + "\" but that instance is no longer available");
                AddEvent(result, PreflightEventKind.HostUnverifiable, simpleName, claim,
                    "reason=instance-unavailable", simpleName);
                continue;
            }

            claim.Resolution = AssemblyResolution.ShareFromHost;
            // Point at the file that is actually in use (the host's own image) when it is
            // known, so the report's assembly= line never names a file that was not loaded.
            claim.CanonicalPath = string.IsNullOrEmpty(match.Location) ? claim.Path : match.Location;
            claim.CanonicalModId = "host";
            claim.HostAssembly = match.Assembly;
            AddEvent(result, PreflightEventKind.HostShare, simpleName, claim,
                "role=" + claim.RoleText + " content=same", match.SimpleName);
        }
    }

    private static ContentVerdict VerifyHostContent(
        HostAssemblyIdentity host,
        AssemblyClaim claim,
        out string reason)
    {
        reason = "";
        var location = host.Location;
        if (string.IsNullOrEmpty(location))
        {
            reason = "the loaded host assembly has no readable file location";
            return ContentVerdict.Unverifiable;
        }
        if (!File.Exists(location))
        {
            reason = "the loaded host assembly's file is no longer present";
            return ContentVerdict.Unverifiable;
        }

        long hostLength;
        string lengthError;
        if (!ContentIdentity.TryGetLength(location, out hostLength, out lengthError))
        {
            reason = lengthError;
            return ContentVerdict.Unverifiable;
        }
        return ContentIdentity.CompareFiles(location, hostLength, claim.Path, claim.Length, out reason);
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
            var culture = AssemblyIdentityComparison.NormalizeCulture(hostMatches[i].Culture);
            if (culture.Length > 0)
            {
                text += " Culture=" + culture;
            }
            if (hostMatches[i].PublicKeyToken.Length > 0)
            {
                text += " PublicKeyToken=" + AssemblyFileIdentity.ToHex(hostMatches[i].PublicKeyToken);
            }
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
        var byCulture = string.Compare(a.Culture, b.Culture, StringComparison.Ordinal);
        if (byCulture != 0)
        {
            return byCulture;
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

        var ordered = new List<AssemblyClaim>(group);
        ordered.Sort(CompareClaims);
        var canonical = ordered[0];

        // 1. Identity first: identical bytes must never let two different identities share.
        for (var i = 1; i < ordered.Count; i++)
        {
            string difference;
            if (!AssemblyIdentityComparison.SameIdentity(canonical.Identity, ordered[i].Identity, out difference))
            {
                ConflictWithinMods(simpleName, ordered, result,
                    "a different identity (" + ordered[i].ModId + ": " + ordered[i].IdentityText +
                    "; " + difference + ")");
                return;
            }
        }

        // 2. Content: a full byte comparison, with the digest used only to prove difference.
        for (var i = 1; i < ordered.Count; i++)
        {
            ContentVerdict verdict;
            string reason;
            var proven = CompareClaimContent(canonical, ordered[i], out verdict, out reason);
            if (!proven)
            {
                ConflictWithinMods(simpleName, ordered, result,
                    "content equality could not be proven between " + canonical.ModId + " and " +
                    ordered[i].ModId + ": " + reason);
                return;
            }
            if (verdict != ContentVerdict.Same)
            {
                ConflictWithinMods(simpleName, ordered, result,
                    "a different content (" + ordered[i].ModId + ": " + ordered[i].ContentText +
                    "; " + reason + ")");
                return;
            }
        }

        // 3. Same identity and same content: one deterministic representative, loaded once.
        for (var i = 0; i < ordered.Count; i++)
        {
            var claim = ordered[i];
            // Every claim in the group is loaded from the representative's snapshot: identical
            // bytes were proven above, and `Assembly.LoadFrom` must be issued once per artifact.
            claim.CanonicalPath = canonical.StagedPath.Length > 0 ? canonical.StagedPath : canonical.Path;
            claim.CanonicalModId = canonical.ModId;
            claim.StagedPath = canonical.StagedPath;
            claim.SnapshotFingerprint = canonical.SnapshotFingerprint;
            claim.Resolution = ReferenceEquals(claim, canonical)
                ? AssemblyResolution.Load
                : AssemblyResolution.ShareFromClaim;
        }

        var shared = new PreflightEvent
        {
            Kind = PreflightEventKind.Shared,
            SimpleName = simpleName,
            Detail = canonical.ModId + "|" + canonical.DeclaredName + "|" + ordered.Count,
        };
        for (var i = 0; i < ordered.Count; i++)
        {
            shared.Owners.Add(ordered[i].ModId);
            shared.OwnerLabels.Add(ordered[i].ModId);
        }
        result.Events.Add(shared);
    }

    /// <summary>
    /// Compares the content of two claims. The digest may only prove that they differ; proving
    /// that they are the same always runs the full byte comparison, so a digest collision (or a
    /// non-cryptographic fallback) can never authorize sharing.
    /// </summary>
    private static bool CompareClaimContent(
        AssemblyClaim left,
        AssemblyClaim right,
        out ContentVerdict verdict,
        out string reason)
    {
        reason = "";
        if (left.Length != right.Length)
        {
            verdict = ContentVerdict.Different;
            reason = "different length";
            return true;
        }
        if (left.Digest.Length > 0 && right.Digest.Length > 0 &&
            !string.Equals(left.Digest, right.Digest, StringComparison.Ordinal))
        {
            // Sound for any deterministic digest: identical bytes cannot produce two digests.
            verdict = ContentVerdict.Different;
            reason = "different content digest";
            return true;
        }

        verdict = ContentIdentity.CompareFiles(left.Path, left.Length, right.Path, right.Length, out reason);
        return verdict != ContentVerdict.Unverifiable;
    }

    private static void ConflictWithinMods(
        string simpleName,
        List<AssemblyClaim> ordered,
        AssemblyPreflightResult result,
        string reason)
    {
        var conflict = new PreflightEvent
        {
            Kind = PreflightEventKind.ModConflict,
            SimpleName = simpleName,
            Detail = reason,
        };
        var identities = "";
        for (var i = 0; i < ordered.Count; i++)
        {
            var claim = ordered[i];
            if (i > 0)
            {
                identities += ",";
            }
            identities += claim.IdentityText + " content=" + claim.ContentText;
            conflict.Owners.Add(claim.ModId);
            conflict.OwnerLabels.Add(claim.ModId + "(" + claim.RoleText + ":" + claim.DeclaredName + ")");
            Reject(result, claim.ModId, ErrorCodes.AssemblyIdentityConflict,
                "assembly \"" + simpleName + "\" is declared by more than one mod with " + reason +
                " [" + claim.IdentityText + " content=" + claim.ContentText + "]");
        }
        conflict.Identities = identities;
        result.Events.Add(conflict);
    }

    private static void AddEvent(
        AssemblyPreflightResult result,
        PreflightEventKind kind,
        string simpleName,
        AssemblyClaim claim,
        string detail,
        string identityText)
    {
        var e = new PreflightEvent
        {
            Kind = kind,
            SimpleName = simpleName,
            Detail = detail,
            Identities = identityText,
        };
        e.Owners.Add(claim.ModId);
        e.OwnerLabels.Add(claim.ModId + "(" + claim.RoleText + ":" + claim.DeclaredName + ")");
        result.Events.Add(e);
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

    private static void Reject(AssemblyPreflightResult result, string modId, string code, string message)
    {
        if (result.Rejections.ContainsKey(modId))
        {
            return;
        }
        result.Rejections[modId] = new GateProblem(code, message);
    }
}
