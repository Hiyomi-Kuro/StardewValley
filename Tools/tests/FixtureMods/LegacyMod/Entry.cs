using System;
using System.IO;
using ModFramework.Runtime;

namespace LegacyFixtureMod;

/// <summary>
/// Written against the Phase 1.0.0 contract: it never mentions <c>Events</c>, because that member
/// did not exist when this source was compiled. The marker file is what the host asserts on.
/// </summary>
public sealed class Entry : IModEntry
{
    public void Initialize(IModContext context)
    {
        LegacyState.InitializeCalls++;
        LegacyState.ModId = context.ModId;
        LegacyState.GameVersion = context.GameVersion;
        LegacyState.FrameworkVersion = context.FrameworkVersion;
        LegacyState.ThreadId = Environment.CurrentManagedThreadId;

        try
        {
            File.WriteAllText(Path.Combine(context.ModDirectory, "legacy-ran.txt"),
                "mod=" + context.ModId + "\n" +
                "framework=" + context.FrameworkVersion + "\n" +
                "game=" + context.GameVersion + "\n" +
                "thread=" + Environment.CurrentManagedThreadId + "\n");
        }
        catch (Exception)
        {
            // best effort
        }

        context.Log.Info("legacy fixture initialized against " + context.FrameworkVersion);
    }
}

public static class LegacyState
{
    public static int InitializeCalls;
    public static string ModId = "";
    public static string GameVersion = "";
    public static string FrameworkVersion = "";
    public static int ThreadId;
}
