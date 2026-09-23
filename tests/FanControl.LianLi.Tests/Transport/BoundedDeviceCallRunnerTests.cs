using System;
using System.Threading;
using FanControl.LianLi.Tests.Fakes;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

/// <summary>
/// The production bound: a real thread and deadline, with a failure that lands after the caller gave
/// up written to the log with the operation it belongs to.
/// </summary>
public class BoundedDeviceCallRunnerTests {
    [Fact]
    public void TryRun_CompletesInTime_ReturnsTrue_AndLogsNothing() {
        var log = new FakeLogger();
        bool ran = false;
        int returns = 0;

        bool completed = new BoundedDeviceCallRunner(log).TryRun("open of fake/0", _ => ran = true, 1000, () => { }, () => returns++);

        Assert.True(completed);
        Assert.True(ran);
        Assert.Equal(1, returns);
        Assert.Empty(log.Messages);
    }

    [Fact]
    public void TryRun_FailureAfterTheDeadline_IsLoggedWithTheOperationAndTheBound() {
        var log = new FakeLogger();
        using var release = new ManualResetEventSlim(false);
        using var returned = new ManualResetEventSlim(false);

        bool completed = new BoundedDeviceCallRunner(log).TryRun(
            "reopen on fake/0",
            _ => { release.Wait(CancellationToken.None); throw new InvalidOperationException("the open finally failed"); },
            50,
            () => { },
            returned.Set);

        Assert.False(completed);
        release.Set();
        Assert.True(returned.Wait(5000, TestContext.Current.CancellationToken));
        Assert.Equal(
            "  reopen on fake/0 failed after its 50 ms bound had already given up on it: InvalidOperationException: the open finally failed",
            Assert.Single(log.Messages));
    }

    [Fact]
    public void Constructor_NullLog_Throws()
        => Assert.Throws<ArgumentNullException>(() => new BoundedDeviceCallRunner(null!));

    [Fact]
    public void TryRun_NullOperation_Throws()
        => Assert.Throws<ArgumentNullException>(
            () => new BoundedDeviceCallRunner(new FakeLogger()).TryRun(null!, _ => { }, 100, () => { }, () => { }));
}
