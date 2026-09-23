using System;
using System.Collections.Generic;

namespace FanControl.LianLi.Protocol;

/// <summary>
/// Pure encoder/decoder for the L-Wireless SYNC controller: the transmitter dongle (pid 0x8040) that
/// carries RF commands to the wireless UNI FAN groups bound to it, and the receiver dongle (pid
/// 0x8041) that collects their telemetry. Both are plain USB (WinUSB) devices with one interrupt
/// endpoint pair carrying 64-byte packets. A fan group is addressed by its 6-byte RF address; a
/// speed command is a 240-byte RF payload the transmitter sends in four 60-byte chunks, and the
/// receiver answers a list request with a 42-byte record per device it can hear. Decompiled from
/// L-Connect's slv3.dll (MasterDevice, WinUsb, RfDevice). No I/O and no state - the byte math is
/// testable in isolation.
/// </summary>
internal static class WirelessProtocol {
    /// <summary>The Lian Li USB vendor id both dongles enumerate under.</summary>
    public const int VendorId = 0x0416;

    /// <summary>The transmitter (master) dongle: SLV3TX.</summary>
    public const int TransmitterProductId = 0x8040;

    /// <summary>The receiver dongle: SLV3RX.</summary>
    public const int ReceiverProductId = 0x8041;

    /// <summary>An alternate vendor/product pair L-Connect also accepts for the same dongles.</summary>
    public const int AlternateVendorId = 0x1A86;

    /// <summary>The alternate transmitter product id.</summary>
    public const int AlternateTransmitterProductId = 0xE304;

    /// <summary>The alternate receiver product id.</summary>
    public const int AlternateReceiverProductId = 0xE305;

    /// <summary>Every USB packet to or from a dongle is exactly this long.</summary>
    public const int PacketLength = 64;

    /// <summary>An RF address is six bytes.</summary>
    public const int MacLength = 6;

    /// <summary>A fan group has four fan slots, whatever its fan count.</summary>
    public const int SlotsPerGroup = 4;

    /// <summary>The RF channel L-Connect uses unless the user saved another.</summary>
    public const int DefaultChannel = 8;

    /// <summary>Each device occupies 42 bytes of the receiver's list reply.</summary>
    public const int RecordLength = 42;

    /// <summary>The receiver returns at most ten records per requested page.</summary>
    public const int RecordsPerPage = 10;

    /// <summary>The device-type byte of a master (transmitter) dongle's own record.</summary>
    public const byte MasterDeviceType = 0xFF;

    /// <summary>The device-type byte of a fan group.</summary>
    public const byte FanGroupDeviceType = 0x00;

    /// <summary>The device-type byte of the first-generation wireless water block (L-Connect's WaterBlock / H2).</summary>
    public const byte WaterBlockDeviceType = 10;

    /// <summary>The device-type byte of the second-generation wireless water block (L-Connect's WaterBlock2 / H2S).</summary>
    public const byte WaterBlock2DeviceType = 11;

    /// <summary>The device-type byte of the Lancool 217 case fans.</summary>
    public const byte CaseFanDeviceType = 65;

    /// <summary>The device-type byte of the V150.</summary>
    public const byte V150DeviceType = 66;

    /// <summary>A lighting effect's identity is four bytes.</summary>
    public const int EffectIndexLength = 4;

    /// <summary>The pump-parameter block a water block takes is 32 bytes.</summary>
    public const int AioParameterLength = 32;

    /// <summary>The lowest pump speed either water block accepts.</summary>
    public const int PumpRpmMinimum = 1600;

    /// <summary>The highest pump speed the first-generation water block's timer table covers.</summary>
    public const int WaterBlockPumpRpmMaximum = 2500;

    /// <summary>
    /// The fastest L-Connect ever drives a first-generation pump: every pump curve it offers for the
    /// block tops out here (<c>LWirelessRPMSettingManager</c>, <c>MaxSpeed = 2450</c>), short of what
    /// the timer table would accept. The second generation's curves reach its table's 3200.
    /// </summary>
    public const int WaterBlockPumpRpmDriven = 2450;

    /// <summary>The highest pump speed the second-generation water block accepts.</summary>
    public const int WaterBlock2PumpRpmMaximum = 3200;

    /// <summary>The receiver slot a broadcast (clock, save config) is sent to: every slot.</summary>
    public const int BroadcastReceiverType = 0xFF;

    /// <summary>
    /// The slot a water block reports its pump in: its RPM in the fourth RPM slot and the coolant
    /// temperature in the fourth type byte (<c>RFController.GetAioTemp</c> reads <c>fans_type[3]</c>),
    /// which leaves the first three slots for fans.
    /// </summary>
    public const int WaterBlockPumpSlot = 3;

