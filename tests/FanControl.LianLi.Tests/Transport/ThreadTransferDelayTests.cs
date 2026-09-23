using System.Diagnostics;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

public class ThreadTransferDelayTests {
    [Fact]
    public void Wait_BlocksForAtLeastTheDelay() {
        var elapsed = Stopwatch.StartNew();

        ThreadTransferDelay.Instance.Wait(20);

        Assert.True(elapsed.ElapsedMilliseconds >= 15, "slept " + elapsed.ElapsedMilliseconds + " ms");
    }
}
