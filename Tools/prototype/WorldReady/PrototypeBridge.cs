namespace Phase21.WorldReady;

/// <summary>Receives the raw signals the prototype hooks observe.</summary>
public interface IWorldReadySink
{
    /// <summary>The epoch a start call site must read before it reads mode/loader.</summary>
    long CurrentResetEpoch { get; }

    /// <summary><c>Game1.ResetGameStateOnTitleScreen()</c> entry.</summary>
    void OnResetBegan(long resetEpoch, int threadId);

    /// <summary><c>Game1.ResetGameStateOnTitleScreen()</c> normal return.</summary>
    void OnResetCompleted(long resetEpoch, int threadId, bool completed);

    void OnWorldLoading(long resetEpoch, bool loadedGame, int gameMode, object? loader, int threadId);

    void OnDayCompleted(int gameMode, bool hasLoadedGame, object? loader, int threadId);
}

/// <summary>
/// Process-wide entry point called by the injected hook call sites in the synthetic fixture.
///
/// <para>This type lives in the prototype assembly and is only reachable from a test fixture or
/// from a temporary, non-installable copy of the game assembly. It is not part of
/// <c>ModFramework.Runtime</c>, is not referenced by the loader, and cannot be wired by the
/// default build.</para>
///
/// <para>The emitted call sequences are part of the contract. The start path reads the epoch
/// <b>first</b>: <c>GetResetEpoch()</c>, then <c>loadedGame</c>, then <c>get_gameMode()</c>, then
/// <c>currentLoader</c>, then <c>WorldLoading(epoch, loadedGame, gameMode, loader)</c>. Any other
/// order leaves the "epoch is current but the fields are the previous world's" window open.</para>
/// </summary>
public static class PrototypeBridge
{
    private static IWorldReadySink? _sink;

    public static void Install(IWorldReadySink? sink)
    {
        _sink = sink;
    }

    /// <summary>
    /// Reads the coordinator's reset epoch. Called <i>before</i> the game fields are read, so a
    /// reset that lands afterwards makes the start stale instead of silently current.
    /// </summary>
    public static long GetResetEpoch()
    {
        var sink = Volatile.Read(ref _sink);
        return sink?.CurrentResetEpoch ?? 0L;
    }

    /// <summary>Injected at the entry of <c>ResetGameStateOnTitleScreen</c>.</summary>
    public static void ResetBegan()
    {
        var sink = Volatile.Read(ref _sink);
        sink?.OnResetBegan(sink.CurrentResetEpoch, Environment.CurrentManagedThreadId);
    }

    /// <summary>Injected before the single normal return of <c>ResetGameStateOnTitleScreen</c>.</summary>
    public static void ResetCompleted()
    {
        var sink = Volatile.Read(ref _sink);
        sink?.OnResetCompleted(sink.CurrentResetEpoch, Environment.CurrentManagedThreadId, true);
    }

    public static void WorldLoading(long resetEpoch, bool loadedGame, int gameMode, object? loader)
    {
        var sink = Volatile.Read(ref _sink);
        sink?.OnWorldLoading(resetEpoch, loadedGame, gameMode, loader,
            Environment.CurrentManagedThreadId);
    }

    public static void DayCompleted(int gameMode, bool hasLoadedGame, object? loader)
    {
        var sink = Volatile.Read(ref _sink);
        sink?.OnDayCompleted(gameMode, hasLoadedGame, loader, Environment.CurrentManagedThreadId);
    }
}

/// <summary>
/// Adapts the raw hook signals to the coordinator. It is the only place that decides how the
/// loader object is correlated, which keeps the coordinator free of game knowledge.
/// </summary>
public sealed class CoordinatorSink : IWorldReadySink
{
    private readonly WorldReadyCoordinator _coordinator;
    private readonly List<string> _notes = new List<string>();

    public CoordinatorSink(WorldReadyCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public WorldReadyCoordinator Coordinator
    {
        get { return _coordinator; }
    }

    public IReadOnlyList<string> Notes
    {
        get
        {
            lock (_notes)
            {
                return _notes.ToArray();
            }
        }
    }

    public long CurrentResetEpoch
    {
        get { return _coordinator.ResetEpoch; }
    }

    public void OnResetBegan(long resetEpoch, int threadId)
    {
        if (!_coordinator.TryBeginReset(threadId, "ResetGameStateOnTitleScreen", out var epoch,
                out var refusal))
        {
            Note("reset begin refused: " + refusal + " (epoch=" + resetEpoch + ", thread=" +
                 threadId + ")");
            return;
        }

        Note("reset began: epoch " + epoch + " on thread " + threadId);
    }

    public void OnResetCompleted(long resetEpoch, int threadId, bool completed)
    {
        if (!_coordinator.TryCompleteReset(threadId, out var refusal))
        {
            Note("reset complete refused: " + refusal + " (epoch=" + resetEpoch + ", thread=" +
                 threadId + ")");
            return;
        }

        Note("reset completed: epoch " + resetEpoch + " on thread " + threadId +
             ", completed=" + completed);
    }

    public void OnWorldLoading(long resetEpoch, bool loadedGame, int gameMode, object? loader,
        int threadId)
    {
        if (!_coordinator.TryBeginAttempt(loadedGame, gameMode, loader, threadId, resetEpoch,
                out _, out var refusal))
        {
            Note("start refused: " + refusal + " (loadedGame=" + loadedGame + ", gameMode=" +
                 gameMode + ", epoch=" + resetEpoch + ", thread=" + threadId + ")");
        }
    }

    public void OnDayCompleted(int gameMode, bool hasLoadedGame, object? loader, int threadId)
    {
        var outcome = _coordinator.Complete(gameMode, hasLoadedGame, loader, threadId);
        if (!outcome.Accepted)
        {
            Note("completion refused: " + outcome.Refusal + " (gameMode=" + gameMode +
                 ", hasLoadedGame=" + hasLoadedGame + ", thread=" + threadId + ")");
        }
    }

    private void Note(string text)
    {
        lock (_notes)
        {
            _notes.Add(text);
        }
    }
}
