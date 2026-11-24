using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Phase21.WorldReady;

namespace Phase21PrototypeTests;

/// <summary>
/// Phase 2.1-P.1 host acceptance matrix.
///
/// <para>Section 1 exercises the coordinator with explicit interleavings. Section 2 builds a
/// synthetic assembly with Cecil, injects the three prototype hooks (reset, start, completion),
/// writes it, loads it and executes it. Both are offline: no game assembly is loaded, no device is
/// touched.</para>
///
/// <para>Usage:</para>
/// <code>
/// dotnet run --project Tools/tests/PrototypeTests
/// dotnet run --project Tools/tests/PrototypeTests -- --target &lt;in.dll&gt; &lt;out.dll&gt;
/// dotnet run --project Tools/tests/PrototypeTests -- --verify-target &lt;patched.dll&gt;
/// </code>
/// </summary>
internal static class Program
{
    private static int _checks;
    private static int _failures;
    private static string _work = "";

    internal static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--target")
        {
            return TargetCheck.Apply(args[1], args[2], args.Length > 3 ? args[3] : null);
        }

        if (args.Length >= 2 && args[0] == "--verify-target")
        {
            return TargetCheck.VerifyOnly(args[1], args.Length > 2 ? args[2] : null);
        }

        _work = Path.Combine(Path.GetTempPath(),
            "phase21-prototype-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_work);
        try
        {
            CoordinatorCases();
            SyntheticCases();
        }
        catch (Exception ex)
        {
            Check("suite_completed", false, ex.GetType().Name + ": " + ex.Message);
            Console.WriteLine(ex);
        }
        finally
        {
            PrototypeBridge.Install(null);
            try
            {
                Directory.Delete(_work, true);
            }
            catch (Exception)
            {
                // loaded assemblies can keep files locked on Windows; best effort only
            }
        }

        Console.WriteLine();
        Console.WriteLine(_checks + " checks, " + (_checks - _failures) + " passed, " + _failures + " failed");
        return _failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ section 1: coordinator

    private static void CoordinatorCases()
    {
        ColdStartAndNoStart();
        LoadAndNewGameEntries();
        StartGuards();
        PostConsumptionCompletions();
        TitleAndModeGuards();
        ThreadGuards();
        ReloadAndSwitch();
        FailureAndCancel();
        StaleAttemptDoesNotEatNext();
        PendingOnlyModeMisattributes();
        SupersededAttempt();
        ResetEpochClosesStartRace();
        CompleteVersusResetRace();
        ResetLifecycle();
        ResetVersusPending();
        ResetCommitOrder();
        LateCompletionReadsBLoader();
        ResetThreadCases();
    }

    private static void ColdStartAndNoStart()
    {
        var c = NewCoordinator();
        var outcome = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, null, Tid);
        Check("cold_start_completion_zero", !outcome.Accepted &&
                                            outcome.Refusal == RefusalKind.NoPendingAttempt &&
                                            c.ObservationCount == 0,
            "refusal=" + outcome.Refusal);
    }

