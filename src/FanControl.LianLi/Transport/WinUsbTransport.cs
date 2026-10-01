using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FanControl.LianLi.Logging;
using FanControl.LianLi.Protocol;

namespace FanControl.LianLi.Transport;

/// <summary>
/// The <see cref="IDeviceTransport"/> over a WinUSB device: one of the two L-Wireless dongles, the
/// HydroShift II OLED Curve's pump MCU or a FLEX receiver. Each is a plain USB device with one
/// interrupt endpoint pair carrying 64-byte packets, so <see cref="Write"/> is one OUT transfer and
/// <see cref="Read"/> gathers IN transfers - until the device stops sending, as L-Connect's
/// <c>WinUsb.ReadAll</c> does for a dongle, or the one packet the others answer with - with the
/// pipe timeouts of the device's <see cref="WinUsbPipePolicy"/>; there are no HID reports, so the
/// feature and input-report calls are not supported. Every call that reaches the device - transfer,
/// reopen, close - is bounded. A device that stops taking writes or stops answering requests is
/// marked faulted and reopened on the same path on the <see cref="DoublingBackoff"/> schedule, the
/// recovery L-Connect's own RF code relies on (<c>WinUsb.RfSend</c> closes the dongle on any failed
/// write and opens it again on the next call).
/// </summary>
internal sealed class WinUsbTransport : IDeviceTransport {
    // The devices' single endpoint pair: OUT 1 for commands, IN 1 (0x81) for replies.
    private const byte OutPipe = 0x01;
    private const byte InPipe = 0x81;

    // A read of a dongle drains the reply to its end, as ReadAll does, rather than stopping at the
    // length asked for: packets left in the dongle would otherwise sit in front of the next request's
    // reply, and a flush does not reach them (see Write). The drain is capped so a dongle that never
    // stops sending cannot hold a read open: past the length asked for, at most this many more packets
    // are read - more than the longest reply the plugin asks for, a 434-byte list page (seven
    // packets), so a whole page left over from an exchange cut short is drained in one read. A read
    // under a policy that takes the packets asked for and no more does not drain.
    private const int DrainPacketLimit = 8;

    // The policy's pipe timeouts end a transfer only when WinUSB's own cancel completes; these bound
    // the whole call around them, so a driver stack that stops completing anything cannot hold the
    // thread. A write is a flush and one packet: its pipe timeout and the margin, and never less
    // than a second, so a FLEX receiver's 2000 ms write is given its whole window before the call
    // is given up on. A read is one pipe timeout per packet it may read - those asked for, the
    // drain allowance where the policy drains, and the one that ends the reply - and the margin.
    // The margin is far beyond a healthy device's latency.
    private const int MinimumWriteCallTimeoutMilliseconds = 1000;
    private const int CallMarginMilliseconds = 500;

    // An abort is itself a request to the same driver stack, so it is bounded too; if it runs out
    // the handle's I/O is cancelled instead, which waits for nothing.
    private const int AbortTimeoutMilliseconds = 500;

    // See HidTransport: a reopen or close on a device that came back from sleep wedged never
    // returns, and either can run on a thread the host waits for.
    private const int ReopenTimeoutMilliseconds = 2000;
    private const int CloseTimeoutMilliseconds = 2000;

    // L-Connect waits 300 ms after a failed write before it closes the dongle for the next call to
    // reopen (WinUsb.RfSend: Thread.Sleep(300) then CloseRFUsb), giving a dongle that has just
    // re-enumerated time to settle before it is opened again. The same pause precedes the fault here.
    private const int FailedWriteSettleMilliseconds = 300;

    // ERROR_SEM_TIMEOUT: an IN transfer that ran out its pipe timeout, which is the normal end of a
    // reply. Any other read error, and every write error, means the pipe no longer carries packets.
    private const int ErrorSemTimeout = 121;

    private readonly string _devicePath;
    private readonly WinUsbPipePolicy _policy;
    private readonly ILog _log;
    private readonly IWinUsbApi _api;
    private readonly DeviceCallGate _calls;
    private readonly ITransferDelay _delay;

    // Replaced by Reopen() after a fault; worker-thread only, like HidTransport's handles.
    private OpenedInterface _opened;

    // Whether the last write's flush failed, and whether the last read drained packets beyond the
    // length asked for, so a run of either is logged once rather than per transfer. Worker-thread
    // only, like the handles.
    private bool _flushFailing;
    private bool _draining;

