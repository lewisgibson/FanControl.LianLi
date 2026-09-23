using System;
using System.Threading;
using FanControl.LianLi.Tests.Fakes;
using FanControl.LianLi.Worker;
using Xunit;

namespace FanControl.LianLi.Tests.Worker;

/// <summary>One controller's loop: its ticks, its wake, and the bounded two-party stop.</summary>
public sealed class ControllerLoopTests {
    [Fact]
    public void NeverStarted_StoppingReleasesTheControllerAtOnce() {
        var device = new FakeFanDevice("c0");
        var loop = new ControllerLoop(0, device, new FakeLogger(), 1000);

        loop.Dispose();

        Assert.True(device.IsDisposed);
    }

    [Fact]
    public void Started_StoppingWaitsForTheLoop_ThenReleasesTheController() {
        var device = new FakeFanDevice("c0");
        var loop = new ControllerLoop(0, device, new FakeLogger(), 1000);
        loop.Start();
        Assert.True(SpinWait.SpinUntil(() => device.PollCount >= 1, TimeSpan.FromSeconds(5)));

        loop.Dispose();

        Assert.True(device.IsDisposed);
    }

    [Fact]
    public void Tick_AppliesThenPolls() {
        var device = new FakeFanDevice("c0");
        using var loop = new ControllerLoop(0, device, new FakeLogger(), 1000);

        loop.Tick();

        Assert.Equal(1, device.ApplyCount);
        Assert.Equal(1, device.PollCount);
    }

    [Fact]
    public void StoppingTwice_AndWakingAfterStopping_DoNothing() {
        var device = new FakeFanDevice("c0");
        var loop = new ControllerLoop(0, device, new FakeLogger(), 1000);
        loop.SignalStop();
        loop.SignalStop();
        loop.Wake();
        loop.WaitForStop(100);
        loop.WaitForStop(100);

        Assert.True(device.IsDisposed);
    }

    [Fact]
    public void AControllerThatThrowsOnDispose_IsLogged_NotLetEscapeTheLoopThread() {
        var device = new FakeFanDevice("c0") { DisposeFault = new InvalidOperationException("handle gone") };
        var logger = new FakeLogger();
        var loop = new ControllerLoop(2, device, logger, 1000);
        loop.Start();
        Assert.True(SpinWait.SpinUntil(() => device.PollCount >= 1, TimeSpan.FromSeconds(5)));

        loop.Dispose();

        Assert.True(loop.HasStopped);
        Assert.Contains("release err C2: handle gone", logger.Messages);
    }

    [Fact]
    public void WakingAndStopping_WhileTheLoopReleasesItself_NeverTouchesADisposedEvent() {
        // The loop's thread disposes its event as its last act; a wake or a stop from another thread
        // that lands in that moment must be ignored rather than set a disposed handle.
        for (int round = 0; round < 200; round++) {
            var device = new FakeFanDevice("c0");
            var loop = new ControllerLoop(0, device, new FakeLogger(), 1);
            loop.Start();
            using var go = new ManualResetEventSlim(false);
            var waker = new Thread(() => {
                go.Wait();
                for (int i = 0; i < 1000 && !loop.HasStopped; i++) {
                    loop.Wake();
                }

                loop.Wake();
            });
            waker.Start();
            go.Set();
            loop.SignalStop();
            loop.SignalStop();
            Assert.True(loop.Join(5000));
            waker.Join();
            loop.Wake();
            Assert.True(device.IsDisposed);
        }
    }

    [Fact]
    public void Constructor_RejectsBadArguments() {
        Assert.Throws<ArgumentNullException>(() => new ControllerLoop(0, null!, new FakeLogger(), 1000));
        Assert.Throws<ArgumentNullException>(() => new ControllerLoop(0, new FakeFanDevice("c0"), null!, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ControllerLoop(0, new FakeFanDevice("c0"), new FakeLogger(), 0));
    }

    // A controller whose device call does not return while the loop is being stopped.
    private sealed class StuckDevice : FanControl.LianLi.Devices.IFanDevice {
        private readonly ManualResetEventSlim _release;

        public StuckDevice(ManualResetEventSlim release) => _release = release;

        public ManualResetEventSlim Entered { get; } = new ManualResetEventSlim(false);

        public int ChannelCount => 0;

        public bool IsChannelPopulated(int channel) => true;

        public FanControl.LianLi.Devices.ChannelDescriptor Describe(int channel) => throw new NotSupportedException();

        public void SetTarget(int channel, int duty) {
        }

        public void ReleaseChannel(int channel) {
        }

        public float GetRpm(int channel) => 0f;

        public void ApplyPending() {
            Entered.Set();
            _release.Wait();
        }

        public void PollRpm() {
        }

        public void ReplayOnReconnect(Action replay) {
        }

        public void Dispose() {
        }
    }

    [Fact]
    public void WaitForStop_ThatRunsOut_SaysTheLoopIsStillInADeviceCall() {
        using var release = new ManualResetEventSlim(false);
        var device = new StuckDevice(release);
        var logger = new FakeLogger();
        var loop = new ControllerLoop(3, device, logger, 1000);
        loop.Start();
        Assert.True(device.Entered.Wait(5000, TestContext.Current.CancellationToken));

        loop.SignalStop();
        loop.WaitForStop(20);

        Assert.Contains("C3 still inside a device call at shutdown; it is released when the call returns", logger.Messages);
        Assert.False(loop.HasStopped);
        release.Set();
    }
}
