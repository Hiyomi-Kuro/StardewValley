using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ModFramework.Runtime;

namespace ModFramework.Tests;

/// <summary>
/// Phase 2.0.1: correctness and cost of the event dispatcher, and the multi-root loader session
/// contract.
///
/// Every check here was written against a defect found by review:
///   * the first-dispatch order string was rebuilt (StringBuilder + ToString) on every tick;
///   * ModCount deduplicated only ADJACENT active entries, so A -&gt; B -&gt; A counted three mods;
///   * sessions were not isolated: BeginSession kept the previous session's subscriptions,
///     Subscribe/CommitScope did not validate the owner's SessionId, and aborting a session left
///     a stale context able to register again;
///   * GameLaunched "once per process" was reset by BeginSession;
///   * a mod handler could call the public DispatchTick/DispatchGameLaunched and recurse.
///
/// The allocation checks use <see cref="GC.GetAllocatedBytesForCurrentThread"/> on a fixed,
/// already-warmed subscription set and deliberately exclude EventDiagnostics.Capture() and any
/// logging, so the number is the cost of one stable dispatch, not of the measurement harness.
/// A desktop allocation number is not a frame-rate claim for Android.
/// </summary>
internal static class Phase201Tests
{
    internal static void Run(TestRunner t, string eventFixtureDir)
    {
        DispatcherSuite(t);

        if (eventFixtureDir.Length == 0)
        {
            t.Check("p201_loader_multi_root", false, "--event-fixture-dir was not provided");
        }
        else
        {
            LoaderMultiRootSuite(t, eventFixtureDir);
        }
    }

    // ------------------------------------------------------------------ allocation

    /// <summary>Fixed handler: no closure, no allocation, no logging.</summary>
    private static void NoOpTick(TickEventArgs args)
    {
        // deliberately empty: measuring dispatch, not the handler
    }

    private static void NoOpLaunched(GameLaunchedEventArgs args)
    {
        // deliberately empty
    }

    private sealed class AllocationSample
    {
        internal int Subscriptions;
        internal long Bytes;
        internal long Ticks;
        internal double ElapsedMilliseconds;

        internal double BytesPerTick
        {
            get { return Ticks == 0 ? 0 : (double)Bytes / Ticks; }
        }
    }

