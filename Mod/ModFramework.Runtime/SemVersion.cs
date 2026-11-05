using System.Text;

namespace ModFramework.Runtime;

/// <summary>
/// Strict SemVer 2.0.0 version (build metadata parsed but ignored for precedence).
///
/// Written by hand because the shipped runtime has no <c>Version.TryParse</c> and because
/// SemVer's prerelease ordering is not <see cref="Version"/> ordering.
/// </summary>
internal sealed class SemVersion : IComparable<SemVersion>
{
    private SemVersion()
    {
    }

    internal long Major { get; private set; }

    internal long Minor { get; private set; }

    internal long Patch { get; private set; }

    /// <summary>Prerelease identifiers; empty means "release".</summary>
    internal string[] PreRelease { get; private set; } = new string[0];

    internal string Build { get; private set; } = "";

    internal string Text { get; private set; } = "";

    internal bool IsPrerelease => PreRelease.Length > 0;

    internal static bool TryParse(string? text, out SemVersion version, out string error)
    {
        version = new SemVersion();
        error = "";
        if (text == null)
        {
            error = "version is null";
            return false;
        }
        var value = text.Trim();
        if (value.Length == 0)
        {
            error = "version is empty";
            return false;
        }
        if (value.Length > 128)
        {
            error = "version is longer than 128 characters";
            return false;
        }

        var core = value;
        var buildIndex = core.IndexOf('+');
        if (buildIndex >= 0)
        {
            version.Build = core.Substring(buildIndex + 1);
            core = core.Substring(0, buildIndex);
            if (!IsValidIdentifierSequence(version.Build, allowLeadingZeroes: true))
            {
                error = "invalid build metadata";
                return false;
            }
        }

        var preIndex = core.IndexOf('-');
        if (preIndex >= 0)
        {
            var pre = core.Substring(preIndex + 1);
            core = core.Substring(0, preIndex);
            if (!IsValidIdentifierSequence(pre, allowLeadingZeroes: false))
            {
                error = "invalid prerelease identifier";
                return false;
            }
            version.PreRelease = pre.Split('.');
        }

        var parts = core.Split('.');
        if (parts.Length != 3)
        {
            error = "version must be major.minor.patch";
            return false;
        }
        if (!TryParseNumber(parts[0], out var major) ||
            !TryParseNumber(parts[1], out var minor) ||
            !TryParseNumber(parts[2], out var patch))
        {
            error = "version components must be non-negative integers without leading zeros";
            return false;
        }

        version.Major = major;
        version.Minor = minor;
        version.Patch = patch;
        version.Text = value;
        return true;
    }

    private static bool TryParseNumber(string part, out long value)
    {
        value = 0;
        if (part.Length == 0 || part.Length > 18)
        {
            return false;
        }
        if (part.Length > 1 && part[0] == '0')
        {
            return false;
        }
        for (var i = 0; i < part.Length; i++)
        {
            var c = part[i];
            if (c < '0' || c > '9')
            {
                return false;
            }
            value = (value * 10) + (c - '0');
        }
        return true;
    }

    private static bool IsValidIdentifierSequence(string text, bool allowLeadingZeroes)
    {
        if (text.Length == 0)
        {
            return false;
        }
        var parts = text.Split('.');
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0)
            {
                return false;
            }
            var allDigits = true;
            for (var j = 0; j < part.Length; j++)
            {
                var c = part[j];
                var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-';
                if (!ok)
                {
                    return false;
                }
                if (c < '0' || c > '9')
                {
                    allDigits = false;
                }
            }
            if (allDigits && !allowLeadingZeroes && part.Length > 1 && part[0] == '0')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>SemVer 2.0 precedence. Build metadata never participates.</summary>
    public int CompareTo(SemVersion? other)
    {
        if (other is null)
        {
            return 1;
        }
        var c = Major.CompareTo(other.Major);
        if (c != 0) { return c; }
        c = Minor.CompareTo(other.Minor);
        if (c != 0) { return c; }
        c = Patch.CompareTo(other.Patch);
        if (c != 0) { return c; }

        // A release version has higher precedence than any prerelease of the same core.
        if (PreRelease.Length == 0 && other.PreRelease.Length == 0) { return 0; }
        if (PreRelease.Length == 0) { return 1; }
        if (other.PreRelease.Length == 0) { return -1; }

        var shared = PreRelease.Length < other.PreRelease.Length ? PreRelease.Length : other.PreRelease.Length;
        for (var i = 0; i < shared; i++)
        {
            c = CompareIdentifier(PreRelease[i], other.PreRelease[i]);
            if (c != 0)
            {
                return c;
            }
        }
        return PreRelease.Length.CompareTo(other.PreRelease.Length);
    }

    private static int CompareIdentifier(string a, string b)
    {
        long na, nb;
        var aNumeric = TryParseNumber(a, out na);
        var bNumeric = TryParseNumber(b, out nb);
        if (aNumeric && bNumeric)
        {
            return na.CompareTo(nb);
        }
        if (aNumeric)
        {
            return -1; // numeric identifiers have lower precedence than alphanumeric ones
        }
        if (bNumeric)
        {
            return 1;
        }
        return string.CompareOrdinal(a, b);
    }

