namespace ModFramework.Runtime;

/// <summary>
/// The game lifecycle events this framework dispatches, one instance per mod
/// (see <see cref="IModContext.Events"/>).
///
/// Contract (Phase 2.0, deliberately minimal):
///
/// <list type="bullet">
/// <item><description>
/// <b>GameLaunched</b> is dispatched at most once per process, from the normal return path of
/// <c>StardewValley.GameRunner.Initialize()</c>. It does not claim that all content is loaded
/// and it is not the same thing as "the title screen is visible".
/// </description></item>
/// <item><description>
/// <b>Tick</b> is dispatched once per entry to <c>StardewValley.GameRunner.Update(GameTime)</c>.
/// It is not a render-frame counter, not a promised 60 Hz timer and not wall-clock time.
/// </description></item>
/// <item><description>
/// Dispatch order is deterministic: ordinal <see cref="IModContext.ModId"/> order first, then
/// registration order inside one mod. It never depends on dictionary enumeration order or the
/// current culture.
/// </description></item>
/// <item><description>
/// Each dispatch runs against a snapshot taken at the start of that dispatch. Subscribing or
/// disposing from inside a handler takes effect on the next dispatch, never on the current one.
/// </description></item>
/// <item><description>
/// Every handler is isolated: an exception is recorded against that subscription and the
/// remaining handlers still run. Repeated failures are logged a bounded number of times.
/// </description></item>
/// <item><description>
/// Handlers run on whatever thread entered the game method. Phase 2.0 measures that thread on
/// device instead of assuming "Android UI thread"; the framework never switches threads and a
/// handler must not block. See <see cref="EventDiagnostics"/>.
/// </description></item>
/// <item><description>
/// No game assembly types cross this boundary: the arguments expose only framework-owned
/// primitives.
/// </description></item>
/// </list>
///
/// Not provided: event priority, cancellation, hot reload, assembly unloading, cross-process
/// subscription and full SMAPI compatibility.
/// </summary>
public interface IModEvents
{
    /// <summary>
    /// Subscribes to <c>GameLaunched</c>. The returned token unsubscribes; disposing it twice is
    /// a no-op. Registering the same delegate twice creates two independent subscriptions, both
    /// of which fire.
    /// </summary>
    /// <param name="handler">Callback. Must not be <c>null</c>.</param>
    /// <returns>Subscription token; <see cref="IDisposable.Dispose"/> only removes this one.</returns>
    IDisposable OnGameLaunched(Action<GameLaunchedEventArgs> handler);

    /// <summary>
    /// Subscribes to <c>Tick</c>. Same token semantics as <see cref="OnGameLaunched"/>.
    /// </summary>
    /// <param name="handler">Callback. Must not be <c>null</c>.</param>
    /// <returns>Subscription token; <see cref="IDisposable.Dispose"/> only removes this one.</returns>
    IDisposable OnTick(Action<TickEventArgs> handler);
}

/// <summary>
/// Arguments of <c>GameLaunched</c>. A value type, so dispatching it allocates nothing.
/// </summary>
public readonly struct GameLaunchedEventArgs
{
    internal GameLaunchedEventArgs(string gameVersion, string frameworkVersion, int threadId)
    {
        GameVersion = gameVersion;
        FrameworkVersion = frameworkVersion;
        ThreadId = threadId;
    }

    /// <summary>Detected game version, or <c>unknown</c> when it could not be determined.</summary>
    public string GameVersion { get; }

    /// <summary>Version of the hosting <c>ModFramework.Runtime</c> (the framework API version).</summary>
    public string FrameworkVersion { get; }

    /// <summary>Managed thread id that dispatched this event.</summary>
    public int ThreadId { get; }
}

/// <summary>
/// Arguments of <c>Tick</c>. A value type, so dispatching it allocates nothing.
///
/// <see cref="TickCount"/> is 1 for the first dispatched tick and increases by exactly one for
/// every subsequent one. It is a <see cref="long"/> and saturates at <see cref="long.MaxValue"/>
/// instead of wrapping (reachable only after 2^63-1 updates). It is a dispatch counter, not a
/// frame index or a time value.
/// </summary>
public readonly struct TickEventArgs
{
    internal TickEventArgs(long tickCount, int threadId)
    {
        TickCount = tickCount;
        ThreadId = threadId;
    }

    /// <summary>1-based, monotonically increasing dispatch counter.</summary>
    public long TickCount { get; }

    /// <summary>Managed thread id that dispatched this event.</summary>
    public int ThreadId { get; }
}

