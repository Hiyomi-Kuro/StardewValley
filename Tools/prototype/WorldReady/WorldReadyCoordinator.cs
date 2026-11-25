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

    /// <summary>
    /// A load-advance signal named a loader object that is not the pending attempt's own state
    /// machine. The pending attempt is <b>preserved</b>: a foreign advance must never consume or
    /// clear a newer attempt (P.2 §"ownership before consumption").
    /// </summary>
    ForeignAttemptIdentity,

    /// <summary>
    /// A load-advance signal arrived with no usable identity (null loader, or a pending attempt
    /// that was never given a proven state-machine identity). The pending attempt is preserved.
    /// </summary>
    MissingAttemptIdentity,

    /// <summary>
    /// Ownership of the completion was proven (the advanced state machine is the pending attempt's
    /// own object), but the signal did not run on the confirmed game-loop thread. Defined behaviour:
    /// the pending attempt is <b>preserved</b>, because an off-thread advance is not trustworthy
    /// evidence that the attempt reached its terminal state.
    /// </summary>
    OwnedButWrongThread,

    /// <summary>
    /// Ownership was proven and the handoff reached the playing mode, but <c>hasLoadedGame</c> was
    /// false, so the world is not readable. Defined behaviour: the attempt is <b>consumed without an
    /// observation</b> (it is provably over), and the refusal is counted separately.
    /// </summary>
    OwnedButWorldNotLoaded,

    /// <summary>
    /// An owned load enumerator finished (<c>MoveNext()==false</c>) while the game was still in the
    /// loading mode: the load failed or was abandoned. The attempt is consumed without an observation.
    /// </summary>
    LoadFailed,
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

    /// <summary>
    /// P.2: the completion edge names the load state machine object it just advanced. The identity
    /// is a value the game itself carries (the <c>IEnumerator&lt;int&gt;</c> created by
    /// <c>SaveGame.getLoadEnumerator</c> and stored in <c>Game1.currentLoader</c>), not a token this
    /// prototype invents. See <see cref="WorldReadyCoordinator.Advance"/>.
    /// </summary>
    StateMachineIdentity = 2,
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

    /// <summary>
    /// P.2: the completion edge reported the load state machine it advanced, and that object is the
    /// one captured for the pending attempt. This is the only basis for which
    /// <see cref="WorldReadyObservation.AttemptOwnershipProven"/> is true.
    /// </summary>
    LoadStateMachine = 2,
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
    /// True only when the completion edge carried the pending attempt's own load state machine
    /// object (P.2). The legacy loader-reference basis keeps this false: reference equality between
    /// two reads of a global is not ownership.
    /// </summary>
    public bool AttemptOwnershipProven
    {
        get { return Basis == AttributionBasis.LoadStateMachine; }
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

    /// <summary>P.2: the loading-mode driver advanced the pending load state machine (no state change).</summary>
    LoadAdvanced,
}

/// <summary>What one load-advance signal turned out to be.</summary>
public enum AdvanceOutcomeKind
{
    /// <summary>The signal was not about a pending load attempt.</summary>
    None = 0,

    /// <summary>The enumerator advanced and the load is still in progress (mode 6, more work).</summary>
    InProgress,

    /// <summary>The enumerator reached the playing mode and produced one observation.</summary>
    Completed,

    /// <summary>The owned attempt reached a terminal state without an observation.</summary>
    Failed,

    /// <summary>The signal was refused; see <see cref="LoadAdvanceOutcome.Refusal"/>.</summary>
    Refused,
}

/// <summary>Result of one load-advance signal (P.2 completion edge).</summary>
public sealed class LoadAdvanceOutcome
{
    public AdvanceOutcomeKind Kind { get; internal set; }

    public RefusalKind Refusal { get; internal set; }

    public long AttemptId { get; internal set; }

    /// <summary>True when the reported object was the pending attempt's own state machine.</summary>
    public bool OwnershipVerified { get; internal set; }

    /// <summary>True when the refusal deliberately left the pending attempt untouched.</summary>
    public bool PendingPreserved { get; internal set; }