    // L-Connect reads 434 bytes per page: the 4-byte header, ten 42-byte records, and slack.
    private const int PageLength = 434;
    private const int ListHeaderLength = 4;

    // The master-query reply runs to the firmware version in bytes 11-12.
    private const int MasterReplyLength = 13;

    // Both clocks count 0.625 ms ticks (QuerryMasterMac, RefreshList).
    private const double ClockMillisecondsPerTick = 0.625;

    // An RF payload is 240 bytes, carried in 60-byte chunks after the transmit packet's 4-byte header.
    private const int RfPayloadLength = 240;
    private const int ChunkPayloadLength = 60;
    private const int TransmitHeaderLength = 4;

    // Transmitter packet commands (byte 0).
    private const byte TransmitCommand = 0x10;     // one chunk of an RF payload follows
    private const byte QueryMasterCommand = 0x11;  // reply carries the dongle's own RF address
    private const byte DongleResetCommand = 0x15;  // sent to either dongle, through the other's failures

    // Receiver packet command (byte 0): request the list of devices it can hear.
    private const byte DeviceListCommand = 0x10;

    // Every RF payload starts with this header byte; byte 1 selects the command.
    private const byte RfHeader = 0x12;
    private const byte RfBindAndSpeedCommand = 0x10;
    private const byte RfClockCommand = 0x14;        // keeps effects in step across devices
    private const byte RfSaveConfigurationCommand = 0x15;   // devices commit their state to flash
    private const byte RfEffectDataCommand = 0x20;   // one chunk of a rendered lighting effect
    private const byte RfAioParametersCommand = 0x21; // a water block's pump and screen parameters
    private const byte RfWirelessThemeCommand = 0x19; // a water block's screen shows its own theme, not PC-streamed content

    // An effect is streamed as a header chunk followed by 220-byte slices of its compressed data.
    private const int EffectChunkLength = 220;
    private const int EffectChunkOffset = 20;

    // Where the pump-parameter block sits in its payload.
    private const int AioParameterOffset = 18;

    // The last byte of a valid list record.
    private const byte RecordMarker = 0x1C;

    // A group reporting ten or more fans is flagged as having its SL-Infinity end-cap on the right;
    // the count is the value less ten.
    private const int RightAttachOffset = 10;

    // What RefreshList reads a device reporting no PWM while spinning as.
    private const byte UnreportedPwm = 100;

    // The duty L-Connect's service sends when a curve asks for nothing: getTemperatureDuty and
    // calculateDuty both return 5 for a zero speed, below every floor.
    private const int IdleDuty = 5;

    // LWirelessController.startWriteCaseSpeed floors both Lancool 217 curves at 11.
    private const int CaseDutyFloor = 11;

    // LWirelessController.startWriteFanSpeed's floors by LWirelessFanType: SL V3 14; TL V2 LED and
    // SL-Infinity 11; TL V2 LCD, CL and everything else 10.
    private const int SlV3DutyFloor = 14;
    private const int LedDutyFloor = 11;
    private const int DefaultDutyFloor = 10;

    // MasterDevice.NeedSyncPwm steps a CL group's 153 and 154 down to 152 and 155 up to 156.
    private const byte ClReservedLow = 153;
    private const byte ClReservedHigh = 155;
    private const byte ClBelowReserved = 152;
    private const byte ClAboveReserved = 156;

    /// <summary>Whether a vendor/product pair is one of the two dongles.</summary>
    public static bool IsDongle(int vendorId, int productId)
        => IsTransmitter(vendorId, productId) || IsReceiver(vendorId, productId);

    /// <summary>Whether a vendor/product pair is the transmitter dongle.</summary>
    public static bool IsTransmitter(int vendorId, int productId)
        => (vendorId == VendorId && productId == TransmitterProductId)
            || (vendorId == AlternateVendorId && productId == AlternateTransmitterProductId);

    /// <summary>Whether a vendor/product pair is the receiver dongle.</summary>
    public static bool IsReceiver(int vendorId, int productId)
        => (vendorId == VendorId && productId == ReceiverProductId)
            || (vendorId == AlternateVendorId && productId == AlternateReceiverProductId);

    /// <summary>
    /// Encode the transmitter query whose reply carries the master's own RF address. Byte 1 is the
    /// RF channel the transmitter should work on, so the query also selects the channel
    /// (<c>MasterDevice.QuerryMasterMac</c>, which L-Connect sends every second).
    /// </summary>
    public static byte[] EncodeMasterQuery(int channel) {
        var packet = new byte[PacketLength];
        packet[0] = QueryMasterCommand;
        packet[1] = (byte)channel;
        return packet;
    }

