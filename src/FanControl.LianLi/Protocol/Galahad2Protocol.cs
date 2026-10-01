using System;

namespace FanControl.LianLi.Protocol;

/// <summary>
/// Pure encoder/decoder for the Galahad II Trinity AIO (vendor 0x0416, pids 0x7371/0x7373) and
/// the screen-less parts of its HydroShift siblings. Like the Uni Fan TL it speaks the 64-byte
/// <see cref="CommandPacket"/> protocol and reports RPM via a handshake reply, but it is a single
/// fan and a pump, not a multi-fan hub. The fan duty is the percent 1:1; the pump duty is
/// floored for safety (see <see cref="PumpDutyFloor"/>). No I/O and no state.
/// </summary>
internal static class Galahad2Protocol {
    // Command bytes (LConnectCore.Products.Galahad2Trinity.Commands).
    private const byte HandshakeCommand = 0x81;
    private const byte SetPumpCommand = 0x8A;
    private const byte SetFanCommand = 0x8B;

    private const int DutyMax = 100;

    /// <summary>
    /// Lowest pump duty this plugin will ever command. L-Connect clamps only the upper bound, so a
    /// raw 0% would stop the pump; on an AIO that risks overheating the CPU. Because the wire bytes
    /// are datamined and unverified on hardware, the pump is never driven below this floor - better
    /// a pump that runs too fast than one that stops. A bad FanControl curve cannot stall it.
    /// </summary>
    public const int PumpDutyFloor = 50;

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
    /// Decode a handshake reply into the fan and pump RPM. The reply forces a 4-byte payload: the
    /// fan RPM is the big-endian pair at payload bytes 0-1, the pump RPM the pair at bytes 2-3.
    /// </summary>
    public static Galahad2Reading DecodeHandshake(byte[] reply) {
        if (reply is null) {
            throw new ArgumentNullException(nameof(reply));
        }

        byte[] payload = CommandPacket.Payload(reply, 4);
        int fanRpm = (payload[0] << 8) | payload[1];
        int pumpRpm = (payload[2] << 8) | payload[3];
        return new Galahad2Reading(fanRpm, pumpRpm);
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
