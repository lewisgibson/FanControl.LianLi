using System;
using FanControl.LianLi.Protocol;
using Xunit;

namespace FanControl.LianLi.Tests.Protocol;

public sealed class WirelessSavedEffectTests {
    [Fact]
    public void Constructor_KeepsItsOwnCopyOfTheBytes() {
        byte[] data = { 9, 8, 7 };
        byte[] index = { 1, 2, 3, 4 };

        var effect = new WirelessSavedEffect(data, index, totalFrame: 10, totalSubFrame: 2, ledNum: 48, interval: 40, subInterval: 20);
        data[0] = 0;
        index[0] = 0;

        Assert.Equal(new byte[] { 9, 8, 7 }, effect.Data);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, effect.EffectIndex);
        Assert.Equal(10, effect.TotalFrame);
        Assert.Equal(2, effect.TotalSubFrame);
        Assert.Equal(48, effect.LedNum);
        Assert.Equal(40, effect.Interval);
        Assert.Equal(20, effect.SubInterval);
    }

    // MasterDevice.SyncStrimmer_22 changes only the interval.
    [Fact]
    public void WithInterval_ChangesOnlyTheInterval() {
        var effect = new WirelessSavedEffect(new byte[] { 9 }, new byte[] { 1, 2, 3, 4 }, 10, 2, 48, 40, 20);

        WirelessSavedEffect retimed = effect.WithInterval(12.5);

        Assert.Equal(12.5, retimed.Interval);
        Assert.Equal(40, effect.Interval);
        Assert.Equal(effect.Data, retimed.Data);
        Assert.Equal(effect.EffectIndex, retimed.EffectIndex);
        Assert.Equal(10, retimed.TotalFrame);
        Assert.Equal(2, retimed.TotalSubFrame);
        Assert.Equal(48, retimed.LedNum);
        Assert.Equal(20, retimed.SubInterval);
    }

    [Fact]
    public void Constructor_RejectsAnEffectItCouldNotStream() {
        byte[] index = { 1, 2, 3, 4 };

        Assert.Throws<ArgumentNullException>(() => new WirelessSavedEffect(null!, index, 1, 1, 1, 1, 1));
        Assert.Throws<ArgumentNullException>(() => new WirelessSavedEffect(new byte[] { 1 }, null!, 1, 1, 1, 1, 1));
        Assert.Throws<ArgumentException>(() => new WirelessSavedEffect(new byte[] { 1 }, new byte[] { 1, 2, 3 }, 1, 1, 1, 1, 1));
        Assert.Throws<ArgumentException>(() => new WirelessSavedEffect(Array.Empty<byte>(), index, 1, 1, 1, 1, 1));
    }
}