    /// <summary>
    /// Decode the master-query reply: bytes 1-6 the master's RF address, 7-10 its clock in
    /// 0.625 ms ticks (big-endian), 11-12 the transmitter's firmware version. Null for a reply that
    /// does not echo the query, which L-Connect ignores.
    /// </summary>
    public static WirelessMasterReply? DecodeMasterQuery(byte[] reply) {
        if (reply is null) {
            throw new ArgumentNullException(nameof(reply));
        }

        if (reply.Length < MasterReplyLength || reply[0] != QueryMasterCommand) {
            return null;
        }

        var mac = new byte[MacLength];
        Array.Copy(reply, 1, mac, 0, MacLength);

        // QuerryMasterMac: the ticks are summed as an int and scaled by 0.625 into an int.
        int ticks = (reply[7] << 24) + (reply[8] << 16) + (reply[9] << 8) + reply[10];
        return new WirelessMasterReply(mac, (int)(ticks * ClockMillisecondsPerTick));
    }

    /// <summary>
    /// Encode the reset L-Connect sends a dongle after five consecutive failed writes to its
    /// partner (<c>MasterDevice.ResetRx</c> and <c>ResetTx</c>): command 0x15 and nothing else.
    /// </summary>
    public static byte[] EncodeDongleReset() {
        var packet = new byte[PacketLength];
        packet[0] = DongleResetCommand;
        return packet;
    }

    /// <summary>
    /// Encode the receiver's device-list request for <paramref name="pages"/> pages of ten records.
    /// Bytes 2-3 stay zero: L-Connect fills them only while it forwards the motherboard header's
    /// speed (<c>MasterDevice.GetDev</c> with <c>FgSync</c>), which the plugin does not do.
    /// </summary>
    public static byte[] EncodeDeviceListRequest(int pages) {
        var packet = new byte[PacketLength];
        packet[0] = DeviceListCommand;
        packet[1] = (byte)pages;
        return packet;
    }

    /// <summary>How many bytes to read back for a <paramref name="pages"/>-page list reply.</summary>
    public static int DeviceListReplyLength(int pages) => PageLength * pages;

    /// <summary>
    /// How many pages the next list request asks for after a reply reporting <paramref name="total"/>
    /// devices: exactly enough for all of them, which grows or shrinks the request
    /// (<c>MasterDevice.RefreshList</c>: <c>get_page_cnt = Math.Ceiling(total / 10.0)</c>).
    /// </summary>
    public static int PagesFor(int total) => (total + RecordsPerPage - 1) / RecordsPerPage;

    /// <summary>
    /// Decode a list reply to a request for <paramref name="pagesRequested"/> pages. Records start
    /// at byte 4 and are 42 bytes each; at most the requested pages' worth are read however many the
    /// receiver reports, and one whose end marker is wrong is skipped, as <c>RefreshList</c> does.
    /// Null for a reply that does not echo the request.
    /// </summary>
    public static WirelessDeviceList? DecodeDeviceList(byte[] reply, int pagesRequested) {
        if (reply is null) {
            throw new ArgumentNullException(nameof(reply));
        }

        if (reply.Length < ListHeaderLength || reply[0] != DeviceListCommand) {
            return null;
        }

        int total = reply[1];
        int count = Math.Min(total, pagesRequested * RecordsPerPage);
        var records = new List<WirelessDeviceRecord>();
        for (int i = 0; i < count; i++) {
            int offset = ListHeaderLength + (i * RecordLength);
            if (offset + RecordLength > reply.Length) {
                break;
            }

            if (reply[offset + RecordLength - 1] != RecordMarker) {
                continue;
            }

            records.Add(DecodeRecord(reply, offset));
        }

        return new WirelessDeviceList(total, records);
    }

