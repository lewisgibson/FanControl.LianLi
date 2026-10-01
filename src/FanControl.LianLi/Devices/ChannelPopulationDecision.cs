using System;
using System.Collections.Generic;

namespace FanControl.LianLi.Devices;

/// <summary>
/// The pure population-detection core. A Uni controller never reports which channels have a fan
/// attached (the input report carries only RPM, never a presence bit), so the plugin infers it from
/// a burst of RPM probes at startup: a channel with a spinning fan returns a plausible non-zero RPM
/// on (almost) every probe, while an empty channel reads 0 or occasional out-of-range garbage.
/// A channel is populated when a strict majority of its probes were plausible and non-zero - a
/// single stray in-range garbage read cannot promote an empty channel, and a stale idle-buffer read
/// or two at startup cannot demote a real one. If NO channel looks populated (fans not spun up, or
/// every probe was garbage), the result is inconclusive and every channel is shown, so a controller
/// is never hidden as a whole.
/// </summary>
internal static class ChannelPopulationDecision {
    /// <summary>
    /// Resolve which channels are populated from the per-channel count of plausible non-zero probe
    /// reads. A channel is populated when its count is a strict majority of <paramref name="totalReads"/>.
    /// An all-unpopulated result is treated as inconclusive and every channel is marked populated, so
    /// a transient all-idle probe never hides the whole controller.
    /// </summary>
    /// <param name="plausibleReadCounts">Per-channel count of probes that returned a plausible, non-zero RPM.</param>
    /// <param name="totalReads">How many probe reads were taken per channel.</param>
    public static bool[] Resolve(IReadOnlyList<int> plausibleReadCounts, int totalReads) {
        if (plausibleReadCounts is null) {
            throw new ArgumentNullException(nameof(plausibleReadCounts));
        }

        var populated = new bool[plausibleReadCounts.Count];
        bool any = false;
        for (int channel = 0; channel < populated.Length; channel++) {
            // Strict majority: count > totalReads / 2, written as count*2 > totalReads to stay integral.
            populated[channel] = totalReads > 0 && (plausibleReadCounts[channel] * 2) > totalReads;
            any |= populated[channel];
        }

        if (!any) {
            for (int channel = 0; channel < populated.Length; channel++) {
                populated[channel] = true;
            }
        }

        return populated;
    }
}
