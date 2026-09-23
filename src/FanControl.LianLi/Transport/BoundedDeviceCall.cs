using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace FanControl.LianLi.Transport;

/// <summary>
/// Runs a synchronous device call - a control transfer (HidD_GetInputReport / HidD_SetFeature), a
/// stream or pipe transfer, a device open, a handle close - with a bounded wait. Those native calls
/// either take no timeout or end in a wait that has none, so on a device that has stopped answering -
/// re-enumerated across a sleep/wake, or come back from one wedged - they block forever. On the
/// keepalive worker that is the hibernate hang; on a thread the host is waiting for (its post-resume
/// refresh, its Close) it freezes FanControl itself. Running the call on a throwaway thread lets the
/// caller give up after a deadline; on timeout it invokes a cancellation callback (for a transfer,
/// CancelIoEx on its handle or an abort of its pipe) and then cancels the abandoned thread's
/// synchronous I/O by thread, so a blocked open or IOCTL unwinds and releases the device handle rather
/// than pinning it (a pinned handle blocks the next open on wake - the secondary freeze a plain
/// watchdog leaves behind).
///
/// Those cancels reach only the native call in flight when the deadline passes. A compound call - a
/// flush then a write, an open then its setup, a scan walking many devices - would otherwise carry on
/// into its next native call once the cancelled one returned, and that one would have nothing left to
/// cancel it. So the call is handed a token that is cancelled at the deadline, before any I/O is
/// cancelled, and every compound call checks it before each native call it makes (Microsoft's
/// guidance for <c>CancelSynchronousIo</c>: check between synchronous operations). The
/// <see cref="OperationCanceledException"/> that check throws is the call unwinding as asked, not a
/// failure, so it is never reported.
///
/// A new thread per call, rather than a pooled or long-lived one, is deliberate. Creating one costs
/// in the order of a tenth of a millisecond, so even the few hundred transfers of a lighting stream
/// add up to milliseconds; and a thread used only once can be abandoned at the deadline with nothing
/// queued behind it, and cancelled by handle without any risk of reaching a later call that a reused
/// thread would already have moved on to.
///
/// The cheapness has one limit. A thread whose native call ignores every cancel - a CreateFile or a
/// configuration manager call blocked inside the kernel on a device that stays wedged - never returns,
/// and the reopen backoffs keep retrying such a device for the life of the process. So the bound
/// reports when an abandoned call is finally done with, and <see cref="DeviceCallGate"/> uses that to
/// start nothing more on a device while an earlier call to it is still out: at most one stuck thread
/// per device, rather than one per retry.
/// </summary>
internal static class BoundedDeviceCall {
    // Marks the thread-handle slot as claimed by the caller (see TryRun). Never a valid handle.
    private static readonly IntPtr Taken = new IntPtr(-1);

    // Who owns a failure the call throws. The thread marks the call Finished when it returns; the
    // caller marks it Abandoned when it gives up. Each side exchanges its own mark in and reads the
    // other's out, so exactly one of them sees that the other got there first: a failure that lands
    // before the caller gave up is rethrown to the caller, one that lands after is reported through
    // the late-failure callback, and none is ever lost between the two.
    private const int Running = 0;
    private const int Finished = 1;
    private const int Abandoned = 2;

    /// <summary>
    /// Run <paramref name="call"/> on a background thread, waiting up to
    /// <paramref name="timeoutMilliseconds"/>. Returns true if it completed in time, rethrowing on
    /// the caller's thread any exception <paramref name="call"/> threw. Returns false on timeout,
    /// having first cancelled the token <paramref name="call"/> was handed, then invoked
    /// <paramref name="onTimeout"/> to cancel the stuck transfer by handle, then cancelled the
    /// abandoned thread's synchronous I/O, so it can unwind either way and stops before any further
    /// native call. An exception the abandoned call throws once the caller has given up has nobody
    /// left to rethrow it to, so it is handed to <paramref name="onLateFailure"/> instead - on
    /// whichever thread noticed it last, so the callback must not throw (the transports route it to an
    /// <c>ILog</c>, which never does) - unless it is the cancellation that token raised.
    /// <paramref name="onReturned"/> is invoked exactly once, when the thread is done with
    /// <paramref name="call"/> - it returned, it threw, or it was never started because the deadline
    /// passed first - and after any late failure has been reported. For a call that completed in time
    /// that is before this returns; for one abandoned it is whenever its thread gets there, on that
    /// thread or on the caller's, so it must not throw either. For a call that never returns it is
    /// never invoked, which is how the caller can tell a call that is still out from one that is done.
    /// </summary>
    public static bool TryRun(
        Action<CancellationToken> call,
        int timeoutMilliseconds,
        Action onTimeout,
        Action<Exception> onLateFailure,
        Action onReturned)
        => TryRun(call, timeoutMilliseconds, onTimeout, onLateFailure, onReturned, WindowsThreadCanceller.Instance);

