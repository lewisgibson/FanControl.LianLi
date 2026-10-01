using System;

namespace FanControl.LianLi.Protocol;

/// <summary>
/// Builds the fixed 64-byte command packet the second-vendor (0x0416) controllers - Uni Fan TL
/// and the Galahad II / HydroShift AIOs - use, in place of the Uni family's 0xE0 reports. The
/// frame is report id 0x01, the command byte, a reserved zero, a 16-bit big-endian packet number
/// (always 0 here), the payload length, then the payload; the rest is zero-padded to 64 bytes.
/// Pure: same input always yields the same bytes, so the framing is testable in isolation.
/// </summary>
internal static class CommandPacket {
    /// <summary>Report id every command packet begins with (0x01).</summary>
    public const byte ReportId = 0x01;

    /// <summary>Total packet length on the wire (64 bytes, zero-padded after the payload).</summary>
    public const int Length = 64;

    // Byte layout: [0]=report id, [1]=command, [2]=reserved 0, [3..4]=packet number (big-endian),
    // [5]=payload length, [6..]=payload.
    private const int CommandIndex = 1;
    private const int LengthIndex = 5;
    private const int PayloadIndex = 6;

    /// <summary>
    /// Build the 64-byte packet for <paramref name="command"/> carrying <paramref name="payload"/>.
    /// The payload must fit in the 58 bytes after the 6-byte header.
    /// </summary>
    public static byte[] Build(byte command, params byte[] payload) {
        if (payload is null) {
            throw new ArgumentNullException(nameof(payload));
        }

        if (payload.Length > Length - PayloadIndex) {
            throw new ArgumentException(
                "Payload does not fit in a 64-byte command packet.", nameof(payload));
        }

        byte[] packet = new byte[Length];
        packet[0] = ReportId;
        packet[CommandIndex] = command;
        packet[LengthIndex] = (byte)payload.Length;
        Array.Copy(payload, 0, packet, PayloadIndex, payload.Length);
        return packet;
    }

    /// <summary>The command byte of a received packet (byte 1).</summary>
    public static byte CommandOf(byte[] packet) => Read(packet, CommandIndex);

    /// <summary>The declared payload length of a received packet (byte 5).</summary>
    public static int PayloadLengthOf(byte[] packet) => Read(packet, LengthIndex);

    /// <summary>
    /// Copy <paramref name="count"/> payload bytes out of a received packet, starting at the
    /// payload offset (byte 6). Reads only what the packet holds.
    /// </summary>
    public static byte[] Payload(byte[] packet, int count) {
        if (packet is null) {
            throw new ArgumentNullException(nameof(packet));
        }

        if (count < 0 || PayloadIndex + count > packet.Length) {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        byte[] payload = new byte[count];
        Array.Copy(packet, PayloadIndex, payload, 0, count);
        return payload;
    }

    private static byte Read(byte[] packet, int index) {
        if (packet is null) {
            throw new ArgumentNullException(nameof(packet));
        }

        if (index >= packet.Length) {
            throw new ArgumentOutOfRangeException(nameof(packet), "Packet is shorter than the header.");
        }

        return packet[index];
    }
}
