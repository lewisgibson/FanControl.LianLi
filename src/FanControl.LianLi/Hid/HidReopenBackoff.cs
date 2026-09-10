using System;

namespace FanControl.LianLi.Hid;

/// <summary>
/// Schedules the reopen attempts on a faulted HID handle. A handle faults when the device it was
/// opened on goes away underneath it - the USB re-enumeration across sleep/wake or hibernate -
/// and only a fresh open on the same device path recovers it. The transport has
/// no clock (the injected clock lives above the Hid layer), so the schedule is counted in faulted
/// transfers: the worker drives two transfers per controller per one-second tick while a handle is
/// faulted, so a gap of N transfers is roughly N/2 seconds. The first attempt is immediate, so a
/// transient fault (one transfer lost to a device mid-re-enumeration) costs no dead time; each later
/// gap doubles up to <see cref="MaximumGap"/>, so a device that is genuinely gone (unplugged) is
/// probed every few minutes rather than every tick. Pure and clock-free so the schedule is
/// unit-tested in isolation.
/// </summary>
internal sealed class HidReopenBackoff {
    /// <summary>Faulted transfers skipped before the second attempt; the gap doubles after each failed attempt.</summary>
    public const int InitialGap = 10;

    /// <summary>The largest gap between attempts - about five minutes at the worker's cadence.</summary>
    public const int MaximumGap = 640;

    private int _gap = InitialGap;
    private int _skipsBeforeNextAttempt;

    /// <summary>
    /// Record one faulted transfer and report whether this is the one that attempts a reopen. The
    /// first call after a fault (or after <see cref="Reset"/>) always attempts.
    /// </summary>
    public bool ShouldAttempt() {
        if (_skipsBeforeNextAttempt > 0) {
            _skipsBeforeNextAttempt--;
            return false;
        }

        _skipsBeforeNextAttempt = _gap;
        _gap = Math.Min(_gap * 2, MaximumGap);
        return true;
    }

    /// <summary>A reopen succeeded: the next fault is retried immediately again.</summary>
    public void Reset() {
        _gap = InitialGap;
        _skipsBeforeNextAttempt = 0;
    }
}
