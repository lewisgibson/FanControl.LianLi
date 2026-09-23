using System;
using System.Collections.Generic;
using System.Threading;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Logging;

namespace FanControl.LianLi.Plugin;

/// <summary>
/// Builds every controller a scan found at once, under one deadline. FanControl calls Initialize
/// holding the lock that serialises all of its devices, so the time spent here is time the whole
/// application stands still; each build is individually bounded, but in turn they would add up - a
/// wake that leaves three controllers wedged would cost three timeouts. Side by side they cost one.
/// A build still running at the deadline is abandoned to its thread, which hands what it finally
/// produces to a callback instead of leaking it.
/// </summary>
internal static class ControllerBuilder {
    /// <summary>
    /// Run every build on its own thread and wait at most <paramref name="deadlineMilliseconds"/>
    /// for all of them. Returns one outcome per build, in order. A build that ends after the deadline
    /// reaches <paramref name="onLateController"/> (which then owns the controller) or
    /// <paramref name="onLateFailure"/>, on its own thread, with its index.
    /// </summary>
    public static BuildOutcome[] Run(
        IReadOnlyList<Func<IFanDevice>> builds,
        int deadlineMilliseconds,
        Action<int, IFanDevice> onLateController,
        Action<int, Exception> onLateFailure,
        ILog log) {
        if (builds is null) {
            throw new ArgumentNullException(nameof(builds));
        }

        if (deadlineMilliseconds < 0) {
            throw new ArgumentOutOfRangeException(nameof(deadlineMilliseconds));
        }

        if (onLateController is null) {
            throw new ArgumentNullException(nameof(onLateController));
        }

        if (onLateFailure is null) {
            throw new ArgumentNullException(nameof(onLateFailure));
        }

        if (log is null) {
            throw new ArgumentNullException(nameof(log));
        }

        var slots = new Slot[builds.Count];
        var threads = new Thread[builds.Count];
        for (int i = 0; i < builds.Count; i++) {
            var slot = new Slot(
                i, builds[i] ?? throw new ArgumentException("A build is null.", nameof(builds)), onLateController, onLateFailure, log);
            slots[i] = slot;
            threads[i] = new Thread(slot.Run) { IsBackground = true, Name = "LianLiControllerBuild" };
        }

        // TickCount rather than a clock: this is the host's own wall time being rationed, and the
        // subtraction is wrap-safe in unchecked arithmetic.
        int started = Environment.TickCount;
        foreach (Thread thread in threads) {
            thread.Start();
        }

        foreach (Thread thread in threads) {
            int elapsed = unchecked(Environment.TickCount - started);
            _ = thread.Join(Math.Max(0, deadlineMilliseconds - elapsed));
        }

        var outcomes = new BuildOutcome[slots.Length];
        for (int i = 0; i < slots.Length; i++) {
            outcomes[i] = slots[i].Take();
        }

        return outcomes;
    }

    // One build and the handoff of its result: whichever of the build thread and the waiting
    // caller gets to the lock second decides where the result goes.
    private sealed class Slot {
        private readonly object _gate = new object();
        private readonly int _index;
        private readonly Func<IFanDevice> _build;
        private readonly Action<int, IFanDevice> _onLateController;
        private readonly Action<int, Exception> _onLateFailure;
        private readonly ILog _log;
        private BuildOutcome? _outcome;
        private bool _abandoned;

        public Slot(
            int index,
            Func<IFanDevice> build,
            Action<int, IFanDevice> onLateController,
            Action<int, Exception> onLateFailure,
            ILog log) {
            _index = index;
            _build = build;
            _onLateController = onLateController;
            _onLateFailure = onLateFailure;
            _log = log;
        }

        public void Run() {
            BuildOutcome outcome;
            try {
                outcome = BuildOutcome.Built(_build());
            }
#pragma warning disable CA1031 // captured and handed to the caller (or the late callback); an exception escaping this thread would end FanControl's process
            catch (Exception ex) {
                outcome = BuildOutcome.Failed(ex);
            }
#pragma warning restore CA1031

            lock (_gate) {
                if (!_abandoned) {
                    _outcome = outcome;
                    return;
                }
            }

            try {
                outcome.Deliver(_index, _onLateController, _onLateFailure);
            }
#pragma warning disable CA1031 // host seam: an exception escaping this thread would end FanControl's process
            catch (Exception ex) {
                _log.Write("controller build " + _index + ": handling its late result failed: " + ex.Message);
            }
#pragma warning restore CA1031
        }

        public BuildOutcome Take() {
            lock (_gate) {
                _abandoned = true;
                return _outcome ?? BuildOutcome.Late();
            }
        }
    }
}