    public override string ToString() => Text;

    internal static SemVersion Of(long major, long minor, long patch)
    {
        var v = new SemVersion { Major = major, Minor = minor, Patch = patch };
        v.Text = major + "." + minor + "." + patch;
        return v;
    }
}

/// <summary>
/// A conjunction of comparators: <c>&gt;=1.0.0 &lt;2.0.0</c>, <c>^1.2.3</c>, <c>~1.2.3</c>,
/// <c>=1.2.3</c>, <c>*</c>. Comparators are separated by spaces and/or commas and ALL must hold.
/// Disjunction (<c>||</c>) is deliberately not supported in this phase.
/// </summary>
internal sealed class VersionRange
{
    private readonly List<Comparator> _comparators = new();

    private VersionRange(string text)
    {
        Text = text;
    }

    internal string Text { get; }

    internal static VersionRange Any { get; } = new VersionRange("*");

    private struct Comparator
    {
        internal char Op;      // '=', '<', '>'
        internal bool OrEqual;
        internal SemVersion Version;
    }

    internal static bool TryParse(string? text, out VersionRange range, out string error)
    {
        range = Any;
        error = "";
        if (text == null)
        {
            error = "range is null";
            return false;
        }
        var value = text.Trim();
        if (value.Length == 0)
        {
            error = "range is empty";
            return false;
        }
        if (value == "*")
        {
            range = Any;
            return true;
        }

        var result = new VersionRange(value);
        var tokens = value.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            error = "range has no comparators";
            return false;
        }
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (!TryParseComparator(token, result, out error))
            {
                error = "'" + token + "': " + error;
                return false;
            }
        }
        range = result;
        return true;
    }

    private static bool TryParseComparator(string token, VersionRange target, out string error)
    {
        error = "";
        if (token == "*")
        {
            error = "'*' cannot be combined with other comparators";
            return false;
        }

        var op = '\0';
        var orEqual = false;
        var rest = token;

        if (token.Length >= 2 && token[0] == '>' && token[1] == '=')
        {
            op = '>';
            orEqual = true;
            rest = token.Substring(2);
        }
        else if (token.Length >= 2 && token[0] == '<' && token[1] == '=')
        {
            op = '<';
            orEqual = true;
            rest = token.Substring(2);
        }
        else if (token[0] == '>')
        {
            op = '>';
            rest = token.Substring(1);
        }
        else if (token[0] == '<')
        {
            op = '<';
            rest = token.Substring(1);
        }
        else if (token[0] == '=')
        {
            op = '=';
            rest = token.Substring(1);
        }
        else if (token[0] == '^')
        {
            SemVersion caret;
            if (!SemVersion.TryParse(token.Substring(1), out caret, out error))
            {
                return false;
            }
            target.Add('>', true, caret);
            target.Add('<', false, UpperBoundForCaret(caret));
            return true;
        }
        else if (token[0] == '~')
        {
            SemVersion tilde;
            if (!SemVersion.TryParse(token.Substring(1), out tilde, out error))
            {
                return false;
            }
            target.Add('>', true, tilde);
            target.Add('<', false, SemVersion.Of(tilde.Major, tilde.Minor + 1, 0));
            return true;
        }
        else
        {
            op = '=';
        }

        SemVersion version;
        if (!SemVersion.TryParse(rest, out version, out error))
        {
            return false;
        }
        target.Add(op, orEqual, version);
        return true;
    }

    private static SemVersion UpperBoundForCaret(SemVersion v)
    {
        if (v.Major > 0)
        {
            return SemVersion.Of(v.Major + 1, 0, 0);
        }
        if (v.Minor > 0)
        {
            return SemVersion.Of(0, v.Minor + 1, 0);
        }
        return SemVersion.Of(0, 0, v.Patch + 1);
    }

    private void Add(char op, bool orEqual, SemVersion version)
    {
        _comparators.Add(new Comparator { Op = op, OrEqual = orEqual, Version = version });
    }

    internal bool IsSatisfiedBy(SemVersion version)
    {
        for (var i = 0; i < _comparators.Count; i++)
        {
            var c = _comparators[i];
            var cmp = version.CompareTo(c.Version);
            switch (c.Op)
            {
                case '=':
                    if (cmp != 0) { return false; }
                    break;
                case '>':
                    if (c.OrEqual ? cmp < 0 : cmp <= 0) { return false; }
                    break;
                case '<':
                    if (c.OrEqual ? cmp > 0 : cmp >= 0) { return false; }
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    public override string ToString() => Text;

    /// <summary>Human readable list of comparators, used in diagnostics.</summary>
    internal string Describe()
    {
        if (_comparators.Count == 0)
        {
            return "*";
        }
        var sb = new StringBuilder();
        for (var i = 0; i < _comparators.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }
            var c = _comparators[i];
            sb.Append(c.Op);
            if (c.OrEqual)
            {
                sb.Append('=');
            }
            sb.Append(c.Version.Text);
        }
        return sb.ToString();
    }
}
