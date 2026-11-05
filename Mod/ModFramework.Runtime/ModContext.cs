namespace ModFramework.Runtime;

/// <summary>Read-only view of one mod, handed to its entry point.</summary>
internal sealed class ModContext : IModContext
{
    internal ModContext(ModManifest manifest, string gameVersion, string frameworkVersion, IModLogger logger)
    {
        ModId = manifest.Id;
        ModName = manifest.Name;
        ModVersion = manifest.VersionText;
        ModDirectory = manifest.Directory;
        GameVersion = gameVersion;
        FrameworkVersion = frameworkVersion;
        Log = logger;
    }

    public string ModId { get; }

    public string ModName { get; }

    public string ModVersion { get; }

    public string GameVersion { get; }

    public string FrameworkVersion { get; }

    public string ModDirectory { get; }

    public IModLogger Log { get; }
}
