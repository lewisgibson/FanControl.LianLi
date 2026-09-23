using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FanControl.LianLi.Logging;

namespace FanControl.LianLi.Transport;

/// <summary>
/// The <see cref="IDeviceTransport"/> over a HID interface, on handles the plugin opens and owns.
/// Output reports and interrupt-IN reads are overlapped <c>WriteFile</c> / <c>ReadFile</c> transfers on
/// a stream handle, each waited for with a deadline of the transport's own. Feature writes and RPM
/// telemetry are control transfers - SET_REPORT(Feature) (<c>HidD_SetFeature</c>) and GET_REPORT(Input)
/// (<c>HidD_GetInputReport</c>) - on a synchronous handle opened beside it. A handle the device has gone
/// away underneath (USB re-enumeration across sleep/wake or hibernate) is marked faulted and reopened on
/// the same device path from the worker path, on a <see cref="DoublingBackoff"/> schedule, so the
/// controller recovers without the host restarting the plugin. Every call that reaches the device -
/// stream transfer, control transfer, reopen, close - runs under a bounded wait on a thread the plugin
/// owns, so a device that has stopped answering Windows can fault the handle but never block the thread
/// using it, nor anything else in the host.
/// </summary>
internal sealed class HidTransport : IDeviceTransport {
    // How long an interrupt transfer - a TL or Galahad command packet out, its reply in - is waited for
    // before it is cancelled. A local USB transfer answers in tens of milliseconds, so this is a generous
    // ceiling that only a device that is not ready (unplugged, asleep, mid-re-enumeration after a
    // sleep/wake) runs out. Each controller has a loop of its own, so a longer wait would hold up only
    // this controller - but every tick of it, and its RPM with it.
    private const int StreamTransferTimeoutMilliseconds = 500;

    // A transfer is cancelled by CancelIoEx, but it only ends once the driver completes the cancel, so
    // that completion is waited for too - and with a deadline, since a driver that never completes it
    // must not hold the thread. A transfer whose cancel does not complete in time is released once the
    // kernel does complete it (see IHidTransfer) and faults the handle.
    private const int StreamCancelTimeoutMilliseconds = 400;

    // The whole stream call runs under this bound as well, a backstop over the two waits above (which
    // together come to 900 ms) for a start that blocks inside the driver before it can be waited on.
    private const int StreamCallTimeoutMilliseconds = 1000;

    // HidD_GetInputReport / HidD_SetFeature are synchronous control transfers with no timeout
    // parameter, so on a stale handle (the device re-enumerated across sleep/wake) they block
    // forever - freezing the keepalive thread, which is the hibernate hang. So these transfers run
    // under a bounded wait, and on timeout the pending I/O is cancelled so the handle is released
    // cleanly rather than pinned (a pinned handle blocks the next open after wake).
    private const int ControlTransferTimeoutMilliseconds = 500;

    // A reopen is a CreateFile and one HIDCLASS IOCTL per handle: milliseconds on a healthy device, an
    // immediate error on one Windows is still re-enumerating. Only a controller that has come back
    // from sleep wedged - present to Windows but never completing an open - runs this out, and that
    // is what the bound is for: the reopen runs on the worker thread, and an open that never returned
    // would hold the tick gate through the host's own post-resume refresh (Close, then Initialize,
    // five seconds after the wake), stalling FanControl with it.
    private const int ReopenTimeoutMilliseconds = 2000;

    // Closing a handle sends the driver a cleanup that completes only once the handle's pending I/O
    // has, so a close can block on the same wedged device an open does - and Dispose runs on the
    // host's refresh thread. Same ceiling; a close that runs it out is abandoned to its thread.
    private const int CloseTimeoutMilliseconds = 2000;

    // L-Connect sleeps writeDelayTime=20ms after every feature write before the next transfer; without
    // it back-to-back writes can be dropped by the controller. Applied after each feature write (the
    // fan-control and primer path) to match the vendor's pacing. Runs on the worker thread, not the host.
    private const int WriteDelayMilliseconds = 20;

    // Fallback feature report length when the interface reports none: the input report length, which
    // exceeds any real feature length, and HidD_SetFeature accepts a buffer longer than the report.
    private const int DefaultFeatureReportLength = 65;

