using System;
using System.Threading;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Tests.Fakes;
using FanControl.LianLi.Worker;
using Xunit;

namespace FanControl.LianLi.Tests.Worker;

public class KeepAliveWorkerTests {
    private static FanController NewController(int index, FakeDeviceTransport transport, FakeLogger logger)
        => new FanController(index, transport, new SlProtocol(), new bool[4], new FakeClock(), logger);

    [Fact]
    public void Tick_IsolatesAndLogsAPerControllerFault() {
        var logger = new FakeLogger();
        var badTransport = new FakeDeviceTransport { FailReads = true };
        var goodTransport = new FakeDeviceTransport();
        var goodBuffer = new byte[65];
        goodBuffer[1] = 0x05; // ch0 high
        goodBuffer[2] = 0xDC; // ch0 low -> 1500
        goodTransport.InputReport = goodBuffer;

        FanController bad = NewController(0, badTransport, logger);
        FanController good = NewController(1, goodTransport, logger);
        using var worker = new KeepAliveWorker(new[] { bad, good }, logger);

        worker.Tick();

        // The faulting controller's poll was caught and logged...
        Assert.Contains(logger.Messages, m => m.Contains("poll err C0"));
        // ...and the healthy controller was still polled despite the earlier fault.
        Assert.Equal(1500f, good.GetRpm(0));
    }

    [Fact]
    public void Start_RunsBackgroundLoopUntilDisposed() {
        var logger = new FakeLogger();
        var transport = new FakeDeviceTransport();
        var buffer = new byte[65];
        buffer[1] = 0x05; // ch0 high
        buffer[2] = 0xDC; // ch0 low -> 1500
        transport.InputReport = buffer;
        FanController controller = NewController(0, transport, logger);
        var healthy = new FakeDeviceTransport();
        var worker = new KeepAliveWorker(new[] { controller, NewController(1, healthy, logger) }, logger);

        worker.Start();
        // The loop ticks immediately; wait until it has polled at least once.
        SpinWait.SpinUntil(() => controller.GetRpm(0) == 1500f, TimeSpan.FromSeconds(2));
        worker.Dispose();

        Assert.Equal(1500f, controller.GetRpm(0));
        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public void Dispose_DisposesEveryController() {
        var t0 = new FakeDeviceTransport();
        var t1 = new FakeDeviceTransport();
        FanController c0 = NewController(0, t0, new FakeLogger());
        FanController c1 = NewController(1, t1, new FakeLogger());
        var worker = new KeepAliveWorker(new[] { c0, c1 }, new FakeLogger());

        worker.Dispose();

        Assert.True(t0.IsDisposed);
        Assert.True(t1.IsDisposed);
    }

    [Fact]
    public void Wake_TicksAtOnce_RatherThanAtTheNextInterval() {
        var logger = new FakeLogger();
        var transport = new FakeDeviceTransport();
        FanController controller = NewController(0, transport, logger);
        // An interval far longer than the test: any second tick can only have come from the wake.
        using var worker = new KeepAliveWorker(new[] { controller }, logger, tickIntervalMs: 600_000);
        worker.Start();
        Assert.True(
            SpinWait.SpinUntil(() => transport.ReadCount >= 1, TimeSpan.FromSeconds(5)),
            "the worker never ran its first tick");

        controller.SetTarget(0, 60);
        worker.Wake();

        Assert.True(
            SpinWait.SpinUntil(() => transport.ReadCount >= 2, TimeSpan.FromSeconds(5)),
            "the wake did not bring the next tick forward");
    }

    [Fact]
    public void Wake_AfterDispose_IsANoOp() {
        var worker = new KeepAliveWorker(new[] { NewController(0, new FakeDeviceTransport(), new FakeLogger()) }, new FakeLogger());
        worker.Dispose();

        worker.Wake();
    }

    [Fact]
    public void Constructor_RejectsANonPositiveInterval()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new KeepAliveWorker(Array.Empty<IFanDevice>(), new FakeLogger(), tickIntervalMs: 0));

