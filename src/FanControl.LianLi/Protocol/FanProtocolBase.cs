using System;

namespace FanControl.LianLi.Protocol;

/// <summary>
/// Shares the report framing common to every Uni family. Concrete families
/// supply only the duty-to-byte curve and the family-specific register bytes.
/// The byte math is the controllers' fixed wire protocol - a hardware fact, not
/// a stylistic choice; do not "simplify" the arithmetic.
/// </summary>
internal abstract class FanProtocolBase : IFanProtocol {
    /// <summary>Report id every Uni report begins with (0xE0).</summary>
    protected const byte ReportId = 224;

    /// <summary>Sub-command byte for the manual-mode and ARGB-sync reports (0x10).</summary>
    protected const byte ConfigCommand = 16;

    /// <summary>Base for the set-speed channel byte: byte 1 is 32 + channel.</summary>
    protected const byte SpeedChannelBase = 32;

    /// <summary>
    /// "Prepare an input report" command (0x50). L-Connect sends the feature report
    /// [ReportId, 0x50, 0x00] before every RPM read for the whole Uni family; the 0x00 sub-arg
    /// selects the RPM report (0x01 would select firmware version).
    /// </summary>
    private const byte PrepareInputReportCommand = 0x50;

    /// <summary>
    /// Length of the RPM-primer feature report. It must equal the device's feature report byte
    /// length: HidD_SetFeature rejects a 3-byte buffer with ERROR_INVALID_PARAMETER and accepts 7
    /// (verified on hardware), which is the feature report size in the SL-Infinity HID descriptor.
    /// </summary>
    private const int PrimerLength = 7;

    /// <inheritdoc />
    public abstract DeviceFamily Family { get; }

    /// <inheritdoc />
    public int ChannelCount => 4;

    /// <inheritdoc />
    public abstract int RpmReportOffset { get; }

    /// <summary>Family-specific disable-PWM register byte (third byte of the manual-mode report).</summary>
    protected abstract byte ManualModeRegister { get; }

    /// <summary>Family-specific ARGB-sync register byte.</summary>
    protected abstract byte ArgbRegister { get; }

    /// <summary>Map a clamped duty (0-100) to the family's speed byte.</summary>
    protected abstract byte DutyByte(int dutyPercent);

    /// <inheritdoc />
    public byte[] EncodeSetSpeed(int channel, int dutyPercent) {
        ValidateChannel(channel);
        int duty = Clamp(dutyPercent, 0, 100);

        // 0% emits speed byte 0 - a full-stop request, not the curve's non-zero floor.
        // This mirrors L-Connect, whose fan curve computes Math.Max(speed, 0) and sends
        // SetFanSpeed(0) at the bottom; the formula's lowest running step (d=1: 42 SL/AL,
        // 13 SLv2/ALv2, 10 SLI) keeps a *running* fan above its reliable-spin minimum, and
        // d=0 short-circuits past it to the stop request. Controllers that
        // support zero-rpm honor byte 0 and stop; the SL-Infinity firmware clamps a sub-floor
        // byte up to its ~210-rpm minimum (verified on hardware) and cannot truly stop under
        // host duty control - the same limitation L-Connect has on that family. A user who
        // wants a minimum spin instead of a stop sets a non-zero floor in their FanControl curve.
        byte speedByte = duty == 0 ? (byte)0 : DutyByte(duty);
        return new byte[] { ReportId, (byte)(SpeedChannelBase + channel), 0, speedByte };
    }

    /// <inheritdoc />
    public byte[] EncodeManualMode(int channel) {
        ValidateChannel(channel);

        // One bit per channel: ch0=0x10, ch1=0x20, ch2=0x40, ch3=0x80.
        // The upstream C# port used (2*ch)*16 which gives 0x60 for ch3 (wrong);
        // the Rust origin and liquidctl use 0x10 << ch.
        byte channelByte = (byte)(0x10 << channel);
        return new byte[] { ReportId, ConfigCommand, ManualModeRegister, channelByte };
    }

    /// <inheritdoc />
    public byte[] EncodeArgbSync(bool on) {
        return new byte[] { ReportId, ConfigCommand, ArgbRegister, (byte)(on ? 1 : 0), 0, 0, 0 };
    }

    /// <inheritdoc />
    public byte[] EncodeRpmPrimer() {
        // The Uni controllers do not stream their input report: HidD_GetInputReport returns a stale
        // idle buffer until this feature report asks the device to refresh it. L-Connect sends the
        // identical [0xE0, 0x50, 0x00] feature report before every RPM read for every Uni family
        // (SL, AL, SLv2, ALv2, SL-Infinity), so the whole family is primed here. Some revisions
        // return live RPM without it (verified harmless on SL-Infinity v1.4 here); others return
        // 0/garbage until primed - priming matches the vendor and is safe either way. The trailing
        // zero bytes pad to the device's feature report length (see PrimerLength).
        var primer = new byte[PrimerLength];
        primer[0] = ReportId;
        primer[1] = PrepareInputReportCommand;
        return primer;
    }

    /// <inheritdoc />
    public float DecodeRpm(byte[] inputReport, int channel) {
        ValidateChannel(channel);
        int offset = RpmReportOffset + (channel * 2);
        return (float)((inputReport[offset] << 8) | inputReport[offset + 1]); // big-endian
    }

    private void ValidateChannel(int channel) {
        if (channel < 0 || channel >= ChannelCount) {
            throw new ArgumentOutOfRangeException(
                nameof(channel), channel, "Channel must be in the range 0-3.");
        }
    }

    // Math.Clamp is unavailable on netstandard2.0, so the clamp is hand-rolled.
    private static int Clamp(int value, int min, int max) {
        if (value < min) {
            return min;
        }

        return value > max ? max : value;
    }
}
