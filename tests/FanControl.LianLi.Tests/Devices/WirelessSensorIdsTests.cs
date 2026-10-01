using System;
using FanControl.LianLi.Devices;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

/// <summary>The ids a device keyed on its RF address gets, shared by the wireless controller and a FLEX receiver, and reading the address back from a group's control id.</summary>
public sealed class WirelessSensorIdsTests {
    [Fact]
    public void AGroupsControlAndFans_AreKeyedOnTheAddress() {
        Assert.Equal("LianLi/wa1b2c3d4e5f6/ctl", WirelessSensorIds.GroupControlId("a1b2c3d4e5f6"));
        Assert.Equal("LianLi/wa1b2c3d4e5f6/f0/fan", WirelessSensorIds.FanId("a1b2c3d4e5f6", 0));
        Assert.Equal("LianLi/wa1b2c3d4e5f6/f3/fan", WirelessSensorIds.FanId("a1b2c3d4e5f6", 3));
    }

    [Theory]
    [InlineData("LianLi/wa1b2c3d4e5f6/ctl", "a1b2c3d4e5f6")]
    [InlineData("LianLi/w000000000000/ctl", "000000000000")]
    [InlineData("LianLi/wa1b2c3d4e5f6/front/ctl", null)]
    [InlineData("LianLi/wa1b2c3d4e5f6/pump/ctl", null)]
    [InlineData("LianLi/wa1b2c3d4e5f6/f0/fan", null)]
    [InlineData("LianLi/0/ch0/ctl", null)]
    [InlineData("LianLi/wA1B2C3D4E5F6/ctl", null)]
    [InlineData("LianLi/wa1b2c3d4e5g6/ctl", null)]
    [InlineData("LianLi/wa1b2c3d4e5f/ctl", null)]
    [InlineData("Other/wa1b2c3d4e5f6/ctl", null)]
    [InlineData("LianLX/wa1b2c3d4e5f6/ctl", null)]
    [InlineData("LianLi/wa1b2c3d4e5f-/ctl", null)]
    [InlineData("LianLi/wa1b2c3d4e5f6/ctx", null)]
    public void TheAddress_IsReadBackFromAGroupsControlIdOnly(string controlId, string? expected) {
        Assert.Equal(expected, WirelessSensorIds.TryChainAddress(controlId));
    }

    [Fact]
    public void TryChainAddress_RejectsNull() {
        Assert.Throws<ArgumentNullException>(() => WirelessSensorIds.TryChainAddress(null!));
    }
}
