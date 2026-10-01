using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Tests.Fakes;
using FanControl.LianLi.Tests.Protocol;
using FanControl.LianLi.Transport;
using FanControl.LianLi.Worker;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

/// <summary>
/// A FLEX or P28 V2 chain driven through its USB receiver: the status read at build for the
/// address and fans, the sensors keyed on that address as the wireless controller keys them and
/// given only once whose the chain is has been settled, the speed command on change and on the
/// keepalive cadence with the reply checked, the readings from each poll, a fan reported later,
/// the identification and re-send after a reopen, the chain changing hands with the wireless
/// controller either way - decided from the shared state at every write, never from a flag - and
/// another receiver answering on the path.
/// </summary>
public sealed class FlexReceiverControllerTests {
    // Stands in for the wireless controller that publishes the bound set.
    private static readonly object RadioOwner = new object();

    private const string MacText = "a1b2c3d4e5f6";
    private static readonly byte[] Mac = { 0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6 };
    private static readonly byte[] OtherMac = { 0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0x00 };
    private static readonly byte[] Master = { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };

    private static byte[] Status(int fanCount, params int[] rpm)
        => Status(Mac, fanCount, rpm);

    private static byte[] Status(byte[] mac, int fanCount, params int[] rpm) {
        var readings = new int[4];
        Array.Copy(rpm, readings, rpm.Length);
        return FlexReceiverProtocolTests.StatusReply(new FakeWirelessRecord(mac, Master) {
            FanCountByte = (byte)fanCount,
            FanTypes = new byte[] { 51, 51, 51, 51 },
            Rpm = readings,
            RpmHighNibbles = new byte[] { 0xA, 0x3, 0x2, 0x6 },
        });
    }

    private static readonly byte[] SpeedTaken = { 0x13, 0 };

    private sealed class Rig {
        public FakeDeviceTransport Transport { get; } = new FakeDeviceTransport();

        public FakeClock Clock { get; } = new FakeClock();

        public FakeLogger Logger { get; } = new FakeLogger();

        public WirelessProcessState State { get; } = new WirelessProcessState();

        public List<FlexReceiverChange> Changes { get; } = new List<FlexReceiverChange>();

        /// <summary>A receiver identified and settled, as the plugin builds one.</summary>
        public FlexReceiverController Build(FlexReceiverFamily family = FlexReceiverFamily.TlFlex, int index = 0) {
            FlexReceiverController controller = Identify(family, index);
            controller.SettleOwnership();
            return controller;
        }

        /// <summary>A receiver identified but not yet settled: what a scan has while it waits for the wireless pair.</summary>
        public FlexReceiverController Identify(FlexReceiverFamily family = FlexReceiverFamily.TlFlex, int index = 0) {
            var controller = new FlexReceiverController(index, Transport, family, Clock, Logger, State);
            controller.Changed += (_, change) => Changes.Add(change);
            return controller;
        }

        /// <summary>A receiver that answers every status request the same way, and takes every speed.</summary>
        public void Answer(byte[] status) {
            Transport.ReadReplies.Clear();
            Transport.ReplyFor = written => written[0] == 0x12 ? status : SpeedTaken;
        }

        public void RadioTakes() => State.RecordBoundDevices(RadioOwner, new[] { MacText });

        public void RadioLetsGo() => State.ForgetBoundDevices(RadioOwner);
    }

    private static Rig NewRig(byte[] status) {
        var rig = new Rig();
        rig.Answer(status);
        return rig;
    }

