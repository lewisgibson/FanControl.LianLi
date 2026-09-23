using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace FanControl.LianLi.Transport;

/// <summary>
/// <see cref="IHidApi"/> and <see cref="IHidOverlappedApi"/> over <c>kernel32</c> and <c>hid.dll</c>.
/// </summary>
// Excluded from coverage: nothing here but the P/Invoke calls themselves, the unmanaged memory an
// overlapped transfer hands the kernel and the thread-pool wait on its event. Every decision taken on
// their results is HidTransport's, HidDeviceLocator's or HidOverlappedTransfer's, unit-tested through
// a fake.
[ExcludeFromCodeCoverage]
internal sealed class WindowsHidApi : IHidApi, IHidOverlappedApi {
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    // No access rights at all: enough for HidD_GetAttributes and HidD_GetPreparsedData, and granted
    // even on an interface another process holds open exclusively.
    private const uint QueryAccess = 0;

    // No FILE_FLAG_OVERLAPPED on the query and control handles: the HidD_* calls on them are
    // synchronous, which is what lets CancelIoEx on the handle, or CancelSynchronousIo on the thread,
    // unblock one.
    private const uint SynchronousAccess = 0;

    // The stream handle is overlapped, so a read or write returns at once and the caller waits for it
    // with a deadline of its own instead of inside the driver.
    private const uint FileFlagOverlapped = 0x40000000;

    // HIDP_STATUS_SUCCESS: HidP_GetCaps returns an NTSTATUS-style code, not a Win32 BOOL.
    private const int HidpStatusSuccess = 0x00110000;

    /// <summary>The one instance; it holds no state.</summary>
    public static readonly WindowsHidApi Instance = new WindowsHidApi();

    private WindowsHidApi() {
    }

    public Guid GetInterfaceClass() {
        NativeMethods.HidD_GetHidGuid(out Guid hidClass);
        return hidClass;
    }

    public SafeHandle? OpenQueryHandle(string devicePath, out int error)
        => Open(devicePath, QueryAccess, SynchronousAccess, out error);

    public SafeHandle? OpenControlHandle(string devicePath, out int error)
        => Open(devicePath, GenericRead | GenericWrite, SynchronousAccess, out error);

    public SafeHandle? OpenStreamHandle(string devicePath, out int error)
        => Open(devicePath, GenericRead | GenericWrite, FileFlagOverlapped, out error);

    public bool GetAttributes(SafeHandle handle, out int vendorId, out int productId, out int error) {
        var attributes = new NativeMethods.HiddAttributes { Size = Marshal.SizeOf<NativeMethods.HiddAttributes>() };
        bool succeeded = NativeMethods.HidD_GetAttributes(handle, ref attributes);
        vendorId = attributes.VendorId;
        productId = attributes.ProductId;
        return Report(succeeded, out error);
    }

    public bool GetPreparsedData(SafeHandle handle, out IntPtr preparsed, out int error)
        => Report(NativeMethods.HidD_GetPreparsedData(handle, out preparsed), out error);

    // HIDP_STATUS_SUCCESS is not zero, so it is reported as 0, like every other call's success.
    public bool GetCapabilities(IntPtr preparsed, out HidCapabilities capabilities, out int status) {
        int returned = NativeMethods.HidP_GetCaps(preparsed, out NativeMethods.HidpCaps caps);
        capabilities = new HidCapabilities(caps.UsagePage, caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength);
        status = returned == HidpStatusSuccess ? 0 : returned;
        return returned == HidpStatusSuccess;
    }

    // Frees memory hid.dll allocated in this process; it reaches no device and cannot fail in any way
    // the caller could act on.
    public void FreePreparsedData(IntPtr preparsed) => _ = NativeMethods.HidD_FreePreparsedData(preparsed);

    public bool SetInputBufferCount(SafeHandle handle, int count, out int error)
        => Report(NativeMethods.HidD_SetNumInputBuffers(handle, (uint)count), out error);

    public bool SetFeature(SafeHandle handle, byte[] buffer, out int error)
        => Report(NativeMethods.HidD_SetFeature(handle, buffer, buffer.Length), out error);

    public bool GetInputReport(SafeHandle handle, byte[] buffer, out int error)
        => Report(NativeMethods.HidD_GetInputReport(handle, buffer, buffer.Length), out error);

    public bool CancelPendingIo(SafeHandle handle, out int error)
        => Report(NativeMethods.CancelIoEx(handle, IntPtr.Zero), out error);

    public IHidTransfer BeginWrite(SafeHandle stream, byte[] report)
        => HidOverlappedTransfer.Begin(this, stream, report, isRead: false);

    public IHidTransfer BeginRead(SafeHandle stream, byte[] buffer)
        => HidOverlappedTransfer.Begin(this, stream, buffer, isRead: true);

    private static SafeFileHandle? Open(string devicePath, uint access, uint flags, out int error) {
        SafeFileHandle opened = NativeMethods.CreateFile(
            devicePath,
            access,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            flags,
            IntPtr.Zero);
        if (opened.IsInvalid) {
            error = Marshal.GetLastWin32Error();
            opened.Dispose();
            return null;
        }

        error = 0;
        return opened;
    }

