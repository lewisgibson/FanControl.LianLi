using System;
using System.Runtime.InteropServices;

namespace FanControl.LianLi.Transport;

/// <summary>
/// The <c>kernel32</c> calls, unmanaged memory and thread-pool wait behind one overlapped
/// <c>ReadFile</c> or <c>WriteFile</c> on a HID stream handle, one member per underlying call, so every
/// decision <see cref="HidOverlappedTransfer"/> takes on the results - whether the transfer completed at
/// once, was queued or was refused, when it has completed, and when the memory and event the kernel
/// writes into may be released - is tested without a device. A failed call reports its Win32 error,
/// read on the calling thread straight after the call.
/// </summary>
internal interface IHidOverlappedApi {
    /// <summary>
    /// <c>CreateEventW</c>: an unnamed manual-reset event, not yet signalled, for the kernel to set when
    /// the transfer completes; <see cref="IntPtr.Zero"/> when Windows refuses.
    /// </summary>
    IntPtr CreateEvent(out int error);

    /// <summary><c>CloseHandle</c> on an event <see cref="CreateEvent"/> returned.</summary>
    void CloseEvent(IntPtr completionEvent);

    /// <summary>
    /// <c>AllocHGlobal</c>: <paramref name="length"/> bytes of unmanaged memory the kernel reads the
    /// report from or writes it into; throws <see cref="OutOfMemoryException"/> when there is none.
    /// </summary>
    IntPtr AllocateBuffer(int length);

    /// <summary>
    /// An <c>OVERLAPPED</c> in unmanaged memory, zeroed but for <paramref name="completionEvent"/>;
    /// throws <see cref="OutOfMemoryException"/> when there is no memory for it.
    /// </summary>
    IntPtr AllocateOverlapped(IntPtr completionEvent);

    /// <summary><c>FreeHGlobal</c> on memory <see cref="AllocateBuffer"/> or <see cref="AllocateOverlapped"/> returned.</summary>
    void FreeMemory(IntPtr memory);

    /// <summary>Copy all of <paramref name="source"/> into the start of <paramref name="buffer"/>.</summary>
    void CopyToBuffer(byte[] source, IntPtr buffer);

    /// <summary>Copy the first <paramref name="count"/> bytes of <paramref name="buffer"/> into <paramref name="destination"/>.</summary>
    void CopyFromBuffer(IntPtr buffer, byte[] destination, int count);

    /// <summary>
    /// <c>ReadFile</c> of <paramref name="length"/> bytes into <paramref name="buffer"/> on an overlapped
    /// handle: true when it completed at once, else false with the Win32 error, which is
    /// <c>ERROR_IO_PENDING</c> for a read that was queued.
    /// </summary>
    bool StartRead(SafeHandle stream, IntPtr buffer, int length, IntPtr overlapped, out int error);

    /// <summary>
    /// <c>WriteFile</c> of <paramref name="length"/> bytes from <paramref name="buffer"/> on an overlapped
    /// handle: true when it completed at once, else false with the Win32 error, which is
    /// <c>ERROR_IO_PENDING</c> for a write that was queued.
    /// </summary>
    bool StartWrite(SafeHandle stream, IntPtr buffer, int length, IntPtr overlapped, out int error);

    /// <summary><c>WaitForSingleObject</c> on the event for up to <paramref name="milliseconds"/>: the raw <c>WAIT_*</c> result.</summary>
    uint WaitForEvent(IntPtr completionEvent, int milliseconds);

    /// <summary><c>CancelIoEx(handle, overlapped)</c>: cancel that one transfer, without waiting for it.</summary>
    bool CancelTransfer(SafeHandle stream, IntPtr overlapped, out int error);

    /// <summary>
    /// <c>GetOverlappedResult</c> without waiting: the bytes the transfer moved, or the Win32 error it
    /// ended with.
    /// </summary>
    bool GetOverlappedResult(SafeHandle stream, IntPtr overlapped, out int transferred, out int error);

    /// <summary>
    /// A one-shot thread-pool wait on the event: <paramref name="onSignalled"/> runs on a thread-pool
    /// thread once the kernel sets it - which may be before this returns, when it is already set. Disposing
    /// the result unregisters the wait; it never closes the event.
    /// </summary>
    IDisposable RegisterEventWait(IntPtr completionEvent, Action onSignalled);
}