    [Fact]
    public void Constructor_ReadsTheStatusOnce_AndKeysTheSensorsOnTheAddress_AsTheWirelessControllerDoes() {
        Rig rig = NewRig(Status(2, 1200, 1150));

        FlexReceiverController controller = rig.Build();

        Assert.Equal(new[] { FlexReceiverProtocol.EncodeStatusRequest() }, rig.Transport.Writes);
        Assert.Equal(1, rig.Transport.InterruptReadCount);
        Assert.Equal(MacText, controller.MacText);
        Assert.Equal(2, controller.FanCount);
        Assert.False(controller.IsLeftToWireless);
        Assert.True(controller.IsDrivingChain);
        Assert.Equal(1, controller.ChannelCount);
        Assert.True(controller.IsChannelPopulated(0));
        Assert.Equal(2, controller.FanSpeedCount);

        ChannelDescriptor control = controller.Describe(0);
        Assert.Equal("LianLi/wa1b2c3d4e5f6/ctl", control.ControlId);
        Assert.Equal("Lian Li UNI FAN TL FLEX USB d4e5f6", control.ControlName);
        Assert.Equal("LianLi/wa1b2c3d4e5f6/f0/fan", control.RpmId);
        Assert.Equal("Lian Li UNI FAN TL FLEX USB d4e5f6 Fan 1 RPM", control.RpmName);
        Assert.Equal("LianLi/wa1b2c3d4e5f6/f1/fan", controller.DescribeFanSpeed(1).Id);
        Assert.Equal("Lian Li UNI FAN TL FLEX USB d4e5f6 Fan 2 RPM", controller.DescribeFanSpeed(1).Name);
        Assert.Equal(1200f, controller.GetRpm(0));
        Assert.Equal(1150f, controller.GetFanSpeed(1));
        Assert.Equal(0f, controller.GetFanSpeed(2)); // not a registered reading
        Assert.Equal(
            new[] { "LianLi/wa1b2c3d4e5f6/ctl", "LianLi/wa1b2c3d4e5f6/f0/fan", "LianLi/wa1b2c3d4e5f6/f1/fan" },
            controller.DrivenSensorIds);
    }

    // The build identifies the receiver and reads its fans; whose the chain is waits for the plan.
    [Fact]
    public void Constructor_IdentifiesTheReceiver_ButGivesTheChainNoSensorsUntilOwnershipIsSettled() {
        Rig rig = NewRig(Status(2, 1200, 1150));

        FlexReceiverController controller = rig.Identify();

        Assert.Equal(MacText, controller.MacText);
        Assert.Equal(2, controller.FanCount);
        Assert.True(controller.IsDrivingChain);
        Assert.Equal(0, controller.ChannelCount);
        Assert.Equal(0, controller.FanSpeedCount);
        Assert.Empty(controller.DrivenSensorIds);

        rig.RadioTakes();
        controller.SettleOwnership();
        Assert.Equal(0, controller.ChannelCount);
        Assert.Contains(
            "F0:a1b2c3d4e5f6 is bound to the L-Wireless controller's master, which drives it; left to the radio, with no sensors of its own",
            rig.Logger.Messages);
    }

    [Theory]
    [InlineData("TlFlexLcd", "Lian Li UNI FAN TL FLEX LCD USB d4e5f6")]
    [InlineData("SlInfinityFlexLcd", "Lian Li UNI FAN SL-INF FLEX LCD USB d4e5f6")]
    [InlineData("P28V2", "Lian Li UNI FAN P28 V2 USB d4e5f6")]
    public void Constructor_NamesTheChainForItsProduct(string familyName, string expected) {
        Rig rig = NewRig(Status(1, 900));
        var family = Enum.Parse<FlexReceiverFamily>(familyName);

        Assert.Equal(expected, rig.Build(family).Describe(0).ControlName);
    }

    [Fact]
    public void Constructor_RefusesAReplyThatIsNotTheStatus() {
        var rig = new Rig();
        rig.Transport.ReadReplies.Enqueue(new byte[] { 0x13, 0 });
        IOException wrongEcho = Assert.Throws<IOException>(() => rig.Build());
        Assert.Equal("the receiver answered the status request with 13, not its status", wrongEcho.Message);

        // Nothing queued: the fake answers zeros, as L-Connect's own read hands back for a reply
        // that timed out. The real transport throws DeviceReplyMissingException ("returned no
        // data") from Read instead, which fails the build the same way.
        Assert.Contains("with 00, not its status", Assert.Throws<IOException>(() => rig.Build()).Message);
    }