    // Record layout (offsets within the 42 bytes): 0-5 device address, 6-11 master address,
    // 12 channel, 13 receiver slot, 14-17 device clock, 18 device type, 19 fan count (+10 = the
    // SL-Infinity end cap is on the right), 20-23 running effect identity, 24-27 per-slot fan type,
    // 28-35 per-slot RPM as big-endian 16-bit, 36-39 per-slot PWM, 40 command sequence, 41 marker.
    private static WirelessDeviceRecord DecodeRecord(byte[] reply, int offset) {
        var mac = new byte[MacLength];
        var masterMac = new byte[MacLength];
        Array.Copy(reply, offset, mac, 0, MacLength);
        Array.Copy(reply, offset + 6, masterMac, 0, MacLength);

        int ticks = (reply[offset + 14] << 24) + (reply[offset + 15] << 16) + (reply[offset + 16] << 8) + reply[offset + 17];

        // RefreshList takes ten off a count of ten or more (the right-attached end cap), and the
        // FanNum getter takes ten off again whatever is still ten or more. No device has more than
        // the four slots its record carries, so anything beyond is clamped rather than trusted.
        int fanCount = reply[offset + 19];
        if (fanCount >= RightAttachOffset) {
            fanCount -= RightAttachOffset;
        }

        if (fanCount >= RightAttachOffset) {
            fanCount -= RightAttachOffset;
        }

        fanCount = Math.Min(fanCount, SlotsPerGroup);

        var effectIndex = new byte[EffectIndexLength];
        Array.Copy(reply, offset + 20, effectIndex, 0, EffectIndexLength);

        var fanTypes = new byte[SlotsPerGroup];
        Array.Copy(reply, offset + 24, fanTypes, 0, SlotsPerGroup);

        var rpm = new int[SlotsPerGroup];
        for (int slot = 0; slot < SlotsPerGroup; slot++) {
            int at = offset + 28 + (slot * 2);
            rpm[slot] = (reply[at] << 8) | reply[at + 1];
        }

        var pwm = new byte[SlotsPerGroup];
        Array.Copy(reply, offset + 36, pwm, 0, SlotsPerGroup);

        // RefreshList reads a device that reports no PWM at all while its first fan's speed has a
        // high byte (fans_speed[0] is the high byte of slot 0's RPM) as running at 100 on every slot.
        if (pwm[0] == 0 && pwm[1] == 0 && pwm[2] == 0 && pwm[3] == 0 && reply[offset + 28] > 0) {
            for (int slot = 0; slot < SlotsPerGroup; slot++) {
                pwm[slot] = UnreportedPwm;
            }
        }

        return new WirelessDeviceRecord(
            mac,
            masterMac,
            reply[offset + 12],
            reply[offset + 13],
            (long)(ticks * ClockMillisecondsPerTick),
            reply[offset + 18],
            fanCount,
            effectIndex,
            fanTypes,
            rpm,
            pwm,
            reply[offset + 40]);
    }

    /// <summary>
    /// Encode the RF payload that sets a device's four slot PWMs, exactly as
    /// <c>MasterDevice.SyncPwm</c> builds it. It is the same command that binds a device to a master,
    /// so it restates the binding: the master's address, the receiver slot the device was bound
    /// under, the master's channel, and <paramref name="bindIndex"/> (the device's 1-based position
    /// among the bound devices; 0 would unbind it, so it is refused here). The PWM bytes are on the
    /// device's 0-255 scale; see <see cref="FanPwm"/> and <see cref="CasePwm"/>.
    /// </summary>
    public static byte[] EncodeSpeedPayload(
        byte[] deviceMac, byte[] masterMac, int receiverType, int channel, int bindIndex, byte[] pwm) {
        if (deviceMac is null) {
            throw new ArgumentNullException(nameof(deviceMac));
        }

        if (masterMac is null) {
            throw new ArgumentNullException(nameof(masterMac));
        }

        if (pwm is null) {
            throw new ArgumentNullException(nameof(pwm));
        }

        if (deviceMac.Length != MacLength || masterMac.Length != MacLength) {
            throw new ArgumentException("An RF address is " + MacLength + " bytes.");
        }

        if (pwm.Length != SlotsPerGroup) {
            throw new ArgumentException("A group has " + SlotsPerGroup + " slots.", nameof(pwm));
        }

        if (bindIndex <= 0 || bindIndex > byte.MaxValue) {
            throw new ArgumentOutOfRangeException(nameof(bindIndex), "The bind index is 1-255; 0 unbinds the group.");
        }

        // Payload layout: 0 header, 1 command, 2-7 device address, 8-13 master address, 14 receiver
        // slot, 15 channel, 16 bind index, 17-20 slot PWMs, rest zero.
        var payload = new byte[RfPayloadLength];
        payload[0] = RfHeader;
        payload[1] = RfBindAndSpeedCommand;
        Array.Copy(deviceMac, 0, payload, 2, MacLength);
        Array.Copy(masterMac, 0, payload, 8, MacLength);
        payload[14] = (byte)receiverType;
        payload[15] = (byte)channel;
        payload[16] = (byte)bindIndex;
        Array.Copy(pwm, 0, payload, 17, SlotsPerGroup);
        return payload;
    }

