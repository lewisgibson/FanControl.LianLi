using System;
using System.IO;
using System.Linq;
using System.Threading;
using FanControl.LianLi.Tests.Fakes;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

/// <summary>
/// The WinUSB transport's decisions, driven through a scripted winusb.dll and a rule-based bound: how
/// a dongle is opened, what a write and a read put on the pipes, which failures fault the handle (every
/// failed write, as in L-Connect's <c>WinUsb.RfSend</c>, and a request left unanswered), how a read
/// drains a reply to its end as L-Connect's <c>WinUsb.ReadAll</c> does, how a stuck transfer is
/// cancelled, what a call given up on does next, how a faulted handle is reopened on the backoff, and a
/// close that can never throw.
/// </summary>
public class WinUsbTransportTests {
    private const string Path = @"\\?\usb#vid_0416&pid_8041#6&1b2c3d4e&0&3#{guid}";

    private readonly FakeWinUsbApi _api = new FakeWinUsbApi();
    private readonly FakeDeviceCallRunner _calls = new FakeDeviceCallRunner();
    private readonly FakeDeviceCallClock _clock = new FakeDeviceCallClock();
    private readonly FakeTransferDelay _delay = new FakeTransferDelay();
    private readonly FakeLogger _log = new FakeLogger();
    private readonly DeviceCallGate _gate;

    private WinUsbTransport Open() => WinUsbTransport.Open(Path, _log, _api, _gate, _delay, CancellationToken.None);

    public WinUsbTransportTests() {
        _gate = new DeviceCallGate(_calls, _clock, _log);
    }

    private static byte[] Packet(byte first) {
        var packet = new byte[64];
        packet[0] = first;
        packet[63] = (byte)(first + 1);
        return packet;
    }

    private int OpenCount => _api.Calls.Count(call => call.StartsWith("OpenDevice", StringComparison.Ordinal));

    private void Fault(WinUsbTransport transport) {
        _api.WriteResults.Enqueue((false, 0, 31));
        Assert.Throws<IOException>(() => transport.Write(Packet(1)));
    }

    [Fact]
    public void Open_OpensTheDevice_BindsWinUsb_AndSetsBothPipeTimeouts() {
        using WinUsbTransport transport = Open();

        Assert.Equal(
            new[] {
                "OpenDevice " + Path,
                "Initialize device0",
                "SetPipeTransferTimeout interface0 0x01 100",
                "SetPipeTransferTimeout interface0 0x81 50",
            },
            _api.Calls);
        Assert.True(transport.CanWrite);
        Assert.Equal(0, transport.Generation);
    }

    [Fact]
    public void Open_DeviceRefused_Throws_WithTheWin32Error() {
        _api.OpenDeviceErrors.Enqueue(5);

        IOException failure = Assert.Throws<IOException>(Open);

        Assert.Equal("Failed to open WinUSB device at " + Path + " (error 5).", failure.Message);
    }

