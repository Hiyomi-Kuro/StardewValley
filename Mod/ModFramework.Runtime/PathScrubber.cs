using System.Text;

namespace ModFramework.Runtime;

/// <summary>
/// Makes text that may embed host file-system locations safe to persist.
///
/// Policy (documented in <c>docs/audit/phase1.1.1-report.md</c> §F):
/// <list type="bullet">
///   <item>the exception <b>type</b> is always kept - it is the diagnostic contract;</item>
///   <item>the message is kept, but any absolute path is replaced by <c>&lt;path&gt;</c>;</item>
///   <item>nothing else is invented: no user name, no device, no SDK location.</item>
/// </list>
///
/// The report and the mod logs are the only callers. A relative path inside a mod package
/// (for example <c>data.bin</c>) is deliberately left alone: that is the author's own name for
/// their own file and carries no host information.
/// </summary>
internal static class PathScrubber
{
    internal const string Placeholder = "<path>";

    /// <summary><c>&lt;ExceptionType&gt;: &lt;message with absolute paths removed&gt;</c>.</summary>
    internal static string Describe(Exception exception)
    {
        var message = exception.Message;
        return exception.GetType().Name + ": " + Scrub(message == null ? "" : message);
    }

    /// <summary>Replaces every absolute path in <paramref name="text"/> with <see cref="Placeholder"/>.</summary>
    internal static string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? "";
        }

        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var end = AbsolutePathEnd(text, i);
            if (end > i)
            {
                sb.Append(Placeholder);
                i = end;
                continue;
            }
            sb.Append(text[i]);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>Last path segment only; used for report fields that must stay path-free.</summary>
    internal static string Leaf(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "";
        }
        var normalized = path.Replace('\\', '/');
        var end = normalized.Length;
        while (end > 0 && normalized[end - 1] == '/')
        {
            end--;
        }
        var start = end;
        while (start > 0 && normalized[start - 1] != '/')
        {
            start--;
        }
        return normalized.Substring(start, end - start);
    }

    /// <summary>
    /// End offset (exclusive) of an absolute path starting at <paramref name="start"/>, or
    /// <paramref name="start"/> when no absolute path starts there.
    /// </summary>
    private static int AbsolutePathEnd(string text, int start)
    {
        // Windows drive path: C:\... or C:/...
        if (start + 2 < text.Length && IsAsciiLetter(text[start]) && text[start + 1] == ':' &&
            (text[start + 2] == '\\' || text[start + 2] == '/'))
        {
            return Consume(text, start);
        }

        // UNC path: \\server\share
        if (text[start] == '\\' && start + 1 < text.Length && text[start + 1] == '\\')
        {
            return Consume(text, start);
        }

        // POSIX absolute path, but not a URL ("https://...") and not a bare "/".
        if (text[start] == '/' && start + 1 < text.Length && text[start + 1] != '/' && text[start + 1] != ' ' &&
            (start == 0 || IsBoundary(text[start - 1])))
        {
            return Consume(text, start);
        }

        return start;
    }

    private static int Consume(string text, int start)
    {
        var i = start;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '\r' || ch == '\n' || ch == '\t' || ch == '"' || ch == '\'' || ch == '|' ||
                ch == '<' || ch == '>' || ch == '*' || ch == '?' || ch == ')' || ch == ']' || ch == '}' ||
                ch == ',' || ch == ';')
            {
                break;
            }

            // A space may be part of a path (a user profile directory can contain one). Keep it
            // only when the same segment continues with a separator before the next space;
            // otherwise the sentence resumes and the path ends here.
            if (ch == ' ')
            {
                if (!SpaceBelongsToPath(text, i))
                {
                    break;
                }
            }
            i++;
        }
        return i;
    }

    private static bool SpaceBelongsToPath(string text, int spaceIndex)
    {
        for (var i = spaceIndex + 1; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\\' || ch == '/')
            {
                return true;
            }
            if (ch == ' ' || ch == '\r' || ch == '\n' || ch == '\t' || ch == '"' || ch == '\'' ||
                ch == ')' || ch == ']' || ch == ',' || ch == ';')
            {
                return false;
            }
        }
        return false;
    }

    private static bool IsBoundary(char ch)
    {
        return ch == ' ' || ch == '\t' || ch == '"' || ch == '\'' || ch == '=' || ch == '(' ||
               ch == '[' || ch == '{' || ch == ',' || ch == ';' || ch == ':' || ch == '\r' || ch == '\n';
    }

    private static bool IsAsciiLetter(char ch)
    {
        return (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');
    }
}
