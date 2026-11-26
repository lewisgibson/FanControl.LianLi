namespace FanControl.LianLi.Protocol;

/// <summary>
/// The fan and pump RPM of a Galahad II AIO, and the coolant temperature when the reply carries
/// one, decoded from a handshake reply. Produced by <see cref="Galahad2Protocol.DecodeHandshake"/>.
/// </summary>
internal readonly struct Galahad2Reading {
    /// <summary>Create a reading with the given fan and pump RPM and coolant temperature.</summary>
    public Galahad2Reading(int fanRpm, int pumpRpm, float? coolantTemperature) {
        FanRpm = fanRpm;
        PumpRpm = pumpRpm;
        CoolantTemperature = coolantTemperature;
    }

    /// <summary>The AIO fan's measured RPM.</summary>
    public int FanRpm { get; }

    /// <summary>The pump's measured RPM.</summary>
    public int PumpRpm { get; }

    /// <summary>
    /// The coolant temperature in degrees Celsius, or null when the reply carries none: too short a
    /// payload, or the cooler's valid flag clear.
    /// </summary>
    public float? CoolantTemperature { get; }
}