    // The input reports the HID class driver queues on the stream handle between reads, raised from
    // its default of 32 so a reply the 0x0416 family sends while no read is pending waits there for the
    // next read instead of being pushed out by the reports that follow it.
    private const int InputBufferCount = 512;

    // Win32 errors a control transfer returns only when its handle no longer reaches a device: the
    // device was removed (re-enumerated) or the handle itself is dead. Either is a handle fault to
    // recover by reopening. Every other failure code is the device answering "no" to that command
    // and is reported as a plain failure, so a rejected command never churns the handle.
    private const int ErrorInvalidHandle = 6;
    private const int ErrorDeviceNotConnected = 1167;

    private readonly HidInterface _device;
    private readonly IHidApi _hid;
    private readonly ILog _log;
    private readonly DeviceCallGate _calls;
    private readonly ITransferDelay _delay;

    // The device's feature report byte length. HidD_SetFeature requires the buffer to be at least this
    // long, so a short command prefix (set-speed, manual-mode, primer) is padded up to it.
    private readonly int _featureReportLength;

    // Replaced by Reopen() after a fault, so not readonly. Only ever touched from the worker thread
    // (or the composition root before the worker starts), which is what lets the swap happen without
    // a lock.
    private OpenedHandles _handles;

    // Once one transfer proves the handle stale (a control transfer times out, a device-gone Win32
    // error, a stream I/O failure) every later transfer on it fails too. Latch that so the fault
    // fails fast instead of spawning a fresh watchdog every tick, and route every later transfer
    // through EnsureOpen, which reopens the device on the backoff schedule. FanControl does refresh
    // its plugins a few seconds after a resume (Close, then Initialize) but not after every
    // re-enumeration, and the fans are uncontrolled until it does: this reopen is the plugin's own
    // recovery and works whether or not the host follows up.
    private volatile bool _faulted;

    // The reopen schedule: an immediate first attempt, then a gap doubling from 10 faulted
    // transfers to 640 - about five seconds to five minutes at the worker's two transfers a tick.
    private readonly DoublingBackoff _reopenBackoff = new DoublingBackoff(10, 640);
    private int _faultedTransfers;

    // Bumped by every successful Reopen so the controller above can tell the device may have reset
    // and replay its setup. Worker-thread only, like the handles it tracks.
    private int _generation;

    // int (not bool) so the idempotency guard is a lock-free Interlocked.Exchange, as in the worker.
    private int _disposed;

    private HidTransport(
        HidInterface device,
        IHidApi hid,
        OpenedHandles handles,
        ILog log,
        DeviceCallGate calls,
        ITransferDelay delay) {
        _device = device;
        _hid = hid;
        _handles = handles;
        _log = log;
        _calls = calls;
        _delay = delay;

        int featureReportLength = device.Capabilities.FeatureReportLength;
        _featureReportLength = featureReportLength > 0 ? featureReportLength : DefaultFeatureReportLength;
    }

    // A control transfer on the raw handle: one of IHidApi's HidD_* members.
    private delegate bool ControlTransfer(SafeHandle handle, byte[] buffer, out int error);

    /// <summary>
    /// Open a transport on <paramref name="device"/>: its stream handle and the control-transfer handle
    /// beside it. Throws <see cref="IOException"/> when either open is refused, leaving nothing open, and
    /// <see cref="OperationCanceledException"/> once <paramref name="token"/> is cancelled, before its next
    /// native call. The caller bounds the open (see <see cref="WindowsDeviceEnumerator"/>).
    /// </summary>
    public static HidTransport Open(
        HidInterface device,
        IHidApi hid,
        ILog log,
        DeviceCallGate calls,
        ITransferDelay delay,
        CancellationToken token) {
        if (device is null) {
            throw new ArgumentNullException(nameof(device));
        }

        if (hid is null) {
            throw new ArgumentNullException(nameof(hid));
        }

        if (log is null) {
            throw new ArgumentNullException(nameof(log));
        }

        if (calls is null) {
            throw new ArgumentNullException(nameof(calls));
        }

        if (delay is null) {
            throw new ArgumentNullException(nameof(delay));
        }

        // The Lian Li controllers do NOT stream interrupt-IN reports, so RPM is pulled with a
        // GET_REPORT(Input) control transfer on a second, synchronous handle opened on the same device
        // path. Verified on hardware to coexist with the write stream.
        OpenedHandles handles = OpenHandles(device, hid, token);
        return new HidTransport(device, hid, handles, log, calls, delay);
    }

