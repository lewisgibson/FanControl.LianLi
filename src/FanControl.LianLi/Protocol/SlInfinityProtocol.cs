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
    public override byte[]? EncodeRpmPrimer() {
        // The SL-Infinity does not stream its input report: HidD_GetInputReport returns a stale
        // idle buffer (which decodes to ~57000 rpm) until this feature report asks it to report
        // live RPM. The buffer MUST be 65 bytes - HidD_SetFeature rejects a shorter buffer with
        // ERROR_INVALID_PARAMETER, because that is the feature report size in the HID descriptor.
        // The [0xE0, 0x50, 0x00] command is the same one SignalRGB's SL-Infinity plugin sends
        // before each read.
        var primer = new byte[65];
        primer[0] = 0xE0;
        primer[1] = 0x50;
        return primer;
    }
}
