using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FanControl.LianLi.Transport;

/// <summary>
/// <see cref="IWinUsbApi"/> over <c>kernel32</c> and <c>winusb.dll</c>.
/// </summary>
// Excluded from coverage: nothing here but the P/Invoke calls themselves and the handle type that
// frees what they return. Every decision taken on their results is WinUsbTransport's, unit-tested
// through a fake.
[ExcludeFromCodeCoverage]
internal sealed class WindowsWinUsbApi : IWinUsbApi {
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;

    // WinUSB requires a handle opened for overlapped I/O, even for the synchronous calls made here.
    private const uint FileFlagOverlapped = 0x40000000;

    // WinUsb_SetPipePolicy PIPE_TRANSFER_TIMEOUT: milliseconds a transfer on that pipe may take.
    private const uint PipeTransferTimeoutPolicy = 0x03;

    /// <summary>The one instance; it holds no state.</summary>
    public static readonly WindowsWinUsbApi Instance = new WindowsWinUsbApi();

    private WindowsWinUsbApi() {
    }

    public SafeHandle? OpenDevice(string devicePath, out int error) {
        SafeFileHandle opened = NativeMethods.CreateFile(
            devicePath,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal | FileFlagOverlapped,
            IntPtr.Zero);
        if (opened.IsInvalid) {
            error = Marshal.GetLastWin32Error();
            opened.Dispose();
            return null;
        }

        error = 0;
        return opened;
    }

    public SafeHandle? Initialize(SafeHandle device, out int error) {
        if (!NativeMethods.WinUsb_Initialize(device, out SafeWinUsbInterfaceHandle opened)) {
            error = Marshal.GetLastWin32Error();
            opened.Dispose();
            return null;
        }

        opened.HoldDevice(device);
        error = 0;
        return opened;
    }

    public bool SetPipeTransferTimeout(SafeHandle usbInterface, byte pipe, uint milliseconds, out int error) {
        uint value = milliseconds;
        return Report(
            NativeMethods.WinUsb_SetPipePolicy(usbInterface, pipe, PipeTransferTimeoutPolicy, sizeof(uint), ref value),
            out error);
    }

    public bool FlushPipe(SafeHandle usbInterface, byte pipe, out int error)
        => Report(NativeMethods.WinUsb_FlushPipe(usbInterface, pipe), out error);

    public bool WritePipe(SafeHandle usbInterface, byte pipe, byte[] buffer, out int transferred, out int error) {
        bool succeeded = NativeMethods.WinUsb_WritePipe(
            usbInterface, pipe, buffer, (uint)buffer.Length, out uint written, IntPtr.Zero);
        transferred = (int)written;
        return Report(succeeded, out error);
    }

    public bool ReadPipe(SafeHandle usbInterface, byte pipe, byte[] buffer, out int transferred, out int error) {
        bool succeeded = NativeMethods.WinUsb_ReadPipe(
            usbInterface, pipe, buffer, (uint)buffer.Length, out uint read, IntPtr.Zero);
        transferred = (int)read;
        return Report(succeeded, out error);
    }

    public bool AbortPipe(SafeHandle usbInterface, byte pipe, out int error)
        => Report(NativeMethods.WinUsb_AbortPipe(usbInterface, pipe), out error);

    public bool CancelPendingIo(SafeHandle device, out int error)
        => Report(NativeMethods.CancelIoEx(device, IntPtr.Zero), out error);

    // GetLastWin32Error is thread-local and overwritten by the next P/Invoke, so it is read here,
    // straight after the call it describes.
    private static bool Report(bool succeeded, out int error) {
        error = succeeded ? 0 : Marshal.GetLastWin32Error();
        return succeeded;
    }

    // The WINUSB_INTERFACE_HANDLE WinUsb_Initialize returns, freed with WinUsb_Free. As a SafeHandle
    // it is not freed while a call on another thread is still using it - the abandoned transfer a
    // timeout leaves behind - and it holds a reference on the device handle it was bound to, so that
    // handle closes only after WinUSB has let go of it, whichever of the two is disposed first.
    private sealed class SafeWinUsbInterfaceHandle : SafeHandleZeroOrMinusOneIsInvalid {
        private SafeHandle? _device;

        public SafeWinUsbInterfaceHandle()
            : base(ownsHandle: true) {
        }

        public void HoldDevice(SafeHandle device) {
            bool added = false;
            device.DangerousAddRef(ref added);
            if (added) {
                _device = device;
            }
        }

        protected override bool ReleaseHandle() {
            bool freed = NativeMethods.WinUsb_Free(handle);
            _device?.DangerousRelease();
            return freed;
        }
    }

    private static class NativeMethods {
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

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CancelIoEx(SafeHandle handle, IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_Initialize(SafeHandle deviceHandle, out SafeWinUsbInterfaceHandle interfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_Free(IntPtr interfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_SetPipePolicy(
            SafeHandle interfaceHandle, byte pipeId, uint policyType, uint valueLength, ref uint value);

        [DllImport("winusb.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_WritePipe(
            SafeHandle interfaceHandle, byte pipeId, byte[] buffer, uint bufferLength, out uint lengthTransferred, IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_ReadPipe(
            SafeHandle interfaceHandle, byte pipeId, byte[] buffer, uint bufferLength, out uint lengthTransferred, IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_FlushPipe(SafeHandle interfaceHandle, byte pipeId);

        [DllImport("winusb.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_AbortPipe(SafeHandle interfaceHandle, byte pipeId);
    }
}