    /// <summary>
    /// Split an RF payload into the 64-byte packets the transmitter takes: byte 0 the transmit
    /// command, 1 the chunk index, 2 the channel, 3 the receiver slot to address, 4-63 the next
    /// sixty bytes of payload. A 240-byte payload is four packets.
    /// </summary>
    public static byte[][] EncodeTransmitPackets(int channel, int receiverType, byte[] payload) {
        if (payload is null) {
            throw new ArgumentNullException(nameof(payload));
        }

        int chunks = (payload.Length + ChunkPayloadLength - 1) / ChunkPayloadLength;
        var packets = new byte[chunks][];
        for (int chunk = 0; chunk < chunks; chunk++) {
            var packet = new byte[PacketLength];
            packet[0] = TransmitCommand;
            packet[1] = (byte)chunk;
            packet[2] = (byte)channel;
            packet[3] = (byte)receiverType;
            int start = chunk * ChunkPayloadLength;
            Array.Copy(payload, start, packet, TransmitHeaderLength, Math.Min(ChunkPayloadLength, payload.Length - start));
            packets[chunk] = packet;
        }

        return packets;
    }

    /// <summary>
    /// Encode the payload that hands a water block its 32-byte parameter block (see
    /// <see cref="EncodeAioParameters"/>). L-Connect sends it every second to every bound water block.
    /// </summary>
    public static byte[] EncodeAioPayload(byte[] deviceMac, byte[] masterMac, int receiverType, int channel, byte[] parameters) {
        if (parameters is null) {
            throw new ArgumentNullException(nameof(parameters));
        }

        if (parameters.Length != AioParameterLength) {
            throw new ArgumentException("The parameter block is " + AioParameterLength + " bytes.", nameof(parameters));
        }

        byte[] payload = AddressedPayload(RfAioParametersCommand, deviceMac, masterMac, receiverType, channel);
        Array.Copy(parameters, 0, payload, AioParameterOffset, AioParameterLength);
        return payload;
    }

    /// <summary>
    /// The 32-byte parameter block for a water block, laid out as <c>RFController.SetAioParams</c>
    /// lays it out: 0-3 CPU temperature, CPU load, GPU temperature, GPU load; 4-5 the fan speed
    /// figure, big-endian; 6 the refresh interval; 7 whether the pump temperature is shown; 8-12 whether
    /// the CPU temperature, CPU load, GPU temperature, GPU load and fan speed are shown; 13-24 three
    /// ARGB colours (title, value, unit); 25 brightness; 26 always 1; 27 theme; 28-29 the pump timer;
    /// 30 rotation; 31 zero. Every saved setting comes from <paramref name="presentation"/>. The four
    /// live CPU and GPU figures are the one part the plugin has nothing to fill with, so they are
    /// sent as zero and hidden rather than frozen on the screen at a wrong value.
    /// A screen saved in advance mode keeps its saved settings too, with theme 0, as L-Connect's own
    /// setting handlers write it (<c>handleSetPumpLCDBrightness</c> and the rest call
    /// <c>SetAioParams</c> whatever the mode, and the wireless theme is applied only out of advance
    /// mode, so the theme byte stays 0).
    /// </summary>
    public static byte[] EncodeAioParameters(WirelessAioPresentation presentation, int pumpTimer) {
        if (presentation is null) {
            throw new ArgumentNullException(nameof(presentation));
        }

        var parameters = new byte[AioParameterLength];
        parameters[28] = (byte)(pumpTimer >> 8);
        parameters[29] = (byte)(pumpTimer & 0xFF);

        parameters[4] = unchecked((byte)(presentation.FanSpeed >> 8));
        parameters[5] = unchecked((byte)(presentation.FanSpeed & 0xFF));
        parameters[6] = presentation.RefreshInterval;
        parameters[7] = presentation.PumpTemperatureShown ? (byte)1 : (byte)0;
        parameters[12] = presentation.FanSpeedShown ? (byte)1 : (byte)0;
        WriteColour(parameters, 13, presentation.Title);
        WriteColour(parameters, 17, presentation.Value);
        WriteColour(parameters, 21, presentation.Unit);
        parameters[25] = presentation.Brightness;
        parameters[26] = 1;
        parameters[27] = presentation.ThemeIndex;
        parameters[30] = presentation.Rotation;
        return parameters;
    }

    private static void WriteColour(byte[] parameters, int offset, WirelessAioPresentation.Argb colour) {
        parameters[offset] = colour.Alpha;
        parameters[offset + 1] = colour.Red;
        parameters[offset + 2] = colour.Green;
        parameters[offset + 3] = colour.Blue;
    }

    /// <summary>
    /// The fastest a water block of the given device type is driven: the top of L-Connect's pump
    /// curves for it, which is what 100% asks for.
    /// </summary>
    public static int PumpRpmMaximum(byte deviceType)
        => deviceType == WaterBlock2DeviceType ? WaterBlock2PumpRpmMaximum : WaterBlockPumpRpmDriven;

    /// <summary>
    /// The pump speed to ask for at a duty percent: the block's speed range spanned linearly, so 0%
    /// is the slowest speed it accepts and 100% the fastest.
    /// </summary>
    public static int PumpRpmFromDuty(int dutyPercent, byte deviceType) {
        int duty = dutyPercent < 0 ? 0 : (dutyPercent > 100 ? 100 : dutyPercent);
        int maximum = PumpRpmMaximum(deviceType);
        return PumpRpmMinimum + ((maximum - PumpRpmMinimum) * duty / 100);
    }

