using System;
using System.IO;
using FixtureMod.Dependency;
using ModFramework.Runtime;

namespace FixtureMod;

/// <summary>
/// The well-behaved fixture: logs through the context, touches its private dependency and
/// leaves a marker file so the host test can prove the entry really ran.
/// </summary>
public sealed class GoodEntry : IModEntry
{
    public void Initialize(IModContext context)
    {
        context.Log.Info("hello from " + context.ModId + " " + context.ModVersion);
        context.Log.Info("dependency says: " + DependencyMarker.Describe());
        context.Log.Warn("a warning is recorded too");
        File.WriteAllText(Path.Combine(context.ModDirectory, "entry-ran.txt"),
            context.ModId + "|" + context.GameVersion + "|" + context.FrameworkVersion);
    }
}

/// <summary>Initialization throws: only this mod (and its dependents) may fail.</summary>
public sealed class ThrowingEntry : IModEntry
{
    public void Initialize(IModContext context)
    {
        context.Log.Info("about to throw");
        throw new InvalidOperationException("fixture entry threw on purpose");
    }
}

/// <summary>Abstract: must be rejected before instantiation.</summary>
public abstract class AbstractEntry : IModEntry
{
    public abstract void Initialize(IModContext context);
}

/// <summary>No public parameterless constructor: must be rejected before instantiation.</summary>
public sealed class NoDefaultCtorEntry : IModEntry
{
    public NoDefaultCtorEntry(int unused)
    {
        Unused = unused;
    }

    public int Unused { get; }

    public void Initialize(IModContext context)
    {
        context.Log.Info("should never run: " + Unused);
    }
}

/// <summary>Public class that does not implement the contract.</summary>
public sealed class NotAnEntry
{
    public void Initialize()
    {
    }
}

/// <summary>Internal implementation: must be rejected as not public.</summary>
internal sealed class InternalEntry : IModEntry
{
    public void Initialize(IModContext context)
    {
        context.Log.Info("should never run");
    }
}

/// <summary>
/// Calls the loader again from inside Initialize, which must be refused by the re-entrancy
/// guard instead of re-running every mod.
/// </summary>
public sealed class ReentrantEntry : IModEntry
{
    public void Initialize(IModContext context)
    {
        var parent = Path.GetDirectoryName(context.ModDirectory);
        var result = Loader.Run(parent);
        context.Log.Info("reentrant-call-result=" + (result == null ? "null" : result));
        File.WriteAllText(Path.Combine(context.ModDirectory, "reentrant.txt"),
            result == null ? "null" : result);
    }
}

/// <summary>Depends on another fixture mod; used to prove dependency ordering.</summary>
public sealed class DependentEntry : IModEntry
{
    public void Initialize(IModContext context)
    {
        context.Log.Info("dependent mod initialized after its dependency");
    }
}
