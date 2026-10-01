using System;

namespace FanControl.LianLi.Protocol;

/// <summary>
/// Pure encoder/decoder for the pump of the HydroShift II OLED Curve AIO. The pump hangs off the
/// cooler's lighting MCU (vendor 0x0416, pid 0x8051), a WinUSB device, not a HID one, and
/// L-Connect drives it through its lcd207 SDK (<c>WinUsbHS2</c> and <c>HS2Controller</c>, from
/// which every byte here was taken). Each command is one eight-byte packet whose first byte is the
/// command, and the MCU answers every command with one interrupt packet that echoes the command
/// in its first byte and carries the value in the next. The pump is commanded by an output value,
/// not an rpm: L-Connect converts a target rpm through a 22-point piecewise-linear table
/// (<see cref="OutputValueForRpm"/>), and the rpm it reports is the raw tachometer count less a
/// per-band correction (<see cref="DecodePumpSpeed"/>). No I/O and no state.
/// </summary>
internal static class HydroShiftCurveProtocol {
    /// <summary>The USB vendor id of the pump MCU: the same 0x0416 as the command-packet coolers.</summary>
    public const int VendorId = 0x0416;

    /// <summary>The USB product id of the pump MCU (<c>WinUsbHS2.LED_HS2Pid</c>); 0x8052 is its bootloader and is never opened.</summary>
    public const int ProductId = 0x8051;

    /// <summary>Every command is one eight-byte OUT packet.</summary>
    public const int PacketLength = 8;

    /// <summary>
    /// The length a reply is read at. The MCU answers with one interrupt packet; L-Connect reads it
    /// into a 512-byte buffer and uses its first three bytes, and one 64-byte packet holds it.
    /// </summary>
    public const int ReplyLength = 64;

    /// <summary>The slowest speed L-Connect asks the pump for (<c>MinPumpSpeedRPM</c>), which is what 0% asks for.</summary>
    public const int PumpRpmMinimum = 1600;

    /// <summary>
    /// The fastest speed every ordinary L-Connect pump mode drives (<c>MaxPumpSpeedRPM</c>: the
    /// curves and the fixed speed all top out here), which is what 100% asks for. L-Connect's Turbo
    /// curve reaches 2800 (<c>MaxPumpSpeedTurboRPM</c>), but it is a separate mode behind a warning
    /// dialog, so a plain 100% stops here.
    /// </summary>
    public const int PumpRpmMaximum = 2400;

    // Command bytes (WinUsbHS2): 0x60 answers with the liquid temperature and the motherboard-sync
    // state, 0x61 sets the pump output value, 0x62 answers with the pump tachometer, and 0x64 hands
    // the pump to the motherboard's PWM header or takes it back.
    private const byte StatusCommand = 0x60;
    private const byte SetPumpCommand = 0x61;
    private const byte PumpSpeedCommand = 0x62;
    private const byte MotherboardSyncCommand = 0x64;

    // The highest output value the table reaches; the set command carries it as a big-endian pair.
    private const int OutputValueMaximum = 0xFFFF;

    // HS2Controller.DataPoints, in its order (fastest first): the target rpm and the output value
    // the pump is sent for it. The rpm axis is what the pump was measured to run at for each output
    // value, so the table is not evenly spaced and is interpolated, not fitted.
    private static readonly int[] TableRpm =
    {
        2735, 2703, 2651, 2592, 2537, 2477, 2420, 2365, 2305, 2248, 2188,
        2130, 2073, 2013, 1950, 1895, 1838, 1783, 1725, 1663, 1608, 1577,
    };

    private static readonly int[] TableOutput =
    {
        2300, 2200, 2100, 2000, 1900, 1800, 1700, 1600, 1500, 1400, 1300,
        1200, 1100, 1000, 900, 800, 700, 600, 500, 400, 300, 250,
    };

    // HS2Controller.GetPumpSpeed: the tachometer over-reads by a band-dependent amount, which
    // L-Connect subtracts before showing or using the speed.
    private const int LowBandCeiling = 1800;
    private const int LowBandCorrection = 40;
    private const int MiddleBandCeiling = 2500;
    private const int MiddleBandCorrection = 50;
    private const int HighBandCorrection = 30;

    /// <summary>Whether a vendor/product pair is the pump MCU.</summary>
    public static bool IsPump(int vendorId, int productId)
        => vendorId == VendorId && productId == ProductId;

    /// <summary>
    /// Encode the status request (<c>WinUsbHS2.GetTemperlate</c>), whose reply
    /// <see cref="DecodeStatus"/> reads.
    /// </summary>
    public static byte[] EncodeStatusRequest() => Packet(StatusCommand);

    /// <summary>
    /// Encode the pump tachometer request (<c>WinUsbHS2.GetPumpSpeed</c>), whose reply
    /// <see cref="DecodePumpSpeed"/> reads.
    /// </summary>
    public static byte[] EncodePumpSpeedRequest() => Packet(PumpSpeedCommand);