    // Always true: an open transport holds a stream handle opened for writing, and a write it cannot
    // make fails loudly rather than being skipped.
    public bool CanWrite => true;

    public int Generation => _generation;

    public void Write(byte[] report) {
        if (report is null) {
            throw new ArgumentNullException(nameof(report));
        }

        EnsureOpen("WriteFile");

        // An interrupt OUT transfer is one whole output report: the HID class driver refuses a buffer of
        // any other length, so a shorter report is zero-padded up to it. A report longer than the
        // interface's output report is a caller bug, not a device fault.
        int outputReportLength = _device.Capabilities.OutputReportLength;
        if (outputReportLength <= 0) {
            throw new IOException(_device.DevicePath + " has no output report to write.");
        }

        if (report.Length > outputReportLength) {
            throw new ArgumentException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "A {0}-byte report does not fit the {1}-byte output report of {2}.",
                    report.Length,
                    outputReportLength,
                    _device.DevicePath),
                nameof(report));
        }

        byte[] padded = new byte[outputReportLength];
        Array.Copy(report, padded, report.Length);
        SafeHandle stream = _handles.Stream;
        Func<IHidTransfer> begin = () => _hid.BeginWrite(stream, padded);
        RunStreamCall("WriteFile", stream, token => _ = RunStreamTransfer("WriteFile", begin, token));
    }

    public void SetFeature(byte[] report) {
        if (report is null) {
            throw new ArgumentNullException(nameof(report));
        }

        EnsureOpen("HidD_SetFeature");

        // SET_REPORT(Feature) on the same raw handle used for input reports. Set-speed, manual-mode,
        // the RPM primer, and the lighting effect commands are all feature reports; byte 0 is the
        // report id (0xE0). Pad a short command prefix up to the device's feature report length, which
        // HidD_SetFeature requires (it rejects a buffer shorter than the report).
        RunControlTransfer("HidD_SetFeature", _hid.SetFeature, PadToFeatureLength(report));

        // Match L-Connect's 20ms post-write settle so the next transfer is not dropped.
        _delay.Wait(WriteDelayMilliseconds);
    }

    public byte[] GetInputReport(byte reportId, int length) {
        if (length <= 0) {
            throw new ArgumentOutOfRangeException(nameof(length), "An input report holds at least its report id.");
        }

        string operation = string.Format(CultureInfo.InvariantCulture, "HidD_GetInputReport(0x{0:X2})", reportId);
        EnsureOpen(operation);

        // GET_REPORT(Input): byte 0 selects the report id, the device fills the rest.
        // The decoder skips the leading id via its per-family RPM offset. The device
        // answers this pull on demand even though it never streams input reports.
        byte[] buffer = new byte[length];
        buffer[0] = reportId;
        RunControlTransfer(operation, _hid.GetInputReport, buffer);
        return buffer;
    }

    public byte[] Read(int length) {
        if (length <= 0) {
            throw new ArgumentOutOfRangeException(nameof(length), "A read asks for at least one byte.");
        }

        EnsureOpen("ReadFile");

        // Interrupt-IN read for the 0x0416 command-packet family: after a command write the device
        // answers on its input endpoint. byte 0 of the reply is the report id (0x01). The HID class
        // driver hands back whole input reports only, so the buffer holds at least one; the caller gets
        // the length it asked for, zero-padded past what arrived.
        int inputReportLength = _device.Capabilities.InputReportLength;
        if (inputReportLength <= 0) {
            throw new IOException(_device.DevicePath + " has no input report to read.");
        }

        SafeHandle stream = _handles.Stream;
        byte[] received = new byte[Math.Max(length, inputReportLength)];
        int read = 0;
        Func<IHidTransfer> begin = () => _hid.BeginRead(stream, received);
        RunStreamCall("ReadFile", stream, token => read = RunStreamTransfer("ReadFile", begin, token));

        if (read <= 0) {
            throw new IOException("HID interrupt-IN read returned no data.");
        }

        byte[] buffer = new byte[length];
        Array.Copy(received, buffer, Math.Min(read, length));
        return buffer;
    }

    // Pad a short command prefix up to the device's feature report length (zero-filled). A buffer that
    // already meets or exceeds the length is sent as-is (HidD_SetFeature accepts an over-long buffer).
    private byte[] PadToFeatureLength(byte[] report) {
        if (report.Length >= _featureReportLength) {
            return report;
        }

        var padded = new byte[_featureReportLength];
        Array.Copy(report, padded, report.Length);
        return padded;
    }

    // Run one stream transfer under the stream-call bound. A transfer that failed, ran out its own wait
    // or outlived the bound means the handle no longer reaches the device, so any of the three latches
    // the fault. On the bound running out, everything pending on the stream handle is cancelled.
    private void RunStreamCall(string operation, SafeHandle stream, Action<CancellationToken> call) {
        bool completed;
        string cancelOutcome = string.Empty;
        try {
            completed = _calls.TryRun(
                _device.DevicePath, Describe(operation), call, StreamCallTimeoutMilliseconds, () => cancelOutcome = CancelPendingTransfer(stream));
        } catch (IOException ex) {
            _faulted = true;
            throw new IOException(string.Format(
                CultureInfo.InvariantCulture, "{0} failed, handle faulted: {1}", operation, ex.Message), ex);
        }

        if (!completed) {
            _faulted = true;
            throw new IOException(string.Format(
                CultureInfo.InvariantCulture,
                "{0} did not return within {1} ms; {2}; device unresponsive, handle faulted.",
                operation,
                StreamCallTimeoutMilliseconds,
                cancelOutcome));
        }
    }

    // Start one overlapped transfer and wait for it with the transport's own deadline, cancelling it and
    // then waiting for the cancel to complete, also with a deadline, if it runs out. Returns the bytes
    // transferred; throws IOException for a transfer that failed, timed out or could not be cancelled.
    // A transfer completes even as it is being cancelled, so its own result decides: one the cancel
    // aborted is a timeout, one that finished first is kept.
    // A transfer the driver does not complete even once cancelled counts, until it does, as a call
    // still out on the device (DeviceCallGate.HoldUntilComplete), so the next transfers fail fast
    // rather than queue another request the driver will not finish either.
    private int RunStreamTransfer(string operation, Func<IHidTransfer> begin, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        using IHidTransfer transfer = begin();
        bool timedOut = false;
        if (!transfer.Wait(StreamTransferTimeoutMilliseconds)) {
            timedOut = true;
            string cancel = transfer.Cancel(out int cancelError)
                ? "was cancelled"
                : string.Format(CultureInfo.InvariantCulture, "its cancel failed (error {0})", cancelError);
            if (!transfer.Wait(StreamCancelTimeoutMilliseconds)) {
                transfer.ReleaseWhenComplete(_calls.HoldUntilComplete(_device.DevicePath, Describe(operation)));
                throw new IOException(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} timed out after {1} ms and {2}, but the driver did not complete it within {3} ms more",
                    operation,
                    StreamTransferTimeoutMilliseconds,
                    cancel,
                    StreamCancelTimeoutMilliseconds));
            }
        }

        if (transfer.GetResult(out int transferred, out int error)) {
            return transferred;
        }

        throw new IOException(timedOut
            ? string.Format(CultureInfo.InvariantCulture, "{0} timed out after {1} ms", operation, StreamTransferTimeoutMilliseconds)
            : string.Format(CultureInfo.InvariantCulture, "{0} failed (error {1})", operation, error));
    }

    // Run a synchronous HID control transfer under a bounded wait. The native call has no timeout,
    // so on a stale handle it blocks forever; the bound runs it on a throwaway thread and, on
    // timeout, cancels the pending I/O via CancelIoEx so the abandoned thread unwinds and the handle
    // is released (rather than pinned, which would block the next open after wake). A timeout, or
    // a device-gone Win32 error, latches the fault so later transfers fail fast until the backoff
    // reopens the device; the worker isolates the throw either way.
    private void RunControlTransfer(string operation, ControlTransfer transfer, byte[] buffer) {
        SafeHandle control = _handles.ControlHandle;
        bool succeeded = false;
        int lastError = 0;
        string cancelOutcome = string.Empty;

        bool completed = _calls.TryRun(
            _device.DevicePath,
            Describe(operation),
            token => {
                token.ThrowIfCancellationRequested();
                succeeded = transfer(control, buffer, out lastError);
            },
            ControlTransferTimeoutMilliseconds,
            () => cancelOutcome = CancelPendingTransfer(control));

        if (!completed) {
            _faulted = true;
            throw new IOException(string.Format(
                CultureInfo.InvariantCulture,
                "{0} timed out after {1} ms; {2}; device unresponsive (re-enumerating?), handle faulted.",
                operation,
                ControlTransferTimeoutMilliseconds,
                cancelOutcome));
        }

        if (succeeded) {
            return;
        }

        if (lastError == ErrorInvalidHandle || lastError == ErrorDeviceNotConnected) {
            _faulted = true;
            throw new IOException(string.Format(
                CultureInfo.InvariantCulture, "{0} failed (error {1}); device gone, handle faulted.", operation, lastError));
        }

        throw new IOException(string.Format(
            CultureInfo.InvariantCulture, "{0} failed (error {1}).", operation, lastError));
    }

    // Cancel what the timed-out thread is still blocked in, by handle, and say whether Windows accepted
    // the cancel. A transfer that could not be cancelled keeps its handle open with I/O pending -
    // across a sleep, that is the handle the device comes back wedged behind - so the fault line in the
    // log records which of the two happened.
    private string CancelPendingTransfer(SafeHandle handle) {
        if (_hid.CancelPendingIo(handle, out int error)) {
            return "pending transfer cancelled";
        }

        return string.Format(
            CultureInfo.InvariantCulture, "cancel of the pending transfer failed (error {0})", error);
    }

    // Gate every transfer: a healthy handle passes straight through; a faulted one is either reopened
    // now (when the backoff says so) or refused fast. The refusal is an IOException so the worker's
    // per-controller catch logs and isolates it exactly like any other transfer failure.
    private void EnsureOpen(string operation) {
        if (!_faulted) {
            return;
        }

        _faultedTransfers++;
        if (!_reopenBackoff.ShouldAttempt()) {
            throw new IOException(string.Format(
                CultureInfo.InvariantCulture, "{0} skipped: HID handle faulted, reopen pending.", operation));
        }

        Reopen(operation);
    }

    // Reopen the device on its original path. The stale handles are released first so the fresh
    // open does not hold two handles on the same device, and so a failed attempt leaves nothing
    // pinned; the fault stays latched on failure and the next backoff slot retries. Windows keeps a
    // controller's device path stable across re-enumeration on the same port, so the path opened at
    // Initialize is the one to reopen. The whole attempt - the close of the stale handles as much as
    // the open, since either can block on a wedged device - runs under the reopen bound; an attempt
    // that runs it out is abandoned to its thread, which stops before its next native call and
    // disposes whatever it had already opened, and is retried whole on the next backoff slot (both
    // disposes are idempotent, so a stale handle closed twice is harmless).
    private void Reopen(string operation) {
        OpenedHandles stale = _handles;
        var handoff = new OpenHandoff<OpenedHandles>();
        try {
            // The bound's own verdict is not needed: the handoff holds the reopened handles if and
            // only if the open finished, even in the moment after the deadline. Nothing to cancel by
            // handle: the open has no handle until it returns, so the bound cancels it by thread.
            _ = _calls.TryRun(
                _device.DevicePath,
                Describe("reopen"),
                token => {
                    stale.Dispose();
                    handoff.Complete(OpenHandles(_device, _hid, token));
                },
                ReopenTimeoutMilliseconds,
                () => { });
        } catch (IOException ex) {
            throw new IOException(string.Format(
                CultureInfo.InvariantCulture,
                "{0} skipped: reopen of {1} failed: {2}",
                operation,
                _device.DevicePath,
                ex.Message), ex);
        }

        OpenedHandles? handles = handoff.Take();
        if (handles is null) {
            throw new IOException(string.Format(
                CultureInfo.InvariantCulture,
                "{0} skipped: reopen of {1} timed out after {2} ms; device not answering Windows, handle still faulted.",
                operation,
                _device.DevicePath,
                ReopenTimeoutMilliseconds));
        }

        _handles = handles;
        _faulted = false;
        _reopenBackoff.Reset();
        _generation++;
        _log.Write(string.Format(
            CultureInfo.InvariantCulture,
            "  reopened {0} after {1} faulted transfer(s)",
            _device.DevicePath,
            _faultedTransfers));
        _faultedTransfers = 0;
    }

    // Open a fresh stream and control handle on the device, leaving nothing open if any step fails or
    // the bounded call running this has been given up on before the next one.
    private static OpenedHandles OpenHandles(HidInterface device, IHidApi hid, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        SafeHandle stream = hid.OpenStreamHandle(device.DevicePath, out int error)
            ?? throw new IOException(string.Format(
                CultureInfo.InvariantCulture, "Failed to open HID stream at {0} (error {1}).", device.DevicePath, error));
        try {
            token.ThrowIfCancellationRequested();
            if (!hid.SetInputBufferCount(stream, InputBufferCount, out error)) {
                throw new IOException(string.Format(
                    CultureInfo.InvariantCulture,
                    "HidD_SetNumInputBuffers failed for {0} (error {1}).",
                    device.DevicePath,
                    error));
            }

            token.ThrowIfCancellationRequested();
            SafeHandle control = hid.OpenControlHandle(device.DevicePath, out error)
                ?? throw new IOException(string.Format(
                    CultureInfo.InvariantCulture, "Failed to open HID input handle at {0} (error {1}).", device.DevicePath, error));
            return new OpenedHandles(stream, control);
        } catch {
            stream.Dispose();
            throw;
        }
    }

    private string Describe(string operation) => operation + " on " + _device.DevicePath;

    // Dispose runs on the host's refresh thread (through the worker, once its loop has stopped), on the
    // plugin's own loop, reconnect and lighting threads, or on the composition root when an open fails
    // part-way, so the close is bounded like every other call that reaches the device: a wedged device
    // is left to its thread and to Windows, and logged. It never throws: an exception out of Dispose on
    // one of the plugin's own threads would end the FanControl service, so a close that fails is logged
    // and left to Windows like one that times out. The close does not stop for the token: a handle left
    // unclosed would be closed by its finalizer instead, on the one finalizer thread every object in the
    // host shares, where the same wedged close would block for good.
    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) {
            return;
        }

        OpenedHandles handles = _handles;
        bool closed;
        try {
            closed = _calls.TryRunCleanup(
                _device.DevicePath, Describe("close"), _ => handles.Dispose(), CloseTimeoutMilliseconds, () => { });
        }
