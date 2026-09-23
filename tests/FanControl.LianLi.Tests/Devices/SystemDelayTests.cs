using System;
using System.Diagnostics;
using FanControl.LianLi.Devices;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

public class SystemDelayTests {
    [Fact]
    public void Wait_BlocksForAtLeastTheDuration() {
        var watch = Stopwatch.StartNew();

        new SystemDelay().Wait(TimeSpan.FromMilliseconds(20));

        Assert.True(watch.ElapsedMilliseconds >= 15);
    }
}
