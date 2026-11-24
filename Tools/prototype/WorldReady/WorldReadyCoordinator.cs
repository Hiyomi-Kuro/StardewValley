namespace Phase21.WorldReady;

/// <summary>What kind of world entry an attempt is.</summary>
public enum AttemptKind
{
    /// <summary><c>loadForNewGame(true)</c>: an existing save is being loaded.</summary>
    Load = 0,

    /// <summary><c>loadForNewGame(false)</c>: a new game is being created.</summary>
    NewGame = 1,
}

/// <summary>
/// Why a signal was refused. Every refusal is counted; nothing is silently dropped.
/// </summary>
public enum RefusalKind
{
    None = 0,

    /// <summary>A read attempt started while <c>gameMode != 6</c> (the load screen mode).</summary>
    StartOutsideLoadMode,

    /// <summary>A read attempt started without a loader object (nothing to correlate later).</summary>
    StartWithoutLoaderEvidence,

    /// <summary>A completion arrived while no attempt was pending.</summary>
    NoPendingAttempt,

    /// <summary>A completion arrived after this attempt's one allowed observation was consumed.</summary>
    AlreadyConsumed,

    /// <summary>The coordinator has no game-loop thread authority yet.</summary>
    UnknownGameLoopThread,

    /// <summary>The completion ran on a managed thread other than the confirmed game loop.</summary>
    WrongThread,

    /// <summary>The completion arrived while <c>gameMode == 0</c> (title screen).</summary>
    TitleMode,

    /// <summary>The completion arrived in a mode other than the playing mode.</summary>
    NotPlayingMode,

    /// <summary><c>hasLoadedGame</c> was false: the world is not readable yet.</summary>
    WorldNotLoaded,

    /// <summary>
    /// A completion for a read attempt named a different loader object than the one captured at
    /// start. This is the stale-attempt case: the pending attempt is left alone.
    /// </summary>
    StaleLoaderEvidence,

    /// <summary>The attempt was cancelled/invalidated before its completion.</summary>
    Reset,

    /// <summary>A newer attempt replaced an older pending one.</summary>
    Superseded,

    /// <summary>
    /// The start signal carried a reset epoch older than the coordinator's current one: a reset
    /// landed between the call site reading its evidence and the coordinator recording it.
    /// </summary>
    ResetEpochChanged,
}

/// <summary>How completion is correlated with the start signal.</summary>
public enum CorrelationMode
{
    /// <summary>
    /// The completion reads <c>Game1.currentLoader</c> and compares its identity with the loader
    /// captured by <c>loadForNewGame</c>. This is what the two hook sites can actually read.
    /// </summary>
    LoaderIdentity = 0,

    /// <summary>
    /// Counterfactual model: only "there is a pending attempt" is correlated. Used by the tests
    /// to show that a tokenless design cannot tell a stale completion from a fresh one.
    /// </summary>
    PendingOnly = 1,
}

/// <summary>One accepted internal observation.</summary>
public sealed class WorldReadyObservation
{
    public WorldReadyObservation(long attemptId, AttemptKind kind, bool attributionProven,
        int gameMode, bool hasLoadedGame, int threadId, long resetEpoch)
    {
        AttemptId = attemptId;
        Kind = kind;
        AttributionProven = attributionProven;
        GameMode = gameMode;
        HasLoadedGame = hasLoadedGame;
        ThreadId = threadId;
        ResetEpoch = resetEpoch;
    }

    public long AttemptId { get; }

    public AttemptKind Kind { get; }

    /// <summary>
    /// True only when the completion proved which attempt it belongs to (loader identity was
    /// captured at start and matched at completion). New games have no loader object, so their
    /// observation stays explicitly unproven instead of being guessed.
    /// </summary>
    public bool AttributionProven { get; }

    public int GameMode { get; }

    public bool HasLoadedGame { get; }

    public int ThreadId { get; }

    public long ResetEpoch { get; }
}

/// <summary>Result of one completion signal.</summary>
public sealed class CompletionOutcome
{
    public bool Accepted { get; internal set; }

    public RefusalKind Refusal { get; internal set; }

    public long AttemptId { get; internal set; }

    /// <summary>True when the refusal deliberately left the pending attempt untouched.</summary>
    public bool PendingPreserved { get; internal set; }

    public WorldReadyObservation? Observation { get; internal set; }
}

