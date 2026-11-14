using System.Text;

namespace ModFramework.Runtime;

/// <summary>Which lifecycle event a subscription is for.</summary>
internal enum ModEventKind
{
    GameLaunched = 0,
    Tick = 1,
}

/// <summary>
/// One subscription. Created by <see cref="ModEventsApi"/>; the token handed to the mod is this
/// object, so <see cref="Dispose"/> removes exactly this registration (a second registration of
/// the same delegate is a different object and stays).
/// </summary>
internal sealed class ModEventSubscription : IDisposable
{
    internal ModEventKind Kind;
    internal string ModId = "";
    internal long Sequence;
    internal int SessionId;
    internal Action<GameLaunchedEventArgs>? LaunchedHandler;
    internal Action<TickEventArgs>? TickHandler;
    internal IModLogger? Log;

    /// <summary>True while this subscription is in the dispatcher's live list.</summary>
    internal bool Active;

    /// <summary>True once the token was disposed (or rolled back); never dispatched again.</summary>
    internal bool Disposed;

    internal int Faults;
    internal int LastLoggedFaultCount;

    public void Dispose()
    {
        EventDispatcher.Unsubscribe(this);
    }
}

/// <summary>Token returned when a mod tries to subscribe after it failed to initialize.</summary>
internal sealed class DetachedSubscription : IDisposable
{
    internal static readonly DetachedSubscription Instance = new DetachedSubscription();

    public void Dispose()
    {
    }
}

/// <summary>
/// Per-mod event entry point. Owns the staging area of one <c>Initialize</c> call: every
/// subscription is held here until <see cref="Commit"/> and is discarded by
/// <see cref="Rollback"/>, so a mod that throws never observes an event.
/// </summary>
internal sealed class ModEventsApi : IModEvents
{
    private const int StateOpen = 0;
    private const int StateCommitted = 1;
    private const int StateRolledBack = 2;

    private readonly string _modId;
    private readonly IModLogger _log;
    private readonly int _sessionId;
    private readonly List<ModEventSubscription> _staged = new List<ModEventSubscription>();
    private int _state = StateOpen;
    private int _committedCount;

    internal ModEventsApi(string modId, IModLogger log, int sessionId)
    {
        _modId = modId;
        _log = log;
        _sessionId = sessionId;
    }

    public IDisposable OnGameLaunched(Action<GameLaunchedEventArgs> handler)
    {
        return EventDispatcher.Subscribe(this, ModEventKind.GameLaunched, handler, null);
    }

    public IDisposable OnTick(Action<TickEventArgs> handler)
    {
        return EventDispatcher.Subscribe(this, ModEventKind.Tick, null, handler);
    }

    internal string ModId
    {
        get { return _modId; }
    }

    internal IModLogger Log
    {
        get { return _log; }
    }

    internal int SessionId
    {
        get { return _sessionId; }
    }

    internal bool IsOpen
    {
        get { return _state == StateOpen; }
    }

    internal bool IsRolledBack
    {
        get { return _state == StateRolledBack; }
    }

    /// <summary>Stages one subscription. Caller holds the dispatcher gate.</summary>
    internal void StageLocked(ModEventSubscription subscription)
    {
        _staged.Add(subscription);
    }

    /// <summary>
    /// Moves every staged subscription into the active set. Called only after
    /// <c>IModEntry.Initialize</c> returned normally.
    /// </summary>
    internal void Commit()
    {
        EventDispatcher.CommitScope(this);
    }

    /// <summary>Drops every staged subscription. Called when the mod failed to initialize.</summary>
    internal void Rollback()
    {
        EventDispatcher.RollbackScope(this);
    }

    internal List<ModEventSubscription> Staged
    {
        get { return _staged; }
    }

    internal void MarkCommitted()
    {
        _state = StateCommitted;
    }

    internal void NoteCommitted()
    {
        _committedCount++;
    }

    /// <summary>Subscriptions that became live when this mod's Initialize returned normally.</summary>
    internal int CommittedCount
    {
        get { return _committedCount; }
    }

    internal void MarkRolledBack()
    {
        _state = StateRolledBack;
    }
}

