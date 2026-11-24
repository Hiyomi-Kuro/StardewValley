using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Phase21.WorldReady;

namespace Phase21PrototypeTests;

/// <summary>
/// Phase 2.1-P host acceptance matrix.
///
/// <para>Section 1 exercises the coordinator with explicit interleavings. Section 2 builds a
/// synthetic assembly with Cecil, injects the prototype hooks, writes it, loads it and executes
/// it. Both are offline: no game assembly is loaded, no device is touched.</para>
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

    // ------------------------------------------------------------------ section 2: synthetic IL

    private static void SyntheticCases()
    {
        SyntheticSuccess();
        SyntheticBranchToRet();
        SyntheticExceptionNoCompletion();
        SyntheticRejections();
        SyntheticEHEndBoundary();
        SyntheticStaleCompletionThroughHooks();
    }

    private static void SyntheticSuccess()
    {
        var fixture = SyntheticFixture.Build("ok");
        var targets = PrototypeInjector.Resolve(fixture.Module, SyntheticFixture.Game1Type);
        var hooks = new PrototypeHooks(fixture.StartHook, fixture.CompletionHook);
        var result = PrototypeInjector.Inject(targets, hooks);
        var verification = PrototypeInjector.Verify(targets, hooks);
        Check("synthetic_injection_shape",
            result.WorldLoadingCalls == 1 && result.DayCompletedCalls == 1 && verification.Ok,
            "start=" + result.WorldLoadingCalls + ", completion=" + result.DayCompletedCalls +
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
        var hooks = new PrototypeHooks(fixture.StartHook, fixture.CompletionHook);
        PrototypeInjector.Inject(targets, hooks);
        var verification = PrototypeInjector.Verify(targets, hooks);

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

        // The same signal is refused by the coordinator in title mode.
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
        PrototypeInjector.Inject(targets, new PrototypeHooks(fixture.StartHook, fixture.CompletionHook));

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
            PrototypeInjector.Inject(retInTryTargets,
                new PrototypeHooks(retInTry.StartHook, retInTry.CompletionHook)));

        var handlerOnly = SyntheticFixture.Build("handler-only", handlerOnlyRet: true);
        var handlerTargets = PrototypeInjector.Resolve(handlerOnly.Module, SyntheticFixture.Game1Type);
        CheckThrows("synthetic_handler_only_ret_rejected", () =>
            PrototypeInjector.Inject(handlerTargets,
                new PrototypeHooks(handlerOnly.StartHook, handlerOnly.CompletionHook)));

        var twice = SyntheticFixture.Build("twice");
        var twiceTargets = PrototypeInjector.Resolve(twice.Module, SyntheticFixture.Game1Type);
        var twiceHooks = new PrototypeHooks(twice.StartHook, twice.CompletionHook);
        PrototypeInjector.Inject(twiceTargets, twiceHooks);
        CheckThrows("synthetic_double_inject_rejected", () =>
            PrototypeInjector.Inject(twiceTargets, twiceHooks));

        var bypass = SyntheticFixture.Build("bypass", withFinally: false);
        var bypassTargets = PrototypeInjector.Resolve(bypass.Module, SyntheticFixture.Game1Type);
        var bypassHooks = new PrototypeHooks(bypass.StartHook, bypass.CompletionHook);
        var ret = bypass.OnDayStarted.Body.Instructions.Last();
        var bil = bypass.OnDayStarted.Body.GetILProcessor();
        bil.InsertBefore(ret, bil.Create(OpCodes.Call, bypass.CompletionHook));
        bil.InsertBefore(bypass.OnDayStarted.Body.Instructions[0], bil.Create(OpCodes.Br, ret));
        var bypassReport = PrototypeInjector.Verify(bypassTargets, bypassHooks);
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
        var hooks = new PrototypeHooks(fixture.StartHook, fixture.CompletionHook);
        PrototypeInjector.Inject(targets, hooks);
        var eh = method.Body.ExceptionHandlers[0];
        var hookStart = ret.Previous!.Previous!.Previous!.Previous!;
        var verification = PrototypeInjector.Verify(targets, hooks);

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
        PrototypeInjector.Inject(targets, new PrototypeHooks(fixture.StartHook, fixture.CompletionHook));

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
        public bool LastLoadedGame;
        public int LastGameMode;
        public bool LastHasLoadedGame;
        public object? LastLoader;
        public int LoadingThreadId;
        public int CompletedThreadId;

        public void OnWorldLoading(bool loadedGame, object? loader, int gameMode, int threadId)
        {
            LoadingCount++;
            LastLoadedGame = loadedGame;
            LastLoader = loader;
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
}
