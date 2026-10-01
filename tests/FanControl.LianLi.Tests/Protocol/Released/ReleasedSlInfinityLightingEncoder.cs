#pragma warning disable CA1510, CA1512 // The released v1.1.34 source, kept verbatim as the oracle the current encoder is compared against (ReleasedLightingEncoderTests); it compiles here on net8.0, where the analyzer would have it use ThrowIfNull.
#if ENABLE_LIGHTING
using System;
using System.Collections.Generic;
using System.Linq;

namespace FanControl.LianLi.Protocol;

/// <summary>
/// Pure encoder that turns an SL-Infinity (PID 0xA102) controller's saved L-Connect look
/// into the exact ordered HID transfers L-Connect itself sends to reproduce it. No I/O and
/// no state: same input always yields the same bytes, which is what makes the byte math
/// testable in isolation.
/// </summary>
/// <remarks>
/// The apply sequence is: set the per-group fan quantity (groups 0-3), then for each present
/// port in reverse order (7-0) write the colour output report followed by the effect feature
/// report, then latch the frame. The saved mode integer, colours, and brightness are each
/// translated to the controller's wire encoding before they are written.
/// </remarks>
internal static class ReleasedSlInfinityLightingEncoder
{
    // Every Uni report starts with report id 0xE0 (224). Feature reports are a fixed 7 bytes
    // and the colour output report a fixed 353 bytes (header + per-LED data, zero-padded).
    private const byte ReportId = 0xE0;
    private const int ColorReportLength = 353;

    // L-Connect's brightness enum has both a Lowest (4) and an Off step, and its own normalize step
    // folds Lowest into Off - choosing the lowest brightness in L-Connect turns the LEDs off. We
    // reproduce that: a saved Lowest brightness is sent as Off on the wire. For the Uni fan family
    // (Ene6K77Fan) Off is 8, NOT the Strimer Plus value 255 - the two families use distinct enums.
    private const int BrightnessLowest = 4;
    private const byte BrightnessOff = 8;

    // SetQuantity defaults to a full four fans per group when the saved value is absent or
    // out of range; the four groups are validated together (each 0-4), matching L-Connect.
    private static readonly int[] DefaultQuantity = { 4, 4, 4, 4 };

    // Modes whose colours expand to the full inner (4 fans x 8) or outer (4 fans x 12) ring
    // buffer. Every other mode - including the combined-mode halves L-Connect persists as
    // _Inner on even ports and _Outer on odd ports - uses the 16-slot fan-group palette.
    private static readonly HashSet<int> InnerExpansionModes = new HashSet<int> { 36, 62 }; // Breathing_Inner, StaticColor_Inner
    private static readonly HashSet<int> OuterExpansionModes = new HashSet<int> { 70, 92 }; // Breathing_Outer, StaticColor_Outer

