using System;

namespace FanControl.LianLi.Devices;

/// <summary>
/// The pure keepalive core. A channel is written when its target changed since
/// the last write OR when the last write is stale (older than the refresh
/// interval). Re-asserting on staleness is what stops the controller silently
/// reverting a channel to maximum speed.
/// </summary>
internal static class ChannelWriteDecision {
    /// <summary>
    /// How often a channel is re-asserted even when its target has not changed.
    /// Without this the controller reverts to PWM/max after a short time.
    /// </summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Decide whether a channel should be written now.
    /// </summary>
    /// <param name="target">Commanded duty percent; negative means unassigned.</param>
    /// <param name="lastWritten">The duty last actually written.</param>
    /// <param name="lastWriteUtc">When the last write happened.</param>
    /// <param name="now">The current time.</param>
    /// <param name="refreshInterval">The staleness threshold.</param>
    public static bool ShouldWrite(
        int target,
        int lastWritten,
        DateTime lastWriteUtc,
        DateTime now,
        TimeSpan refreshInterval) {
        if (target < 0) {
            return false; // nothing assigned to this channel yet
        }

        bool changed = target != lastWritten;
        bool stale = ClockSpan.Since(now, lastWriteUtc) >= refreshInterval;
        return changed || stale;
    }
}