    /// <summary>True when the signal consumed (cleared) the pending attempt.</summary>
    public bool PendingCleared { get; internal set; }

    public WorldReadyObservation? Observation { get; internal set; }
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
    /// <summary>
    /// <b>Mode 6 is <c>Game1</c>'s <i>loadingMode</i>, not <c>loadScreenGameMode</c>.</b> In
    /// 1.6.15.24354 the literal fields are <c>titleScreenGameMode=0</c>, <c>loadScreenGameMode=1</c>,
    /// <c>newGameMode=2</c>, <c>playingGameMode=3</c>, <c>logoScreenGameMode=4</c>; mode 6 is the
    /// name "loadingMode" in <c>Game1.GameModeToString</c>. <c>SaveGame.LoadAfterPermissionCheck</c>
    /// sets mode 6 before it stores the load enumerator, and <c>Game1.UpdateTitleScreen</c>
    /// dispatches <c>UpdateTitleScreenDuringLoadingMode</c> on mode 6, so 6 is the mode a real load
    /// attempt starts in. The name is kept for source compatibility with P.1 call sites; use
    /// <see cref="LoadingGameMode"/> in new code.
    /// </summary>
    public const int LoadScreenGameMode = 6;

    /// <summary><c>Game1</c> mode 6, the loading mode a load attempt starts in (see above).</summary>
    public const int LoadingGameMode = 6;

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

        /// <summary>
        /// The object that proves which attempt a completion belongs to. For a P.2 load attempt this
        /// is the <c>IEnumerator&lt;int&gt;</c> state machine created by
        /// <c>SaveGame.getLoadEnumerator</c>; <see cref="IdentityProven"/> is true only when the
        /// start edge obtained that object directly from the load attempt itself.
        /// </summary>
        public object? Identity;

        public bool IdentityProven;
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
    private long _advancesObserved;
    private long _ownedTerminalWithoutObservation;

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

