using System;

namespace FanControl.LianLi.Devices;

/// <summary>
/// How long ago something happened by the injected clock, safe against the clock stepping back.
/// The system clock can jump backwards - a time sync after a wake, a user correcting it - and a
/// plain subtraction then comes out negative, so every "has the interval passed" check would wait
/// until the clock caught up again: keepalives, the wireless cycle and the save schedule would all
/// stall for however far it jumped. A moment that is now in the future is taken as long ago, so the
/// check fires and the caller records a fresh time from which it runs on normally.
/// </summary>
internal static class ClockSpan {
    /// <summary>The time from <paramref name="thenUtc"/> to <paramref name="nowUtc"/>; <see cref="TimeSpan.MaxValue"/> when the clock went back.</summary>
    public static TimeSpan Since(DateTime nowUtc, DateTime thenUtc)
        => nowUtc >= thenUtc ? nowUtc - thenUtc : TimeSpan.MaxValue;

    /// <summary>
    /// Whether <paramref name="thenUtc"/> is more than <paramref name="window"/> before
    /// <paramref name="nowUtc"/>. The opposite of <see cref="Since"/>'s choice: for letting something
    /// go as too old, a moment now in the future - the clock stepped back since, a dual-boot clock or a
    /// time sync at boot - counts as recent, since forgetting on a clock correction would throw away
    /// what was seen a moment ago.
    /// </summary>
    public static bool IsOlderThan(DateTime nowUtc, DateTime thenUtc, TimeSpan window)
        => nowUtc > thenUtc && nowUtc - thenUtc > window;
}
