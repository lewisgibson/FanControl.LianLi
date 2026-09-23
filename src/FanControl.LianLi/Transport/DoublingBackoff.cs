using System;

namespace FanControl.LianLi.Transport;

/// <summary>
/// The pure schedule behind every "try again, but not every time" loop in the plugin: the first
/// attempt is immediate, so a transient fault costs no dead time, and each attempt that does not
/// settle the matter pushes the next one further out, doubling up to a ceiling.
///
/// It counts <em>attempts offered</em> rather than seconds, because the transports sit below the
/// injected clock, and because the thing being waited on is a device, whose readiness is not a
/// function of wall time. The callers: each transport reopening a faulted handle, offered a turn per
/// transfer, and a stand-in rebuilding a controller a scan could not reach, offered a turn per worker
/// call. Pure, so the schedule is unit-tested in isolation.
/// </summary>
internal sealed class DoublingBackoff {
    private readonly int _initialGap;
    private readonly int _maximumGap;

    private int _gap;
    private int _skipsBeforeNextAttempt;

    /// <summary>
    /// Create a schedule that skips <paramref name="initialGap"/> offers before its second attempt
    /// and doubles that gap after each attempt, up to <paramref name="maximumGap"/>.
    /// </summary>
    public DoublingBackoff(int initialGap, int maximumGap) {
        if (initialGap <= 0) {
            throw new ArgumentOutOfRangeException(nameof(initialGap), "The first gap is at least one offer.");
        }

        if (maximumGap < initialGap) {
            throw new ArgumentOutOfRangeException(nameof(maximumGap), "The ceiling cannot be below the first gap.");
        }

        _initialGap = initialGap;
        _maximumGap = maximumGap;
        _gap = initialGap;
    }

    /// <summary>
    /// Take one offer and report whether this is the one that attempts. The first offer after
    /// construction or <see cref="Reset"/> always attempts.
    /// </summary>
    public bool ShouldAttempt() {
        if (_skipsBeforeNextAttempt > 0) {
            _skipsBeforeNextAttempt--;
            return false;
        }

        _skipsBeforeNextAttempt = _gap;
        _gap = Math.Min(_gap * 2, _maximumGap);
        return true;
    }

    /// <summary>The matter settled: the next offer attempts immediately again.</summary>
    public void Reset() {
        _gap = _initialGap;
        _skipsBeforeNextAttempt = 0;
    }
}
