using System;
using System.Collections.Generic;
using FanControl.LianLi.Devices;

namespace FanControl.LianLi.Tests.Fakes;

/// <summary>
/// Records every pause instead of sleeping, and moves a <see cref="FakeClock"/> on by it when given
/// one, so time-driven code sees the time it waited pass.
/// </summary>
internal sealed class FakeDelay : IDelay {
    private readonly FakeClock? _clock;

    public FakeDelay(FakeClock? clock = null) => _clock = clock;

    public List<TimeSpan> Waits { get; } = new List<TimeSpan>();

    /// <summary>Run after each wait, e.g. to change what a fake device reports while the code under test pauses.</summary>
    public Action? OnWait { get; set; }

    public void Wait(TimeSpan duration) {
        Waits.Add(duration);
        _clock?.Advance(duration);
        OnWait?.Invoke();
    }
}
