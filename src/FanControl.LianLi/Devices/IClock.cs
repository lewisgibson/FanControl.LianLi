using System;

namespace FanControl.LianLi.Devices;

/// <summary>
/// Abstracts the system clock so the keepalive staleness math can be tested
/// deterministically with a fake clock.
/// </summary>
internal interface IClock {
    /// <summary>The current UTC time.</summary>
    DateTime UtcNow { get; }
}
