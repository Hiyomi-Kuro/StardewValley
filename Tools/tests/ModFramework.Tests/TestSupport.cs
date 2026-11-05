using System;
using System.IO;
using System.Text;

namespace ModFramework.Tests;

/// <summary>
/// Small helpers shared by the Phase 1.1.2 tests.
///
/// Deliberately separate from <c>Phase111Tests</c>: the previous phase's file is kept byte for
/// byte as historical evidence, so its private helpers are not reused here.
/// </summary>
internal static class TestSupport
{
    internal static string Report(string root)
    {
        return Temp.ReadText(Path.Combine(root, "loader-report.txt"));
    }

    internal static string Line(string report, string needle)
    {
        var lines = report.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].IndexOf(needle, StringComparison.Ordinal) >= 0)
            {
                return lines[i].Trim();
            }
        }
        return "(no line for " + needle + ")";
    }

    /// <summary>True when the mod is in the Failed state with one of the given stable codes.</summary>
    internal static bool FailedWith(string report, string modId, params string[] codes)
    {
        for (var i = 0; i < codes.Length; i++)
        {
            if (report.Contains("id=" + modId + " state=Failed code=" + codes[i]))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Ordered <c>id/state/code</c> signature of the <c>mods:</c> block.</summary>
    internal static string Signature(string report)
    {
        var sb = new StringBuilder();
        var lines = report.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (!trimmed.StartsWith("- id=", StringComparison.Ordinal))
            {
                continue;
            }
            var cut = trimmed.IndexOf(" message=", StringComparison.Ordinal);
            if (cut < 0)
            {
                cut = trimmed.IndexOf(" kind=", StringComparison.Ordinal);
            }
            var segment = cut > 0 ? trimmed.Substring(0, cut) : trimmed;
            if (sb.Length > 0)
            {
                sb.Append('|');
            }
            sb.Append(segment);
        }
        return sb.ToString();
    }

    /// <summary>Index of the first report line that starts with <paramref name="prefix"/>, or -1.</summary>
    internal static int LineIndex(string report, string prefix)
    {
        var lines = report.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith(prefix, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    internal static string CreatePackage(string root, string directoryName, string manifestJson)
    {
        var package = Path.Combine(root, directoryName);
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "manifest.json"), manifestJson, new UTF8Encoding(false));
        return package;
    }

    internal static void CopyAs(string package, string source, string targetName)
    {
        File.Copy(source, Path.Combine(package, targetName), true);
    }

    internal static string ManifestJson(
        string id,
        string entryAssembly,
        string entryType,
        string extraJson = "",
        string version = "1.0.0")
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"schemaVersion\": 1,\n");
        sb.Append("  \"id\": \"" + id + "\",\n");
        sb.Append("  \"name\": \"" + id + "\",\n");
        sb.Append("  \"version\": \"" + version + "\",\n");
        sb.Append("  \"entryAssembly\": \"" + entryAssembly + "\",\n");
        sb.Append("  \"entryType\": \"" + entryType + "\"");
        if (extraJson.Length > 0)
        {
            sb.Append(",\n  " + extraJson);
        }
        sb.Append("\n}\n");
        return sb.ToString();
    }

    internal static string PrivateAssemblies(params string[] names)
    {
        var sb = new StringBuilder("\"privateAssemblies\": [ ");
        for (var i = 0; i < names.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            sb.Append("\"" + names[i] + "\"");
        }
        sb.Append(" ]");
        return sb.ToString();
    }

    internal static string Dependency(string id, string range = ">=1.0.0")
    {
        return "\"dependencies\": [ { \"id\": \"" + id + "\", \"version\": \"" + range + "\" } ]";
    }

    internal static string OptionalDependency(string id, string range = ">=1.0.0")
    {
        return "\"dependencies\": [ { \"id\": \"" + id + "\", \"version\": \"" + range +
               "\", \"optional\": true } ]";
    }

    /// <summary>
    /// Replaces every occurrence of an ASCII string with another of the <b>same length</b>, so the
    /// result keeps the exact file length and PE layout while changing its bytes. Used to forge a
    /// "same length, valid managed assembly, different identity" replacement.
    /// </summary>
    internal static byte[] PatchAscii(byte[] image, string from, string to)
    {
        if (from.Length == 0 || from.Length != to.Length)
        {
            throw new ArgumentException("the replacement must have the same non-zero length");
        }
        var needle = Encoding.ASCII.GetBytes(from);
        var replacement = Encoding.ASCII.GetBytes(to);
        var hits = 0;
        for (var i = 0; i + needle.Length <= image.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (image[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (!match)
            {
                continue;
            }
            Array.Copy(replacement, 0, image, i, replacement.Length);
            hits++;
        }
        if (hits == 0)
        {
            throw new InvalidOperationException("\"" + from + "\" does not occur in the image");
        }
        return image;
    }

    internal static string Sha256Hex(byte[] bytes)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        for (var i = 0; i < hash.Length; i++)
        {
            sb.Append(hash[i].ToString("x2"));
        }
        return sb.ToString();
    }
}
