using System;
using System.Globalization;

namespace FanControl.LianLi.Protocol;

/// <summary>
/// Pure encoder/decoder for the USB receivers of the FLEX generation and the P28 V2: the small
/// box a chain of those fans hangs off, which has a radio (it is the same device the L-Wireless
/// dongles list) and a USB port. Over USB it is a WinUSB device, vendor 0x43A8 with one product
/// id per product, driven by L-Connect's <c>slv3.WinUsbLed</c> through the service's
/// <c>TLFLEXController</c>, <c>TLFLEXLCDController</c>, <c>SLINFFlexLCDController</c> and
/// <c>P28V2Controller</c>. Every packet is 64 bytes with the command in byte 0 and no report id,
/// and the receiver answers each with one 64-byte packet that echoes the command in byte 0. The
/// status reply carries the receiver's RF address, which is its identity in both modes, and the
/// same 42-byte record the wireless list carries, one byte in. No I/O and no state.
/// </summary>
internal static class FlexReceiverProtocol {
    /// <summary>The USB vendor id of every receiver (<c>WinUsbLed.TLV3LEDRecVid</c>, 17320).</summary>
    public const int VendorId = 0x43A8;

    /// <summary>UNI FAN TL FLEX (<c>WinUsbLed.TLV3LEDRecPid</c>, 257).</summary>
    public const int TlFlexProductId = 0x0101;

    /// <summary>The receiver half of UNI FAN TL FLEX LCD (<c>TLV3LCDRecPid</c>, 258); its screen is a separate device the plugin never opens.</summary>
    public const int TlFlexLcdProductId = 0x0102;

    /// <summary>The receiver half of UNI FAN SL-INF FLEX LCD (<c>INFV3LCDRecPid</c>, 260); its screen is a separate device the plugin never opens.</summary>
    public const int SlInfinityFlexLcdProductId = 0x0104;

    /// <summary>UNI FAN P28 V2 (<c>P28V2LEDRecPid</c>, 261).</summary>
    public const int P28V2ProductId = 0x0105;

    /// <summary>Every packet, sent or received, is 64 bytes.</summary>
    public const int PacketLength = 64;

    /// <summary>
    /// The PWM byte the receiver reads as "follow the motherboard's PWM header"
    /// (<c>WinUsbLed.SetFansRPMSyncMainBoardByWired</c> sends it in all four slots). An ordinary
    /// speed write never carries it: <c>SetFansRPM</c> sends 0 for any slot that would, and so does
    /// <see cref="EncodeSpeed(byte[])"/>. The plugin never puts a receiver into motherboard sync;
    /// its first speed write takes one out of it, as L-Connect's next write does.
    /// </summary>
    public const byte MotherboardSyncPwm = 6;

    // WinUsbLed.LEDCmdType: GetInfo is the status record, SetFansPWM the per-slot speed.
    private const byte StatusCommand = 0x12;
    private const byte SpeedCommand = 0x13;

    // The status reply is the command echo at byte 0 and then the 42-byte record the wireless list
    // carries (UsbRecevierController.SetStatus reads mac_addr at 1, fans_speed at 29, cmd_seq at 41).
    private const int StatusRecordOffset = 1;

    // The service's duty rules, per product: TLFLEXController.handleSetFanRPM and
    // TLFLEXLCDController.pushFanRPMToHardware send a duty of 0 as 5 and floor the rest at 11;
    // SLINFFlexLCDController.pushFanRPMToHardware floors at 10; P28V2Controller.handleSetFanRPM
    // sends 0 as 1 and floors at 8.
    private const int FlexIdleDuty = 5;
    private const int TlFlexDutyFloor = 11;
    private const int SlInfinityFlexLcdDutyFloor = 10;
    private const int P28V2IdleDuty = 1;
    private const int P28V2DutyFloor = 8;

    /// <summary>Whether a vendor/product pair is a receiver the plugin drives over USB.</summary>
    public static bool IsReceiver(int vendorId, int productId)
        => vendorId == VendorId
            && (productId == TlFlexProductId
                || productId == TlFlexLcdProductId
                || productId == SlInfinityFlexLcdProductId
                || productId == P28V2ProductId);

    /// <summary>
    /// The family of a receiver product id. Throws <see cref="ArgumentException"/> for any other
    /// id, including the receivers L-Connect gives no wired fan control.
    /// </summary>
    public static FlexReceiverFamily FamilyOf(int productId) {
        switch (productId) {
            case TlFlexProductId:
                return FlexReceiverFamily.TlFlex;
            case TlFlexLcdProductId:
                return FlexReceiverFamily.TlFlexLcd;
            case SlInfinityFlexLcdProductId:
                return FlexReceiverFamily.SlInfinityFlexLcd;
            case P28V2ProductId:
                return FlexReceiverFamily.P28V2;
            default:
                throw new ArgumentException(string.Format(
                    CultureInfo.InvariantCulture, "Product id 0x{0:x4} is not a receiver the plugin drives over USB.", productId), nameof(productId));
        }
    }

    /// <summary>The product name the plugin shows for a family, in the form the wireless names use.</summary>
    public static string ProductName(FlexReceiverFamily family) {
        switch (family) {
            case FlexReceiverFamily.TlFlex:
                return "UNI FAN TL FLEX";
            case FlexReceiverFamily.TlFlexLcd:
                return "UNI FAN TL FLEX LCD";
            case FlexReceiverFamily.SlInfinityFlexLcd:
                return "UNI FAN SL-INF FLEX LCD";
            default:
                return "UNI FAN P28 V2";
        }
    }

