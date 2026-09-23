using System;
using System.Collections.Generic;
using System.Linq;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Tests.Fakes;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

public class TlFanControllerTests {
    // A handshake reply: one 3-byte record per detected fan - header (detected bit | port | fan),
    // then the big-endian RPM.
    private static byte[] HandshakeReply(params (int port, int fan, int rpm)[] fans) {
        var payload = new List<byte>();
        foreach ((int port, int fan, int rpm) in fans) {
            payload.Add((byte)(0x80 | ((port & 0x0F) << 4) | (fan & 0x0F)));
            payload.Add((byte)((rpm >> 8) & 0xFF));
            payload.Add((byte)(rpm & 0xFF));
        }

        return CommandPacket.Build(0xA1, payload.ToArray());
    }

    private static (TlFanController controller, FakeDeviceTransport transport, FakeClock clock) NewController(
        params (int port, int fan, int rpm)[] fans) {
        var transport = new FakeDeviceTransport();
        transport.ReadReplies.Enqueue(HandshakeReply(fans)); // consumed by the construction handshake
        var clock = new FakeClock();
        var controller = new TlFanController(0, transport, clock, new FakeLogger());
        return (controller, transport, clock);
    }

    [Fact]
    public void Constructor_DiscoversFansAndTakesSoftwareControl() {
        var (controller, transport, _) = NewController((0, 0, 1000), (0, 1, 1100));

        Assert.Equal(2, controller.ChannelCount);
        Assert.Equal(0xA1, transport.Writes[0][1]); // handshake request first
        // Then a motherboard-sync-off command per fan (command 0xB1, sync bit clear).
        Assert.Equal(0xB1, transport.Writes[1][1]);
        Assert.Equal(0x00, transport.Writes[1][6] & 0x80); // sync bit off
        Assert.Equal(3, transport.Writes.Count); // handshake + two fans
    }

    [Fact]
    public void Constructor_OrdersChannelsByPortThenFan() {
        var (controller, _, _) = NewController((1, 0, 900), (0, 1, 1100), (0, 0, 1000));

        Assert.Equal("LianLi/0/p0f0/ctl", controller.Describe(0).ControlId);
        Assert.Equal("LianLi/0/p0f1/ctl", controller.Describe(1).ControlId);
        Assert.Equal("LianLi/0/p1f0/ctl", controller.Describe(2).ControlId);
    }

    [Fact]
    public void ApplyPending_AfterTransportReopened_RetakesSoftwareControlThenResendsEveryFan() {
        var (controller, transport, _) = NewController((0, 0, 1000), (0, 1, 1100));
        var replayedAt = new List<int>();
        controller.ReplayOnReconnect(() => replayedAt.Add(transport.Writes.Count));
        controller.SetTarget(0, 50);
        controller.SetTarget(1, 60);
        controller.ApplyPending();
        transport.Clear();

        // The transport reopened the hub (a wake): motherboard sync is switched off per fan again,
        // then the saved look replays, then both unchanged duties are re-sent - the hub may have reset.
        transport.Generation = 1;
        controller.ApplyPending();

        Assert.Equal(0xB1, transport.Writes[0][1]);
        Assert.Equal(0x00, transport.Writes[0][6] & 0x80);
        Assert.Equal(0xB1, transport.Writes[1][1]);
        Assert.Equal(new[] { 2 }, replayedAt);
        Assert.Equal(4, transport.Writes.Count); // two sync-off, two set-speed
    }

    [Fact]
    public void ApplyPending_WritesSetSpeedForTheFan() {
        var (controller, transport, _) = NewController((0, 0, 1000));
        transport.Clear();

        controller.SetTarget(0, 50);
        controller.ApplyPending();

        Assert.Single(transport.Writes);
        Assert.Equal(0xAA, transport.Writes[0][1]);  // SetFanSpeed
        Assert.Equal(0x00, transport.Writes[0][6]);  // address: port 0, fan 0
        Assert.Equal(50, transport.Writes[0][7]);    // duty 50 within the 12-100 window
    }

    [Fact]
    public void ApplyPending_ClampsAndIdlesTheDuty() {
        var (controller, transport, _) = NewController((0, 0, 1000));
        transport.Clear();

        controller.SetTarget(0, 0); // 0% idles the fan at the wire value 1
        controller.ApplyPending();

        Assert.Equal(1, transport.Writes[0][7]);
    }

