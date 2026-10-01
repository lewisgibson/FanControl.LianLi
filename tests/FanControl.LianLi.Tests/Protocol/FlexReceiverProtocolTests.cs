using System;
using System.Linq;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Tests.Fakes;
using Xunit;

namespace FanControl.LianLi.Tests.Protocol;

/// <summary>
/// Byte-level tests for the FLEX and P28 V2 USB receivers: the status request and its record
/// decode (the wireless record one byte in, flag nibbles masked, the command echo required), each
/// family's duty rule and rounding, the speed packet with its motherboard-sync scrub, and the
/// reply check.
/// </summary>
public sealed class FlexReceiverProtocolTests {
    private static readonly byte[] Mac = { 0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6 };
    private static readonly byte[] Master = { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };

    /// <summary>A 64-byte status reply: the command echo, then the record as the wireless list carries it.</summary>
    internal static byte[] StatusReply(FakeWirelessRecord record, byte echo = 0x12) {
        var reply = new byte[64];
        reply[0] = echo;
        Array.Copy(record.ToBytes(), 0, reply, 1, 42);
        return reply;
    }

    private static byte[] Packet(byte command, params int[] payload) {
        var packet = new byte[64];
        packet[0] = command;
        for (int i = 0; i < payload.Length; i++) {
            packet[1 + i] = (byte)payload[i];
        }

        return packet;
    }

    [Theory]
    [InlineData(0x43A8, 0x0101, true)]  // TL FLEX
    [InlineData(0x43A8, 0x0102, true)]  // TL FLEX LCD receiver
    [InlineData(0x43A8, 0x0104, true)]  // SL-INF FLEX LCD receiver
    [InlineData(0x43A8, 0x0105, true)]  // P28 V2
    [InlineData(0x43A8, 0x0103, false)] // SL-INF FLEX: no wired fan control in L-Connect
    [InlineData(0x43A8, 0x0106, false)] // SL FLEX: none either
    [InlineData(0x43A8, 0x0107, false)] // CL FLEX: none either
    [InlineData(0x43A8, 0x0108, false)] // unnamed, never used
    [InlineData(0x0416, 0x0101, false)] // right pid, wrong vendor
    [InlineData(0x1CBE, 0xA018, false)] // the TL FLEX LCD screen, a separate device
    public void IsReceiver_RecognisesOnlyTheReceiversLConnectDrivesOverUsb(int vendor, int product, bool expected)
        => Assert.Equal(expected, FlexReceiverProtocol.IsReceiver(vendor, product));

    // The family is passed by name because the enum is internal.
    [Theory]
    [InlineData(0x0101, "TlFlex")]
    [InlineData(0x0102, "TlFlexLcd")]
    [InlineData(0x0104, "SlInfinityFlexLcd")]
    [InlineData(0x0105, "P28V2")]
    public void FamilyOf_MapsEachProductId(int product, string expected)
        => Assert.Equal(expected, FlexReceiverProtocol.FamilyOf(product).ToString());

    [Theory]
    [InlineData(0x0103)]
    [InlineData(0x0106)]
    [InlineData(0x7372)]
    public void FamilyOf_RejectsAnyOtherProductId(int product)
        => Assert.Throws<ArgumentException>(() => FlexReceiverProtocol.FamilyOf(product));

    [Theory]
    [InlineData("TlFlex", "UNI FAN TL FLEX", 11, 5)]
    [InlineData("TlFlexLcd", "UNI FAN TL FLEX LCD", 11, 5)]
    [InlineData("SlInfinityFlexLcd", "UNI FAN SL-INF FLEX LCD", 10, 5)]
    [InlineData("P28V2", "UNI FAN P28 V2", 8, 1)]
    public void EachFamily_HasItsNameFloorAndIdleDuty(string familyName, string name, int floor, int idle) {
        var family = Enum.Parse<FlexReceiverFamily>(familyName);

        Assert.Equal(name, FlexReceiverProtocol.ProductName(family));
        Assert.Equal(floor, FlexReceiverProtocol.DutyFloor(family));
        Assert.Equal(idle, FlexReceiverProtocol.IdleDuty(family));
    }

