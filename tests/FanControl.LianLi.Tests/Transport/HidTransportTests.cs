using System;
using System.IO;
using System.Linq;
using System.Threading;
using FanControl.LianLi.Tests.Fakes;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

/// <summary>
/// The HID transport's decisions, driven through a scripted hid.dll and kernel32 and a rule-based
/// bound: the stream and control-handle open, report padding and pacing, how long an overlapped
/// transfer is waited for and what its cancel becomes, which failures fault the handle, the reopen on
/// the backoff, a call given up on making no further native call, and a close that can never throw.
/// </summary>
public class HidTransportTests {
    private const string Path = @"\\?\hid#vid_0416&pid_7372&mi_01#fake";

    private readonly FakeHidApi _hid = new FakeHidApi();
    private readonly FakeDeviceCallRunner _calls = new FakeDeviceCallRunner();
    private readonly FakeDeviceCallClock _clock = new FakeDeviceCallClock();
    private readonly FakeTransferDelay _delay = new FakeTransferDelay();
    private readonly FakeLogger _log = new FakeLogger();
    private readonly DeviceCallGate _gate;

    private HidInterface _device = Interface(new HidCapabilities(0xFF1B, 64, 64, 33));

    public HidTransportTests() {
        _gate = new DeviceCallGate(_calls, _clock, _log);
    }

    private static HidInterface Interface(HidCapabilities capabilities)
        => new HidInterface(0x0416, 0x7372, Path, capabilities);

    private HidTransport Open() => HidTransport.Open(_device, _hid, _log, _gate, _delay, CancellationToken.None);

    private FakeSafeHandle Stream(int index = 0) => _hid.HandlesOf("stream")[index];

    private FakeSafeHandle Control(int index = 0) => _hid.HandlesOf("control")[index];

    private void Fault(HidTransport transport) {
        _hid.TransferResults.Enqueue(1167);
        Assert.Throws<IOException>(() => transport.SetFeature(new byte[] { 0xE0 }));
    }

    [Fact]
    public void Open_OpensAStreamWithDeepInputBuffers_AndAControlHandleBesideIt() {
        using HidTransport transport = Open();

        Assert.Equal(
            new[] { "OpenStreamHandle " + Path, "HidD_SetNumInputBuffers stream 0 512", "OpenControlHandle " + Path },
            _hid.Calls);
        Assert.True(transport.CanWrite);
        Assert.Equal(0, transport.Generation);
    }

    [Fact]
    public void Open_StreamRefused_Throws_AndOpensNothingElse() {
        _hid.StreamOpenErrors.Enqueue(5);

        IOException failure = Assert.Throws<IOException>(Open);

        Assert.Equal("Failed to open HID stream at " + Path + " (error 5).", failure.Message);
        Assert.Single(_hid.Calls);
    }

    [Fact]
    public void Open_InputBuffersRefused_ClosesTheStream() {
        _hid.InputBufferError = 87;

        IOException failure = Assert.Throws<IOException>(Open);

        Assert.Equal("HidD_SetNumInputBuffers failed for " + Path + " (error 87).", failure.Message);
        Assert.Equal(1, Stream().Releases);
        Assert.Empty(_hid.HandlesOf("control"));
    }

    [Fact]
    public void Open_ControlHandleRefused_ClosesTheStream() {
        _hid.ControlOpenErrors.Enqueue(5);

        IOException failure = Assert.Throws<IOException>(Open);

        Assert.Equal("Failed to open HID input handle at " + Path + " (error 5).", failure.Message);
        Assert.Equal(1, Stream().Releases);
    }

    [Theory]
    [InlineData("OpenStreamHandle")]
    [InlineData("HidD_SetNumInputBuffers")]
    public void Open_GivenUpOnDuringAStep_MakesNoFurtherCall_AndReleasesWhatItOpened(string blocked) {
        using var abandonment = new CancellationTokenSource();
        _hid.OnCall = call => {
            if (call.StartsWith(blocked, StringComparison.Ordinal)) {
                abandonment.Cancel();
            }
        };

        Assert.Throws<OperationCanceledException>(
            () => HidTransport.Open(_device, _hid, _log, _gate, _delay, abandonment.Token));

        Assert.StartsWith(blocked, _hid.Calls.Last(), StringComparison.Ordinal);
        Assert.Equal(1, Stream().Releases);
        Assert.Empty(_hid.HandlesOf("control"));
    }

