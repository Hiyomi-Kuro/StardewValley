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

    /// <summary>
    /// A start or completion arrived while <c>ResetGameStateOnTitleScreen</c> had already begun but
    /// had not reached its normal return, so the game still holds the previous world's fields.
    /// </summary>
    ResetInProgress,

    /// <summary>A reset-completed signal arrived with no reset in progress.</summary>
    NoResetInProgress,
}

/// <summary>How completion is correlated with the start signal.</summary>
public enum CorrelationMode
{
    /// <summary>
    /// The completion reads <c>Game1.currentLoader</c> and compares its identity with the loader
    /// captured by <c>loadForNewGame</c>. This is what the hook sites can actually read.
    /// </summary>
    LoaderIdentity = 0,

    /// <summary>
    /// Counterfactual model: only "there is a pending attempt" is correlated. Used by the tests
    /// to show that a tokenless design cannot tell a stale completion from a fresh one.
    /// </summary>
    PendingOnly = 1,
}

/// <summary>
/// What the coordinator can actually prove about a completion's ownership.
///
/// <para><see cref="LoaderReference"/> means the completion read the same loader object the start
/// captured. That is reference equality, which is <b>not</b> proof that the completion belongs to
/// that attempt: a delayed completion of attempt A reads whatever
/// <c>Game1.currentLoader</c> holds at that moment, and after B started that value is B's loader
/// (counterexample B / P.1 §4.4). The enum keeps that limit explicit instead of calling the result
/// "proven".</para>
/// </summary>
public enum AttributionBasis
{
    /// <summary>Nothing was compared (new game, or pending-only model).</summary>
    None = 0,

    /// <summary>The loader reference captured at start equals the one read at completion.</summary>
    LoaderReference = 1,
}

/// <summary>One accepted internal observation.</summary>
public sealed class WorldReadyObservation
{
    public WorldReadyObservation(long attemptId, AttemptKind kind, AttributionBasis basis,
        int gameMode, bool hasLoadedGame, int threadId, long resetEpoch)
    {
        AttemptId = attemptId;
        Kind = kind;
        Basis = basis;
        GameMode = gameMode;
        HasLoadedGame = hasLoadedGame;
        ThreadId = threadId;
        ResetEpoch = resetEpoch;
    }

    public long AttemptId { get; }

    public AttemptKind Kind { get; }

    public AttributionBasis Basis { get; }

    /// <summary>
    /// True only when a loader reference was captured at start and matched at completion. It does
    /// <b>not</b> prove the completion belongs to this attempt; see <see cref="AttributionBasis"/>.
    /// </summary>
    public bool AttributionProven
    {
        get { return Basis == AttributionBasis.LoaderReference; }
    }

    /// <summary>
    /// Always false for the two-hook design: no signal carries an explicit attempt token, so this
    /// coordinator never claims that a completion is provably owned by the pending attempt.
    /// </summary>
    public bool AttemptOwnershipProven
    {
        get { return false; }
    }

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

/// <summary>Kinds of accepted/refused signals, in the order they happened.</summary>
public enum SignalKind
{
    ResetBegan = 0,
    ResetCompleted,
    StartAccepted,
    StartRefused,
    CompletionAccepted,
    CompletionRefused,
}

/// <summary>One entry of the coordinator's ordered signal trace (diagnostics only).</summary>
public sealed class SignalRecord
{
    public SignalRecord(long sequence, SignalKind kind, long attemptId, long resetEpoch,
        RefusalKind refusal, int threadId)
    {
        Sequence = sequence;
        Kind = kind;
        AttemptId = attemptId;
        ResetEpoch = resetEpoch;
        Refusal = refusal;
        ThreadId = threadId;
    }

    public long Sequence { get; }

    public SignalKind Kind { get; }

    public long AttemptId { get; }

    public long ResetEpoch { get; }

    public RefusalKind Refusal { get; }

    public int ThreadId { get; }

