using FanControl.LianLi.Transport;

namespace FanControl.LianLi.Tests.Fakes;

// A call clock a test sets by hand, so an abandoned call's age in a log line is exact. Given a step,
// every reading moves it on by that much first, for a test that drives a device by repeated ticks and
// wants the reopen schedule to come round as it would in real time.
internal sealed class FakeDeviceCallClock : IDeviceCallClock {
    private long _now;

    public FakeDeviceCallClock(long stepMilliseconds = 0) {
        StepMilliseconds = stepMilliseconds;
    }

    public long StepMilliseconds { get; }

    public long NowMilliseconds {
        get => _now += StepMilliseconds;
        set => _now = value;
    }
}