    [Fact]
    public void Open_AlreadyGivenUpOn_OpensNothing() {
        Assert.Throws<OperationCanceledException>(
            () => HidTransport.Open(_device, _hid, _log, _gate, _delay, FakeDeviceCallRunner.Cancelled));

        Assert.Empty(_hid.Calls);
    }

    [Fact]
    public void Open_ValidatesItsArguments() {
        CancellationToken none = CancellationToken.None;
        Assert.Throws<ArgumentNullException>(() => HidTransport.Open(null!, _hid, _log, _gate, _delay, none));
        Assert.Throws<ArgumentNullException>(() => HidTransport.Open(_device, null!, _log, _gate, _delay, none));
        Assert.Throws<ArgumentNullException>(() => HidTransport.Open(_device, _hid, null!, _gate, _delay, none));
        Assert.Throws<ArgumentNullException>(() => HidTransport.Open(_device, _hid, _log, null!, _delay, none));
        Assert.Throws<ArgumentNullException>(() => HidTransport.Open(_device, _hid, _log, _gate, null!, none));
    }

    [Fact]
    public void SetFeature_PadsAShortCommandToTheFeatureLength_OnTheControlHandle_ThenSettles() {
        using HidTransport transport = Open();

        transport.SetFeature(new byte[] { 0xE0, 0x10, 0x60 });

        byte[] sent = Assert.Single(_hid.Features);
        Assert.Equal(33, sent.Length);
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x60 }, sent.Take(3));
        Assert.All(sent.Skip(3), b => Assert.Equal(0, b));
        Assert.Equal("HidD_SetFeature " + Control() + " 33", _hid.Calls.Last());
        Assert.Equal(new[] { 20 }, _delay.Waits);
        Assert.Equal(500, _calls.TimeoutOf("HidD_SetFeature on " + Path));
    }

    [Fact]
    public void SetFeature_AReportAtOrPastTheFeatureLength_IsSentAsIs() {
        using HidTransport transport = Open();
        byte[] report = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();

        transport.SetFeature(report);

        Assert.Equal(report, Assert.Single(_hid.Features));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Open_AFeatureLengthThatIsNotPositive_FallsBackToTheInputReportLength(int reported) {
        _device = Interface(new HidCapabilities(0xFF1B, 64, 64, reported));
        using HidTransport transport = Open();

        transport.SetFeature(new byte[] { 0xE0 });

        Assert.Equal(65, Assert.Single(_hid.Features).Length);
    }

    [Theory]
    [InlineData(6)]    // ERROR_INVALID_HANDLE
    [InlineData(1167)] // ERROR_DEVICE_NOT_CONNECTED
    public void SetFeature_ADeviceGoneError_FaultsTheHandle(int error) {
        using HidTransport transport = Open();
        _hid.TransferResults.Enqueue(error);

        IOException failure = Assert.Throws<IOException>(() => transport.SetFeature(new byte[] { 0xE0 }));

        Assert.Equal($"HidD_SetFeature failed (error {error}); device gone, handle faulted.", failure.Message);
        transport.SetFeature(new byte[] { 0xE0 });
        Assert.Equal(1, transport.Generation);
        Assert.Equal("HidD_SetFeature " + Control(1) + " 33", _hid.Calls.Last());
    }

    [Fact]
    public void SetFeature_ARejectedCommand_FailsWithoutChurningTheHandle() {
        using HidTransport transport = Open();
        _hid.TransferResults.Enqueue(31);

        IOException failure = Assert.Throws<IOException>(() => transport.SetFeature(new byte[] { 0xE0 }));

        Assert.Equal("HidD_SetFeature failed (error 31).", failure.Message);
        transport.SetFeature(new byte[] { 0xE0 });
        Assert.Equal(0, transport.Generation);
        Assert.Single(_hid.HandlesOf("control"));
    }

    [Fact]
    public void SetFeature_OutlivingItsBound_CancelsTheTransferByHandle_AndFaults() {
        using HidTransport transport = Open();
        _calls.TimesOut = operation => operation.StartsWith("HidD_SetFeature", StringComparison.Ordinal);

        IOException failure = Assert.Throws<IOException>(() => transport.SetFeature(new byte[] { 0xE0 }));

        Assert.Equal(
            "HidD_SetFeature timed out after 500 ms; pending transfer cancelled; device unresponsive (re-enumerating?), handle faulted.",
            failure.Message);
        Assert.Equal("CancelIoEx " + Control(), _hid.Calls.Last());
        Assert.Empty(_delay.Waits);
    }

    [Fact]
    public void SetFeature_OutlivingItsBound_WhenTheCancelIsRefused_SaysSo() {
        using HidTransport transport = Open();
        _calls.TimesOut = operation => operation.StartsWith("HidD_SetFeature", StringComparison.Ordinal);
        _hid.CancelError = 1168;

        IOException failure = Assert.Throws<IOException>(() => transport.SetFeature(new byte[] { 0xE0 }));

        Assert.Contains("cancel of the pending transfer failed (error 1168)", failure.Message);
    }

    [Fact]
    public void SetFeature_GivenUpOnBeforeItStarted_NeverReachesTheDevice() {
        using HidTransport transport = Open();
        _calls.TimesOut = operation => operation.StartsWith("HidD_SetFeature", StringComparison.Ordinal);
        Assert.Throws<IOException>(() => transport.SetFeature(new byte[] { 0xE0 }));

        Assert.Throws<OperationCanceledException>(() => Assert.Single(_calls.Abandoned)(FakeDeviceCallRunner.Cancelled));

        Assert.Empty(_hid.Features);
    }

    [Fact]
    public void SetFeature_OutlivingItsBound_WhileTheStuckTransferHasNotReturned_OpensNothingMore_UntilItDoes() {
        // A transfer that ignores its cancels leaves its thread stuck. Every reopen the backoff offers
        // meanwhile fails fast without a thread or a CreateFile, the refusal is logged once, and the
        // first offer after the transfer returns reopens as normal.
        using HidTransport transport = Open();
        _calls.TimesOut = operation => operation.StartsWith("HidD_SetFeature", StringComparison.Ordinal);
        Assert.Throws<IOException>(() => transport.SetFeature(new byte[] { 0xE0 }));
        _calls.TimesOut = _ => false;
        _clock.NowMilliseconds = 1200;

        IOException refused = Assert.Throws<IOException>(() => transport.SetFeature(new byte[] { 0xE0 }));

        Assert.Equal(
            "HidD_SetFeature skipped: reopen of " + Path + " failed: reopen on " + Path + " not started: the earlier HidD_SetFeature on " + Path + ", given up on 1200 ms ago, has not returned.",
            refused.Message);
        for (int i = 0; i < 10; i++) {
            Assert.Throws<IOException>(() => transport.GetInputReport(0xE0, 65));
        }

        Assert.Throws<IOException>(() => transport.GetInputReport(0xE0, 65));
        Assert.Single(_hid.HandlesOf("stream"));
        Assert.DoesNotContain(_calls.Operations, operation => operation.StartsWith("reopen", StringComparison.Ordinal));
        Assert.Single(_log.Messages);

        Assert.Throws<OperationCanceledException>(() => Assert.Single(_calls.Abandoned)(FakeDeviceCallRunner.Cancelled));
        for (int i = 0; i < 20; i++) {
            Assert.Throws<IOException>(() => transport.GetInputReport(0xE0, 65));
        }

        transport.SetFeature(new byte[] { 0xE0 });
        Assert.Equal(1, transport.Generation);
        Assert.Equal(2, _hid.HandlesOf("stream").Count);
        Assert.Equal(
            "  HidD_SetFeature on " + Path + " returned 1200 ms after it was given up on; calls to " + Path + " resume (2 refused meanwhile)",
            _log.Messages[1]);
    }

    [Fact]
    public void Dispose_WhileACallIsStuck_StillCloses() {
        // A close only releases what the transport holds, so it is never refused: refused, its handles
        // would be left to the finalizer thread instead.
        HidTransport transport = Open();
        _calls.TimesOut = operation => operation.StartsWith("HidD_SetFeature", StringComparison.Ordinal);
        Assert.Throws<IOException>(() => transport.SetFeature(new byte[] { 0xE0 }));
        _calls.TimesOut = _ => false;

        transport.Dispose();

        Assert.Equal(1, Stream().Releases);
        Assert.Equal(1, Control().Releases);
        Assert.Empty(_log.Messages);
    }

    [Fact]
    public void SetFeature_NullReport_Throws() {
        using HidTransport transport = Open();

        Assert.Throws<ArgumentNullException>(() => transport.SetFeature(null!));
    }

    [Fact]
    public void GetInputReport_SelectsTheReportId_AndReturnsWhatTheDeviceFilled() {
        using HidTransport transport = Open();
        _hid.InputReports.Enqueue(new byte[] { 0xE0, 0x03, 0xE8 });

        byte[] report = transport.GetInputReport(0xE0, 65);

        Assert.Equal(65, report.Length);
        Assert.Equal(new byte[] { 0xE0, 0x03, 0xE8 }, report.Take(3));
        Assert.Equal("HidD_GetInputReport " + Control() + " 65", _hid.Calls.Last());
        Assert.Equal(500, _calls.TimeoutOf("HidD_GetInputReport(0xE0) on " + Path));
        Assert.Empty(_delay.Waits);
    }

    [Fact]
    public void GetInputReport_ADeviceGoneError_NamesTheReportId() {
        using HidTransport transport = Open();
        _hid.TransferResults.Enqueue(1167);

        IOException failure = Assert.Throws<IOException>(() => transport.GetInputReport(0xE0, 65));

        Assert.Equal("HidD_GetInputReport(0xE0) failed (error 1167); device gone, handle faulted.", failure.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void GetInputReport_WithNoRoomForTheReportId_Throws(int length) {
        using HidTransport transport = Open();

        Assert.Throws<ArgumentOutOfRangeException>(() => transport.GetInputReport(0xE0, length));
    }

    [Fact]
    public void Write_SendsOneWholeOutputReport_ZeroPadded_WaitingItsOwnDeadline_UnderTheStreamCallBound() {
        using HidTransport transport = Open();

        transport.Write(new byte[] { 0x01, 0x02 });

        byte[] sent = Assert.Single(_hid.Written);
        Assert.Equal(64, sent.Length);
        Assert.Equal(new byte[] { 0x01, 0x02 }, sent.Take(2));
        Assert.All(sent.Skip(2), b => Assert.Equal(0, b));
        Assert.Equal("WriteFile " + Stream() + " 64", _hid.Calls.Last());
        FakeHidTransfer transfer = Assert.Single(_hid.Started);
        Assert.Equal(new[] { 500 }, transfer.Waits);
        Assert.True(transfer.DisposedAfterCompletion);
        Assert.Equal(1000, _calls.TimeoutOf("WriteFile on " + Path));
    }

    [Fact]
    public void Write_AReportLongerThanTheOutputReport_IsRefused_WithoutFaulting() {
        using HidTransport transport = Open();

        ArgumentException failure = Assert.Throws<ArgumentException>(() => transport.Write(new byte[65]));

        Assert.StartsWith("A 65-byte report does not fit the 64-byte output report of " + Path + ".", failure.Message);
        Assert.Empty(_hid.Written);
        transport.Write(new byte[64]);
        Assert.Equal(0, transport.Generation);
    }

    [Fact]
    public void Write_OnAnInterfaceWithNoOutputReport_FailsWithoutFaulting() {
        _device = Interface(new HidCapabilities(0xFF1B, 64, 0, 33));
        using HidTransport transport = Open();

        IOException failure = Assert.Throws<IOException>(() => transport.Write(new byte[] { 0x01 }));

        Assert.Equal(Path + " has no output report to write.", failure.Message);
        transport.SetFeature(new byte[] { 0xE0 });
        Assert.Equal(0, transport.Generation);
    }

    [Fact]
    public void Write_NullReport_Throws() {
        using HidTransport transport = Open();

        Assert.Throws<ArgumentNullException>(() => transport.Write(null!));
    }

    [Fact]
    public void Write_ATransferThatFails_FaultsTheHandle() {
        using HidTransport transport = Open();
        _hid.Transfers.Enqueue(new FakeHidTransfer { Error = 31 });

        IOException failure = Assert.Throws<IOException>(() => transport.Write(new byte[] { 0x01 }));

        Assert.Equal("WriteFile failed, handle faulted: WriteFile failed (error 31)", failure.Message);
        transport.Write(new byte[] { 0x01 });
        Assert.Equal(1, transport.Generation);
        Assert.Equal("WriteFile " + Stream(1) + " 64", _hid.Calls.Last());
    }

    [Fact]
    public void Write_ATransferThatRunsOutItsWait_IsCancelled_ThenWaitedFor_AndFaults() {
        using HidTransport transport = Open();
        var transfer = new FakeHidTransfer { Pending = true };
        _hid.Transfers.Enqueue(transfer);

        IOException failure = Assert.Throws<IOException>(() => transport.Write(new byte[] { 0x01 }));

        Assert.Equal("WriteFile failed, handle faulted: WriteFile timed out after 500 ms", failure.Message);
        Assert.Equal(new[] { 500, 400 }, transfer.Waits);
        Assert.Equal(1, transfer.Cancels);
        Assert.True(transfer.DisposedAfterCompletion);
        transport.Write(new byte[] { 0x01 });
        Assert.Equal(1, transport.Generation);
    }

    [Fact]
    public void Write_ATransferThatCompletesAsItIsCancelled_IsKept() {
        using HidTransport transport = Open();
        _hid.Transfers.Enqueue(new FakeHidTransfer { Pending = true, FinishesAsCancelled = true });

        transport.Write(new byte[] { 0x01 });

        transport.Write(new byte[] { 0x01 });
        Assert.Equal(0, transport.Generation);
    }

    [Fact]
    public void Write_ATransferWhoseCancelDoesNotCompleteInTime_IsReleasedWhenTheKernelCompletesIt_AndFaults() {
        using HidTransport transport = Open();
        var transfer = new FakeHidTransfer { Pending = true, CancelCompletes = false };
        _hid.Transfers.Enqueue(transfer);

        IOException failure = Assert.Throws<IOException>(() => transport.Write(new byte[] { 0x01 }));

        Assert.Equal(
            "WriteFile failed, handle faulted: WriteFile timed out after 500 ms and was cancelled, but the driver did not complete it within 400 ms more",
            failure.Message);
        Assert.False(transfer.DisposedAfterCompletion);
        Assert.True(transfer.ReleasedWhenComplete);
    }

    // A transfer the driver never completes counts as a call still out on the device: nothing more
    // is started on it - no reopen, no fresh request the driver would not finish either - until it does.
    [Fact]
    public void AnUncompletedTransfer_HoldsTheDevice_UntilTheKernelCompletesIt() {
        using HidTransport transport = Open();
        var transfer = new FakeHidTransfer { Pending = true, CancelCompletes = false };
        _hid.Transfers.Enqueue(transfer);
        Assert.Throws<IOException>(() => transport.Write(new byte[] { 0x01 }));

        IOException held = Assert.Throws<IOException>(() => transport.Write(new byte[] { 0x01 }));
        Assert.Contains("which the driver has not completed", held.Message);

        transfer.Released!();
        Assert.True(_gate.TryRun(_device.DevicePath, "probe", _ => { }, 500, () => { }));
        Assert.Throws<ArgumentNullException>(() => _gate.HoldUntilComplete(null!, "x"));
        Assert.Throws<ArgumentNullException>(() => _gate.HoldUntilComplete("x", null!));
    }

    [Fact]
    public void Write_ATransferWhoseCancelIsRefused_SaysSo() {
        using HidTransport transport = Open();
        _hid.Transfers.Enqueue(new FakeHidTransfer { Pending = true, CancelCompletes = false, CancelError = 1168 });

        IOException failure = Assert.Throws<IOException>(() => transport.Write(new byte[] { 0x01 }));

        Assert.Contains("WriteFile timed out after 500 ms and its cancel failed (error 1168)", failure.Message);
    }

    [Fact]
    public void Write_AnyOtherFailure_PropagatesWithoutFaulting() {
        using HidTransport transport = Open();
        _hid.BeginFailure = new ObjectDisposedException("stream");

        Assert.Throws<ObjectDisposedException>(() => transport.Write(new byte[] { 0x01 }));

        _hid.BeginFailure = null;
        transport.Write(new byte[] { 0x01 });
        Assert.Equal(0, transport.Generation);
    }

    [Fact]
    public void Write_OutlivingTheStreamCallBound_CancelsTheStreamsIo_AndFaults() {
        using HidTransport transport = Open();
        _calls.TimesOut = operation => operation.StartsWith("WriteFile", StringComparison.Ordinal);

        IOException failure = Assert.Throws<IOException>(() => transport.Write(new byte[] { 0x01 }));

        Assert.Equal(
            "WriteFile did not return within 1000 ms; pending transfer cancelled; device unresponsive, handle faulted.",
            failure.Message);
        Assert.Equal("CancelIoEx " + Stream(), _hid.Calls.Last());

        // Given up on before it started, the abandoned write never reaches the device.
        Assert.Throws<OperationCanceledException>(() => Assert.Single(_calls.Abandoned)(FakeDeviceCallRunner.Cancelled));
        Assert.Empty(_hid.Written);
    }

    [Fact]
    public void Read_ReadsAWholeInputReport_AndReturnsTheLengthAskedFor() {
        using HidTransport transport = Open();
        _hid.Transfers.Enqueue(new FakeHidTransfer { Reply = Enumerable.Range(1, 64).Select(i => (byte)i).ToArray() });

        byte[] reply = transport.Read(8);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, reply);
        Assert.Equal("ReadFile " + Stream() + " 64", _hid.Calls.Last());
        Assert.Equal(1000, _calls.TimeoutOf("ReadFile on " + Path));
    }

    [Fact]
    public void Read_AskingForMoreThanAReport_ZeroPadsPastWhatArrived() {
        using HidTransport transport = Open();
        _hid.Transfers.Enqueue(new FakeHidTransfer { Reply = new byte[] { 0x01, 0xAA } });

        byte[] reply = transport.Read(100);

        Assert.Equal(100, reply.Length);
        Assert.Equal(new byte[] { 0x01, 0xAA }, reply.Take(2));
        Assert.All(reply.Skip(2), b => Assert.Equal(0, b));
        Assert.Equal("ReadFile " + Stream() + " 100", _hid.Calls.Last());
    }

    [Fact]
    public void Read_NoData_FailsWithoutFaulting() {
        using HidTransport transport = Open();

        IOException failure = Assert.Throws<IOException>(() => transport.Read(64));

        Assert.Equal("HID interrupt-IN read returned no data.", failure.Message);
        transport.Write(new byte[] { 0x01 });
        Assert.Equal(0, transport.Generation);
    }

    [Fact]
    public void Read_OnAnInterfaceWithNoInputReport_FailsWithoutFaulting() {
        _device = Interface(new HidCapabilities(0xFF1B, 0, 64, 33));
        using HidTransport transport = Open();

        IOException failure = Assert.Throws<IOException>(() => transport.Read(64));

        Assert.Equal(Path + " has no input report to read.", failure.Message);
        Assert.Empty(_hid.Started);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Read_NothingAskedFor_Throws(int length) {
        using HidTransport transport = Open();

        Assert.Throws<ArgumentOutOfRangeException>(() => transport.Read(length));
    }

    [Fact]
    public void Read_ATransferThatFails_FaultsTheHandle() {
        using HidTransport transport = Open();
        _hid.Transfers.Enqueue(new FakeHidTransfer { Error = 1167 });

        IOException failure = Assert.Throws<IOException>(() => transport.Read(64));

        Assert.Equal("ReadFile failed, handle faulted: ReadFile failed (error 1167)", failure.Message);
        transport.Write(new byte[] { 0x01 });
        Assert.Equal(1, transport.Generation);
    }

    [Fact]
    public void Read_ADeviceThatDoesNotAnswer_TimesOut_AndFaults() {
        using HidTransport transport = Open();
        _hid.Transfers.Enqueue(new FakeHidTransfer { Pending = true });

        IOException failure = Assert.Throws<IOException>(() => transport.Read(64));

        Assert.Equal("ReadFile failed, handle faulted: ReadFile timed out after 500 ms", failure.Message);
    }

    [Fact]
    public void Reopen_ReleasesTheStaleHandles_ThenOpensBothAgain() {
        using HidTransport transport = Open();
        Fault(transport);
        int before = _hid.Calls.Count;

        transport.SetFeature(new byte[] { 0xE0 });

        Assert.Equal(1, Stream(0).Releases);
        Assert.Equal(1, Control(0).Releases);
        Assert.Equal(
            new[] {
                "OpenStreamHandle " + Path,
                "HidD_SetNumInputBuffers " + Stream(1) + " 512",
                "OpenControlHandle " + Path,
                "HidD_SetFeature " + Control(1) + " 33",
            },
            _hid.Calls.Skip(before));
        Assert.Equal(2000, _calls.TimeoutOf("reopen on " + Path));
        Assert.Equal("  reopened " + Path + " after 1 faulted transfer(s)", Assert.Single(_log.Messages));
    }

    [Fact]
    public void Reopen_ThatFails_IsRetriedOnTheBackoff_NotOnEveryTransfer() {
        using HidTransport transport = Open();
        Fault(transport);
        _hid.StreamOpenErrors.Enqueue(2);

        IOException failure = Assert.Throws<IOException>(() => transport.GetInputReport(0xE0, 65));
        Assert.Equal(
            "HidD_GetInputReport(0xE0) skipped: reopen of " + Path + " failed: Failed to open HID stream at " + Path + " (error 2).",
            failure.Message);

        for (int i = 0; i < 10; i++) {
            IOException skipped = Assert.Throws<IOException>(() => transport.Write(new byte[] { 0x01 }));
            Assert.Equal("WriteFile skipped: HID handle faulted, reopen pending.", skipped.Message);
        }

        Assert.Single(_hid.HandlesOf("stream"));
        _hid.Transfers.Enqueue(new FakeHidTransfer { Reply = new byte[] { 0x01 } });
        Assert.Equal(0x01, transport.Read(64)[0]);
        Assert.Equal(2, _hid.HandlesOf("stream").Count);
    }

    [Fact]
    public void Reopen_WhoseControlHandleIsRefused_ClosesTheNewStream_AndStaysFaulted() {
        using HidTransport transport = Open();
        Fault(transport);
        _hid.ControlOpenErrors.Enqueue(2);

        IOException failure = Assert.Throws<IOException>(() => transport.SetFeature(new byte[] { 0xE0 }));

        Assert.Equal(
            "HidD_SetFeature skipped: reopen of " + Path + " failed: Failed to open HID input handle at " + Path + " (error 2).",
            failure.Message);
        Assert.Equal(1, Stream(1).Releases);
        Assert.Equal(0, transport.Generation);
    }

    [Fact]
    public void Reopen_OutlivingItsBound_StaysFaulted_AndAnOpenThatFinishesLateIsDisposedNotLeaked() {
        using HidTransport transport = Open();
        Fault(transport);
        _calls.TimesOut = operation => operation.StartsWith("reopen", StringComparison.Ordinal);

        IOException failure = Assert.Throws<IOException>(() => transport.SetFeature(new byte[] { 0xE0 }));

        Assert.Equal(
            "HidD_SetFeature skipped: reopen of " + Path + " timed out after 2000 ms; device not answering Windows, handle still faulted.",
            failure.Message);

        // The deadline landed while the open's last native call was in flight: it finishes, and the
        // handoff releases what it opened.
        Assert.Single(_calls.Abandoned)(CancellationToken.None);
        Assert.Equal(1, Stream(1).Releases);
        Assert.Equal(1, Control(1).Releases);
        Assert.Equal(0, transport.Generation);
    }

    [Fact]
    public void Reopen_GivenUpOnBeforeItStarted_ReleasesTheStaleHandles_AndOpensNothing() {
        using HidTransport transport = Open();
        Fault(transport);
        _calls.TimesOut = operation => operation.StartsWith("reopen", StringComparison.Ordinal);
        Assert.Throws<IOException>(() => transport.SetFeature(new byte[] { 0xE0 }));

        Assert.Throws<OperationCanceledException>(() => Assert.Single(_calls.Abandoned)(FakeDeviceCallRunner.Cancelled));

        Assert.Equal(1, Stream(0).Releases);
        Assert.Single(_hid.HandlesOf("stream"));
    }

    [Fact]
    public void Reopen_GivenUpOnWhileTheStreamOpens_GoesNoFurther_AndReleasesTheNewStream() {
        using HidTransport transport = Open();
        Fault(transport);
        _hid.OnCall = call => {
            if (call.StartsWith("OpenStreamHandle", StringComparison.Ordinal)) {
                _calls.AbandonRunning();
            }
        };

        IOException failure = Assert.Throws<IOException>(() => transport.SetFeature(new byte[] { 0xE0 }));

        Assert.Contains("reopen of " + Path + " timed out after 2000 ms", failure.Message);
        Assert.StartsWith("OpenStreamHandle", _hid.Calls.Last(), StringComparison.Ordinal);
        Assert.Equal(1, Stream(1).Releases);
        Assert.Empty(_calls.LateFailures);
    }

    [Fact]
    public void Reopen_ThatFinishesAtTheDeadline_IsTaken() {
        using HidTransport transport = Open();
        Fault(transport);
        _calls.FinishesAtDeadline = operation => operation.StartsWith("reopen", StringComparison.Ordinal);

        transport.SetFeature(new byte[] { 0xE0 });

        Assert.Equal(1, transport.Generation);
    }

    [Fact]
    public void Dispose_ClosesTheStreamAndTheControlHandle_Once() {
        HidTransport transport = Open();

        transport.Dispose();
        transport.Dispose();

        Assert.Equal(1, Stream().Releases);
        Assert.Equal(1, Control().Releases);
        Assert.Single(_calls.Operations, operation => operation == "close on " + Path);
        Assert.Equal(2000, _calls.TimeoutOf("close"));
    }

    [Fact]
    public void Dispose_OutlivingItsBound_IsLogged() {
        HidTransport transport = Open();
        _calls.TimesOut = operation => operation.StartsWith("close", StringComparison.Ordinal);

        transport.Dispose();

        Assert.Equal(
            "  close of " + Path + " timed out after 2000 ms; handles left for Windows to reclaim",
            Assert.Single(_log.Messages));
    }

    [Fact]
    public void Dispose_WhoseCloseThrows_IsLogged_NeverThrown_AndStillReleasesTheOtherHandle() {
        HidTransport transport = Open();
        Stream().ReleaseFailure = new InvalidOperationException("close refused");

        transport.Dispose();

        Assert.Equal(
            "  close of " + Path + " failed: InvalidOperationException: close refused; handles left for Windows to reclaim",
            Assert.Single(_log.Messages));
        Assert.Equal(1, Control().Releases);
    }
}