    [Fact]
    public void Constructor_WithNoFanReported_HasNoSensors_AndGainsThemWhenOneIs() {
        Rig rig = NewRig(Status(0));
        FlexReceiverController controller = rig.Build();
        Assert.Equal(0, controller.ChannelCount);
        Assert.Equal(0, controller.FanSpeedCount);
        Assert.Equal(0f, controller.GetRpm(0));

        controller.SetTarget(0, 50);
        controller.ApplyPending(); // a chain without fans is never written
        Assert.DoesNotContain(rig.Transport.Writes, w => w[0] == 0x13);

        rig.Answer(Status(2, 800, 810));
        controller.PollRpm();

        Assert.Equal(1, controller.ChannelCount);
        Assert.Equal(2, controller.FanSpeedCount);
        Assert.Equal(810f, controller.GetFanSpeed(1));
        Assert.Equal(new[] { FlexReceiverChange.FanReported }, rig.Changes);

        controller.PollRpm();
        Assert.Single(rig.Changes); // nothing new
    }

    [Fact]
    public void PollRpm_WithNobodyListening_StillAddsTheSensors() {
        Rig rig = NewRig(Status(0));
        var controller = new FlexReceiverController(0, rig.Transport, FlexReceiverFamily.TlFlex, rig.Clock, rig.Logger, rig.State);
        controller.SettleOwnership();

        rig.Answer(Status(1, 700));
        controller.PollRpm();

        Assert.Equal(1, controller.ChannelCount);
        Assert.Equal(700f, controller.GetRpm(0));
    }

    [Fact]
    public void Constructor_WhenTheWirelessControllerHasTheChain_LeavesItToTheRadio_WithNoSensors() {
        Rig rig = NewRig(Status(2, 1200, 1150));
        rig.RadioTakes();

        FlexReceiverController controller = rig.Build();

        Assert.True(controller.IsLeftToWireless);
        Assert.False(controller.IsDrivingChain);
        Assert.Equal(0, controller.ChannelCount);
        Assert.Equal(0, controller.FanSpeedCount);
        Assert.Contains(
            "F0:a1b2c3d4e5f6 is bound to the L-Wireless controller's master, which drives it; left to the radio, with no sensors of its own",
            rig.Logger.Messages);

        controller.SetTarget(0, 50);
        controller.ApplyPending();
        controller.PollRpm();

        Assert.DoesNotContain(rig.Transport.Writes, w => w[0] == 0x13);
        Assert.Equal(2, rig.Transport.Writes.Count(w => w[0] == 0x12)); // the build's read and the poll
        Assert.Empty(rig.Changes);
        Assert.Equal(0f, controller.GetRpm(0)); // no registered reading to publish to
    }

    [Fact]
    public void ApplyPending_SendsTheFamilysPwmOncePerFan_TakesTheReply_AndLogsTheChange() {
        Rig rig = NewRig(Status(3, 1000, 1000, 1000));
        FlexReceiverController controller = rig.Build();
        rig.Transport.Clear();

        controller.SetTarget(0, 50);
        controller.ApplyPending();

        Assert.Equal(new[] { FlexReceiverProtocol.EncodeSpeed(new byte[] { 128, 128, 128 }) }, rig.Transport.Writes);
        Assert.Equal(1, rig.Transport.InterruptReadCount);
        Assert.Contains("Set F0:a1b2c3d4e5f6 = 50% (PWM 128 to 3 fan(s))", rig.Logger.Messages);
    }

    // The duty lives in the shared state, keyed on the chain's address: one the host handed to
    // the chain's other controller, or to a stand-in's control, is sent all the same.
    [Fact]
    public void ApplyPending_SendsTheDutyKeptForTheChainInTheSharedState_WhoeverPutItThere() {
        Rig rig = NewRig(Status(2, 1000, 1000));
        FlexReceiverController controller = rig.Build();
        rig.Transport.Clear();

        rig.State.SetChainTarget(MacText, 60);
        controller.ApplyPending();
        Assert.Equal(new byte[] { 0x13, 153, 153 }, rig.Transport.Writes.Single().Take(3));

        controller.SetTarget(0, 70);
        Assert.Equal(70, rig.State.ChainTarget(MacText));
        controller.ReleaseChannel(0);
        Assert.Equal(-1, rig.State.ChainTarget(MacText));
    }

    [Theory]
    [InlineData("SlInfinityFlexLcd", 5, 26)]
    [InlineData("P28V2", 0, 3)]
    [InlineData("TlFlexLcd", 0, 13)]
    public void ApplyPending_UsesTheFamilysDutyRule(string familyName, int duty, int pwm) {
        Rig rig = NewRig(Status(1, 500));
        FlexReceiverController controller = rig.Build(Enum.Parse<FlexReceiverFamily>(familyName));

        controller.SetTarget(0, duty);
        controller.ApplyPending();

        Assert.Equal(new byte[] { 0x13, (byte)pwm, 0 }, rig.Transport.Writes[1].Take(3));
    }

