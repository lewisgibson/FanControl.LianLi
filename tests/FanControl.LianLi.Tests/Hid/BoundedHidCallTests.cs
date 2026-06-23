using System;
using System.Threading;
using FanControl.LianLi.Hid;
using Xunit;

namespace FanControl.LianLi.Tests.Hid;

public class BoundedHidCallTests {
    [Fact]
    public void TryRun_CompletesInTime_ReturnsTrue_AndDoesNotCancel() {
        bool cancelled = false;

        bool completed = BoundedHidCall.TryRun(() => { }, 1000, () => cancelled = true);

        Assert.True(completed);
        Assert.False(cancelled);
    }

    [Fact]
    public void TryRun_CallThrows_RethrowsOnCallerThread() {
        var thrown = new InvalidOperationException("boom");

        InvalidOperationException caught = Assert.Throws<InvalidOperationException>(
            () => BoundedHidCall.TryRun(() => throw thrown, 1000, () => { }));

        Assert.Same(thrown, caught);
    }

    [Fact]
    public void TryRun_CallBlocks_ReturnsFalse_AndInvokesOnTimeout() {
        using var release = new ManualResetEventSlim(false);
        bool cancelled = false;

        // The call blocks past the timeout; TryRun must give up and invoke onTimeout. The real
        // transport's onTimeout cancels the I/O so the blocked native call returns - here releasing
        // the gate stands in for that, letting the abandoned thread unwind and the test end cleanly.
        bool completed = BoundedHidCall.TryRun(
            () => release.Wait(),
            50,
            () => { cancelled = true; release.Set(); });

        Assert.False(completed);
        Assert.True(cancelled);
    }

    [Fact]
    public void TryRun_NullCall_Throws()
        => Assert.Throws<ArgumentNullException>(() => BoundedHidCall.TryRun(null!, 100, () => { }));

    [Fact]
    public void TryRun_NullOnTimeout_Throws()
        => Assert.Throws<ArgumentNullException>(() => BoundedHidCall.TryRun(() => { }, 100, null!));
}