    /// <summary>
    /// Encode the set-pump command (<c>WinUsbHS2.SetPumpSpeed</c>): <paramref name="outputValue"/>,
    /// from <see cref="OutputValueForRpm"/>, as a big-endian pair at bytes 1-2. Throws
    /// <see cref="ArgumentOutOfRangeException"/> for a value the pair cannot carry.
    /// </summary>
    public static byte[] EncodeSetPump(int outputValue) {
        if (outputValue < 0 || outputValue > OutputValueMaximum) {
            throw new ArgumentOutOfRangeException(nameof(outputValue), "The pump output value is a 16-bit quantity.");
        }

        return Packet(SetPumpCommand, (byte)(outputValue >> 8), (byte)(outputValue & 0xFF));
    }

    /// <summary>
    /// Encode the motherboard-sync command (<c>WinUsbHS2.SetMBSync</c>): byte 1 is 0 to hand the
    /// pump to the motherboard's PWM header and 1 to take it back under software control.
    /// </summary>
    public static byte[] EncodeMotherboardSync(bool sync) => Packet(MotherboardSyncCommand, sync ? (byte)0 : (byte)1);

    /// <summary>
    /// Decode a status reply: byte 1 is the liquid temperature in whole degrees Celsius, and byte 2
    /// is 0 while the pump follows the motherboard's PWM header (<c>HS2Controller.GetIsSyncMBPwm</c>).
    /// Null for a reply whose first byte does not echo the status command: this MCU echoes the
    /// command in every reply L-Connect checks (<c>WinUsbHS2</c>'s motor, version and screen
    /// replies), and a packet echoing another command is a stale reply to that other command, which
    /// read as a status would give a liquid temperature of a tachometer's high byte. Throws
    /// <see cref="ArgumentException"/> for a reply too short to hold both values.
    /// </summary>
    public static HydroShiftCurveStatus? DecodeStatus(byte[] reply) {
        RequireBytes(reply, 3);
        if (reply[0] != StatusCommand) {
            return null;
        }

        return new HydroShiftCurveStatus(reply[1], reply[2] == 0);
    }

    /// <summary>
    /// Decode a tachometer reply into the pump speed L-Connect reports: the big-endian count at
    /// bytes 1-2, less 40 up to 1800, 50 up to 2500 and 30 above that, and never below 0
    /// (<c>HS2Controller.GetPumpSpeed</c>). Null for a reply whose first byte does not echo the
    /// tachometer command (see <see cref="DecodeStatus"/>). Throws <see cref="ArgumentException"/>
    /// for a reply too short to hold the pair.
    /// </summary>
    public static int? DecodePumpSpeed(byte[] reply) {
        RequireBytes(reply, 3);
        if (reply[0] != PumpSpeedCommand) {
            return null;
        }

        int raw = (reply[1] << 8) | reply[2];
        int corrected = raw <= LowBandCeiling
            ? raw - LowBandCorrection
            : raw <= MiddleBandCeiling ? raw - MiddleBandCorrection : raw - HighBandCorrection;
        return corrected < 0 ? 0 : corrected;
    }

    /// <summary>
    /// The output value to send for a target speed (<c>HS2Controller.GetOutputValue</c>): the
    /// table's top value at or above its fastest rpm, its bottom value at or below its slowest, and
    /// between two rows a linear interpolation rounded to the nearest integer, halves to even, as
    /// <c>Math.Round</c> rounds it.
    /// </summary>
    public static int OutputValueForRpm(int rpm) {
        int last = TableRpm.Length - 1;
        if (rpm >= TableRpm[0]) {
            return TableOutput[0];
        }

        if (rpm <= TableRpm[last]) {
            return TableOutput[last];
        }

        // The rows are strictly descending, so a target inside the table lies between exactly one
        // pair of neighbours, and the loop always finds it.
        int row = 0;
        while (rpm < TableRpm[row + 1]) {
            row++;
        }

        double position = (double)(rpm - TableRpm[row]) / (TableRpm[row + 1] - TableRpm[row]);
        return (int)Math.Round(TableOutput[row] + (position * (TableOutput[row + 1] - TableOutput[row])));
    }

    /// <summary>
    /// The pump speed to ask for at a duty percent: the ordinary range spanned linearly, so 0% is
    /// <see cref="PumpRpmMinimum"/> and 100% <see cref="PumpRpmMaximum"/>, as the wireless water
    /// blocks are spanned.
    /// </summary>
    public static int PumpRpmFromDuty(int dutyPercent) {
        int duty = dutyPercent < 0 ? 0 : (dutyPercent > 100 ? 100 : dutyPercent);
        return PumpRpmMinimum + ((PumpRpmMaximum - PumpRpmMinimum) * duty / 100);
    }

    private static byte[] Packet(byte command, byte first = 0, byte second = 0) {
        var packet = new byte[PacketLength];
        packet[0] = command;
        packet[1] = first;
        packet[2] = second;
        return packet;
    }

    private static void RequireBytes(byte[] reply, int count) {
        if (reply is null) {
            throw new ArgumentNullException(nameof(reply));
        }

        if (reply.Length < count) {
            throw new ArgumentException(
                "A reply carries at least " + count + " bytes; this one has " + reply.Length + ".", nameof(reply));
        }
    }
}
