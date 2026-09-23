using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace FanControl.LianLi.Transport;

/// <summary>
/// <see cref="IThreadCanceller"/> over kernel32. Elsewhere - the unit suite also runs on Linux -
/// there is no Win32 thread to cancel, so it hands out no handle and the timeout path skips the
/// cancel.
/// </summary>
// Excluded from coverage: nothing here but the P/Invoke calls themselves. The handshake that
// decides when each is called is BoundedDeviceCall's, and is unit-tested through a fake.
[ExcludeFromCodeCoverage]
internal sealed class WindowsThreadCanceller : IThreadCanceller {
    // CancelSynchronousIo requires THREAD_TERMINATE on the thread whose I/O it cancels.
    private const uint ThreadTerminate = 0x0001;

    /// <summary>The one instance; it holds no state.</summary>
    public static readonly WindowsThreadCanceller Instance = new WindowsThreadCanceller();

    private WindowsThreadCanceller() {
    }

    /// <inheritdoc />
    public IntPtr OpenCurrentThread()
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? NativeMethods.OpenThread(ThreadTerminate, false, NativeMethods.GetCurrentThreadId())
            : IntPtr.Zero;

    /// <inheritdoc />
    // A false return (ERROR_NOT_FOUND) means the thread had no I/O pending - it is blocked in
    // managed code, or the call already returned - and needs nothing.
    public void CancelSynchronousIo(IntPtr thread) => _ = NativeMethods.CancelSynchronousIo(thread);

    /// <inheritdoc />
    public void Close(IntPtr thread) => _ = NativeMethods.CloseHandle(thread);

    private static class NativeMethods {
        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr OpenThread(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int threadId);

        // Marks the synchronous I/O the thread is blocked in as cancelled, so the blocked call returns
        // with ERROR_OPERATION_ABORTED instead of waiting on a device that will never answer.
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CancelSynchronousIo(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
