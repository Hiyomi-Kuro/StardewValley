using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ModFramework.Runtime;

namespace ModFramework.Tests;

/// <summary>
/// Read-only differential check of <see cref="ManagedAssemblyReader"/> against the desktop
/// BCL's own parser (<c>AssemblyName.GetAssemblyName</c>) over a directory of real assemblies.
///
/// Purpose: prove the hand-written PE/CLI reader does not merely "not throw" but agrees with an
/// independent implementation on the identity of real, compiler-produced files. It is the
/// positive half of the parser evidence; the synthetic malformations in
/// <see cref="Phase111Tests"/> are the negative half.
///
/// Privacy: the assemblies under test are the game's own reference assemblies, which may not be
/// copied into the repository or a public log. This routine therefore prints only file
/// <b>names</b>, counts and error kinds - never a directory, never a path, never file content.
/// </summary>
internal static class RefsDifferential
{
    internal static int Run(string directory)
    {
        if (!Directory.Exists(directory))
        {
            Console.WriteLine("refs-differential: directory not available");
            return 2;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*.dll");
        }
        catch (Exception ex)
        {
            Console.WriteLine("refs-differential: cannot enumerate the directory " + ex.GetType().Name);
            return 2;
        }

        Array.Sort(files, StringComparer.Ordinal);

        var compared = 0;
        var mismatched = 0;
        var unreadable = 0;
        var notManaged = 0;
        var tokenMismatched = 0;
        var signed = 0;
        var details = new List<string>();

        for (var i = 0; i < files.Length; i++)
        {
            var file = files[i];
            var display = Path.GetFileName(file);

            AssemblyName reference;
            try
            {
                reference = AssemblyName.GetAssemblyName(file);
            }
            catch (Exception)
            {
                // The BCL itself refuses it: not a managed assembly we can diff against.
                notManaged++;
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(file);
            }
            catch (Exception ex)
            {
                unreadable++;
                Add(details, display + " read-failed " + ex.GetType().Name);
                continue;
            }

            AssemblyFileIdentity parsed;
            string error;
            if (!ManagedAssemblyReader.TryReadIdentity(bytes, out parsed, out error))
            {
                unreadable++;
                Add(details, display + " rejected " + Shorten(error));
                continue;
            }

            compared++;

            var referenceCulture = Normalize(reference.CultureName);
            var parsedCulture = Normalize(parsed.Culture);
            var nameOk = string.Equals(reference.Name, parsed.Name, StringComparison.Ordinal);
            var versionOk = string.Equals(
                HostAssemblyIdentity.NormalizeVersion(reference.Version), parsed.VersionText, StringComparison.Ordinal);
            var cultureOk = string.Equals(referenceCulture, parsedCulture, StringComparison.OrdinalIgnoreCase);

            byte[] referenceToken;
            try
            {
                referenceToken = reference.GetPublicKeyToken() ?? Array.Empty<byte>();
            }
            catch (Exception)
            {
                referenceToken = Array.Empty<byte>();
            }
            var tokenOk = BytesEqual(referenceToken, parsed.PublicKeyToken);
            if (parsed.PublicKeyToken.Length > 0)
            {
                // A signed assembly actually exercises the ECMA-335 token derivation: this is the
                // check that would catch a wrong SHA-1 or a wrong Flags interpretation.
                signed++;
            }

            if (!nameOk || !versionOk || !cultureOk)
            {
                mismatched++;
                Add(details, display + " identity reference=" + reference.Name + "/" +
                             HostAssemblyIdentity.NormalizeVersion(reference.Version) + "/" + referenceCulture +
                             " parsed=" + parsed.Name + "/" + parsed.VersionText + "/" + parsedCulture);
            }
            else if (!tokenOk)
            {
                tokenMismatched++;
                Add(details, display + " token reference=" + ToHex(referenceToken) +
                             " parsed=" + ToHex(parsed.PublicKeyToken) +
                             " flags=0x" + parsed.Flags.ToString("x4"));
            }
        }

        Console.WriteLine("refs-differential: " + files.Length + " files present");
        Console.WriteLine("  compared        : " + compared + " (strong-name signed: " + signed + ")");
        Console.WriteLine("  identity mismatch: " + mismatched);
        Console.WriteLine("  token mismatch  : " + tokenMismatched);
        Console.WriteLine("  rejected by us  : " + unreadable);
        Console.WriteLine("  not managed (BCL): " + notManaged);
        for (var i = 0; i < details.Count; i++)
        {
            Console.WriteLine("  detail: " + details[i]);
        }

        if (compared == 0)
        {
            Console.WriteLine("RESULT: FAILED (nothing could be compared)");
            return 1;
        }
        if (mismatched != 0 || tokenMismatched != 0 || unreadable != 0)
        {
            Console.WriteLine("RESULT: FAILED");
            return 1;
        }
        Console.WriteLine("RESULT: OK");
        return 0;
    }

    private static void Add(List<string> details, string text)
    {
        if (details.Count < 12)
        {
            details.Add(text);
        }
    }

    private static string Shorten(string text)
    {
        return text.Length <= 90 ? text : text.Substring(0, 90) + "...";
    }

    private static string Normalize(string culture)
    {
        if (string.IsNullOrEmpty(culture) || string.Equals(culture, "neutral", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }
        return culture;
    }

    private static bool BytesEqual(byte[] left, byte[] right)
    {
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

    private static string ToHex(byte[] value)
    {
        var text = "";
        for (var i = 0; i < value.Length; i++)
        {
            text += value[i].ToString("x2");
        }
        return text.Length == 0 ? "(none)" : text;
    }
}
