using System;
using System.Collections.Generic;
using System.IO;
using ModFramework.Runtime;

namespace ModFramework.Tests;

/// <summary>
/// Phase 2.0: the event contract.
///
/// Two layers:
///   * dispatcher level - drives <c>EventDispatcher</c> directly (ready gate, once-only launch,
///     1-based monotonic ticks, ordinal order, duplicate registration, idempotent dispose, frozen
///     snapshot semantics, per-handler isolation, bounded fault logging, staged/rolled-back
///     subscriptions, session abort);
///   * loader level - real fixture mod assemblies through the real manifest -&gt; preflight -&gt;
///     LoadFrom -&gt; Initialize chain, plus the legacy (1.0.0-built) mod binary-compatibility run.
///
/// No game assembly, no device and no network are involved.
/// </summary>
internal static class Phase20Tests
{
    internal static void Run(TestRunner t, string eventFixtureDir, string legacyModDir)
    {
        DispatcherSuite(t);

        if (eventFixtureDir.Length == 0)
        {
            t.Check("phase20_loader_events", false, "--event-fixture-dir was not provided");
        }
        else
        {
            LoaderSuite(t, eventFixtureDir);
        }

        if (legacyModDir.Length == 0)
        {
            Console.WriteLine("SKIP phase20_legacy_binary_compat             --legacy-mod-dir not provided (needs the frozen 1.0.0 contract build)");
        }
        else
        {
            LegacyBinaryCompatibility(t, legacyModDir);
        }
    }

    // ------------------------------------------------------------------ dispatcher level

    private static void DispatcherSuite(TestRunner t)
    {
        ReadyGate(t);
        OnceOnlyLaunch(t);
        TickCounting(t);
        OrdinalOrder(t);
        DuplicateRegistration(t);
        IdempotentDispose(t);
        SnapshotSemantics(t);
        FaultIsolation(t);
        BoundedFaultLogging(t);
        StagedRollback(t);
        SessionAbort(t);
    }

    private static void ReadyGate(TestRunner t)
    {
        EventDispatcher.ResetForTests();
        var logger = new RecordingLogger();
        var session = EventDispatcher.BeginSession();
        var api = EventDispatcher.CreateModEvents("Mod.Ready", logger, session);
        var ticks = 0;
        var launches = 0;
        api.OnTick(_ => ticks++);
        api.OnGameLaunched(_ => launches++);
        api.Commit();

        EventDispatcher.DispatchTick();
        EventDispatcher.DispatchGameLaunched();

        var before = EventDiagnostics.Capture();
        t.Check("events_ready_gate_no_dispatch_before_commit",
            ticks == 0 && launches == 0 && before.TickCount == 0 && before.GameLaunchedCount == 0,
            "ticks=" + ticks + " launches=" + launches + " ready=" + before.Ready);

        EventDispatcher.CommitSession(session, "1.6.15.0", "1.1.0");
        EventDispatcher.DispatchGameLaunched();
        EventDispatcher.DispatchTick();
        var after = EventDiagnostics.Capture();
        t.Check("events_ready_gate_dispatches_after_commit",
            ticks == 1 && launches == 1 && after.TickCount == 1 && after.GameLaunchedCount == 1,
            "ticks=" + ticks + " launches=" + launches + " ready=" + after.Ready);
    }

    private static void OnceOnlyLaunch(TestRunner t)
    {
        EventDispatcher.ResetForTests();
        var logger = new RecordingLogger();
        var session = Ready("Mod.Once", logger, out var api);
        var launches = 0;
        api.OnGameLaunched(_ => launches++);
        api.Commit();

        EventDispatcher.DispatchGameLaunched();
        EventDispatcher.DispatchGameLaunched();
        EventDispatcher.DispatchGameLaunched();

        var snapshot = EventDiagnostics.Capture();
        t.Check("events_game_launched_exactly_once",
            launches == 1 && snapshot.GameLaunchedCount == 1,
            "handler=" + launches + " diagnostics=" + snapshot.GameLaunchedCount);
    }