    [Fact]
    public void ApplyPending_UnassignedTarget_WritesNothing() {
        Rig rig = NewRig(Status(2, 1, 1));
        FlexReceiverController controller = rig.Build();
        rig.Transport.Clear();

        controller.ApplyPending();

        Assert.Empty(rig.Transport.Writes);
    }

    [Fact]
    public void ApplyPending_ResendsOnTheKeepaliveCadence_WithoutLoggingTheResend() {
        Rig rig = NewRig(Status(2, 1, 1));
        FlexReceiverController controller = rig.Build();
        controller.SetTarget(0, 40);
        controller.ApplyPending();
        rig.Transport.Clear();

        rig.Clock.Advance(TimeSpan.FromSeconds(14));
        controller.ApplyPending();
        Assert.Empty(rig.Transport.Writes);

        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        controller.ApplyPending();
        Assert.Single(rig.Transport.Writes);
        Assert.Single(rig.Logger.Messages, m => m.StartsWith("Set F0:", StringComparison.Ordinal));
    }

    [Fact]
    public void ReleaseChannel_StopsTheResend() {
        Rig rig = NewRig(Status(2, 1, 1));
        FlexReceiverController controller = rig.Build();
        controller.SetTarget(0, 40);
        controller.ApplyPending();
        controller.ReleaseChannel(0);
        rig.Transport.Clear();

        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        controller.ApplyPending();

        Assert.Empty(rig.Transport.Writes);
    }

    [Fact]
    public void ApplyPending_ARefusedOrForeignReply_Throws_AndTheDutyGoesOutAgainNextTick() {
        Rig rig = NewRig(Status(2, 1, 1));
        FlexReceiverController controller = rig.Build();
        controller.SetTarget(0, 40);

        rig.Transport.ReplyFor = _ => new byte[] { 0x13, 1 };
        IOException refused = Assert.Throws<IOException>(controller.ApplyPending);
        Assert.Equal("a1b2c3d4e5f6 refused the speed write: reply 13 01", refused.Message);

        rig.Transport.ReplyFor = _ => new byte[] { 0x12, 0 };
        Assert.Contains("reply 12 00", Assert.Throws<IOException>(controller.ApplyPending).Message);

        rig.Transport.ReplyFor = _ => SpeedTaken;
        controller.ApplyPending();
        Assert.Equal(3, rig.Transport.Writes.Count(w => w[0] == 0x13));
        Assert.Single(rig.Logger.Messages, m => m.StartsWith("Set F0:", StringComparison.Ordinal));
    }

    // After a reopen the device on the path is identified first - the status read - and only then
    // is the registered work replayed and the duty re-sent, at once.
    [Fact]
    public void ApplyPending_AfterTheTransportReopened_IdentifiesTheReceiver_ReplaysTheRegisteredWork_ThenResendsTheDutyNow() {
        Rig rig = NewRig(Status(2, 1, 1));
        FlexReceiverController controller = rig.Build();
        int replayedAt = -1;
        controller.ReplayOnReconnect(() => {
            replayedAt = rig.Transport.Writes.Count;
            return true;
        });
        controller.SetTarget(0, 40);
        controller.ApplyPending();
        rig.Transport.Clear();

        rig.Transport.Generation = 1;
        Assert.False(controller.IsDrivingChain); // not until the device on the path has answered as this receiver
        controller.ApplyPending();

        Assert.Equal(1, replayedAt); // after the status read, before the speed
        Assert.Equal(new[] { (byte)0x12, (byte)0x13 }, rig.Transport.Writes.Select(w => w[0]));
        Assert.True(controller.IsDrivingChain);
        Assert.Contains("F0:a1b2c3d4e5f6 reconnected and answers as the same receiver (transport generation 1)", rig.Logger.Messages);
        Assert.Contains("F0:a1b2c3d4e5f6 duty re-sent after the reconnect", rig.Logger.Messages);

        controller.ApplyPending();
        Assert.Equal(2, rig.Transport.Writes.Count); // the generation is recorded; no second identification or replay
    }