    /// <summary>
    /// <see cref="TryRun(Action{CancellationToken}, int, Action, Action{Exception}, Action)"/> with the
    /// thread operations supplied.
    /// </summary>
    internal static bool TryRun(
        Action<CancellationToken> call,
        int timeoutMilliseconds,
        Action onTimeout,
        Action<Exception> onLateFailure,
        Action onReturned,
        IThreadCanceller threads) {
        if (call is null) {
            throw new ArgumentNullException(nameof(call));
        }

        if (onTimeout is null) {
            throw new ArgumentNullException(nameof(onTimeout));
        }

        if (onLateFailure is null) {
            throw new ArgumentNullException(nameof(onLateFailure));
        }

        if (onReturned is null) {
            throw new ArgumentNullException(nameof(onReturned));
        }

        if (threads is null) {
            throw new ArgumentNullException(nameof(threads));
        }

        ExceptionDispatchInfo? failure = null;
        int state = Running;

        // Cancelled at the deadline; see the summary. Disposing it while an abandoned call may still
        // read it is safe: the token reads the source's state and nothing registers on it.
        using var abandonment = new CancellationTokenSource();
        CancellationToken token = abandonment.Token;

        // A handle on the throwaway thread, opened by that thread itself, so the timeout path
        // cancels through a handle that pins the thread object: cancelling by id instead would race
        // the thread exiting and Windows reusing its id for another thread, whose I/O would then be
        // cancelled. The slot holds 0 until the thread has opened it, and Taken once the caller has
        // claimed it; whoever finds the other side's mark closes the handle.
        IntPtr threadHandle = IntPtr.Zero;

        var thread = new Thread(() => {
            // Publishing the handle also tells the thread whether the caller has already given up:
            // a slot it finds claimed means the deadline passed before the thread even got going.
            // Then it must not start the call at all - the cancels have already been sent, so a call
            // started now that blocked would have no second chance of being cancelled. A call never
            // started is still finished with, so it goes through the same handshake below and its
            // caller hears that it returned.
            IntPtr self = threads.OpenCurrentThread();
            if (Interlocked.CompareExchange(ref threadHandle, self, IntPtr.Zero) == Taken) {
                if (self != IntPtr.Zero) {
                    threads.Close(self);
                }
            } else {
                try {
                    call(token);
                }
#pragma warning disable CA1031 // captured and rethrown on the caller's thread, or reported through onLateFailure; never dropped
                catch (Exception ex) {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
#pragma warning restore CA1031
            }

            if (Interlocked.Exchange(ref state, Finished) == Abandoned) {
                ReportLate(failure, onLateFailure, token);
                onReturned();
            }
        }) {
            IsBackground = true,
            Name = "LianLiDeviceCall",
        };
        thread.Start();

        // Thread.Join is the bounded wait and the memory barrier: when it returns true the thread has
        // terminated, so 'failure' is published to this thread.
        bool completed = thread.Join(timeoutMilliseconds);

        IntPtr claimed = Interlocked.Exchange(ref threadHandle, Taken);
        try {
            if (!completed) {
                // Abandonment is signalled before any I/O is cancelled, so the call already sees it
                // when its cancelled native call returns and makes no further one. Then the stuck call
                // is cancelled so the abandoned thread returns and releases the handle: first by handle
                // (the caller knows which, if any), then by thread, which also reaches a blocked
                // CreateFile that has no handle yet.
                abandonment.Cancel();
                onTimeout();
                if (claimed != IntPtr.Zero) {
                    threads.CancelSynchronousIo(claimed);
                }

                // Marked last, so a call the cancel above has just made fail is reported here rather
                // than raced for. The interlocked exchange publishes the thread's 'failure' write.
                if (Interlocked.Exchange(ref state, Abandoned) == Finished) {
                    ReportLate(failure, onLateFailure, token);
                    onReturned();
                }

                return false;
            }
        } finally {
            if (claimed != IntPtr.Zero) {
                threads.Close(claimed);
            }
        }

        // The thread has terminated without the call being marked abandoned, so nobody else reports it.
        onReturned();
        failure?.Throw();
        return true;
    }

    // Report what an abandoned call ended with, unless it ended well or by seeing it was abandoned:
    // the cancellation its own token raised is the call stopping before its next native call, as
    // asked, and the caller has already logged the timeout. Any other exception - including an
    // OperationCanceledException from some other token - is a failure and is reported.
    private static void ReportLate(ExceptionDispatchInfo? failure, Action<Exception> onLateFailure, CancellationToken token) {
        if (failure is null
            || (failure.SourceException is OperationCanceledException canceled && canceled.CancellationToken == token)) {
            return;
        }

        onLateFailure(failure.SourceException);
    }
}
