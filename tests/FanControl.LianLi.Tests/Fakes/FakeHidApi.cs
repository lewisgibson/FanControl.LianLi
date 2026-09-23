using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using FanControl.LianLi.Transport;

namespace FanControl.LianLi.Tests.Fakes;

/// <summary>
/// A scripted hid.dll and kernel32. Every call is appended to <see cref="Calls"/> as text, so a test
/// asserts the exact native sequence, and <see cref="OnCall"/> runs with each call's name as it is
/// entered, so a test can land a deadline inside any one of them. Each open hands out a fresh
/// <see cref="FakeSafeHandle"/> named for its kind and path, kept in <see cref="Handles"/>. An interface
/// reports the ids in <see cref="Attributes"/> and the capabilities in <see cref="Capabilities"/> (a
/// 65-byte report of each kind on the vendor page when none are set); each open, attribute or capability
/// read fails with the error scripted for it. Control transfers take their outcome from
/// <see cref="TransferResults"/> (an empty queue is a success) and an input report is filled from
/// <see cref="InputReports"/>. A stream transfer is the next <see cref="Transfers"/> entry, or one that
/// completes at once and in full.
/// </summary>
internal sealed class FakeHidApi : IHidApi {
    public static readonly Guid HidClass = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");

    public static readonly HidCapabilities DefaultCapabilities = new HidCapabilities(0xFF00, 65, 65, 65);

    private readonly Dictionary<SafeHandle, string> _paths = new Dictionary<SafeHandle, string>();

    public List<string> Calls { get; } = new List<string>();

    public Action<string>? OnCall { get; set; }

    public List<FakeSafeHandle> Handles { get; } = new List<FakeSafeHandle>();