    // GetLastWin32Error is thread-local and overwritten by the next P/Invoke, so it is read here,
    // straight after the call it describes.
    private static bool Report(bool succeeded, out int error) {
        error = succeeded ? 0 : Marshal.GetLastWin32Error();
        return succeeded;
    }

    public IntPtr CreateEvent(out int error) {
        IntPtr created = NativeMethods.CreateEventW(IntPtr.Zero, true, false, null);
        error = created == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        return created;
    }

    // Closes a handle this process created and alone holds; there is nothing a caller could do about
    // a failure.
    public void CloseEvent(IntPtr completionEvent) => _ = NativeMethods.CloseHandle(completionEvent);

    public IntPtr AllocateBuffer(int length) => Marshal.AllocHGlobal(length);

    public IntPtr AllocateOverlapped(IntPtr completionEvent) {
        IntPtr overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        Marshal.StructureToPtr(new NativeOverlapped { EventHandle = completionEvent }, overlapped, false);
        return overlapped;
    }

    public void FreeMemory(IntPtr memory) => Marshal.FreeHGlobal(memory);

    public void CopyToBuffer(byte[] source, IntPtr buffer) => Marshal.Copy(source, 0, buffer, source.Length);

    public void CopyFromBuffer(IntPtr buffer, byte[] destination, int count) => Marshal.Copy(buffer, destination, 0, count);

    public bool StartRead(SafeHandle stream, IntPtr buffer, int length, IntPtr overlapped, out int error)
        => Report(NativeMethods.ReadFile(stream, buffer, (uint)length, IntPtr.Zero, overlapped), out error);

    public bool StartWrite(SafeHandle stream, IntPtr buffer, int length, IntPtr overlapped, out int error)
        => Report(NativeMethods.WriteFile(stream, buffer, (uint)length, IntPtr.Zero, overlapped), out error);

    public uint WaitForEvent(IntPtr completionEvent, int milliseconds)
        => NativeMethods.WaitForSingleObject(completionEvent, (uint)milliseconds);

    public bool CancelTransfer(SafeHandle stream, IntPtr overlapped, out int error)
        => Report(NativeMethods.CancelIoEx(stream, overlapped), out error);

    public bool GetOverlappedResult(SafeHandle stream, IntPtr overlapped, out int transferred, out int error) {
        bool succeeded = NativeMethods.GetOverlappedResult(stream, overlapped, out uint count, false);
        transferred = (int)count;
        return Report(succeeded, out error);
    }

    public IDisposable RegisterEventWait(IntPtr completionEvent, Action onSignalled) {
        var wait = new CompletionWait(completionEvent);
        RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(
            wait, (state, timedOut) => onSignalled(), null, Timeout.Infinite, executeOnlyOnce: true);
        return new CompletionRegistration(registration, wait);
    }

    // The transfer's own event as a WaitHandle, without a handle of its own: it neither creates nor
    // closes one, so the transfer still closes its event when it is freed.
    private sealed class CompletionWait : WaitHandle {
        public CompletionWait(IntPtr handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: false);
    }

    // What RegisterEventWait hands back: disposing it unregisters the thread-pool wait and drops the
    // WaitHandle view of the event, leaving the event itself for the transfer to close.
    private sealed class CompletionRegistration : IDisposable {
        private readonly RegisteredWaitHandle _registration;
        private readonly CompletionWait _wait;

        public CompletionRegistration(RegisteredWaitHandle registration, CompletionWait wait) {
            _registration = registration;
            _wait = wait;
        }

        public void Dispose() {
            _ = _registration.Unregister(null);
            _wait.Dispose();
        }
    }

    private static class NativeMethods {
        [StructLayout(LayoutKind.Sequential)]
        public struct HiddAttributes {
            public int Size;
            public ushort VendorId;
            public ushort ProductId;
            public ushort VersionNumber;
        }

        // HIDP_CAPS is 32 USHORTs; only the first five are read, the rest are sized in by Size (the
        // Reserved[17] block and the ten capability counts).
        [StructLayout(LayoutKind.Sequential, Size = 64)]
        public struct HidpCaps {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern SafeFileHandle CreateFile(
            string path,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        // Cancel pending I/O on the handle: overlapped = NULL cancels all the process queued on it, a
        // specific OVERLAPPED cancels that transfer alone.
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CancelIoEx(SafeHandle handle, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReadFile(SafeHandle handle, IntPtr buffer, uint bytesToRead, IntPtr bytesRead, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WriteFile(SafeHandle handle, IntPtr buffer, uint bytesToWrite, IntPtr bytesWritten, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetOverlappedResult(
            SafeHandle handle, IntPtr overlapped, out uint bytesTransferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr CreateEventW(
            IntPtr eventAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool manualReset,
            [MarshalAs(UnmanagedType.Bool)] bool initialState,
            string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("hid.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_GetAttributes(SafeHandle handle, ref HiddAttributes attributes);

        [DllImport("hid.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_GetPreparsedData(SafeHandle handle, out IntPtr preparsedData);

        [DllImport("hid.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);

        [DllImport("hid.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_SetNumInputBuffers(SafeHandle handle, uint numberBuffers);

        [DllImport("hid.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_GetInputReport(SafeHandle handle, byte[] buffer, int bufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_SetFeature(SafeHandle handle, byte[] buffer, int bufferLength);
    }
}