    // Registered work that reports failure on the reopened path stays owed, and is tried again on
    // the identified path with the keepalive re-send, until it reports itself done; the duty is
    // not held up by it.
    [Fact]
    public void ApplyPending_AfterAReopen_RegisteredWorkThatReportsFailure_IsTriedAgainOnTheKeepaliveCadence() {
        Rig rig = NewRig(Status(2, 1, 1));
        FlexReceiverController controller = rig.Build();
        int replays = 0;
        controller.ReplayOnReconnect(() => ++replays >= 2);
        controller.SetTarget(0, 40);
        controller.ApplyPending();
        rig.Transport.Clear();

        rig.Transport.Generation = 1;
        controller.ApplyPending();
        Assert.Equal(1, replays);
        Assert.Equal(new[] { (byte)0x12, (byte)0x13 }, rig.Transport.Writes.Select(w => w[0]));

        controller.ApplyPending();
        Assert.Equal(1, replays);
        rig.Clock.Advance(ChannelWriteDecision.RefreshInterval);
        controller.ApplyPending();
        Assert.Equal(2, replays);
        rig.Clock.Advance(ChannelWriteDecision.RefreshInterval);
        controller.ApplyPending();
        Assert.Equal(2, replays);
    }

    [Fact]
    public void ApplyPending_AfterAReopen_WithNoReplayRegistered_ResendsTheDuty() {
        Rig rig = NewRig(Status(2, 1, 1));
        FlexReceiverController controller = rig.Build();
        controller.SetTarget(0, 40);
        controller.ApplyPending();
        rig.Transport.Clear();

        rig.Transport.Generation = 1;
        controller.ApplyPending();

        Assert.Single(rig.Transport.Writes, w => w[0] == 0x13);
    }

    // The radio took the chain during the outage: the first good tick after it identifies the
    // receiver and sends nothing, and says nothing about a re-send that did not happen.
    [Fact]
    public void ApplyPending_AfterAReopen_WithTheChainNowTheRadios_IdentifiesTheReceiver_AndSendsNothing() {
        Rig rig = NewRig(Status(2, 1, 1));
        FlexReceiverController controller = rig.Build();
        controller.SetTarget(0, 40);
        controller.ApplyPending();
        rig.Transport.Clear();

        rig.RadioTakes();
        rig.Transport.Generation = 1;
        controller.ApplyPending();

        Assert.Equal(new[] { (byte)0x12 }, rig.Transport.Writes.Select(w => w[0]));
        Assert.Contains("F0:a1b2c3d4e5f6 reconnected and answers as the same receiver (transport generation 1)", rig.Logger.Messages);
        Assert.DoesNotContain(rig.Logger.Messages, m => m.Contains("re-sent"));

        // Let go again later: the poll notices, and the duty that was due goes out then.
        rig.RadioLetsGo();
        controller.PollRpm();
        controller.ApplyPending();
        Assert.Single(rig.Transport.Writes, w => w[0] == 0x13);
        Assert.Contains("F0:a1b2c3d4e5f6 duty re-sent after the reconnect", rig.Logger.Messages);
    }

    // A reopened path that does not answer yet: nothing is sent to it, and the identification is
    // tried again next tick.
    [Fact]
    public void ApplyPending_AfterAReopen_WhoseStatusReadFails_SendsNothingUntilItAnswers() {
        Rig rig = NewRig(Status(2, 1, 1));
        FlexReceiverController controller = rig.Build();
        controller.SetTarget(0, 40);
        controller.ApplyPending();
        rig.Transport.Clear();

        rig.Transport.Generation = 1;
        rig.Transport.FailReads = true;
        controller.SetTarget(0, 80);
        Assert.Throws<IOException>(controller.ApplyPending);
        Assert.Throws<IOException>(controller.PollRpm);
        Assert.DoesNotContain(rig.Transport.Writes, w => w[0] == 0x13);
        Assert.Empty(controller.DrivenSensorIds);

        rig.Transport.FailReads = false;
        controller.ApplyPending();
        Assert.Equal(new byte[] { 0x13, 204, 204 }, rig.Transport.Writes.Last().Take(3));
    }

