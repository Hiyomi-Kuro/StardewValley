using System;
using System.Globalization;
using System.IO;
using System.Text;
using ModFramework.Runtime;

namespace P20.Acceptance.Beta;

/// <summary>
/// Phase 2.0 subscriber B - the deliberately awkward one.
///
/// Registration order inside this mod is: faulting handler, healthy handler, duplicate A,
/// duplicate B, reporter. That order is the point:
///
///   * the faulting handler throws on the first three ticks only. Because it is registered
///     <b>before</b> the healthy handler, a broken isolation would be visible as
///     <c>healthy_ticks &lt; tick</c>;
///   * the same delegate is registered twice (two independent subscriptions). Duplicate A is
///     disposed from inside the tick 1 callback, duplicate B from inside tick 2. With frozen
///     snapshot semantics the counts must be 2 / 1 / 0 for ticks 1 / 2 / 3;
///   * a subscription created during the tick 3 callback must not run in tick 3 and must run
///     from tick 4;
///   * a second Dispose on an already disposed token is a no-op.
///
/// Writes are bounded: one overwritten report file at ticks 1, 2, 3, 60 and every 600 ticks.
/// </summary>
public sealed class BetaEntry : IModEntry
{
    private const long SlowSnapshotInterval = 600;

    private IModEvents? _events;
    private string _modDirectory = "";
    private IModLogger? _log;

    private int _faultAttempts;
    private long _healthyTicks;
    private long _duplicateCallsThisTick;
    private long _duplicateCallsTotal;
    private long _lateCalls;
    private long _lateCallsThisTick;
    private long _duplicateCallsTick1 = -1;
    private long _duplicateCallsTick2 = -1;
    private long _duplicateCallsTick3 = -1;
    private long _lateCallsTick3 = -1;
    private int _doubleDispose;
    private bool _lateRegistered;

    private IDisposable? _duplicateA;
    private IDisposable? _duplicateB;

    public void Initialize(IModContext context)
    {
        _events = context.Events;
        _modDirectory = context.ModDirectory;
        _log = context.Log;

        // Registration order matters for the isolation proof: the throwing handler comes first.
        context.Events.OnTick(Faulting);
        context.Events.OnTick(Healthy);
        _duplicateA = context.Events.OnTick(Duplicate);
        _duplicateB = context.Events.OnTick(Duplicate);
        context.Events.OnTick(Reporter);

        context.Log.Info("subscribed: faulting, healthy, duplicate x2, reporter (Tick)");
        WriteReport("initialize", 0);
    }

    private void Faulting(TickEventArgs args)
    {
        if (args.TickCount > 3)
        {
            return;
        }

        _faultAttempts++;
        throw new InvalidOperationException(
            "phase2.0 bounded fault injection at tick " + args.TickCount.ToString(CultureInfo.InvariantCulture));
    }

    private void Healthy(TickEventArgs args)
    {
        _healthyTicks++;
    }

    private void Duplicate(TickEventArgs args)
    {
        _duplicateCallsThisTick++;
        _duplicateCallsTotal++;

        if (args.TickCount == 1 && _duplicateA != null)
        {
            _duplicateA.Dispose();
            _duplicateA.Dispose(); // second dispose must be a no-op
            _duplicateA = null;
            _doubleDispose++;
        }
        else if (args.TickCount == 2 && _duplicateB != null)
        {
            _duplicateB.Dispose();
            _duplicateB = null;
        }
    }

    private void Late(TickEventArgs args)
    {
        _lateCalls++;
        _lateCallsThisTick++;
    }

    private void Reporter(TickEventArgs args)
    {
        var tick = args.TickCount;

        if (tick == 1)
        {
            _duplicateCallsTick1 = _duplicateCallsThisTick;
        }
        else if (tick == 2)
        {
            _duplicateCallsTick2 = _duplicateCallsThisTick;
        }
        else if (tick == 3)
        {
            _duplicateCallsTick3 = _duplicateCallsThisTick;
            _lateCallsTick3 = _lateCallsThisTick;

            // Snapshot semantics: this subscription must be invisible to the current dispatch.
            if (_lateRegistered || _events == null)
            {
                return;
            }

            _lateRegistered = true;
            _events.OnTick(Late);
            _log?.Info("registered a late Tick subscription from inside tick 3");
        }

        _duplicateCallsThisTick = 0;
        _lateCallsThisTick = 0;

        if (ShouldSnapshot(tick))
        {
            WriteReport("on-tick", tick);
        }
    }

    private bool ShouldSnapshot(long tick)
    {
        if (tick <= 3 || tick == 60)
        {
            return true;
        }

        return tick % SlowSnapshotInterval == 0;
    }

    private void WriteReport(string reason, long tick)
    {
        var diagnostics = EventDiagnostics.Capture();
        var text = new StringBuilder();
        text.Append("phase2.0-beta-report").Append('\n');
        text.Append("mod=P20.Acceptance.Beta").Append('\n');
        text.Append("reason=").Append(reason).Append('\n');
        text.Append("tick=").Append(tick.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("fault_attempts=").Append(_faultAttempts.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("healthy_ticks=").Append(_healthyTicks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("duplicate_calls_total=").Append(_duplicateCallsTotal.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("duplicate_calls_tick1=").Append(_duplicateCallsTick1.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("duplicate_calls_tick2=").Append(_duplicateCallsTick2.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("duplicate_calls_tick3=").Append(_duplicateCallsTick3.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("late_calls=").Append(_lateCalls.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("late_calls_tick3=").Append(_lateCallsTick3.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("late_registered=").Append(_lateRegistered ? "true" : "false").Append('\n');
        text.Append("double_dispose=").Append(_doubleDispose.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("write_thread=").Append(Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("diagnostics_ticks=").Append(diagnostics.TickCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("diagnostics_subscriptions=").Append(diagnostics.SubscriptionCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("diagnostics_handler_faults=").Append(diagnostics.HandlerFaults.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("diagnostics_log_faults=").Append(diagnostics.LogFaults.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("diagnostics_bridge_faults=").Append(diagnostics.BridgeFaults.ToString(CultureInfo.InvariantCulture)).Append('\n');
        WriteText("phase2.0-beta.txt", text.ToString());
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
            // A probe must never break the game loop.
        }
    }
}
