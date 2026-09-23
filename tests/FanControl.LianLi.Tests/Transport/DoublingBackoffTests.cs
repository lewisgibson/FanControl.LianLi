using System;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

/// <summary>
/// The shared retry schedule: immediate first attempt, then gaps that double to a ceiling, and a
/// reset that puts it back to immediate.
/// </summary>
public class DoublingBackoffTests {
    // How many offers it takes to reach the next attempt, counting the attempting offer itself.
    private static int OffersUntilNextAttempt(DoublingBackoff backoff) {
        int offers = 1;
        while (!backoff.ShouldAttempt()) {
            offers++;
        }

        return offers;
    }

    [Fact]
    public void FirstOfferAlwaysAttempts() {
        var backoff = new DoublingBackoff(10, 640);

        Assert.True(backoff.ShouldAttempt());
    }

    [Fact]
    public void GapsDoubleFromTheFirstUpToTheCeiling() {
        var backoff = new DoublingBackoff(10, 40);

        Assert.True(backoff.ShouldAttempt());
        Assert.Equal(11, OffersUntilNextAttempt(backoff)); // ten skipped, then the attempt
        Assert.Equal(21, OffersUntilNextAttempt(backoff));
        Assert.Equal(41, OffersUntilNextAttempt(backoff));
        Assert.Equal(41, OffersUntilNextAttempt(backoff)); // held at the ceiling
    }

    [Fact]
    public void ResetPutsItBackToImmediate() {
        var backoff = new DoublingBackoff(10, 640);
        backoff.ShouldAttempt();
        Assert.False(backoff.ShouldAttempt());

        backoff.Reset();

        Assert.True(backoff.ShouldAttempt());
        Assert.Equal(11, OffersUntilNextAttempt(backoff)); // and the gap starts over too
    }

    [Fact]
    public void AGapOfOneAttemptsEveryOtherOffer() {
        var backoff = new DoublingBackoff(1, 1);

        Assert.True(backoff.ShouldAttempt());
        Assert.False(backoff.ShouldAttempt());
        Assert.True(backoff.ShouldAttempt());
        Assert.False(backoff.ShouldAttempt());
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    public void RejectsAFirstGapBelowOne(int initialGap, int maximumGap)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new DoublingBackoff(initialGap, maximumGap));

    [Fact]
    public void RejectsACeilingBelowTheFirstGap()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new DoublingBackoff(10, 9));
}