    private volatile bool _faulted;
    private readonly DoublingBackoff _reopenBackoff = new DoublingBackoff(10, 640);
    private int _faultedTransfers;
    private int _generation;
    private int _disposed;

    private WinUsbTransport(
        OpenedInterface opened,
        string devicePath,
        WinUsbPipePolicy policy,
        ILog log,
        IWinUsbApi api,
        DeviceCallGate calls,
        ITransferDelay delay) {
        _opened = opened;
        _devicePath = devicePath;
        _policy = policy;
        _log = log;
        _api = api;
        _calls = calls;
        _delay = delay;
    }

    /// <summary>
    /// Open the device at <paramref name="devicePath"/> with the pipe timeouts and reply shape of
    /// <paramref name="policy"/>; throws <see cref="IOException"/> when Windows refuses, and
    /// <see cref="OperationCanceledException"/> once <paramref name="token"/> is cancelled, before its
    /// next native call. The caller bounds the open (see <see cref="WindowsDeviceEnumerator"/>).
    /// </summary>
    public static WinUsbTransport Open(
        string devicePath,
        WinUsbPipePolicy policy,
        ILog log,
        IWinUsbApi api,
        DeviceCallGate calls,
        ITransferDelay delay,
        CancellationToken token) {
        if (string.IsNullOrEmpty(devicePath)) {
            throw new ArgumentException("Device path is required.", nameof(devicePath));
        }

        if (policy is null) {
            throw new ArgumentNullException(nameof(policy));
        }

        if (log is null) {
            throw new ArgumentNullException(nameof(log));
        }

        if (api is null) {
            throw new ArgumentNullException(nameof(api));
        }

        if (calls is null) {
            throw new ArgumentNullException(nameof(calls));
        }

        if (delay is null) {
            throw new ArgumentNullException(nameof(delay));
        }

        return new WinUsbTransport(OpenInterface(api, devicePath, policy, token), devicePath, policy, log, api, calls, delay);
    }

    public bool CanWrite => true;

    public int Generation => _generation;

    public bool IsFaulted => _faulted;

    public void Write(byte[] report) {
        if (report is null) {
            throw new ArgumentNullException(nameof(report));
        }

        EnsureOpen("WinUsb_WritePipe");

        OpenedInterface opened = _opened;
        int timeout = Math.Max(
            MinimumWriteCallTimeoutMilliseconds, (int)_policy.WriteTimeoutMilliseconds + CallMarginMilliseconds);
        bool flushed = false;
        int flushError = 0;
        bool written = false;
        int transferred = 0;
        int error = 0;
        string cancelOutcome = string.Empty;
        bool completed = _calls.TryRun(
            _devicePath,
            Describe("WinUsb_WritePipe"),
            token => {
                // A write starts a new request/reply exchange, so first drop whatever WinUSB is still
                // holding from the IN pipe. The flush reaches only what WinUSB has already taken from
                // the dongle, not packets still waiting in the dongle's own endpoint - those are
                // drained by the read that ends every exchange. A flush that fails does not stop the
                // write, which decides on its own whether the pipe still works; it is logged below.
                token.ThrowIfCancellationRequested();
                flushed = _api.FlushPipe(opened.Interface, InPipe, out flushError);

                // Checked again because a write held up here past the deadline would otherwise be
                // issued after both pipes were aborted, with nothing left to cancel it.
                token.ThrowIfCancellationRequested();
                written = _api.WritePipe(opened.Interface, OutPipe, report, out transferred, out error);
            },
            timeout,
            () => cancelOutcome = CancelPendingTransfers(opened));

        if (!completed) {
            throw FailedWrite(string.Format(
                CultureInfo.InvariantCulture,
                "WinUsb_WritePipe timed out after {0} ms; {1}",
                timeout,
                cancelOutcome));
        }

        RecordFlush(flushed, flushError);

        if (!written) {
            throw FailedWrite(string.Format(
                CultureInfo.InvariantCulture, "WinUsb_WritePipe failed (error {0})", error));
        }

        if (transferred != report.Length) {
            throw FailedWrite(string.Format(
                CultureInfo.InvariantCulture, "WinUsb_WritePipe wrote {0} of {1} bytes", transferred, report.Length));
        }
    }