    // Saved lighting-mode value -> on-wire effect byte for SL-Infinity. A mode absent here is
    // one the controller does not apply, so that port is left untouched (see AppendPort).
    private static readonly Dictionary<int, byte> ModeToWire = new Dictionary<int, byte>
    {
        { 1, 2 },     // Breathing
        { 2, 6 },     // BreathingRainbow
        { 6, 35 },    // Disco
        { 7, 60 },    // Door
        { 8, 65 },    // ElectricCurrent
        { 10, 66 },   // HeartBeat
        { 11, 67 },   // HeartBeatRunway
        { 12, 25 },   // Meteor
        { 13, 56 },   // Mixing
        { 14, 68 },   // MopUp
        { 16, 50 },   // Rainbow
        { 17, 63 },   // RainbowMorph
        { 19, 26 },   // Runway
        { 20, 59 },   // Scan
        { 23, 57 },   // Stack
        { 26, 1 },    // StaticColor
        { 30, 58 },   // Tide
        { 33, 55 },   // Voice
        { 34, 54 },   // Warning
        { 36, 2 },    // Breathing_Inner
        { 37, 6 },    // BreathingRainbow_Inner
        { 38, 24 },   // ColorCycle_Inner
        { 40, 35 },   // Disco_Inner
        { 41, 34 },   // Door_Inner
        { 42, 33 },   // DoubleArc_Inner
        { 43, 29 },   // DoubleMeteor_Inner
        { 44, 36 },   // HeartBeat_Inner
        { 45, 69 },   // HeartBeatRunway_Inner
        { 46, 38 },   // Lottery_Inner
        { 47, 25 },   // Meteor_Inner
        { 48, 30 },   // MeteorContest_Inner
        { 49, 31 },   // MeteorMix_Inner
        { 50, 8 },    // MeteorRainbow_Inner
        { 51, 43 },   // Mixing_Inner
        { 52, 27 },   // MopUp_Inner
        { 54, 5 },    // Rainbow_Inner
        { 55, 4 },    // RainbowMorph_Inner
        { 57, 32 },   // ReturnArc_Inner
        { 58, 26 },   // Runway_Inner
        { 59, 46 },   // Scan_Inner
        { 61, 44 },   // Stack_Inner
        { 62, 1 },    // StaticColor_Inner
        { 63, 28 },   // Taichi_Inner
        { 65, 45 },   // Tide_Inner
        { 67, 42 },   // Voice_Inner
        { 68, 41 },   // Warning_Inner
        { 70, 2 },    // Breathing_Outer
        { 72, 6 },    // BreathingRainbow_Outer
        { 73, 24 },   // ColorCycle_Outer
        { 75, 39 },   // ColorfulMeteor_Outer
        { 76, 35 },   // Disco_Outer
        { 77, 34 },   // Door_Outer
        { 78, 36 },   // HeartBeat_Outer
        { 79, 38 },   // Lottery_Outer
        { 80, 25 },   // Meteor_Outer
        { 81, 8 },    // MeteorRainbow_Outer
        { 82, 43 },   // Mixing_Outer
        { 83, 27 },   // MopUp_Outer
        { 84, 5 },    // Rainbow_Outer
        { 85, 4 },    // RainbowMorph_Outer
        { 86, 48 },   // Reflect_Outer
        { 88, 26 },   // Runway_Outer
        { 89, 46 },   // Scan_Outer
        { 91, 44 },   // Stack_Outer
        { 92, 1 },    // StaticColor_Outer
        { 95, 45 },   // Tide_Outer
        { 97, 42 },   // Voice_Outer
        { 98, 41 },   // Warning_Outer
        { 101, 76 },  // Door_Merge
        { 102, 78 },  // ElectricCurrent_Merge
        { 103, 77 },  // HeartBeatRunway_Merge
        { 105, 72 },  // Mixing_Merge
        { 106, 71 },  // MopUp_Merge
        { 107, 70 },  // Runway_Merge
        { 108, 75 },  // Scan_Merge
        { 110, 73 },  // Stack_Merge
        { 113, 74 },  // Tide_Merge
    };

    /// <summary>
    /// Encode the full apply sequence for one controller: fan-quantity for groups 0-3, each
    /// present port (in reverse order) as a colour output report and an effect feature
    /// report, then the frame latch. Ports whose mode L-Connect does not recognise are
    /// skipped, as L-Connect skips them.
    /// </summary>
    public static IReadOnlyList<LightingTransfer> Encode(IReadOnlyList<LightingPortState> ports, IReadOnlyList<int>? quantity)
    {
        if (ports is null)
        {
            throw new ArgumentNullException(nameof(ports));
        }

        var transfers = new List<LightingTransfer>();

        IReadOnlyList<int> fanQuantity = NormalizeQuantity(quantity);
        for (int group = 0; group < 4; group++)
        {
            transfers.Add(Feature(EncodeQuantity(group, fanQuantity[group])));
        }

        foreach (LightingPortState port in ports.OrderByDescending(p => p.Port))
        {
            AppendPort(transfers, port);
        }

        transfers.Add(Feature(EncodeFrame()));
        return transfers;
    }

    private static void AppendPort(List<LightingTransfer> transfers, LightingPortState port)
    {
        // Only a port whose saved mode is in the lookup is applied; an unrecognised mode leaves
        // that port untouched while the others still apply.
        if (!ModeToWire.TryGetValue(port.Mode, out byte wireMode))
        {
            return;
        }

        RgbColor[] leds = ExpandColors(port.Mode, port.Colors);
        transfers.Add(Output(EncodeColorSetting(port.Port, leds)));
        transfers.Add(Feature(EncodeEffectSetting(
            port.Port, wireMode, port.Speed, port.Direction, NormalizeBrightness(port.Brightness))));
    }

