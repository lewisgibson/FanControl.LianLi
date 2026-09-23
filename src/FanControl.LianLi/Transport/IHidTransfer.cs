using System;

namespace FanControl.LianLi.Transport;

/// <summary>
/// One overlapped interrupt transfer on a HID stream handle, started by <see cref="IHidApi.BeginWrite"/>
/// or <see cref="IHidApi.BeginRead"/>. The caller decides how long to wait and when to cancel, so the
/// transfer never outlives a deadline the caller chose, and those decisions are tested without a device.
///
/// Until the transfer has completed, the kernel may still write its result into memory the transfer
/// owns. So <see cref="IDisposable.Dispose"/> releases that memory only once <see cref="Wait"/> has seen
/// the transfer complete. A caller that gives up on a transfer whose completion it never saw - a
/// cancel the driver did not finish in time - calls <see cref="ReleaseWhenComplete"/> first, so the
/// memory is freed when the kernel does complete it, rather than freed early (a late completion
/// would then corrupt it) or kept for ever.
/// </summary>
internal interface IHidTransfer : IDisposable {
    /// <summary>
    /// Wait up to <paramref name="milliseconds"/> for the transfer to complete; true once it has, which
    /// includes a transfer the driver refused when it was started.
    /// </summary>
    bool Wait(int milliseconds);

    /// <summary><c>CancelIoEx(handle, overlapped)</c>: cancel this transfer alone, without waiting for it.</summary>
    bool Cancel(out int error);

    /// <summary>
    /// <c>GetOverlappedResult</c> without waiting, once <see cref="Wait"/> returned true: the bytes
    /// transferred, or the Win32 error the transfer ended with (<c>ERROR_OPERATION_ABORTED</c> for one
    /// that was cancelled).
    /// </summary>
    bool GetResult(out int transferred, out int error);

    /// <summary>
    /// The caller is giving up on this transfer before seeing it complete: release what it owns
    /// when the kernel completes it, without anyone waiting for that, then call
    /// <paramref name="released"/> (at once, for a transfer already seen to complete). It runs on the
    /// thread that saw the completion, so it must not throw.
    /// </summary>
    void ReleaseWhenComplete(Action released);
}
