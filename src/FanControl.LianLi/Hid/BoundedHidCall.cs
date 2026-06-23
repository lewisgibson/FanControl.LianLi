using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace FanControl.LianLi.Hid;

/// <summary>
/// Runs a synchronous HID control transfer (HidD_GetInputReport / HidD_SetFeature) with a bounded
/// wait. Those native calls take no timeout, so on a stale handle - the device re-enumerated across
/// a sleep/wake - they block forever and freeze the keepalive worker (the hibernate hang). Running
/// the call on a throwaway thread lets the caller give up after a deadline; on timeout it invokes a
/// cancellation callback so the pending I/O is cancelled and the abandoned thread unwinds and
/// releases the device handle, rather than pinning it (a pinned handle blocks the next Open() on
/// wake - the secondary freeze a plain watchdog leaves behind).
/// </summary>
internal static class BoundedHidCall {
    /// <summary>
    /// Run <paramref name="call"/> on a background thread, waiting up to
    /// <paramref name="timeoutMilliseconds"/>. Returns true if it completed in time, rethrowing on
    /// the caller's thread any exception <paramref name="call"/> threw. Returns false on timeout,
    /// having first invoked <paramref name="onTimeout"/> to cancel the stuck transfer so the
    /// abandoned thread can unwind.
    /// </summary>
    public static bool TryRun(Action call, int timeoutMilliseconds, Action onTimeout) {
        if (call is null) {
            throw new ArgumentNullException(nameof(call));
        }

        if (onTimeout is null) {
            throw new ArgumentNullException(nameof(onTimeout));
        }

        ExceptionDispatchInfo? failure = null;

        var thread = new Thread(() => {
            try {
                call();
            }
#pragma warning disable CA1031 // captured and rethrown on the caller's thread below; not swallowed
            catch (Exception ex) {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
#pragma warning restore CA1031
        }) {
            IsBackground = true,
            Name = "LianLiHidControlTransfer",
        };
        thread.Start();

        // Thread.Join is the bounded wait and the memory barrier: when it returns true the thread has
        // terminated, so 'failure' is published to this thread. No disposable to leak on timeout.
        if (!thread.Join(timeoutMilliseconds)) {
            // Cancel the stuck transfer so the abandoned thread returns and releases the handle.
            onTimeout();
            return false;
        }

        failure?.Throw();
        return true;
    }
}