    public override string ToString()
    {
        return Sequence + ":" + Kind + "(attempt=" + AttemptId + ", epoch=" + ResetEpoch +
               ", refusal=" + Refusal + ", thread=" + ThreadId + ")";
    }
}

/// <summary>
/// Internal state coordinator for the SaveLoaded research contract.
///
/// <para><b>Contract.</b> One successful world entry produces at most one internal observation.
/// An attempt starts at <c>Game1.loadForNewGame(bool)</c>, completes at the normal return of
/// <c>Game1.OnDayStarted()</c>, and is invalidated by the reset boundary at
/// <c>Game1.ResetGameStateOnTitleScreen()</c>.</para>
///
/// <para><b>Reset epoch.</b> The reset boundary increments an epoch. A start path must read that
/// epoch <i>before</i> it reads <c>gameMode</c>/<c>currentLoader</c> and pass it to
/// <see cref="TryBeginAttempt(bool,int,object?,int,long,out long,out RefusalKind)"/>. Because the
/// real reset method clears <c>hasLoadedGame</c>, <c>currentLoader</c> and (early)
/// <c>gameMode</c> only part-way through its 543 instructions, a start that read the epoch
/// <i>after</i> the increment could otherwise capture the previous world's fields; the
/// reset-in-progress latch refuses exactly that window (see <see cref="TryBeginReset"/>).</para>
///
/// <para><b>What this class does not prove.</b> It only models the bookkeeping. It cannot make the
/// game supply a genuine attempt token, it cannot prove <c>currentLoader</c> is unchanged between
/// the two edges, and it cannot prove the real managed thread of <c>OnDayStarted</c>. Those are
/// runtime facts and stay NOT_RUN for this phase.</para>
///
/// <para><b>Threading.</b> One short lock protects the bookkeeping; handlers are never called from
/// inside it. The model is deliberately independent of the production dispatcher so that testing it
/// cannot change GameLaunched/Tick behaviour.</para>
/// </summary>
public sealed class WorldReadyCoordinator
{
    /// <summary><c>Game1.loadScreenGameMode</c>, the only mode a real read start runs in.</summary>
    public const int LoadScreenGameMode = 6;

    /// <summary><c>Game1.playingGameMode</c>.</summary>
    public const int PlayingGameMode = 3;

    private const int MaxSignals = 1024;

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
    private readonly bool _honorResetInProgress;
    private readonly Dictionary<RefusalKind, long> _refusals = new Dictionary<RefusalKind, long>();
    private readonly Dictionary<RefusalKind, long> _resetRefusals = new Dictionary<RefusalKind, long>();
    private readonly List<WorldReadyObservation> _observations = new List<WorldReadyObservation>();
    private readonly List<SignalRecord> _signals = new List<SignalRecord>();

    private long _nextAttemptId;
    private long _nextSignalSequence;
    private long _resetEpoch;
    private bool _resetting;
    private PendingAttempt? _pending;
    private long _lastConsumedAttemptId;
    private int _gameLoopThreadId;
    private long _supersededAttempts;
    private long _cancelledAttempts;
    private long _resetCount;
    private long _completedResetCount;
    private long _signalsDropped;

    public WorldReadyCoordinator(CorrelationMode mode = CorrelationMode.LoaderIdentity,
        bool honorResetInProgress = true)
    {
        _mode = mode;
        _honorResetInProgress = honorResetInProgress;
    }

    public CorrelationMode Mode
    {
        get { return _mode; }
    }