/// <summary>
/// The framework-owned event router and its bridge entry points.
///
/// <para><b>Why a static router.</b> The loader runs before any <c>GameRunner</c> or <c>Game1</c>
/// instance exists, so mods can only register static callbacks. The injected hooks call
/// <see cref="DispatchGameLaunched"/> and <see cref="DispatchTick"/> through delegates that
/// <c>ModFramework.Bootstrap</c> resolves and caches once; no reflection happens per tick.</para>
///
/// <para><b>Threads.</b> Registration, dispatch and disposal are all supported on the game loop
/// thread; the gate only protects the short bookkeeping sections and is never held while a
/// handler runs, so a handler may subscribe or dispose. Handlers run on the dispatching thread,
/// which Phase 2.0 measures instead of assuming it is the Android UI thread.</para>
///
/// <para><b>Order.</b> Ordinal mod id, then registration order inside a mod. A stable snapshot is
/// reused until the subscription set changes, so a quiet tick neither sorts nor allocates.</para>
/// </summary>
public static class EventDispatcher
{
    private static readonly object Gate = new object();
    private static readonly List<ModEventSubscription> Active = new List<ModEventSubscription>();
    private static ModEventSubscription[] s_snapshot = new ModEventSubscription[0];
    private static bool s_snapshotDirty = true;
    private static long s_sequence;

    private static bool s_ready;
    private static int s_sessionId;
    private static string s_gameVersion = "unknown";
    private static string s_frameworkVersion = "unknown";

    private static int s_gameLaunchedCount;
    private static long s_tickCount;
    private static int s_handlerFaults;
    private static int s_logFaults;
    private static int s_bridgeFaults;
    private static long s_dispatchCount;
    private static int s_firstGameLaunchedThreadId;
    private static int s_firstTickThreadId;
    private static int s_loaderThreadId;
    private static bool s_gameLaunchedBeforeFirstTick = true;
    private static string s_firstGameLaunchedOrder = "";
    private static string s_firstTickOrder = "";
    private static bool s_bridgeFaultReported;

    // ------------------------------------------------------------------ bridge entry points

    /// <summary>
    /// Bridge entry point: called by the injected <c>Bootstrap.GameLaunched()</c> at the normal
    /// return path of <c>GameRunner.Initialize()</c>. Never throws.
    /// </summary>
    public static void DispatchGameLaunched()
    {
        try
        {
            ModEventSubscription[] snapshot;
            int threadId = Environment.CurrentManagedThreadId;
            lock (Gate)
            {
                if (!s_ready || s_gameLaunchedCount != 0)
                {
                    return;
                }

                s_gameLaunchedCount = 1;
                s_firstGameLaunchedThreadId = threadId;
                s_gameLaunchedBeforeFirstTick = s_tickCount == 0;
                s_dispatchCount++;
                snapshot = GetSnapshotLocked();
            }

            var args = new GameLaunchedEventArgs(s_gameVersion, s_frameworkVersion, threadId);
            DispatchTo(snapshot, ModEventKind.GameLaunched, args, default, true);
        }
        catch (Exception ex)
        {
            BridgeFault(ex, "GameLaunched");
        }
    }

    /// <summary>
    /// Bridge entry point: called by the injected <c>Bootstrap.Tick()</c> at the head of
    /// <c>GameRunner.Update(GameTime)</c>. Never throws.
    /// </summary>
    public static void DispatchTick()
    {
        try
        {
            ModEventSubscription[] snapshot;
            long tick;
            int threadId = Environment.CurrentManagedThreadId;
            lock (Gate)
            {
                if (!s_ready)
                {
                    return;
                }

                // Saturation, never wraparound: a wrapped counter would look like a young session.
                tick = s_tickCount == long.MaxValue ? long.MaxValue : s_tickCount + 1;
                s_tickCount = tick;
                if (s_firstTickThreadId == 0)
                {
                    s_firstTickThreadId = threadId;
                }
                s_dispatchCount++;
                snapshot = GetSnapshotLocked();
            }

            var args = new TickEventArgs(tick, threadId);
            DispatchTo(snapshot, ModEventKind.Tick, default, args, true);
        }
        catch (Exception ex)
        {
            BridgeFault(ex, "Tick");
        }
    }

