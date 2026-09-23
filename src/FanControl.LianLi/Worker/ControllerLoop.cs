using System;
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
/// of time simply stops waiting while the thread finishes the job.
/// </summary>
internal sealed class ControllerLoop : IDisposable {
    // How long Dispose waits for the loop on its own.
    private const int DisposeTimeoutMs = 2000;

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
        _thread = new Thread(Loop) { IsBackground = true, Name = "LianLiController" + index.ToString(CultureInfo.InvariantCulture) };
    }

    /// <summary>Start the loop's thread.</summary>
    public void Start() {
        _started = true;
        _thread.Start();
    }

    /// <summary>
    /// Tick now rather than at the next interval. Safe from any thread at any time; a loop that is
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
            }
#pragma warning disable CA1031 // resilience: a failed transfer on one device must not stall the others
            catch (Exception ex) {
                _log.Write(string.Format(CultureInfo.InvariantCulture, "apply err C{0}: {1}", _index, ex.Message));
            }
#pragma warning restore CA1031

            try {
                _controller.PollRpm();
            }
#pragma warning disable CA1031 // resilience: see above
            catch (Exception ex) {
                _log.Write(string.Format(CultureInfo.InvariantCulture, "poll err C{0}: {1}", _index, ex.Message));
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
}
