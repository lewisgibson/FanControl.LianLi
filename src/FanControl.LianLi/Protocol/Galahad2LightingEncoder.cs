#if ENABLE_LIGHTING
using System;
using System.Collections.Generic;

namespace FanControl.LianLi.Protocol;

/// <summary>
/// Pure encoder that turns a 0x0416 AIO cooler's saved look into the command packets L-Connect
/// sends to reproduce it: for the Galahad II Trinity the 0x85 fan light then one 0x83 pump light
/// per saved pump setting (the whole cap, or its inner and outer ring each); for the Galahad II
/// Vision the 0x85 fan light then, through the same 0x83 command, the ring of LEDs around its
/// screen; for the HydroShift LCD the 0x85 fan light alone, since its fans are its only LEDs. In
/// every case the order is L-Connect's and nothing commits or latches. No I/O and no state. These
/// coolers speak the 0x0416 protocol over output reports, so every transfer is
/// <see cref="LightingTransfer.IsFeature"/> false. The ARGB signal source byte on each packet is
/// the on-board MCU, so the plugin's colours show, unless the Trinity's L-Connect "sync to
/// motherboard" switch is on, when both its lights name the motherboard as the source and the
/// saved colours ride along unused, exactly as L-Connect writes them.
/// </summary>
internal static class Galahad2LightingEncoder
{
    // Command bytes and payload sizes (LConnectCore.Products.Galahad2Trinity; the Galahad2Vision
    // and HydroShiftLCD packets carry the same bytes at the same offsets).
    private const byte SetFanLightCommand = 0x85;
    private const byte SetPumpLightCommand = 0x83;
    private const int FanPayloadLength = 20;
    private const int PumpPayloadLength = 19;
    private const int MaxColors = 4;
    private const byte SourceMcu = 0;         // ARGBSignalSource.MCU: the on-board MCU drives the LEDs
    private const byte SourceMotherboard = 1; // ARGBSignalSource.Motherboard: the motherboard's ARGB header does

    // The Vision's screen ring goes out as a pump light with the scope byte at Inner (0), the only
    // value L-Connect gives it, and its saved 0 to 100 sliders divided by 25 (an unset slider,
    // saved as int.MinValue, writes 0): how setScreenLEDLighting maps a ScreenLEDLightingSetting
    // onto the PumpLightingSetting it sends.
    private const byte ScreenScopeInner = 0;
    private const int ScreenSliderStep = 25;
    private const int ScreenSliderUnset = int.MinValue;

    /// <summary>
    /// Encode a Galahad II Trinity's fan light (0x85) then its pump lights (0x83, one per saved
    /// setting in their saved order, as L-Connect's <c>setPumpLEDLighting</c> writes every entry
    /// of the array), with the signal source set to the motherboard when
    /// <paramref name="motherboardArgbSync"/>. At least one pump setting is required.
    /// </summary>
    public static IReadOnlyList<LightingTransfer> Encode(
        Galahad2FanLightingState fan, IReadOnlyList<Galahad2PumpLightingState> pumps, bool motherboardArgbSync = false)
    {
        if (fan is null)
        {
            throw new ArgumentNullException(nameof(fan));
        }

        if (pumps is null)
        {
            throw new ArgumentNullException(nameof(pumps));
        }

        if (pumps.Count == 0)
        {
            throw new ArgumentException("A Trinity look carries at least one pump light.", nameof(pumps));
        }

        byte source = motherboardArgbSync ? SourceMotherboard : SourceMcu;
        var transfers = new List<LightingTransfer>(1 + pumps.Count) { EncodeFan(fan, source) };
        foreach (Galahad2PumpLightingState pump in pumps)
        {
            transfers.Add(EncodePump(pump ?? throw new ArgumentException("A pump light is missing.", nameof(pumps)), source));
        }

        return transfers;
    }

    /// <summary>
    /// Encode a Galahad II Vision's fan light (0x85) then its screen ring (0x83), writing only the
    /// halves that were saved: either may be null, not both. A dynamic screen look is the caller's
    /// to withhold (see <see cref="Galahad2ScreenLightingState.IsDynamicMode"/>).
    /// </summary>
    public static IReadOnlyList<LightingTransfer> EncodeVision(Galahad2FanLightingState? fan, Galahad2ScreenLightingState? screen)
    {
        if (fan is null && screen is null)
        {
            throw new ArgumentException("A Vision look needs its fan light or its screen ring.", nameof(fan));
        }

        var transfers = new List<LightingTransfer>(2);
        if (fan != null)
        {
            transfers.Add(EncodeFan(fan, SourceMcu));
        }

        if (screen != null)
        {
            transfers.Add(EncodeScreen(screen));
        }

        return transfers;
    }

    /// <summary>
    /// Encode a HydroShift LCD's fan light (0x85), the one lighting packet L-Connect sends it: its
    /// radiator fans are its only LEDs.
    /// </summary>
    public static IReadOnlyList<LightingTransfer> EncodeHydroShiftLcd(Galahad2FanLightingState fan)
    {
        if (fan is null)
        {
            throw new ArgumentNullException(nameof(fan));
        }

        return new List<LightingTransfer>(1) { EncodeFan(fan, SourceMcu) };
    }

    private static LightingTransfer EncodeFan(Galahad2FanLightingState fan, byte source)
    {
        byte[] payload = new byte[FanPayloadLength];
        payload[0] = (byte)fan.Mode; // fan mode is the direct enum value (no modulo)
        payload[1] = (byte)fan.Brightness;
        payload[2] = (byte)fan.Speed;
        WriteColors(payload, 3, fan.Colors); // payload[3..14] = four R,G,B triples
        payload[15] = (byte)fan.Direction;
        payload[16] = 0;          // not disabled
        payload[17] = source;
        payload[18] = (byte)(fan.SyncToPump ? 1 : 0);
        payload[19] = (byte)fan.NumberOfLed;
        return new LightingTransfer(isFeature: false, CommandPacket.Build(SetFanLightCommand, payload));
    }

    private static LightingTransfer EncodePump(Galahad2PumpLightingState pump, byte source)
    {
        byte[] payload = new byte[PumpPayloadLength];
        payload[0] = (byte)pump.Scope;
        payload[1] = (byte)(pump.Mode % 1000); // pump mode strips the scope band the scope byte carries
        payload[2] = (byte)pump.Brightness;
        payload[3] = (byte)pump.Speed;
        WriteColors(payload, 4, pump.Colors); // payload[4..15] = four R,G,B triples
        payload[16] = (byte)pump.Direction;
        payload[17] = 0;          // not disabled
        payload[18] = source;
        return new LightingTransfer(isFeature: false, CommandPacket.Build(SetPumpLightCommand, payload));
    }

    private static LightingTransfer EncodeScreen(Galahad2ScreenLightingState screen)
    {
        byte[] payload = new byte[PumpPayloadLength];
        payload[0] = ScreenScopeInner;
        payload[1] = (byte)screen.Mode; // the ring mode is the direct enum value (no modulo)
        payload[2] = ScaleSlider(screen.Brightness);
        payload[3] = ScaleSlider(screen.Speed);
        WriteColors(payload, 4, screen.Colors); // payload[4..15] = four R,G,B triples
        payload[16] = (byte)screen.Direction;
        payload[17] = 0;          // not disabled
        payload[18] = SourceMcu;
        return new LightingTransfer(isFeature: false, CommandPacket.Build(SetPumpLightCommand, payload));
    }

    private static byte ScaleSlider(int slider) => slider == ScreenSliderUnset ? (byte)0 : (byte)(slider / ScreenSliderStep);

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
