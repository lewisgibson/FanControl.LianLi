using System;

namespace FanControl.LianLi.Transport;

/// <summary>
/// The pure schedule behind every "try again, but not every time" loop in the plugin: the first
/// attempt is immediate, so a transient fault costs no dead time, and each attempt that does not
/// resolve it pushes the next one further out, doubling up to a ceiling.
///
/// It is timed in milliseconds on a monotonic clock. It used to count the attempts offered, but the
/// callers offer at very different rates - a wired hub about one a second, a wireless pair several -
/// and a ceiling of 640 offers meant a hub that had been gone a few minutes was tried again only
/// every five to eleven minutes, so one plugged back in could go that long uncontrolled. The
/// callers: each transport reopening a faulted handle, offered a turn per transfer, and a stand-in
/// rebuilding a controller a scan could not reach, offered a turn per worker call. Pure apart from
/// the injected clock, so the schedule is unit-tested in isolation.
/// </summary>
internal sealed class DoublingBackoff {
    /// <summary>The wait before the second try at a device that is gone: about what it used to be.</summary>
    public const int DeviceFirstGapMilliseconds = 5000;

    /// <summary>The longest wait between tries at a device that is gone, so one plugged back in is driven again within it.</summary>
    public const int DeviceMaximumGapMilliseconds = 30000;

    private readonly int _initialGapMilliseconds;
    private readonly int _maximumGapMilliseconds;
    private readonly IDeviceCallClock _clock;

    private int _gapMilliseconds;
    private long? _nextAttemptAt;

    /// <summary>
    /// Create a schedule that waits <paramref name="initialGapMilliseconds"/> before its second
    /// attempt and doubles that wait after each attempt, up to <paramref name="maximumGapMilliseconds"/>.
    /// </summary>
    public DoublingBackoff(int initialGapMilliseconds, int maximumGapMilliseconds, IDeviceCallClock clock) {
        if (initialGapMilliseconds <= 0) {
            throw new ArgumentOutOfRangeException(nameof(initialGapMilliseconds), "The first gap is at least one millisecond.");
        }

        if (maximumGapMilliseconds < initialGapMilliseconds) {
            throw new ArgumentOutOfRangeException(nameof(maximumGapMilliseconds), "The ceiling cannot be below the first gap.");
        }

        _initialGapMilliseconds = initialGapMilliseconds;
        _maximumGapMilliseconds = maximumGapMilliseconds;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _gapMilliseconds = initialGapMilliseconds;
    }

    /// <summary>
    /// Take one offer and report whether this is the one that attempts. The first offer after
    /// construction or <see cref="Reset"/> always attempts; after that, the first offer once the
    /// current gap has passed.
    /// </summary>
    public bool ShouldAttempt() {
        long now = _clock.NowMilliseconds;
        if (_nextAttemptAt is long due && now < due) {
            return false;
        }

        _nextAttemptAt = now + _gapMilliseconds;
        _gapMilliseconds = (int)Math.Min((long)_gapMilliseconds * 2, _maximumGapMilliseconds);
        return true;
    }

    /// <summary>Resolved: the next offer attempts immediately again.</summary>
    public void Reset() {
        _gapMilliseconds = _initialGapMilliseconds;
        _nextAttemptAt = null;
    }
}
