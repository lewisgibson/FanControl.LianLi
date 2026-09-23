using System;
using System.Collections.Generic;
using System.Threading;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Logging;

namespace FanControl.LianLi.Worker;

/// <summary>
/// Owns the controller set and drives all of its I/O, one <see cref="ControllerLoop"/> per
/// controller, each on its own thread, so a controller stalled in a device call delays nothing
/// but itself. Each ticks once a second, and at once when <see cref="Wake"/> says a target
/// changed. FanControl's own thread never ticks: the host calls the plugin's Update holding the
/// lock that serialises every device it drives, so a device call there, bounded or not, would stall
/// all of FanControl. <see cref="Dispose"/> waits a bounded time for all the loops together and
/// never longer: a loop still inside a device call keeps its controller and releases it itself
/// when the call returns.
/// </summary>
internal sealed class KeepAliveWorker : IDisposable {
    // The controllers' own cadence: RPM once a second, the keepalive re-assert every 15 s.
    private const int DefaultTickIntervalMs = 1000;

    // How long Dispose waits for every loop to finish, together. FanControl waits for Close.
    private const int JoinTimeoutMs = 2000;

    private readonly ControllerLoop[] _loops;
    private int _disposed;

    public KeepAliveWorker(IReadOnlyList<IFanDevice> controllers, ILog log)
        : this(controllers, log, DefaultTickIntervalMs) {
    }

    /// <summary>A worker ticking every <paramref name="tickIntervalMs"/>; a test lengthens it to see a wake alone.</summary>
    internal KeepAliveWorker(IReadOnlyList<IFanDevice> controllers, ILog log, int tickIntervalMs) {
        if (controllers is null) {
            throw new ArgumentNullException(nameof(controllers));
        }

        if (log is null) {
            throw new ArgumentNullException(nameof(log));
        }

        if (tickIntervalMs <= 0) {
            throw new ArgumentOutOfRangeException(nameof(tickIntervalMs));
        }

        _loops = new ControllerLoop[controllers.Count];
        for (int i = 0; i < controllers.Count; i++) {
            _loops[i] = new ControllerLoop(i, controllers[i], log, tickIntervalMs);
        }
    }

    /// <summary>Start every controller's loop.</summary>
    public void Start() {
        foreach (ControllerLoop loop in _loops) {
            loop.Start();
        }
    }

    /// <summary>Run one tick of every controller, in turn, on the calling thread; for tests.</summary>
    public void Tick() {
        foreach (ControllerLoop loop in _loops) {
            loop.Tick();
        }
    }

    /// <summary>
    /// Tick every controller now rather than at its next interval, because a target changed. Safe
    /// from any thread until <see cref="Dispose"/> is called; the caller must not wake a worker it
    /// has started disposing.
    /// </summary>
    public void Wake() {
        foreach (ControllerLoop loop in _loops) {
            loop.Wake();
        }
    }

    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) {
            return;
        }

        // Every loop is told first, so they all wind down at once, then each is waited for out of
        // one shared budget: a wedged controller costs the budget once, not once per controller.
        foreach (ControllerLoop loop in _loops) {
            loop.SignalStop();
        }

        // Waiting for them in turn happens on a thread of its own, so the caller's one bounded wait
        // covers them all without reading a clock to share a budget out.
        var joiner = new Thread(() => {
            foreach (ControllerLoop loop in _loops) {
                _ = loop.Join(Timeout.Infinite);
            }
        }) {
            IsBackground = true,
            Name = "LianLiWorkerStop",
        };
        joiner.Start();
        if (!joiner.Join(JoinTimeoutMs)) {
            foreach (ControllerLoop loop in _loops) {
                if (!loop.HasStopped) {
                    loop.LogStillRunning();
                }
            }
        }
    }
}
