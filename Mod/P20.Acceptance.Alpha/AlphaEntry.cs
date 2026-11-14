using System;
using System.Globalization;
using System.IO;
using System.Text;
using ModFramework.Runtime;

namespace P20.Acceptance.Alpha;

/// <summary>
/// Phase 2.0 subscriber A.
///
/// Proves, from inside the game process and without touching game types:
///   * it is initialized (and therefore subscribed) before any event is dispatched;
///   * <c>GameLaunched</c> is dispatched exactly once and before the first <c>Tick</c>;
///   * the dispatch thread is a stable, reported thread id (not an assumption);
///   * the tick counter grows between two bounded snapshots;
///   * the dispatch order is Alpha -&gt; Beta even though Beta is initialized first (the manifest
///     makes Alpha depend on Beta), i.e. order comes from the ordinal mod id.
///
/// Writes are deliberately bounded: one overwritten report file, at most a handful of writes.
/// Nothing is written per tick.
/// </summary>
public sealed class AlphaEntry : IModEntry
{
    private const long SlowSnapshotInterval = 600;

    private string _modDirectory = "";
    private string _gameVersion = "unknown";
    private string _frameworkVersion = "unknown";

    private int _launchedCalls;
    private long _tickCalls;
    private long _launchedAtTickCount = -1;
    private int _launchedThread;
    private int _firstTickThread;
    private long _lastSnapshotTick = -1;

    public void Initialize(IModContext context)
    {
        _modDirectory = context.ModDirectory;
        _gameVersion = context.GameVersion;
        _frameworkVersion = context.FrameworkVersion;

        context.Events.OnGameLaunched(OnGameLaunched);
        context.Events.OnTick(OnTick);

        context.Log.Info("subscribed: GameLaunched + Tick");
        WriteSnapshot("initialize-before-first-event", 0);
    }

    private void OnGameLaunched(GameLaunchedEventArgs args)
    {
        _launchedCalls++;
        _launchedThread = args.ThreadId;
        // TickCount is read from the dispatcher, not from the arguments: it proves that the
        // launch was observed while no tick had been dispatched yet.
        var snapshot = EventDiagnostics.Capture();
        _launchedAtTickCount = snapshot.TickCount;
        WriteSnapshot("on-game-launched", 0);
    }

    private void OnTick(TickEventArgs args)
    {
        _tickCalls++;
        if (_firstTickThread == 0)
        {
            _firstTickThread = args.ThreadId;
        }

        if (!ShouldSnapshot(args.TickCount))
        {
            return;
        }

        _lastSnapshotTick = args.TickCount;
        WriteSnapshot("on-tick", args.TickCount);
    }

    private bool ShouldSnapshot(long tick)
    {
        if (tick <= 3 || tick == 60)
        {
            return true;
        }

        return tick % SlowSnapshotInterval == 0;
    }

    private void WriteSnapshot(string reason, long tick)
    {
        var diagnostics = EventDiagnostics.Capture();
        var text = new StringBuilder();
        text.Append("phase2.0-alpha-report").Append('\n');
        text.Append("mod=P20.Acceptance.Alpha").Append('\n');
        text.Append("reason=").Append(reason).Append('\n');
        text.Append("tick_argument=").Append(tick.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("observed_ticks=").Append(_tickCalls.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("observed_launches=").Append(_launchedCalls.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("launched_at_tick_count=").Append(_launchedAtTickCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("launched_thread=").Append(_launchedThread.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("first_tick_thread=").Append(_firstTickThread.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("last_snapshot_tick=").Append(_lastSnapshotTick.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("write_thread=").Append(Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("game_version=").Append(_gameVersion).Append('\n');
        text.Append("framework_version=").Append(_frameworkVersion).Append('\n');
        text.Append("ready=").Append(diagnostics.Ready ? "true" : "false").Append('\n');
        text.Append("session=").Append(diagnostics.SessionId.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("diagnostics_launches=").Append(diagnostics.GameLaunchedCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("diagnostics_ticks=").Append(diagnostics.TickCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("diagnostics_dispatch=").Append(diagnostics.DispatchCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("diagnostics_subscriptions=").Append(diagnostics.SubscriptionCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("diagnostics_mods=").Append(diagnostics.ModCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("diagnostics_handler_faults=").Append(diagnostics.HandlerFaults.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("diagnostics_log_faults=").Append(diagnostics.LogFaults.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("diagnostics_bridge_faults=").Append(diagnostics.BridgeFaults.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("loader_thread=").Append(diagnostics.LoaderThreadId.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("launch_thread=").Append(diagnostics.FirstGameLaunchedThreadId.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("tick_thread=").Append(diagnostics.FirstTickThreadId.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("launch_before_first_tick=").Append(diagnostics.GameLaunchedBeforeFirstTick ? "true" : "false").Append('\n');
        text.Append("first_launch_order=").Append(diagnostics.FirstGameLaunchedOrder).Append('\n');
        text.Append("first_tick_order=").Append(diagnostics.FirstTickOrder).Append('\n');
        WriteText("phase2.0-alpha.txt", text.ToString());
    }

    private void WriteText(string fileName, string text)
    {
        try
        {
            var directory = Path.Combine(_modDirectory, "probe-output");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, fileName), text);
        }
        catch (Exception)
        {
            // A probe must never break the game loop; the loader report and the device log remain.
        }
    }
}