    // A flush that fails leaves whatever WinUSB still held in front of the next reply. The reply
    // decoders reject a packet that echoes a different command, but not one left over from an earlier
    // request for the same thing - the tail of an earlier list reply, say - which would be read as this
    // request's reply and carry that earlier moment's values. So the failure is worth a line in the
    // log, with its code; once per run of failures, since a flush that fails once tends to fail on
    // every write until the dongle is reopened.
    private void RecordFlush(bool flushed, int error) {
        if (flushed) {
            _flushFailing = false;
            return;
        }

        if (_flushFailing) {
            return;
        }

        _flushFailing = true;
        _log.Write(string.Format(
            CultureInfo.InvariantCulture,
            "  WinUsb_FlushPipe(0x{0:X2}) failed on {1} (error {2}); the write went ahead, and a reply still queued from an earlier request may be read before the next one (logged once until a flush succeeds)",
            InPipe,
            _devicePath,
            error));
    }

    public void SetFeature(byte[] report)
        => throw new NotSupportedException("A WinUSB dongle has no HID feature reports.");

    public byte[] GetInputReport(byte reportId, int length)
        => throw new NotSupportedException("A WinUSB dongle has no HID input reports.");

    public byte[] Read(int length) {
        if (length <= 0) {
            throw new ArgumentOutOfRangeException(nameof(length), "A read asks for at least one byte.");
        }

        EnsureOpen("WinUsb_ReadPipe");

        // Gather 64-byte packets, keeping the first length bytes: until the device stops sending (an
        // IN transfer times out) where the policy reads the whole reply, otherwise the packets asked
        // for and no more. A reply shorter than requested is returned zero-padded, as the HID stream
        // read does; one longer is drained and the surplus discarded, as ReadAll does.
        OpenedInterface opened = _opened;
        byte[] buffer = new byte[length];
        int packets = (length + WirelessProtocol.PacketLength - 1) / WirelessProtocol.PacketLength;
        int limit = _policy.ReadsWholeReply ? packets + DrainPacketLimit : packets;
        int timeout = CallMarginMilliseconds + ((limit + 1) * (int)_policy.ReadTimeoutMilliseconds);
        int total = 0;
        int error = 0;
        string cancelOutcome = string.Empty;
        bool completed = _calls.TryRun(
            _devicePath,
            Describe("WinUsb_ReadPipe"),
            token => total = GatherPackets(opened, buffer, limit, token, out error),
            timeout,
            () => cancelOutcome = CancelPendingTransfers(opened));

        if (!completed) {
            Fault();
            throw new IOException(string.Format(
                CultureInfo.InvariantCulture,
                "WinUsb_ReadPipe timed out after {0} ms; {1}; device unresponsive, handle faulted.",
                timeout,
                cancelOutcome));
        }

        if (error != 0) {
            Fault();
            throw new IOException(string.Format(
                CultureInfo.InvariantCulture, "WinUsb_ReadPipe failed (error {0}); handle faulted.", error));
        }

        // Nothing at all within the pipe timeout is a reply that did not come: L-Connect's ReadAll
        // and Read hand back zeros for it and their callers ignore the reply, without closing
        // anything. So is it here - the read fails, the handle stays; a device that has really
        // stopped also fails its writes, which do fault it. A caller for whom the reply is
        // optional tells this failure apart by its type.
        if (total == 0) {
            throw new DeviceReplyMissingException("WinUSB interrupt-IN read returned no data.");
        }

        RecordDrain(total - length);
        return buffer;
    }

    // A reply longer than the length asked for is either a request the caller sized short or packets
    // left over from an earlier exchange; the surplus is discarded either way, and said once per run
    // so the log shows it without repeating it on every tick.
    private void RecordDrain(int surplus) {
        if (surplus <= 0) {
            _draining = false;
            return;
        }

        if (_draining) {
            return;
        }

        _draining = true;
        _log.Write(string.Format(
            CultureInfo.InvariantCulture,
            "  WinUsb_ReadPipe on {0}: {1} byte(s) arrived beyond the length asked for and were discarded (logged once until a read ends where it was asked to)",
            _devicePath,
            surplus));
    }