    // ------------------------------------------------------------------ session lifecycle

    /// <summary>Starts a loader session. Dispatch stays disabled until <see cref="CommitSession"/>.</summary>
    internal static int BeginSession()
    {
        lock (Gate)
        {
            s_sessionId++;
            s_ready = false;
            s_gameLaunchedCount = 0;
            s_tickCount = 0;
            s_handlerFaults = 0;
            s_logFaults = 0;
            s_bridgeFaults = 0;
            s_dispatchCount = 0;
            s_firstGameLaunchedThreadId = 0;
            s_firstTickThreadId = 0;
            s_gameLaunchedBeforeFirstTick = true;
            s_firstGameLaunchedOrder = "";
            s_firstTickOrder = "";
            return s_sessionId;
        }
    }

    /// <summary>Marks the session usable and records what the loader detected.</summary>
    internal static void CommitSession(int sessionId, string gameVersion, string frameworkVersion)
    {
        lock (Gate)
        {
            if (sessionId != s_sessionId)
            {
                return;
            }

            s_gameVersion = string.IsNullOrEmpty(gameVersion) ? "unknown" : gameVersion;
            s_frameworkVersion = string.IsNullOrEmpty(frameworkVersion) ? "unknown" : frameworkVersion;
            s_loaderThreadId = Environment.CurrentManagedThreadId;
            s_ready = true;
        }
    }

    /// <summary>
    /// Fails the session closed: nothing dispatched afterwards and every subscription created
    /// inside this session is dropped, so a crashed loader cannot leave ghost subscriptions.
    /// </summary>
    internal static void AbortSession(int sessionId)
    {
        lock (Gate)
        {
            if (sessionId != s_sessionId)
            {
                return;
            }

            s_ready = false;
            RemoveSessionLocked(sessionId);
        }
    }

    internal static ModEventsApi CreateModEvents(string modId, IModLogger log, int sessionId)
    {
        return new ModEventsApi(modId, log, sessionId);
    }

    internal static int CurrentSession
    {
        get
        {
            lock (Gate)
            {
                return s_sessionId;
            }
        }
    }

    // ------------------------------------------------------------------ subscription plumbing

    internal static IDisposable Subscribe(
        ModEventsApi owner,
        ModEventKind kind,
        Action<GameLaunchedEventArgs>? launched,
        Action<TickEventArgs>? tick)
    {
        lock (Gate)
        {
            if (owner.IsRolledBack)
            {
                // The mod already failed: it must never run, so the token is inert.
                return DetachedSubscription.Instance;
            }

            var subscription = new ModEventSubscription
            {
                Kind = kind,
                ModId = owner.ModId,
                Sequence = ++s_sequence,
                SessionId = owner.SessionId,
                LaunchedHandler = launched,
                TickHandler = tick,
                Log = owner.Log,
                Active = !owner.IsOpen,
                Disposed = false,
            };

            if (owner.IsOpen)
            {
                owner.StageLocked(subscription);
            }
            else
            {
                Active.Add(subscription);
                s_snapshotDirty = true;
            }

            return subscription;
        }
    }

    internal static void Unsubscribe(ModEventSubscription subscription)
    {
        lock (Gate)
        {
            if (subscription.Disposed)
            {
                return;
            }

            subscription.Disposed = true;
            // A staged subscription is not in the active list yet; CommitScope skips it.
            for (var i = 0; i < Active.Count; i++)
            {
                if (!ReferenceEquals(Active[i], subscription))
                {
                    continue;
                }

                Active.RemoveAt(i);
                subscription.Active = false;
                s_snapshotDirty = true;
                break;
            }
        }
    }

    internal static void CommitScope(ModEventsApi owner)
    {
        lock (Gate)
        {
            if (!owner.IsOpen)
            {
                return;
            }

            var staged = owner.Staged;
            for (var i = 0; i < staged.Count; i++)
            {
                var subscription = staged[i];
                if (subscription.Disposed)
                {
                    // Disposed while staged: honour it.
                    continue;
                }

                subscription.Active = true;
                Active.Add(subscription);
                owner.NoteCommitted();
                s_snapshotDirty = true;
            }

            while (staged.Count > 0)
            {
                staged.RemoveAt(staged.Count - 1);
            }

            owner.MarkCommitted();
        }
    }