    /// <summary>
    /// False only for the negative control that proves the reset-in-progress guard is what closes
    /// the "new epoch, previous world's fields" window. Production/prototype wiring leaves it true.
    /// </summary>
    public bool HonorsResetInProgress
    {
        get { return _honorResetInProgress; }
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

    public long ResetCount
    {
        get
        {
            lock (_gate)
            {
                return _resetCount;
            }
        }
    }

    public long CompletedResetCount
    {
        get
        {
            lock (_gate)
            {
                return _completedResetCount;
            }
        }
    }

    /// <summary>True between a reset entry and its normal return.</summary>
    public bool ResetInProgress
    {
        get
        {
            lock (_gate)
            {
                return _resetting;
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

    /// <summary>The ordered signal trace, for tests only. Bounded; drops are counted.</summary>
    public IReadOnlyList<SignalRecord> Signals
    {
        get
        {
            lock (_gate)
            {
                return _signals.ToArray();
            }
        }
    }

    public long SignalsDropped
    {
        get
        {
            lock (_gate)
            {
                return _signalsDropped;
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

    public Dictionary<RefusalKind, long> ResetRefusals
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<RefusalKind, long>(_resetRefusals);
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

    public long ResetRefusalCount(RefusalKind kind)
    {
        lock (_gate)
        {
            return _resetRefusals.TryGetValue(kind, out var count) ? count : 0;
        }
    }

    /// <summary>
    /// The reset boundary: <c>Game1.ResetGameStateOnTitleScreen()</c> entry.
    ///
    /// <para>Commit order inside the lock: (1) the pending attempt is dropped, (2) the epoch is
    /// incremented, (3) the in-progress latch is set. All three become visible together, so a
    /// concurrent start sees either the old epoch (and is refused) or the new epoch plus the
    /// latch (and is refused until the normal return).</para>
    ///
    /// <para>A second entry while the latch is set is accepted and re-arms it: that is the only
    /// recovery from a reset that threw before its normal return (see <see cref="TryCompleteReset"/>).</para>
    /// </summary>
    public bool TryBeginReset(int threadId, string reason, out long resetEpoch, out RefusalKind refusal)
    {
        lock (_gate)
        {
            resetEpoch = _resetEpoch;
            refusal = RefusalKind.None;

            if (_gameLoopThreadId != 0 && threadId != _gameLoopThreadId)
            {
                // The reset boundary is a game-loop event. A reset from another thread is not
                // modelled as a valid world transition; refuse and change nothing.
                refusal = RefusalKind.WrongThread;
                NoteResetRefusalLocked(refusal);
                NoteSignalLocked(SignalKind.ResetBegan, 0, _resetEpoch, refusal, threadId);
                return false;
            }

            if (_pending != null)
            {
                _pending = null;
                _cancelledAttempts++;
            }

            _resetEpoch++;
            _resetting = true;
            _resetCount++;
            _ = reason;

            resetEpoch = _resetEpoch;
            NoteSignalLocked(SignalKind.ResetBegan, 0, _resetEpoch, RefusalKind.None, threadId);
            return true;
        }
    }

    /// <summary>
    /// The reset boundary's normal return. Clears the in-progress latch and records that the
    /// previous world's fields have been cleaned.
    ///
    /// <para>There is deliberately no hook on an exceptional exit: if
    /// <c>ResetGameStateOnTitleScreen</c> throws, this method is not called and the latch stays
    /// set, so every later start/completion is refused with <see cref="RefusalKind.ResetInProgress"/>
    /// until a later reset reaches its normal return.</para>
    /// </summary>
    public bool TryCompleteReset(int threadId, out RefusalKind refusal)
    {
        lock (_gate)
        {
            refusal = RefusalKind.None;

            if (_gameLoopThreadId != 0 && threadId != _gameLoopThreadId)
            {
                refusal = RefusalKind.WrongThread;
                NoteResetRefusalLocked(refusal);
                NoteSignalLocked(SignalKind.ResetCompleted, 0, _resetEpoch, refusal, threadId);
                return false;
            }

            if (!_resetting)
            {
                refusal = RefusalKind.NoResetInProgress;
                NoteResetRefusalLocked(refusal);
                NoteSignalLocked(SignalKind.ResetCompleted, 0, _resetEpoch, refusal, threadId);
                return false;
            }

            _resetting = false;
            _completedResetCount++;
            NoteSignalLocked(SignalKind.ResetCompleted, 0, _resetEpoch, RefusalKind.None, threadId);
            return true;
        }
    }

    /// <summary>Model-only reset that does not model the in-progress window (kept for A–E cases).</summary>
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
            NoteSignalLocked(SignalKind.ResetBegan, 0, _resetEpoch, RefusalKind.None, threadId);
            _ = reason;
        }
    }

    /// <summary>
    /// Registers an attempt. A read attempt is only accepted in the load-screen mode and with a
    /// loader object, because those are the two facts the completion edge can later check. A new
    /// game has no loader object, so its attribution stays explicitly unproven.
    ///
    /// <para><paramref name="observedResetEpoch"/> is the epoch the call site read <i>before</i>
    /// it read <c>gameMode</c> and the loader. Passing the current value (the overload below) is
    /// the weak two-hook behaviour; a stale value is refused, and a start that lands inside the
    /// reset window is refused even though its epoch is current.</para>
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
                NoteSignalLocked(SignalKind.StartRefused, 0, _resetEpoch, refusal, threadId);
                return false;
            }

            if (_honorResetInProgress && _resetting)
            {
                refusal = RefusalKind.ResetInProgress;
                NoteRefusalLocked(refusal);
                NoteSignalLocked(SignalKind.StartRefused, 0, _resetEpoch, refusal, threadId);
                return false;
            }

            return BeginLocked(loadedGame, gameMode, loader, threadId, ref attemptId, ref refusal);
        }
    }

    public bool TryBeginAttempt(bool loadedGame, int gameMode, object? loader, int threadId,
        out long attemptId, out RefusalKind refusal)
    {
        lock (_gate)
        {
            attemptId = 0;
            refusal = RefusalKind.None;

            if (_honorResetInProgress && _resetting)
            {
                refusal = RefusalKind.ResetInProgress;
                NoteRefusalLocked(refusal);
                NoteSignalLocked(SignalKind.StartRefused, 0, _resetEpoch, refusal, threadId);
                return false;
            }

            return BeginLocked(loadedGame, gameMode, loader, threadId, ref attemptId, ref refusal);
        }
    }

    private bool BeginLocked(bool loadedGame, int gameMode, object? loader, int threadId,
        ref long attemptId, ref RefusalKind refusal)
    {
        if (loadedGame)
        {
            if (gameMode != LoadScreenGameMode)
            {
                refusal = RefusalKind.StartOutsideLoadMode;
                NoteRefusalLocked(refusal);
                NoteSignalLocked(SignalKind.StartRefused, 0, _resetEpoch, refusal, threadId);
                return false;
            }

            if (loader is null)
            {
                refusal = RefusalKind.StartWithoutLoaderEvidence;
                NoteRefusalLocked(refusal);
                NoteSignalLocked(SignalKind.StartRefused, 0, _resetEpoch, refusal, threadId);
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
        NoteSignalLocked(SignalKind.StartAccepted, id, _resetEpoch, RefusalKind.None, threadId);
        return true;
    }

    /// <summary>
    /// Consumes one completion signal.
    ///
    /// <para>Refusals that mean "this completion does not describe a readable world" clear the
    /// pending attempt (it is dead). The stale-loader refusal is the one exception: it proves the
    /// completion read a different loader object, so the newer pending attempt must stay intact.
    /// A completion that lands inside the reset window is refused without changing anything: the
    /// reset already cancelled the pending attempt.</para>
    /// </summary>
    public CompletionOutcome Complete(int gameMode, bool hasLoadedGame, object? loader, int threadId)
    {
        lock (_gate)
        {
            var outcome = new CompletionOutcome();

            if (_resetting)
            {
                outcome.Refusal = RefusalKind.ResetInProgress;
                NoteRefusalLocked(outcome.Refusal);
                NoteSignalLocked(SignalKind.CompletionRefused, 0, _resetEpoch, outcome.Refusal, threadId);
                return outcome;
            }

            var pending = _pending;
            if (pending is null)
            {
                outcome.Refusal = _lastConsumedAttemptId != 0
                    ? RefusalKind.AlreadyConsumed
                    : RefusalKind.NoPendingAttempt;
                NoteRefusalLocked(outcome.Refusal);
                NoteSignalLocked(SignalKind.CompletionRefused, 0, _resetEpoch, outcome.Refusal, threadId);
                return outcome;
            }

            if (_gameLoopThreadId == 0)
            {
                RefuseAndClearLocked(pending, RefusalKind.UnknownGameLoopThread, outcome, threadId);
                return outcome;
            }

            if (threadId != _gameLoopThreadId)
            {
                RefuseAndClearLocked(pending, RefusalKind.WrongThread, outcome, threadId);
                return outcome;
            }

            if (gameMode == 0)
            {
                // Title-screen completion: the attempt was cancelled by returning to the title.
                RefuseAndClearLocked(pending, RefusalKind.TitleMode, outcome, threadId);
                return outcome;
            }

            if (gameMode != PlayingGameMode)
            {
                RefuseAndClearLocked(pending, RefusalKind.NotPlayingMode, outcome, threadId);
                return outcome;
            }

            if (!hasLoadedGame)
            {
                RefuseAndClearLocked(pending, RefusalKind.WorldNotLoaded, outcome, threadId);
                return outcome;
            }

            var basis = AttributionBasis.None;
            if (pending.Kind == AttemptKind.Load)
            {
                if (_mode == CorrelationMode.LoaderIdentity && !ReferenceEquals(loader, pending.Loader))
                {
                    // Do not clear: a stale completion must never swallow a newer attempt.
                    outcome.Refusal = RefusalKind.StaleLoaderEvidence;
                    outcome.PendingPreserved = true;
                    NoteRefusalLocked(outcome.Refusal);
                    NoteSignalLocked(SignalKind.CompletionRefused, pending.Id, _resetEpoch,
                        outcome.Refusal, threadId);
                    return outcome;
                }

                if (_mode == CorrelationMode.LoaderIdentity)
                {
                    basis = AttributionBasis.LoaderReference;
                }
            }

            var observation = new WorldReadyObservation(
                pending.Id,
                pending.Kind,
                basis,
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
            NoteSignalLocked(SignalKind.CompletionAccepted, pending.Id, _resetEpoch, RefusalKind.None,
                threadId);
            return outcome;
        }
    }

    private void RefuseAndClearLocked(PendingAttempt pending, RefusalKind refusal,
        CompletionOutcome outcome, int threadId)
    {
        _pending = null;
        outcome.Refusal = refusal;
        outcome.PendingPreserved = false;
        NoteRefusalLocked(refusal);
        NoteSignalLocked(SignalKind.CompletionRefused, pending.Id, _resetEpoch, refusal, threadId);
    }

    private void NoteRefusalLocked(RefusalKind refusal)
    {
        _refusals.TryGetValue(refusal, out var count);
        _refusals[refusal] = count + 1;
    }

    private void NoteResetRefusalLocked(RefusalKind refusal)
    {
        _resetRefusals.TryGetValue(refusal, out var count);
        _resetRefusals[refusal] = count + 1;
    }

    private void NoteSignalLocked(SignalKind kind, long attemptId, long resetEpoch,
        RefusalKind refusal, int threadId)
    {
        if (_signals.Count >= MaxSignals)
        {
            _signalsDropped++;
            return;
        }

        _signals.Add(new SignalRecord(++_nextSignalSequence, kind, attemptId, resetEpoch, refusal,
            threadId));
    }
}
