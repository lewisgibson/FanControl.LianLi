using System;
using System.Collections.Generic;
using System.Linq;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Tests.Fakes;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

public class Galahad2ControllerTests {
    private const int FanChannel = 0;
    private const int PumpChannel = 1;
    private const int TrinityProductId = 0x7371;
    private const int VisionProductId = 0x7391;

    private static (Galahad2Controller controller, FakeDeviceTransport transport, FakeClock clock) NewController() {
        var transport = new FakeDeviceTransport();
        var clock = new FakeClock();
        var controller = new Galahad2Controller(0, transport, TrinityProductId, clock, new FakeLogger());
        return (controller, transport, clock);
    }

    private static (Galahad2Controller controller, FakeDeviceTransport transport) NewCooler(int productId = VisionProductId) {
        var transport = new FakeDeviceTransport();
        var controller = new Galahad2Controller(0, transport, productId, new FakeClock(), new FakeLogger());
        return (controller, transport);
    }

    // A Vision LCD's handshake reply: the RPM pairs, then the coolant valid flag, whole degrees and tenths.
    private static byte[] VisionHandshakeReply(int fanRpm, int pumpRpm, byte valid, byte whole, byte tenths) {
        return CommandPacket.Build(
            0x81,
            (byte)((fanRpm >> 8) & 0xFF),
            (byte)(fanRpm & 0xFF),
            (byte)((pumpRpm >> 8) & 0xFF),
            (byte)(pumpRpm & 0xFF),
            valid,
            whole,
            tenths);
    }

    // The handshake reply frame: fan rpm at payload 0-1, pump rpm at payload 2-3, big-endian.
    private static byte[] HandshakeReply(int fanRpm, int pumpRpm) {
        return CommandPacket.Build(
            0x81,
            (byte)((fanRpm >> 8) & 0xFF),
            (byte)(fanRpm & 0xFF),
            (byte)((pumpRpm >> 8) & 0xFF),
            (byte)(pumpRpm & 0xFF));
    }

