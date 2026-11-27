using System;

namespace FanControl.LianLi.Protocol;

/// <summary>
/// Pure encoder/decoder for the Galahad II Trinity AIO (vendor 0x0416, pids 0x7371/0x7373) and
/// the screen-less parts of its HydroShift siblings. Like the Uni Fan TL it speaks the 64-byte
/// <see cref="CommandPacket"/> protocol and reports RPM via a handshake reply, but it is a single
/// fan and a pump, not a multi-fan hub. The fan duty is the percent 1:1; the pump duty is
/// floored for safety (see <see cref="PumpDutyFloor"/>). The Galahad II Vision LCD's reply also
/// carries the coolant temperature (see <see cref="ReportsCoolant"/>). No I/O and no state.
/// </summary>
internal static class Galahad2Protocol {
    // Command bytes (LConnectCore.Products.Galahad2Trinity.Commands).
    private const byte HandshakeCommand = 0x81;
    private const byte SetPumpCommand = 0x8A;
    private const byte SetFanCommand = 0x8B;

    private const int DutyMax = 100;

    // The Galahad II Vision LCD's two product ids, the only ones whose handshake reply carries the
    // coolant temperature. The Trinity's reply has bytes in the same places that are not one: in a
    // Trinity owner's raw capture (issue #30) the byte where the Vision keeps its whole degrees
    // stayed at 0x01.
    private const int VisionProductId = 0x7391;
    private const int VisionAlternateProductId = 0x7395;

    // The Vision's handshake payload past the two RPM pairs: a valid flag (non-zero when the
    // cooler has a reading), the whole degrees Celsius, then the tenths. A reply whose declared
    // payload is shorter carries no coolant fields.
    private const int RpmPayloadLength = 4;
    private const int CoolantPayloadLength = 7;
    private const int CoolantValidIndex = 4;
    private const int CoolantWholeIndex = 5;
    private const int CoolantTenthsIndex = 6;
    private const int MaxTenths = 9;

    /// <summary>
    /// Lowest pump duty this plugin will ever command. L-Connect clamps only the upper bound, so a
    /// raw 0% would stop the pump; on an AIO that risks overheating the CPU. Because the wire bytes
    /// are datamined and unverified on hardware, the pump is never driven below this floor - better
    /// a pump that runs too fast than one that stops. A bad FanControl curve cannot stall it.
    /// </summary>
    public const int PumpDutyFloor = 50;

    /// <summary>
    /// Whether a Galahad II product id is the Vision LCD (0x7391 or 0x7395), whose handshake reply
    /// carries the coolant temperature. The Trinity's (0x7371/0x7373) and the HydroShift LCD's do not
    /// count: nothing shows the bytes there are a temperature.
    /// </summary>
    public static bool ReportsCoolant(int productId)
        => productId == VisionProductId || productId == VisionAlternateProductId;

    /// <summary>Encode a fan-duty command. <paramref name="dutyPercent"/> is clamped to 0-100, 1:1.</summary>
    public static byte[] EncodeSetFan(bool motherboardSync, int dutyPercent) {
        byte duty = (byte)Clamp(dutyPercent, 0, DutyMax);
        return CommandPacket.Build(SetFanCommand, Flag(motherboardSync), duty);
    }

    /// <summary>
    /// Encode a pump-duty command. <paramref name="dutyPercent"/> is clamped to the safe
    /// <see cref="PumpDutyFloor"/>-100 window so the pump can never be stopped.
    /// </summary>
    public static byte[] EncodeSetPump(bool motherboardSync, int dutyPercent) {
        byte duty = (byte)Clamp(dutyPercent, PumpDutyFloor, DutyMax);
        return CommandPacket.Build(SetPumpCommand, Flag(motherboardSync), duty);
    }

    /// <summary>Encode the handshake request whose reply carries the fan and pump RPM.</summary>
    public static byte[] EncodeHandshakeRequest() => CommandPacket.Build(HandshakeCommand);

    /// <summary>
    /// Whether a received packet answers a handshake: report id 0x01 and the command byte echoing 0x81,
    /// the checks <c>Galahad2TrinityDevice.GetHandshakeInfo</c> and
    /// <c>Galahad2VisionDevice.ReadACommandPacket</c> make before they take a packet as RPM.
    /// </summary>
    public static bool IsHandshakeReply(byte[] reply) => CommandPacket.IsReply(reply, HandshakeCommand);

    /// <summary>
    /// Decode a handshake reply into the fan and pump RPM and, when the reply has them, the coolant
    /// temperature. The reply forces a 4-byte payload: the fan RPM is the big-endian pair at payload
    /// bytes 0-1, the pump RPM the pair at bytes 2-3. A payload declared at least 7 bytes long whose
    /// byte 4 is non-zero carries the coolant temperature, whole degrees at byte 5 and tenths at byte
    /// 6; any other reply has none (null). Only a Vision's reply is read for it
    /// (<see cref="ReportsCoolant"/>); the caller ignores it on every other cooler.
    /// Callers match the reply with <see cref="IsHandshakeReply"/> first; a packet that is not one
    /// throws <see cref="ArgumentException"/>, so the echo of a set command is never read as RPM.
    /// </summary>
    public static Galahad2Reading DecodeHandshake(byte[] reply) {
        if (reply is null) {
            throw new ArgumentNullException(nameof(reply));
        }

        if (!IsHandshakeReply(reply)) {
            throw new ArgumentException("The packet does not answer a handshake.", nameof(reply));
        }

        byte[] payload = CommandPacket.Payload(reply, RpmPayloadLength);
        int fanRpm = (payload[0] << 8) | payload[1];
        int pumpRpm = (payload[2] << 8) | payload[3];
        return new Galahad2Reading(fanRpm, pumpRpm, DecodeCoolant(reply));
    }

    // The coolant temperature in a handshake reply, or null when the payload is too short to carry
    // it or the valid flag says the cooler has no reading. The tenths byte should be 0-9, but the two
    // open readers of this reply differ on one above that: liquidctl (ga2_lcd.py) adds it as
    // byte / 10, so 12 would add 1.2 degrees, and lian-li-linux (hydroshift_lcd/controller.rs) keeps
    // byte % 10, so 12 would add 0.2. Neither is known to be right, so such a byte adds nothing and
    // the whole degrees stand alone. Whole and tenths are divided once, so 37 and 8 are 37.8 exactly
    // as a float spells it.
    private static float? DecodeCoolant(byte[] reply) {
        if (CommandPacket.PayloadLengthOf(reply) < CoolantPayloadLength) {
            return null;
        }

        byte[] payload = CommandPacket.Payload(reply, CoolantPayloadLength);
        if (payload[CoolantValidIndex] == 0) {
            return null;
        }

        int whole = payload[CoolantWholeIndex];
        int tenths = payload[CoolantTenthsIndex];
        return tenths > MaxTenths ? whole : ((whole * 10) + tenths) / 10f;
    }

    private static byte Flag(bool value) => value ? (byte)1 : (byte)0;

    // Math.Clamp is unavailable on netstandard2.0, so the clamp is hand-rolled.
    private static int Clamp(int value, int min, int max) {
        if (value < min) {
            return min;
        }

        return value > max ? max : value;
    }
}
