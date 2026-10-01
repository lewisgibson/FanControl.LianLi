#pragma warning disable CA1510, CA1512 // The released v1.1.34 source, kept verbatim as the oracle the current encoder is compared against (ReleasedLightingEncoderTests); it compiles here on net8.0, where the analyzer would have it use ThrowIfNull.
#if ENABLE_LIGHTING
using System;
using System.Collections.Generic;

namespace FanControl.LianLi.Protocol;

/// <summary>
/// Pure encoder that turns a Galahad II AIO's saved look into the two command packets L-Connect
/// sends to reproduce it: the 0x85 fan light then the 0x83 pump light, in that order, with no
/// commit/latch. No I/O and no state. The Galahad speaks the 0x0416 protocol over output reports,
/// so both transfers are <see cref="LightingTransfer.IsFeature"/> false. The plugin drives the
/// colours, so the ARGB signal source is the on-board MCU, not the motherboard.
/// </summary>
internal static class ReleasedGalahad2LightingEncoder
{
    // Command bytes and payload sizes (LConnectCore.Products.Galahad2Trinity).
    private const byte SetFanLightCommand = 0x85;
    private const byte SetPumpLightCommand = 0x83;
    private const int FanPayloadLength = 20;
    private const int PumpPayloadLength = 19;
    private const int MaxColors = 4;
    private const byte SourceMcu = 0; // 0 = on-board MCU drives the LEDs, 1 = motherboard sync.

    /// <summary>Encode the fan light (0x85) then the pump light (0x83), the order L-Connect applies.</summary>
    public static IReadOnlyList<LightingTransfer> Encode(Galahad2FanLightingState fan, Galahad2PumpLightingState pump)
    {
        if (fan is null)
        {
            throw new ArgumentNullException(nameof(fan));
        }

        if (pump is null)
        {
            throw new ArgumentNullException(nameof(pump));
        }

        return new List<LightingTransfer>(2)
        {
            EncodeFan(fan),
            EncodePump(pump),
        };
    }

    private static LightingTransfer EncodeFan(Galahad2FanLightingState fan)
    {
        byte[] payload = new byte[FanPayloadLength];
        payload[0] = (byte)fan.Mode; // fan mode is the direct enum value (no modulo)
        payload[1] = (byte)fan.Brightness;
        payload[2] = (byte)fan.Speed;
        WriteColors(payload, 3, fan.Colors); // payload[3..14] = four R,G,B triples
        payload[15] = (byte)fan.Direction;
        payload[16] = 0;          // not disabled
        payload[17] = SourceMcu;
        payload[18] = (byte)(fan.SyncToPump ? 1 : 0);
        payload[19] = (byte)fan.NumberOfLed;
        return new LightingTransfer(isFeature: false, CommandPacket.Build(SetFanLightCommand, payload));
    }

    private static LightingTransfer EncodePump(Galahad2PumpLightingState pump)
    {
        byte[] payload = new byte[PumpPayloadLength];
        payload[0] = (byte)pump.Scope;
        payload[1] = (byte)(pump.Mode % 1000); // pump mode strips the scope band the scope byte carries
        payload[2] = (byte)pump.Brightness;
        payload[3] = (byte)pump.Speed;
        WriteColors(payload, 4, pump.Colors); // payload[4..15] = four R,G,B triples
        payload[16] = (byte)pump.Direction;
        payload[17] = 0;          // not disabled
        payload[18] = SourceMcu;
        return new LightingTransfer(isFeature: false, CommandPacket.Build(SetPumpLightCommand, payload));
    }

    private static void WriteColors(byte[] payload, int offset, IReadOnlyList<RgbColor> colors)
    {
        int count = colors.Count < MaxColors ? colors.Count : MaxColors;
        for (int i = 0; i < count; i++)
        {
            int at = offset + (i * 3);
            payload[at] = colors[i].R;
            payload[at + 1] = colors[i].G;
            payload[at + 2] = colors[i].B;
        }
    }
}
#endif

#pragma warning restore CA1510, CA1512