    /// <summary>
    /// The lowest duty percent the service sends a family's fans for any non-zero duty: 11 for TL
    /// FLEX and TL FLEX LCD, 10 for SL-INF FLEX LCD, 8 for the P28 V2.
    /// </summary>
    public static int DutyFloor(FlexReceiverFamily family) {
        switch (family) {
            case FlexReceiverFamily.SlInfinityFlexLcd:
                return SlInfinityFlexLcdDutyFloor;
            case FlexReceiverFamily.P28V2:
                return P28V2DutyFloor;
            default:
                return TlFlexDutyFloor;
        }
    }

    /// <summary>
    /// The duty percent the service sends a family's fans for a duty of 0, without the floor: 5 for
    /// the FLEX families, 1 for the P28 V2. There is no stop command; whether either stops a fan is
    /// the firmware's business.
    /// </summary>
    public static int IdleDuty(FlexReceiverFamily family)
        => family == FlexReceiverFamily.P28V2 ? P28V2IdleDuty : FlexIdleDuty;

    /// <summary>
    /// The PWM byte for a family at a duty percent, as the service produces it: a duty of 0 becomes
    /// <see cref="IdleDuty"/>, anything else is raised to <see cref="DutyFloor"/> (and capped at
    /// 100, which the app does before the service sees it), then <c>Math.Round(q / 100.0 * 255.0)</c>
    /// with halves to even, as <c>Math.Round</c> rounds: 10% is 26, 30% is 76, 50% is 128.
    /// </summary>
    public static byte FanPwm(FlexReceiverFamily family, int dutyPercent) {
        int duty = dutyPercent <= 0 ? IdleDuty(family) : Math.Max(DutyFloor(family), Math.Min(100, dutyPercent));
        return (byte)Math.Round(duty / 100.0 * 255.0);
    }

    /// <summary>Encode the status request (<c>WinUsbLed.GetLedStatus</c>): the command alone.</summary>
    public static byte[] EncodeStatusRequest() => Packet(StatusCommand);

    /// <summary>
    /// Decode a status reply into the receiver's record: its RF address at bytes 1-6, the master it
    /// is bound to at 7-12, the fan count at 20, the per-slot fan types at 25-28, the RPMs at 29-36
    /// (twelve bits each, the high nibbles being flags and the firmware version, masked off as
    /// <c>SetStatus</c> masks them) and the PWMs at 37-40 - the wireless list record one byte in
    /// (<see cref="WirelessProtocol.DecodeRecord(byte[], int)"/>). Null for a reply that does not
    /// echo the command in byte 0, or is too short: L-Connect parses whatever it read, so a read
    /// that timed out becomes a receiver at address 00:00:00:00:00:00 with no fans, and the plugin
    /// does not copy that.
    /// </summary>
    public static WirelessDeviceRecord? DecodeStatus(byte[] reply) {
        if (reply is null) {
            throw new ArgumentNullException(nameof(reply));
        }

        if (reply.Length < StatusRecordOffset + WirelessProtocol.RecordLength || reply[0] != StatusCommand) {
            return null;
        }

        return WirelessProtocol.DecodeRecord(reply, StatusRecordOffset);
    }

    /// <summary>
    /// Encode the speed command (<c>WinUsbLed.SetFansRPM</c>): byte 0 the command, then one PWM byte
    /// per entry of <paramref name="pwms"/> at bytes 1 onward, any entry equal to
    /// <see cref="MotherboardSyncPwm"/> sent as 0, the rest of the packet zero. Throws
    /// <see cref="ArgumentException"/> for more entries than the packet has room for.
    /// </summary>
    public static byte[] EncodeSpeed(byte[] pwms) {
        if (pwms is null) {
            throw new ArgumentNullException(nameof(pwms));
        }

        if (pwms.Length > PacketLength - 1) {
            throw new ArgumentException("A speed packet carries at most " + (PacketLength - 1) + " slots.", nameof(pwms));
        }

        byte[] packet = Packet(SpeedCommand);
        for (int i = 0; i < pwms.Length; i++) {
            packet[1 + i] = pwms[i] == MotherboardSyncPwm ? (byte)0 : pwms[i];
        }

        return packet;
    }

    /// <summary>
    /// Encode the speed command for a whole chain at one duty, as every L-Connect sender builds it
    /// (<c>Enumerable.Repeat(duty, Math.Max(1, fanNum))</c>): <see cref="FanPwm"/> for the family
    /// repeated once per fan, and once when no fan is reported. One duty per receiver is L-Connect's
    /// granularity; it never sends the slots different values.
    /// </summary>
    public static byte[] EncodeSpeed(FlexReceiverFamily family, int dutyPercent, int fanCount) {
        if (fanCount < 0 || fanCount > WirelessProtocol.SlotsPerGroup) {
            throw new ArgumentOutOfRangeException(nameof(fanCount), "A receiver reports 0-" + WirelessProtocol.SlotsPerGroup + " fans.");
        }

        var pwms = new byte[Math.Max(1, fanCount)];
        byte pwm = FanPwm(family, dutyPercent);
        for (int i = 0; i < pwms.Length; i++) {
            pwms[i] = pwm;
        }

        return EncodeSpeed(pwms);
    }

    /// <summary>
    /// Whether a speed command's reply says it was taken: byte 0 echoes the command and byte 1 is 0
    /// (<c>SetFansRPM</c> returns <c>reply[0] == 0x13 &amp;&amp; reply[1] == 0</c>).
    /// </summary>
    public static bool IsSpeedAccepted(byte[] reply) {
        if (reply is null) {
            throw new ArgumentNullException(nameof(reply));
        }

        return reply.Length >= 2 && reply[0] == SpeedCommand && reply[1] == 0;
    }

    private static byte[] Packet(byte command) {
        var packet = new byte[PacketLength];
        packet[0] = command;
        return packet;
    }
}
