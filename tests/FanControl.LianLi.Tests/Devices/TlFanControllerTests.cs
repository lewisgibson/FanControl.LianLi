using System;
using System.Collections.Generic;
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

    private static (TlFanController controller, FakeHidTransport transport, FakeClock clock) NewController(
        params (int port, int fan, int rpm)[] fans) {
        var transport = new FakeHidTransport();
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

    [Fact]
    public void PollRpm_IgnoresAReadingForAnUndiscoveredFan() {
        var (controller, transport, _) = NewController((0, 0, 1000));
        transport.ReadReplies.Enqueue(HandshakeReply((0, 0, 1500), (3, 3, 9999)));

        controller.PollRpm(); // the (3,3) record has no channel and must be ignored, not throw

        Assert.Equal(1500f, controller.GetRpm(0));
    }

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
}