            // P.2: when the coordinator is in identity mode, the ownership-carrying edge is
            // Advance(...). A signal arriving through the legacy edge (the OnDayStarted hook) carries
            // no attempt identity at all, so it must not be allowed to consume a proven attempt.
            if (_mode == CorrelationMode.StateMachineIdentity && pending.IdentityProven)
            {
                outcome.Refusal = RefusalKind.MissingAttemptIdentity;
                outcome.PendingPreserved = true;
                NoteRefusalLocked(outcome.Refusal);
                NoteSignalLocked(SignalKind.CompletionRefused, pending.Id, _resetEpoch,
                    outcome.Refusal, threadId);
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
            {                if (_mode == CorrelationMode.LoaderIdentity && !ReferenceEquals(loader, pending.Loader))
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

    // ---------------------------------------------------------------- P.2 attempt ownership

    /// <summary>Number of load-advance signals the coordinator has been given.</summary>
    public long AdvancesObserved
    {
        get
        {
            lock (_gate)
            {
                return _advancesObserved;
            }
        }
    }

    /// <summary>
    /// Owned attempts that reached a terminal state without producing an observation (thread or
    /// readiness guard failed, or the load failed). Kept separate so "observed" and "provably over
    /// but unusable" are never conflated.
    /// </summary>
    public long OwnedTerminalWithoutObservation
    {
        get
        {
            lock (_gate)
            {
                return _ownedTerminalWithoutObservation;
            }
        }
    }

    /// <summary>
    /// The P.2 start edge: registers a load attempt together with the state machine object that
    /// proves which attempt a later completion belongs to.
    ///
    /// <para><paramref name="identity"/> must be the <c>IEnumerator&lt;int&gt;</c> the load attempt
    /// created (<c>Game1.currentLoader</c> at <c>loadForNewGame</c> time), not a token this
    /// coordinator invents. The epoch is read <i>before</i> the game fields and passed in, exactly as
    /// in P.1, so the reset window semantics are unchanged.</para>
    /// </summary>
    public bool BeginLoad(object identity, int gameMode, long observedResetEpoch, int threadId,
        out long attemptId, out RefusalKind refusal)
    {
        lock (_gate)
        {
            attemptId = 0;
            refusal = RefusalKind.None;

            if (identity is null)
            {
                return RefuseStartLocked(RefusalKind.StartWithoutLoaderEvidence, threadId,
                    ref refusal);
            }

            if (observedResetEpoch != _resetEpoch)
            {
                return RefuseStartLocked(RefusalKind.ResetEpochChanged, threadId, ref refusal);
            }

            if (_honorResetInProgress && _resetting)
            {
                return RefuseStartLocked(RefusalKind.ResetInProgress, threadId, ref refusal);
            }

            if (gameMode != LoadingGameMode)
            {
                return RefuseStartLocked(RefusalKind.StartOutsideLoadMode, threadId, ref refusal);
            }

            if (_pending != null)
            {
                _supersededAttempts++;
            }

            var id = ++_nextAttemptId;
            _pending = new PendingAttempt
            {
                Id = id,
                Kind = AttemptKind.Load,
                Loader = identity,
                Identity = identity,
                IdentityProven = true,
                ResetEpoch = _resetEpoch,
                StartThreadId = threadId,
            };

            attemptId = id;
            NoteSignalLocked(SignalKind.StartAccepted, id, _resetEpoch, RefusalKind.None, threadId);
            return true;
        }
    }

    private bool RefuseStartLocked(RefusalKind refusal, int threadId, ref RefusalKind result)
    {
        result = refusal;
        NoteRefusalLocked(refusal);
        NoteSignalLocked(SignalKind.StartRefused, 0, _resetEpoch, refusal, threadId);
        return false;
    }

    /// <summary>
    /// The P.2 completion edge: the loading-mode driver advanced the load state machine.
    ///
    /// <para><paramref name="identity"/> is the object whose <c>MoveNext()</c> was just called, taken
    /// from a local in the injected code — not re-read from a global. <paramref name="gameMode"/> is
    /// read after the advance, so a value of <see cref="PlayingGameMode"/> means the enumerator itself
    /// flipped the mode, which is the game's own success handoff.</para>
    ///
    /// <para><b>Ownership is checked before anything else is consumed, cancelled or cleared.</b>
    /// A missing, unknown, stale or foreign identity always preserves the pending attempt, so a late
    /// signal can never destroy a newer one.</para>
    /// </summary>
    public LoadAdvanceOutcome Advance(object? identity, int gameMode, bool moved,
        bool hasLoadedGame, int threadId)
    {
        lock (_gate)
        {
            _advancesObserved++;
            var outcome = new LoadAdvanceOutcome();

            if (_resetting)
            {
                return RefuseAdvanceLocked(outcome, RefusalKind.ResetInProgress, threadId,
                    preserve: true);
            }

            var pending = _pending;
            if (pending is null)
            {
                outcome.Kind = AdvanceOutcomeKind.None;
                outcome.Refusal = _lastConsumedAttemptId != 0
                    ? RefusalKind.AlreadyConsumed
                    : RefusalKind.NoPendingAttempt;
                NoteRefusalLocked(outcome.Refusal);
                NoteSignalLocked(SignalKind.CompletionRefused, 0, _resetEpoch, outcome.Refusal,
                    threadId);
                return outcome;
            }

            // (1) Ownership first. Nothing below this point runs for an unowned identity.
            if (identity is null || !pending.IdentityProven || pending.Identity is null)
            {
                return RefuseAdvanceLocked(outcome, RefusalKind.MissingAttemptIdentity, threadId,
                    preserve: true);
            }

            if (!ReferenceEquals(identity, pending.Identity))
            {
                return RefuseAdvanceLocked(outcome, RefusalKind.ForeignAttemptIdentity, threadId,
                    preserve: true);
            }

            outcome.OwnershipVerified = true;
            outcome.AttemptId = pending.Id;

            // (2) The enumerator finished while still in the loading mode: failed/abandoned load.
            if (!moved && gameMode != PlayingGameMode)
            {
                var refusal = gameMode == 0 ? RefusalKind.TitleMode : RefusalKind.LoadFailed;
                return ConsumeOwnedLocked(pending, outcome, AdvanceOutcomeKind.Failed, refusal,
                    threadId);
            }

            // (3) Still loading.
            if (gameMode != PlayingGameMode)
            {
                outcome.Kind = AdvanceOutcomeKind.InProgress;
                NoteSignalLocked(SignalKind.LoadAdvanced, pending.Id, _resetEpoch, RefusalKind.None,
                    threadId);
                return outcome;
            }

            // (4) Owned attempt reached the playing mode: this is the success handoff.
            if (_gameLoopThreadId == 0)
            {
                // Ownership is proven, but there is no thread authority to judge the signal. Defined
                // behaviour: preserve the attempt and refuse; it is not evidence the attempt ended.
                return RefuseOwnedLocked(pending, outcome, RefusalKind.UnknownGameLoopThread,
                    threadId, preserve: true);
            }

            if (threadId != _gameLoopThreadId)
            {
                // Ownership proven, thread guard failed => preserve (an off-thread advance is not
                // trustworthy evidence that the attempt is over).
                return RefuseOwnedLocked(pending, outcome, RefusalKind.OwnedButWrongThread, threadId,
                    preserve: true);
            }

            if (!hasLoadedGame)
            {
                // Ownership proven, readiness guard failed => the attempt is provably over, so it is
                // consumed without an observation.
                return ConsumeOwnedLocked(pending, outcome, AdvanceOutcomeKind.Failed,
                    RefusalKind.OwnedButWorldNotLoaded, threadId);
            }

            var observation = new WorldReadyObservation(
                pending.Id,
                pending.Kind,
                AttributionBasis.LoadStateMachine,
                gameMode,
                hasLoadedGame,
                threadId,
                pending.ResetEpoch);

            _pending = null;
            _lastConsumedAttemptId = pending.Id;
            _observations.Add(observation);

            outcome.Kind = AdvanceOutcomeKind.Completed;
            outcome.PendingCleared = true;
            outcome.Observation = observation;
            NoteSignalLocked(SignalKind.CompletionAccepted, pending.Id, _resetEpoch, RefusalKind.None,
                threadId);
            return outcome;
        }
    }

    private LoadAdvanceOutcome RefuseAdvanceLocked(LoadAdvanceOutcome outcome, RefusalKind refusal,
        int threadId, bool preserve)
    {
        outcome.Kind = AdvanceOutcomeKind.Refused;
        outcome.Refusal = refusal;
        outcome.PendingPreserved = preserve;
        NoteRefusalLocked(refusal);
        NoteSignalLocked(SignalKind.CompletionRefused, 0, _resetEpoch, refusal, threadId);
        return outcome;
    }

    private LoadAdvanceOutcome RefuseOwnedLocked(PendingAttempt pending, LoadAdvanceOutcome outcome,
        RefusalKind refusal, int threadId, bool preserve)
    {
        outcome.Kind = AdvanceOutcomeKind.Refused;
        outcome.Refusal = refusal;
        outcome.PendingPreserved = preserve;
        if (!preserve)
        {
            _pending = null;
            outcome.PendingCleared = true;
            _ownedTerminalWithoutObservation++;
        }

        NoteRefusalLocked(refusal);
        NoteSignalLocked(SignalKind.CompletionRefused, pending.Id, _resetEpoch, refusal, threadId);
        return outcome;
    }

    private LoadAdvanceOutcome ConsumeOwnedLocked(PendingAttempt pending, LoadAdvanceOutcome outcome,
        AdvanceOutcomeKind kind, RefusalKind refusal, int threadId)
    {
        _pending = null;
        _lastConsumedAttemptId = pending.Id;
        _ownedTerminalWithoutObservation++;

        outcome.Kind = kind;
        outcome.Refusal = refusal;
        outcome.PendingCleared = true;
        NoteRefusalLocked(refusal);
        NoteSignalLocked(SignalKind.CompletionRefused, pending.Id, _resetEpoch, refusal, threadId);
        return outcome;
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