    // The real transport: a status transfer fails while the receiver is unplugged, and the next
    // transfer reopens the path inside its own call. The controller sees the lost handle before
    // that transfer, so the first command on the fresh handle is the status request, and the duty
    // follows only once the same address has answered on it.
    [Fact]
    public void ApplyPending_OverTheRealTransport_IdentifiesTheDeviceOnTheReopenedPath_BeforeItSendsTheDuty() {
        var api = new FakeWinUsbApi();
        var logger = new FakeLogger();
        using WinUsbTransport transport = OpenRealTransport(api, logger);
        api.Replies.Enqueue((Status(2, 1000, 1000), 0));
        using var controller = new FlexReceiverController(0, transport, FlexReceiverFamily.TlFlex, new FakeClock(), logger, new WirelessProcessState());
        controller.SettleOwnership();
        controller.SetTarget(0, 40);
        api.WriteResults.Enqueue((false, 0, 31));
        Assert.Throws<IOException>(controller.PollRpm);
        Assert.Equal(1, transport.Generation);
        Assert.False(controller.IsDrivingChain);
        api.Written.Clear();

        api.Replies.Enqueue((Status(2, 1000, 1000), 0));
        api.Replies.Enqueue((SpeedTaken, 0));
        controller.ApplyPending();

        Assert.Equal(new[] { (byte)0x12, (byte)0x13 }, api.Written.Select(w => w[0]));
        Assert.Equal(new byte[] { 0x13, 0x66, 0x66, 0 }, api.Written[1].Take(4));
        Assert.Equal("WritePipe interface1 0x01 64", api.Calls.Last(call => call.StartsWith("WritePipe", StringComparison.Ordinal)));
        Assert.True(controller.IsDrivingChain);
        Assert.Contains("F0:a1b2c3d4e5f6 reconnected and answers as the same receiver (transport generation 1)", logger.Messages);
    }

    // The same, with another receiver on the path after the reopen: it is asked who it is and
    // sent nothing else, whatever duty was due.
    [Fact]
    public void ApplyPending_OverTheRealTransport_SendsAnotherReceiverOnTheReopenedPathNothingButTheStatusRequest() {
        var api = new FakeWinUsbApi();
        var logger = new FakeLogger();
        using WinUsbTransport transport = OpenRealTransport(api, logger);
        api.Replies.Enqueue((Status(2, 1000, 1000), 0));
        using var controller = new FlexReceiverController(0, transport, FlexReceiverFamily.TlFlex, new FakeClock(), logger, new WirelessProcessState());
        controller.SettleOwnership();
        controller.SetTarget(0, 40);
        api.WriteResults.Enqueue((false, 0, 31));
        Assert.Throws<IOException>(controller.PollRpm);
        api.Written.Clear();

        api.Replies.Enqueue((Status(OtherMac, 2, 900, 900), 0));
        controller.ApplyPending();
        controller.ApplyPending();
        controller.PollRpm();

        Assert.Equal((byte)0x12, Assert.Single(api.Written)[0]);
        Assert.False(controller.IsDrivingChain);
        Assert.Contains(logger.Messages, m => m.Contains("now answers as a1b2c3d4e500"));
    }

    private static WinUsbTransport OpenRealTransport(FakeWinUsbApi api, FakeLogger logger) {
        var calls = new FakeDeviceCallRunner();
        return WinUsbTransport.Open(
            @"\\?\usb#vid_43a8&pid_0101#8&3&0&2#{guid}",
            WinUsbPipePolicy.FlexReceiver,
            logger,
            api,
            new DeviceCallGate(calls, new FakeDeviceCallClock(), logger),
            new FakeTransferDelay(),
            System.Threading.CancellationToken.None);
    }

    [Fact]
    public void PollRpm_PublishesEveryRegisteredReading_AndKeepsAReadingWhoseFanIsGone() {
        Rig rig = NewRig(Status(3, 1000, 1100, 1200));
        FlexReceiverController controller = rig.Build();

        rig.Answer(Status(2, 1010, 1110, 0));
        controller.PollRpm();

        Assert.Equal(2, controller.FanCount);
        Assert.Equal(3, controller.FanSpeedCount); // append-only
        Assert.Equal(1010f, controller.GetFanSpeed(0));
        Assert.Equal(1110f, controller.GetFanSpeed(1));
        Assert.Equal(0f, controller.GetFanSpeed(2));
        Assert.Empty(rig.Changes);
    }

