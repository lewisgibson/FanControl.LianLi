using System;
using FanControl.LianLi.Protocol;
using Xunit;

namespace FanControl.LianLi.Tests.Protocol;

/// <summary>
/// Byte-level tests for the HydroShift II OLED Curve's pump MCU: the four eight-byte commands,
/// the status and tachometer decodes with L-Connect's per-band rpm correction, the 22-point
/// rpm-to-output table with its interpolation and rounding, and the duty-to-rpm span.
/// </summary>
public sealed class HydroShiftCurveProtocolTests {
    // HS2Controller.DataPoints, verbatim: the target rpm and the output value sent for it.
    private static readonly (int Rpm, int Output)[] Table =
    {
        (2735, 2300), (2703, 2200), (2651, 2100), (2592, 2000), (2537, 1900), (2477, 1800),
        (2420, 1700), (2365, 1600), (2305, 1500), (2248, 1400), (2188, 1300), (2130, 1200),
        (2073, 1100), (2013, 1000), (1950, 900), (1895, 800), (1838, 700), (1783, 600),
        (1725, 500), (1663, 400), (1608, 300), (1577, 250),
    };

    [Theory]
    [InlineData(0x0416, 0x8051, true)]
    [InlineData(0x0416, 0x8052, false)] // the MCU's bootloader
    [InlineData(0x0416, 0x8040, false)] // a dongle
    [InlineData(0x0CF2, 0x8051, false)] // right pid, wrong vendor
    public void IsPump_RecognisesOnlyThePumpMcu(int vendor, int product, bool expected)
        => Assert.Equal(expected, HydroShiftCurveProtocol.IsPump(vendor, product));

    [Fact]
    public void EncodeStatusRequest_IsCommand0x60Alone()
        => Assert.Equal(new byte[] { 0x60, 0, 0, 0, 0, 0, 0, 0 }, HydroShiftCurveProtocol.EncodeStatusRequest());

    [Fact]
    public void EncodePumpSpeedRequest_IsCommand0x62Alone()
        => Assert.Equal(new byte[] { 0x62, 0, 0, 0, 0, 0, 0, 0 }, HydroShiftCurveProtocol.EncodePumpSpeedRequest());

    [Theory]
    [InlineData(979, 0x03, 0xD3)]
    [InlineData(2300, 0x08, 0xFC)]
    [InlineData(250, 0x00, 0xFA)]
    [InlineData(0, 0x00, 0x00)]
    [InlineData(0xFFFF, 0xFF, 0xFF)]
    public void EncodeSetPump_CarriesTheOutputValueBigEndianAtBytes1And2(int outputValue, int high, int low)
        => Assert.Equal(
            new byte[] { 0x61, (byte)high, (byte)low, 0, 0, 0, 0, 0 },
            HydroShiftCurveProtocol.EncodeSetPump(outputValue));

    [Theory]
    [InlineData(-1)]
    [InlineData(0x10000)]
    public void EncodeSetPump_RejectsAValueThePairCannotCarry(int outputValue)
        => Assert.Throws<ArgumentOutOfRangeException>(() => HydroShiftCurveProtocol.EncodeSetPump(outputValue));

    [Fact]
    public void EncodeMotherboardSync_SendsZeroToFollowTheHeader_AndOneToTakeThePumpBack() {
        Assert.Equal(new byte[] { 0x64, 0, 0, 0, 0, 0, 0, 0 }, HydroShiftCurveProtocol.EncodeMotherboardSync(sync: true));
        Assert.Equal(new byte[] { 0x64, 1, 0, 0, 0, 0, 0, 0 }, HydroShiftCurveProtocol.EncodeMotherboardSync(sync: false));
    }

    [Fact]
    public void DecodeStatus_ReadsTheTemperatureAtByte1_AndFollowsMotherboardWhenByte2IsZero() {
        HydroShiftCurveStatus software = HydroShiftCurveProtocol.DecodeStatus(new byte[] { 0x60, 34, 1, 0, 0, 0, 0, 0 })!.Value;
        HydroShiftCurveStatus header = HydroShiftCurveProtocol.DecodeStatus(new byte[] { 0x60, 20, 0 })!.Value;

        Assert.Equal(34, software.LiquidTemperature);
        Assert.False(software.FollowsMotherboard);
        Assert.Equal(20, header.LiquidTemperature);
        Assert.True(header.FollowsMotherboard);
    }