    /// <summary>
    /// The timer value the water block's parameter block carries for a target pump speed - the
    /// piecewise-linear tables L-Connect uses (one per generation), which clamp the speed into the
    /// block's range first. A lower value is a faster pump.
    /// </summary>
    public static int PumpTimerFromRpm(int rpm, byte deviceType) {
        return deviceType == WaterBlock2DeviceType ? SecondGenerationPumpTimer(rpm) : FirstGenerationPumpTimer(rpm);
    }

    // RFController.SetAioPumpSpeed: 1600-2500 rpm.
    private static int FirstGenerationPumpTimer(int rpm) {
        rpm = rpm < PumpRpmMinimum ? PumpRpmMinimum : (rpm > WaterBlockPumpRpmMaximum ? WaterBlockPumpRpmMaximum : rpm);
        if (rpm <= 1720) {
            return 1500 - (int)((rpm - 1600) * 1.667);
        }

        if (rpm <= 1870) {
            return 1300 - ((rpm - 1720) * 2);
        }

        if (rpm <= 2000) {
            return 1000 - (int)((rpm - 1870) * 1.23);
        }

        if (rpm <= 2300) {
            return 840 - ((rpm - 2000) * 2);
        }

        if (rpm <= 2400) {
            return 240 - (int)((rpm - 2300) * 1.8);
        }

        return 60 - (int)((rpm - 2400) * 0.5);
    }

    // RFController.SetH2SAioPumpSpeed: 1600-3200 rpm.
    private static int SecondGenerationPumpTimer(int rpm) {
        rpm = rpm < PumpRpmMinimum ? PumpRpmMinimum : (rpm > WaterBlock2PumpRpmMaximum ? WaterBlock2PumpRpmMaximum : rpm);
        if (rpm <= 1800) {
            return 1590 - (int)((rpm - 1600) * 0.95);
        }

        if (rpm <= 2000) {
            return 1400 - (rpm - 1800);
        }

        if (rpm <= 2200) {
            return 1200 - (rpm - 2000);
        }

        if (rpm <= 2400) {
            return 1000 - (rpm - 2200);
        }

        if (rpm <= 2600) {
            return 800 - (rpm - 2400);
        }

        if (rpm <= 2800) {
            return 580 - (int)((rpm - 2600) * 1.11);
        }

        if (rpm <= 3000) {
            return 330 - (int)((rpm - 2800) * 1.2);
        }

        return 90 - (int)((rpm - 3000) * 0.45);
    }

    /// <summary>
    /// Encode the payloads that stream a saved lighting effect to a device: a header chunk carrying
    /// the effect's identity, compressed length, frame counts, LED count and frame intervals, then
    /// 220-byte slices of the compressed frame data. The header is repeated three more times after
    /// the first send, as L-Connect does, so it is returned once here and the caller repeats it.
    /// </summary>
    public static byte[][] EncodeEffectPayloads(byte[] deviceMac, byte[] masterMac, WirelessSavedEffect effect) {
        if (effect is null) {
            throw new ArgumentNullException(nameof(effect));
        }

        int chunks = (effect.Data.Length + EffectChunkLength - 1) / EffectChunkLength;
        var payloads = new byte[chunks + 1][];

        byte[] header = EffectPayload(deviceMac, masterMac, effect, 0, chunks);
        int length = effect.Data.Length;
        header[20] = (byte)(length >> 24);
        header[21] = (byte)((length >> 16) & 0xFF);
        header[22] = (byte)((length >> 8) & 0xFF);
        header[23] = (byte)(length & 0xFF);
        header[24] = 0;
        header[25] = (byte)(effect.TotalFrame >> 8);
        header[26] = (byte)(effect.TotalFrame & 0xFF);
        header[27] = effect.LedNum;
        header[32] = (byte)((int)effect.Interval >> 8);
        header[33] = (byte)((int)effect.Interval & 0xFF);
        header[34] = (byte)(effect.Interval * 100.0 % 100.0);
        header[35] = (byte)((int)effect.SubInterval >> 8);
        header[36] = (byte)((int)effect.SubInterval & 0xFF);
        header[37] = 0; // isOuterMatchMax: not part of the saved effect
        header[38] = (byte)(effect.TotalSubFrame >> 8);
        header[39] = (byte)(effect.TotalSubFrame & 0xFF);
        payloads[0] = header;

        for (int chunk = 0; chunk < chunks; chunk++) {
            byte[] payload = EffectPayload(deviceMac, masterMac, effect, chunk + 1, chunks);
            int start = chunk * EffectChunkLength;
            Array.Copy(effect.Data, start, payload, EffectChunkOffset, Math.Min(EffectChunkLength, effect.Data.Length - start));
            payloads[chunk + 1] = payload;
        }

        return payloads;
    }