    public Dictionary<string, int> QueryOpenErrors { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

    public Dictionary<string, (int VendorId, int ProductId)> Attributes { get; } =
        new Dictionary<string, (int VendorId, int ProductId)>(StringComparer.Ordinal);

    public Dictionary<string, int> AttributeErrors { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

    public Dictionary<string, HidCapabilities> Capabilities { get; } =
        new Dictionary<string, HidCapabilities>(StringComparer.Ordinal);

    public Dictionary<string, int> CapabilityErrors { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Per path, the <c>HIDP_STATUS</c> code <c>HidP_GetCaps</c> fails with; absent means it parses.</summary>
    public Dictionary<string, int> CapabilityStatuses { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>The path of each interface whose preparsed data was freed, in order.</summary>
    public List<string> Freed { get; } = new List<string>();

    private readonly Dictionary<IntPtr, string> _preparsed = new Dictionary<IntPtr, string>();

    /// <summary>Win32 errors the next stream opens fail with, in order; empty means the open succeeds.</summary>
    public Queue<int> StreamOpenErrors { get; } = new Queue<int>();

    public int? InputBufferError { get; set; }

    /// <summary>Win32 errors the next control-handle opens fail with, in order; empty means the open succeeds.</summary>
    public Queue<int> ControlOpenErrors { get; } = new Queue<int>();

    /// <summary>Win32 error each next control transfer fails with; 0 or an empty queue succeeds.</summary>
    public Queue<int> TransferResults { get; } = new Queue<int>();

    public Queue<byte[]> InputReports { get; } = new Queue<byte[]>();

    public List<byte[]> Features { get; } = new List<byte[]>();

    public int? CancelError { get; set; }

    public Queue<FakeHidTransfer> Transfers { get; } = new Queue<FakeHidTransfer>();

    public List<FakeHidTransfer> Started { get; } = new List<FakeHidTransfer>();

    public Exception? BeginFailure { get; set; }

    public List<byte[]> Written { get; } = new List<byte[]>();

    /// <summary>The handles of one kind ("query", "stream", "control") in the order they were opened.</summary>
    public List<FakeSafeHandle> HandlesOf(string kind)
        => Handles.FindAll(handle => handle.Name.StartsWith(kind + " ", StringComparison.Ordinal));

    public Guid GetInterfaceClass() {
        Record("HidD_GetHidGuid");
        return HidClass;
    }

    public SafeHandle? OpenQueryHandle(string devicePath, out int error) {
        Record("OpenQueryHandle " + devicePath);
        return Open("query", devicePath, QueryOpenErrors.TryGetValue(devicePath, out int failure) ? failure : 0, out error);
    }

    public SafeHandle? OpenControlHandle(string devicePath, out int error) {
        Record("OpenControlHandle " + devicePath);
        return Open("control", devicePath, ControlOpenErrors.Count > 0 ? ControlOpenErrors.Dequeue() : 0, out error);
    }

    public SafeHandle? OpenStreamHandle(string devicePath, out int error) {
        Record("OpenStreamHandle " + devicePath);
        return Open("stream", devicePath, StreamOpenErrors.Count > 0 ? StreamOpenErrors.Dequeue() : 0, out error);
    }

    public bool GetAttributes(SafeHandle handle, out int vendorId, out int productId, out int error) {
        string path = _paths[handle];
        Record("HidD_GetAttributes " + handle);
        (vendorId, productId) = Attributes.TryGetValue(path, out (int VendorId, int ProductId) ids) ? ids : (0, 0);
        return Result(AttributeErrors.TryGetValue(path, out int failure) ? failure : 0, out error);
    }

    public bool GetPreparsedData(SafeHandle handle, out IntPtr preparsed, out int error) {
        string path = _paths[handle];
        Record("HidD_GetPreparsedData " + handle);
        if (!Result(CapabilityErrors.TryGetValue(path, out int failure) ? failure : 0, out error)) {
            preparsed = IntPtr.Zero;
            return false;
        }

        preparsed = new IntPtr(_preparsed.Count + 1);
        _preparsed.Add(preparsed, path);
        return true;
    }

    public bool GetCapabilities(IntPtr preparsed, out HidCapabilities capabilities, out int status) {
        string path = _preparsed[preparsed];
        capabilities = Capabilities.TryGetValue(path, out HidCapabilities known) ? known : DefaultCapabilities;
        return Result(CapabilityStatuses.TryGetValue(path, out int failure) ? failure : 0, out status);
    }

    public void FreePreparsedData(IntPtr preparsed) => Freed.Add(_preparsed[preparsed]);

    public bool SetInputBufferCount(SafeHandle handle, int count, out int error) {
        Record(string.Format(CultureInfo.InvariantCulture, "HidD_SetNumInputBuffers {0} {1}", handle, count));
        return Result(InputBufferError ?? 0, out error);
    }

    public bool SetFeature(SafeHandle handle, byte[] buffer, out int error) {
        Record(string.Format(CultureInfo.InvariantCulture, "HidD_SetFeature {0} {1}", handle, buffer.Length));
        Features.Add((byte[])buffer.Clone());
        return Result(TransferResults.Count > 0 ? TransferResults.Dequeue() : 0, out error);
    }

    public bool GetInputReport(SafeHandle handle, byte[] buffer, out int error) {
        Record(string.Format(CultureInfo.InvariantCulture, "HidD_GetInputReport {0} {1}", handle, buffer.Length));
        if (InputReports.Count > 0) {
            byte[] report = InputReports.Dequeue();
            Array.Copy(report, buffer, Math.Min(report.Length, buffer.Length));
        }

        return Result(TransferResults.Count > 0 ? TransferResults.Dequeue() : 0, out error);
    }

    public bool CancelPendingIo(SafeHandle handle, out int error) {
        Record("CancelIoEx " + handle);
        return Result(CancelError ?? 0, out error);
    }

    public IHidTransfer BeginWrite(SafeHandle stream, byte[] report) {
        Record(string.Format(CultureInfo.InvariantCulture, "WriteFile {0} {1}", stream, report.Length));
        Written.Add((byte[])report.Clone());
        return Begin(report.Length, null);
    }

    public IHidTransfer BeginRead(SafeHandle stream, byte[] buffer) {
        Record(string.Format(CultureInfo.InvariantCulture, "ReadFile {0} {1}", stream, buffer.Length));
        return Begin(buffer.Length, buffer);
    }

    private FakeHidTransfer Begin(int length, byte[]? readInto) {
        if (BeginFailure != null) {
            throw BeginFailure;
        }

        FakeHidTransfer transfer = Transfers.Count > 0 ? Transfers.Dequeue() : new FakeHidTransfer();
        transfer.Bind(length, readInto);
        Started.Add(transfer);
        return transfer;
    }

    private FakeSafeHandle? Open(string kind, string devicePath, int failure, out int error) {
        error = failure;
        if (failure != 0) {
            return null;
        }

        var handle = new FakeSafeHandle(kind + " " + Handles.Count);
        Handles.Add(handle);
        _paths[handle] = devicePath;
        return handle;
    }

    private void Record(string call) {
        Calls.Add(call);
        OnCall?.Invoke(call);
    }

    private static bool Result(int failure, out int error) {
        error = failure;
        return failure == 0;
    }
}