    private static void TickCounting(TestRunner t)
    {
        EventDispatcher.ResetForTests();
        var logger = new RecordingLogger();
        var session = Ready("Mod.Ticks", logger, out var api);
        var seen = new List<long>();
        var launches = 0;
        api.OnTick(args => seen.Add(args.TickCount));
        api.OnGameLaunched(_ => launches++);
        api.Commit();

        EventDispatcher.DispatchGameLaunched();
        EventDispatcher.DispatchTick();
        EventDispatcher.DispatchTick();
        EventDispatcher.DispatchTick();

        var snapshot = EventDiagnostics.Capture();
        t.Check("events_tick_starts_at_one_and_increments",
            seen.Count == 3 && seen[0] == 1 && seen[1] == 2 && seen[2] == 3,
            "seen=" + string.Join(",", seen));
        t.Check("events_tick_totals_match", snapshot.TickCount == 3, snapshot.TickCount.ToString());
        t.Check("events_launch_observed_before_first_tick",
            launches == 1 && snapshot.GameLaunchedBeforeFirstTick && snapshot.FirstGameLaunchedThreadId != 0,
            "beforeFirstTick=" + snapshot.GameLaunchedBeforeFirstTick +
            " launchThread=" + snapshot.FirstGameLaunchedThreadId +
            " tickThread=" + snapshot.FirstTickThreadId);
    }

    private static void OrdinalOrder(TestRunner t)
    {
        EventDispatcher.ResetForTests();
        var session = EventDispatcher.BeginSession();
        // Registered in the "wrong" order and with ids whose ordinal order differs from a
        // culture-aware comparison: "Zeta" < "alpha" ordinally, but culture-aware sorting would
        // put "alpha" first.
        var last = EventDispatcher.CreateModEvents("Zeta", new RecordingLogger(), session);
        var first = EventDispatcher.CreateModEvents("alpha", new RecordingLogger(), session);
        var observed = new List<string>();
        last.OnTick(_ => observed.Add("Zeta"));
        first.OnTick(_ => observed.Add("alpha"));
        last.Commit();
        first.Commit();
        EventDispatcher.CommitSession(session, "1.6.15.0", "1.1.0");

        EventDispatcher.DispatchTick();

        var snapshot = EventDiagnostics.Capture();
        t.Check("events_order_is_ordinal_mod_id",
            observed.Count == 2 && observed[0] == "Zeta" && observed[1] == "alpha",
            string.Join(",", observed));
        t.Check("events_order_reported", snapshot.FirstTickOrder == "Zeta,alpha", snapshot.FirstTickOrder);
    }

    private static void DuplicateRegistration(TestRunner t)
    {
        EventDispatcher.ResetForTests();
        var logger = new RecordingLogger();
        var session = Ready("Mod.Dup", logger, out var api);
        var calls = 0;
        Action<TickEventArgs> handler = _ => calls++;
        var first = api.OnTick(handler);
        var second = api.OnTick(handler);
        api.Commit();
        EventDispatcher.CommitSession(session, "1.6.15.0", "1.1.0");

        EventDispatcher.DispatchTick();
        t.Check("events_duplicate_registration_runs_twice", calls == 2, "calls=" + calls);

        first.Dispose();
        EventDispatcher.DispatchTick();
        t.Check("events_disposing_one_duplicate_leaves_one", calls == 3, "calls=" + calls);

        second.Dispose();
        EventDispatcher.DispatchTick();
        t.Check("events_disposing_both_stops_dispatch", calls == 3, "calls=" + calls);
    }

    private static void IdempotentDispose(TestRunner t)
    {
        EventDispatcher.ResetForTests();
        var logger = new RecordingLogger();
        var session = Ready("Mod.Dispose", logger, out var api);
        var calls = 0;
        var token = api.OnTick(_ => calls++);
        api.Commit();
        EventDispatcher.CommitSession(session, "1.6.15.0", "1.1.0");

        EventDispatcher.DispatchTick();
        var threw = false;
        try
        {
            token.Dispose();
            token.Dispose();
            token.Dispose();
        }
        catch (Exception)
        {
            threw = true;
        }

        EventDispatcher.DispatchTick();
        t.Check("events_dispose_is_idempotent", !threw && calls == 1, "threw=" + threw + " calls=" + calls);
        t.Check("events_disposed_not_counted", EventDiagnostics.Capture().SubscriptionCount == 0,
            EventDiagnostics.Capture().SubscriptionCount.ToString());
    }

