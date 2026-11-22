using System;
using FanControl.LianLi.Tests.Fakes;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

/// <summary>
/// The shared retry schedule: immediate first attempt, then gaps in milliseconds that double to a
/// ceiling, and a reset that puts it back to immediate.
/// </summary>
public class DoublingBackoffTests {
    private readonly FakeDeviceCallClock _clock = new FakeDeviceCallClock();

    // How long after the last attempt the next offer attempts, offering every millisecond.
    private long WaitUntilNextAttempt(DoublingBackoff backoff) {
        long start = _clock.NowMilliseconds;
        while (!backoff.ShouldAttempt()) {
            _clock.NowMilliseconds++;
        }

        return _clock.NowMilliseconds - start;
    }

    [Fact]
    public void FirstOfferAlwaysAttempts() {
        var backoff = new DoublingBackoff(10, 640, _clock);

        Assert.True(backoff.ShouldAttempt());
    }

    [Fact]
    public void GapsDoubleFromTheFirstUpToTheCeiling() {
        var backoff = new DoublingBackoff(10, 40, _clock);

        Assert.True(backoff.ShouldAttempt());
        Assert.Equal(10, WaitUntilNextAttempt(backoff));
        Assert.Equal(20, WaitUntilNextAttempt(backoff));
        Assert.Equal(40, WaitUntilNextAttempt(backoff));
        Assert.Equal(40, WaitUntilNextAttempt(backoff)); // held at the ceiling
    }

    [Fact]
    public void HowOftenItIsOffered_DoesNotChangeTheSchedule() {
        var backoff = new DoublingBackoff(10, 40, _clock);
        Assert.True(backoff.ShouldAttempt());

        for (int offer = 0; offer < 1000; offer++) {
            Assert.False(backoff.ShouldAttempt()); // any number of offers within the gap
        }

        _clock.NowMilliseconds = 10;
        Assert.True(backoff.ShouldAttempt());
    }

    [Fact]
    public void ADeviceGoneLongIsTriedAtLeastEveryHalfMinute() {
        var backoff = new DoublingBackoff(
            DoublingBackoff.DeviceFirstGapMilliseconds, DoublingBackoff.DeviceMaximumGapMilliseconds, _clock);
        Assert.True(backoff.ShouldAttempt());

        Assert.Equal(5000, WaitUntilNextAttempt(backoff));
        Assert.Equal(10000, WaitUntilNextAttempt(backoff));
        Assert.Equal(20000, WaitUntilNextAttempt(backoff));
        for (int attempt = 0; attempt < 20; attempt++) {
            Assert.Equal(30000, WaitUntilNextAttempt(backoff));
        }
    }

    [Fact]
    public void ResetPutsItBackToImmediate() {
        var backoff = new DoublingBackoff(10, 640, _clock);
        backoff.ShouldAttempt();
        Assert.False(backoff.ShouldAttempt());

        backoff.Reset();

        Assert.True(backoff.ShouldAttempt());
        Assert.Equal(10, WaitUntilNextAttempt(backoff)); // and the gap starts over too
    }

    [Fact]
    public void ACeilingNearTheLargestGap_DoesNotOverflow() {
        var backoff = new DoublingBackoff(int.MaxValue / 2 + 1, int.MaxValue, _clock);
        Assert.True(backoff.ShouldAttempt());
        _clock.NowMilliseconds += int.MaxValue / 2 + 1;
        Assert.True(backoff.ShouldAttempt());
        _clock.NowMilliseconds += int.MaxValue;
        Assert.True(backoff.ShouldAttempt()); // the doubled gap was held at the ceiling, not wrapped
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    public void RejectsAFirstGapBelowOne(int initialGap, int maximumGap)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new DoublingBackoff(initialGap, maximumGap, _clock));

    [Fact]
    public void RejectsACeilingBelowTheFirstGap()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new DoublingBackoff(10, 9, _clock));

    [Fact]
    public void RejectsNoClock()
        => Assert.Throws<ArgumentNullException>(() => new DoublingBackoff(10, 640, null!));
}
