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
}
