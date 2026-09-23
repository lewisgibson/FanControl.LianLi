using System;
using System.Collections.Generic;

namespace FanControl.LianLi.Protocol;

/// <summary>
/// Pure encoder/decoder for the Uni Fan TL controller (vendor 0x0416, pid 0x7372). Unlike the
/// Uni 0xCF2 families this controller speaks the 64-byte <see cref="CommandPacket"/> protocol over
/// output reports, and reports RPM by replying to a handshake command on the interrupt-IN endpoint
/// rather than through a pulled input report. Each fan is addressed by (port, fan-index); the duty
/// byte is the percent clamped to the firmware's 12-100 window, with a 0% command sent as the idle
/// value 1. No I/O and no state - the byte math is testable in isolation.
/// </summary>
internal static class TlFanProtocol {
    // Command bytes (LConnectCore.Products.TLFan.TLFanDevice).
    private const byte SetFanSpeedCommand = 0xAA;
    private const byte HandshakeCommand = 0xA1;
    private const byte MotherboardSyncCommand = 0xB1;

    // The firmware accepts a duty of 12-100; a 0% command is sent as 1, the idle/stop value.
    private const int PwmMin = 12;
    private const int PwmMax = 100;
    private const byte PwmIdle = 1;

    // A 64-byte command packet less its 6-byte header.
    private const int MaxPayloadLength = 58;

    /// <summary>
    /// Encode a set-speed command for one fan. <paramref name="dutyPercent"/> 0 idles the fan
    /// (wire value 1); 1-100 is clamped to the firmware's 12-100 window.
    /// </summary>
    public static byte[] EncodeSetFanSpeed(int port, int fanIndex, int dutyPercent) {
        byte pwm = dutyPercent <= 0 ? PwmIdle : (byte)Clamp(dutyPercent, PwmMin, PwmMax);
        return CommandPacket.Build(SetFanSpeedCommand, Address(port, fanIndex), pwm);
    }

    /// <summary>Encode the handshake request whose reply carries the detected fans and their RPM.</summary>
    public static byte[] EncodeHandshakeRequest() => CommandPacket.Build(HandshakeCommand);

    /// <summary>
    /// Encode the motherboard-RPM-sync command for one fan. While sync is on L-Connect stops
    /// writing speeds; the high bit of the address byte carries the on/off flag.
    /// </summary>
    public static byte[] EncodeMotherboardSync(int port, int fanIndex, bool sync) {
        byte address = (byte)((sync ? 0x80 : 0x00) | Address(port, fanIndex));
        return CommandPacket.Build(MotherboardSyncCommand, address);
    }

    /// <summary>
    /// Whether a received packet answers a handshake: its command byte echoes 0xA1, the one check
    /// <c>TLFanDevice.GetHandshakeInfo</c> makes before it trusts a reply. The hub answers every
    /// command, so the other replies queue ahead of it.
    /// </summary>
    public static bool IsHandshakeReply(byte[] reply) => CommandPacket.CommandOf(reply) == HandshakeCommand;

    /// <summary>
    /// Decode a handshake reply into the detected fans and their RPM. The payload is a run of
    /// 3-byte records: byte 0 packs detected/port/fan-index, bytes 1-2 are the big-endian RPM.
    /// Undetected records are skipped, a second record for an address replaces the first, and a
    /// packet that is not a handshake reply holds no fans.
    /// </summary>
    public static IReadOnlyList<TlFanReading> DecodeHandshake(byte[] reply) {
        if (reply is null) {
            throw new ArgumentNullException(nameof(reply));
        }

        if (!IsHandshakeReply(reply)) {
            return Array.Empty<TlFanReading>();
        }

        // LEDPacket.Data reads at most the 58 bytes a 64-byte frame holds after its header, whatever
        // byte 5 claims.
        int payloadLength = Math.Min(CommandPacket.PayloadLengthOf(reply), MaxPayloadLength);
        int recordCount = payloadLength / 3;
        byte[] payload = CommandPacket.Payload(reply, recordCount * 3);

        var readings = new List<TlFanReading>(recordCount);
        for (int i = 0; i < recordCount; i++) {
            int offset = i * 3;
            byte header = payload[offset];
            bool detected = (header & 0x80) != 0;
            if (!detected) {
                continue;
            }

            int port = (header >> 4) & 0x03;
            int fanIndex = header & 0x0F;
            int rpm = (payload[offset + 1] << 8) | payload[offset + 2];
            // TLFanHandshakeInfo.Parse stores each record at its address, so a later one replaces it.
            int earlier = readings.FindIndex(r => r.Port == port && r.FanIndex == fanIndex);
            if (earlier >= 0) {
                readings[earlier] = new TlFanReading(port, fanIndex, rpm);
            } else {
                readings.Add(new TlFanReading(port, fanIndex, rpm));
            }
        }

        return readings;
    }

    // Address byte: high nibble is the port (0-3), low nibble the fan index within the port.
    private static byte Address(int port, int fanIndex) => (byte)(((port & 0x0F) << 4) | (fanIndex & 0x0F));

    // Math.Clamp is unavailable on netstandard2.0, so the clamp is hand-rolled.
    private static int Clamp(int value, int min, int max) {
        if (value < min) {
            return min;
        }

        return value > max ? max : value;
    }
}
