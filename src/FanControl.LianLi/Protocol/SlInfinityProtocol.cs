namespace FanControl.LianLi.Protocol;

/// <summary>Uni SL-Infinity (0xA102). Duty curve 200-2100 rpm.</summary>
internal sealed class SlInfinityProtocol : FanProtocolBase {
    /// <inheritdoc />
    public override DeviceFamily Family => DeviceFamily.SlInfinity;

    /// <inheritdoc />
    public override int RpmReportOffset => 1;

    /// <inheritdoc />
    protected override byte ManualModeRegister => 98;

    /// <inheritdoc />
    protected override byte ArgbRegister => 97;

    /// <inheritdoc />
    protected override byte DutyByte(int dutyPercent) => (byte)((200 + (19 * dutyPercent)) / 21);

    /// <inheritdoc />
    /// <remarks>
    /// The SL Infinity idles in a buffered state after hibernate: the device returns stale
    /// channel data unless a [0xE0, 0x50, 0x00] feature report is sent before every read.
    /// The buffer must be exactly 65 bytes to match the HID feature report descriptor.
    /// </remarks>
    public override byte[]? EncodeRpmRequest() {
        byte[] buffer = new byte[65];
        buffer[0] = 0xE0;
        buffer[1] = 0x50;
        buffer[2] = 0x00;
        return buffer;
    }
}