    internal static void RollbackScope(ModEventsApi owner)
    {
        lock (Gate)
        {
            if (!owner.IsOpen)
            {
                return;
            }

            var staged = owner.Staged;
            for (var i = 0; i < staged.Count; i++)
            {
                staged[i].Disposed = true;
                staged[i].Active = false;
            }

            while (staged.Count > 0)
            {
                staged.RemoveAt(staged.Count - 1);
            }

            owner.MarkRolledBack();
        }
    }

    private static void RemoveSessionLocked(int sessionId)
    {
        var removed = false;
        for (var i = Active.Count - 1; i >= 0; i--)
        {
            if (Active[i].SessionId != sessionId)
            {
                continue;
            }

            Active[i].Active = false;
            Active[i].Disposed = true;
            Active.RemoveAt(i);
            removed = true;
        }

        if (removed)
        {
            s_snapshotDirty = true;
        }
    }

    // ------------------------------------------------------------------ dispatch

    private static ModEventSubscription[] GetSnapshotLocked()
    {
        if (!s_snapshotDirty && s_snapshot.Length == Active.Count)
        {
            return s_snapshot;
        }

        var snapshot = new ModEventSubscription[Active.Count];
        for (var i = 0; i < Active.Count; i++)
        {
            snapshot[i] = Active[i];
        }

        // Ordinal mod id, then registration order. Never culture-aware and never insertion order
        // of a dictionary. Insertion sort is deliberate: the set is tiny, it is stable (so equal
        // keys keep registration order), and it uses no BCL member that the trimmed runtime might
        // not ship.
        for (var i = 1; i < snapshot.Length; i++)
        {
            var current = snapshot[i];
            var j = i - 1;
            while (j >= 0 && CompareSubscriptions(snapshot[j], current) > 0)
            {
                snapshot[j + 1] = snapshot[j];
                j--;
            }

            snapshot[j + 1] = current;
        }

        s_snapshot = snapshot;
        s_snapshotDirty = false;
        return snapshot;
    }

    private static int CompareSubscriptions(ModEventSubscription a, ModEventSubscription b)
    {
        var byMod = string.CompareOrdinal(a.ModId, b.ModId);
        if (byMod != 0)
        {
            return byMod;
        }

        return a.Sequence.CompareTo(b.Sequence);
    }

    private static void DispatchTo(
        ModEventSubscription[] snapshot,
        ModEventKind kind,
        GameLaunchedEventArgs launchedArgs,
        TickEventArgs tickArgs,
        bool recordOrder)
    {
        if (recordOrder)
        {
            RecordOrderLocked(kind, snapshot);
        }

        for (var i = 0; i < snapshot.Length; i++)
        {
            var subscription = snapshot[i];
            if (subscription.Kind != kind)
            {
                continue;
            }

            // Deliberately no liveness check: the snapshot is frozen for this dispatch, so a
            // handler that disposes (or adds) a subscription only affects the next one.
            try
            {
                if (kind == ModEventKind.GameLaunched)
                {
                    var handler = subscription.LaunchedHandler;
                    if (handler != null)
                    {
                        handler(launchedArgs);
                    }
                }
                else
                {
                    var handler = subscription.TickHandler;
                    if (handler != null)
                    {
                        handler(tickArgs);
                    }
                }
            }
            catch (Exception ex)
            {
                RecordHandlerFault(subscription, kind, ex);
            }
        }
    }

