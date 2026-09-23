using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

public class StopwatchDeviceCallClockTests {
    [Fact]
    public void NowMilliseconds_NeverRunsBackwards() {
        long first = StopwatchDeviceCallClock.Instance.NowMilliseconds;
        long second = StopwatchDeviceCallClock.Instance.NowMilliseconds;

        Assert.True(first >= 0);
        Assert.True(second >= first);
    }
}