    [Fact]
    public void Constructor_DoesNoIo() {
        var (_, transport, _) = NewController();
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public void ApplyPending_AfterTransportReopened_ReplaysLookThenResendsFanAndPump() {
        var (controller, transport, _) = NewController();
        var replayedAt = new List<int>();
        controller.ReplayOnReconnect(() => {
            replayedAt.Add(transport.Writes.Count);
            return true;
        });
        controller.SetTarget(FanChannel, 50);
        controller.SetTarget(PumpChannel, 70);
        controller.ApplyPending();
        transport.Clear();

        // The transport lost and reopened the cooler (a wake): no setup writes of its own, so the
        // saved look replays first, then both unchanged duties are re-sent - the cooler may have reset.
        transport.Generation = 1;
        controller.ApplyPending();

        Assert.Equal(new[] { 0 }, replayedAt);
        Assert.Equal(2, transport.Writes.Count);
        Assert.Equal(0x8B, transport.Writes[0][1]); // fan
        Assert.Equal(0x8A, transport.Writes[1][1]); // pump
    }

    // While the handle is still faulted the cooler is off the bus: the look is not replayed (it
    // would be refused, or reopen the path itself and be lost on it), and the duties go out as
    // usual, one of them being what reopens the cooler on the backoff. The look follows once it
    // is back.
    [Fact]
    public void ApplyPending_WhileTheTransportIsStillFaulted_ReplaysNothing_UntilTheCoolerIsBack() {
        var (controller, transport, _) = NewController();
        int replays = 0;
        controller.ReplayOnReconnect(() => {
            replays++;
            return true;
        });
        controller.SetTarget(FanChannel, 50);
        controller.ApplyPending();
        transport.Clear();

        transport.Generation = 1;
        transport.IsFaulted = true;
        controller.ApplyPending();

        Assert.Equal(0, replays);
        Assert.Empty(transport.Writes); // the duty is fresh, and nothing was replayed

        transport.IsFaulted = false;
        controller.ApplyPending();

        Assert.Equal(1, replays);
        Assert.Single(transport.Writes); // the fan, re-sent after the look
    }

    // A look the cooler refused stays owed on its own and is tried again on the keepalive
    // interval; the duties are re-sent regardless, and the reconnect is recorded.
    [Fact]
    public void ApplyPending_ALookTheCoolerRefused_StaysOwed_AndIsTriedAgainOnTheKeepaliveInterval() {
        var (controller, transport, clock) = NewController();
        int replays = 0;
        controller.ReplayOnReconnect(() => ++replays >= 2);
        controller.SetTarget(PumpChannel, 70);
        controller.ApplyPending();
        transport.Clear();

        transport.Generation = 1;
        controller.ApplyPending();
        Assert.Equal(1, replays);
        Assert.Single(transport.Writes);

        controller.ApplyPending();
        Assert.Equal(1, replays);
        clock.Advance(ChannelWriteDecision.RefreshInterval);
        controller.ApplyPending();
        Assert.Equal(2, replays);
        clock.Advance(ChannelWriteDecision.RefreshInterval);
        controller.ApplyPending();
        Assert.Equal(2, replays);
    }

    [Fact]
    public void ApplyPending_FanChannel_WritesTheFanCommand() {
        var (controller, transport, _) = NewController();

        controller.SetTarget(FanChannel, 50);
        controller.ApplyPending();

        Assert.Single(transport.Writes);
        Assert.Equal(0x8B, transport.Writes[0][1]); // SetFan command
        Assert.Equal(0x00, transport.Writes[0][6]); // motherboard-sync flag off
        Assert.Equal(50, transport.Writes[0][7]);   // duty 1:1
    }

    [Fact]
    public void ApplyPending_PumpChannel_FloorsTheDutySoThePumpNeverStops() {
        var (controller, transport, _) = NewController();

        controller.SetTarget(PumpChannel, 0); // a curve commanding 0% must not stop the pump
        controller.ApplyPending();

        Assert.Single(transport.Writes);
        Assert.Equal(0x8A, transport.Writes[0][1]); // SetPump command
        Assert.Equal(Galahad2Protocol.PumpDutyFloor, transport.Writes[0][7]);
    }

    [Fact]
    public void ApplyPending_UnchangedAndFresh_WritesNothing() {
        var (controller, transport, _) = NewController();
        controller.SetTarget(FanChannel, 50);
        controller.ApplyPending();
        transport.Clear();

        controller.ApplyPending(); // same target, clock not advanced

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public void ApplyPending_StaleAfterFifteenSeconds_Reasserts() {
        var (controller, transport, clock) = NewController();
        controller.SetTarget(FanChannel, 50);
        controller.ApplyPending();
        transport.Clear();

        clock.Advance(TimeSpan.FromSeconds(15));
        controller.ApplyPending();

        Assert.Single(transport.Writes);
        Assert.Equal(0x8B, transport.Writes[0][1]);
    }

    [Fact]
    public void ApplyPending_LogsTheFirstSpeed_ButNotALaterChangeOrTheKeepaliveResend() {
        var clock = new FakeClock();
        var logger = new FakeLogger();
        var controller = new Galahad2Controller(0, new FakeDeviceTransport(), TrinityProductId, clock, logger);
        controller.SetTarget(FanChannel, 50);
        controller.ApplyPending();
        Assert.Contains("Set G0:fan = 50%", logger.Messages);
        int before = logger.Messages.Count;

        clock.Advance(ChannelWriteDecision.RefreshInterval);
        controller.ApplyPending();
        controller.SetTarget(FanChannel, 60);
        controller.ApplyPending();

        Assert.Equal(before, logger.Messages.Count);
    }

    [Fact]
    public void ReleaseChannel_StopsKeepaliveForThatChannel() {
        var (controller, transport, clock) = NewController();
        controller.SetTarget(PumpChannel, 80);
        controller.ApplyPending();
        controller.ReleaseChannel(PumpChannel);
        transport.Clear();

        clock.Advance(TimeSpan.FromSeconds(30));
        controller.ApplyPending();

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public void PollRpm_WritesHandshakeAndCachesFanAndPumpRpm() {
        var (controller, transport, _) = NewController();
        transport.ReadReplies.Enqueue(HandshakeReply(1200, 2800));

        controller.PollRpm();

        Assert.Equal(0x81, transport.Writes[0][1]); // the handshake request was written
        Assert.Equal(1, transport.InterruptReadCount); // then the reply was read
        Assert.Equal(1200f, controller.GetRpm(FanChannel));
        Assert.Equal(2800f, controller.GetRpm(PumpChannel));
    }

    // A packet that arrives after the handshake is written and before the cooler's answer (another
    // program's reply, or one the Vision sends unasked) is read past, not decoded.
    [Fact]
    public void ForgetReadings_ReadsNothing_UntilTheNextPoll() {
        var (controller, transport, _) = NewController();
        transport.ReadReplies.Enqueue(HandshakeReply(1200, 2800));
        controller.PollRpm();

        controller.ForgetReadings();
        Assert.Null(controller.GetRpm(FanChannel));
        Assert.Null(controller.GetRpm(PumpChannel));

        transport.ReadReplies.Enqueue(HandshakeReply(1300, 2900));
        controller.PollRpm();
        Assert.Equal(1300f, controller.GetRpm(FanChannel));
        Assert.Equal(2900f, controller.GetRpm(PumpChannel));
    }

    [Fact]
    public void PollRpm_ReadsPastOtherReportsToTheHandshakeReply() {
        var (controller, transport, _) = NewController();
        transport.ReadReplies.Enqueue(CommandPacket.Build(0x8A, 0x00, 0x50));
        transport.ReadReplies.Enqueue(HandshakeReply(1200, 2800));

        controller.PollRpm();

        Assert.Equal(2, transport.InterruptReadCount);
        Assert.Equal(1200f, controller.GetRpm(FanChannel));
        Assert.Equal(2800f, controller.GetRpm(PumpChannel));
    }

    [Fact]
    public void PollRpm_AReportThatIsNotAHandshakeReply_IsNeverTakenAsRpm() {
        var (controller, transport, _) = NewController();
        transport.ReadReplies.Enqueue(HandshakeReply(1200, 2800));
        controller.PollRpm();
        for (int i = 0; i < 40; i++) {
            transport.ReadReplies.Enqueue(CommandPacket.Build(0x8A, 0x03, 0xE8, 0x07, 0xD0)); // would decode to a plausible 1000 and 2000 rpm
        }

        transport.Clear();

        controller.PollRpm();

        Assert.Equal(0x81, Assert.Single(transport.Writes)[1]);
        Assert.Equal(32, transport.InterruptReadCount);
        Assert.Equal(1200f, controller.GetRpm(FanChannel));
        Assert.Equal(2800f, controller.GetRpm(PumpChannel));
    }

    // A cooler that stops answering is said once, with how far the poll got and the command the last
    // report echoed, and once more when it answers again.
    [Fact]
    public void PollRpm_WithNoHandshakeReply_IsLoggedOnce_AndOnceWhenOneComesAgain() {
        var transport = new FakeDeviceTransport();
        var logger = new FakeLogger();
        var controller = new Galahad2Controller(0, transport, TrinityProductId, new FakeClock(), logger);
        transport.ReadReplies.Enqueue(HandshakeReply(1200, 2700));
        controller.PollRpm();
        transport.ReplyFor = _ => CommandPacket.Build(0x8B, 0x00, 0x32);

        controller.PollRpm();
        controller.PollRpm();
        transport.ReadReplies.Enqueue(HandshakeReply(1500, 2800));
        controller.PollRpm();
        transport.ReadReplies.Enqueue(HandshakeReply(1500, 2800));
        controller.PollRpm();

        Assert.Equal(
            new[] {
                "G0: the cooler did not answer the handshake (32 report(s) read, the last echoing command 0x8B); RPM left as it was (logged once until it does)",
                "G0: the cooler answers the handshake again",
            },
            logger.Messages);
        Assert.Equal(1500f, controller.GetRpm(FanChannel));
    }

    // A cooler that has never answered is not answering "again" when it first does.
    [Fact]
    public void PollRpm_TheFirstAnswerAfterAMiss_IsLoggedAsTheFirst() {
        var transport = new FakeDeviceTransport();
        var logger = new FakeLogger();
        var controller = new Galahad2Controller(0, transport, TrinityProductId, new FakeClock(), logger);

        controller.PollRpm();
        Assert.Null(controller.GetRpm(FanChannel)); // a cooler that never answered has no reading, not 0
        Assert.Null(controller.GetRpm(PumpChannel));
        transport.ReadReplies.Enqueue(HandshakeReply(1500, 2800));
        controller.PollRpm();

        Assert.Equal(
            new[] {
                "G0: the cooler did not answer the handshake (32 report(s) read, the last echoing command 0x00); RPM left as it was (logged once until it does)",
                "G0: the cooler answers the handshake for the first time",
            },
            logger.Messages);
    }

    // A stream of other reports cannot hold the tick: the poll stops reading once a second has passed,
    // even short of the report count.
    [Fact]
    public void PollRpm_StopsReadingOnceTheTimeLimitHasPassed() {
        var transport = new FakeDeviceTransport();
        var clock = new FakeClock();
        var logger = new FakeLogger();
        var controller = new Galahad2Controller(0, transport, TrinityProductId, clock, logger);
        transport.ReplyFor = _ => {
            clock.Advance(TimeSpan.FromMilliseconds(200));
            return CommandPacket.Build(0x8A, 0x00, 0x50);
        };

        controller.PollRpm();

        Assert.Equal(6, transport.InterruptReadCount); // reads at 0-1000 ms; the one due at 1200 ms is not made
        Assert.Single(transport.Writes);
        Assert.Equal(
            "G0: the cooler did not answer the handshake (6 report(s) read, the last echoing command 0x8A); RPM left as it was (logged once until it does)",
            Assert.Single(logger.Messages));
    }

    // A garbage first read leaves the reading as it was: none.
    [Fact]
    public void PollRpm_AnImplausibleFirstReading_LeavesNoReading() {
        var transport = new FakeDeviceTransport();
        var logger = new FakeLogger();
        var controller = new Galahad2Controller(0, transport, TrinityProductId, new FakeClock(), logger);
        transport.ReadReplies.Enqueue(HandshakeReply(50000, 2800));

        controller.PollRpm();

        Assert.Null(controller.GetRpm(FanChannel));
        Assert.Equal(2800f, controller.GetRpm(PumpChannel));
        Assert.Contains("G0:0 implausible rpm 50000 ignored, keeping no reading", logger.Messages);
    }

    [Fact]
    public void PollRpm_IgnoresImplausibleReading_AndKeepsLastGood() {
        var (controller, transport, _) = NewController();
        transport.ReadReplies.Enqueue(HandshakeReply(1500, 2800));
        controller.PollRpm();

        // A post-hibernate garbage read decodes to ~50000 rpm; it must be ignored, not cached.
        transport.ReadReplies.Enqueue(HandshakeReply(50000, 50000));
        controller.PollRpm();

        Assert.Equal(1500f, controller.GetRpm(FanChannel));
        Assert.Equal(2800f, controller.GetRpm(PumpChannel));
    }

    [Fact]
    public void Describe_NamesFanAndPumpDistinctly() {
        var (controller, _, _) = NewController();

        ChannelDescriptor fan = controller.Describe(FanChannel);
        ChannelDescriptor pump = controller.Describe(PumpChannel);

        Assert.Contains("Fan", fan.ControlName);
        Assert.Contains("Pump", pump.ControlName);
        Assert.NotEqual(fan.ControlId, pump.ControlId);
        Assert.NotEqual(fan.ControlId, fan.RpmId); // control and rpm ids never collide
    }

    [Fact]
    public void Dispose_DisposesTransport() {
        var (controller, transport, _) = NewController();
        controller.Dispose();
        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public void PollRpm_LogsImplausibleOnsetOnce_ThenRecovery() {
        var transport = new FakeDeviceTransport();
        var logger = new FakeLogger();
        var controller = new Galahad2Controller(0, transport, TrinityProductId, new FakeClock(), logger);
        transport.ReadReplies.Enqueue(HandshakeReply(1500, 2800));
        transport.ReadReplies.Enqueue(HandshakeReply(1500, 50000));
        transport.ReadReplies.Enqueue(HandshakeReply(1500, 50000));
        transport.ReadReplies.Enqueue(HandshakeReply(1500, 2700));

        for (int i = 0; i < 4; i++) {
            controller.PollRpm();
        }

        Assert.Equal(2700f, controller.GetRpm(PumpChannel));
        Assert.Single(logger.Messages, m => m.Contains("G0:1 implausible rpm 50000"));
        Assert.Single(logger.Messages, m => m.Contains("G0:1 rpm recovered (2700)"));
    }

    [Fact]
    public void Constructor_RejectsMissingDependencies() {
        Assert.Throws<ArgumentNullException>(() => new Galahad2Controller(0, null!, TrinityProductId, new FakeClock(), new FakeLogger()));
        Assert.Throws<ArgumentNullException>(() => new Galahad2Controller(0, new FakeDeviceTransport(), TrinityProductId, null!, new FakeLogger()));
        Assert.Throws<ArgumentNullException>(() => new Galahad2Controller(0, new FakeDeviceTransport(), TrinityProductId, new FakeClock(), null!));
        Assert.Throws<ArgumentNullException>(
            () => new Galahad2Controller(0, new FakeDeviceTransport(), TrinityProductId, new FakeClock(), new FakeLogger()).ReplayOnReconnect(null!));
    }

    [Fact]
    public void ApplyPending_AfterAReopen_WithNoReplayRegistered_ResendsBothDuties() {
        var (controller, transport, _) = NewController();
        controller.SetTarget(FanChannel, 50);
        controller.SetTarget(PumpChannel, 70);
        controller.ApplyPending();
        transport.Clear();

        transport.Generation = 1;
        controller.ApplyPending();

        Assert.Equal(2, transport.Writes.Count);
    }

    [Fact]
    public void PollRpm_BothChannelsChangingStateInOnePoll_AreBothLogged() {
        var transport = new FakeDeviceTransport();
        var logger = new FakeLogger();
        var controller = new Galahad2Controller(0, transport, TrinityProductId, new FakeClock(), logger);
        transport.ReadReplies.Enqueue(HandshakeReply(50000, 50000));
        transport.ReadReplies.Enqueue(HandshakeReply(1500, 2800));

        controller.PollRpm();
        controller.PollRpm();

        Assert.Equal(2, logger.Messages.Count(m => m.Contains("implausible rpm")));
        Assert.Equal(2, logger.Messages.Count(m => m.Contains("rpm recovered")));
    }

    [Theory]
    [InlineData(0x7391)]
    [InlineData(0x7395)]
    public void AVisionLcd_ReportsTheCoolantTemperature_UnderItsOwnId(int productId) {
        var (controller, transport) = NewCooler(productId);
        transport.ReadReplies.Enqueue(VisionHandshakeReply(1440, 2670, 0x01, 37, 8));

        Assert.Null(controller.GetTemperature(0)); // not measured until the cooler answers
        controller.PollRpm();

        Assert.Equal(1, controller.TemperatureCount);
        TemperatureDescriptor coolant = controller.DescribeTemperature(0);
        Assert.Equal("LianLi/0/coolant/temp", coolant.Id);
        Assert.Equal("Lian Li Galahad #1 Coolant", coolant.Name);
        Assert.Equal(37.8f, controller.GetTemperature(0));
        Assert.Equal(1440f, controller.GetRpm(FanChannel));
        Assert.Equal(2670f, controller.GetRpm(PumpChannel));
    }

    // A reply whose valid flag is clear has no coolant reading: the last one is not kept, so a curve
    // on it does not follow a stale value.
    [Fact]
    public void AVisionLcd_AReplyWithTheValidFlagClear_LeavesTheCoolantNotMeasured() {
        var (controller, transport) = NewCooler();
        transport.ReadReplies.Enqueue(VisionHandshakeReply(1440, 2670, 0x01, 37, 8));
        controller.PollRpm();

        transport.ReadReplies.Enqueue(VisionHandshakeReply(1440, 2670, 0x00, 37, 8));
        controller.PollRpm();

        Assert.Null(controller.GetTemperature(0));
        Assert.Equal(1440f, controller.GetRpm(FanChannel));
    }

    // A handshake that goes unanswered leaves the coolant as it was, as it does the RPM; forgetting
    // the readings forgets it too.
    [Fact]
    public void AVisionLcd_KeepsTheCoolantThroughAMissedHandshake_AndForgetsItWithTheReadings() {
        var (controller, transport) = NewCooler();
        transport.ReadReplies.Enqueue(VisionHandshakeReply(1440, 2670, 0x01, 37, 8));
        controller.PollRpm();

        controller.PollRpm(); // nothing answers
        Assert.Equal(37.8f, controller.GetTemperature(0));

        controller.ForgetReadings();
        Assert.Null(controller.GetTemperature(0));
    }

    // The Trinity's reply has bytes where the Vision keeps the coolant, but they are not a
    // temperature: it gets no temperature sensor, and its reply is never read as one.
    [Theory]
    [InlineData(0x7371)]
    [InlineData(0x7373)]
    [InlineData(0x7398)]
    public void ACoolerOtherThanTheVision_ReportsNoTemperature(int productId) {
        var (controller, transport) = NewCooler(productId);
        transport.ReadReplies.Enqueue(VisionHandshakeReply(1440, 2670, 0x01, 0x01, 0x01));

        controller.PollRpm();

        Assert.Equal(0, controller.TemperatureCount);
        Assert.Null(controller.GetTemperature(0));
        Assert.Equal(1440f, controller.GetRpm(FanChannel));
        Assert.Equal(2670f, controller.GetRpm(PumpChannel));
    }
}
