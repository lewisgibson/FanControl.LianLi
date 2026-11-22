using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Logging;

namespace FanControl.LianLi.Worker;

/// <summary>
/// Drives one controller on a thread of its own: applies pending targets and polls RPM once a
/// second, and at once when woken. One thread per controller, because a device call can take
/// seconds on a controller that came back from sleep wedged (a bounded reopen, a transfer running
/// out its timeout) and on a shared thread every healthy controller would wait out that stall too.
/// Stopping is split in two so a caller stopping several loops pays one deadline, not one each:
/// <see cref="SignalStop"/> tells the loop to finish, <see cref="WaitForStop"/> waits a bounded
/// time. The loop's own thread disposes the controller as its last act, so that wait covers the
/// disposal too - closing a device can itself take seconds after a wake - and a caller that runs out
/// of time stops waiting while the thread finishes the job.
/// </summary>
internal sealed class ControllerLoop : IDisposable {
    // How long Dispose waits for the loop on its own.
    private const int DisposeTimeoutMs = 2000;

    // How many polls in a row must fail before the controller's readings are forgotten, so FanControl
    // shows nothing rather than the last speeds and temperature it was given: about half a minute at
    // one poll a second, the same count after which a wireless device that is not heard reads nothing
    // (WirelessDevice.MaximumMissedReads). A blip, or the reopen after a wake, stays well inside it.
    internal const int ForgetReadingsAfterFailedPolls = 30;

    private readonly int _index;
    private readonly IFanDevice _controller;
    private readonly ILog _log;
    private readonly int _tickIntervalMs;
    private readonly Thread _thread;

    // Set to end the wait between ticks early: by SignalStop to stop, by Wake to tick now. An
    // auto-reset event, so a wake that lands while a tick runs is kept for the next wait.
    private readonly AutoResetEvent _signal = new AutoResetEvent(false);

    // Guards setting and disposing _signal: the loop's thread disposes it as its last act, which can
    // land between a caller seeing the loop still running and that caller setting the event.
    private readonly object _signalGate = new object();
    private readonly object _tickGate = new object();

    // Under _tickGate.
    private readonly FaultLog _applyFaults;
    private readonly FaultLog _pollFaults;

    private volatile bool _stop;
    private bool _started;
    private int _stopSignalled;
    private int _waited;
    private bool _signalDisposed;

    public ControllerLoop(int index, IFanDevice controller, ILog log, int tickIntervalMs) {
        _index = index;
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        if (tickIntervalMs <= 0) {
            throw new ArgumentOutOfRangeException(nameof(tickIntervalMs));
        }

        _tickIntervalMs = tickIntervalMs;
        _applyFaults = new FaultLog("apply", index, log);
        _pollFaults = new FaultLog("poll", index, log);
        _thread = new Thread(Loop) { IsBackground = true, Name = "LianLiController" + index.ToString(CultureInfo.InvariantCulture) };
    }

    /// <summary>Start the loop's thread.</summary>
    public void Start() {
        _started = true;
        _thread.Start();
    }

    /// <summary>
    /// Tick now instead of at the next interval. Safe from any thread at any time; a loop that is
    /// stopping or has stopped ignores it.
    /// </summary>
    public void Wake() {
        if (Volatile.Read(ref _stopSignalled) == 0) {
            Signal();
        }
    }

    /// <summary>Apply pending targets, then poll RPM, isolating a fault in either and logging it.</summary>
    public void Tick() {
        lock (_tickGate) {
            try {
                _controller.ApplyPending();
                _applyFaults.Recovered();
            }
#pragma warning disable CA1031 // resilience: a failed transfer on one device must not stall the others
            catch (Exception ex) {
                _applyFaults.Failed(ex.Message);
            }
#pragma warning restore CA1031

            try {
                _controller.PollRpm();
                _pollFaults.Recovered();
            }
#pragma warning disable CA1031 // resilience: see above
            catch (Exception ex) {
                if (_pollFaults.Failed(ex.Message) == ForgetReadingsAfterFailedPolls) {
                    _controller.ForgetReadings();
                }
            }
#pragma warning restore CA1031
        }
    }

