using System;

namespace FanControl.LianLi.Devices;

/// <summary>
/// Abstracts the system clock (and the one blocking wait the setup path needs) so the
/// keepalive staleness math and the startup channel-detection retry can both be tested
/// deterministically with a fake clock, without waiting in real time.
/// </summary>
internal interface IClock {
    /// <summary>The current UTC time.</summary>
    DateTime UtcNow { get; }

    /// <summary>
    /// Block the calling thread for <paramref name="duration"/>. Used only on the one-time
    /// setup path (channel detection re-read after wake), never on the recurring tick, so a
    /// bounded wait here cannot stall the host's <c>Update()</c>. Injected so a test can pass
    /// the time forward instead of sleeping for real.
    /// </summary>
    void Sleep(TimeSpan duration);
}
