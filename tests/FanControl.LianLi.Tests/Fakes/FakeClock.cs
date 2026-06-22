using System;
using FanControl.LianLi.Devices;

namespace FanControl.LianLi.Tests.Fakes;

internal sealed class FakeClock : IClock {
    public FakeClock()
        : this(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)) {
    }

    public FakeClock(DateTime start) => UtcNow = start;

    public DateTime UtcNow { get; private set; }

    /// <summary>Total time the code under test asked to <see cref="Sleep"/>, for asserting retry waits.</summary>
    public TimeSpan TotalSlept { get; private set; }

    public void Advance(TimeSpan delta) => UtcNow += delta;

    // A test never waits in real time: a requested sleep just moves the virtual clock forward.
    public void Sleep(TimeSpan duration) {
        TotalSlept += duration;
        UtcNow += duration;
    }
}