    /// <summary>Tell the loop to finish after its current tick. Never waits on a device call; idempotent.</summary>
    public void SignalStop() {
        if (Interlocked.Exchange(ref _stopSignalled, 1) == 1) {
            return;
        }

        _stop = true;
        Signal();
    }

    /// <summary>
    /// Wait up to <paramref name="timeoutMilliseconds"/> for the loop to finish and release the
    /// controller; if it has not by then, it still will when its device call returns. Call after
    /// <see cref="SignalStop"/>; later calls do nothing.
    /// </summary>
    public void WaitForStop(int timeoutMilliseconds) {
        if (!Join(timeoutMilliseconds)) {
            LogStillRunning();
        }
    }

    /// <summary>
    /// Wait up to <paramref name="timeoutMilliseconds"/> (<see cref="Timeout.Infinite"/> for as long
    /// as it takes) for the loop to finish and release the controller; true when it has. A loop that
    /// was never started releases the controller here instead. Only the first call waits.
    /// </summary>
    public bool Join(int timeoutMilliseconds) {
        if (Interlocked.Exchange(ref _waited, 1) == 1) {
            return HasStopped;
        }

        if (!_started) {
            Release();
            return true;
        }

        return _thread.Join(timeoutMilliseconds);
    }

    /// <summary>Whether the loop has finished (or never ran).</summary>
    public bool HasStopped => !_started || !_thread.IsAlive;

    /// <summary>Log that the loop is still in a device call; it releases the controller when the call returns.</summary>
    public void LogStillRunning()
        => _log.Write(string.Format(
            CultureInfo.InvariantCulture,
            "C{0} still inside a device call at shutdown; it is released when the call returns",
            _index));

    /// <summary>Stop the loop and wait for it, as <see cref="SignalStop"/> then <see cref="WaitForStop"/>.</summary>
    public void Dispose() {
        SignalStop();
        WaitForStop(DisposeTimeoutMs);
    }

    private void Signal() {
        lock (_signalGate) {
            if (!_signalDisposed) {
                _ = _signal.Set();
            }
        }
    }

    private void Release() {
        try {
            _controller.Dispose();
        }
#pragma warning disable CA1031 // resilience: this runs on a thread of the plugin's own, where an escaping exception ends FanControl's process
        catch (Exception ex) {
            _log.Write(string.Format(CultureInfo.InvariantCulture, "release err C{0}: {1}", _index, ex.Message));
        }
#pragma warning restore CA1031

        lock (_signalGate) {
            _signalDisposed = true;
            _signal.Dispose();
        }
    }

    private void Loop() {
        try {
            while (!_stop) {
                Tick();
                if (_stop) {
                    break;
                }

                _ = _signal.WaitOne(_tickIntervalMs);
            }
        } finally {
            Release();
        }
    }

    // A controller that stops answering fails every tick, once a second, until it comes back. Each
    // different failure is logged the first time it happens in a run of failures, and the end of the
    // run once with how many ticks failed, so a controller that is gone for an hour writes a few
    // lines, not one a second.
    private sealed class FaultLog {
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        private readonly string _what;
        private readonly int _index;
        private readonly ILog _log;
        private int _failures;

        public FaultLog(string what, int index, ILog log) {
            _what = what;
            _index = index;
            _log = log;
        }

        // The number of ticks that have failed in this run of failures, this one included.
        public int Failed(string message) {
            _failures++;
            if (_seen.Add(message)) {
                _log.Write(string.Format(CultureInfo.InvariantCulture, "{0} err C{1}: {2}", _what, _index, message));
            }

            return _failures;
        }

        public void Recovered() {
            if (_failures == 0) {
                return;
            }

            _log.Write(string.Format(
                CultureInfo.InvariantCulture, "{0} C{1} recovered after {2} failed tick(s)", _what, _index, _failures));
            _failures = 0;
            _seen.Clear();
        }
    }
}