    // The path carries another receiver now (two swapped between ports while the host slept):
    // said once, reported once so the plugin can plan the path afresh, the readings are not the
    // chain's any more, and nothing is ever sent on the path again - not the duty the host set,
    // not a keepalive, not after a further reopen.
    [Fact]
    public void PollRpm_WhenAnotherReceiverAnswers_GivesThePathUp_AndNeverWritesToItAgain() {
        Rig rig = NewRig(Status(2, 1000, 1000));
        FlexReceiverController controller = rig.Build();
        controller.SetTarget(0, 40);
        controller.ApplyPending();
        rig.Transport.Clear();

        rig.Answer(Status(OtherMac, 2, 900, 900));
        rig.Transport.Generation = 1;
        controller.PollRpm();

        Assert.Equal(new[] { FlexReceiverChange.AnotherReceiverAnswered }, rig.Changes);
        Assert.Contains(
            "F0:a1b2c3d4e5f6: the receiver on its path now answers as a1b2c3d4e500; nothing more is sent on it, and a refresh is asked for to plan it afresh",
            rig.Logger.Messages);
        Assert.False(controller.IsDrivingChain);
        Assert.Empty(controller.DrivenSensorIds);
        Assert.Equal(0f, controller.GetRpm(0));
        Assert.Equal(1, controller.ChannelCount); // the sensors stay registered, reading nothing

        controller.SetTarget(0, 80);
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        controller.ApplyPending();
        controller.PollRpm();
        rig.Transport.Generation = 2;
        controller.ApplyPending();
        controller.PollRpm();

        Assert.Single(rig.Transport.Writes); // the one status request that found the other receiver
        Assert.Single(rig.Changes);
    }

    [Fact]
    public void PollRpm_WhenAnotherReceiverAnswers_WithNobodyListening_StillGivesThePathUp() {
        Rig rig = NewRig(Status(2, 1000, 1000));
        var controller = new FlexReceiverController(0, rig.Transport, FlexReceiverFamily.TlFlex, rig.Clock, rig.Logger, rig.State);
        controller.SettleOwnership();

        rig.Answer(Status(OtherMac, 2, 900, 900));
        controller.PollRpm();

        Assert.False(controller.IsDrivingChain);
        Assert.Equal(0f, controller.GetRpm(0));
    }

    [Fact]
    public void ApplyPending_WhenAnotherReceiverAnswersTheIdentification_WritesNothing() {
        Rig rig = NewRig(Status(2, 1000, 1000));
        FlexReceiverController controller = rig.Build();
        controller.SetTarget(0, 40);
        controller.ApplyPending();
        rig.Transport.Clear();

        rig.Answer(Status(OtherMac, 2, 900, 900));
        rig.Transport.Generation = 1;
        controller.ApplyPending();

        Assert.Equal(new[] { (byte)0x12 }, rig.Transport.Writes.Select(w => w[0]));
        Assert.Equal(new[] { FlexReceiverChange.AnotherReceiverAnswered }, rig.Changes);
    }

    [Fact]
    public void PollRpm_WhenTheWirelessControllerTakesTheChain_StopsWriting_AndReportsTheChange() {
        Rig rig = NewRig(Status(2, 1000, 1000));
        FlexReceiverController controller = rig.Build();
        controller.SetTarget(0, 40);
        controller.ApplyPending();
        rig.Transport.Clear();

        rig.RadioTakes();
        controller.PollRpm();

        Assert.True(controller.IsLeftToWireless);
        Assert.Equal(new[] { FlexReceiverChange.TakenByRadio }, rig.Changes);
        Assert.Contains(
            "F0:a1b2c3d4e5f6 is now bound to the L-Wireless controller's master, which drives it; nothing more is sent over USB",
            rig.Logger.Messages);
        Assert.Equal(1, controller.ChannelCount); // its sensors stay; only the writes stop
        Assert.Empty(controller.DrivenSensorIds);

        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        controller.ApplyPending();
        controller.PollRpm();
        Assert.DoesNotContain(rig.Transport.Writes, w => w[0] == 0x13);
        Assert.Equal(1000f, controller.GetRpm(0)); // still read from the receiver
        Assert.Single(rig.Changes);
    }

