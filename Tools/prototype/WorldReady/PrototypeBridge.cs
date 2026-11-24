namespace Phase21.WorldReady;

/// <summary>Receives the raw signals the two prototype hooks observe.</summary>
public interface IWorldReadySink
{
    void OnWorldLoading(bool loadedGame, object? loader, int gameMode, int threadId);

    void OnDayCompleted(int gameMode, bool hasLoadedGame, object? loader, int threadId);
}

/// <summary>
/// Process-wide entry point called by the injected hook call sites in the synthetic fixture.
///
/// <para>This type lives in the prototype assembly and is only reachable from a test fixture or
/// from a temporary, non-installable copy of the game assembly. It is not part of
/// <c>ModFramework.Runtime</c>, is not referenced by the loader, and cannot be wired by the
/// default build.</para>
/// </summary>
public static class PrototypeBridge
{
    private static IWorldReadySink? _sink;

    public static void Install(IWorldReadySink? sink)
    {
        _sink = sink;
    }

    public static void WorldLoading(bool loadedGame, object? loader, int gameMode)
    {
        var sink = Volatile.Read(ref _sink);
        sink?.OnWorldLoading(loadedGame, loader, gameMode, Environment.CurrentManagedThreadId);
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

    public void OnWorldLoading(bool loadedGame, object? loader, int gameMode, int threadId)
    {
        if (!_coordinator.TryBeginAttempt(loadedGame, gameMode, loader, threadId, out _, out var refusal))
        {
            Note("start refused: " + refusal + " (loadedGame=" + loadedGame + ", gameMode=" + gameMode +
                 ", thread=" + threadId + ")");
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