/// <summary>
/// Point-in-time copy of the framework's event bookkeeping. Read-only; produced by
/// <see cref="EventDiagnostics.Capture"/>.
///
/// The loader report is written before any event happens, so a mod that wants later evidence
/// (two consecutive snapshots proving that <see cref="TickCount"/> grows) reads it from here and
/// writes it at its own bounded cadence.
/// </summary>
public sealed class EventDiagnosticsSnapshot
{
    internal EventDiagnosticsSnapshot(
        bool ready,
        int sessionId,
        int gameLaunchedCount,
        long tickCount,
        int subscriptionCount,
        int modCount,
        int handlerFaults,
        int logFaults,
        int bridgeFaults,
        long dispatchCount,
        int firstGameLaunchedThreadId,
        int firstTickThreadId,
        int loaderThreadId,
        bool gameLaunchedBeforeFirstTick,
        string firstGameLaunchedOrder,
        string firstTickOrder)
    {
        Ready = ready;
        SessionId = sessionId;
        GameLaunchedCount = gameLaunchedCount;
        TickCount = tickCount;
        SubscriptionCount = subscriptionCount;
        ModCount = modCount;
        HandlerFaults = handlerFaults;
        LogFaults = logFaults;
        BridgeFaults = bridgeFaults;
        DispatchCount = dispatchCount;
        FirstGameLaunchedThreadId = firstGameLaunchedThreadId;
        FirstTickThreadId = firstTickThreadId;
        LoaderThreadId = loaderThreadId;
        GameLaunchedBeforeFirstTick = gameLaunchedBeforeFirstTick;
        FirstGameLaunchedOrder = firstGameLaunchedOrder;
        FirstTickOrder = firstTickOrder;
    }

    /// <summary>Whether the loader committed an event session (dispatch is enabled).</summary>
    public bool Ready { get; }

    /// <summary>Monotonic loader session id; 0 means no session has started.</summary>
    public int SessionId { get; }

    /// <summary>0 or 1: <c>GameLaunched</c> is dispatched at most once per process.</summary>
    public int GameLaunchedCount { get; }

    /// <summary>Number of dispatched tick events (0 before the first one).</summary>
    public long TickCount { get; }

    /// <summary>Live subscriptions of every mod.</summary>
    public int SubscriptionCount { get; }

    /// <summary>Number of mods that currently own at least one subscription.</summary>
    public int ModCount { get; }

    /// <summary>Total handler exceptions caught by the dispatcher.</summary>
    public int HandlerFaults { get; }

    /// <summary>Total logger writes that failed while recording a fault.</summary>
    public int LogFaults { get; }

    /// <summary>Total faults inside the dispatcher itself (never propagated to the game).</summary>
    public int BridgeFaults { get; }

    /// <summary>Total event dispatches attempted while ready.</summary>
    public long DispatchCount { get; }

    /// <summary>Managed thread id of the first <c>GameLaunched</c> (0 if never).</summary>
    public int FirstGameLaunchedThreadId { get; }

    /// <summary>Managed thread id of the first <c>Tick</c> (0 if never).</summary>
    public int FirstTickThreadId { get; }

    /// <summary>Managed thread id that ran the loader (0 if it never reported one).</summary>
    public int LoaderThreadId { get; }

    /// <summary>True when <c>GameLaunched</c> was observed while no tick had been dispatched yet.</summary>
    public bool GameLaunchedBeforeFirstTick { get; }

    /// <summary>Mod ids of the first GameLaunched dispatch, comma separated, in dispatch order.</summary>
    public string FirstGameLaunchedOrder { get; }

    /// <summary>Mod ids of the first Tick dispatch, comma separated, in dispatch order.</summary>
    public string FirstTickOrder { get; }
}

/// <summary>
/// Read-only access to the event bookkeeping. Safe to call from any handler; takes one short
/// lock and allocates one snapshot.
/// </summary>
public static class EventDiagnostics
{
    /// <summary>Captures the current state. Never throws.</summary>
    public static EventDiagnosticsSnapshot Capture()
    {
        return EventDispatcher.CaptureDiagnostics();
    }
}
