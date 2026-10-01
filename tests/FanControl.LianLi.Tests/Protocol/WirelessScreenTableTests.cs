using System;
using FanControl.LianLi.Protocol;
using Xunit;

namespace FanControl.LianLi.Tests.Protocol;

/// <summary>
/// The clock broadcast's per-receiver-slot screen table, every byte checked against
/// RFController.UpdateSensorDataByWiredLess: slot s at (s - 1) * 12, themes at 0-3, direction and
/// data source at 4-7 as (direction &lt;&lt; 5) | source, brightness at 8.
/// </summary>
public sealed class WirelessScreenTableTests {
    [Fact]
    public void AnEmptyTable_IsOneHundredAndSixtyEightZeros() {
        byte[] bytes = new WirelessScreenTable().ToBytes();

        Assert.Equal(168, WirelessScreenTable.Length);
        Assert.Equal(new byte[168], bytes);
    }

    // A group's entry is its slot's twelve bytes, as the broadcast carries them.
    [Fact]
    public void EntryOf_IsTheSlotsTwelveBytes_Copied() {
        var table = new WirelessScreenTable();
        table.SetFan(3, 0, 5, 2, 1, 60);
        table.SetFan(3, 2, 255, 4, 31, 80);

        byte[] entry = table.EntryOf(3);

        Assert.Equal(new byte[] { 5, 0, 255, 0, 0x41, 0, 0x9F, 0, 80, 0, 0, 0 }, entry);
        Assert.Equal(new byte[12], table.EntryOf(2));
        entry[0] = 9;
        Assert.Equal(5, table.EntryOf(3)[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => table.EntryOf(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.EntryOf(14));
    }

    [Fact]
    public void SetFan_WritesTheThemeDirectionSourceAndBrightnessOfItsSlot() {
        var table = new WirelessScreenTable();

        table.SetFan(3, 0, 5, 2, 1, 60);
        table.SetFan(3, 2, 255, 4, 31, 80); // the brightness is the group's: the last fan written sets it
        table.SetFan(13, 3, 1, 1, 0, 100);

        var expected = new byte[168];
        expected[24] = 5;
        expected[26] = 255;
        expected[28] = 0x41; // 2 << 5 | 1
        expected[30] = 0x9F; // 4 << 5 | 31
        expected[32] = 80;
        expected[144 + 3] = 1;
        expected[144 + 7] = 0x20;
        expected[144 + 8] = 100;
        Assert.Equal(expected, table.ToBytes());
    }

    // A data source too wide for its five bits spills into the direction, as L-Connect's cast lets it.
    [Fact]
    public void SetFan_CastsTheDirectionAndSourceToOneByteAsLConnectDoes() {
        var table = new WirelessScreenTable();

        table.SetFan(1, 1, 0, 7, 0xFF, 0);

        Assert.Equal(0xFF, table.ToBytes()[5]);
    }

    [Fact]
    public void ToBytes_IsACopy() {
        var table = new WirelessScreenTable();
        byte[] bytes = table.ToBytes();
        bytes[0] = 9;

        Assert.Equal(0, table.ToBytes()[0]);
    }

    // L-Connect assigns and accepts receiver slots 1-13 (GetRxUnused, UpdateSensorDataByWiredLess).
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(13, true)]
    [InlineData(14, false)]
    [InlineData(255, false)]
    public void HasSlot_IsOneToThirteen(int receiverType, bool expected)
        => Assert.Equal(expected, WirelessScreenTable.HasSlot(receiverType));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(14, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 4)]
    public void SetFan_RefusesASlotOrFanOutsideTheTable(int receiverType, int fanIndex)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new WirelessScreenTable().SetFan(receiverType, fanIndex, 0, 0, 0, 0));
}
