namespace ModFramework.Runtime;

/// <summary>
/// The single entry contract a mod must implement.
///
/// The loader finds this type by the exact name in the mod's <c>manifest.json</c>
/// (<c>entryType</c>); it never guesses by scanning for "the first class".
///
/// Requirements enforced before <see cref="Initialize"/> is called:
/// the type exists, is public, is a class, is not abstract, is not generic,
/// implements <see cref="IModEntry"/>, and has a public parameterless constructor.
/// </summary>
public interface IModEntry
{
    /// <summary>
    /// Called once, on the game's UI thread, after every dependency of this mod has been
    /// initialized successfully. Throwing from here fails only this mod (and the mods that
    /// depend on it); the loader records the failure and continues with unrelated mods.
    /// </summary>
    /// <param name="context">Per-mod context: identity, versions, mod directory and logger.</param>
    void Initialize(IModContext context);
}

/// <summary>
/// Per-mod context handed to <see cref="IModEntry.Initialize"/>.
///
/// Deliberately small: no game objects, no events, no content pipeline. Exposing
/// unverified <c>Game1</c> members as a stable API is out of scope for this phase.
/// </summary>
public interface IModContext
{
    /// <summary>Manifest id of this mod (already validated: letters, digits, <c>.</c>, <c>_</c>, <c>-</c>).</summary>
    string ModId { get; }

    /// <summary>Human readable name from the manifest.</summary>
    string ModName { get; }

    /// <summary>This mod's own version, as written in the manifest.</summary>
    string ModVersion { get; }

    /// <summary>Version of the running game, or <c>unknown</c> when it could not be determined.</summary>
    string GameVersion { get; }

    /// <summary>Version of <c>ModFramework.Runtime</c> that is hosting this mod.</summary>
    string FrameworkVersion { get; }

    /// <summary>Absolute path of this mod's package directory.</summary>
    string ModDirectory { get; }

    /// <summary>Logger that prefixes every line with this mod's id.</summary>
    IModLogger Log { get; }
}

/// <summary>
/// Minimal logger. Every record carries the mod id, a timestamp and a level.
/// Write failures never propagate to the mod and never recurse.
/// </summary>
public interface IModLogger
{
    /// <summary>Informational message.</summary>
    void Info(string message);

    /// <summary>Something looks wrong but the mod keeps running.</summary>
    void Warn(string message);

    /// <summary>A failure. Prefer the overload that takes the exception.</summary>
    void Error(string message);

    /// <summary>A failure with its exception (type, message and stack trace are recorded).</summary>
    void Error(string message, Exception exception);
}

/// <summary>Severity of a log record. Values are stable and used in the log text.</summary>
public enum ModLogLevel
{
    /// <summary>Informational.</summary>
    Info = 0,

    /// <summary>Warning.</summary>
    Warn = 1,

    /// <summary>Error.</summary>
    Error = 2,
}
