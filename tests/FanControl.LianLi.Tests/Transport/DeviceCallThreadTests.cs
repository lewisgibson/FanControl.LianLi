using System;
using System.Threading;
using FanControl.LianLi.Tests.Fakes;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

/// <summary>The threads device calls run on: reused while calls return in time, never once one is given up on.</summary>
public sealed class DeviceCallThreadTests {
    private static readonly Action<Exception> NoLateFailure =
        failure => throw new InvalidOperationException("Unexpected late failure: " + failure.Message);

    private static int RunOn(DeviceCallThreadPool pool) {
        int threadId = 0;
        Assert.True(BoundedDeviceCall.TryRun(
            _ => threadId = Environment.CurrentManagedThreadId, 5000, () => { }, NoLateFailure, () => { }, new FakeThreadCanceller(), pool));
        return threadId;
    }

    [Fact]
    public void ACallThatReturnsInTime_LeavesItsThreadForTheNextCall() {
        var pool = new DeviceCallThreadPool(DeviceCallThreadPool.DefaultMaximumIdle);

        int first = RunOn(pool);
        Assert.Equal(1, pool.IdleCount);
        int second = RunOn(pool);

        Assert.Equal(first, second);
        Assert.NotEqual(Environment.CurrentManagedThreadId, first);
        Assert.Equal(1, pool.IdleCount);
    }

    [Fact]
    public void ACallGivenUpOn_IsNeverFollowedByAnotherOnItsThread() {
        var pool = new DeviceCallThreadPool(DeviceCallThreadPool.DefaultMaximumIdle);
        using var release = new ManualResetEventSlim(false);
        using var returned = new ManualResetEventSlim(false);
        int abandonedId = 0;

        bool completed = BoundedDeviceCall.TryRun(
            _ => {
                abandonedId = Environment.CurrentManagedThreadId;
                release.Wait(TimeSpan.FromSeconds(5), CancellationToken.None);
            },
            50,
            () => { },
            NoLateFailure,
            returned.Set,
            new FakeThreadCanceller(),
            pool);

        Assert.False(completed);
        release.Set();
        Assert.True(returned.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(0, pool.IdleCount);
        Assert.NotEqual(abandonedId, RunOn(pool));
    }

    [Fact]
    public void ThreadsBeyondWhatThePoolKeeps_End() {
        var pool = new DeviceCallThreadPool(1);
        DeviceCallThread kept = pool.Take();
        DeviceCallThread extra = pool.Take();

        kept.Release();
        extra.Release();

        Assert.Equal(1, pool.IdleCount);
        Assert.Same(kept, pool.Take());
        Assert.Equal(0, pool.IdleCount);
    }

    [Fact]
    public void Wait_ReturnsFalse_WhileTheWorkIsStillRunning() {
        var pool = new DeviceCallThreadPool(1);
        DeviceCallThread thread = pool.Take();
        using var release = new ManualResetEventSlim(false);

        thread.Run(() => release.Wait(TimeSpan.FromSeconds(5)));

        Assert.False(thread.Wait(20));
        release.Set();
        Assert.True(thread.Wait(5000));
        thread.Retire();
    }

    [Fact]
    public void Wait_ReturnsTrueAtOnce_ForWorkThatHasAlreadyReturned() {
        var pool = new DeviceCallThreadPool(1);
        DeviceCallThread thread = pool.Take();
        using var ran = new ManualResetEventSlim(false);

        thread.Run(ran.Set);
        Assert.True(ran.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Thread.Sleep(100); // past the thread marking the work done

        Assert.True(thread.Wait(0));
        thread.Retire();
    }

    [Fact]
    public void TryRun_NullPool_Throws()
        => Assert.Throws<ArgumentNullException>(() => BoundedDeviceCall.TryRun(
            _ => { }, 1000, () => { }, NoLateFailure, () => { }, new FakeThreadCanceller(), null!));
}