    // Read up to limit packets until the device has nothing more, keeping what fits in buffer and
    // returning how many bytes arrived in all. error is 0 unless a transfer failed for a reason other
    // than the end of the reply. Each packet is its own native call, so the token is checked before
    // each: a read given up on stops rather than going on to a transfer the abort has already passed.
    private int GatherPackets(OpenedInterface opened, byte[] buffer, int limit, CancellationToken token, out int error) {
        error = 0;
        byte[] packet = new byte[WirelessProtocol.PacketLength];
        int total = 0;
        for (int read = 0; read < limit; read++) {
            token.ThrowIfCancellationRequested();
            if (!_api.ReadPipe(opened.Interface, InPipe, packet, out int received, out int readError)) {
                if (readError != ErrorSemTimeout) {
                    error = readError;
                }

                break;
            }

            if (received == 0) {
                break;
            }

            if (total < buffer.Length) {
                Array.Copy(packet, 0, buffer, total, Math.Min(received, buffer.Length - total));
            }

            total += received;
        }

        return total;
    }

    // Every failed write is a handle fault, as it is in L-Connect: pause as L-Connect does, latch the
    // fault so the next transfer reopens the dongle, and report the failure with the device.
    private IOException FailedWrite(string failure) {
        _delay.Wait(FailedWriteSettleMilliseconds);
        Fault();
        return new IOException(failure + "; handle faulted.");
    }

    // The handle no longer reaches the device: latch the fault and move the generation on, as
    // HidTransport does and for the same reason - the next transfer reopens the device inside its
    // own call, and the controller above must see the loss before that transfer goes out.
    private void Fault() {
        _faulted = true;
        _generation++;
    }

    // Cancel a transfer that ran out its bound, and say how, for the fault line. Both pipes are
    // aborted because a write's call also flushes the IN pipe, so either may be the one stuck. The
    // abort runs bounded on its own; if it fails or stalls, the handle's pending I/O is cancelled
    // outright, which never waits - and an abort given up on does not go on to the second pipe.
    private string CancelPendingTransfers(OpenedInterface opened) {
        bool aborted = false;
        int abortError = 0;
        bool abortReturned = _calls.TryRunCleanup(
            _devicePath,
            Describe("WinUsb_AbortPipe"),
            token => {
                token.ThrowIfCancellationRequested();
                aborted = _api.AbortPipe(opened.Interface, OutPipe, out abortError);
                if (aborted) {
                    token.ThrowIfCancellationRequested();
                    aborted = _api.AbortPipe(opened.Interface, InPipe, out abortError);
                }
            },
            AbortTimeoutMilliseconds,
            () => { });

        if (abortReturned && aborted) {
            return "pending transfers aborted";
        }

        string abortOutcome = abortReturned
            ? string.Format(CultureInfo.InvariantCulture, "pipe abort failed (error {0})", abortError)
            : string.Format(CultureInfo.InvariantCulture, "pipe abort timed out after {0} ms", AbortTimeoutMilliseconds);

        if (_api.CancelPendingIo(opened.Device, out int cancelError)) {
            return abortOutcome + ", pending I/O cancelled";
        }

        return string.Format(
            CultureInfo.InvariantCulture, "{0}, cancel of pending I/O failed (error {1})", abortOutcome, cancelError);
    }

    // Gate every transfer exactly as HidTransport does: a healthy handle passes, a faulted one
    // is reopened now or refused fast, and the refusal is an IOException the worker isolates.
    private void EnsureOpen(string operation) {
        if (!_faulted) {
            return;
        }

        _faultedTransfers++;
        if (!_reopenBackoff.ShouldAttempt()) {
            throw new IOException(string.Format(
                CultureInfo.InvariantCulture, "{0} skipped: WinUSB handle faulted, reopen pending.", operation));
        }

        Reopen(operation);
    }

    // Close the stale interface and open a fresh one on the same path, both under the reopen bound
    // and handed back through OpenHandoff, for the same reasons HidTransport.Reopen gives.
    private void Reopen(string operation) {
        OpenedInterface stale = _opened;
        var handoff = new OpenHandoff<OpenedInterface>();
        try {
            // The bound's own verdict is not needed: the handoff holds the reopened interface if and
            // only if the open finished, even in the moment after the deadline.
            _ = _calls.TryRun(
                _devicePath,
                Describe("reopen"),
                token => {
                    stale.Dispose();
                    handoff.Complete(OpenInterface(_api, _devicePath, _policy, token));
                },
                ReopenTimeoutMilliseconds,
                () => { });
        } catch (IOException ex) {
            throw new IOException(string.Format(
                CultureInfo.InvariantCulture,
                "{0} skipped: reopen of {1} failed: {2}",
                operation,
                _devicePath,
                ex.Message), ex);
        }

        OpenedInterface? reopened = handoff.Take();
        if (reopened is null) {
            throw new IOException(string.Format(
                CultureInfo.InvariantCulture,
                "{0} skipped: reopen of {1} timed out after {2} ms; device not answering Windows, handle still faulted.",
                operation,
                _devicePath,
                ReopenTimeoutMilliseconds));
        }

        _opened = reopened;
        _faulted = false;
        _reopenBackoff.Reset();
        _log.Write(string.Format(
            CultureInfo.InvariantCulture, "  reopened {0} after {1} faulted transfer(s)", _devicePath, _faultedTransfers));
        _faultedTransfers = 0;
    }