    [Theory]
    [InlineData(0, 0)]        // the -40 is clamped at 0
    [InlineData(30, 0)]
    [InlineData(40, 0)]
    [InlineData(41, 1)]
    [InlineData(1800, 1760)]  // the low band's ceiling: -40
    [InlineData(1801, 1751)]  // the middle band: -50
    [InlineData(2500, 2450)]
    [InlineData(2501, 2471)]  // the high band: -30
    [InlineData(2735, 2705)]
    [InlineData(0xFFFF, 0xFFFF - 30)]
    public void DecodePumpSpeed_ReadsTheBigEndianCount_LessTheBandCorrection(int raw, int expected)
        => Assert.Equal(expected, HydroShiftCurveProtocol.DecodePumpSpeed(new byte[] { 0x62, (byte)(raw >> 8), (byte)(raw & 0xFF), 0, 0, 0, 0, 0 }));

    [Fact]
    public void Decoders_RejectAMissingOrShortReply() {
        Assert.Throws<ArgumentNullException>(() => HydroShiftCurveProtocol.DecodeStatus(null!));
        Assert.Throws<ArgumentNullException>(() => HydroShiftCurveProtocol.DecodePumpSpeed(null!));
        Assert.Throws<ArgumentException>(() => HydroShiftCurveProtocol.DecodeStatus(new byte[] { 0x60, 34 }));
        Assert.Throws<ArgumentException>(() => HydroShiftCurveProtocol.DecodePumpSpeed(new byte[] { 0x62, 0x07 }));
    }

    [Fact]
    public void OutputValueForRpm_MapsEveryTableRowToItsOutput() {
        foreach ((int rpm, int output) in Table) {
            Assert.Equal(output, HydroShiftCurveProtocol.OutputValueForRpm(rpm));
        }
    }

    [Theory]
    [InlineData(3000, 2300)] // at or above the fastest row: its output
    [InlineData(2800, 2300)] // the Turbo ceiling is beyond the table
    [InlineData(2735, 2300)]
    [InlineData(1577, 250)]  // at or below the slowest row: its output
    [InlineData(1500, 250)]
    [InlineData(1578, 252)]  // 251.6 rounds up
    [InlineData(1600, 287)]  // 287.1 rounds down
    [InlineData(1700, 460)]  // 459.7
    [InlineData(1800, 631)]  // 630.9
    [InlineData(2000, 979)]  // 979.4
    [InlineData(2043, 1050)] // exactly midway between 1100 and 1000
    [InlineData(2200, 1320)] // exact
    [InlineData(2400, 1664)] // 1663.6
    [InlineData(2600, 2014)] // 2013.6
    [InlineData(2723, 2262)] // 2262.5: halves go to even, as Math.Round does
    [InlineData(2731, 2288)] // 2287.5
    [InlineData(2734, 2297)] // 2296.9
    public void OutputValueForRpm_InterpolatesBetweenRows_AndRoundsHalvesToEven(int rpm, int expected)
        => Assert.Equal(expected, HydroShiftCurveProtocol.OutputValueForRpm(rpm));

    [Theory]
    [InlineData(-5, 1600)]
    [InlineData(0, 1600)]
    [InlineData(1, 1608)]
    [InlineData(25, 1800)]
    [InlineData(50, 2000)]
    [InlineData(100, 2400)]
    [InlineData(150, 2400)]
    public void PumpRpmFromDuty_SpansTheOrdinaryRangeLinearly(int duty, int expected)
        => Assert.Equal(expected, HydroShiftCurveProtocol.PumpRpmFromDuty(duty));

    // The MCU echoes the command in byte 0 of every reply WinUsbHS2 checks; a packet echoing
    // another command is a stale reply to it, not this one.
    [Fact]
    public void Decoders_RejectAReplyThatEchoesAnotherCommand() {
        Assert.Null(HydroShiftCurveProtocol.DecodeStatus(new byte[] { 0x62, 7, 0xD0, 0, 0, 0, 0, 0 }));
        Assert.Null(HydroShiftCurveProtocol.DecodeStatus(new byte[] { 0, 34, 1 }));
        Assert.Null(HydroShiftCurveProtocol.DecodePumpSpeed(new byte[] { 0x60, 34, 1, 0, 0, 0, 0, 0 }));
        Assert.Null(HydroShiftCurveProtocol.DecodePumpSpeed(new byte[] { 0, 7, 0xD0 }));
    }

    [Fact]
    public void Ranges_AreLConnects() {
        Assert.Equal(1600, HydroShiftCurveProtocol.PumpRpmMinimum);
        Assert.Equal(2400, HydroShiftCurveProtocol.PumpRpmMaximum);
        Assert.Equal(8, HydroShiftCurveProtocol.PacketLength);
        Assert.Equal(64, HydroShiftCurveProtocol.ReplyLength);
    }
}
