namespace ModFramework.Runtime;

/// <summary>
/// Lifecycle of one mod package. The order is meaningful: a mod only ever moves forward
/// (except into <see cref="Failed"/> or <see cref="Skipped"/>).
///
/// <see cref="AssemblyLoaded"/> is deliberately distinct from <see cref="Initialized"/>:
/// loading a DLL proves nothing about the mod having run.
/// </summary>
internal enum ModState
{
    Discovered = 0,
    ManifestValidated = 1,
    DependenciesResolved = 2,
    AssemblyLoaded = 3,
    EntryValidated = 4,
    Instantiated = 5,
    Initialized = 6,
    Failed = 7,
    Skipped = 8,
}

/// <summary>One discovered mod package and its progress through the lifecycle.</summary>
internal sealed class ModRecord
{
    internal ModRecord(string directoryName, string directory)
    {
        DirectoryName = directoryName;
        Directory = directory;
    }

    internal string DirectoryName { get; }

    internal string Directory { get; }

    internal ModManifest? Manifest { get; set; }

    /// <summary>Mod id when the manifest validated, otherwise the directory name (for the report).</summary>
    internal string Id => Manifest != null ? Manifest.Id : DirectoryName;

    internal ModState State { get; private set; } = ModState.Discovered;

    internal string ErrorCode { get; private set; } = "";

    internal string ErrorMessage { get; private set; } = "";

    internal string AssemblyPath { get; private set; } = "";

    /// <summary>
    /// <c>ok</c> or <c>degraded</c>. Logging failure is deliberately <b>not</b> a mod failure:
    /// a mod whose <c>Initialize</c> returned normally stays <see cref="ModState.Initialized"/>
    /// even when its log file could not be written.
    /// </summary>
    internal string LogStatus { get; private set; } = "ok";

    /// <summary>Exception type of the first log write failure, or empty. Never a path.</summary>
    internal string LogFailureType { get; private set; } = "";

    /// <summary>Assemblies the preflight made this mod share instead of loading its own copy.</summary>
    internal List<string> SharedAssemblies { get; } = new();

    internal List<string> History { get; } = new();

    internal bool IsTerminalFailure => State == ModState.Failed || State == ModState.Skipped;

    internal bool IsInitialized => State == ModState.Initialized;

    internal void Advance(ModState state)
    {
        if (state > State && State != ModState.Failed && State != ModState.Skipped)
        {
            State = state;
            History.Add(state.ToString());
        }
    }

    internal void SetLogStatus(bool broken, string failureType)
    {
        LogStatus = broken ? "degraded" : "ok";
        LogFailureType = broken ? failureType : "";
    }

    internal void AddSharedAssembly(string text)
    {
        SharedAssemblies.Add(text);
    }

    internal void SetAssemblyPath(string path)
    {
        AssemblyPath = path;
    }

    internal void Fail(string code, string message)
    {
        ErrorCode = code;
        ErrorMessage = message;
        State = ModState.Failed;
        History.Add("Failed(" + code + ")");
    }

    internal void Skip(string code, string message)
    {
        ErrorCode = code;
        ErrorMessage = message;
        State = ModState.Skipped;
        History.Add("Skipped(" + code + ")");
    }

    internal string DescribeHistory()
    {
        var text = "";
        for (var i = 0; i < History.Count; i++)
        {
            if (i > 0)
            {
                text += ">";
            }
            text += History[i];
        }
        return text;
    }
}