    // The service's rule per family, then Math.Round(q / 100.0 * 255.0) with halves to even, as
    // checked in IEEE double: 10% is 26, 30% is 76, 50% is 128, 70% is 178, 90% is 230.
    [Theory]
    [InlineData("TlFlex", 0, 13)]    // 0 is sent as 5%
    [InlineData("TlFlex", -5, 13)]
    [InlineData("TlFlex", 1, 28)]    // floored at 11%
    [InlineData("TlFlex", 11, 28)]
    [InlineData("TlFlex", 12, 31)]
    [InlineData("TlFlex", 20, 51)]
    [InlineData("TlFlex", 30, 76)]
    [InlineData("TlFlex", 50, 128)]
    [InlineData("TlFlex", 70, 178)]
    [InlineData("TlFlex", 90, 230)]
    [InlineData("TlFlex", 100, 255)]
    [InlineData("TlFlex", 150, 255)] // capped, as the app caps before the service
    [InlineData("TlFlexLcd", 0, 13)]
    [InlineData("TlFlexLcd", 5, 28)]
    [InlineData("SlInfinityFlexLcd", 0, 13)]
    [InlineData("SlInfinityFlexLcd", 1, 26)]   // floored at 10%
    [InlineData("SlInfinityFlexLcd", 10, 26)]
    [InlineData("SlInfinityFlexLcd", 11, 28)]
    [InlineData("P28V2", 0, 3)]      // 0 is sent as 1%
    [InlineData("P28V2", 1, 20)]     // floored at 8%
    [InlineData("P28V2", 8, 20)]
    [InlineData("P28V2", 9, 23)]
    [InlineData("P28V2", 100, 255)]
    public void FanPwm_AppliesEachFamilysRule_ThenRoundsHalvesToEven(string familyName, int duty, int expected) {
        var family = Enum.Parse<FlexReceiverFamily>(familyName);
        Assert.Equal((byte)expected, FlexReceiverProtocol.FanPwm(family, duty));
    }

    [Fact]
    public void FanPwm_NeverProducesTheMotherboardSyncValue() {
        foreach (FlexReceiverFamily family in Enum.GetValues<FlexReceiverFamily>()) {
            for (int duty = -1; duty <= 101; duty++) {
                Assert.NotEqual(FlexReceiverProtocol.MotherboardSyncPwm, FlexReceiverProtocol.FanPwm(family, duty));
            }
        }

        Assert.Equal((byte)6, FlexReceiverProtocol.MotherboardSyncPwm);
    }

    [Fact]
    public void EncodeStatusRequest_IsCommand0x12AloneIn64Bytes()
        => Assert.Equal(Packet(0x12), FlexReceiverProtocol.EncodeStatusRequest());

    [Fact]
    public void DecodeStatus_ReadsTheRecordOneByteIn_MaskingTheFlagNibbles() {
        // Right-attached (count byte 12 = 2 fans), flags in slot 0's high nibble, the wireless theme
        // bits in slot 1's and the firmware version in slots 2 and 3's, all masked off the RPMs.
        var record = new FakeWirelessRecord(Mac, Master) {
            Channel = 9,
            Receiver = 3,
            FanCountByte = 12,
            FanTypes = new byte[] { 51, 52, 0, 0 },
            Rpm = new[] { 1200, 1150, 0, 0 },
            RpmHighNibbles = new byte[] { 0xA, 0x3, 0x2, 0x6 },
            Pwm = new byte[] { 128, 128, 0, 0 },
            Sequence = 7,
        };

        WirelessDeviceRecord status = FlexReceiverProtocol.DecodeStatus(StatusReply(record))!;

        Assert.Equal("a1b2c3d4e5f6", status.MacText);
        Assert.True(status.IsBoundTo(Master));
        Assert.Equal((byte)9, status.Channel);
        Assert.Equal((byte)3, status.ReceiverType);
        Assert.Equal(2, status.FanCount);
        Assert.True(status.RightAttached);
        Assert.Equal(new byte[] { 51, 52, 0, 0 }, status.FanTypes);
        Assert.Equal(new[] { 1200, 1150, 0, 0 }, status.Rpm);
        Assert.Equal(new byte[] { 128, 128, 0, 0 }, status.Pwm);
        Assert.Equal((byte)7, status.CommandSequence);
    }

