using System.Collections.Generic;

namespace FanControl.LianLi.Transport;

/// <summary>
/// The idle <see cref="DeviceCallThread"/>s waiting for a call. One for the process; tests make their
/// own so threads from other tests running alongside never reach them.
/// </summary>
internal sealed class DeviceCallThreadPool {
    // More than the plugin ever runs at once (one call per device at a time, a handful of devices),
    // so a thread is almost always reused; a burst beyond it ends its extra threads instead of
    // keeping them.
    internal const int DefaultMaximumIdle = 8;

    private readonly object _gate = new object();
    private readonly Stack<DeviceCallThread> _idle = new Stack<DeviceCallThread>();
    private readonly int _maximumIdle;

    internal DeviceCallThreadPool(int maximumIdle) {
        _maximumIdle = maximumIdle;
    }

    /// <summary>The pool every device call in the process uses.</summary>
    public static DeviceCallThreadPool Process { get; } = new DeviceCallThreadPool(DefaultMaximumIdle);

    /// <summary>The threads waiting for a call.</summary>
    internal int IdleCount {
        get {
            lock (_gate) {
                return _idle.Count;
            }
        }
    }

    /// <summary>An idle thread, or a new one when none is waiting.</summary>
    public DeviceCallThread Take() {
        lock (_gate) {
            if (_idle.Count > 0) {
                return _idle.Pop();
            }
        }

        return new DeviceCallThread(this);
    }

    // False when the pool already holds as many as it keeps, and the thread is to end instead.
    internal bool Return(DeviceCallThread thread) {
        lock (_gate) {
            if (_idle.Count >= _maximumIdle) {
                return false;
            }

            _idle.Push(thread);
            return true;
        }
    }
}