    // The write is decided on the shared state at the moment of the write, not on what the last
    // poll saw: the radio recording the chain between two ticks stops the next tick's write,
    // which the worker runs before the poll.
    [Fact]
    public void ApplyPending_WhenTheRadioTookTheChainSinceTheLastPoll_SendsNothing_EvenBeforeTheNextPoll() {
        Rig rig = NewRig(Status(2, 1000, 1000));
        FlexReceiverController controller = rig.Build();
        controller.SetTarget(0, 40);
        controller.ApplyPending();
        rig.Transport.Clear();

        rig.RadioTakes();
        controller.SetTarget(0, 80);
        using var loop = new ControllerLoop(0, controller, rig.Logger, 1000);
        loop.Tick();

        Assert.DoesNotContain(rig.Transport.Writes, w => w[0] == 0x13);
        Assert.Equal(new[] { FlexReceiverChange.TakenByRadio }, rig.Changes);
    }

    [Fact]
    public void PollRpm_WhenTheWirelessControllerLetsTheChainGo_ResendsTheDutyAtOnce_AndReportsTheChange() {
        Rig rig = NewRig(Status(2, 1000, 1000));
        rig.RadioTakes();
        FlexReceiverController controller = rig.Build();
        controller.SetTarget(0, 40);
        controller.ApplyPending();
        Assert.DoesNotContain(rig.Transport.Writes, w => w[0] == 0x13);

        rig.RadioLetsGo();
        controller.PollRpm();

        Assert.False(controller.IsLeftToWireless);
        Assert.Equal(1, controller.ChannelCount);
        Assert.Equal(2, controller.FanSpeedCount);
        Assert.Equal(new[] { FlexReceiverChange.ReleasedByRadio }, rig.Changes);
        Assert.Contains("F0:a1b2c3d4e5f6 is no longer bound to the L-Wireless controller's master; driven over USB", rig.Logger.Messages);
        Assert.Equal(3, controller.DrivenSensorIds.Count());

        controller.ApplyPending();
        Assert.Single(rig.Transport.Writes, w => w[0] == 0x13);
    }

    [Fact]
    public void PollRpm_AChainHandedBackAfterItsDutyWasWritten_GetsItAgain() {
        Rig rig = NewRig(Status(2, 1000, 1000));
        FlexReceiverController controller = rig.Build();
        controller.SetTarget(0, 40);
        controller.ApplyPending();
        rig.RadioTakes();
        controller.PollRpm();
        rig.RadioLetsGo();
        controller.PollRpm();
        rig.Transport.Clear();

        controller.ApplyPending();

        Assert.Single(rig.Transport.Writes); // the unchanged duty, since the radio may have changed the receiver's
        Assert.Equal(new[] { FlexReceiverChange.TakenByRadio, FlexReceiverChange.ReleasedByRadio }, rig.Changes);
    }

    [Fact]
    public void Dispose_DisposesTheTransport() {
        Rig rig = NewRig(Status(1, 1));
        FlexReceiverController controller = rig.Build();

        controller.Dispose();

        Assert.True(rig.Transport.IsDisposed);
    }

    [Fact]
    public void Constructor_RejectsMissingDependencies() {
        Rig rig = NewRig(Status(1, 1));
        Assert.Throws<ArgumentNullException>(() => new FlexReceiverController(0, null!, FlexReceiverFamily.TlFlex, rig.Clock, rig.Logger, rig.State));
        Assert.Throws<ArgumentNullException>(() => new FlexReceiverController(0, rig.Transport, FlexReceiverFamily.TlFlex, null!, rig.Logger, rig.State));
        Assert.Throws<ArgumentNullException>(() => new FlexReceiverController(0, rig.Transport, FlexReceiverFamily.TlFlex, rig.Clock, null!, rig.State));
        Assert.Throws<ArgumentNullException>(() => new FlexReceiverController(0, rig.Transport, FlexReceiverFamily.TlFlex, rig.Clock, rig.Logger, null!));
        Assert.Throws<ArgumentNullException>(() => rig.Build().ReplayOnReconnect(null!));
    }
}