    [Fact]
    public void Open_WinUsbRefused_ClosesTheDevice() {
        _api.InitializeError = 31;

        IOException failure = Assert.Throws<IOException>(Open);

        Assert.Equal("WinUsb_Initialize failed for " + Path + " (error 31).", failure.Message);
        Assert.Equal(1, _api.Devices[0].Releases);
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0x81)]
    public void Open_PipePolicyRefused_ReleasesTheInterfaceAndTheDevice(byte pipe) {
        _api.PipePolicyErrors[pipe] = 87;

        IOException failure = Assert.Throws<IOException>(Open);

        Assert.Equal(
            string.Format("WinUsb_SetPipePolicy(pipe 0x{0:X2}) failed for {1} (error 87).", pipe, Path),
            failure.Message);
        Assert.Equal(1, _api.Interfaces[0].Releases);
        Assert.Equal(1, _api.Devices[0].Releases);
    }

    // The four native steps of an open. One given up on while any of the first three is in flight
    // makes no further call and releases what it had opened.
    [Theory]
    [InlineData("OpenDevice", 0)]
    [InlineData("Initialize", 1)]
    [InlineData("SetPipeTransferTimeout interface0 0x01", 1)]
    public void Open_GivenUpOnDuringAStep_MakesNoFurtherCall_AndReleasesWhatItOpened(string blocked, int interfaces) {
        using var abandonment = new CancellationTokenSource();
        _api.OnCall = call => {
            if (call.StartsWith(blocked, StringComparison.Ordinal)) {
                abandonment.Cancel();
            }
        };

        Assert.Throws<OperationCanceledException>(
            () => WinUsbTransport.Open(Path, _log, _api, _gate, _delay, abandonment.Token));

        Assert.StartsWith(blocked, _api.Calls.Last(), StringComparison.Ordinal);
        Assert.Equal(1, _api.Devices[0].Releases);
        Assert.Equal(interfaces, _api.Interfaces.Count);
        Assert.All(_api.Interfaces, handle => Assert.Equal(1, handle.Releases));
    }

    [Fact]
    public void Open_AlreadyGivenUpOn_OpensNothing() {
        Assert.Throws<OperationCanceledException>(
            () => WinUsbTransport.Open(Path, _log, _api, _gate, _delay, FakeDeviceCallRunner.Cancelled));

        Assert.Empty(_api.Calls);
    }

    [Fact]
    public void Open_ValidatesItsArguments() {
        CancellationToken none = CancellationToken.None;
        Assert.Throws<ArgumentException>(() => WinUsbTransport.Open(string.Empty, _log, _api, _gate, _delay, none));
        Assert.Throws<ArgumentNullException>(() => WinUsbTransport.Open(Path, null!, _api, _gate, _delay, none));
        Assert.Throws<ArgumentNullException>(() => WinUsbTransport.Open(Path, _log, null!, _gate, _delay, none));
        Assert.Throws<ArgumentNullException>(() => WinUsbTransport.Open(Path, _log, _api, null!, _delay, none));
        Assert.Throws<ArgumentNullException>(() => WinUsbTransport.Open(Path, _log, _api, _gate, null!, none));
    }

    [Fact]
    public void HidReports_AreNotSupported() {
        using WinUsbTransport transport = Open();

        Assert.Throws<NotSupportedException>(() => transport.SetFeature(new byte[] { 0xE0 }));
        Assert.Throws<NotSupportedException>(() => transport.GetInputReport(0xE0, 65));
    }

    [Fact]
    public void Write_FlushesTheInPipe_ThenWritesThePacketOut_UnderTheWriteBound() {
        using WinUsbTransport transport = Open();
        byte[] packet = Packet(0x10);

        transport.Write(packet);

        Assert.Equal(new[] { "FlushPipe interface0 0x81", "WritePipe interface0 0x01 64" }, _api.Calls.Skip(4));
        Assert.Equal(packet, Assert.Single(_api.Written));
        Assert.Equal(1000, _calls.TimeoutOf("WinUsb_WritePipe on " + Path));
        Assert.Empty(_delay.Waits);
    }

    [Fact]
    public void Write_AFlushThatFails_DoesNotStopTheWrite_AndIsLoggedOncePerRun() {
        using WinUsbTransport transport = Open();
        _api.FlushSucceeds = false;

        transport.Write(Packet(0x10));
        transport.Write(Packet(0x10));

        Assert.Equal(2, _api.Written.Count);
        Assert.Equal(
            "  WinUsb_FlushPipe(0x81) failed on " + Path + " (error 31); the write went ahead, and a reply still queued from an earlier request may be read before the next one (logged once until a flush succeeds)",
            Assert.Single(_log.Messages));

        _api.FlushSucceeds = true;
        transport.Write(Packet(0x10));
        _api.FlushSucceeds = false;
        transport.Write(Packet(0x10));
        Assert.Equal(2, _log.Messages.Count);
    }

    [Fact]
    public void Write_GivenUpOnDuringTheFlush_NeverIssuesTheWrite() {
        // The deadline lands while the flush is in flight: both pipes are aborted, the flush returns,
        // and the write that would have followed with nothing left to cancel it is not issued.
        using WinUsbTransport transport = Open();
        _api.OnCall = call => {
            if (call.StartsWith("FlushPipe", StringComparison.Ordinal)) {
                _calls.AbandonRunning();
            }
        };

        IOException failure = Assert.Throws<IOException>(() => transport.Write(Packet(1)));

        Assert.Equal("WinUsb_WritePipe timed out after 1000 ms; pending transfers aborted; handle faulted.", failure.Message);
        Assert.Equal(
            new[] { "FlushPipe interface0 0x81", "AbortPipe interface0 0x01", "AbortPipe interface0 0x81" },
            _api.Calls.Skip(4));
        Assert.Empty(_api.Written);
        Assert.Empty(_calls.LateFailures);
    }

    [Fact]
    public void Write_GivenUpOnBeforeItStarted_NeverReachesTheDongle() {
        using WinUsbTransport transport = Open();
        _calls.TimesOut = operation => operation.StartsWith("WinUsb_WritePipe", StringComparison.Ordinal);
        Assert.Throws<IOException>(() => transport.Write(Packet(1)));
        int before = _api.Calls.Count;

        Assert.Throws<OperationCanceledException>(() => Assert.Single(_calls.Abandoned)(FakeDeviceCallRunner.Cancelled));

        Assert.Equal(before, _api.Calls.Count);
    }

    [Theory]
    [InlineData(121)]  // ERROR_SEM_TIMEOUT: the 100 ms pipe timeout ran out
    [InlineData(31)]   // ERROR_GEN_FAILURE
    [InlineData(22)]   // ERROR_BAD_COMMAND
    [InlineData(995)]  // ERROR_OPERATION_ABORTED
    [InlineData(1167)] // ERROR_DEVICE_NOT_CONNECTED
    public void Write_AnyFailure_PausesAsLConnectDoes_ThenFaultsTheHandle(int error) {
        using WinUsbTransport transport = Open();
        _api.WriteResults.Enqueue((false, 0, error));

        IOException failure = Assert.Throws<IOException>(() => transport.Write(Packet(1)));

        Assert.Equal($"WinUsb_WritePipe failed (error {error}); handle faulted.", failure.Message);
        Assert.Equal(new[] { 300 }, _delay.Waits);

        transport.Write(Packet(2));
        Assert.Equal(2, OpenCount);
        Assert.Equal(1, transport.Generation);
    }

    [Fact]
    public void Write_AShortWrite_FaultsTheHandle() {
        using WinUsbTransport transport = Open();
        _api.WriteResults.Enqueue((true, 10, 0));

        IOException failure = Assert.Throws<IOException>(() => transport.Write(Packet(1)));

        Assert.Equal("WinUsb_WritePipe wrote 10 of 64 bytes; handle faulted.", failure.Message);
        transport.Write(Packet(2));
        Assert.Equal(1, transport.Generation);
    }

    [Fact]
    public void Write_OutlivingItsBound_AbortsBothPipes_AndFaults() {
        using WinUsbTransport transport = Open();
        _calls.TimesOut = operation => operation.StartsWith("WinUsb_WritePipe", StringComparison.Ordinal);

        IOException failure = Assert.Throws<IOException>(() => transport.Write(Packet(1)));

        Assert.Equal("WinUsb_WritePipe timed out after 1000 ms; pending transfers aborted; handle faulted.", failure.Message);
        Assert.Equal(new[] { "AbortPipe interface0 0x01", "AbortPipe interface0 0x81" }, _api.Calls.Skip(4));
        Assert.Equal(500, _calls.TimeoutOf("WinUsb_AbortPipe on " + Path));
        Assert.Equal(new[] { 300 }, _delay.Waits);

        // The aborts unwind the stuck write, which returns having seen it was given up on; only then
        // is the dongle free for the reopen.
        Assert.Throws<OperationCanceledException>(() => Assert.Single(_calls.Abandoned)(FakeDeviceCallRunner.Cancelled));
        _calls.TimesOut = _ => false;
        transport.Write(Packet(2));
        Assert.Equal(1, transport.Generation);
    }

    [Fact]
    public void Write_OutlivingItsBound_WhileTheStuckWriteHasNotReturned_RefusesTheReopenFast_UntilItDoes() {
        using WinUsbTransport transport = Open();
        _calls.TimesOut = operation => operation.StartsWith("WinUsb_WritePipe", StringComparison.Ordinal);
        _clock.NowMilliseconds = 1000;
        Assert.Throws<IOException>(() => transport.Write(Packet(1)));
        _calls.TimesOut = _ => false;
        _clock.NowMilliseconds = 4500;

        IOException refused = Assert.Throws<IOException>(() => transport.Write(Packet(2)));

        Assert.Equal(
            "WinUsb_WritePipe skipped: reopen of " + Path + " failed: reopen on " + Path + " not started: the earlier WinUsb_WritePipe on " + Path + ", given up on 3500 ms ago, has not returned.",
            refused.Message);
        Assert.Equal(1, OpenCount);
        Assert.DoesNotContain(_calls.Operations, operation => operation.StartsWith("reopen", StringComparison.Ordinal));
        Assert.Equal(
            "  reopen on " + Path + " refused: the earlier WinUsb_WritePipe on " + Path + ", given up on 3500 ms ago, has not returned; every call to " + Path + " fails fast until it does (logged once until then)",
            Assert.Single(_log.Messages));

        // The stuck write returns; the next reopen the backoff offers goes ahead.
        _clock.NowMilliseconds = 9000;
        Assert.Throws<OperationCanceledException>(() => Assert.Single(_calls.Abandoned)(FakeDeviceCallRunner.Cancelled));
        for (int i = 0; i < 10; i++) {
            Assert.Throws<IOException>(() => transport.Write(Packet(3)));
        }

        transport.Write(Packet(4));
        Assert.Equal(1, transport.Generation);
        Assert.Equal(
            new[] {
                "  WinUsb_WritePipe on " + Path + " returned 8000 ms after it was given up on; calls to " + Path + " resume (1 refused meanwhile)",
                "  reopened " + Path + " after 12 faulted transfer(s)",
            },
            _log.Messages.Skip(1));
    }

    [Fact]
    public void Write_OutlivingItsBound_WhenTheAbortIsGivenUpOnAfterTheFirstPipe_DoesNotAbortTheSecond() {
        using WinUsbTransport transport = Open();
        _calls.TimesOut = operation => operation.StartsWith("WinUsb_WritePipe", StringComparison.Ordinal);
        _api.OnCall = call => {
            if (call == "AbortPipe interface0 0x01") {
                _calls.AbandonRunning();
            }
        };

        IOException failure = Assert.Throws<IOException>(() => transport.Write(Packet(1)));

        Assert.Equal(
            "WinUsb_WritePipe timed out after 1000 ms; pipe abort timed out after 500 ms, pending I/O cancelled; handle faulted.",
            failure.Message);
        Assert.Equal(new[] { "AbortPipe interface0 0x01", "CancelPendingIo device0" }, _api.Calls.Skip(4));
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0x81)]
    public void Write_OutlivingItsBound_WhenAnAbortFails_CancelsTheHandlesIo(byte pipe) {
        using WinUsbTransport transport = Open();
        _calls.TimesOut = operation => operation.StartsWith("WinUsb_WritePipe", StringComparison.Ordinal);
        _api.AbortErrors[pipe] = 31;

        IOException failure = Assert.Throws<IOException>(() => transport.Write(Packet(1)));

        Assert.Equal(
            "WinUsb_WritePipe timed out after 1000 ms; pipe abort failed (error 31), pending I/O cancelled; handle faulted.",
            failure.Message);
        Assert.Equal("CancelPendingIo device0", _api.Calls.Last());
    }

    [Fact]
    public void Write_OutlivingItsBound_WhenTheAbortStallsToo_CancelsTheHandlesIo() {
        using WinUsbTransport transport = Open();
        _calls.TimesOut = operation => operation.StartsWith("WinUsb_", StringComparison.Ordinal);

        IOException failure = Assert.Throws<IOException>(() => transport.Write(Packet(1)));

        Assert.Equal(
            "WinUsb_WritePipe timed out after 1000 ms; pipe abort timed out after 500 ms, pending I/O cancelled; handle faulted.",
            failure.Message);
    }

    [Fact]
    public void Write_OutlivingItsBound_WhenNothingCancels_SaysSo() {
        using WinUsbTransport transport = Open();
        _calls.TimesOut = operation => operation.StartsWith("WinUsb_", StringComparison.Ordinal);
        _api.CancelError = 6;

        IOException failure = Assert.Throws<IOException>(() => transport.Write(Packet(1)));

        Assert.Equal(
            "WinUsb_WritePipe timed out after 1000 ms; pipe abort timed out after 500 ms, cancel of pending I/O failed (error 6); handle faulted.",
            failure.Message);
    }

    [Fact]
    public void Write_NullReport_Throws() {
        using WinUsbTransport transport = Open();

        Assert.Throws<ArgumentNullException>(() => transport.Write(null!));
    }

    [Fact]
    public void Read_GathersPacketsUntilTheDongleStopsSending_AndZeroPadsTheRest() {
        using WinUsbTransport transport = Open();
        _api.Replies.Enqueue((Packet(0x10), 0));
        _api.Replies.Enqueue((Packet(0x20), 0));

        byte[] reply = transport.Read(200);

        Assert.Equal(200, reply.Length);
        Assert.Equal(Packet(0x10), reply.Take(64));
        Assert.Equal(Packet(0x20), reply.Skip(64).Take(64));
        Assert.All(reply.Skip(128), b => Assert.Equal(0, b));
        Assert.Equal(3, _api.Calls.Count(call => call.StartsWith("ReadPipe", StringComparison.Ordinal)));
        // Four packets asked for and eight more of drain allowance: a pipe timeout each, one more to end
        // the reply, and the margin.
        Assert.Equal(1150, _calls.TimeoutOf("WinUsb_ReadPipe on " + Path));
        Assert.Empty(_log.Messages);
    }

    [Fact]
    public void Read_AReplyThatEndsWhereItWasAskedTo_StillReadsOnUntilTheDongleIsQuiet() {
        using WinUsbTransport transport = Open();
        _api.Replies.Enqueue((Packet(0x10), 0));

        Assert.Equal(Packet(0x10), transport.Read(64));

        Assert.Equal(2, _api.Calls.Count(call => call.StartsWith("ReadPipe", StringComparison.Ordinal)));
        Assert.Equal(1000, _calls.TimeoutOf("WinUsb_ReadPipe on " + Path));
        Assert.Empty(_log.Messages);
    }

    [Fact]
    public void Read_DrainsTheReplyToItsEnd_KeepingTheLengthAskedFor_AndLogsTheSurplusOncePerRun() {
        using WinUsbTransport transport = Open();
        _api.Replies.Enqueue((Packet(0x10), 0));
        _api.Replies.Enqueue((Packet(0x20), 0));
        _api.Replies.Enqueue((Packet(0x30), 0));

        byte[] reply = transport.Read(100);

        Assert.Equal(Packet(0x10).Concat(Packet(0x20).Take(36)), reply);
        Assert.Empty(_api.Replies);
        Assert.Equal(4, _api.Calls.Count(call => call.StartsWith("ReadPipe", StringComparison.Ordinal)));
        Assert.Equal(
            "  WinUsb_ReadPipe on " + Path + ": 92 byte(s) arrived beyond the length asked for and were discarded (logged once until a read ends where it was asked to)",
            Assert.Single(_log.Messages));

        // Another long reply straight after is the same run; a reply that fits ends it.
        _api.Replies.Enqueue((Packet(0x40), 0));
        _api.Replies.Enqueue((Packet(0x50), 0));
        Assert.Equal(Packet(0x40), transport.Read(64));
        Assert.Single(_log.Messages);
        _api.Replies.Enqueue((Packet(0x60), 0));
        Assert.Equal(Packet(0x60), transport.Read(64));
        _api.Replies.Enqueue((Packet(0x70), 0));
        _api.Replies.Enqueue((Packet(0x80), 0));
        Assert.Equal(Packet(0x70), transport.Read(64));
        Assert.Equal(2, _log.Messages.Count);
    }

    [Fact]
    public void Read_ADongleThatNeverStopsSending_IsReadNoFurtherThanTheDrainAllowance() {
        using WinUsbTransport transport = Open();
        for (int i = 0; i < 20; i++) {
            _api.Replies.Enqueue((Packet((byte)(i * 2)), 0));
        }

        byte[] reply = transport.Read(64);

        Assert.Equal(Packet(0x00), reply);
        Assert.Equal(9, _api.Calls.Count(call => call.StartsWith("ReadPipe", StringComparison.Ordinal)));
        Assert.Equal(11, _api.Replies.Count);
        Assert.Contains("512 byte(s) arrived beyond the length asked for", Assert.Single(_log.Messages));
    }

    [Fact]
    public void Read_GivenUpOnWhileAPacketIsInFlight_ReadsNoFurtherPacket() {
        using WinUsbTransport transport = Open();
        _api.Replies.Enqueue((Packet(0x10), 0));
        _api.Replies.Enqueue((Packet(0x20), 0));
        _api.OnCall = call => {
            if (call.StartsWith("ReadPipe", StringComparison.Ordinal)) {
                _calls.AbandonRunning();
            }
        };

        IOException failure = Assert.Throws<IOException>(() => transport.Read(128));

        Assert.Equal(
            "WinUsb_ReadPipe timed out after 1050 ms; pending transfers aborted; dongle unresponsive, handle faulted.",
            failure.Message);
        Assert.Single(_api.Calls, call => call.StartsWith("ReadPipe", StringComparison.Ordinal));
        Assert.Single(_api.Replies);
    }

    [Fact]
    public void Read_AZeroLengthPacket_EndsTheReply() {
        using WinUsbTransport transport = Open();
        _api.Replies.Enqueue((Packet(0x10), 0));
        _api.Replies.Enqueue((Array.Empty<byte>(), 0));
        _api.Replies.Enqueue((Packet(0x30), 0));

        byte[] reply = transport.Read(192);

        Assert.All(reply.Skip(64), b => Assert.Equal(0, b));
        Assert.Single(_api.Replies);
    }

    // L-Connect's ReadAll returns zeros for a reply that did not come and nothing is closed: a
    // missed reply is not a fault. Only a write failing marks the dongle for a reopen.
    [Fact]
    public void Read_NothingAfterARequest_FailsWithoutFaulting() {
        using WinUsbTransport transport = Open();
        transport.Write(Packet(0x11));

        IOException failure = Assert.Throws<IOException>(() => transport.Read(64));

        Assert.Equal("WinUSB interrupt-IN read returned no data.", failure.Message);
        transport.Write(Packet(0x11));
        Assert.Equal(0, transport.Generation);
        Assert.Empty(_log.Messages);
    }

    [Fact]
    public void Read_ATransferError_FaultsTheHandle() {
        using WinUsbTransport transport = Open();
        _api.Replies.Enqueue((Packet(0x10), 0));
        _api.Replies.Enqueue((null, 31));

        IOException failure = Assert.Throws<IOException>(() => transport.Read(128));

        Assert.Equal("WinUsb_ReadPipe failed (error 31); handle faulted.", failure.Message);
        _api.Replies.Enqueue((Packet(0x10), 0));
        _ = transport.Read(64);
        Assert.Equal(1, transport.Generation);
    }

    [Fact]
    public void Read_OutlivingItsBound_AbortsAndFaults() {
        using WinUsbTransport transport = Open();
        _calls.TimesOut = operation => operation.StartsWith("WinUsb_ReadPipe", StringComparison.Ordinal);

        IOException failure = Assert.Throws<IOException>(() => transport.Read(64));

        Assert.Equal(
            "WinUsb_ReadPipe timed out after 1000 ms; pending transfers aborted; dongle unresponsive, handle faulted.",
            failure.Message);
        Assert.Empty(_delay.Waits);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Read_NothingAskedFor_Throws(int length) {
        using WinUsbTransport transport = Open();

        Assert.Throws<ArgumentOutOfRangeException>(() => transport.Read(length));
    }

    [Fact]
    public void Reopen_ReleasesTheStaleHandles_BeforeOpeningAgain() {
        using WinUsbTransport transport = Open();
        Fault(transport);

        transport.Write(Packet(2));

        Assert.Equal(1, _api.Interfaces[0].Releases);
        Assert.Equal(1, _api.Devices[0].Releases);
        Assert.Equal(0, _api.Interfaces[1].Releases);
        Assert.Equal("WritePipe interface1 0x01 64", _api.Calls.Last());
        Assert.Equal(2000, _calls.TimeoutOf("reopen on " + Path));
    }

    [Fact]
    public void Reopen_ThatFails_IsRetriedOnTheBackoff_NotOnEveryTransfer() {
        using WinUsbTransport transport = Open();
        Fault(transport);
        _api.OpenDeviceErrors.Enqueue(2);

        IOException failure = Assert.Throws<IOException>(() => transport.Write(Packet(2)));
        Assert.Equal(
            "WinUsb_WritePipe skipped: reopen of " + Path + " failed: Failed to open WinUSB device at " + Path + " (error 2).",
            failure.Message);

        // The first retry waits out ten faulted transfers, refused fast without touching the device.
        for (int i = 0; i < 10; i++) {
            IOException skipped = Assert.Throws<IOException>(() => transport.Read(64));
            Assert.Equal("WinUsb_ReadPipe skipped: WinUSB handle faulted, reopen pending.", skipped.Message);
        }

        Assert.Equal(2, OpenCount);
        transport.Write(Packet(3));
        Assert.Equal(3, OpenCount);
        Assert.Equal("  reopened " + Path + " after 12 faulted transfer(s)", Assert.Single(_log.Messages));
    }

    [Fact]
    public void Reopen_OutlivingItsBound_StaysFaulted_AndALateOpenIsDisposedNotLeaked() {
        using WinUsbTransport transport = Open();
        Fault(transport);
        _calls.TimesOut = operation => operation.StartsWith("reopen", StringComparison.Ordinal);

        IOException failure = Assert.Throws<IOException>(() => transport.Write(Packet(2)));

        Assert.Equal(
            "WinUsb_WritePipe skipped: reopen of " + Path + " timed out after 2000 ms; device not answering Windows, handle still faulted.",
            failure.Message);
        // The deadline landed while the open's last native call was in flight: it finishes, and the
        // handoff releases what it opened.
        Assert.Single(_calls.Abandoned)(CancellationToken.None);
        Assert.Equal(1, _api.Interfaces[1].Releases);
        Assert.Equal(1, _api.Devices[1].Releases);
        Assert.Equal(0, transport.Generation);
    }

    [Fact]
    public void Reopen_GivenUpOnBeforeItStarted_ReleasesTheStaleHandles_AndOpensNothing() {
        using WinUsbTransport transport = Open();
        Fault(transport);
        _calls.TimesOut = operation => operation.StartsWith("reopen", StringComparison.Ordinal);
        Assert.Throws<IOException>(() => transport.Write(Packet(2)));

        Assert.Throws<OperationCanceledException>(() => Assert.Single(_calls.Abandoned)(FakeDeviceCallRunner.Cancelled));

        Assert.Equal(1, _api.Interfaces[0].Releases);
        Assert.Equal(1, OpenCount);
    }

    [Fact]
    public void Reopen_ThatFinishesAtTheDeadline_IsTaken() {
        using WinUsbTransport transport = Open();
        Fault(transport);
        _calls.FinishesAtDeadline = operation => operation.StartsWith("reopen", StringComparison.Ordinal);

        transport.Write(Packet(2));

        Assert.Equal(1, transport.Generation);
    }

    [Fact]
    public void Dispose_ReleasesBothHandles_Once() {
        WinUsbTransport transport = Open();

        transport.Dispose();
        transport.Dispose();

        Assert.Equal(1, _api.Interfaces[0].Releases);
        Assert.Equal(1, _api.Devices[0].Releases);
        Assert.Single(_calls.Operations, operation => operation == "close on " + Path);
        Assert.Equal(2000, _calls.TimeoutOf("close"));
        Assert.Empty(_log.Messages);
    }

    [Fact]
    public void Dispose_WhoseCloseThrows_IsLogged_NeverThrown_AndStillReleasesTheDevice() {
        WinUsbTransport transport = Open();
        _api.Interfaces[0].ReleaseFailure = new InvalidOperationException("free refused");

        transport.Dispose();

        Assert.Equal(
            "  close of " + Path + " failed: InvalidOperationException: free refused; handles left for Windows to reclaim",
            Assert.Single(_log.Messages));
        Assert.Equal(1, _api.Devices[0].Releases);
    }

    [Fact]
    public void Dispose_OutlivingItsBound_IsLogged() {
        WinUsbTransport transport = Open();
        _calls.TimesOut = operation => operation.StartsWith("close", StringComparison.Ordinal);

        transport.Dispose();

        Assert.Equal(
            "  close of " + Path + " timed out after 2000 ms; handles left for Windows to reclaim",
            Assert.Single(_log.Messages));
    }
}