    private static void SnapshotSemantics(TestRunner t)
    {
        EventDispatcher.ResetForTests();
        var logger = new RecordingLogger();
        var session = Ready("Mod.Snapshot", logger, out var api);
        var removedCalls = 0;
        var lateCalls = 0;
        var added = false;
        IDisposable? removed = null;
        removed = api.OnTick(_ => removedCalls++);
        var late = api.OnTick(_ => lateCalls++);
        // Remove `late` and add a new subscription from inside the first dispatch.
        api.OnTick(args =>
        {
            if (args.TickCount != 1)
            {
                return;
            }

            late.Dispose();
            if (!added)
            {
                added = true;
                api.OnTick(_ => lateCalls++);
            }
        });
        api.Commit();
        EventDispatcher.CommitSession(session, "1.6.15.0", "1.1.0");

        EventDispatcher.DispatchTick();
        t.Check("events_snapshot_current_dispatch_uses_frozen_set", lateCalls == 1 && removedCalls == 1,
            "lateCalls=" + lateCalls + " removedCalls=" + removedCalls);

        EventDispatcher.DispatchTick();
        t.Check("events_snapshot_changes_apply_next_dispatch", lateCalls == 2 && added,
            "lateCalls=" + lateCalls + " added=" + added);

        removed!.Dispose();
        EventDispatcher.DispatchTick();
        t.Check("events_snapshot_removal_applies_next_dispatch", removedCalls == 2,
            "removedCalls=" + removedCalls);
    }

    private static void FaultIsolation(TestRunner t)
    {
        EventDispatcher.ResetForTests();
        var logger = new RecordingLogger();
        var session = Ready("Mod.Fault", logger, out var api);
        var healthy = 0;
        api.OnTick(_ => throw new InvalidOperationException("fixture fault"));
        api.OnTick(_ => healthy++);
        api.Commit();
        EventDispatcher.CommitSession(session, "1.6.15.0", "1.1.0");

        EventDispatcher.DispatchTick();
        EventDispatcher.DispatchTick();

        var snapshot = EventDiagnostics.Capture();
        t.Check("events_fault_does_not_block_other_handlers", healthy == 2, "healthy=" + healthy);
        t.Check("events_faults_counted", snapshot.HandlerFaults == 2, snapshot.HandlerFaults.ToString());
        t.Check("events_faults_do_not_reach_game_loop", snapshot.BridgeFaults == 0,
            snapshot.BridgeFaults.ToString());
    }

    private static void BoundedFaultLogging(TestRunner t)
    {
        EventDispatcher.ResetForTests();
        var logger = new RecordingLogger();
        var session = Ready("Mod.Log", logger, out var api);
        api.OnTick(_ => throw new InvalidOperationException("fixture fault"));
        api.Commit();
        EventDispatcher.CommitSession(session, "1.6.15.0", "1.1.0");

        for (var i = 0; i < 100; i++)
        {
            EventDispatcher.DispatchTick();
        }

        var written = logger.ErrorCount + logger.ErrorWithExceptionCount;
        t.Check("events_repeated_fault_logging_is_bounded", written <= 6 && written >= 3,
            "log records=" + written + " (faults=100)");
        t.Check("events_first_fault_keeps_diagnosis",
            logger.LastError.IndexOf("fixture fault", StringComparison.Ordinal) >= 0 ||
            logger.LastException != null,
            logger.LastError.Length > 80 ? logger.LastError.Substring(0, 80) : logger.LastError);
    }

