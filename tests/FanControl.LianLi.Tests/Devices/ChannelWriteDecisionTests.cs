using System;
using FanControl.LianLi.Devices;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

public class ChannelWriteDecisionTests {
    private static readonly DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Refresh = ChannelWriteDecision.RefreshInterval;

    [Fact]
    public void RefreshInterval_IsFifteenSeconds()
        => Assert.Equal(TimeSpan.FromSeconds(15), ChannelWriteDecision.RefreshInterval);

    [Fact]
    public void UnassignedTarget_NeverWrites() {
        // Even when long stale, a negative (unassigned) target must not write.
        Assert.False(ChannelWriteDecision.ShouldWrite(-1, -2, DateTime.MinValue, Now, Refresh));
    }

    [Fact]
    public void ChangedTarget_Writes()
        => Assert.True(ChannelWriteDecision.ShouldWrite(50, 40, Now, Now, Refresh));

    [Fact]
    public void UnchangedAndFresh_DoesNotWrite() {
        DateTime lastWrite = Now - TimeSpan.FromSeconds(14);
        Assert.False(ChannelWriteDecision.ShouldWrite(50, 50, lastWrite, Now, Refresh));
    }

    [Fact]
    public void UnchangedButStale_Writes() {
        DateTime lastWrite = Now - TimeSpan.FromSeconds(15);
        Assert.True(ChannelWriteDecision.ShouldWrite(50, 50, lastWrite, Now, Refresh));
    }

    [Fact]
    public void AClockThatWentBack_MakesTheWriteDueAtOnce_RatherThanWaitingForIt()
        => Assert.True(ChannelWriteDecision.ShouldWrite(50, 50, Now, Now.AddMinutes(-10), Refresh));
}
