using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using FanControl.LianLi.Logging;

namespace FanControl.LianLi.Transport;

/// <summary>
/// Every bounded device call goes through here, keyed by the device it reaches, so that a device that
/// stays wedged costs at most one stuck thread rather than one per retry. A call the bound gave up on
/// is abandoned to its thread (see <see cref="BoundedDeviceCall"/>), and the cancels it sends usually
/// unwind it within milliseconds - but a CreateFile or configuration manager call blocked inside the
/// kernel may never return, and the reopen backoffs, the stand-in rebuilds and the host's retried
/// Initialize keep trying that device for the life of the process. So while an earlier call to a
/// device has not returned, a new call that would start more work on it is refused before any thread
/// is created: it fails fast with an <see cref="IOException"/>, the way a transfer on a faulted handle
/// does, so every caller already handles it. The refusal is logged once per stuck episode rather than
/// on every attempt, and once the stuck call returns the device is reachable again, with a line
/// saying so if anything was refused meanwhile.
///
/// The key is the device path, compared without regard to case as Windows compares it. A transport
/// and the enumerator that opened it share one gate, so a reopen still stuck on a path also holds
/// back the next open of that path, and the reverse. The scan walks every device at once, so it has
/// a key of its own: a scan still out holds back only the next scan.
/// </summary>
internal sealed class DeviceCallGate {
    private readonly IDeviceCallRunner _runner;
    private readonly IDeviceCallClock _clock;
    private readonly ILog _log;

    // Guards _stuck and every PendingCall's fields: the caller registers an abandoned call here while
    // that call's own thread may be reporting that it returned. Both belong to the StuckCalls the gate
    // shares, so every gate over it sees the same stuck calls.
    private readonly object _lock;
    private readonly Dictionary<string, StuckDevice> _stuck;

    public DeviceCallGate(IDeviceCallRunner runner, IDeviceCallClock clock, ILog log)
        : this(runner, clock, log, new StuckCalls()) {
    }

    /// <summary>A gate over <paramref name="stuck"/>, the stuck calls it shares with other gates.</summary>
    internal DeviceCallGate(IDeviceCallRunner runner, IDeviceCallClock clock, ILog log, StuckCalls stuck) {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        if (stuck is null) {
            throw new ArgumentNullException(nameof(stuck));
        }

        _lock = stuck.Lock;
        _stuck = stuck.Devices;
        _claimed = stuck.Claimed;
    }

    private readonly HashSet<string> _claimed;

    /// <summary>
    /// Claim <paramref name="device"/> for one owner: false when another in this process holds it.
    /// FanControl makes a new plugin object on every refresh, and the previous one's controllers are
    /// given only a bounded time to stop, so one still mid-transfer (a lighting stream takes seconds)
    /// can outlive it; the path is not opened again until it has been closed, so two owners never
    /// write to one device at once.
    /// </summary>
    public bool TryClaim(string device) {
        if (device is null) {
            throw new ArgumentNullException(nameof(device));
        }

        lock (_lock) {
            return _claimed.Add(device);
        }
    }

    /// <summary>Let go of the claim on <paramref name="device"/>.</summary>
    public void Release(string device) {
        lock (_lock) {
            _ = _claimed.Remove(device);
        }
    }

    /// <summary>
    /// A gate over this process's stuck calls. FanControl makes a new plugin object, and so a new
    /// enumerator, on every refresh, while a call it gave up on stays stuck on its thread; so what is
    /// stuck is known across them, and a wake, logon or unlock does not start another call on a device
    /// that still has one out.
    /// </summary>
    public static DeviceCallGate ForProcess(IDeviceCallRunner runner, IDeviceCallClock clock, ILog log)
        => new DeviceCallGate(runner, clock, log, StuckCalls.Process);

    /// <summary>The calls given up on that have not returned, by device; one per process, or one per gate in a test.</summary>
    internal sealed class StuckCalls {
        /// <summary>This process's.</summary>
        public static StuckCalls Process { get; } = new StuckCalls();

        public object Lock { get; } = new object();

