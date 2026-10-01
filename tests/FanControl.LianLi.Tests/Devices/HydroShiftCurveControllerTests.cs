using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Tests.Fakes;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

public class HydroShiftCurveControllerTests {
    private const int PumpChannel = 0;

    private static readonly byte[] SyncOff = { 0x64, 1, 0, 0, 0, 0, 0, 0 };

    private static (HydroShiftCurveController controller, FakeDeviceTransport transport, FakeClock clock, FakeLogger logger) NewController() {
        var transport = new FakeDeviceTransport();
        var clock = new FakeClock();
        var logger = new FakeLogger();
        var controller = new HydroShiftCurveController(0, transport, clock, logger);
        return (controller, transport, clock, logger);
    }

    // The status reply: the liquid temperature at byte 1, and 0 at byte 2 while the pump follows
    // the motherboard header.
    private static byte[] StatusReply(int temperature, bool followsMotherboard)
        => new[] { (byte)0x60, (byte)temperature, followsMotherboard ? (byte)0 : (byte)1 };

    // The tachometer reply: the raw count, big-endian at bytes 1-2.
    private static byte[] SpeedReply(int raw)
        => new[] { (byte)0x62, (byte)(raw >> 8), (byte)(raw & 0xFF) };

    private static void Poll(HydroShiftCurveController controller, FakeDeviceTransport transport, int temperature, bool followsMotherboard, int raw) {
        transport.ReadReplies.Enqueue(StatusReply(temperature, followsMotherboard));
        transport.ReadReplies.Enqueue(SpeedReply(raw));
        controller.PollRpm();
    }

