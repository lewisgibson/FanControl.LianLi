using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Logging;

namespace FanControl.LianLi.Worker;

/// <summary>
/// Owns the controller set and drives their I/O. <see cref="Tick"/> applies
/// pending targets and polls RPM for every controller; it is invoked both by
/// the host's <c>IPlugin2.Update()</c> hook and by a background thread, so the
/// 15s keepalive still fires even if the host stops pumping. All ticks are
/// serialized, so the two callers never touch a transport concurrently. Each
/// per-controller call is isolated so one failing device cannot stall the rest.
/// </summary>
internal sealed class KeepAliveWorker : IDisposable {
    private const int TickIntervalMs = 1000;
    private const int JoinTimeoutMs = 2000;

    private readonly IReadOnlyList<FanController> _controllers;
    private readonly ILog _log;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stopSignal = new ManualResetEventSlim(false);
    private readonly object _tickGate = new object();

    private volatile bool _stop;
    private bool _started;
    private bool _disposed;

    public KeepAliveWorker(IReadOnlyList<FanController> controllers, ILog log) {
        _controllers = controllers ?? throw new ArgumentNullException(nameof(controllers));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _thread = new Thread(Loop) { IsBackground = true, Name = "LianLiHidWorker" };
    }

    /// <summary>Start the background keepalive thread (no-op when there are no controllers).</summary>
    public void Start() {
        if (_controllers.Count == 0) {
            return;
        }

        _started = true;
        _thread.Start();
    }

    /// <summary>
    /// Apply pending targets and poll RPM for every controller. Blocks until the tick
    /// completes; used by the background keepalive loop.
    /// </summary>
    public void Tick() {
        lock (_tickGate) {
            TickCore();
        }
    }

    /// <summary>
    /// Non-blocking variant of <see cref="Tick"/>: skips the tick if the background
    /// thread currently holds <c>_tickGate</c>. Used by the host <c>Update()</c> hook so
    /// a blocked background tick (e.g. a slow HID read after hibernate) does not stall
    /// the FanControl UI thread.
    /// </summary>
    public void TryTick() {
        if (!Monitor.TryEnter(_tickGate)) {
            return;
        }

        try {
            TickCore();
        }
        finally {
            Monitor.Exit(_tickGate);
        }
    }

    public void Dispose() {
        lock (_tickGate) {
            if (_disposed) {
                return;
            }

            _disposed = true;
        }

        _stop = true;
        _stopSignal.Set();

        bool threadExited = !_started || _thread.Join(JoinTimeoutMs);

        // Only dispose controllers (and the stop signal) once the loop thread has
        // confirmed to have exited. If the join timed out the thread may still hold
        // _tickGate or reference the signal, so leave those to the finalizer rather
        // than risk a use-after-dispose on the worker thread.
        if (threadExited) {
            lock (_tickGate) {
                for (int i = 0; i < _controllers.Count; i++) {
                    _controllers[i].Dispose();
                }
            }

            _stopSignal.Dispose();
        }
    }

    private void TickCore() {
        for (int i = 0; i < _controllers.Count; i++) {
            FanController controller = _controllers[i];

            try {
                controller.ApplyPending();
            }
#pragma warning disable CA1031 // resilience: a failed transfer on one device must not stall the others
            catch (Exception ex) {
                _log.Write(string.Format(CultureInfo.InvariantCulture, "apply err C{0}: {1}", i, ex.Message));
            }
#pragma warning restore CA1031

            try {
                controller.PollRpm();
            }
#pragma warning disable CA1031 // resilience: see above
            catch (Exception ex) {
                _log.Write(string.Format(CultureInfo.InvariantCulture, "poll err C{0}: {1}", i, ex.Message));
            }
#pragma warning restore CA1031
        }
    }

    private void Loop() {
        while (!_stop) {
            Tick();
            if (_stop) {
                break;
            }

            _stopSignal.Wait(TickIntervalMs);
        }
    }
}