        public Dictionary<string, StuckDevice> Devices { get; } = new Dictionary<string, StuckDevice>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The device paths an owner has open.</summary>
        public HashSet<string> Claimed { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Run a call that starts new work on <paramref name="device"/> - a transfer, an open, a reopen, a
    /// scan - under the bound, as <see cref="IDeviceCallRunner.TryRun"/> does. Throws
    /// <see cref="IOException"/> without starting it while an earlier call to the same device that the
    /// bound gave up on has not returned.
    /// </summary>
    public bool TryRun(
        string device, string operation, Action<CancellationToken> call, int timeoutMilliseconds, Action onTimeout) {
        Validate(device, operation, call, onTimeout);
        RefuseWhileStuck(device, operation);
        return Run(device, operation, call, timeoutMilliseconds, onTimeout);
    }

    /// <summary>
    /// Run a call that only releases or cancels what <paramref name="device"/> already holds - a close,
    /// a pipe abort - even while an earlier call to it is still out. Refusing a close would leave its
    /// handles to their finalizer, on the one finalizer thread the whole host shares, where the same
    /// wedged close would block for good; refusing an abort would leave the transfer it cancels
    /// running. Neither can pile up: a transport closes once, and an abort runs only inside a
    /// transfer this gate let start. If one is itself abandoned, it holds back later calls like any
    /// other.
    /// </summary>
    public bool TryRunCleanup(
        string device, string operation, Action<CancellationToken> call, int timeoutMilliseconds, Action onTimeout) {
        Validate(device, operation, call, onTimeout);
        return Run(device, operation, call, timeoutMilliseconds, onTimeout);
    }

    /// <summary>
    /// Count <paramref name="operation"/> - a transfer the driver has not completed even once
    /// cancelled, whose memory stays the kernel's until it does - as a call still out on
    /// <paramref name="device"/>, so new work on it is refused meanwhile, exactly as for a call the
    /// bound gave up on. Returns what to call once it completes, which must be called exactly once.
    /// </summary>
    public Action HoldUntilComplete(string device, string operation) {
        if (device is null) {
            throw new ArgumentNullException(nameof(device));
        }

        if (operation is null) {
            throw new ArgumentNullException(nameof(operation));
        }

        var pending = new PendingCall(operation + ", which the driver has not completed,");
        lock (_lock) {
            pending.AbandonedAt = _clock.NowMilliseconds;
            if (!_stuck.TryGetValue(device, out StuckDevice? stuck)) {
                stuck = new StuckDevice();
                _stuck.Add(device, stuck);
            }

            stuck.Calls.Add(pending);
        }

        return () => Returned(device, pending);
    }

    private static void Validate(string device, string operation, Action<CancellationToken> call, Action onTimeout) {
        if (device is null) {
            throw new ArgumentNullException(nameof(device));
        }

        if (operation is null) {
            throw new ArgumentNullException(nameof(operation));
        }

        if (call is null) {
            throw new ArgumentNullException(nameof(call));
        }

        if (onTimeout is null) {
            throw new ArgumentNullException(nameof(onTimeout));
        }
    }

    private void RefuseWhileStuck(string device, string operation) {
        string? line = null;
        IOException refusal;
        lock (_lock) {
            if (!_stuck.TryGetValue(device, out StuckDevice? stuck)) {
                return;
            }

            // The oldest call still out is the one named: it is the one the device has been wedged
            // behind longest, and any later ones are stuck behind the same thing.
            PendingCall earliest = stuck.Calls[0];
            long ago = _clock.NowMilliseconds - earliest.AbandonedAt;
            stuck.Refused++;
            refusal = new IOException(string.Format(
                CultureInfo.InvariantCulture,
                "{0} not started: the earlier {1}, given up on {2} ms ago, has not returned.",
                operation,
                earliest.Operation,
                ago));
            if (stuck.Refused == 1) {
                line = string.Format(
                    CultureInfo.InvariantCulture,
                    "  {0} refused: the earlier {1}, given up on {2} ms ago, has not returned; every call to {3} fails fast until it does (logged once until then)",
                    operation,
                    earliest.Operation,
                    ago,
                    device);
            }
        }

        if (line != null) {
            _log.Write(line);
        }

        throw refusal;
    }

    private bool Run(string device, string operation, Action<CancellationToken> call, int timeoutMilliseconds, Action onTimeout) {
        var pending = new PendingCall(operation);
        bool completed = _runner.TryRun(operation, call, timeoutMilliseconds, onTimeout, () => Returned(device, pending));
        if (completed) {
            return true;
        }

        // Given up on. It may already have returned - it finished at the deadline, or the cancel
        // unwound it before the bound handed back - in which case nothing is left out on the device.
        lock (_lock) {
            if (!pending.Returned) {
                pending.AbandonedAt = _clock.NowMilliseconds;
                if (!_stuck.TryGetValue(device, out StuckDevice? stuck)) {
                    stuck = new StuckDevice();
                    _stuck.Add(device, stuck);
                }

                stuck.Calls.Add(pending);
            }
        }

        return false;
    }

    // Runs on whichever thread finished with the call, possibly the abandoned one, so it only takes the
    // lock and writes to the log, neither of which throws.
    private void Returned(string device, PendingCall pending) {
        string? line = null;
        lock (_lock) {
            pending.Returned = true;
            if (!_stuck.TryGetValue(device, out StuckDevice? stuck) || !stuck.Calls.Remove(pending)) {
                return;
            }

            if (stuck.Calls.Count == 0) {
                _stuck.Remove(device);
                if (stuck.Refused > 0) {
                    line = string.Format(
                        CultureInfo.InvariantCulture,
                        "  {0} returned {1} ms after it was given up on; calls to {2} resume ({3} refused meanwhile)",
                        pending.Operation,
                        _clock.NowMilliseconds - pending.AbandonedAt,
                        device,
                        stuck.Refused);
                }
            }
        }

        if (line != null) {
            _log.Write(line);
        }
    }

    // One bounded call's fate, shared between the caller that may register it as abandoned and the
    // thread that reports it returned. Guarded by the gate's lock.
    internal sealed class PendingCall {
        public PendingCall(string operation) {
            Operation = operation;
        }

        public string Operation { get; }

        public bool Returned { get; set; }

        public long AbandonedAt { get; set; }
    }

    // The calls abandoned on one device that have not returned, oldest first, and how many calls the
    // gate has refused since the first of them. Guarded by the gate's lock.
    internal sealed class StuckDevice {
        public List<PendingCall> Calls { get; } = new List<PendingCall>();

        public int Refused { get; set; }
    }
}