    private static RgbColor[] ExpandColors(int mode, IReadOnlyList<RgbColor> colors)
    {
        // The dedicated inner/outer static+breathing modes fill the whole ring (one colour per
        // fan); every other mode uses the 16-slot fan-group palette.
        if (InnerExpansionModes.Contains(mode))
        {
            return ExpandPerFan(colors, ledsPerFan: 8);   // inner ring: 4 fans x 8 = 32
        }

        if (OuterExpansionModes.Contains(mode))
        {
            return ExpandPerFan(colors, ledsPerFan: 12);  // outer ring: 4 fans x 12 = 48
        }

        return ExpandFanGroup(colors);                     // fan-group palette: 4 fans x 4 = 16
    }

    // Fan i takes colours[i] across all its LEDs; fans past the supplied colours go black.
    private static RgbColor[] ExpandPerFan(IReadOnlyList<RgbColor> colors, int ledsPerFan)
    {
        var leds = new RgbColor[4 * ledsPerFan];
        for (int fan = 0; fan < 4; fan++)
        {
            RgbColor color = fan < colors.Count ? colors[fan] : default;
            for (int led = 0; led < ledsPerFan; led++)
            {
                leds[(fan * ledsPerFan) + led] = color;
            }
        }

        return leds;
    }

    // Every fan shows the same 4-slot palette; slot j takes colours[j], black past the end.
    private static RgbColor[] ExpandFanGroup(IReadOnlyList<RgbColor> colors)
    {
        var leds = new RgbColor[16];
        for (int fan = 0; fan < 4; fan++)
        {
            for (int slot = 0; slot < 4; slot++)
            {
                leds[(fan * 4) + slot] = slot < colors.Count ? colors[slot] : default;
            }
        }

        return leds;
    }

    private static byte NormalizeBrightness(int brightness)
    {
        return brightness == BrightnessLowest ? BrightnessOff : (byte)brightness;
    }

    private static IReadOnlyList<int> NormalizeQuantity(IReadOnlyList<int>? quantity)
    {
        if (quantity is null || quantity.Count != 4)
        {
            return DefaultQuantity;
        }

        foreach (int value in quantity)
        {
            if (value < 0 || value > 4)
            {
                return DefaultQuantity;
            }
        }

        return quantity;
    }

    // Colour output report: {E0, 0x30|port} then each LED as R, B, G (the wire order swaps green
    // and blue). Built by index because the LED count varies; the rest of the 353 bytes stays zero.
    private static byte[] EncodeColorSetting(int port, RgbColor[] leds)
    {
        var report = new byte[ColorReportLength];
        report[0] = ReportId;
        report[1] = (byte)(0x30 | (port & 0x0F));
        int offset = 2;
        foreach (RgbColor led in leds)
        {
            report[offset] = led.R;
            report[offset + 1] = led.B;
            report[offset + 2] = led.G;
            offset += 3;
        }

        return report;
    }

    // Effect feature report: {E0, 0x10|port, wireMode, speed, direction, brightness} (7 bytes).
    private static byte[] EncodeEffectSetting(int port, byte wireMode, int speed, int direction, byte brightness) =>
        new byte[] { ReportId, (byte)(0x10 | (port & 0x0F)), wireMode, (byte)speed, (byte)direction, brightness, 0 };

    // Fan-quantity feature report: {E0, 16, 96, group+1, quantity, 0} (7 bytes).
    private static byte[] EncodeQuantity(int group, int quantity) =>
        new byte[] { ReportId, 16, 96, (byte)(group + 1), (byte)quantity, 0, 0 };

    // Frame-latch feature report: {E0, 96, 0, 1} latches the assembled frame to display it (7 bytes).
    private static byte[] EncodeFrame() => new byte[] { ReportId, 96, 0, 1, 0, 0, 0 };

    private static LightingTransfer Feature(byte[] report) => new LightingTransfer(true, report);

    private static LightingTransfer Output(byte[] report) => new LightingTransfer(false, report);
}
#endif

#pragma warning restore CA1510, CA1512
