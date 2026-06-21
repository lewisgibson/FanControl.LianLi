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
        return new byte[] { ReportId, (byte)(SpeedChannelBase + channel), 0, DutyByte(duty) };
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
    public virtual byte[]? EncodeRpmRequest() => null;

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