    private static void StagedRollback(TestRunner t)
    {
        EventDispatcher.ResetForTests();
        var session = EventDispatcher.BeginSession();
        var api = EventDispatcher.CreateModEvents("Mod.Rollback", new RecordingLogger(), session);
        var calls = 0;
        api.OnTick(_ => calls++);
        api.OnGameLaunched(_ => calls++);

        // Not committed: a subscription is invisible until the mod's Initialize returned.
        EventDispatcher.CommitSession(session, "1.6.15.0", "1.1.0");
        EventDispatcher.DispatchTick();
        EventDispatcher.DispatchGameLaunched();
        t.Check("events_staged_subscription_is_invisible", calls == 0 && EventDiagnostics.Capture().SubscriptionCount == 0,
            "calls=" + calls);

        // Rollback (the subscribe-then-throw path) must leave nothing behind either.
        api.Rollback();
        EventDispatcher.DispatchTick();
        t.Check("events_rollback_discards_subscription", calls == 0 && EventDiagnostics.Capture().SubscriptionCount == 0,
            "calls=" + calls);

        // And a registration after rollback must be inert.
        var token = api.OnTick(_ => calls++);
        token.Dispose();
        EventDispatcher.DispatchTick();
        t.Check("events_register_after_rollback_is_inert", calls == 0, "calls=" + calls);
    }

    private static void SessionAbort(TestRunner t)
    {
        EventDispatcher.ResetForTests();
        var session = EventDispatcher.BeginSession();
        var api = EventDispatcher.CreateModEvents("Mod.Abort", new RecordingLogger(), session);
        var calls = 0;
        api.OnTick(_ => calls++);
        api.Commit();
        EventDispatcher.CommitSession(session, "1.6.15.0", "1.1.0");

        EventDispatcher.DispatchTick();
        var before = calls;

        EventDispatcher.AbortSession(session);
        EventDispatcher.DispatchTick();

        var snapshot = EventDiagnostics.Capture();
        t.Check("events_abort_session_drops_subscriptions",
            before == 1 && calls == 1 && snapshot.SubscriptionCount == 0 && !snapshot.Ready,
            "calls=" + calls + " subscriptions=" + snapshot.SubscriptionCount + " ready=" + snapshot.Ready);
    }

    // ------------------------------------------------------------------ loader level

    private static void LoaderSuite(TestRunner t, string fixtureDir)
    {
        var root = Temp.NewDirectory("phase20-events");
        try
        {
            var a = Path.Combine(fixtureDir, "EventMod.A.dll");
            var z = Path.Combine(fixtureDir, "EventMod.Z.dll");
            var fault = Path.Combine(fixtureDir, "EventMod.Fault.dll");
            StagePackage(root, "EventMod.A", a, "EventFixture.Entry", "\"frameworkVersion\": \">=1.1.0\",");
            StagePackage(root, "EventMod.Z", z, "EventFixture.Entry", "\"frameworkVersion\": \">=1.1.0\",");
            StagePackage(root, "EventMod.Fault", fault, "EventFixture.Entry", "\"frameworkVersion\": \">=1.1.0\",");

            EventDispatcher.ResetForTests();
            var returned = Loader.Run(root);
            var report = Temp.ReadText(Path.Combine(root, "loader-report.txt"));

            t.Check("events_loader_session_ready",
                returned == null && report.IndexOf("events=ready", StringComparison.Ordinal) >= 0,
                returned ?? "(null)");

            // A healthy A (3 subscriptions: 1 launch + 2 duplicate ticks), a healthy Z (3) and a
            // mod that subscribed then threw (0 - rolled back).
            var afterLoad = EventDiagnostics.Capture();
            t.Check("events_loader_counts_subscriptions", afterLoad.SubscriptionCount == 6,
                afterLoad.SubscriptionCount.ToString());
            t.Check("events_failed_mod_left_no_subscription",
                afterLoad.ModCount == 2, "mods with subscriptions=" + afterLoad.ModCount);

            EventDispatcher.DispatchGameLaunched();
            EventDispatcher.DispatchTick();
            EventDispatcher.DispatchTick();

            var aState = ReadState(root, "EventMod.A");
            var zState = ReadState(root, "EventMod.Z");
            var faultState = ReadState(root, "EventMod.Fault");

            t.Check("events_loader_a_launched_once", Field(aState, "launched_calls") == "1", aState);
            t.Check("events_loader_a_duplicate_registration",
                Field(aState, "tick_calls") == "4", "tick_calls=" + Field(aState, "tick_calls"));
            t.Check("events_loader_a_first_tick_is_one", Field(aState, "first_tick") == "1", aState);
            t.Check("events_loader_z_healthy_survived_fault",
                Field(zState, "tick_calls") == "2", "tick_calls=" + Field(zState, "tick_calls"));
            t.Check("events_loader_z_faults_counted",
                Field(zState, "fault_attempts") == "2", Field(zState, "fault_attempts"));
            t.Check("events_loader_failed_mod_never_ran",
                Field(faultState, "tick_calls") == "0" && Field(faultState, "launched_calls") == "0" &&
                Field(faultState, "subscribed_before_throw") == "true",
                faultState);

            var diag = EventDiagnostics.Capture();
            t.Check("events_loader_order_across_mods", diag.FirstTickOrder == "EventMod.A,EventMod.Z",
                diag.FirstTickOrder);

            // A second run of the same root must not re-initialize or duplicate dispatch.
            var second = Loader.Run(root);
            var secondReport = Temp.ReadText(Path.Combine(root, "loader-report.txt"));
            EventDispatcher.DispatchTick();
            var aState2 = ReadState(root, "EventMod.A");
            t.Check("events_loader_repeat_run_is_idempotent",
                second == null &&
                secondReport.IndexOf("result=already-initialized", StringComparison.Ordinal) >= 0 &&
                Field(aState2, "initialize_calls") == "1",
                "initialize_calls=" + Field(aState2, "initialize_calls"));
            t.Check("events_loader_repeat_run_no_duplicate_dispatch",
                Field(aState2, "tick_calls") == "6",
                "tick_calls=" + Field(aState2, "tick_calls") + " (expected 4 + 2)");
        }
        finally
        {
            // Temp directories are removed by the suite's Temp.Cleanup().
        }
    }