    [Fact]
    public void DecodeStatus_KeepsAnAllZeroPwm_AsTheWiredParseDoes() {
        // RefreshList rewrites an all-zero PWM to 100 while the first fan spins; SetStatus does not.
        var record = new FakeWirelessRecord(Mac, Master) { FanCountByte = 1, FanTypes = new byte[] { 63, 0, 0, 0 }, Rpm = new[] { 900, 0, 0, 0 } };

        Assert.Equal(new byte[4], FlexReceiverProtocol.DecodeStatus(StatusReply(record))!.Pwm);
    }

    [Fact]
    public void DecodeStatus_RefusesAReplyThatDoesNotEchoTheCommand_OrIsTooShort() {
        var record = new FakeWirelessRecord(Mac, Master) { FanCountByte = 2 };

        Assert.Null(FlexReceiverProtocol.DecodeStatus(StatusReply(record, echo: 0x13)));
        Assert.Null(FlexReceiverProtocol.DecodeStatus(new byte[64])); // a read that timed out, as L-Connect would parse it
        Assert.Null(FlexReceiverProtocol.DecodeStatus(StatusReply(record).Take(42).ToArray()));
        Assert.NotNull(FlexReceiverProtocol.DecodeStatus(StatusReply(record).Take(43).ToArray()));
        Assert.Throws<ArgumentNullException>(() => FlexReceiverProtocol.DecodeStatus(null!));
    }

    [Fact]
    public void EncodeSpeed_PutsOneByteASlotAfterTheCommand_SendingTheSyncValueAsZero()
        => Assert.Equal(Packet(0x13, 20, 0, 255, 13), FlexReceiverProtocol.EncodeSpeed(new byte[] { 20, 6, 255, 13 }));

    [Fact]
    public void EncodeSpeed_RejectsMoreSlotsThanThePacketHolds_AndNull() {
        Assert.Equal(64, FlexReceiverProtocol.EncodeSpeed(new byte[63]).Length);
        Assert.Throws<ArgumentException>(() => FlexReceiverProtocol.EncodeSpeed(new byte[64]));
        Assert.Throws<ArgumentNullException>(() => FlexReceiverProtocol.EncodeSpeed(null!));
    }

    [Theory]
    [InlineData("TlFlex", 50, 3, new[] { 128, 128, 128 })]
    [InlineData("TlFlex", 0, 0, new[] { 13 })]            // no fan reported: one slot, as Math.Max(1, fanNum)
    [InlineData("SlInfinityFlexLcd", 5, 4, new[] { 26, 26, 26, 26 })]
    [InlineData("P28V2", 0, 1, new[] { 3 })]
    public void EncodeSpeed_RepeatsTheFamilysPwmOncePerFan(string familyName, int duty, int fanCount, int[] expected) {
        var family = Enum.Parse<FlexReceiverFamily>(familyName);
        Assert.Equal(Packet(0x13, expected), FlexReceiverProtocol.EncodeSpeed(family, duty, fanCount));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void EncodeSpeed_RejectsAFanCountARecordCannotCarry(int fanCount)
        => Assert.Throws<ArgumentOutOfRangeException>(() => FlexReceiverProtocol.EncodeSpeed(FlexReceiverFamily.TlFlex, 50, fanCount));

    [Theory]
    [InlineData(new byte[] { 0x13, 0 }, true)]
    [InlineData(new byte[] { 0x13, 0, 9, 9 }, true)]
    [InlineData(new byte[] { 0x13, 1 }, false)]
    [InlineData(new byte[] { 0x12, 0 }, false)]
    [InlineData(new byte[] { 0x13 }, false)]
    public void IsSpeedAccepted_RequiresTheEchoAndAZeroStatus(byte[] reply, bool expected)
        => Assert.Equal(expected, FlexReceiverProtocol.IsSpeedAccepted(reply));

    [Fact]
    public void IsSpeedAccepted_RejectsNull()
        => Assert.Throws<ArgumentNullException>(() => FlexReceiverProtocol.IsSpeedAccepted(null!));
}