    [Fact]
    public void ApplyPending_UnchangedAndFresh_WritesNothing() {
        var (controller, transport, _) = NewController((0, 0, 1000));
        controller.SetTarget(0, 50);
        controller.ApplyPending();
        transport.Clear();

        controller.ApplyPending();

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public void ApplyPending_StaleAfterFifteenSeconds_Reasserts() {
        var (controller, transport, clock) = NewController((0, 0, 1000));
        controller.SetTarget(0, 50);
        controller.ApplyPending();
        transport.Clear();

        clock.Advance(TimeSpan.FromSeconds(15));
        controller.ApplyPending();

        Assert.Single(transport.Writes);
        Assert.Equal(0xAA, transport.Writes[0][1]);
    }

    [Fact]
    public void PollRpm_MatchesReplyRecordsBackToChannels() {
        var (controller, transport, _) = NewController((0, 0, 1000), (0, 1, 1100));
        transport.ReadReplies.Enqueue(HandshakeReply((0, 0, 1500), (0, 1, 1600)));

        controller.PollRpm();

        Assert.Equal(1500f, controller.GetRpm(0));
        Assert.Equal(1600f, controller.GetRpm(1));
    }

    // The hub answers every command, so the answers to the speed writes sit ahead of the handshake's:
    // the poll reads past them to the reply that echoes the handshake.
    [Fact]
    public void PollRpm_ReadsPastTheAnswersQueuedAheadOfTheHandshakeReply() {
        var (controller, transport, _) = NewController((0, 0, 1000));
        transport.ReadReplies.Enqueue(CommandPacket.Build(TlSpeedCommand, 0x80, 0xC3, 0x50));
        transport.ReadReplies.Enqueue(CommandPacket.Build(0xB1, 0x80));
        transport.ReadReplies.Enqueue(HandshakeReply((0, 0, 1500)));
        int reads = transport.InterruptReadCount;

        controller.PollRpm();

        Assert.Equal(1500f, controller.GetRpm(0));
        Assert.Equal(1, controller.ChannelCount);
        Assert.Equal(reads + 3, transport.InterruptReadCount);
    }

    // With no handshake reply among the reports a poll reads, it takes no reading and adds no fan,
    // and a bounded number of reports is read.
    [Fact]
    public void PollRpm_WithNoHandshakeReply_ReadsABoundedNumber_AndAddsNothing() {
        var (controller, transport, _) = NewController((0, 0, 1000));
        transport.ReadReplies.Enqueue(HandshakeReply((0, 0, 1500)));
        controller.PollRpm();
        for (int i = 0; i < 40; i++) {
            transport.ReadReplies.Enqueue(CommandPacket.Build(TlSpeedCommand, 0xB3, 0x05, 0xDC));
        }

        int reads = transport.InterruptReadCount;

        controller.PollRpm();

        Assert.Equal(reads + 32, transport.InterruptReadCount);
        Assert.Equal(1, controller.ChannelCount);
        Assert.Equal(1500f, controller.GetRpm(0));
    }

    private static void Poll(TlFanController controller, FakeDeviceTransport transport, int polls, params (int port, int fan, int rpm)[] fans) {
        for (int poll = 0; poll < polls; poll++) {
            transport.ReadReplies.Enqueue(HandshakeReply(fans));
            controller.PollRpm();
        }
    }

    // A fan that first answers a later poll - slow to come back after a wake - becomes a channel after
    // the others once three polls in a row report it (TLFanController.updateFanGroupStatus), taken
    // under software control, with its own address as its sensor identity.
    [Fact]
    public void PollRpm_AFanFirstHeardLater_IsAdded_OnTheThirdPollInARow_UnderSoftwareControl() {
        var (controller, transport, _) = NewController((0, 0, 1000));
        transport.Writes.Clear();

        Poll(controller, transport, 2, (0, 0, 1500), (3, 3, 1200));
        Assert.Equal(1, controller.ChannelCount);
        Assert.DoesNotContain(transport.Writes, w => w[1] == 0xB1);

        Poll(controller, transport, 1, (0, 0, 1500), (3, 3, 1200));

        Assert.Equal(1500f, controller.GetRpm(0));
        Assert.Equal(2, controller.ChannelCount);
        Assert.Equal("LianLi/0/p3f3/ctl", controller.Describe(1).ControlId);
        Assert.Equal(1200f, controller.GetRpm(1));
        Assert.Equal(0xB1, transport.Writes[3][1]); // after the third poll's handshake, sync off for the new fan

        controller.SetTarget(1, 60);
        controller.ApplyPending();
        Assert.Contains(transport.Writes, w => w[1] == TlSpeedCommand && w[6] == 0x33);
    }

    // A fan added before a later fan's setup write fails is still reported, since the next poll finds
    // it already a channel.
    [Fact]
    public void PollRpm_AFanAddedBeforeALaterWriteFails_IsStillReported() {
        var (controller, transport, _) = NewController((0, 0, 1000));
        int reports = 0;
        controller.TopologyChanged += (_, _) => reports++;
        Poll(controller, transport, 2, (0, 0, 1500), (0, 1, 1100), (0, 2, 1200));
        transport.ReadReplies.Enqueue(HandshakeReply((0, 0, 1500), (0, 1, 1100), (0, 2, 1200)));
        int syncWrites = 0;
        transport.FailWrite = packet => packet[1] == 0xB1 && ++syncWrites == 2;

        Assert.Throws<System.IO.IOException>(() => controller.PollRpm());

        Assert.Equal(2, controller.ChannelCount);
        Assert.Equal(1, reports);
    }

    // One reply that misses a new fan starts its count again: a garbled reply after a wake that names
    // a fan twice is not enough to add it.
    [Fact]
    public void PollRpm_AFanNotReportedThreePollsInARow_IsNotAdded() {
        var (controller, transport, _) = NewController((0, 0, 1000));
        int reports = 0;
        controller.TopologyChanged += (_, _) => reports++;

        Poll(controller, transport, 2, (0, 0, 1500), (1, 3, 1200));
        Poll(controller, transport, 1, (0, 0, 1500));
        Poll(controller, transport, 2, (0, 0, 1500), (1, 3, 1200));

        Assert.Equal(1, controller.ChannelCount);
        Assert.Equal(0, reports);
    }

    // The TL set-speed command byte, as the encoder writes it.
    private static readonly byte TlSpeedCommand = TlFanProtocol.EncodeSetFanSpeed(0, 0, 50)[1];

    [Fact]
    public void PollRpm_IgnoresImplausibleReading_AndKeepsLastGood() {
        var (controller, transport, _) = NewController((0, 0, 1000));
        transport.ReadReplies.Enqueue(HandshakeReply((0, 0, 1500)));
        controller.PollRpm();

        transport.ReadReplies.Enqueue(HandshakeReply((0, 0, 50000))); // garbage idle-buffer read
        controller.PollRpm();

        Assert.Equal(1500f, controller.GetRpm(0));
    }

    [Fact]
    public void Dispose_DisposesTransport() {
        var (controller, transport, _) = NewController((0, 0, 1000));
        controller.Dispose();
        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public void PollRpm_LogsImplausibleOnsetOnce_ThenRecovery() {
        var transport = new FakeDeviceTransport();
        transport.ReadReplies.Enqueue(HandshakeReply((0, 2, 1000)));
        var logger = new FakeLogger();
        var controller = new TlFanController(0, transport, new FakeClock(), logger);
        transport.ReadReplies.Enqueue(HandshakeReply((0, 2, 50000)));
        transport.ReadReplies.Enqueue(HandshakeReply((0, 2, 50000)));
        transport.ReadReplies.Enqueue(HandshakeReply((0, 2, 1200)));

        for (int i = 0; i < 3; i++) {
            controller.PollRpm();
        }

        Assert.Equal(1200f, controller.GetRpm(0));
        Assert.Single(logger.Messages, m => m.Contains("T0:0/2 implausible rpm 50000"));
        Assert.Single(logger.Messages, m => m.Contains("T0:0/2 rpm recovered (1200)"));
    }

    [Fact]
    public void EveryDiscoveredFanIsPopulated() {
        var (controller, _, _) = NewController((0, 0, 1000), (1, 3, 1000));

        Assert.True(controller.IsChannelPopulated(0));
        Assert.True(controller.IsChannelPopulated(1));
    }

    [Fact]
    public void ReleaseChannel_StopsAssertingTheDuty() {
        var (controller, transport, _) = NewController((0, 0, 1000));
        controller.SetTarget(0, 60);
        controller.ApplyPending();
        transport.Clear();

        controller.ReleaseChannel(0);
        controller.ApplyPending();

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public void Constructor_RejectsMissingDependencies() {
        Assert.Throws<ArgumentNullException>(() => new TlFanController(0, null!, new FakeClock(), new FakeLogger()));
        Assert.Throws<ArgumentNullException>(() => new TlFanController(0, new FakeDeviceTransport(), null!, new FakeLogger()));
        Assert.Throws<ArgumentNullException>(() => new TlFanController(0, new FakeDeviceTransport(), new FakeClock(), null!));
        var (controller, _, _) = NewController((0, 0, 1000));
        Assert.Throws<ArgumentNullException>(() => controller.ReplayOnReconnect(null!));
    }

    [Fact]
    public void ApplyPending_AfterAReopen_WithNoReplayRegistered_StillRetakesControl() {
        var (controller, transport, _) = NewController((0, 0, 1000));
        controller.SetTarget(0, 50);
        controller.ApplyPending();
        transport.Clear();

        transport.Generation = 1;
        controller.ApplyPending();

        Assert.NotEmpty(transport.Writes);
    }

    [Fact]
    public void PollRpm_TwoFansChangingStateInOnePoll_AreBothLogged() {
        var transport = new FakeDeviceTransport();
        transport.ReadReplies.Enqueue(HandshakeReply((0, 0, 1000), (0, 1, 1000)));
        var logger = new FakeLogger();
        var controller = new TlFanController(0, transport, new FakeClock(), logger);
        transport.ReadReplies.Enqueue(HandshakeReply((0, 0, 50000), (0, 1, 50000)));
        transport.ReadReplies.Enqueue(HandshakeReply((0, 0, 1100), (0, 1, 1200)));

        controller.PollRpm();
        controller.PollRpm();

        Assert.Equal(2, logger.Messages.Count(m => m.Contains("implausible rpm")));
        Assert.Equal(2, logger.Messages.Count(m => m.Contains("rpm recovered")));
    }
}