    private static void LegacyBinaryCompatibility(TestRunner t, string legacyModDir)
    {
        var root = Temp.NewDirectory("phase20-legacy");
        var dll = Path.Combine(legacyModDir, "LegacyFixtureMod.dll");
        if (!File.Exists(dll))
        {
            t.Check("phase20_legacy_fixture_present", false, "missing " + dll);
            return;
        }

        StagePackage(root, "LegacyFixture", dll, "LegacyFixtureMod.Entry",
            "\"frameworkVersion\": \"^1.0.0\",");
        EventDispatcher.ResetForTests();
        var returned = Loader.Run(root);
        var report = Temp.ReadText(Path.Combine(root, "loader-report.txt"));
        var marker = Temp.ReadText(Path.Combine(root, "LegacyFixture", "legacy-ran.txt"));

        t.Check("phase20_legacy_mod_initialized",
            returned == null && report.IndexOf("initialized=LegacyFixture", StringComparison.Ordinal) >= 0 &&
            marker.IndexOf("framework=1.1.0", StringComparison.Ordinal) >= 0,
            returned ?? marker);
        t.Check("phase20_legacy_mod_gets_no_events",
            EventDiagnostics.Capture().SubscriptionCount == 0,
            EventDiagnostics.Capture().SubscriptionCount.ToString());
    }

    // ------------------------------------------------------------------ helpers

    private static int Ready(string modId, RecordingLogger logger, out ModEventsApi api)
    {
        var session = EventDispatcher.BeginSession();
        api = EventDispatcher.CreateModEvents(modId, logger, session);
        EventDispatcher.CommitSession(session, "1.6.15.0", "1.1.0");
        return session;
    }

    private static void StagePackage(string root, string id, string dllPath, string entryType, string extraJson)
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
            "  \"entryType\": \"" + entryType + "\",\n" +
            "  \"gameVersion\": \"*\",\n" +
            "  " + extraJson + "\n" +
            "  \"description\": \"phase 2.0 fixture\"\n" +
            "}\n";
        // Keep the JSON valid when the caller supplies no extra member.
        manifest = manifest.Replace("  ,\n", "");
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

    private sealed class RecordingLogger : IModLogger
    {
        internal int InfoCount;
        internal int WarnCount;
        internal int ErrorCount;
        internal int ErrorWithExceptionCount;
        internal string LastError = "";
        internal Exception? LastException;

        public void Info(string message)
        {
            InfoCount++;
        }

        public void Warn(string message)
        {
            WarnCount++;
        }

        public void Error(string message)
        {
            ErrorCount++;
            LastError = message;
        }

        public void Error(string message, Exception exception)
        {
            ErrorWithExceptionCount++;
            LastError = message;
            LastException = exception;
        }
    }
}