/// <summary>
/// Internal state coordinator for the SaveLoaded research contract.
///
/// <para><b>Contract.</b> One successful world entry produces at most one internal observation.
/// An attempt starts at <c>Game1.loadForNewGame(bool)</c>, completes at the normal return of
/// <c>Game1.OnDayStarted()</c>, and is invalidated by a reset/cancel signal. The completion must
/// additionally prove: the world is readable (<c>hasLoadedGame</c>), the mode is the playing
/// mode, the thread is the confirmed game loop, and (for read attempts) the loader object is the
/// same one the start captured.</para>
///
/// <para><b>What this class does not prove.</b> It only models the bookkeeping. It cannot make
/// the game supply a genuine attempt token, it cannot prove <c>currentLoader</c> is unchanged
/// between the two edges, and it cannot prove the real managed thread of
/// <c>OnDayStarted</c>. Those are runtime facts and stay NOT_RUN for this phase.</para>
///
/// <para><b>Threading.</b> One short lock protects the bookkeeping; handlers are never called
/// from inside it. The model is deliberately independent of the production dispatcher so that
/// testing it cannot change GameLaunched/Tick behaviour.</para>
/// </summary>
public sealed class WorldReadyCoordinator
{
    /// <summary><c>Game1.loadScreenGameMode</c>, the only mode a real read start runs in.</summary>
    public const int LoadScreenGameMode = 6;

    /// <summary><c>Game1.playingGameMode</c>.</summary>
    public const int PlayingGameMode = 3;

    private sealed class PendingAttempt
    {
        public long Id;
        public AttemptKind Kind;
        public object? Loader;
        public long ResetEpoch;
        public int StartThreadId;
    }

    private readonly object _gate = new object();
    private readonly CorrelationMode _mode;
    private readonly Dictionary<RefusalKind, long> _refusals = new Dictionary<RefusalKind, long>();
    private readonly List<WorldReadyObservation> _observations = new List<WorldReadyObservation>();

    private long _nextAttemptId;
    private long _resetEpoch;
    private PendingAttempt? _pending;
    private long _lastConsumedAttemptId;
    private int _gameLoopThreadId;
    private long _supersededAttempts;
    private long _cancelledAttempts;

    public WorldReadyCoordinator(CorrelationMode mode = CorrelationMode.LoaderIdentity)
    {
        _mode = mode;
    }

    public CorrelationMode Mode
    {
        get { return _mode; }
    }

    /// <summary>
    /// Confirms the one managed thread the game loop runs on. Set-once: a different second value
    /// is refused instead of silently moving the authority.
    /// </summary>
    public bool TrySetGameLoopThread(int threadId)
    {
        if (threadId <= 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (_gameLoopThreadId != 0)
            {
                return _gameLoopThreadId == threadId;
            }

            _gameLoopThreadId = threadId;
            return true;
        }
    }

    public int GameLoopThreadId
    {
        get
        {
            lock (_gate)
            {
                return _gameLoopThreadId;
            }
        }
    }

    public long ObservationCount
    {
        get
        {
            lock (_gate)
            {
                return _observations.Count;
            }
        }
    }

    public long SupersededAttempts
    {
        get
        {
            lock (_gate)
            {
                return _supersededAttempts;
            }
        }
    }

    public long CancelledAttempts
    {
        get
        {
            lock (_gate)
            {
                return _cancelledAttempts;
            }
        }
    }

    public long PendingAttemptId
    {
        get
        {
            lock (_gate)
            {
                return _pending?.Id ?? 0;
            }
        }
    }

    public long ResetEpoch
    {
        get
        {
            lock (_gate)
            {
                return _resetEpoch;
            }
        }
    }

    public IReadOnlyList<WorldReadyObservation> Observations
    {
        get
        {
            lock (_gate)
            {
                return _observations.ToArray();
            }
        }
    }

