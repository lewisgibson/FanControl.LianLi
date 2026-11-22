using System;
using System.Threading;

namespace FanControl.LianLi.Transport;

/// <summary>
/// A thread that runs <see cref="BoundedDeviceCall"/>'s calls one at a time, and is used again for
/// the next call once one returns in time. A new thread for every call left about three Windows
/// handles behind each time until FanControl next collected garbage, and FanControl allocates so
/// little that it can go a long time without collecting: at a few calls a second its handle count
/// climbed by about 20 a second. A thread whose call was given up on is never used again, so the
/// cancels sent to it by thread reach that call and nothing later; it ends once the call returns.
/// </summary>
internal sealed class DeviceCallThread {
    private readonly DeviceCallThreadPool _pool;
    private readonly object _gate = new object();
    private Action? _work;
    private bool _done;
    private bool _retired;

    internal DeviceCallThread(DeviceCallThreadPool pool) {
        _pool = pool;
        new Thread(Loop) { IsBackground = true, Name = "LianLiDeviceCall" }.Start();
    }

    /// <summary>Start <paramref name="work"/> on this thread. It must not be running anything.</summary>
    public void Run(Action work) {
        lock (_gate) {
            _work = work;
            _done = false;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Wait up to <paramref name="timeoutMilliseconds"/> for the work to return. True once it has,
    /// with everything it wrote visible to the caller.
    /// </summary>
    public bool Wait(int timeoutMilliseconds) {
        // Only the work returning pulses the gate while a caller waits on it, so one wait is enough.
        lock (_gate) {
            if (!_done) {
                _ = Monitor.Wait(_gate, timeoutMilliseconds);
            }

            return _done;
        }
    }

    /// <summary>Hand a thread whose work returned in time back for the next call.</summary>
    public void Release() {
        if (!_pool.Return(this)) {
            Retire();
        }
    }

    /// <summary>End the thread once its current work, if any, has returned. It takes no more.</summary>
    public void Retire() {
        lock (_gate) {
            _retired = true;
            Monitor.PulseAll(_gate);
        }
    }

    private void Loop() {
        while (true) {
            Action work;
            lock (_gate) {
                while (_work is null && !_retired) {
                    _ = Monitor.Wait(_gate);
                }

                if (_work is null) {
                    return;
                }

                work = _work;
            }

            work(); // BoundedDeviceCall's work catches everything the call throws

            lock (_gate) {
                _work = null;
                _done = true;
                Monitor.PulseAll(_gate);
            }
        }
    }
}
