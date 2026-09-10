using System;
using System.Collections.Generic;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Tests.Fakes;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

public class Galahad2ControllerTests {
    private const int FanChannel = 0;
    private const int PumpChannel = 1;

    private static (Galahad2Controller controller, FakeHidTransport transport, FakeClock clock) NewController() {
        var transport = new FakeHidTransport();
        var clock = new FakeClock();
        var controller = new Galahad2Controller(0, transport, clock, new FakeLogger());
        return (controller, transport, clock);
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
        controller.ReplayOnReconnect(() => replayedAt.Add(transport.Writes.Count));
        controller.SetTarget(FanChannel, 50);
        controller.SetTarget(PumpChannel, 70);
        controller.ApplyPending();
        transport.Clear();

        // The transport reopened the cooler (a wake): no setup writes of its own, so the saved look
        // replays first, then both unchanged duties are re-sent - the cooler may have reset.
        transport.Generation = 1;
        controller.ApplyPending();

        Assert.Equal(new[] { 0 }, replayedAt);
        Assert.Equal(2, transport.Writes.Count);
        Assert.Equal(0x8B, transport.Writes[0][1]); // fan
        Assert.Equal(0x8A, transport.Writes[1][1]); // pump
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
}
