namespace ModFramework.Runtime;

// Frozen copy of the Phase 1.0.0 public contract. Do not add anything here: that would defeat
// the purpose of the binary-compatibility fixture.

public interface IModEntry
{
    void Initialize(IModContext context);
}

public interface IModContext
{
    string ModId { get; }

    string ModName { get; }

    string ModVersion { get; }

    string GameVersion { get; }

    string FrameworkVersion { get; }

    string ModDirectory { get; }

    IModLogger Log { get; }
}

public interface IModLogger
{
    void Info(string message);

    void Warn(string message);

    void Error(string message);

    void Error(string message, Exception exception);
}

public enum ModLogLevel
{
    Info = 0,
    Warn = 1,
    Error = 2,
}