    // Effect chunk layout: 0 header, 1 command, 2-7 device address, 8-13 master address, 14-17 the
    // effect identity, 18 chunk index (0 = the descriptor), 19 chunk count plus one.
    private static byte[] EffectPayload(byte[] deviceMac, byte[] masterMac, WirelessSavedEffect effect, int index, int chunks) {
        byte[] payload = AddressedPayload(RfEffectDataCommand, deviceMac, masterMac, 0, 0);
        Array.Copy(effect.EffectIndex, 0, payload, 14, EffectIndexLength);
        payload[18] = (byte)index;
        payload[19] = (byte)(chunks + 1);
        return payload;
    }

    /// <summary>
    /// Encode the clock pulse: a broadcast carrying only the master's address, which the devices use
    /// to keep their clocks, and so their effects, in step with the master's. L-Connect sends it every
    /// second whatever it is doing (<c>MasterDevice.SyncMasterClock</c>). Unlike every other payload
    /// it addresses nobody - the device address stays all zero - because it is for whoever can hear it.
    /// </summary>
    public static byte[] EncodeClockPayload(byte[] masterMac)
        => AddressedPayload(RfClockCommand, new byte[MacLength], masterMac, 0, 0);

    /// <summary>
    /// Encode the broadcast that tells every device bound to the master to commit its state
    /// (binding and effect) to flash (<c>MasterDevice.SaveConfig</c>): the broadcast address, every
    /// receiver slot at byte 14, and zero at 15 and 16.
    /// </summary>
    public static byte[] EncodeSaveConfigurationPayload(byte[] masterMac) {
        var broadcast = new byte[MacLength];
        for (int i = 0; i < MacLength; i++) {
            broadcast[i] = 0xFF;
        }

        return AddressedPayload(RfSaveConfigurationCommand, broadcast, masterMac, 0xFF, 0);
    }

    /// <summary>
    /// The payload that switches a water block's screen to its wireless theme
    /// (<c>MasterDevice.SyncControlInfo</c>'s <c>aio_switch_wirelesstheme</c> branch): the device
    /// and master, the receiver slot and channel it should be on at 14 and 15, at 16 how many bound
    /// devices before it on the table the pass has counted (<c>SyncControlInfo</c>'s <c>b</c>), and at
    /// 17 the command sequence the device reports back in its record once it has carried it out.
    /// </summary>
    public static byte[] EncodeWirelessThemePayload(
        byte[] deviceMac, byte[] masterMac, int receiverType, int channel, byte countedBefore, byte sequence) {
        byte[] payload = AddressedPayload(RfWirelessThemeCommand, deviceMac, masterMac, receiverType, channel);
        payload[16] = countedBefore;
        payload[17] = sequence;
        return payload;
    }

    /// <summary>
    /// A command sequence other than the one the device last acknowledged, so the device's record
    /// can tell the command carried out from not. It runs 1-254: L-Connect wraps its sequence back
    /// to 1 before it reaches 255 (<c>RFController.Close217Wifi</c>, <c>RebootLcdGroup</c>).
    /// </summary>
    public static byte NextCommandSequence(byte acknowledged) => (byte)((acknowledged % 254) + 1);

    // The common front of every RF payload: header, command, the addressed device, the master,
    // then the receiver slot and channel bytes at 14 and 15.
    private static byte[] AddressedPayload(byte command, byte[] deviceMac, byte[] masterMac, int receiverType, int channel) {
        if (deviceMac is null) {
            throw new ArgumentNullException(nameof(deviceMac));
        }

        if (masterMac is null) {
            throw new ArgumentNullException(nameof(masterMac));
        }

        if (deviceMac.Length != MacLength || masterMac.Length != MacLength) {
            throw new ArgumentException("An RF address is " + MacLength + " bytes.");
        }

        var payload = new byte[RfPayloadLength];
        payload[0] = RfHeader;
        payload[1] = command;
        Array.Copy(deviceMac, 0, payload, 2, MacLength);
        Array.Copy(masterMac, 0, payload, 8, MacLength);
        payload[14] = (byte)receiverType;
        payload[15] = (byte)channel;
        return payload;
    }

