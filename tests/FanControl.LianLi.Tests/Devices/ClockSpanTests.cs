using System;
using FanControl.LianLi.Devices;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

public sealed class ClockSpanTests {
    private static readonly DateTime Then = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Since_IsTheTimeThatPassed()
        => Assert.Equal(TimeSpan.FromSeconds(3), ClockSpan.Since(Then.AddSeconds(3), Then));

    [Fact]
    public void Since_TheSameMoment_IsZero()
        => Assert.Equal(TimeSpan.Zero, ClockSpan.Since(Then, Then));

    [Fact]
    public void Since_AMomentTheClockHasSinceGoneBackPast_IsLongAgo()
        => Assert.Equal(TimeSpan.MaxValue, ClockSpan.Since(Then.AddMinutes(-5), Then));

    [Theory]
    [InlineData(31, true)]
    [InlineData(29, false)]
    [InlineData(-1, false)] // the clock went back: recent, never too old
    public void IsOlderThan_CountsAFutureMomentAsRecent(int daysAgo, bool older)
        => Assert.Equal(older, ClockSpan.IsOlderThan(Then, Then.AddDays(-daysAgo), TimeSpan.FromDays(30)));
}
