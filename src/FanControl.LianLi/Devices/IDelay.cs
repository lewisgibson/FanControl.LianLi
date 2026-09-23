using System;

namespace FanControl.LianLi.Devices;

/// <summary>
/// Abstracts waiting, the companion of <see cref="IClock"/>: a device whose protocol needs a pause
/// between two transfers (the wireless effect stream's 20 ms gaps, the bounded wait for a
/// wireless master to answer) waits through this, so a test advances a fake clock instead of
/// sleeping and can assert exactly which pauses were taken.
/// </summary>
internal interface IDelay {
    /// <summary>Block the calling thread for <paramref name="duration"/>.</summary>
    void Wait(TimeSpan duration);
}
