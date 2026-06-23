using System;
using FanControl.LianLi.Devices;

namespace FanControl.LianLi.Tests.Fakes;

internal sealed class FakeClock : IClock {
    public FakeClock()
        : this(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)) {
    }

    public FakeClock(DateTime start) => UtcNow = start;

    public DateTime UtcNow { get; private set; }

    public void Advance(TimeSpan delta) => UtcNow += delta;
}