    /// <summary>Which family a slot's fan type code belongs to; 0 (empty) and unrecognised codes are Unknown.</summary>
    public static WirelessFanFamily FamilyOf(int fanType) {
        // Type codes (RfDevice.InitAttr): 20-26 SL V3, 27-35 TL V2, 36-39 SL-Infinity, 41-42 CL.
        if (fanType >= 20 && fanType <= 26) {
            return WirelessFanFamily.SlV3;
        }

        if (fanType >= 27 && fanType <= 35) {
            return WirelessFanFamily.TlV2;
        }

        if (fanType >= 36 && fanType <= 39) {
            return WirelessFanFamily.SlInfinity;
        }

        if (fanType == 41 || fanType == 42) {
            return WirelessFanFamily.Cl;
        }

        return WirelessFanFamily.Unknown;
    }

    /// <summary>
    /// The lowest duty percent L-Connect's service sends a device's fans, picked per device the way
    /// <c>LWirelessController.convertFanType</c> and <c>startWriteFanSpeed</c> pick it: a fan group by
    /// its first slot's family - SL V3 14, SL-Infinity 11, TL V2 11 or 10 if any slot is an LCD
    /// fan (type 23-27 or 32-35, <c>RfDevice.InitAttr</c>'s <c>BindLcd</c>) - and 10 for everything
    /// else: CL, RL120 and unrecognised fans, a water block (whose fans the service always configures
    /// as <c>CLFan120LED</c>), the V150 and any unrecognised device.
    /// </summary>
    public static int GroupDutyFloor(WirelessDeviceRecord record) {
        if (record is null) {
            throw new ArgumentNullException(nameof(record));
        }

        if (record.Kind != WirelessDeviceKind.FanGroup) {
            return DefaultDutyFloor;
        }

        switch (FamilyOf(record.FanTypes[0])) {
            case WirelessFanFamily.SlV3:
                return SlV3DutyFloor;
            case WirelessFanFamily.TlV2:
                return AnyLcdFan(record.FanTypes) ? DefaultDutyFloor : LedDutyFloor;
            case WirelessFanFamily.SlInfinity:
                return LedDutyFloor;
            default:
                return DefaultDutyFloor;
        }
    }

    /// <summary>
    /// Whether L-Connect treats the device as a CL group, whose reserved PWM values it steps around
    /// (<c>NeedSyncPwm</c> checks <c>RecType[0] == CLV1</c>, which <c>InitAttr</c> sets only for a fan
    /// group whose first slot is a CL fan).
    /// </summary>
    public static bool IsClGroup(WirelessDeviceRecord record) {
        if (record is null) {
            throw new ArgumentNullException(nameof(record));
        }

        return record.Kind == WirelessDeviceKind.FanGroup && FamilyOf(record.FanTypes[0]) == WirelessFanFamily.Cl;
    }

    /// <summary>A CL group's PWM stepped off the values its firmware reserves: 153 and 154 to 152, 155 to 156.</summary>
    public static byte StepClReserved(byte pwm) {
        if (pwm == ClReservedLow || pwm == ClReservedLow + 1) {
            return ClBelowReserved;
        }

        return pwm == ClReservedHigh ? ClAboveReserved : pwm;
    }

    /// <summary>
    /// The PWM byte for a fan device at a duty percent, as L-Connect's service produces it: a duty
    /// of 0 is sent as 5% without the floor (<c>getTemperatureDuty</c> returns 5 for a zero speed),
    /// anything else is raised to <paramref name="floor"/> and capped at 100, then mapped onto 0-255.
    /// </summary>
    public static byte FanPwm(int dutyPercent, int floor)
        => PwmFromPercent(dutyPercent <= 0 ? IdleDuty : Math.Max(floor, Math.Min(100, dutyPercent)));

    /// <summary>
    /// The PWM byte for a Lancool 217 case fan at a duty percent: 0 is sent as 5%
    /// (<c>calculateDuty</c>), anything else floored at 11% (<c>startWriteCaseSpeed</c>), then mapped.
    /// </summary>
    public static byte CasePwm(int dutyPercent) => FanPwm(dutyPercent, CaseDutyFloor);

    // NumberHelper.Map(duty, 0, 100, 0, 255), constrained to 0-255 and truncated to an integer, as
    // startWriteFanSpeed and LWirelessDevice.SetCaseSpeed both write it.
    private static byte PwmFromPercent(int dutyPercent) {
        double mapped = 0.0 + ((255.0 - 0.0) * ((dutyPercent - 0.0) / (100.0 - 0.0)));
        return (byte)(int)Math.Max(0.0, Math.Min(255.0, mapped));
    }

    // RfDevice.InitAttr's BindLcd: the LCD variants of SL V3 and TL V2.
    private static bool AnyLcdFan(byte[] fanTypes) {
        foreach (byte fanType in fanTypes) {
            if ((fanType >= 23 && fanType <= 27) || (fanType >= 32 && fanType <= 35)) {
                return true;
            }
        }

        return false;
    }
}