    [Fact]
    public void Dispose_WhenBackgroundReadBlocked_ReturnsPromptly() {
        var logger = new FakeLogger();
        using var gate = new ManualResetEventSlim(false);
        var transport = new FakeDeviceTransport { BlockReadsUntil = gate };
        FanController controller = NewController(0, transport, logger);
        var healthy = new FakeDeviceTransport();
        var worker = new KeepAliveWorker(new[] { controller, NewController(1, healthy, logger) }, logger);

        worker.Start();
        Assert.True(
            SpinWait.SpinUntil(() => transport.ReadCount >= 1, TimeSpan.FromSeconds(2)),
            "background loop never reached the blocking read");

        // Dispose must not block on the stuck read; it is bounded by the join timeout. Run it on a
        // separate thread guarded by a timeout so a regression (taking the tick gate before
        // signalling stop) fails the assert instead of hanging the whole suite.
        var disposeThread = new Thread(worker.Dispose) { IsBackground = true };
        disposeThread.Start();
        bool returned = disposeThread.Join(TimeSpan.FromSeconds(5));
        gate.Set(); // always release so the blocked thread can exit, even if Dispose regressed
        Assert.True(returned, "Dispose blocked on the stuck HID read instead of returning within the join timeout");

        // The join timed out (the thread is still mid-read holding the gate), so Dispose left the
        // controller alone rather than race a use-after-dispose against the worker - the loop thread
        // itself disposes it once the stuck read returns and the loop exits.
        Assert.True(disposeThread.Join(TimeSpan.FromSeconds(2)));
        Assert.True(
            SpinWait.SpinUntil(() => transport.IsDisposed, TimeSpan.FromSeconds(2)),
            "the loop thread did not dispose the controller after the stuck read returned");
        Assert.Contains(logger.Messages, m => m.Contains("C0 still inside a device call at shutdown"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("C1 still inside")); // the healthy one had stopped
        Assert.True(healthy.IsDisposed);
    }

    [Fact]
    public void Dispose_Twice_IsANoOp() {
        var transport = new FakeDeviceTransport();
        var worker = new KeepAliveWorker(new[] { NewController(0, transport, new FakeLogger()) }, new FakeLogger());

        worker.Dispose();
        worker.Dispose();

        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public void Constructor_RejectsMissingDependencies() {
        Assert.Throws<ArgumentNullException>(() => new KeepAliveWorker(null!, new FakeLogger()));
        Assert.Throws<ArgumentNullException>(() => new KeepAliveWorker(Array.Empty<IFanDevice>(), null!));
    }

    [Fact]
    public void Tick_IsolatesAndLogsAFailedApply() {
        var logger = new FakeLogger();
        var transport = new FakeDeviceTransport { FailFeatures = true };
        FanController controller = NewController(0, transport, logger);
        controller.SetTarget(0, 60);
        using var worker = new KeepAliveWorker(new[] { controller }, logger);

        worker.Tick();

        Assert.Contains(logger.Messages, m => m.Contains("apply err C0"));
    }

    [Fact]
    public void AControllerStuckInADeviceCall_DoesNotHoldUpTheOthers() {
        var logger = new FakeLogger();
        using var gate = new ManualResetEventSlim(false);
        var stuck = new FakeDeviceTransport { BlockReadsUntil = gate };
        var healthy = new FakeDeviceTransport();
        using var worker = new KeepAliveWorker(
            new[] { NewController(0, stuck, logger), NewController(1, healthy, logger) }, logger, tickIntervalMs: 20);
        worker.Start();

        try {
            // The first controller never gets past its first read; the second keeps ticking.
            Assert.True(SpinWait.SpinUntil(() => healthy.ReadCount >= 5, TimeSpan.FromSeconds(5)));
            Assert.Equal(1, stuck.ReadCount);
        } finally {
            gate.Set();
        }
    }

    // A controller whose close takes seconds, as a device that came back from sleep wedged does.
    private sealed class SlowToCloseDevice : IFanDevice {
        private readonly CountdownEvent _closing;

        public SlowToCloseDevice(CountdownEvent closing) => _closing = closing;

        public bool SawTheOther { get; private set; }

        private volatile bool _closed;

        public bool Closed => _closed;

        public int ChannelCount => 0;

        public bool IsChannelPopulated(int channel) => true;

        public ChannelDescriptor Describe(int channel) => throw new NotSupportedException();

        public void SetTarget(int channel, int duty) {
        }

        public void ReleaseChannel(int channel) {
        }

        public float GetRpm(int channel) => 0f;

        public void ApplyPending() {
        }

        public void PollRpm() {
        }

        public void ReplayOnReconnect(Action replay) {
        }

        // Each close waits until both are closing, which they can only do if they overlap.
        public void Dispose() {
            _closing.Signal();
            SawTheOther = _closing.Wait(TimeSpan.FromSeconds(10));
            _closed = true;
        }
    }

    [Fact]
    public void Dispose_ClosesEveryController_SideBySide() {
        using var closing = new CountdownEvent(2);
        var first = new SlowToCloseDevice(closing);
        var second = new SlowToCloseDevice(closing);
        var worker = new KeepAliveWorker(new IFanDevice[] { first, second }, new FakeLogger());
        worker.Start();

        worker.Dispose();

        // The two closes run on the loops' own threads, rather than one after the other.
        Assert.True(SpinWait.SpinUntil(() => first.Closed && second.Closed, TimeSpan.FromSeconds(15)));
        Assert.True(first.SawTheOther && second.SawTheOther);
    }
}
