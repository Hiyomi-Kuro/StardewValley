using System.Text;

namespace ModFramework.Runtime;

/// <summary>
/// Appends to <c>&lt;logs&gt;/&lt;mod id&gt;.log</c>, one line per record:
/// <c>&lt;ISO8601&gt; LEVEL [mod id] message</c>.
///
/// Failure policy: a log write that throws is swallowed and remembered; the logger then
/// stops touching the file so a broken file system cannot turn into an exception storm.
/// The framework report records that logging was degraded (see <see cref="ModRecord.SetLogStatus"/>).
/// </summary>
internal sealed class ModLogger : IModLogger
{
    private readonly string _modId;
    private readonly string _path;
    private bool _broken;
    private string _failureType = "";

    internal ModLogger(string modId, string logDirectory)
    {
        _modId = modId;
        _path = Path.Combine(logDirectory, modId + ".log");
    }

    internal bool IsBroken => _broken;

    /// <summary>
    /// Exception <b>type name only</b> of the first write failure. Nothing else is kept: the
    /// report must not gain absolute paths, messages or environment details from here.
    /// </summary>
    internal string FailureType => _failureType;

    /// <summary>
    /// Test seam: when set, replaces the physical append so a deterministic failure can be
    /// injected without permissions, locks or a full disk. <c>null</c> in production.
    /// Internal on purpose - it is not part of the mod-author contract.
    /// </summary>
    internal static Action<string, string>? AppendOverride;

    /// <summary>Report fragment: <c>ok</c> or <c>degraded(&lt;ExceptionType&gt;)</c>.</summary>
    internal string DescribeStatus()
    {
        return _broken ? "degraded(" + _failureType + ")" : "ok";
    }

    public void Info(string message) => Write(ModLogLevel.Info, message, null);

    public void Warn(string message) => Write(ModLogLevel.Warn, message, null);

    public void Error(string message) => Write(ModLogLevel.Error, message, null);

    public void Error(string message, Exception exception) => Write(ModLogLevel.Error, message, exception);

    private void Write(ModLogLevel level, string message, Exception? exception)
    {
        if (_broken)
        {
            return;
        }
        var text = "";
        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTimeOffset.Now.ToString("O"));
            sb.Append(' ');
            sb.Append(LevelText(level));
            sb.Append(" [");
            sb.Append(_modId);
            sb.Append("] ");
            sb.Append(message == null ? "" : message);
            if (exception != null)
            {
                // The mod log is persisted and routinely pasted into issue reports. An exception
                // message or stack trace quotes absolute source and data paths, so both go
                // through the same scrubber the loader report uses; the exception type and the
                // scrubbed reason are kept.
                sb.AppendLine();
                sb.Append("  exception: ");
                sb.Append(exception.GetType().FullName);
                sb.Append(": ");
                sb.Append(PathScrubber.Scrub(exception.Message ?? ""));
                var stack = exception.StackTrace;
                if (!string.IsNullOrEmpty(stack))
                {
                    sb.AppendLine();
                    sb.Append("  stack: ");
                    sb.Append(PathScrubber.Scrub(stack!));
                }
            }
            sb.Append(Environment.NewLine);
            text = sb.ToString();

            var append = AppendOverride;
            if (append != null)
            {
                append(_path, text);
            }
            else
            {
                File.AppendAllText(_path, text);
            }
        }
        catch (Exception ex)
        {
            // One failure disables the file for the rest of the session: no exception storm,
            // no repeated file-system probes, and the report still gets a stable status.
            _broken = true;
            _failureType = ex.GetType().Name;
        }
    }

    private static string LevelText(ModLogLevel level)
    {
        switch (level)
        {
            case ModLogLevel.Info: return "INFO ";
            case ModLogLevel.Warn: return "WARN ";
            case ModLogLevel.Error: return "ERROR";
            default: return "INFO ";
        }
    }
}
