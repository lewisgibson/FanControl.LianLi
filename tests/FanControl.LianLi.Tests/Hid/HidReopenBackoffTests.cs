using System.Collections.Generic;
using FanControl.LianLi.Hid;
using Xunit;

namespace FanControl.LianLi.Tests.Hid;

public class HidReopenBackoffTests {
    [Fact]
    public void FirstFaultedTransfer_AttemptsImmediately() {
        var backoff = new HidReopenBackoff();

        Assert.True(backoff.ShouldAttempt());
    }

    [Fact]
    public void AfterFirstAttempt_SkipsInitialGap_ThenAttempts() {
        var backoff = new HidReopenBackoff();
        backoff.ShouldAttempt();

        for (int skipped = 0; skipped < HidReopenBackoff.InitialGap; skipped++) {
            Assert.False(backoff.ShouldAttempt());
        }

        Assert.True(backoff.ShouldAttempt());
    }

    [Fact]
    public void GapDoublesPerFailedAttempt_UpToMaximum() {
        var backoff = new HidReopenBackoff();
        var gaps = new List<int>();

        // Walk enough attempts to reach and hold the cap; count the skips between attempts.
        int skips = 0;
        while (gaps.Count < 10) {
            if (backoff.ShouldAttempt()) {
                if (skips > 0 || gaps.Count > 0) {
                    gaps.Add(skips);
                }

                skips = 0;
            } else {
                skips++;
            }
        }

        Assert.Equal(new[] { 10, 20, 40, 80, 160, 320, 640, 640, 640, 640 }, gaps);
    }

    [Fact]
    public void Reset_RestartsWithAnImmediateAttempt_AndTheInitialGap() {
        var backoff = new HidReopenBackoff();
        backoff.ShouldAttempt();
        for (int i = 0; i < HidReopenBackoff.InitialGap; i++) {
            backoff.ShouldAttempt();
        }

        backoff.ShouldAttempt(); // second attempt: the gap has doubled

        backoff.Reset();

        Assert.True(backoff.ShouldAttempt());
        for (int skipped = 0; skipped < HidReopenBackoff.InitialGap; skipped++) {
            Assert.False(backoff.ShouldAttempt());
        }

        Assert.True(backoff.ShouldAttempt());
    }

    [Fact]
    public void Constants_MatchTheDocumentedSchedule() {
        // Ten faulted transfers is ~5s at two transfers per one-second tick; 640 is ~5 minutes.
        Assert.Equal(10, HidReopenBackoff.InitialGap);
        Assert.Equal(640, HidReopenBackoff.MaximumGap);
    }
}