    [Fact]
    public void Constructor_DoesNoIo() {
        var (controller, transport, _, _) = NewController();
        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.InterruptReadCount);
        Assert.Null(controller.GetTemperature(0));
        Assert.Equal(0f, controller.GetRpm(PumpChannel));
    }

    [Fact]
    public void AssertSoftwareControl_TakesThePumpOffTheHeader_AndTakesTheReply() {
        var (controller, transport, _, _) = NewController();

        controller.AssertSoftwareControl();

        Assert.Equal(new[] { SyncOff }, transport.Writes);
        Assert.Equal(1, transport.InterruptReadCount);
    }

    [Fact]
    public void ApplyPending_WritesTheOutputValueForTheDuty_TakesTheReply_AndLogsTheChange() {
        var (controller, transport, _, logger) = NewController();

        controller.SetTarget(PumpChannel, 50); // 2000 rpm, output 979 (0x03D3)
        controller.ApplyPending();

        Assert.Equal(new[] { new byte[] { 0x61, 0x03, 0xD3, 0, 0, 0, 0, 0 } }, transport.Writes);
        Assert.Equal(1, transport.InterruptReadCount);
        Assert.Contains("Set H0:pump = 50% (2000 rpm, output 979)", logger.Messages);
    }

    [Theory]
    [InlineData(0, 0x01, 0x1F)]   // 1600 rpm, output 287
    [InlineData(100, 0x06, 0x80)] // 2400 rpm, output 1664
    [InlineData(150, 0x06, 0x80)] // clamped to 100%
    public void ApplyPending_SpansTheOrdinaryRange(int duty, int high, int low) {
        var (controller, transport, _, _) = NewController();

        controller.SetTarget(PumpChannel, duty);
        controller.ApplyPending();

        Assert.Equal((byte)0x61, transport.Writes[0][0]);
        Assert.Equal((byte)high, transport.Writes[0][1]);
        Assert.Equal((byte)low, transport.Writes[0][2]);
    }

    [Fact]
    public void ApplyPending_UnassignedTarget_WritesNothing() {
        var (controller, transport, _, _) = NewController();
        controller.ApplyPending();
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public void ApplyPending_UnchangedWithinTwoSeconds_WritesNothing() {
        var (controller, transport, clock, _) = NewController();
        controller.SetTarget(PumpChannel, 50);
        controller.ApplyPending();
        transport.Clear();

        clock.Advance(TimeSpan.FromSeconds(1));
        controller.ApplyPending();

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public void ApplyPending_ResendsEveryTwoSeconds_WithoutLoggingTheResend() {
        var (controller, transport, clock, logger) = NewController();
        controller.SetTarget(PumpChannel, 50);
        controller.ApplyPending();
        transport.Clear();

        clock.Advance(HydroShiftCurveController.ResendInterval);
        controller.ApplyPending();

        Assert.Single(transport.Writes);
        Assert.Equal((byte)0x61, transport.Writes[0][0]);
        Assert.Single(logger.Messages, m => m.StartsWith("Set H0:pump", StringComparison.Ordinal));
    }

    [Fact]
    public void ReleaseChannel_StopsTheResend() {
        var (controller, transport, clock, _) = NewController();
        controller.SetTarget(PumpChannel, 80);
        controller.ApplyPending();
        controller.ReleaseChannel(PumpChannel);
        transport.Clear();

        clock.Advance(TimeSpan.FromSeconds(30));
        controller.ApplyPending();

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public void ApplyPending_AfterTransportReopened_ReplaysTheLook_ThenSoftwareControl_ThenThePump() {
        var (controller, transport, _, logger) = NewController();
        var replayedAt = new List<int>();
        controller.ReplayOnReconnect(() => {
            replayedAt.Add(transport.Writes.Count);
            return true;
        });
        controller.SetTarget(PumpChannel, 50);
        controller.ApplyPending();
        transport.Clear();

        // The transport lost and reopened the MCU (a wake): the registered replay runs first, then
        // the pump is taken off the header again and the unchanged duty re-sent now - it may have reset.
        transport.Generation = 1;
        controller.ApplyPending();

        Assert.Equal(new[] { 0 }, replayedAt);
        Assert.Equal(2, transport.Writes.Count);
        Assert.Equal(SyncOff, transport.Writes[0]);
        Assert.Equal((byte)0x61, transport.Writes[1][0]);
        Assert.Contains("H0 reconnected: setup replayed (transport generation 1)", logger.Messages);
    }

    // While the handle is still faulted the MCU is off the bus: nothing is replayed, and the pump
    // write goes out as usual, being what reopens the MCU on the backoff. The replay and software
    // control follow once it is back.
    [Fact]
    public void ApplyPending_WhileTheTransportIsStillFaulted_ReplaysNothing_UntilTheMcuIsBack() {
        var (controller, transport, _, logger) = NewController();
        int replays = 0;
        controller.ReplayOnReconnect(() => {
            replays++;
            return true;
        });
        controller.SetTarget(PumpChannel, 50);
        controller.ApplyPending();
        transport.Clear();

        transport.Generation = 1;
        transport.IsFaulted = true;
        controller.ApplyPending();

        Assert.Equal(0, replays);
        Assert.Empty(transport.Writes);
        Assert.DoesNotContain(logger.Messages, m => m.Contains("reconnected"));

        transport.IsFaulted = false;
        controller.ApplyPending();

        Assert.Equal(1, replays);
        Assert.Equal(SyncOff, transport.Writes[0]);
        Assert.Equal((byte)0x61, transport.Writes[1][0]);
        Assert.Contains("H0 reconnected: setup replayed (transport generation 1)", logger.Messages);
    }

    // Registered work the MCU refused stays owed on its own: software control and the pump are
    // replayed regardless, and the work is tried again on the keepalive cadence.
    [Fact]
    public void ApplyPending_ReplayedWorkTheMcuRefused_StaysOwed_AndIsTriedAgainOnTheKeepaliveCadence() {
        var (controller, transport, clock, _) = NewController();
        int replays = 0;
        controller.ReplayOnReconnect(() => ++replays >= 2);
        controller.SetTarget(PumpChannel, 50);
        controller.ApplyPending();
        transport.Clear();

        transport.Generation = 1;
        controller.ApplyPending();
        Assert.Equal(1, replays);
        Assert.Equal(2, transport.Writes.Count);

        controller.ApplyPending();
        Assert.Equal(1, replays);
        clock.Advance(ChannelWriteDecision.RefreshInterval);
        controller.ApplyPending();
        Assert.Equal(2, replays);
    }

    [Fact]
    public void ApplyPending_AfterAReopen_WithNoReplayRegistered_AssertsSoftwareControlAndResendsThePump() {
        var (controller, transport, _, _) = NewController();
        controller.SetTarget(PumpChannel, 50);
        controller.ApplyPending();
        transport.Clear();

        transport.Generation = 1;
        controller.ApplyPending();

        Assert.Equal(2, transport.Writes.Count);
        Assert.Equal(SyncOff, transport.Writes[0]);
    }

    [Fact]
    public void ApplyPending_AfterAReopen_WhoseAssertFails_RetriesTheWholeReplayNextTick() {
        var (controller, transport, _, logger) = NewController();
        controller.SetTarget(PumpChannel, 50);
        controller.ApplyPending();
        transport.Clear();
        transport.Generation = 1;
        transport.FailWrite = report => report[0] == 0x64;

        Assert.Throws<IOException>(controller.ApplyPending);
        Assert.DoesNotContain(logger.Messages, m => m.Contains("reconnected"));

        transport.FailWrite = null;
        controller.ApplyPending();

        Assert.Equal(SyncOff, transport.Writes[0]);
        Assert.Equal((byte)0x61, transport.Writes[1][0]);
        Assert.Single(logger.Messages, m => m.Contains("H0 reconnected"));
    }

    [Fact]
    public void PollRpm_ReadsTheStatusThenTheTachometer_AndCachesTemperatureAndCorrectedRpm() {
        var (controller, transport, _, _) = NewController();

        Poll(controller, transport, 34, followsMotherboard: false, raw: 2050);

        Assert.Equal(new byte[] { 0x60, 0, 0, 0, 0, 0, 0, 0 }, transport.Writes[0]);
        Assert.Equal(new byte[] { 0x62, 0, 0, 0, 0, 0, 0, 0 }, transport.Writes[1]);
        Assert.Equal(2, transport.InterruptReadCount);
        Assert.Equal(34f, controller.GetTemperature(0));
        Assert.Equal(2000f, controller.GetRpm(PumpChannel)); // 2050 less the middle band's 50
    }

    [Fact]
    public void PollRpm_IgnoresAnImplausibleReading_AndKeepsTheLastGood() {
        var (controller, transport, _, _) = NewController();
        Poll(controller, transport, 30, false, 1850);

        Poll(controller, transport, 30, false, 50000); // a garbage count, still 49970 after correction

        Assert.Equal(1800f, controller.GetRpm(PumpChannel));
    }

    [Fact]
    public void PollRpm_LogsImplausibleOnsetOnce_ThenRecovery() {
        var (controller, transport, _, logger) = NewController();

        Poll(controller, transport, 30, false, 1850);
        Poll(controller, transport, 30, false, 50000);
        Poll(controller, transport, 30, false, 50000);
        Poll(controller, transport, 30, false, 2450);

        Assert.Equal(2400f, controller.GetRpm(PumpChannel));
        Assert.Single(logger.Messages, m => m.Contains("H0:pump implausible rpm 49970 ignored, keeping 1800"));
        Assert.Single(logger.Messages, m => m.Contains("H0:pump rpm recovered (2400)"));
    }

    [Fact]
    public void PollRpm_UnderSoftwareControlFromTheFirstReply_LogsNothingAboutSync() {
        var (controller, transport, _, logger) = NewController();

        Poll(controller, transport, 30, followsMotherboard: false, raw: 1850);
        Poll(controller, transport, 30, followsMotherboard: false, raw: 1850);

        Assert.DoesNotContain(logger.Messages, m => m.Contains("motherboard"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("software control"));
        Assert.Equal(4, transport.Writes.Count); // two polls, nothing re-asserted
    }

    [Fact]
    public void PollRpm_WhenThePumpReportsFollowingTheHeader_ReassertsOncePerOnset_AndLogsOnsetAndRecovery() {
        var (controller, transport, _, logger) = NewController();

        Poll(controller, transport, 30, followsMotherboard: true, raw: 1850);
        Poll(controller, transport, 30, followsMotherboard: true, raw: 1850);
        Poll(controller, transport, 30, followsMotherboard: false, raw: 1850);
        Poll(controller, transport, 30, followsMotherboard: true, raw: 1850);

        Assert.Equal(2, transport.Writes.Count(w => w[0] == 0x64));
        Assert.Equal(2, logger.Messages.Count(m => m.Contains("H0 reports the pump following the motherboard PWM header; software control re-asserted")));
        Assert.Single(logger.Messages, m => m.Contains("H0 reports the pump back under software control"));
    }

    [Fact]
    public void PollRpm_WhoseReassertFails_KeepsTheReadings_AndTriesAgainNextPoll() {
        var (controller, transport, _, logger) = NewController();
        transport.FailWrite = report => report[0] == 0x64;
        transport.ReadReplies.Enqueue(StatusReply(31, followsMotherboard: true));
        transport.ReadReplies.Enqueue(SpeedReply(1850));

        Assert.Throws<IOException>(controller.PollRpm);
        Assert.Equal(31f, controller.GetTemperature(0));
        Assert.Equal(1800f, controller.GetRpm(PumpChannel));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("re-asserted"));

        transport.FailWrite = null;
        Poll(controller, transport, 31, followsMotherboard: true, raw: 1850);

        Assert.Single(transport.Writes, w => w[0] == 0x64);
        Assert.Single(logger.Messages, m => m.Contains("re-asserted"));
    }

    // HS2Controller.SetPumpSpeed and SetMBSync discard their reply, and lcd207's WinUsb.Read hands
    // back zeros for one that never comes: a set-pump or sync command the MCU does not answer is
    // sent all the same, logged once, and not sent again next tick as if it had failed.
    [Fact]
    public void ApplyPending_WhoseReplyNeverComes_KeepsTheDutyAsWritten_AndLogsOnceUntilOneComes() {
        var (controller, transport, clock, logger) = NewController();
        transport.MissingRepliesThrow = true;
        controller.SetTarget(PumpChannel, 50);

        controller.ApplyPending();
        clock.Advance(TimeSpan.FromSeconds(1));
        controller.ApplyPending();

        Assert.Single(transport.Writes); // written once, not retried as a failure
        Assert.Single(logger.Messages, m => m == "H0: the pump did not answer the set-pump command (simulated reply that never came); the command was sent, and L-Connect does not wait for that reply either (logged once until one comes)");

        clock.Advance(HydroShiftCurveController.ResendInterval);
        controller.ApplyPending();
        transport.ReadReplies.Enqueue(new byte[] { 0x61 });
        clock.Advance(HydroShiftCurveController.ResendInterval);
        controller.ApplyPending();
        clock.Advance(HydroShiftCurveController.ResendInterval);
        controller.ApplyPending();

        Assert.Equal(4, transport.Writes.Count);
        Assert.Equal(2, logger.Messages.Count(m => m.Contains("did not answer the set-pump command")));
    }

    [Fact]
    public void AssertSoftwareControl_WhoseReplyNeverComes_IsSentAllTheSame_AndLoggedOnce() {
        var (controller, transport, _, logger) = NewController();
        transport.MissingRepliesThrow = true;

        controller.AssertSoftwareControl();
        controller.AssertSoftwareControl();

        Assert.Equal(new[] { SyncOff, SyncOff }, transport.Writes);
        Assert.Equal(2, transport.InterruptReadCount);
        Assert.Single(logger.Messages, m => m.StartsWith("H0: the pump did not answer the software-control command", StringComparison.Ordinal));
    }

    // Only a reply that never came is optional; a pipe that fails still fails the tick.
    [Fact]
    public void ApplyPending_WhoseReadFails_StillThrows() {
        var (controller, transport, _, _) = NewController();
        controller.SetTarget(PumpChannel, 50);
        transport.FailReads = true;

        Assert.Throws<IOException>(controller.ApplyPending);
    }

    [Fact]
    public void ApplyPending_AfterAReopen_WhoseSyncReplyNeverComes_RecordsTheReplay() {
        var (controller, transport, _, logger) = NewController();
        controller.SetTarget(PumpChannel, 50);
        controller.ApplyPending();
        transport.Clear();
        transport.MissingRepliesThrow = true;

        transport.Generation = 1;
        controller.ApplyPending();
        controller.ApplyPending();

        Assert.Equal(2, transport.Writes.Count); // the assert and the pump, once each
        Assert.Single(logger.Messages, m => m.Contains("H0 reconnected"));
    }

    // The MCU echoes the command in byte 0 of every reply; a status packet echoing the tachometer
    // command is a stale reply and is not read as a temperature of 7 and a pump on the header.
    [Fact]
    public void PollRpm_IgnoresAStatusReplyThatEchoesAnotherCommand_AndLogsOncePerRun() {
        var (controller, transport, _, logger) = NewController();
        Poll(controller, transport, 30, followsMotherboard: false, raw: 1850);

        transport.ReadReplies.Enqueue(SpeedReply(2050)); // a tachometer packet read as the status
        transport.ReadReplies.Enqueue(SpeedReply(2050));
        controller.PollRpm();
        transport.ReadReplies.Enqueue(new byte[] { 0, 7, 0 });
        transport.ReadReplies.Enqueue(SpeedReply(2050));
        controller.PollRpm();

        Assert.Equal(30f, controller.GetTemperature(0));
        Assert.Equal(2000f, controller.GetRpm(PumpChannel));
        Assert.DoesNotContain(transport.Writes, w => w[0] == 0x64);
        Assert.Single(logger.Messages, m => m == "H0: the status reply echoes another command (byte 0 = 0x62); ignored, keeping the last reading (logged once until it echoes its own)");

        Poll(controller, transport, 31, followsMotherboard: false, raw: 1850);

        Assert.Equal(31f, controller.GetTemperature(0));
        Assert.Single(logger.Messages, m => m == "H0: the status reply echoes its command again");
    }

    [Fact]
    public void PollRpm_IgnoresATachometerReplyThatEchoesAnotherCommand() {
        var (controller, transport, _, logger) = NewController();
        Poll(controller, transport, 30, followsMotherboard: false, raw: 1850);

        transport.ReadReplies.Enqueue(StatusReply(32, followsMotherboard: false));
        transport.ReadReplies.Enqueue(StatusReply(32, followsMotherboard: false)); // a status packet read as the tachometer
        controller.PollRpm();

        Assert.Equal(32f, controller.GetTemperature(0));
        Assert.Equal(1800f, controller.GetRpm(PumpChannel));
        Assert.Single(logger.Messages, m => m == "H0: the tachometer reply echoes another command (byte 0 = 0x60); ignored, keeping the last reading (logged once until it echoes its own)");
    }

    [Fact]
    public void Describe_NamesThePumpAndItsRpm_UnderTheIndexedIds() {
        var controller = new HydroShiftCurveController(2, new FakeDeviceTransport(), new FakeClock(), new FakeLogger());

        ChannelDescriptor pump = controller.Describe(PumpChannel);
        TemperatureDescriptor coolant = controller.DescribeTemperature(0);

        Assert.Equal(1, controller.ChannelCount);
        Assert.True(controller.IsChannelPopulated(PumpChannel));
        Assert.Equal("LianLi/2/ch0/ctl", pump.ControlId);
        Assert.Equal("Lian Li HydroShift II OLED Curve #3 Pump", pump.ControlName);
        Assert.Equal("LianLi/2/ch0/fan", pump.RpmId);
        Assert.Equal("Lian Li HydroShift II OLED Curve #3 Pump RPM", pump.RpmName);
        Assert.Equal(1, controller.TemperatureCount);
        Assert.Equal("LianLi/2/coolant/temp", coolant.Id);
        Assert.Equal("Lian Li HydroShift II OLED Curve #3 Coolant", coolant.Name);
    }

    [Fact]
    public void Dispose_DisposesTransport() {
        var (controller, transport, _, _) = NewController();
        controller.Dispose();
        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public void Constructor_RejectsMissingDependencies() {
        Assert.Throws<ArgumentNullException>(() => new HydroShiftCurveController(0, null!, new FakeClock(), new FakeLogger()));
        Assert.Throws<ArgumentNullException>(() => new HydroShiftCurveController(0, new FakeDeviceTransport(), null!, new FakeLogger()));
        Assert.Throws<ArgumentNullException>(() => new HydroShiftCurveController(0, new FakeDeviceTransport(), new FakeClock(), null!));
        Assert.Throws<ArgumentNullException>(
            () => new HydroShiftCurveController(0, new FakeDeviceTransport(), new FakeClock(), new FakeLogger()).ReplayOnReconnect(null!));
    }
}