#pragma warning disable CA1031 // host seam: Dispose runs on plugin-owned threads, where an exception ends the FanControl service; the failure is logged
        catch (Exception ex) {
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "  close of {0} failed: {1}: {2}; handles left for Windows to reclaim",
                _device.DevicePath,
                ex.GetType().Name,
                ex.Message));
            return;
        }
#pragma warning restore CA1031

        if (!closed) {
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "  close of {0} timed out after {1} ms; handles left for Windows to reclaim",
                _device.DevicePath,
                CloseTimeoutMilliseconds));
        }
    }

    // The stream and control handle an open produces, handed from the bounded open thread to the
    // worker - or disposed on that thread when the worker gave up waiting for it. Both are SafeHandles,
    // so each is closed only once an abandoned transfer still using it has returned, and a second
    // dispose is a no-op.
    private sealed class OpenedHandles : IDisposable {
        public OpenedHandles(SafeHandle stream, SafeHandle controlHandle) {
            Stream = stream;
            ControlHandle = controlHandle;
        }

        public SafeHandle Stream { get; }

        public SafeHandle ControlHandle { get; }

        // The control handle is released even when closing the stream throws, so one failure never
        // leaves the other for its finalizer.
        public void Dispose() {
            try {
                Stream.Dispose();
            } finally {
                ControlHandle.Dispose();
            }
        }
    }
}
