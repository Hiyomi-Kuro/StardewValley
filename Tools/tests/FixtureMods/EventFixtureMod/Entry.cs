using System;
using System.Globalization;
using System.IO;
using System.Text;
using ModFramework.Runtime;

namespace EventFixture;

/// <summary>
/// One entry type for all three Phase 2.0 event fixtures; the MSBuild property decides which
/// behaviour is compiled in.
///
/// Like the Phase 1 fixtures, it proves what happened by writing a small marker file into its own
/// package directory (the loader never rewrites it, and the host can read it afterwards). Writes
/// are bounded: initialize, GameLaunched, and the first three ticks only.
/// </summary>
public sealed class Entry : IModEntry
{
    public void Initialize(IModContext context)
    {
        EventFixtureState.Reset();
        EventFixtureState.InitializeCalls++;
        EventFixtureState.ModDirectory = context.ModDirectory;
        EventFixtureState.FrameworkVersion = context.FrameworkVersion;

#if EVENT_VARIANT_FAULT
        context.Events.OnTick(EventFixtureState.OnTick);
        context.Events.OnGameLaunched(EventFixtureState.OnLaunched);
        EventFixtureState.SubscribedBeforeThrow = true;
        EventFixtureState.Write("initialize-subscribed-then-throw");
        throw new InvalidOperationException("phase2.0 fixture: subscribe-then-throw");
#elif EVENT_VARIANT_Z
        // Faulting handler first: a broken isolation would stop the healthy handler.
        context.Events.OnTick(EventFixtureState.FaultingTick);
        context.Events.OnTick(EventFixtureState.OnTick);
        context.Events.OnGameLaunched(EventFixtureState.OnLaunched);
        EventFixtureState.Write("initialize");
#else
        context.Events.OnGameLaunched(EventFixtureState.OnLaunched);
        context.Events.OnTick(EventFixtureState.OnTick);
        // Same delegate registered twice: two independent subscriptions.
        context.Events.OnTick(EventFixtureState.OnTick);
        EventFixtureState.Write("initialize");
#endif
    }
}

/// <summary>Observable state; also mirrored into <c>events-state.txt</c> in the package.</summary>
public static class EventFixtureState
{
    public static int InitializeCalls;
    public static int LaunchedCalls;
    public static int TickCalls;
    public static long FirstTick;
    public static long LastTick;
    public static int FaultAttempts;
    public static string ModDirectory = "";
    public static string FrameworkVersion = "";
    public static bool SubscribedBeforeThrow;

    public static void Reset()
    {
        InitializeCalls = 0;
        LaunchedCalls = 0;
        TickCalls = 0;
        FirstTick = 0;
        LastTick = 0;
        FaultAttempts = 0;
        SubscribedBeforeThrow = false;
    }

    public static void OnLaunched(GameLaunchedEventArgs args)
    {
        LaunchedCalls++;
        Write("on-launched thread=" + args.ThreadId.ToString(CultureInfo.InvariantCulture));
    }

    public static void OnTick(TickEventArgs args)
    {
        TickCalls++;
        if (FirstTick == 0)
        {
            FirstTick = args.TickCount;
        }

        LastTick = args.TickCount;
        if (args.TickCount <= 3)
        {
            Write("on-tick tick=" + args.TickCount.ToString(CultureInfo.InvariantCulture) +
                  " thread=" + args.ThreadId.ToString(CultureInfo.InvariantCulture));
        }
    }

    public static void FaultingTick(TickEventArgs args)
    {
        FaultAttempts++;
        Write("fault tick=" + args.TickCount.ToString(CultureInfo.InvariantCulture));
        if (FaultAttempts <= 3)
        {
            throw new InvalidOperationException("phase2.0 fixture bounded fault");
        }
    }

    /// <summary>Overwrites one bounded state file: the host only needs the latest snapshot.</summary>
    public static void Write(string reason)
    {
        try
        {
            var text = new StringBuilder();
            text.Append("reason=").Append(reason).Append('\n');
            text.Append("initialize_calls=").Append(InitializeCalls.ToString(CultureInfo.InvariantCulture)).Append('\n');
            text.Append("launched_calls=").Append(LaunchedCalls.ToString(CultureInfo.InvariantCulture)).Append('\n');
            text.Append("tick_calls=").Append(TickCalls.ToString(CultureInfo.InvariantCulture)).Append('\n');
            text.Append("first_tick=").Append(FirstTick.ToString(CultureInfo.InvariantCulture)).Append('\n');
            text.Append("last_tick=").Append(LastTick.ToString(CultureInfo.InvariantCulture)).Append('\n');
            text.Append("fault_attempts=").Append(FaultAttempts.ToString(CultureInfo.InvariantCulture)).Append('\n');
            text.Append("subscribed_before_throw=").Append(SubscribedBeforeThrow ? "true" : "false").Append('\n');
            text.Append("framework_version=").Append(FrameworkVersion).Append('\n');
            File.WriteAllText(Path.Combine(ModDirectory, "events-state.txt"), text.ToString());
        }
        catch (Exception)
        {
            // best effort: a fixture must never break the loader
        }
    }
}
