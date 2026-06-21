namespace FanControl.LianLi.Protocol;

/// <summary>
/// Pure, side-effect-free encoder/decoder for one controller family. Every
/// method maps directly to a USB report byte sequence; no I/O happens here,
/// which makes the protocol exhaustively unit-testable.
/// </summary>
internal interface IFanProtocol {
    /// <summary>The controller family this encoder implements.</summary>
    DeviceFamily Family { get; }

    /// <summary>Number of fan channels (always 4 across the Uni family).</summary>
    int ChannelCount { get; }

    /// <summary>Byte offset of the RPM data within the input report (1 or 2).</summary>
    int RpmReportOffset { get; }

    /// <summary>Encode a set-speed report for a channel at the given duty percent (0-100).</summary>
    byte[] EncodeSetSpeed(int channel, int dutyPercent);

    /// <summary>Encode the disable-PWM (manual mode) report for a channel.</summary>
    byte[] EncodeManualMode(int channel);

    /// <summary>Encode the optional ARGB-sync report.</summary>
    byte[] EncodeArgbSync(bool on);

    /// <summary>Decode the big-endian RPM for a channel from an input report.</summary>
    float DecodeRpm(byte[] inputReport, int channel);

    /// <summary>
    /// Encode the feature report that primes the device to return a valid RPM input report,
    /// or <see langword="null"/> if the device does not require a primer before each read.
    /// </summary>
    byte[]? EncodeRpmRequest();
}
