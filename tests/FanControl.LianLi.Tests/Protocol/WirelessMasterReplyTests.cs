using System;
using FanControl.LianLi.Protocol;
using Xunit;

namespace FanControl.LianLi.Tests.Protocol;

public sealed class WirelessMasterReplyTests {
    [Fact]
    public void Constructor_CopiesTheAddress() {
        byte[] mac = { 1, 2, 3, 4, 5, 6 };

        var reply = new WirelessMasterReply(mac, 5);
        mac[0] = 9;

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, reply.Mac);
        Assert.Equal(5, reply.ClockMilliseconds);
    }

    [Fact]
    public void Constructor_RejectsAMissingOrMisSizedAddress() {
        Assert.Throws<ArgumentNullException>(() => new WirelessMasterReply(null!, 1));
        Assert.Throws<ArgumentException>(() => new WirelessMasterReply(new byte[5], 1));
    }

    // MasterDevice.QuerryMasterMac: a clock of 0 is "not started"; an all-zero address is no master.
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(-1, true)]
    public void IsClockRunning_IsANonZeroClock(int clock, bool running)
        => Assert.Equal(running, new WirelessMasterReply(new byte[6], clock).IsClockRunning);

    [Fact]
    public void HasAddress_IsAnyNonZeroByte() {
        Assert.False(new WirelessMasterReply(new byte[6], 1).HasAddress);
        Assert.True(new WirelessMasterReply(new byte[] { 0, 0, 0, 0, 0, 1 }, 1).HasAddress);
    }
}