    /// <summary>
    /// Measures one stable-tick run. The caller must have prepared a ready session.
    /// </summary>
    private static AllocationSample MeasureStableTicks(int warmup, int measured)
    {
        for (var i = 0; i < warmup; i++)
        {
            EventDispatcher.DispatchTick();
        }

        // Timestamp-based timing: Stopwatch.StartNew() allocates a Stopwatch instance, and that
        // allocation would be counted by the measurement it is supposed to describe.
        var before = GC.GetAllocatedBytesForCurrentThread();
        var startTimestamp = Stopwatch.GetTimestamp();
        for (var i = 0; i < measured; i++)
        {
            EventDispatcher.DispatchTick();
        }

        var endTimestamp = Stopwatch.GetTimestamp();
        var after = GC.GetAllocatedBytesForCurrentThread();
        var elapsedMilliseconds = (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;
        return new AllocationSample
        {
            Subscriptions = EventDiagnostics.Capture().SubscriptionCount,
            Bytes = after - before,
            Ticks = measured,
            ElapsedMilliseconds = elapsedMilliseconds,
        };
    }

    private static void StableTickAllocations(TestRunner t)
    {
        // No subscriptions at all: even an empty dispatch must not build a diagnostic string.
        EventDispatcher.ResetForTests();
        var emptySession = EventDispatcher.BeginSession();
        EventDispatcher.CommitSession(emptySession, "1.6.15.0", "1.1.0");
        var empty = MeasureStableTicks(2000, 50000);
        t.Check("p201_stable_tick_zero_alloc_no_subscriptions", empty.Bytes == 0,
            "subscriptions=" + empty.Subscriptions + " bytes=" + empty.Bytes +
            " per-tick=" + empty.BytesPerTick.ToString("0.###", CultureInfo.InvariantCulture));

        // A fixed subscription set: three tick handlers in one mod plus one launch handler.
        EventDispatcher.ResetForTests();
        var session = EventDispatcher.BeginSession();
        var api = EventDispatcher.CreateModEvents("Mod.Perf", new NullLogger(), session);
        api.OnGameLaunched(NoOpLaunched);
        api.OnTick(NoOpTick);
        api.OnTick(NoOpTick);
        api.OnTick(NoOpTick);
        api.Commit();
        EventDispatcher.CommitSession(session, "1.6.15.0", "1.1.0");
        var withSubs = MeasureStableTicks(2000, 50000);
        t.Check("p201_stable_tick_zero_alloc_with_subscriptions", withSubs.Bytes == 0,
            "subscriptions=" + withSubs.Subscriptions + " bytes=" + withSubs.Bytes +
            " per-tick=" + withSubs.BytesPerTick.ToString("0.###", CultureInfo.InvariantCulture) +
            " elapsed-ms=" + withSubs.ElapsedMilliseconds.ToString("0.0", CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------ ModCount

    private static void ModCountSuite(TestRunner t)
    {
        // A -> B -> A interleaved registration: two distinct mods, three subscriptions.
        EventDispatcher.ResetForTests();
        var session = EventDispatcher.BeginSession();
        var a1 = EventDispatcher.CreateModEvents("Mod.Alpha", new NullLogger(), session);
        var b = EventDispatcher.CreateModEvents("Mod.Beta", new NullLogger(), session);
        var a2 = EventDispatcher.CreateModEvents("Mod.Alpha", new NullLogger(), session);
        var bToken = b.OnTick(NoOpTick);
        a1.OnTick(NoOpTick);
        a2.OnGameLaunched(NoOpLaunched);
        a1.Commit();
        b.Commit();
        a2.Commit();
        EventDispatcher.CommitSession(session, "1.6.15.0", "1.1.0");

        var interleaved = EventDiagnostics.Capture();
        t.Check("p201_mod_count_distinct_interleaved",
            interleaved.ModCount == 2 && interleaved.SubscriptionCount == 3,
            "mods=" + interleaved.ModCount + " subscriptions=" + interleaved.SubscriptionCount);

        // Disposing a mod's only subscription removes it from the count.
        bToken.Dispose();
        var afterDispose = EventDiagnostics.Capture();
        t.Check("p201_mod_count_after_dispose",
            afterDispose.ModCount == 1 && afterDispose.SubscriptionCount == 2,
            "mods=" + afterDispose.ModCount + " subscriptions=" + afterDispose.SubscriptionCount);

        // Same mod, both event kinds: still one mod.
        EventDispatcher.ResetForTests();
        var session2 = EventDispatcher.BeginSession();
        var multi = EventDispatcher.CreateModEvents("Mod.Multi", new NullLogger(), session2);
        multi.OnTick(NoOpTick);
        multi.OnGameLaunched(NoOpLaunched);
        multi.Commit();
        EventDispatcher.CommitSession(session2, "1.6.15.0", "1.1.0");
        var multiState = EventDiagnostics.Capture();
        t.Check("p201_mod_count_same_mod_multiple_events",
            multiState.ModCount == 1 && multiState.SubscriptionCount == 2,
            "mods=" + multiState.ModCount + " subscriptions=" + multiState.SubscriptionCount);

        // Disposing a mod's only subscription removes it from the count.
        bToken.Dispose();
        // Identity follows the loader's mod-id rule (ordinal-ignore-case), not raw string case.
        EventDispatcher.ResetForTests();
        var session3 = EventDispatcher.BeginSession();
        var upper = EventDispatcher.CreateModEvents("Mod.Case", new NullLogger(), session3);
        var lower = EventDispatcher.CreateModEvents("mod.case", new NullLogger(), session3);
        upper.OnTick(NoOpTick);
        lower.OnGameLaunched(NoOpLaunched);
        upper.Commit();
        lower.Commit();
        EventDispatcher.CommitSession(session3, "1.6.15.0", "1.1.0");
        t.Check("p201_mod_count_case_insensitive_identity",
            EventDiagnostics.Capture().ModCount == 1,
            "mods=" + EventDiagnostics.Capture().ModCount);

        // Empty set.
        EventDispatcher.ResetForTests();
        var emptySession = EventDispatcher.BeginSession();
        EventDispatcher.CommitSession(emptySession, "1.6.15.0", "1.1.0");
        t.Check("p201_mod_count_empty", EventDiagnostics.Capture().ModCount == 0,
            "mods=" + EventDiagnostics.Capture().ModCount);
    }

    // ------------------------------------------------------------------ first order

    private static void FirstOrderRecordedOnce(TestRunner t)
    {
        EventDispatcher.ResetForTests();
        var session = EventDispatcher.BeginSession();
        var api = EventDispatcher.CreateModEvents("Mod.Late", new NullLogger(), session);
        EventDispatcher.CommitSession(session, "1.6.15.0", "1.1.0");

        // First tick: nobody subscribed. The first fact is "no mods", and it must stay that way.
        EventDispatcher.DispatchTick();
        var first = EventDiagnostics.Capture();
        t.Check("p201_first_tick_order_recorded_even_when_empty", first.FirstTickOrder.Length == 0,
            "order=\"" + first.FirstTickOrder + "\"");

        var calls = 0;
        api.OnTick(_ => calls++);
        api.Commit();
        EventDispatcher.DispatchTick();
        var second = EventDiagnostics.Capture();
        t.Check("p201_first_tick_order_not_rewritten_after_registration",
            calls == 1 && second.FirstTickOrder.Length == 0,
            "calls=" + calls + " order=\"" + second.FirstTickOrder + "\"");

        // A populated first dispatch keeps its exact value on later ticks (no rebuild is observable
        // here, but the value must never drift).
        EventDispatcher.ResetForTests();
        var session2 = EventDispatcher.BeginSession();
        var a = EventDispatcher.CreateModEvents("Zeta", new NullLogger(), session2);
        var b = EventDispatcher.CreateModEvents("alpha", new NullLogger(), session2);
        a.OnTick(NoOpTick);
        b.OnTick(NoOpTick);
        a.Commit();
        b.Commit();
        EventDispatcher.CommitSession(session2, "1.6.15.0", "1.1.0");
        EventDispatcher.DispatchTick();
        var populated = EventDiagnostics.Capture().FirstTickOrder;
        EventDispatcher.DispatchTick();
        t.Check("p201_first_tick_order_stable", populated == "Zeta,alpha" &&
            EventDiagnostics.Capture().FirstTickOrder == populated, populated);
    }

    // ------------------------------------------------------------------ sessions

    private static void SessionSuite(TestRunner t)
    {
        // 1. A committed second session replaces the first session's subscriptions.
        EventDispatcher.ResetForTests();
        var s1 = EventDispatcher.BeginSession();
        var oldApi = EventDispatcher.CreateModEvents("Mod.Old", new NullLogger(), s1);
        var oldCalls = 0;
        oldApi.OnTick(_ => oldCalls++);
        oldApi.Commit();
        EventDispatcher.CommitSession(s1, "1.6.15.0", "1.1.0");
        EventDispatcher.DispatchTick();
        var oldAfterFirst = oldCalls;

        var s2 = EventDispatcher.BeginSession();
        var newApi = EventDispatcher.CreateModEvents("Mod.New", new NullLogger(), s2);
        var newCalls = 0;
        newApi.OnTick(_ => newCalls++);
        newApi.Commit();
        EventDispatcher.CommitSession(s2, "1.6.15.0", "1.1.0");
        EventDispatcher.DispatchTick();

        var replaced = EventDiagnostics.Capture();
        t.Check("p201_session_replacement_drops_previous_subscriptions",
            oldCalls == oldAfterFirst && newCalls == 1 && replaced.SubscriptionCount == 1 &&
            replaced.SessionId == s2,
            "old=" + oldCalls + " new=" + newCalls + " subscriptions=" + replaced.SubscriptionCount +
            " session=" + replaced.SessionId);

        // 2. A failed candidate session does not revoke the session that was already serving.
        EventDispatcher.ResetForTests();
        var live = EventDispatcher.BeginSession();
        var liveApi = EventDispatcher.CreateModEvents("Mod.Live", new NullLogger(), live);
        var liveCalls = 0;
        liveApi.OnTick(_ => liveCalls++);
        liveApi.Commit();
        EventDispatcher.CommitSession(live, "1.6.15.0", "1.1.0");
        EventDispatcher.DispatchTick();
        var liveBefore = liveCalls;

        var failed = EventDispatcher.BeginSession();
        var failedApi = EventDispatcher.CreateModEvents("Mod.Failed", new NullLogger(), failed);
        var failedCalls = 0;
        failedApi.OnTick(_ => failedCalls++);
        failedApi.Commit();
        EventDispatcher.AbortSession(failed);
        EventDispatcher.DispatchTick();

        var restored = EventDiagnostics.Capture();
        t.Check("p201_session_abort_restores_previous_session",
            liveCalls == liveBefore + 1 && failedCalls == 0 && restored.Ready &&
            restored.SubscriptionCount == 1,
            "live=" + liveCalls + " failed=" + failedCalls + " ready=" + restored.Ready +
            " subscriptions=" + restored.SubscriptionCount);

        // 3. A context from an aborted session can never register again. The subscription is
        //    created first and checked before it is disposed: disposing immediately would hide a
        //    ghost registration from the count.
        var ghostToken = failedApi.OnTick(_ => failedCalls++);
        EventDispatcher.DispatchTick();
        t.Check("p201_subscribe_after_abort_is_inert",
            failedCalls == 0 && EventDiagnostics.Capture().SubscriptionCount == 1,
            "failed=" + failedCalls + " subscriptions=" +
            EventDiagnostics.Capture().SubscriptionCount);
        ghostToken.Dispose();
        t.Check("p201_aborted_context_token_is_disposable",
            EventDiagnostics.Capture().SubscriptionCount == 1,
            "subscriptions=" + EventDiagnostics.Capture().SubscriptionCount);

        // 4. An uncommitted scope from the previous session cannot commit into the new one.
        EventDispatcher.ResetForTests();
        var staleSession = EventDispatcher.BeginSession();
        var staleApi = EventDispatcher.CreateModEvents("Mod.Stale", new NullLogger(), staleSession);
        var staleCalls = 0;
        staleApi.OnTick(_ => staleCalls++);
        // no Commit() yet: this is the "old scope commits late" shape.

        var winner = EventDispatcher.BeginSession();
        var winnerApi = EventDispatcher.CreateModEvents("Mod.Winner", new NullLogger(), winner);
        var winnerCalls = 0;
        winnerApi.OnTick(_ => winnerCalls++);
        winnerApi.Commit();
        EventDispatcher.CommitSession(winner, "1.6.15.0", "1.1.0");

        staleApi.Commit();
        EventDispatcher.DispatchTick();
        var staleState = EventDiagnostics.Capture();
        t.Check("p201_stale_scope_commit_rejected",
            staleCalls == 0 && winnerCalls == 1 && staleState.SubscriptionCount == 1,
            "stale=" + staleCalls + " winner=" + winnerCalls +
            " subscriptions=" + staleState.SubscriptionCount);

        // 5. Disposing a token from the previous session must not touch the new session's
        //    subscription, even when both wrap the same handler.
        EventDispatcher.ResetForTests();
        var first = EventDispatcher.BeginSession();
        var firstApi = EventDispatcher.CreateModEvents("Mod.First", new NullLogger(), first);
        var sharedCalls = 0;
        Action<TickEventArgs> shared = _ => sharedCalls++;
        var oldToken = firstApi.OnTick(shared);
        firstApi.Commit();
        EventDispatcher.CommitSession(first, "1.6.15.0", "1.1.0");

        var second = EventDispatcher.BeginSession();
        var secondApi = EventDispatcher.CreateModEvents("Mod.Second", new NullLogger(), second);
        secondApi.OnTick(shared);
        secondApi.Commit();
        EventDispatcher.CommitSession(second, "1.6.15.0", "1.1.0");

        oldToken.Dispose();
        oldToken.Dispose();
        EventDispatcher.DispatchTick();
        t.Check("p201_stale_token_dispose_preserves_new_session",
            sharedCalls == 1 && EventDiagnostics.Capture().SubscriptionCount == 1,
            "calls=" + sharedCalls + " subscriptions=" +
            EventDiagnostics.Capture().SubscriptionCount);

        // 6. Aborting a session after some of its mods committed leaves no ghost at all.
        EventDispatcher.ResetForTests();
        var doomed = EventDispatcher.BeginSession();
        var doomedApi = EventDispatcher.CreateModEvents("Mod.Doomed", new NullLogger(), doomed);
        doomedApi.OnTick(NoOpTick);
        doomedApi.Commit();
        EventDispatcher.CommitSession(doomed, "1.6.15.0", "1.1.0");
        EventDispatcher.AbortSession(doomed);
        var aborted = EventDiagnostics.Capture();
        t.Check("p201_aborted_session_leaves_no_ghost_subscriptions",
            aborted.SubscriptionCount == 0 && !aborted.Ready,
            "subscriptions=" + aborted.SubscriptionCount + " ready=" + aborted.Ready);
    }

    private static void GameLaunchedLifecycle(TestRunner t)
    {
        // GameLaunched is at most once per PROCESS. A new loader session re-arms Tick, not the
        // launch event, and it must not make the process look freshly started.
        EventDispatcher.ResetForTests();
        var first = EventDispatcher.BeginSession();
        var firstApi = EventDispatcher.CreateModEvents("Mod.Launch1", new NullLogger(), first);
        var firstLaunches = 0;
        firstApi.OnGameLaunched(_ => firstLaunches++);
        firstApi.Commit();
        EventDispatcher.CommitSession(first, "1.6.15.0", "1.1.0");
        EventDispatcher.DispatchGameLaunched();

        var second = EventDispatcher.BeginSession();
        var secondApi = EventDispatcher.CreateModEvents("Mod.Launch2", new NullLogger(), second);
        var secondLaunches = 0;
        secondApi.OnGameLaunched(_ => secondLaunches++);
        secondApi.Commit();
        EventDispatcher.CommitSession(second, "1.6.15.0", "1.1.0");
        EventDispatcher.DispatchGameLaunched();

        var state = EventDiagnostics.Capture();
        t.Check("p201_game_launched_once_per_process_across_sessions",
            firstLaunches == 1 && secondLaunches == 0 && state.GameLaunchedCount == 1,
            "first=" + firstLaunches + " second=" + secondLaunches +
            " count=" + state.GameLaunchedCount + " session=" + state.SessionId);
    }

    private static void Reentrancy(TestRunner t)
    {
        // A handler that calls the public bridge entry point again must not recurse or reorder.
        // The nested call is bounded by a flag so a broken implementation fails instead of
        // overflowing the stack (which would be a crash, not evidence).
        EventDispatcher.ResetForTests();
        var session = EventDispatcher.BeginSession();
        var api = EventDispatcher.CreateModEvents("Mod.Reentrant", new NullLogger(), session);
        var inTick = false;
        var tickCalls = 0;
        var nestedTicks = 0;
        api.OnTick(_ =>
        {
            tickCalls++;
            if (inTick)
            {
                return;
            }

            inTick = true;
            nestedTicks++;
            EventDispatcher.DispatchTick();
            inTick = false;
        });
        api.Commit();
        EventDispatcher.CommitSession(session, "1.6.15.0", "1.1.0");

        EventDispatcher.DispatchTick();
        var tickState = EventDiagnostics.Capture();
        t.Check("p201_reentrant_tick_dispatch_rejected",
            tickCalls == 1 && nestedTicks == 1 && tickState.TickCount == 1 &&
            tickState.ReentrantDispatchRejections == 1,
            "handler-calls=" + tickCalls + " nested-attempts=" + nestedTicks +
            " tick-count=" + tickState.TickCount +
            " rejected=" + tickState.ReentrantDispatchRejections);

        // A launch handler that asks for a Tick must not manufacture a tick before the launch
        // dispatch has even finished.
        EventDispatcher.ResetForTests();
        var launchSession = EventDispatcher.BeginSession();
        var launchApi = EventDispatcher.CreateModEvents("Mod.LaunchReentrant", new NullLogger(),
            launchSession);
        var launches = 0;
        var ticks = 0;
        launchApi.OnGameLaunched(_ =>
        {
            launches++;
            EventDispatcher.DispatchTick();
        });
        launchApi.OnTick(_ => ticks++);
        launchApi.Commit();
        EventDispatcher.CommitSession(launchSession, "1.6.15.0", "1.1.0");
        EventDispatcher.DispatchGameLaunched();

        var launchState = EventDiagnostics.Capture();
        t.Check("p201_reentrant_tick_from_launch_handler_rejected",
            launches == 1 && ticks == 0 && launchState.TickCount == 0 &&
            launchState.ReentrantDispatchRejections == 1,
            "launches=" + launches + " ticks=" + ticks + " tick-count=" + launchState.TickCount +
            " rejected=" + launchState.ReentrantDispatchRejections);
    }

    private static void DispatcherSuite(TestRunner t)
    {
        StableTickAllocations(t);
        ModCountSuite(t);
        FirstOrderRecordedOnce(t);
        SessionSuite(t);
        GameLaunchedLifecycle(t);
        Reentrancy(t);
    }

    // ------------------------------------------------------------------ loader: multiple roots

    private static void LoaderMultiRootSuite(TestRunner t, string fixtureDir)
    {
        var rootA = Temp.NewDirectory("p201-root-a");
        var rootB = Temp.NewDirectory("p201-root-b");
        var aDll = Path.Combine(fixtureDir, "EventMod.A.dll");
        var zDll = Path.Combine(fixtureDir, "EventMod.Z.dll");
        StagePackage(rootA, "EventMod.A", aDll);
        StagePackage(rootB, "EventMod.Z", zDll);

        EventDispatcher.ResetForTests();
        var first = Loader.Run(rootA);
        var reportA = Temp.ReadText(Path.Combine(rootA, "loader-report.txt"));
        EventDispatcher.DispatchTick();
        EventDispatcher.DispatchTick();
        var aStateBefore = ReadState(rootA, "EventMod.A");
        var sessionA = EventDiagnostics.Capture().SessionId;

        var second = Loader.Run(rootB);
        var reportB = Temp.ReadText(Path.Combine(rootB, "loader-report.txt"));
        var afterSwitch = EventDiagnostics.Capture();
        EventDispatcher.DispatchTick();
        var aStateAfter = ReadState(rootA, "EventMod.A");
        var zState = ReadState(rootB, "EventMod.Z");

        t.Check("p201_loader_second_root_is_a_new_session",
            first == null && second == null &&
            reportA.IndexOf("events=ready", StringComparison.Ordinal) >= 0 &&
            reportB.IndexOf("events=ready", StringComparison.Ordinal) >= 0 &&
            afterSwitch.SessionId > sessionA,
            "session-a=" + sessionA + " session-b=" + afterSwitch.SessionId);

        t.Check("p201_loader_second_root_replaces_previous_subscriptions",
            afterSwitch.SubscriptionCount == 3 && afterSwitch.ModCount == 1,
            "subscriptions=" + afterSwitch.SubscriptionCount + " mods=" + afterSwitch.ModCount);

        // A's two duplicate tick handlers must not observe the tick issued after B's session
        // committed: A is from the replaced session.
        t.Check("p201_loader_second_root_drops_old_subscriptions",
            Field(aStateBefore, "tick_calls") == "4" && Field(aStateAfter, "tick_calls") == "4",
            "before=" + Field(aStateBefore, "tick_calls") +
            " after=" + Field(aStateAfter, "tick_calls"));
        // Z registers a faulting handler and one healthy one; only the healthy handler increments
        // tick_calls, so one dispatched tick means tick_calls=1.
        t.Check("p201_loader_second_root_dispatches_new_mod",
            Field(zState, "tick_calls") == "1" && Field(zState, "first_tick") == "1",
            "z tick_calls=" + Field(zState, "tick_calls") + " first_tick=" +
            Field(zState, "first_tick"));

        // A failed candidate run does not disturb the session that is already serving.
        var zBefore = Field(ReadState(rootB, "EventMod.Z"), "tick_calls");
        var failedRoot = Path.Combine(Temp.NewDirectory("p201-missing-root"), "does-not-exist");
        var failed = Loader.Run(failedRoot);
        var afterFailure = EventDiagnostics.Capture();
        EventDispatcher.DispatchTick();
        var zAfter = Field(ReadState(rootB, "EventMod.Z"), "tick_calls");
        t.Check("p201_loader_failed_run_restores_previous_session",
            failed != null && afterFailure.Ready && afterFailure.SessionId >= sessionA,
            "failed=" + (failed ?? "(null)") + " ready=" + afterFailure.Ready);
        t.Check("p201_loader_failed_run_keeps_dispatching",
            zBefore == "1" && zAfter == "2",
            "before=" + zBefore + " after=" + zAfter);

        // The promised same-root idempotence still holds and does not create a session.
        var repeat = Loader.Run(rootB);
        var repeatReport = Temp.ReadText(Path.Combine(rootB, "loader-report.txt"));
        var afterRepeat = EventDiagnostics.Capture();
        t.Check("p201_loader_same_root_still_idempotent",
            repeat == null &&
            repeatReport.IndexOf("result=already-initialized", StringComparison.Ordinal) >= 0 &&
            afterRepeat.SessionId == afterFailure.SessionId &&
            afterRepeat.SubscriptionCount == 3,
            "session=" + afterRepeat.SessionId + " subscriptions=" + afterRepeat.SubscriptionCount);
    }

    private static void StagePackage(string root, string id, string dllPath)
    {
        var directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        File.Copy(dllPath, Path.Combine(directory, Path.GetFileName(dllPath)), true);
        var manifest =
            "{\n" +
            "  \"schemaVersion\": 1,\n" +
            "  \"id\": \"" + id + "\",\n" +
            "  \"name\": \"" + id + "\",\n" +
            "  \"version\": \"1.0.0\",\n" +
            "  \"entryAssembly\": \"" + Path.GetFileName(dllPath) + "\",\n" +
            "  \"entryType\": \"EventFixture.Entry\",\n" +
            "  \"gameVersion\": \"*\",\n" +
            "  \"frameworkVersion\": \">=1.1.0\",\n" +
            "  \"description\": \"phase 2.0.1 fixture\"\n" +
            "}\n";
        File.WriteAllText(Path.Combine(directory, "manifest.json"), manifest);
    }

    private static string ReadState(string root, string id)
    {
        return Temp.ReadText(Path.Combine(root, id, "events-state.txt"));
    }

    private static string Field(string state, string key)
    {
        var lines = state.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var cut = line.IndexOf('=');
            if (cut > 0 && line.Substring(0, cut) == key)
            {
                return line.Substring(cut + 1).Trim();
            }
        }

        return "";
    }

    // ------------------------------------------------------------------ perf evidence

    /// <summary>
    /// Writes the desktop allocation measurement as JSON. Excluded from the assertions: this
    /// method itself allocates (strings, file I/O) and is called after every measured loop.
    /// </summary>
    internal static void Perf(string outputPath)
    {
        var samples = new List<AllocationSample>();

        EventDispatcher.ResetForTests();
        var emptySession = EventDispatcher.BeginSession();
        EventDispatcher.CommitSession(emptySession, "1.6.15.0", "1.1.0");
        samples.Add(MeasureStableTicks(5000, 200000));

        EventDispatcher.ResetForTests();
        var session = EventDispatcher.BeginSession();
        var api = EventDispatcher.CreateModEvents("Mod.Perf", new NullLogger(), session);
        api.OnGameLaunched(NoOpLaunched);
        api.OnTick(NoOpTick);
        api.OnTick(NoOpTick);
        api.OnTick(NoOpTick);
        api.Commit();
        EventDispatcher.CommitSession(session, "1.6.15.0", "1.1.0");
        samples.Add(MeasureStableTicks(5000, 200000));

        var builder = new StringBuilder();
        builder.Append("{\n");
        builder.Append("  \"phase\": \"2.0.1\",\n");
        builder.Append("  \"generated_utc\": \"").Append(DateTime.UtcNow.ToString("O")).Append("\",\n");
        builder.Append("  \"framework\": \"").Append(Escape(RuntimeInformation.FrameworkDescription)).Append("\",\n");
        builder.Append("  \"os\": \"").Append(Escape(RuntimeInformation.OSDescription)).Append("\",\n");
        builder.Append("  \"process_arch\": \"").Append(Escape(RuntimeInformation.ProcessArchitecture.ToString())).Append("\",\n");
        builder.Append("  \"processor_count\": ").Append(Environment.ProcessorCount).Append(",\n");
        builder.Append("  \"server_gc\": ").Append(System.Runtime.GCSettings.IsServerGC ? "true" : "false").Append(",\n");
        builder.Append("  \"warmup_ticks\": 5000,\n");
        builder.Append("  \"measured_ticks\": 200000,\n");
        builder.Append("  \"excludes\": [\"EventDiagnostics.Capture\", \"logging\", \"handler bodies\"],\n");
        builder.Append("  \"runs\": [\n");
        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            builder.Append("    {\"subscriptions\": ").Append(s.Subscriptions)
                .Append(", \"bytes\": ").Append(s.Bytes)
                .Append(", \"bytes_per_tick\": ").Append(s.BytesPerTick.ToString("0.####", CultureInfo.InvariantCulture))
                .Append(", \"elapsed_ms\": ").Append(s.ElapsedMilliseconds.ToString("0.0", CultureInfo.InvariantCulture))
                .Append("}").Append(i + 1 < samples.Count ? "," : "").Append("\n");
        }

        builder.Append("  ],\n");
        builder.Append("  \"note\": \"Desktop allocation only; not an Android frame-rate claim.\"\n");
        builder.Append("}\n");

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(outputPath, builder.ToString());
        Console.WriteLine("perf report: " + outputPath);
        foreach (var s in samples)
        {
            Console.WriteLine("  subscriptions=" + s.Subscriptions + " bytes/tick=" +
                              s.BytesPerTick.ToString("0.####", CultureInfo.InvariantCulture) +
                              " elapsed-ms=" + s.ElapsedMilliseconds.ToString("0.0", CultureInfo.InvariantCulture));
        }
    }

    private static string Escape(string text)
    {
        return (text ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private sealed class NullLogger : IModLogger
    {
        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }

        public void Error(string message)
        {
        }

        public void Error(string message, Exception exception)
        {
        }
    }
}
