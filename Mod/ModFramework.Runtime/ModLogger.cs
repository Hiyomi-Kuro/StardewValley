using System.Text;

namespace ModFramework.Runtime;

/// <summary>
/// Appends to <c>&lt;logs&gt;/&lt;mod id&gt;.log</c>, one line per record:
/// <c>&lt;ISO8601&gt; LEVEL [mod id] message</c>.
///
/// Failure policy: a log write that throws is swallowed and remembered; the logger then
/// stops touching the file so a broken file system cannot turn into an exception storm.
/// The framework report records that logging was degraded.
/// </summary>
internal sealed class ModLogger : IModLogger
{
    private readonly string _modId;
    private readonly string _path;
    private bool _broken;

    internal ModLogger(string modId, string logDirectory)
    {
        _modId = modId;
        _path = Path.Combine(logDirectory, modId + ".log");
    }

    internal bool IsBroken => _broken;

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
                sb.AppendLine();
                sb.Append("  exception: ");
                sb.Append(exception.GetType().FullName);
                sb.Append(": ");
                sb.Append(exception.Message);
                var stack = exception.StackTrace;
                if (!string.IsNullOrEmpty(stack))
                {
                    sb.AppendLine();
                    sb.Append("  stack: ");
                    sb.Append(stack);
                }
            }
            sb.Append(Environment.NewLine);
            File.AppendAllText(_path, sb.ToString());
        }
        catch (Exception)
        {
            _broken = true;
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