    private static void LoadAndNewGameEntries()
    {
        var c = NewCoordinator();
        var loader = new object();
        var begun = c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loader, Tid,
            out var id, out var refusal);
        var outcome = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, loader, Tid);
        Check("load_entry_exactly_one", begun && outcome.Accepted && c.ObservationCount == 1 &&
                                        outcome.AttemptId == id &&
                                        outcome.Observation is { AttributionProven: true } &&
                                        outcome.Observation.AttemptOwnershipProven == false &&
                                        outcome.Observation.Kind == AttemptKind.Load,
            "begun=" + begun + ", refusal=" + refusal + ", result=" + outcome.Refusal);

        c = NewCoordinator();
        begun = c.TryBeginAttempt(false, 0, null, Tid, out id, out refusal);
        outcome = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, null, Tid);
        Check("new_game_entry_exactly_one", begun && outcome.Accepted && c.ObservationCount == 1 &&
                                            outcome.Observation is
                                            {
                                                Kind: AttemptKind.NewGame,
                                                AttributionProven: false,
                                                Basis: AttributionBasis.None,
                                            },
            "begun=" + begun + ", refusal=" + refusal + ", result=" + outcome.Refusal);
    }

    private static void StartGuards()
    {
        var c = NewCoordinator();
        var loader = new object();
        var begun = c.TryBeginAttempt(true, 0, loader, Tid, out _, out var refusal);
        Check("read_start_outside_load_mode_refused",
            !begun && refusal == RefusalKind.StartOutsideLoadMode, "refusal=" + refusal);

        begun = c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, null, Tid, out _,
            out refusal);
        Check("read_start_without_loader_refused",
            !begun && refusal == RefusalKind.StartWithoutLoaderEvidence, "refusal=" + refusal);
    }

    private static void PostConsumptionCompletions()
    {
        var c = NewCoordinator();
        var loader = new object();
        c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loader, Tid, out _, out _);
        c.Complete(WorldReadyCoordinator.PlayingGameMode, true, loader, Tid);

        var duplicate = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, loader, Tid);
        var menu = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, loader, Tid);
        var crossDay = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, loader, Tid);
        Check("post_consumption_completions_zero",
            !duplicate.Accepted && !menu.Accepted && !crossDay.Accepted && c.ObservationCount == 1,
            "duplicate=" + duplicate.Refusal + ", menu=" + menu.Refusal + ", crossDay=" + crossDay.Refusal);
    }

    private static void TitleAndModeGuards()
    {
        var c = NewCoordinator();
        var loader = new object();
        c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loader, Tid, out _, out _);
        var title = c.Complete(0, true, loader, Tid);
        var restored = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, loader, Tid);
        Check("title_completion_then_restore_zero",
            !title.Accepted && title.Refusal == RefusalKind.TitleMode &&
            !restored.Accepted && restored.Refusal == RefusalKind.NoPendingAttempt &&
            c.ObservationCount == 0,
            "title=" + title.Refusal + ", restored=" + restored.Refusal);

        c = NewCoordinator();
        c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loader, Tid, out _, out _);
        var notPlaying = c.Complete(7, true, loader, Tid);
        var later = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, loader, Tid);
        Check("not_playing_mode_zero",
            !notPlaying.Accepted && notPlaying.Refusal == RefusalKind.NotPlayingMode &&
            !later.Accepted && c.ObservationCount == 0,
            "notPlaying=" + notPlaying.Refusal + ", later=" + later.Refusal);

        c = NewCoordinator();
        c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loader, Tid, out _, out _);
        var notLoaded = c.Complete(WorldReadyCoordinator.PlayingGameMode, false, loader, Tid);
        Check("world_not_loaded_zero",
            !notLoaded.Accepted && notLoaded.Refusal == RefusalKind.WorldNotLoaded &&
            c.ObservationCount == 0,
            "refusal=" + notLoaded.Refusal);
    }

    private static void ThreadGuards()
    {
        var c = NewCoordinator();
        var loader = new object();
        c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loader, Tid, out _, out _);
        CompletionOutcome? wrong = null;
        var thread = new Thread(() =>
        {
            wrong = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, loader,
                Environment.CurrentManagedThreadId);
        });
        thread.Start();
        thread.Join();
        var later = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, loader, Tid);
        Check("wrong_thread_completion_zero",
            wrong is { Accepted: false, Refusal: RefusalKind.WrongThread } &&
            !later.Accepted && c.ObservationCount == 0,
            "wrong=" + wrong?.Refusal + ", later=" + later.Refusal);

        var unknown = new WorldReadyCoordinator();
        unknown.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loader, Tid, out _, out _);
        var noAuthority = unknown.Complete(WorldReadyCoordinator.PlayingGameMode, true, loader, Tid);
        Check("unknown_loop_thread_zero",
            !noAuthority.Accepted && noAuthority.Refusal == RefusalKind.UnknownGameLoopThread &&
            unknown.ObservationCount == 0,
            "refusal=" + noAuthority.Refusal);
    }

    private static void ReloadAndSwitch()
    {
        var c = NewCoordinator();
        var first = new object();
        c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, first, Tid, out var firstId, out _);
        c.Complete(WorldReadyCoordinator.PlayingGameMode, true, first, Tid);
        c.ResetWorld(Tid, "return-to-title");

        var second = new object();
        c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, second, Tid, out var secondId, out _);
        var secondOutcome = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, second, Tid);
        Check("same_day_reload_plus_one",
            secondOutcome.Accepted && secondId > firstId && c.ObservationCount == 2 &&
            secondOutcome.Observation is { AttributionProven: true },
            "observations=" + c.ObservationCount);

        c.ResetWorld(Tid, "switch-save");
        var third = new object();
        c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, third, Tid, out _, out _);
        var thirdOutcome = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, third, Tid);
        Check("switch_save_plus_one", thirdOutcome.Accepted && c.ObservationCount == 3,
            "observations=" + c.ObservationCount);
    }

    private static void FailureAndCancel()
    {
        var c = NewCoordinator();
        var loader = new object();
        c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loader, Tid, out _, out _);
        c.ResetWorld(Tid, "cancel");
        var late = c.Complete(0, true, loader, Tid);
        var restored = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, loader, Tid);
        Check("cancel_then_late_completion_zero",
            c.ObservationCount == 0 && c.PendingAttemptId == 0 && !late.Accepted && !restored.Accepted,
            "late=" + late.Refusal + ", restored=" + restored.Refusal + ", pending=" + c.PendingAttemptId);
    }

    private static void StaleAttemptDoesNotEatNext()
    {
        var c = NewCoordinator();
        var loaderA = new object();
        c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loaderA, Tid, out _, out _);
        c.ResetWorld(Tid, "cancel A");

        var loaderB = new object();
        c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loaderB, Tid, out var bId, out _);
        var stale = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, loaderA, Tid);
        var preserved = c.PendingAttemptId == bId;

        var fresh = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, loaderB, Tid);
        Check("stale_a_completion_does_not_eat_b",
            !stale.Accepted && stale.Refusal == RefusalKind.StaleLoaderEvidence &&
            stale.PendingPreserved && preserved &&
            fresh.Accepted && c.ObservationCount == 1 && fresh.AttemptId == bId,
            "stale=" + stale.Refusal + ", preserved=" + preserved + ", fresh=" + fresh.Accepted);
    }

    private static void PendingOnlyModeMisattributes()
    {
        var c = new WorldReadyCoordinator(CorrelationMode.PendingOnly);
        c.TrySetGameLoopThread(Tid);
        var loaderA = new object();
        c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loaderA, Tid, out _, out _);
        c.ResetWorld(Tid, "cancel A");

        var loaderB = new object();
        c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loaderB, Tid, out var bId, out _);
        var stale = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, loaderA, Tid);
        var fresh = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, loaderB, Tid);
        Check("pending_only_misattributes_b",
            stale.Accepted && stale.AttemptId == bId && !fresh.Accepted &&
            c.ObservationCount == 1,
            "staleAccepted=" + stale.Accepted + ", fresh=" + fresh.Refusal +
            " (model proof: a tokenless design cannot tell A's completion from B's)");
    }

    private static void SupersededAttempt()
    {
        var c = NewCoordinator();
        var loaderA = new object();
        c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loaderA, Tid, out _, out _);
        var loaderB = new object();
        c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loaderB, Tid, out var bId, out _);
        var old = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, loaderA, Tid);
        var current = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, loaderB, Tid);
        Check("superseded_attempt_cannot_consume",
            c.SupersededAttempts == 1 && !old.Accepted &&
            old.Refusal == RefusalKind.StaleLoaderEvidence &&
            current.Accepted && current.AttemptId == bId && c.ObservationCount == 1,
            "superseded=" + c.SupersededAttempts + ", old=" + old.Refusal);
    }

    private static void ResetEpochClosesStartRace()
    {
        var c = NewCoordinator();
        var loader = new object();
        var epochReadByCallSite = c.ResetEpoch;

        // The worker read the epoch (and believes the load is live) ...
        c.ResetWorld(Tid, "cancel raced with start");
        // ... then the reset lands before the coordinator records the start.
        var begun = c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loader, Tid,
            epochReadByCallSite, out _, out var refusal);
        Check("stale_epoch_start_refused",
            !begun && refusal == RefusalKind.ResetEpochChanged && c.PendingAttemptId == 0,
            "refusal=" + refusal);
    }

    private static void CompleteVersusResetRace()
    {
        var violations = 0;
        var accepted = 0;
        for (var i = 0; i < 100; i++)
        {
            var c = NewCoordinator();
            var loader = new object();
            c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loader, Tid, out _, out _);

            CompletionOutcome? outcome = null;
            using var barrier = new Barrier(2);
            var thread = new Thread(() =>
            {
                barrier.SignalAndWait();
                outcome = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, loader, Tid);
            });
            thread.Start();
            barrier.SignalAndWait();
            c.ResetWorld(Tid, "race");
            thread.Join();

            if (c.ObservationCount > 1 || c.PendingAttemptId != 0)
            {
                violations++;
            }

            if (outcome is { Accepted: true })
            {
                accepted++;
                if (c.ObservationCount != 1)
                {
                    violations++;
                }
            }
            else if (c.ObservationCount != 0)
            {
                violations++;
            }
        }

        Check("complete_versus_reset_race_invariants", violations == 0,
            "100 races, accepted=" + accepted + ", violations=" + violations);
    }

    private static void ResetLifecycle()
    {
        var c = NewCoordinator();
        var loader = new object();
        c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loader, Tid, out var id, out _);
        var began = c.TryBeginReset(Tid, "title", out var epoch, out var refusal);
        Check("reset_cancels_pending_and_bumps_epoch",
            began && refusal == RefusalKind.None && epoch == 1 && c.ResetEpoch == 1 &&
            c.PendingAttemptId == 0 && c.CancelledAttempts == 1 && c.ResetInProgress &&
            c.ResetCount == 1,
            "began=" + began + ", epoch=" + epoch + ", cancelled=" + c.CancelledAttempts +
            ", pending=" + c.PendingAttemptId + " (cancelled attempt " + id + ")");

        var completed = c.TryCompleteReset(Tid, out var completeRefusal);
        Check("reset_completion_clears_latch",
            completed && completeRefusal == RefusalKind.None && !c.ResetInProgress &&
            c.CompletedResetCount == 1,
            "completed=" + completed + ", latch=" + c.ResetInProgress);

        // A second completion with no reset in progress is refused, and the latch stays clear.
        var again = c.TryCompleteReset(Tid, out var againRefusal);
        Check("duplicate_reset_completion_refused",
            !again && againRefusal == RefusalKind.NoResetInProgress && !c.ResetInProgress,
            "refusal=" + againRefusal);
    }

    private static void ResetVersusPending()
    {
        // Start inside the reset window: epoch is current, the fields are the previous world's.
        var c = NewCoordinator();
        var loader = new object();
        c.TryBeginReset(Tid, "title", out var epoch, out _);
        var begun = c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loader, Tid,
            epoch, out _, out var refusal);
        var completed = c.TryCompleteReset(Tid, out _);
        Check("reset_in_progress_refuses_start",
            !begun && refusal == RefusalKind.ResetInProgress && c.PendingAttemptId == 0 &&
            c.RefusalCount(RefusalKind.ResetInProgress) == 1 && completed,
            "refusal=" + refusal + ", epoch=" + epoch);

        // Negative control: without the in-progress guard the same start is accepted.
        var uncheckedCoordinator = new WorldReadyCoordinator(CorrelationMode.LoaderIdentity,
            honorResetInProgress: false);
        uncheckedCoordinator.TrySetGameLoopThread(Tid);
        uncheckedCoordinator.TryBeginReset(Tid, "title", out var uncheckedEpoch, out _);
        var uncheckedBegun = uncheckedCoordinator.TryBeginAttempt(true,
            WorldReadyCoordinator.LoadScreenGameMode, loader, Tid, uncheckedEpoch, out _, out var uncheckedRefusal);
        Check("reset_guard_absent_accepts_window_start",
            uncheckedBegun && uncheckedRefusal == RefusalKind.None &&
            uncheckedCoordinator.PendingAttemptId != 0,
            "begun=" + uncheckedBegun + ", pending=" + uncheckedCoordinator.PendingAttemptId +
            " (negative control: this is the window the guard closes)");

        // Completion inside the reset window is refused, and does not become "no pending".
        c = NewCoordinator();
        c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loader, Tid, out _, out _);
        c.TryBeginReset(Tid, "title", out _, out _);
        var outcome = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, loader, Tid);
        Check("reset_in_progress_refuses_completion",
            !outcome.Accepted && outcome.Refusal == RefusalKind.ResetInProgress &&
            c.ObservationCount == 0,
            "refusal=" + outcome.Refusal);

        // A repeated reset bumps the epoch again and cancels nothing more.
        c = NewCoordinator();
        c.TryBeginReset(Tid, "first", out _, out _);
        c.TryCompleteReset(Tid, out _);
        c.TryBeginReset(Tid, "second", out var secondEpoch, out _);
        c.TryCompleteReset(Tid, out _);
        Check("repeated_reset_counts_and_epoch",
            secondEpoch == 2 && c.ResetCount == 2 && c.CompletedResetCount == 2 &&
            c.CancelledAttempts == 0 && c.PendingAttemptId == 0,
            "epoch=" + secondEpoch + ", resets=" + c.ResetCount + ", cancelled=" + c.CancelledAttempts);

        // Normal retry after a reset still produces exactly one observation.
        c = NewCoordinator();
        var retryLoader = new object();
        c.TryBeginReset(Tid, "title", out _, out _);
        c.TryCompleteReset(Tid, out _);
        c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, retryLoader, Tid,
            c.ResetEpoch, out var retryId, out _);
        var retry = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, retryLoader, Tid);
        Check("reset_then_retry_plus_one",
            retry.Accepted && retry.AttemptId == retryId && c.ObservationCount == 1,
            "observed=" + c.ObservationCount);

        // A new game after a reset has no loader token: accepted, but attribution stays unproven.
        c = NewCoordinator();
        c.TryBeginReset(Tid, "title", out _, out _);
        c.TryCompleteReset(Tid, out _);
        var ngBegun = c.TryBeginAttempt(false, 0, null, Tid, c.ResetEpoch, out _, out var ngRefusal);
        var ng = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, null, Tid);
        Check("new_game_after_reset_unproven",
            ngBegun && ng.Accepted && c.ObservationCount == 1 &&
            ng.Observation is { Kind: AttemptKind.NewGame, Basis: AttributionBasis.None } &&
            ng.Observation.AttemptOwnershipProven == false,
            "begun=" + ngBegun + ", refusal=" + ngRefusal + ", basis=" + ng.Observation?.Basis);

        // An aborted reset (exception before the normal return) keeps the latch; the next
        // completed reset heals it.
        c = NewCoordinator();
        c.TryBeginReset(Tid, "throws", out _, out _);
        var afterAbort = c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loader,
            Tid, c.ResetEpoch, out _, out var abortRefusal);
        c.TryBeginReset(Tid, "next reset", out _, out _);
        c.TryCompleteReset(Tid, out _);
        var afterHeal = c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loader,
            Tid, c.ResetEpoch, out _, out var healedRefusal);
        Check("reset_abnormal_exit_then_next_reset_heals",
            !afterAbort && abortRefusal == RefusalKind.ResetInProgress && afterHeal &&
            healedRefusal == RefusalKind.None,
            "afterAbort=" + abortRefusal + ", afterHeal=" + afterHeal + "/" + healedRefusal);

        // A stale-epoch start must not touch the newer pending attempt.
        c = NewCoordinator();
        var epochBefore = c.ResetEpoch;
        c.TryBeginReset(Tid, "title", out _, out _);
        c.TryCompleteReset(Tid, out _);
        var loaderB = new object();
        c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loaderB, Tid,
            c.ResetEpoch, out var bId, out _);
        var staleBegin = c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loaderB,
            Tid, epochBefore, out _, out var staleBeginRefusal);
        var bStillPending = c.PendingAttemptId == bId;
        var bOutcome = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, loaderB, Tid);
        Check("stale_epoch_begin_preserves_newer_pending",
            !staleBegin && staleBeginRefusal == RefusalKind.ResetEpochChanged && bStillPending &&
            bOutcome.Accepted && bOutcome.AttemptId == bId && c.ObservationCount == 1,
            "staleBegin=" + staleBeginRefusal + ", bPending=" + bStillPending);
    }

    private static void ResetCommitOrder()
    {
        // Completion committed first, reset second: the observation stands, the reset only bumps
        // the epoch and cancels nothing.
        var c = NewCoordinator();
        var loader = new object();
        c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loader, Tid, out _, out _);
        c.Complete(WorldReadyCoordinator.PlayingGameMode, true, loader, Tid);
        c.TryBeginReset(Tid, "title", out var epoch, out _);
        c.TryCompleteReset(Tid, out _);
        Check("completion_then_reset_keeps_one",
            c.ObservationCount == 1 && c.CancelledAttempts == 0 && epoch == 1 &&
            string.Join(">", c.Signals.Select(s => s.Kind)) ==
            "StartAccepted>CompletionAccepted>ResetBegan>ResetCompleted",
            "observations=" + c.ObservationCount + ", signals=" + SignalPath(c));

        // Reset committed first, completion second: nothing to consume.
        c = NewCoordinator();
        c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loader, Tid, out _, out _);
        c.TryBeginReset(Tid, "title", out _, out _);
        var duringReset = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, loader, Tid);
        c.TryCompleteReset(Tid, out _);
        var afterReset = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, loader, Tid);
        Check("reset_then_completion_order",
            !duringReset.Accepted && duringReset.Refusal == RefusalKind.ResetInProgress &&
            !afterReset.Accepted && afterReset.Refusal == RefusalKind.NoPendingAttempt &&
            c.ObservationCount == 0,
            "during=" + duringReset.Refusal + ", after=" + afterReset.Refusal);
    }

    /// <summary>
    /// Counterexample for loader identity: a delayed completion of attempt A reads whatever
    /// <c>currentLoader</c> holds now, which is B's loader. Reference equality then matches and the
    /// completion is accepted as B. This is why loader identity is recorded as
    /// <see cref="AttributionBasis.LoaderReference"/> and never as proven ownership.
    /// </summary>
    private static void LateCompletionReadsBLoader()
    {
        var c = NewCoordinator();
        var loaderA = new object();
        c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loaderA, Tid, out _, out _);
        c.TryBeginReset(Tid, "cancel A", out _, out _);
        c.TryCompleteReset(Tid, out _);

        var loaderB = new object();
        c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loaderB, Tid,
            c.ResetEpoch, out var bId, out _);

        // A's late completion: it reads the global currentLoader, which is now B's object.
        var lateA = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, loaderB, Tid);
        var bLater = c.Complete(WorldReadyCoordinator.PlayingGameMode, true, loaderB, Tid);

        Check("late_completion_reads_b_loader",
            lateA.Accepted && lateA.AttemptId == bId &&
            lateA.Observation is { Basis: AttributionBasis.LoaderReference } &&
            lateA.Observation.AttemptOwnershipProven == false &&
            !bLater.Accepted && bLater.Refusal == RefusalKind.AlreadyConsumed &&
            c.ObservationCount == 1,
            "acceptedAsB=" + (lateA.AttemptId == bId) + ", basis=" + lateA.Observation?.Basis +
            ", bRealCompletion=" + bLater.Refusal +
            " (loader identity cannot prove this completion belongs to B)");
    }

    private static void ResetThreadCases()
    {
        var c = NewCoordinator();
        var loader = new object();
        c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loader, Tid, out var id, out _);
        var epochBefore = c.ResetEpoch;

        RefusalKind wrongRefusal = RefusalKind.None;
        var began = true;
        var thread = new Thread(() =>
        {
            began = c.TryBeginReset(Environment.CurrentManagedThreadId, "wrong thread", out _,
                out wrongRefusal);
        });
        thread.Start();
        thread.Join();
        Check("reset_wrong_thread_refused_preserves_pending",
            !began && wrongRefusal == RefusalKind.WrongThread && c.ResetEpoch == epochBefore &&
            !c.ResetInProgress && c.PendingAttemptId == id &&
            c.ResetRefusalCount(RefusalKind.WrongThread) == 1,
            "began=" + began + ", refusal=" + wrongRefusal + ", pending=" + c.PendingAttemptId);

        // A reset completion from the wrong thread is refused too, and does not clear the latch.
        c.TryBeginReset(Tid, "title", out _, out _);
        RefusalKind wrongComplete = RefusalKind.None;
        var completed = true;
        var completeThread = new Thread(() =>
        {
            completed = c.TryCompleteReset(Environment.CurrentManagedThreadId, out wrongComplete);
        });
        completeThread.Start();
        completeThread.Join();
        Check("reset_completion_wrong_thread_refused",
            !completed && wrongComplete == RefusalKind.WrongThread && c.ResetInProgress,
            "completed=" + completed + ", refusal=" + wrongComplete);
        c.TryCompleteReset(Tid, out _);

        // With no thread authority the reset boundary is accepted but nothing else is claimed.
        var unknown = new WorldReadyCoordinator();
        var unknownBegun = unknown.TryBeginReset(Tid, "no authority", out var unknownEpoch, out _);
        var unknownCompleted = unknown.TryCompleteReset(Tid, out _);
        Check("reset_on_unknown_thread_accepted",
            unknownBegun && unknownCompleted && unknownEpoch == 1 && unknown.ResetCount == 1 &&
            unknown.GameLoopThreadId == 0,
            "began=" + unknownBegun + ", epoch=" + unknownEpoch);

        // Correct thread: accepted, and the signal trace records the ordering.
        var ordered = NewCoordinator();
        ordered.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loader, Tid,
            out _, out _);
        ordered.TryBeginReset(Tid, "title", out _, out _);
        ordered.TryCompleteReset(Tid, out _);
        Check("reset_correct_thread_signal_order",
            SignalPath(ordered) == "StartAccepted>ResetBegan>ResetCompleted",
            "signals=" + SignalPath(ordered));
    }

    // ------------------------------------------------------------------ section 2: synthetic IL

    private static void SyntheticCases()
    {
        SyntheticSuccess();
        SyntheticBranchToRet();
        SyntheticExceptionNoCompletion();
        SyntheticRejections();
        SyntheticEHEndBoundary();
        SyntheticStaleCompletionThroughHooks();
        SyntheticResetThroughHooks();
        SyntheticStaleStartRace();
        SyntheticMidResetWindow();
        SyntheticLateCompletionReadsBLoader();
        SyntheticResetExceptionLeavesLatch();
        SyntheticResetShapeRejections();
        SyntheticEpochOrderRegression();
        SyntheticResetEHEndBoundary();
    }

    private static void SyntheticSuccess()
    {
        var fixture = SyntheticFixture.Build("ok");
        var targets = PrototypeInjector.Resolve(fixture.Module, SyntheticFixture.Game1Type);
        var result = PrototypeInjector.Inject(targets, fixture.Hooks);
        var verification = PrototypeInjector.Verify(targets, fixture.Hooks);
        Check("synthetic_injection_shape",
            result.WorldLoadingCalls == 1 && result.DayCompletedCalls == 1 &&
            result.ResetEntryCalls == 1 && result.ResetCompletedCalls == 1 &&
            verification.Ok && verification.EpochReadFirst,
            "reset=" + result.ResetEntryCalls + "/" + result.ResetCompletedCalls +
            ", start=" + result.WorldLoadingCalls + ", completion=" + result.DayCompletedCalls +
            ", epochFirst=" + verification.EpochReadFirst +
            ", problems=" + string.Join("; ", verification.Problems));

        var assembly = Load(fixture, "ok");
        var recorder = new RecordingSink();
        PrototypeBridge.Install(recorder);
        try
        {
            var loader = new object();
            SetStatic(assembly, "currentLoader", loader);
            SetStatic(assembly, "_gameMode", (byte)WorldReadyCoordinator.LoadScreenGameMode);
            var startThread = new Thread(() => InvokeLoad(assembly, true));
            startThread.Start();
            startThread.Join();

            SetStatic(assembly, "_gameMode", (byte)WorldReadyCoordinator.PlayingGameMode);
            SetStatic(assembly, "hasLoadedGame", true);
            InvokeDayStarted(assembly);

            Check("synthetic_hooks_execute_once",
                recorder.LoadingCount == 1 && recorder.CompletedCount == 1 &&
                recorder.LastLoadedGame && recorder.LastGameMode == WorldReadyCoordinator.PlayingGameMode &&
                recorder.LastHasLoadedGame && ReferenceEquals(recorder.LastLoader, loader),
                "loading=" + recorder.LoadingCount + ", completed=" + recorder.CompletedCount +
                ", mode=" + recorder.LastGameMode);

            Check("synthetic_start_thread_differs_from_completion",
                recorder.LoadingThreadId != recorder.CompletedThreadId,
                "start=" + recorder.LoadingThreadId + ", completion=" + recorder.CompletedThreadId);
        }
        finally
        {
            PrototypeBridge.Install(null);
        }

        // End-to-end: real hook IL -> bridge -> coordinator.
        var coordinator = NewCoordinator();
        var sink = new CoordinatorSink(coordinator);
        PrototypeBridge.Install(sink);
        try
        {
            var loader = new object();
            SetStatic(assembly, "currentLoader", loader);
            SetStatic(assembly, "_gameMode", (byte)WorldReadyCoordinator.LoadScreenGameMode);
            InvokeLoad(assembly, true);
            SetStatic(assembly, "_gameMode", (byte)WorldReadyCoordinator.PlayingGameMode);
            SetStatic(assembly, "hasLoadedGame", true);
            InvokeDayStarted(assembly);

            Check("synthetic_end_to_end_one_observation",
                coordinator.ObservationCount == 1 &&
                coordinator.Observations[0].AttributionProven &&
                coordinator.Observations[0].Kind == AttemptKind.Load,
                "observations=" + coordinator.ObservationCount + ", notes=" + string.Join("; ", sink.Notes));
        }
        finally
        {
            PrototypeBridge.Install(null);
        }
    }

    private static void SyntheticBranchToRet()
    {
        var fixture = SyntheticFixture.Build("branch", withFinally: false);
        var targets = PrototypeInjector.Resolve(fixture.Module, SyntheticFixture.Game1Type);
        PrototypeInjector.Inject(targets, fixture.Hooks);
        var verification = PrototypeInjector.Verify(targets, fixture.Hooks);

        var assembly = Load(fixture, "branch");
        var recorder = new RecordingSink();
        PrototypeBridge.Install(recorder);
        try
        {
            SetStatic(assembly, "_gameMode", (byte)0);
            SetStatic(assembly, "hasLoadedGame", true);
            InvokeDayStarted(assembly);
            Check("synthetic_branch_to_ret_runs_completion",
                verification.Ok && recorder.CompletedCount == 1 &&
                GetStatic<int>(assembly, "BodyCount") == 0,
                "completed=" + recorder.CompletedCount + ", body=" + GetStatic<int>(assembly, "BodyCount"));
        }
        finally
        {
            PrototypeBridge.Install(null);
        }

        // The same signal is refused by the coordinator when nothing is pending.
        var coordinator = NewCoordinator();
        PrototypeBridge.Install(new CoordinatorSink(coordinator));
        try
        {
            InvokeDayStarted(assembly);
            Check("synthetic_title_mode_completion_refused",
                coordinator.ObservationCount == 0 &&
                coordinator.RefusalCount(RefusalKind.NoPendingAttempt) == 1,
                "observations=" + coordinator.ObservationCount);
        }
        finally
        {
            PrototypeBridge.Install(null);
        }
    }

    private static void SyntheticExceptionNoCompletion()
    {
        var fixture = SyntheticFixture.Build("throw", throws: true);
        var targets = PrototypeInjector.Resolve(fixture.Module, SyntheticFixture.Game1Type);
        PrototypeInjector.Inject(targets, fixture.Hooks);

        var assembly = Load(fixture, "throw");
        var recorder = new RecordingSink();
        PrototypeBridge.Install(recorder);
        try
        {
            var threw = false;
            try
            {
                InvokeDayStarted(assembly);
            }
            catch (TargetInvocationException)
            {
                threw = true;
            }

            Check("synthetic_exception_no_completion",
                threw && recorder.CompletedCount == 0,
                "threw=" + threw + ", completed=" + recorder.CompletedCount);
        }
        finally
        {
            PrototypeBridge.Install(null);
        }
    }

    private static void SyntheticRejections()
    {
        var fixture = SyntheticFixture.Build("missing-state", withState: false);
        CheckThrows("synthetic_missing_state_rejected", () =>
            PrototypeInjector.Resolve(fixture.Module, SyntheticFixture.Game1Type));

        var ambiguous = SyntheticFixture.Build("ambiguous");
        var second = new MethodDefinition("loadForNewGame",
            Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig,
            ambiguous.Module.TypeSystem.Void);
        second.Parameters.Add(new ParameterDefinition("loadedGame", Mono.Cecil.ParameterAttributes.None,
            ambiguous.Module.TypeSystem.Boolean));
        second.Body.GetILProcessor().Emit(OpCodes.Ret);
        ambiguous.Game1.Methods.Add(second);
        CheckThrows("synthetic_ambiguous_overload_rejected", () =>
            PrototypeInjector.Resolve(ambiguous.Module, SyntheticFixture.Game1Type));

        var retInTry = SyntheticFixture.Build("ret-in-try", retInsideTry: true);
        var retInTryTargets = PrototypeInjector.Resolve(retInTry.Module, SyntheticFixture.Game1Type);
        CheckThrows("synthetic_ret_in_try_rejected", () =>
            PrototypeInjector.Inject(retInTryTargets, retInTry.Hooks));

        var handlerOnly = SyntheticFixture.Build("handler-only", handlerOnlyRet: true);
        var handlerTargets = PrototypeInjector.Resolve(handlerOnly.Module, SyntheticFixture.Game1Type);
        CheckThrows("synthetic_handler_only_ret_rejected", () =>
            PrototypeInjector.Inject(handlerTargets, handlerOnly.Hooks));

        var twice = SyntheticFixture.Build("twice");
        var twiceTargets = PrototypeInjector.Resolve(twice.Module, SyntheticFixture.Game1Type);
        PrototypeInjector.Inject(twiceTargets, twice.Hooks);
        CheckThrows("synthetic_double_inject_rejected", () =>
            PrototypeInjector.Inject(twiceTargets, twice.Hooks));

        var bypass = SyntheticFixture.Build("bypass", withFinally: false);
        var bypassTargets = PrototypeInjector.Resolve(bypass.Module, SyntheticFixture.Game1Type);
        var ret = bypass.OnDayStarted.Body.Instructions.Last();
        var bil = bypass.OnDayStarted.Body.GetILProcessor();
        bil.InsertBefore(ret, bil.Create(OpCodes.Call, bypass.CompletionHook));
        bil.InsertBefore(bypass.OnDayStarted.Body.Instructions[0], bil.Create(OpCodes.Br, ret));
        var bypassReport = PrototypeInjector.Verify(bypassTargets, bypass.Hooks);
        Check("synthetic_bypass_edge_rejected", !bypassReport.Ok,
            string.Join("; ", bypassReport.Problems));
    }

    private static void SyntheticEHEndBoundary()
    {
        var fixture = SyntheticFixture.Build("eh-end", withFinally: false);
        var method = fixture.OnDayStarted;
        var il = method.Body.GetILProcessor();
        method.Body.Instructions.Clear();
        method.Body.ExceptionHandlers.Clear();

        var tryStart = il.Create(OpCodes.Nop);
        var ret = il.Create(OpCodes.Ret);
        var leave = il.Create(OpCodes.Leave_S, ret);
        var handlerStart = il.Create(OpCodes.Pop);
        var handlerLeave = il.Create(OpCodes.Leave_S, ret);

        il.Append(tryStart);
        il.Append(leave);
        il.Append(handlerStart);
        il.Append(handlerLeave);
        il.Append(ret);

        method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = tryStart,
            TryEnd = handlerStart,
            HandlerStart = handlerStart,
            HandlerEnd = ret,
            CatchType = fixture.Module.ImportReference(typeof(Exception)),
        });

        var targets = PrototypeInjector.Resolve(fixture.Module, SyntheticFixture.Game1Type);
        PrototypeInjector.Inject(targets, fixture.Hooks);
        var eh = method.Body.ExceptionHandlers[0];
        var hookStart = ret.Previous!.Previous!.Previous!.Previous!;
        var verification = PrototypeInjector.Verify(targets, fixture.Hooks);

        var assembly = Load(fixture, "eh-end");
        var recorder = new RecordingSink();
        PrototypeBridge.Install(recorder);
        try
        {
            InvokeDayStarted(assembly);
            Check("synthetic_handler_end_moves_with_hook",
                verification.Ok && ReferenceEquals(eh.HandlerEnd, hookStart) &&
                recorder.CompletedCount == 1,
                "handlerEndMoved=" + ReferenceEquals(eh.HandlerEnd, hookStart) +
                ", completed=" + recorder.CompletedCount);
        }
        finally
        {
            PrototypeBridge.Install(null);
        }
    }

    private static void SyntheticStaleCompletionThroughHooks()
    {
        var fixture = SyntheticFixture.Build("stale");
        var targets = PrototypeInjector.Resolve(fixture.Module, SyntheticFixture.Game1Type);
        PrototypeInjector.Inject(targets, fixture.Hooks);

        var assembly = Load(fixture, "stale");
        var coordinator = NewCoordinator();
        PrototypeBridge.Install(new CoordinatorSink(coordinator));
        try
        {
            var loaderA = new object();
            SetStatic(assembly, "currentLoader", loaderA);
            SetStatic(assembly, "_gameMode", (byte)WorldReadyCoordinator.LoadScreenGameMode);
            InvokeLoad(assembly, true);

            coordinator.ResetWorld(Tid, "cancel A");

            var loaderB = new object();
            SetStatic(assembly, "currentLoader", loaderB);
            InvokeLoad(assembly, true);
            var pendingB = coordinator.PendingAttemptId;

            // A stale completion would read whatever is in currentLoader; make it read A's object.
            SetStatic(assembly, "currentLoader", loaderA);
            SetStatic(assembly, "_gameMode", (byte)WorldReadyCoordinator.PlayingGameMode);
            SetStatic(assembly, "hasLoadedGame", true);
            InvokeDayStarted(assembly);
            var preserved = coordinator.PendingAttemptId == pendingB;

            SetStatic(assembly, "currentLoader", loaderB);
            InvokeDayStarted(assembly);

            Check("synthetic_stale_completion_preserves_b",
                preserved && coordinator.ObservationCount == 1 &&
                coordinator.RefusalCount(RefusalKind.StaleLoaderEvidence) == 1,
                "preserved=" + preserved + ", observations=" + coordinator.ObservationCount +
                ", staleRefusals=" + coordinator.RefusalCount(RefusalKind.StaleLoaderEvidence));
        }
        finally
        {
            PrototypeBridge.Install(null);
        }
    }

    /// <summary>Reset hooks fire from the injected IL and the latch closes on the normal return.</summary>
    private static void SyntheticResetThroughHooks()
    {
        var fixture = SyntheticFixture.Build("reset-ok");
        var targets = PrototypeInjector.Resolve(fixture.Module, SyntheticFixture.Game1Type);
        PrototypeInjector.Inject(targets, fixture.Hooks);
        var assembly = Load(fixture, "reset-ok");

        var coordinator = NewCoordinator();
        var sink = new CoordinatorSink(coordinator);
        PrototypeBridge.Install(sink);
        try
        {
            var loader = new object();
            SetStatic(assembly, "currentLoader", loader);
            SetStatic(assembly, "_gameMode", (byte)WorldReadyCoordinator.LoadScreenGameMode);
            SetStatic(assembly, "hasLoadedGame", true);
            InvokeLoad(assembly, true);

            InvokeReset(assembly);

            Check("synthetic_reset_hooks_execute_once",
                coordinator.ResetCount == 1 && coordinator.CompletedResetCount == 1 &&
                !coordinator.ResetInProgress && coordinator.ResetEpoch == 1 &&
                coordinator.PendingAttemptId == 0 && coordinator.CancelledAttempts == 1 &&
                GetStatic<int>(assembly, "ResetCount") == 1,
                "resets=" + coordinator.ResetCount + "/" + coordinator.CompletedResetCount +
                ", epoch=" + coordinator.ResetEpoch + ", cancelled=" + coordinator.CancelledAttempts +
                ", fixtureResets=" + GetStatic<int>(assembly, "ResetCount"));

            Check("synthetic_reset_clears_game_fields",
                GetStatic<byte>(assembly, "_gameMode") == 0 &&
                GetStatic<object?>(assembly, "currentLoader") is null &&
                !GetStatic<bool>(assembly, "hasLoadedGame"),
                "mode=" + GetStatic<byte>(assembly, "_gameMode") +
                ", loaderNull=" + (GetStatic<object?>(assembly, "currentLoader") is null) +
                ", hasLoadedGame=" + GetStatic<bool>(assembly, "hasLoadedGame"));

            // A start after the completed reset is accepted under the new epoch.
            var loaderB = new object();
            SetStatic(assembly, "currentLoader", loaderB);
            SetStatic(assembly, "_gameMode", (byte)WorldReadyCoordinator.LoadScreenGameMode);
            InvokeLoad(assembly, true);
            SetStatic(assembly, "_gameMode", (byte)WorldReadyCoordinator.PlayingGameMode);
            SetStatic(assembly, "hasLoadedGame", true);
            InvokeDayStarted(assembly);
            Check("synthetic_epoch_read_before_mode",
                coordinator.ObservationCount == 1 &&
                coordinator.Observations[0].ResetEpoch == 1 &&
                GetStatic<int>(assembly, "LoadCount") == 2,
                "observations=" + coordinator.ObservationCount +
                ", observedEpoch=" + (coordinator.ObservationCount == 1
                    ? coordinator.Observations[0].ResetEpoch
                    : -1));
        }
        finally
        {
            PrototypeBridge.Install(null);
        }
    }

    /// <summary>
    /// Race 1 (counterexample C): the worker read the epoch, then the reset lands, then the stale
    /// start is committed. Goes through the written assembly, the injected IL, the bridge and the
    /// coordinator. Deterministic: the test-side sink blocks between the epoch read and the commit.
    /// </summary>
    private static void SyntheticStaleStartRace()
    {
        var fixture = SyntheticFixture.Build("reset-race");
        var targets = PrototypeInjector.Resolve(fixture.Module, SyntheticFixture.Game1Type);
        PrototypeInjector.Inject(targets, fixture.Hooks);
        var assembly = Load(fixture, "reset-race");

        var coordinator = NewCoordinator();
        var sink = new PausingSink(coordinator);
        PrototypeBridge.Install(sink);
        try
        {
            var loaderA = new object();
            SetStatic(assembly, "currentLoader", loaderA);
            SetStatic(assembly, "_gameMode", (byte)WorldReadyCoordinator.LoadScreenGameMode);
            SetStatic(assembly, "hasLoadedGame", true);

            // Worker reads the epoch (0) and blocks before the coordinator records the start.
            var worker = new Thread(() => InvokeLoad(assembly, true));
            worker.Start();
            if (!sink.Reached.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("stale-start race gate was never reached");
            }

            // Reset lands on the game-loop (this) thread while the worker is still blocked.
            InvokeReset(assembly);
            var epochAfterReset = coordinator.ResetEpoch;
            sink.Resume.Set();
            worker.Join(TimeSpan.FromSeconds(30));

            var staleRefused = coordinator.RefusalCount(RefusalKind.ResetEpochChanged) == 1;

            // B starts under the new epoch and completes exactly once.
            var loaderB = new object();
            SetStatic(assembly, "currentLoader", loaderB);
            SetStatic(assembly, "_gameMode", (byte)WorldReadyCoordinator.LoadScreenGameMode);
            InvokeLoad(assembly, true);
            var bId = coordinator.PendingAttemptId;
            SetStatic(assembly, "_gameMode", (byte)WorldReadyCoordinator.PlayingGameMode);
            SetStatic(assembly, "hasLoadedGame", true);
            InvokeDayStarted(assembly);

            var order = SignalPath(coordinator);
            Check("synthetic_stale_epoch_start_refused_e2e",
                staleRefused && coordinator.ObservationCount == 1 &&
                coordinator.Observations[0].AttemptId == bId &&
                coordinator.Observations[0].ResetEpoch == epochAfterReset &&
                order == "ResetBegan>ResetCompleted>StartRefused:ResetEpochChanged" +
                    ">StartAccepted>CompletionAccepted",
                "staleRefused=" + staleRefused + ", observations=" + coordinator.ObservationCount +
                ", bAttempt=" + bId + ", signals=" + order);
        }
        finally
        {
            PrototypeBridge.Install(null);
        }
    }

    /// <summary>
    /// Race 2 (the reset window): the epoch is already incremented but the title fields have not
    /// been cleaned yet. A start that reads the current epoch and the previous world's fields must
    /// not be accepted. The fixture blocks inside the reset body after the entry hook.
    /// </summary>
    private static void SyntheticMidResetWindow()
    {
        var fixture = SyntheticFixture.Build("reset-window");
        var targets = PrototypeInjector.Resolve(fixture.Module, SyntheticFixture.Game1Type);
        PrototypeInjector.Inject(targets, fixture.Hooks);
        var assembly = Load(fixture, "reset-window");

        var coordinator = NewCoordinator();
        var sink = new CoordinatorSink(coordinator);
        PrototypeBridge.Install(sink);
        SyntheticGate.Open(paused: true);
        try
        {
            // Previous world still visible: mode 6, loader A, hasLoadedGame true.
            var loaderA = new object();
            SetStatic(assembly, "currentLoader", loaderA);
            SetStatic(assembly, "_gameMode", (byte)WorldReadyCoordinator.LoadScreenGameMode);
            SetStatic(assembly, "hasLoadedGame", true);

            var during = new Thread(() =>
            {
                if (!SyntheticGate.Reached.Wait(TimeSpan.FromSeconds(30)))
                {
                    SyntheticGate.Release();
                    return;
                }

                // Inside the window: epoch is current, the fields are the previous world's.
                InvokeLoad(assembly, true);
                SyntheticGate.Release();
            });
            during.Start();

            // Blocks inside the reset body, after the ResetBegan hook.
            InvokeReset(assembly);
            during.Join(TimeSpan.FromSeconds(30));

            var refusedInWindow = coordinator.RefusalCount(RefusalKind.ResetInProgress) == 1;
            var noPendingInWindow = coordinator.PendingAttemptId == 0;

            // After the reset completed, a fresh start and completion are accepted exactly once.
            var loaderB = new object();
            SetStatic(assembly, "currentLoader", loaderB);
            SetStatic(assembly, "_gameMode", (byte)WorldReadyCoordinator.LoadScreenGameMode);
            InvokeLoad(assembly, true);
            SetStatic(assembly, "_gameMode", (byte)WorldReadyCoordinator.PlayingGameMode);
            SetStatic(assembly, "hasLoadedGame", true);
            InvokeDayStarted(assembly);

            Check("synthetic_mid_reset_window_start_refused_e2e",
                refusedInWindow && noPendingInWindow && coordinator.ObservationCount == 1 &&
                coordinator.CompletedResetCount == 1 &&
                SignalPath(coordinator) ==
                "ResetBegan>StartRefused:ResetInProgress>ResetCompleted>StartAccepted" +
                ">CompletionAccepted",
                "refusedInWindow=" + refusedInWindow + ", pendingInWindow=" +
                !noPendingInWindow + ", observations=" + coordinator.ObservationCount +
                ", signals=" + SignalPath(coordinator));
        }
        finally
        {
            SyntheticGate.Open(paused: false);
            PrototypeBridge.Install(null);
        }
    }

    /// <summary>
    /// Counterexample for loader identity, end to end: A's delayed completion reads B's global
    /// loader, so the accepted observation is B's id even though the signal came from A.
    /// </summary>
    private static void SyntheticLateCompletionReadsBLoader()
    {
        var fixture = SyntheticFixture.Build("reset-late");
        var targets = PrototypeInjector.Resolve(fixture.Module, SyntheticFixture.Game1Type);
        PrototypeInjector.Inject(targets, fixture.Hooks);
        var assembly = Load(fixture, "reset-late");

        var coordinator = NewCoordinator();
        PrototypeBridge.Install(new CoordinatorSink(coordinator));
        try
        {
            var loaderA = new object();
            SetStatic(assembly, "currentLoader", loaderA);
            SetStatic(assembly, "_gameMode", (byte)WorldReadyCoordinator.LoadScreenGameMode);
            InvokeLoad(assembly, true);

            InvokeReset(assembly);

            var loaderB = new object();
            SetStatic(assembly, "currentLoader", loaderB);
            SetStatic(assembly, "_gameMode", (byte)WorldReadyCoordinator.LoadScreenGameMode);
            InvokeLoad(assembly, true);
            var bId = coordinator.PendingAttemptId;

            // A's delayed completion runs now; the completion edge reads currentLoader == L_B.
            SetStatic(assembly, "_gameMode", (byte)WorldReadyCoordinator.PlayingGameMode);
            SetStatic(assembly, "hasLoadedGame", true);
            InvokeDayStarted(assembly);
            var acceptedObservation = coordinator.Observations.Count == 1
                ? coordinator.Observations[0]
                : null;
            InvokeDayStarted(assembly);

            Check("synthetic_late_completion_reads_b_loader",
                acceptedObservation != null && acceptedObservation.AttemptId == bId &&
                acceptedObservation.Basis == AttributionBasis.LoaderReference &&
                acceptedObservation.AttemptOwnershipProven == false &&
                coordinator.ObservationCount == 1 &&
                coordinator.RefusalCount(RefusalKind.AlreadyConsumed) == 1,
                "acceptedAsB=" + (acceptedObservation?.AttemptId == bId) +
                ", basis=" + acceptedObservation?.Basis +
                ", bRealCompletion=AlreadyConsumed" +
                " (loader identity is reference equality, not attempt ownership)");
        }
        finally
        {
            PrototypeBridge.Install(null);
        }
    }

    /// <summary>A reset that throws never reaches its normal return: the latch stays set.</summary>
    private static void SyntheticResetExceptionLeavesLatch()
    {
        var throwing = SyntheticFixture.Build("reset-throws", resetShape: ResetShape.ThrowsBeforeCleanup);
        var throwingTargets = PrototypeInjector.Resolve(throwing.Module, SyntheticFixture.Game1Type);
        PrototypeInjector.Inject(throwingTargets, throwing.Hooks);
        var throwingAssembly = Load(throwing, "reset-throws");

        var normal = SyntheticFixture.Build("reset-heal");
        var normalTargets = PrototypeInjector.Resolve(normal.Module, SyntheticFixture.Game1Type);
        PrototypeInjector.Inject(normalTargets, normal.Hooks);
        var normalAssembly = Load(normal, "reset-heal");

        var coordinator = NewCoordinator();
        PrototypeBridge.Install(new CoordinatorSink(coordinator));
        try
        {
            var loader = new object();
            SetStatic(throwingAssembly, "currentLoader", loader);
            SetStatic(throwingAssembly, "_gameMode", (byte)WorldReadyCoordinator.LoadScreenGameMode);
            SetStatic(throwingAssembly, "hasLoadedGame", true);

            var threw = false;
            try
            {
                InvokeReset(throwingAssembly);
            }
            catch (TargetInvocationException)
            {
                threw = true;
            }

            var latchStuck = coordinator.ResetInProgress && coordinator.CompletedResetCount == 0;

            // A start while the latch is stuck is refused.
            InvokeLoad(throwingAssembly, true);
            var startRefused = coordinator.RefusalCount(RefusalKind.ResetInProgress) == 1;

            // A later reset that does reach its normal return heals the latch.
            InvokeReset(normalAssembly);
            var healed = !coordinator.ResetInProgress;

            SetStatic(throwingAssembly, "currentLoader", new object());
            SetStatic(throwingAssembly, "_gameMode", (byte)WorldReadyCoordinator.LoadScreenGameMode);
            InvokeLoad(throwingAssembly, true);
            var startAccepted = coordinator.PendingAttemptId != 0;

            Check("synthetic_reset_exception_leaves_latch",
                threw && latchStuck && startRefused && healed && startAccepted &&
                coordinator.ResetCount == 2 && coordinator.CompletedResetCount == 1,
                "threw=" + threw + ", latchStuck=" + latchStuck + ", startRefused=" + startRefused +
                ", healed=" + healed + ", startAccepted=" + startAccepted);
        }
        finally
        {
            PrototypeBridge.Install(null);
        }
    }

    private static void SyntheticResetShapeRejections()
    {
        var retInTry = SyntheticFixture.Build("reset-ret-try", resetShape: ResetShape.RetInsideTry);
        var retInTryTargets = PrototypeInjector.Resolve(retInTry.Module, SyntheticFixture.Game1Type);
        CheckThrows("synthetic_reset_ret_in_try_rejected", () =>
            PrototypeInjector.Inject(retInTryTargets, retInTry.Hooks));

        var handlerOnly = SyntheticFixture.Build("reset-handler-only",
            resetShape: ResetShape.HandlerOnlyRet);
        var handlerOnlyTargets = PrototypeInjector.Resolve(handlerOnly.Module, SyntheticFixture.Game1Type);
        CheckThrows("synthetic_reset_handler_only_ret_rejected", () =>
            PrototypeInjector.Inject(handlerOnlyTargets, handlerOnly.Hooks));

        var withFinally = SyntheticFixture.Build("reset-finally", resetShape: ResetShape.WithFinally);
        var withFinallyTargets = PrototypeInjector.Resolve(withFinally.Module, SyntheticFixture.Game1Type);
        var result = PrototypeInjector.Inject(withFinallyTargets, withFinally.Hooks);
        var verification = PrototypeInjector.Verify(withFinallyTargets, withFinally.Hooks);
        Check("synthetic_reset_with_finally_accepted",
            result.ResetCompletedCalls == 1 && verification.Ok &&
            verification.ResetNormalReturns == 1 && verification.ResetHandlerReturns == 0,
            "completedSites=" + result.ResetCompletedCalls + ", normalReturns=" +
            verification.ResetNormalReturns + ", problems=" + string.Join("; ", verification.Problems));
    }

    /// <summary>
    /// Negative control for the instruction order: reading <c>gameMode</c> before the reset epoch
    /// must be rejected by the verifier, because that is exactly the wiring that leaves the
    /// reset window open.
    /// </summary>
    private static void SyntheticEpochOrderRegression()
    {
        var fixture = SyntheticFixture.Build("epoch-order", withFinally: false);
        var targets = PrototypeInjector.Resolve(fixture.Module, SyntheticFixture.Game1Type);
        PrototypeInjector.Inject(targets, fixture.Hooks);

        // Rewrite the start sequence as: get_gameMode, ldarg, GetResetEpoch, currentLoader, call.
        var load = fixture.LoadForNewGame.Body;
        var first = load.Instructions[0];
        var getEpoch = first;
        var loadFlag = first.Next!;
        var getMode = loadFlag.Next!;
        var loadLoader = getMode.Next!;
        var call = loadLoader.Next!;

        // Swap the epoch read behind the mode read.
        getEpoch.OpCode = getMode.OpCode;
        getEpoch.Operand = getMode.Operand;
        getMode.OpCode = OpCodes.Call;
        getMode.Operand = fixture.GetResetEpochHook;

        var report = PrototypeInjector.Verify(targets, fixture.Hooks);
        Check("negative_epoch_read_after_mode_rejected",
            !report.EpochReadFirst && !report.Ok,
            "epochFirst=" + report.EpochReadFirst + ", problems=" +
            string.Join("; ", report.Problems));

        // Model-level negative control: the epoch-less Begin overload accepts a start whose
        // evidence was read before the reset. That is the "old wiring" that regresses.
        var c = NewCoordinator();
        var loader = new object();
        c.TryBeginReset(Tid, "title", out _, out _);
        c.TryCompleteReset(Tid, out _);
        var accepted = c.TryBeginAttempt(true, WorldReadyCoordinator.LoadScreenGameMode, loader, Tid,
            out var id, out var refusal);
        Check("negative_epochless_begin_accepts_start",
            accepted && refusal == RefusalKind.None && id != 0,
            "accepted=" + accepted + ", refusal=" + refusal +
            " (negative control: without the epoch argument this start cannot be told apart)");
    }

    private static void SyntheticResetEHEndBoundary()
    {
        var fixture = SyntheticFixture.Build("reset-eh-end", resetShape: ResetShape.HandlerEndsAtRet);
        var targets = PrototypeInjector.Resolve(fixture.Module, SyntheticFixture.Game1Type);
        PrototypeInjector.Inject(targets, fixture.Hooks);
        var eh = fixture.ResetGameStateOnTitleScreen.Body.ExceptionHandlers[0];
        var ret = fixture.ResetGameStateOnTitleScreen.Body.Instructions.Last();
        var hookStart = ret.Previous!;
        var verification = PrototypeInjector.Verify(targets, fixture.Hooks);
        Check("synthetic_reset_handler_end_moves_with_hook",
            verification.Ok && ReferenceEquals(eh.HandlerEnd, hookStart) &&
            verification.ResetNormalReturns == 1,
            "handlerEndMoved=" + ReferenceEquals(eh.HandlerEnd, hookStart) +
            ", epochFirst=" + verification.EpochReadFirst);
    }

    // ------------------------------------------------------------------ helpers

    private static int Tid
    {
        get { return Environment.CurrentManagedThreadId; }
    }

    private static WorldReadyCoordinator NewCoordinator()
    {
        var coordinator = new WorldReadyCoordinator();
        coordinator.TrySetGameLoopThread(Tid);
        return coordinator;
    }

    private static string SignalPath(WorldReadyCoordinator coordinator)
    {
        return string.Join(">", coordinator.Signals.Select(s =>
            s.Refusal == RefusalKind.None ? s.Kind.ToString() : s.Kind + ":" + s.Refusal));
    }

    private static Assembly Load(SyntheticFixture fixture, string tag)
    {
        var path = Path.Combine(_work, fixture.AssemblyName + "." + tag + ".dll");
        fixture.Module.Write(path);
        return Assembly.LoadFrom(path);
    }

    private static void InvokeLoad(Assembly assembly, bool loadedGame)
    {
        var type = assembly.GetType(SyntheticFixture.Game1Type)
                   ?? throw new InvalidOperationException("synthetic Game1 missing");
        var instance = Activator.CreateInstance(type)
                       ?? throw new InvalidOperationException("synthetic Game1 ctor failed");
        var method = type.GetMethod("loadForNewGame", BindingFlags.Public | BindingFlags.Instance)
                     ?? throw new InvalidOperationException("loadForNewGame missing");
        method.Invoke(instance, new object[] { loadedGame });
    }

    private static void InvokeDayStarted(Assembly assembly)
    {
        var type = assembly.GetType(SyntheticFixture.Game1Type)
                   ?? throw new InvalidOperationException("synthetic Game1 missing");
        var method = type.GetMethod("OnDayStarted", BindingFlags.Public | BindingFlags.Static)
                     ?? throw new InvalidOperationException("OnDayStarted missing");
        method.Invoke(null, null);
    }

    private static void InvokeReset(Assembly assembly)
    {
        var type = assembly.GetType(SyntheticFixture.Game1Type)
                   ?? throw new InvalidOperationException("synthetic Game1 missing");
        var instance = Activator.CreateInstance(type)
                       ?? throw new InvalidOperationException("synthetic Game1 ctor failed");
        var method = type.GetMethod(PrototypeInjector.ResetMethodName,
                         BindingFlags.Public | BindingFlags.Instance)
                     ?? throw new InvalidOperationException("ResetGameStateOnTitleScreen missing");
        method.Invoke(instance, null);
    }

    private static void SetStatic(Assembly assembly, string fieldName, object? value)
    {
        var type = assembly.GetType(SyntheticFixture.Game1Type)
                   ?? throw new InvalidOperationException("synthetic Game1 missing");
        var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Static)
                    ?? throw new InvalidOperationException("field missing: " + fieldName);
        field.SetValue(null, value);
    }

    private static T GetStatic<T>(Assembly assembly, string fieldName)
    {
        var type = assembly.GetType(SyntheticFixture.Game1Type)
                   ?? throw new InvalidOperationException("synthetic Game1 missing");
        var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Static)
                    ?? throw new InvalidOperationException("field missing: " + fieldName);
        return (T)field.GetValue(null)!;
    }

    private static void CheckThrows(string name, Action action)
    {
        var threw = false;
        try
        {
            action();
        }
        catch (PrototypeInjectionException)
        {
            threw = true;
        }

        Check(name, threw, threw ? "refused as expected" : "no refusal");
    }

    private static void Check(string name, bool ok, string detail)
    {
        _checks++;
        if (!ok)
        {
            _failures++;
        }

        Console.WriteLine((ok ? "PASS " : "FAIL ") + name.PadRight(52) + " " + detail);
    }

    private sealed class RecordingSink : IWorldReadySink
    {
        public int LoadingCount;
        public int CompletedCount;
        public int ResetBeganCount;
        public int ResetCompletedCount;
        public bool LastLoadedGame;
        public int LastGameMode;
        public bool LastHasLoadedGame;
        public object? LastLoader;
        public long LastStartEpoch = -1;
        public int LoadingThreadId;
        public int CompletedThreadId;
        public long Epoch;

        public long CurrentResetEpoch
        {
            get { return Epoch; }
        }

        public void OnResetBegan(long resetEpoch, int threadId)
        {
            ResetBeganCount++;
            Epoch++;
        }

        public void OnResetCompleted(long resetEpoch, int threadId, bool completed)
        {
            ResetCompletedCount++;
        }

        public void OnWorldLoading(long resetEpoch, bool loadedGame, int gameMode, object? loader,
            int threadId)
        {
            LoadingCount++;
            LastLoadedGame = loadedGame;
            LastLoader = loader;
            LastStartEpoch = resetEpoch;
            LoadingThreadId = threadId;
        }

        public void OnDayCompleted(int gameMode, bool hasLoadedGame, object? loader, int threadId)
        {
            CompletedCount++;
            LastGameMode = gameMode;
            LastHasLoadedGame = hasLoadedGame;
            LastLoader = loader;
            CompletedThreadId = threadId;
        }
    }

    /// <summary>
    /// Test-side sink that blocks between the injected epoch read and the coordinator commit, so
    /// the "read epoch, then reset, then commit" interleaving is deterministic instead of timing
    /// dependent.
    /// </summary>
    private sealed class PausingSink : IWorldReadySink
    {
        private readonly CoordinatorSink _inner;

        public readonly ManualResetEventSlim Reached = new ManualResetEventSlim(false);
        public readonly ManualResetEventSlim Resume = new ManualResetEventSlim(false);

        public PausingSink(WorldReadyCoordinator coordinator)
        {
            _inner = new CoordinatorSink(coordinator);
        }

        public long CurrentResetEpoch
        {
            get { return _inner.CurrentResetEpoch; }
        }

        public void OnResetBegan(long resetEpoch, int threadId)
        {
            _inner.OnResetBegan(resetEpoch, threadId);
        }

        public void OnResetCompleted(long resetEpoch, int threadId, bool completed)
        {
            _inner.OnResetCompleted(resetEpoch, threadId, completed);
        }

        public void OnWorldLoading(long resetEpoch, bool loadedGame, int gameMode, object? loader,
            int threadId)
        {
            Reached.Set();
            if (!Resume.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("stale-start race gate was never released");
            }

            _inner.OnWorldLoading(resetEpoch, loadedGame, gameMode, loader, threadId);
        }

        public void OnDayCompleted(int gameMode, bool hasLoadedGame, object? loader, int threadId)
        {
            _inner.OnDayCompleted(gameMode, hasLoadedGame, loader, threadId);
        }
    }
}
