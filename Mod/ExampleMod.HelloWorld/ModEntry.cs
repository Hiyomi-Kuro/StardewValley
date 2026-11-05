using ModFramework.Runtime;

namespace ExampleMod.HelloWorld;

/// <summary>
/// The entry point the loader finds through <c>manifest.json</c>
/// (<c>"entryType": "ExampleMod.HelloWorld.HelloWorldEntry"</c>).
///
/// It touches only <see cref="IModContext"/>, so it can be initialized by the desktop host
/// test as well as on the device. Everything that needs the game lives in
/// <see cref="GameBindings"/> and is never called from here.
/// </summary>
public sealed class HelloWorldEntry : IModEntry
{
    /// <summary>
    /// Called once by the loader, after the manifest validated, dependencies resolved and the
    /// assembly loaded.
    /// </summary>
    /// <param name="context">Identity, versions, mod directory and the mod's own logger.</param>
    public void Initialize(IModContext context)
    {
        context.Log.Info("Hello World from \"" + context.ModName + "\" " + context.ModVersion);
        context.Log.Info("game=" + context.GameVersion + " framework=" + context.FrameworkVersion);
        context.Log.Info("mod directory=" + context.ModDirectory);
    }
}

/// <summary>
/// Compile-time proof that a mod can reference game types directly
/// (<c>using StardewValley;</c> + <c>Game1</c>), exactly like a desktop mod would.
///
/// This class is intentionally not called by <see cref="HelloWorldEntry"/>: the loader must be
/// able to run the mod on a host that has no game assembly loaded, and calling a <c>Game1</c>
/// member there would fail at JIT time.
/// </summary>
public static class GameBindings
{
    /// <summary>Reads a static property of <c>Game1</c>.</summary>
    public static string Describe() => "multiplayer=" + StardewValley.Game1.IsMultiplayer;

    /// <summary>Reads a static field of <c>Game1</c>.</summary>
    public static int TicksSnapshot() => StardewValley.Game1.ticks;
}