    public Dictionary<RefusalKind, long> Refusals
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<RefusalKind, long>(_refusals);
            }
        }
    }

    public long RefusalCount(RefusalKind kind)
    {
        lock (_gate)
        {
            return _refusals.TryGetValue(kind, out var count) ? count : 0;
        }
    }

    /// <summary>
    /// Registers an attempt. A read attempt is only accepted in the load-screen mode and with a
    /// loader object, because those are the two facts the completion edge can later check. A new
    /// game has no loader object, so its attribution stays explicitly unproven.
    ///
    /// <para><paramref name="observedResetEpoch"/> is the epoch the call site read <i>before</i>
    /// it read <c>gameMode</c> and the loader. Passing the current value (the overload below) is
    /// the weak two-hook behaviour; a stale value is refused, which is what closes the
    /// "start races with reset" counterexample when the call site can read an epoch first.</para>
    /// </summary>
    public bool TryBeginAttempt(bool loadedGame, int gameMode, object? loader, int threadId,
        long observedResetEpoch, out long attemptId, out RefusalKind refusal)
    {
        lock (_gate)
        {
            attemptId = 0;
            refusal = RefusalKind.None;

            if (observedResetEpoch != _resetEpoch)
            {
                refusal = RefusalKind.ResetEpochChanged;
                NoteRefusalLocked(refusal);
                return false;
            }

            return TryBeginAttemptLocked(loadedGame, gameMode, loader, threadId, ref attemptId,
                ref refusal);
        }
    }

    public bool TryBeginAttempt(bool loadedGame, int gameMode, object? loader, int threadId,
        out long attemptId, out RefusalKind refusal)
    {
        lock (_gate)
        {
            attemptId = 0;
            refusal = RefusalKind.None;
            return TryBeginAttemptLocked(loadedGame, gameMode, loader, threadId, ref attemptId,
                ref refusal);
        }
    }

    private bool TryBeginAttemptLocked(bool loadedGame, int gameMode, object? loader, int threadId,
        ref long attemptId, ref RefusalKind refusal)
    {
        if (loadedGame)
        {
            if (gameMode != LoadScreenGameMode)
            {
                refusal = RefusalKind.StartOutsideLoadMode;
                NoteRefusalLocked(refusal);
                return false;
            }

            if (loader is null)
            {
                refusal = RefusalKind.StartWithoutLoaderEvidence;
                NoteRefusalLocked(refusal);
                return false;
            }
        }

        if (_pending != null)
        {
            _supersededAttempts++;
        }

        var id = ++_nextAttemptId;
        _pending = new PendingAttempt
        {
            Id = id,
            Kind = loadedGame ? AttemptKind.Load : AttemptKind.NewGame,
            Loader = loadedGame ? loader : null,
            ResetEpoch = _resetEpoch,
            StartThreadId = threadId,
        };

        attemptId = id;
        return true;
    }

    /// <summary>
    /// Invalidates the current attempt. This models the cancel / return-to-title boundary that
    /// the two candidate hooks do not currently provide; it is an explicit input of this model,
    /// not an existing game signal.
    /// </summary>
    public void ResetWorld(int threadId, string reason)
    {
        lock (_gate)
        {
            if (_pending != null)
            {
                _pending = null;
                _cancelledAttempts++;
            }

            _resetEpoch++;
            _ = threadId;
            _ = reason;
        }
    }

    /// <summary>
    /// Consumes one completion signal.
    ///
    /// Refusals that mean "this completion does not describe a readable world" clear the pending
    /// attempt (it is dead). The stale-loader refusal is the one exception: it proves the
    /// completion belongs to an older attempt, so the newer pending attempt must stay intact.
    /// </summary>
    public CompletionOutcome Complete(int gameMode, bool hasLoadedGame, object? loader, int threadId)
    {
        lock (_gate)
        {
            var outcome = new CompletionOutcome();
            var pending = _pending;
            if (pending is null)
            {
                outcome.Refusal = _lastConsumedAttemptId != 0
                    ? RefusalKind.AlreadyConsumed
                    : RefusalKind.NoPendingAttempt;
                NoteRefusalLocked(outcome.Refusal);
                return outcome;
            }

            if (_gameLoopThreadId == 0)
            {
                RefuseAndClearLocked(pending, RefusalKind.UnknownGameLoopThread, outcome);
                return outcome;
            }

            if (threadId != _gameLoopThreadId)
            {
                RefuseAndClearLocked(pending, RefusalKind.WrongThread, outcome);
                return outcome;
            }

            if (gameMode == 0)
            {
                // Title-screen completion: the attempt was cancelled by returning to the title.
                RefuseAndClearLocked(pending, RefusalKind.TitleMode, outcome);
                return outcome;
            }

            if (gameMode != PlayingGameMode)
            {
                RefuseAndClearLocked(pending, RefusalKind.NotPlayingMode, outcome);
                return outcome;
            }

            if (!hasLoadedGame)
            {
                RefuseAndClearLocked(pending, RefusalKind.WorldNotLoaded, outcome);
                return outcome;
            }

            bool attributionProven;
            if (pending.Kind == AttemptKind.Load)
            {
                if (_mode == CorrelationMode.LoaderIdentity && !ReferenceEquals(loader, pending.Loader))
                {
                    // Do not clear: a stale completion must never swallow a newer attempt.
                    outcome.Refusal = RefusalKind.StaleLoaderEvidence;
                    outcome.PendingPreserved = true;
                    NoteRefusalLocked(outcome.Refusal);
                    return outcome;
                }

                attributionProven = _mode == CorrelationMode.LoaderIdentity;
            }
            else
            {
                attributionProven = false;
            }

            var observation = new WorldReadyObservation(
                pending.Id,
                pending.Kind,
                attributionProven,
                gameMode,
                hasLoadedGame,
                threadId,
                pending.ResetEpoch);

            _pending = null;
            _lastConsumedAttemptId = pending.Id;
            _observations.Add(observation);

            outcome.Accepted = true;
            outcome.AttemptId = pending.Id;
            outcome.Observation = observation;
            return outcome;
        }
    }

    private void RefuseAndClearLocked(PendingAttempt pending, RefusalKind refusal, CompletionOutcome outcome)
    {
        _pending = null;
        outcome.Refusal = refusal;
        outcome.PendingPreserved = false;
        NoteRefusalLocked(refusal);
    }

    private void NoteRefusalLocked(RefusalKind refusal)
    {
        _refusals.TryGetValue(refusal, out var count);
        _refusals[refusal] = count + 1;
    }
}
