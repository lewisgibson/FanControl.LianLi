using System;
using FanControl.LianLi.Protocol;

namespace FanControl.LianLi.Devices;

/// <summary>
/// The pure core of L-Connect's once-a-second speed resend (<c>MasterDevice.NeedSyncPwm</c>): a
/// device is sent its target PWMs again whenever any slot it reports differs from its target by
/// more than 5, so a speed packet lost on the radio is corrected on the next second rather than
/// waited out. A device with no fans is never sent one, except the Lancool 217 and the V150, which
/// L-Connect drives whatever fan count they report; and a V150 is resent every second while any
/// target is at or below 10. The plugin adds one condition L-Connect does not need: only the slots a
/// FanControl control is actually driving are compared, because a slot nobody asked for is never
/// the reason to send.
/// </summary>
internal static class WirelessSpeedSyncDecision {
    // NeedSyncPwm: "Math.Abs(fans_pwm - target_fans_pwm) > 5".
    private const int Tolerance = 5;

    // NeedSyncPwm's V150 clause: a target at or below 10 is always resent.
    private const int V150AlwaysResendAtOrBelow = 10;

    /// <summary>
    /// Whether a device reporting <paramref name="reported"/> needs its <paramref name="target"/>
    /// sent again. <paramref name="driven"/> marks the slots a control drives.
    /// </summary>
    public static bool NeedsSync(WirelessDeviceKind kind, int fanCount, byte[] reported, byte[] target, bool[] driven) {
        if (reported is null) {
            throw new ArgumentNullException(nameof(reported));
        }

        if (target is null) {
            throw new ArgumentNullException(nameof(target));
        }

        if (driven is null) {
            throw new ArgumentNullException(nameof(driven));
        }

        if (fanCount == 0 && kind != WirelessDeviceKind.CaseFans && kind != WirelessDeviceKind.V150) {
            return false;
        }

        for (int slot = 0; slot < WirelessProtocol.SlotsPerGroup; slot++) {
            if (!driven[slot]) {
                continue;
            }

            if (Math.Abs(reported[slot] - target[slot]) > Tolerance
                || (kind == WirelessDeviceKind.V150 && target[slot] <= V150AlwaysResendAtOrBelow)) {
                return true;
            }
        }

        return false;
    }
}