    private static void RecordOrderLocked(ModEventKind kind, ModEventSubscription[] snapshot)
    {
        var builder = new StringBuilder();
        lock (Gate)
        {
            var previous = "";
            for (var i = 0; i < snapshot.Length; i++)
            {
                if (snapshot[i].Kind != kind)
                {
                    continue;
                }

                // One entry per mod, in dispatch order: a mod with two subscriptions occupies two
                // consecutive slots, and repeating its id would say nothing about ordering.
                var id = snapshot[i].ModId;
                if (string.CompareOrdinal(id, previous) == 0)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(',');
                }

                builder.Append(id);
                previous = id;
            }

            // First dispatch only: the order string is evidence, not a running log.
            var text = builder.ToString();
            if (kind == ModEventKind.GameLaunched)
            {
                if (s_firstGameLaunchedOrder.Length == 0)
                {
                    s_firstGameLaunchedOrder = text;
                }
            }
            else if (s_firstTickOrder.Length == 0)
            {
                s_firstTickOrder = text;
            }
        }
    }

    private static void RecordHandlerFault(ModEventSubscription subscription, ModEventKind kind, Exception ex)
    {
        int count;
        lock (Gate)
        {
            subscription.Faults++;
            count = subscription.Faults;
            s_handlerFaults++;
        }

        // Bounded: first fault, second fault, then one line per power of ten. An endlessly
        // throwing handler can therefore never write one log record per tick.
        if (!ShouldLogFault(count))
        {
            return;
        }

        var eventName = kind == ModEventKind.GameLaunched ? "GameLaunched" : "Tick";
        try
        {
            var log = subscription.Log;
            if (log == null)
            {
                return;
            }

            if (count == 1)
            {
                log.Error("event handler for " + eventName + " threw (subscription #" +
                          subscription.Sequence + "); the remaining handlers still run", ex);
            }
            else
            {
                log.Error("event handler for " + eventName + " is still throwing: " + count +
                          " failures for subscription #" + subscription.Sequence +
                          " (further logs are rate limited)");
            }
        }
        catch (Exception)
        {
            lock (Gate)
            {
                s_logFaults++;
            }
        }
    }

    private static bool ShouldLogFault(int count)
    {
        if (count <= 2)
        {
            return true;
        }

        long threshold = 10;
        while (threshold < count)
        {
            threshold *= 10;
        }

        return count == threshold;
    }

    private static void BridgeFault(Exception ex, string eventName)
    {
        bool first;
        lock (Gate)
        {
            s_bridgeFaults++;
            first = !s_bridgeFaultReported;
            s_bridgeFaultReported = true;
        }

        if (!first)
        {
            return;
        }

        // One line, once per process: the game loop must never fail because diagnostics failed.
        try
        {
            Console.Error.WriteLine("[ModFramework] event bridge fault during " + eventName + ": " +
                                    ex.GetType().Name + ": " + ex.Message);
        }
        catch (Exception)
        {
            // ignored on purpose
        }
    }

    // ------------------------------------------------------------------ diagnostics

    internal static EventDiagnosticsSnapshot CaptureDiagnostics()
    {
        lock (Gate)
        {
            var mods = 0;
            var previousModId = "";
            var seen = false;
            for (var i = 0; i < Active.Count; i++)
            {
                var id = Active[i].ModId;
                if (!seen || string.CompareOrdinal(id, previousModId) != 0)
                {
                    mods++;
                    previousModId = id;
                    seen = true;
                }
            }

            return new EventDiagnosticsSnapshot(
                s_ready,
                s_sessionId,
                s_gameLaunchedCount,
                s_tickCount,
                Active.Count,
                mods,
                s_handlerFaults,
                s_logFaults,
                s_bridgeFaults,
                s_dispatchCount,
                s_firstGameLaunchedThreadId,
                s_firstTickThreadId,
                s_loaderThreadId,
                s_gameLaunchedBeforeFirstTick,
                s_firstGameLaunchedOrder,
                s_firstTickOrder);
        }
    }

    /// <summary>Test seam: restores the process-wide router to its initial state.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Active.Clear();
            s_snapshot = new ModEventSubscription[0];
            s_snapshotDirty = true;
            s_sequence = 0;
            s_ready = false;
            s_sessionId = 0;
            s_gameVersion = "unknown";
            s_frameworkVersion = "unknown";
            s_gameLaunchedCount = 0;
            s_tickCount = 0;
            s_handlerFaults = 0;
            s_logFaults = 0;
            s_bridgeFaults = 0;
            s_dispatchCount = 0;
            s_firstGameLaunchedThreadId = 0;
            s_firstTickThreadId = 0;
            s_loaderThreadId = 0;
            s_gameLaunchedBeforeFirstTick = true;
            s_firstGameLaunchedOrder = "";
            s_firstTickOrder = "";
            s_bridgeFaultReported = false;
        }
    }
}
