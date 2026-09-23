using FanControl.LianLi.Transport;

namespace FanControl.LianLi.Tests.Fakes;

// A call clock a test sets by hand, so an abandoned call's age in a log line is exact.
internal sealed class FakeDeviceCallClock : IDeviceCallClock {
    public long NowMilliseconds { get; set; }
}
