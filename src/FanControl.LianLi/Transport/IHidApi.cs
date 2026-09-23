using System;
using System.Runtime.InteropServices;

namespace FanControl.LianLi.Transport;

/// <summary>
/// The <c>kernel32</c> and <c>hid.dll</c> calls the plugin makes to find and drive a HID interface, one
/// member per underlying call, so every decision <see cref="HidTransport"/> and
/// <see cref="HidDeviceLocator"/> take on the results - which interface to keep, what faults a handle,
/// when to reopen, what a transfer that outlives its wait becomes - is tested without a controller.
/// Every handle is the plugin's own, opened and closed on a thread the plugin owns; nothing here takes
/// a lock or queues work that any other component in the process shares. A failed call reports its
/// Win32 error, read on the calling thread straight after the call.
/// </summary>
internal interface IHidApi {
    /// <summary><c>HidD_GetHidGuid</c>: the device interface class every HID interface is listed under.</summary>
    Guid GetInterfaceClass();

    /// <summary>
    /// <c>CreateFile</c> with no read or write access, which is all the attribute and capability reads
    /// need and which Windows grants even on an interface another process holds exclusively (a keyboard
    /// or mouse); null when Windows refuses.
    /// </summary>
    SafeHandle? OpenQueryHandle(string devicePath, out int error);

    /// <summary>
    /// <c>CreateFile</c> a synchronous read/write handle on the interface path, for control transfers;
    /// null when Windows refuses.
    /// </summary>
    SafeHandle? OpenControlHandle(string devicePath, out int error);

    /// <summary>
    /// <c>CreateFile</c> an overlapped read/write handle on the interface path, for the interrupt
    /// transfers of <see cref="BeginWrite"/> and <see cref="BeginRead"/>; null when Windows refuses.
    /// </summary>
    SafeHandle? OpenStreamHandle(string devicePath, out int error);

    /// <summary><c>HidD_GetAttributes</c>: the interface's USB vendor and product id.</summary>
    bool GetAttributes(SafeHandle handle, out int vendorId, out int productId, out int error);

    /// <summary>
    /// <c>HidD_GetPreparsedData</c>: the interface's report descriptor, parsed, in memory hid.dll
    /// allocates. The one step of reading the capabilities that reaches the device; what it returns is
    /// released with <see cref="FreePreparsedData"/>.
    /// </summary>
    bool GetPreparsedData(SafeHandle handle, out IntPtr preparsed, out int error);

    /// <summary>
    /// <c>HidP_GetCaps</c> over preparsed data: the top-level collection's usage page and report
    /// lengths. Reaches no device; on failure <paramref name="status"/> is the <c>HIDP_STATUS</c> code.
    /// </summary>
    bool GetCapabilities(IntPtr preparsed, out HidCapabilities capabilities, out int status);

    /// <summary><c>HidD_FreePreparsedData</c>: release what <see cref="GetPreparsedData"/> returned.</summary>
    void FreePreparsedData(IntPtr preparsed);

    /// <summary><c>HidD_SetNumInputBuffers</c>: how many input reports the HID class driver queues for the handle.</summary>
    bool SetInputBufferCount(SafeHandle handle, int count, out int error);

    /// <summary><c>HidD_SetFeature</c>: SET_REPORT(Feature); byte 0 of <paramref name="buffer"/> is the report id.</summary>
    bool SetFeature(SafeHandle handle, byte[] buffer, out int error);

    /// <summary>
    /// <c>HidD_GetInputReport</c>: GET_REPORT(Input) into <paramref name="buffer"/>, whose byte 0 selects
    /// the report id.
    /// </summary>
    bool GetInputReport(SafeHandle handle, byte[] buffer, out int error);

    /// <summary>
    /// <c>CancelIoEx(handle, NULL)</c>: cancel every I/O the process has pending on the handle, without
    /// waiting for any of it.
    /// </summary>
    bool CancelPendingIo(SafeHandle handle, out int error);

    /// <summary>
    /// Start an overlapped <c>WriteFile</c> of <paramref name="report"/> (one whole output report) on a
    /// stream handle. Never blocks on the device; a write the driver refused outright comes back as a
    /// transfer that has already completed with that error.
    /// </summary>
    IHidTransfer BeginWrite(SafeHandle stream, byte[] report);

    /// <summary>
    /// Start an overlapped <c>ReadFile</c> of up to <paramref name="buffer"/>'s length (at least one whole
    /// input report) on a stream handle; the bytes land in <paramref name="buffer"/> once
    /// <see cref="IHidTransfer.GetResult"/> succeeds. Never blocks on the device.
    /// </summary>
    IHidTransfer BeginRead(SafeHandle stream, byte[] buffer);
}
