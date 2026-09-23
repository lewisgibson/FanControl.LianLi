using System.Runtime.InteropServices;

namespace FanControl.LianLi.Transport;

/// <summary>
/// The <c>kernel32</c> and <c>winusb</c> calls <see cref="WinUsbTransport"/> makes, one member per
/// native function, so every decision the transport takes on their results - which failure faults the
/// handle, what a short or empty read means, what is cancelled on a timeout, when to reopen - is tested
/// without a dongle. Handles are <see cref="SafeHandle"/>s so a handle closed while an abandoned call
/// is still inside a native function is released only once that call returns. A failed call reports
/// its Win32 error, read on the calling thread straight after the call.
/// </summary>
internal interface IWinUsbApi {
    /// <summary>
    /// <c>CreateFile</c> on the interface path, with the overlapped access WinUSB requires; null when
    /// Windows refuses.
    /// </summary>
    SafeHandle? OpenDevice(string devicePath, out int error);

    /// <summary>
    /// <c>WinUsb_Initialize</c>: bind WinUSB to an open device. The interface handle keeps the device
    /// handle open until the interface has been freed. Null when WinUSB refuses.
    /// </summary>
    SafeHandle? Initialize(SafeHandle device, out int error);

    /// <summary><c>WinUsb_SetPipePolicy(PIPE_TRANSFER_TIMEOUT)</c>: how long one transfer on the pipe may take.</summary>
    bool SetPipeTransferTimeout(SafeHandle usbInterface, byte pipe, uint milliseconds, out int error);

    /// <summary><c>WinUsb_FlushPipe</c>: discard what the device has queued on an IN pipe.</summary>
    bool FlushPipe(SafeHandle usbInterface, byte pipe, out int error);

    /// <summary><c>WinUsb_WritePipe</c>: one synchronous OUT transfer.</summary>
    bool WritePipe(SafeHandle usbInterface, byte pipe, byte[] buffer, out int transferred, out int error);

    /// <summary><c>WinUsb_ReadPipe</c>: one synchronous IN transfer into <paramref name="buffer"/>.</summary>
    bool ReadPipe(SafeHandle usbInterface, byte pipe, byte[] buffer, out int transferred, out int error);

    /// <summary><c>WinUsb_AbortPipe</c>: cancel every transfer pending on the pipe.</summary>
    bool AbortPipe(SafeHandle usbInterface, byte pipe, out int error);

    /// <summary>
    /// <c>CancelIoEx(device, NULL)</c>: cancel every I/O the process has pending on the device handle,
    /// without waiting for any of it.
    /// </summary>
    bool CancelPendingIo(SafeHandle device, out int error);
}
