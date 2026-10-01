namespace FanControl.LianLi.Devices;

/// <summary>
/// The pure read-validation core. A decoded RPM is trusted only when it is physically plausible.
/// After a hibernate/power cycle the SL-Infinity returns its idle-state input buffer (and a read
/// that races USB re-enumeration can return a partial buffer); both decode to nonsense values far
/// above anything a real fan reaches - the documented idle case is ~50000 rpm. Rejecting an
/// implausible value, and keeping the last good reading, stops the host ever seeing the garbage.
/// This follows L-Connect: it validates the report before trusting it, rather
/// than decoding whatever bytes arrive.
/// </summary>
internal static class ChannelReadDecision {
    /// <summary>
    /// Upper bound on a believable fan tachometer reading. The Uni fans top out around 2100 rpm
    /// (SL-Infinity) and no family exceeds ~3000; 6000 sits well above any real reading yet far
    /// below the idle buffer's ~50000, so it separates a live tachometer from garbage without ever
    /// clipping a real fan.
    /// </summary>
    public const float MaxPlausibleRpm = 6000f;

    /// <summary>
    /// Decide whether a decoded RPM is a believable reading (<c>0</c>..<see cref="MaxPlausibleRpm"/>)
    /// and should overwrite the cached value, not idle/partial-buffer garbage to be ignored.
    /// </summary>
    public static bool IsPlausible(float rpm) => rpm >= 0f && rpm <= MaxPlausibleRpm;
}