    // Open the device through its USB device interface and bind WinUSB to it, then set the per-pipe
    // transfer timeouts from the policy. Anything opened is released if a later step fails, or if the
    // bounded call running this has been given up on before the next step.
    private static OpenedInterface OpenInterface(
        IWinUsbApi api, string devicePath, WinUsbPipePolicy policy, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        SafeHandle device = api.OpenDevice(devicePath, out int error)
            ?? throw new IOException(string.Format(
                CultureInfo.InvariantCulture, "Failed to open WinUSB device at {0} (error {1}).", devicePath, error));

        SafeHandle? usbInterface;
        try {
            token.ThrowIfCancellationRequested();
            usbInterface = api.Initialize(device, out error);
        } catch {
            device.Dispose();
            throw;
        }

        if (usbInterface is null) {
            device.Dispose();
            throw new IOException(string.Format(
                CultureInfo.InvariantCulture, "WinUsb_Initialize failed for {0} (error {1}).", devicePath, error));
        }

        var opened = new OpenedInterface(device, usbInterface);
        try {
            token.ThrowIfCancellationRequested();
            if (!api.SetPipeTransferTimeout(usbInterface, OutPipe, policy.WriteTimeoutMilliseconds, out error)) {
                throw PipePolicyFailure(OutPipe, devicePath, error);
            }

            token.ThrowIfCancellationRequested();
            if (!api.SetPipeTransferTimeout(usbInterface, InPipe, policy.ReadTimeoutMilliseconds, out error)) {
                throw PipePolicyFailure(InPipe, devicePath, error);
            }
        } catch {
            opened.Dispose();
            throw;
        }

        return opened;
    }

    private static IOException PipePolicyFailure(byte pipe, string devicePath, int error) {
        return new IOException(string.Format(
            CultureInfo.InvariantCulture,
            "WinUsb_SetPipePolicy(pipe 0x{0:X2}) failed for {1} (error {2}).",
            pipe,
            devicePath,
            error));
    }

    private string Describe(string operation) => operation + " on " + _devicePath;

    // Bounded, and never throwing, like HidTransport.Dispose and for the same reasons: it runs on the
    // host's refresh thread and on the plugin's own threads, a close can block on a wedged device, and
    // it does not stop for the token, since a handle left for its finalizer would block the host's
    // finalizer thread instead.
    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) {
            return;
        }

        OpenedInterface opened = _opened;
        bool closed;
        try {
            closed = _calls.TryRunCleanup(
                _devicePath, Describe("close"), _ => opened.Dispose(), CloseTimeoutMilliseconds, () => { });
        }
#pragma warning disable CA1031 // host seam: Dispose runs on plugin-owned threads, where an exception ends the FanControl service; the failure is logged
        catch (Exception ex) {
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "  close of {0} failed: {1}: {2}; handles left for Windows to reclaim",
                _devicePath,
                ex.GetType().Name,
                ex.Message));
            return;
        }
#pragma warning restore CA1031

        if (!closed) {
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "  close of {0} timed out after {1} ms; handles left for Windows to reclaim",
                _devicePath,
                CloseTimeoutMilliseconds));
        }
    }

    // The device handle and the WinUSB interface handle bound to it, released together, the
    // interface first (it holds the device handle open until it is freed). Handed from the bounded
    // open thread to the worker, or disposed on that thread when the worker gave up waiting. Both are
    // SafeHandles, so a second dispose is a no-op.
    private sealed class OpenedInterface : IDisposable {
        public OpenedInterface(SafeHandle device, SafeHandle usbInterface) {
            Device = device;
            Interface = usbInterface;
        }

        public SafeHandle Device { get; }

        public SafeHandle Interface { get; }

        // The device handle is released even when freeing the interface throws, so one failure never
        // leaves the other for its finalizer.
        public void Dispose() {
            try {
                Interface.Dispose();
            } finally {
                Device.Dispose();
            }
        }
    }
}
