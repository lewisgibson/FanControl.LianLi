#pragma warning disable CA1510, CA1512 // The released v1.1.34 source, kept verbatim as the oracle the current encoder is compared against (ReleasedLightingEncoderTests); it compiles here on net8.0, where the analyzer would have it use ThrowIfNull.
#if ENABLE_LIGHTING
using System.Collections.Generic;

namespace FanControl.LianLi.Protocol;

/// <summary>
/// The per-family <see cref="ReleasedUniFanLightingProfile"/> data that drives
/// <see cref="ReleasedUniFanLightingEncoder"/>. Each profile captures one Uni fan family's apply order,
/// fan-quantity report layout, mode-to-wire table, frame-latch value, and colour-expansion rule,
/// extracted from L-Connect. The default fan quantity is 3 per group for every family, matching
/// L-Connect's own controller default when no saved value is present.
/// </summary>
internal static class ReleasedUniFanLightingProfiles
{
    // Every Uni fan family has four fan-speed groups and defaults each to three fans when the
    // saved quantity is absent or out of range (L-Connect's controller default).
    private const int QuantityGroups = 4;
    private static readonly int[] DefaultQuantity = { 3, 3, 3, 3 };

    /// <summary>
    /// Uni SL (PID 0xA100) and the Redragon OEM SL variant (PID 0xA106): 4 ports applied
    /// low-to-high, fan-quantity register 0x32 with group and quantity packed into one byte,
    /// and a two-model colour expansion (full 4x16 for Breathing/StaticColor, else 4x4 fan-group).
    /// </summary>
    public static ReleasedUniFanLightingProfile Sl { get; } = new ReleasedUniFanLightingProfile(
        reverseApplyOrder: false,
        quantityGroupCount: QuantityGroups,
        quantityRegister: 0x32,
        packQuantityNibbles: true,
        maxQuantity: 4,
        defaultQuantity: DefaultQuantity,
        frameValue: 1,
        modeToWire: new Dictionary<int, byte>
        {
            { 1, 2 },    // Breathing
            { 3, 35 },   // ColorCycle
            { 12, 36 },  // Meteor
            { 13, 30 },  // Mixing
            { 15, 34 },  // Neon
            { 16, 5 },   // Rainbow
            { 17, 4 },   // RainbowMorph
            { 19, 28 },  // Runway
            { 23, 32 },  // Stack
            { 24, 33 },  // StackMulti
            { 25, 24 },  // Staggered
            { 26, 1 },   // StaticColor
            { 30, 26 },  // Tide
            { 104, 36 }, // Meteor_Merge
            { 107, 28 }, // Runway_Merge
        },
        expandColors: ExpandSl);

    /// <summary>
    /// Uni AL (PID 0xA101): 8 ports applied high-to-low, fan-quantity register 0x40 with group+1
    /// and quantity in separate bytes, and a four-model colour expansion (16 fan-group, 32 inner,
    /// 48 outer, and a 48-LED outer-corner model for the colourful outer modes).
    /// </summary>
    public static ReleasedUniFanLightingProfile Al { get; } = new ReleasedUniFanLightingProfile(
        reverseApplyOrder: true,
        quantityGroupCount: QuantityGroups,
        quantityRegister: 0x40,
        packQuantityNibbles: false,
        maxQuantity: 4,
        defaultQuantity: DefaultQuantity,
        frameValue: 1,
        modeToWire: new Dictionary<int, byte>
        {
            { 1, 2 },    // Breathing
            { 3, 43 },   // ColorCycle
            { 5, 51 },   // Contest
            { 12, 25 },  // Meteor
            { 13, 47 },  // Mixing
            { 16, 40 },  // Rainbow
            { 17, 53 },  // RainbowMorph
            { 19, 26 },  // Runway
            { 20, 50 },  // Scan
            { 21, 56 },  // SpanningTeacups
            { 23, 48 },  // Stack
            { 25, 55 },  // Staggered
            { 26, 1 },   // StaticColor
            { 27, 44 },  // Taichi
            { 29, 54 },  // Tornado
            { 30, 49 },  // Tide
            { 33, 46 },  // Voice
            { 34, 45 },  // Warning
            { 36, 2 },   // Breathing_Inner
            { 38, 24 },  // ColorCycle_Inner
            { 46, 29 },  // Lottery_Inner
            { 47, 25 },  // Meteor_Inner
            { 50, 8 },   // MeteorRainbow_Inner
            { 51, 35 },  // Mixing_Inner
            { 52, 27 },  // MopUp_Inner
            { 53, 39 },  // PacMan_Inner
            { 54, 5 },   // Rainbow_Inner
            { 58, 26 },  // Runway_Inner
            { 59, 38 },  // Scan_Inner
            { 60, 31 },  // Spring_Inner
            { 61, 36 },  // Stack_Inner
            { 62, 1 },   // StaticColor_Inner
            { 64, 32 },  // TailChasing_Inner
            { 65, 37 },  // Tide_Inner
            { 67, 34 },  // Voice_Inner
            { 68, 33 },  // Warning_Inner
            { 69, 30 },  // Wave_Inner
            { 70, 2 },   // Breathing_Outer
            { 71, 2 },   // BreathingColorful_Outer
            { 72, 6 },   // BreathingRainbow_Outer
            { 73, 24 },  // ColorCycle_Outer
            { 79, 29 },  // Lottery_Outer
            { 80, 25 },  // Meteor_Outer
            { 81, 8 },   // MeteorRainbow_Outer
            { 82, 35 },  // Mixing_Outer
            { 83, 27 },  // MopUp_Outer
            { 84, 5 },   // Rainbow_Outer
            { 88, 26 },  // Runway_Outer
            { 89, 38 },  // Scan_Outer
            { 90, 31 },  // Spring_Outer
            { 91, 36 },  // Stack_Outer
            { 92, 1 },   // StaticColor_Outer
            { 93, 1 },   // StaticColorful_Outer
            { 94, 32 },  // TailChasing_Outer
            { 95, 37 },  // Tide_Outer
            { 97, 34 },  // Voice_Outer
            { 98, 33 },  // Warning_Outer
            { 99, 30 },  // Wave_Outer
            { 100, 51 }, // Contest_Merge
            { 108, 50 }, // Scan_Merge
        },
        expandColors: ExpandAl);

    /// <summary>
    /// Uni SL v2 (PIDs 0xA103 and 0xA105): 4 ports applied low-to-high, fan-quantity register 0x60
    /// with group and quantity packed into one byte, a frame latch of 4 (not 1), and a two-model
    /// six-fan colour expansion (96-LED per-fan for Breathing/StaticColor, else 24-slot fan-group).
    /// </summary>
    public static ReleasedUniFanLightingProfile SlV2 { get; } = new ReleasedUniFanLightingProfile(
        reverseApplyOrder: false,
        quantityGroupCount: QuantityGroups,
        quantityRegister: 0x60,
        packQuantityNibbles: true,
        maxQuantity: 6,
        defaultQuantity: DefaultQuantity,
        frameValue: 4,
        modeToWire: new Dictionary<int, byte>
        {
            { 1, 2 },    // Breathing
            { 3, 35 },   // ColorCycle
            { 9, 39 },   // Groove
            { 12, 36 },  // Meteor
            { 13, 30 },  // Mixing
            { 15, 34 },  // Neon
            { 16, 5 },   // Rainbow
            { 17, 4 },   // RainbowMorph
            { 18, 40 },  // Render
            { 19, 28 },  // Runway
            { 23, 32 },  // Stack
            { 24, 33 },  // StackMulti
            { 25, 24 },  // Staggered
            { 26, 1 },   // StaticColor
            { 30, 26 },  // Tide
            { 31, 41 },  // Tunnel
            { 33, 38 },  // Voice
            { 104, 42 }, // Meteor_Merge
            { 105, 45 }, // Mixing_Merge
            { 107, 43 }, // Runway_Merge
            { 111, 46 }, // StackMulti_Merge
            { 113, 44 }, // Tide_Merge
        },
        expandColors: ExpandSlV2);

    /// <summary>
    /// Uni AL v2 (PID 0xA104): 8 ports applied high-to-low, fan-quantity register 0x60 with group+1
    /// and quantity in separate bytes, and a six-fan colour expansion with five variants (36
    /// fan-group, a cycle-fill fan-group for the Meteor modes, 48 inner, 72 outer, 72 outer-corner).
    /// </summary>
    public static ReleasedUniFanLightingProfile AlV2 { get; } = new ReleasedUniFanLightingProfile(
        reverseApplyOrder: true,
        quantityGroupCount: QuantityGroups,
        quantityRegister: 0x60,
        packQuantityNibbles: false,
        maxQuantity: 6,
        defaultQuantity: DefaultQuantity,
        frameValue: 1,
        modeToWire: new Dictionary<int, byte>
        {
            { 1, 2 },    // Breathing
            { 3, 46 },   // ColorCycle
            { 4, 56 },   // ColorfulCity
            { 5, 53 },   // Contest
            { 8, 66 },   // ElectricCurrent
            { 12, 25 },  // Meteor
            { 13, 50 },  // Mixing
            { 14, 62 },  // MopUp
            { 16, 43 },  // Rainbow
            { 17, 4 },   // RainbowMorph
            { 18, 57 },  // Render
            { 19, 26 },  // Runway
            { 20, 52 },  // Scan
            { 21, 65 },  // SpanningTeacups
            { 22, 60 },  // Spring
            { 23, 67 },  // Stack
            { 25, 64 },  // Staggered
            { 26, 1 },   // StaticColor
            { 27, 47 },  // Taichi
            { 28, 61 },  // TailChasing
            { 29, 63 },  // Tornado
            { 30, 51 },  // Tide
            { 32, 58 },  // Twinkle
            { 33, 49 },  // Voice
            { 34, 48 },  // Warning
            { 35, 59 },  // Wave
            { 36, 2 },   // Breathing_Inner
            { 38, 24 },  // ColorCycle_Inner
            { 39, 40 },  // ColorfulCity_Inner
            { 46, 29 },  // Lottery_Inner
            { 47, 25 },  // Meteor_Inner
            { 50, 8 },   // MeteorRainbow_Inner
            { 51, 35 },  // Mixing_Inner
            { 52, 27 },  // MopUp_Inner
            { 53, 39 },  // PacMan_Inner
            { 54, 5 },   // Rainbow_Inner
            { 55, 4 },   // RainbowMorph_Inner
            { 56, 41 },  // Render_Inner
            { 58, 26 },  // Runway_Inner
            { 59, 38 },  // Scan_Inner
            { 60, 31 },  // Spring_Inner
            { 61, 36 },  // Stack_Inner
            { 62, 1 },   // StaticColor_Inner
            { 64, 32 },  // TailChasing_Inner
            { 65, 37 },  // Tide_Inner
            { 66, 42 },  // Twinkle_Inner
            { 67, 34 },  // Voice_Inner
            { 68, 33 },  // Warning_Inner
            { 69, 30 },  // Wave_Inner
            { 70, 2 },   // Breathing_Outer
            { 71, 2 },   // BreathingColorful_Outer
            { 72, 6 },   // BreathingRainbow_Outer
            { 73, 28 },  // ColorCycle_Outer
            { 74, 40 },  // ColorfulCity_Outer
            { 79, 29 },  // Lottery_Outer
            { 80, 25 },  // Meteor_Outer
            { 81, 8 },   // MeteorRainbow_Outer
            { 82, 35 },  // Mixing_Outer
            { 83, 27 },  // MopUp_Outer
            { 84, 5 },   // Rainbow_Outer
            { 85, 4 },   // RainbowMorph_Outer
            // Reflect_Outer (86) is absent from L-Connect's lookup, so that port is skipped.
            { 87, 41 },  // Render_Outer
            { 88, 26 },  // Runway_Outer
            { 89, 38 },  // Scan_Outer
            { 90, 31 },  // Spring_Outer
            { 91, 36 },  // Stack_Outer
            { 92, 1 },   // StaticColor_Outer
            { 93, 1 },   // StaticColorful_Outer
            { 94, 32 },  // TailChasing_Outer
            { 95, 37 },  // Tide_Outer
            { 96, 42 },  // Twinkle_Outer
            { 97, 34 },  // Voice_Outer
            { 98, 33 },  // Warning_Outer
            { 99, 30 },  // Wave_Outer
            { 100, 69 }, // Contest_Merge
            { 102, 79 }, // ElectricCurrent_Merge
            { 105, 71 }, // Mixing_Merge
            { 106, 76 }, // MopUp_Merge
            { 107, 70 }, // Runway_Merge
            { 108, 68 }, // Scan_Merge
            { 109, 75 }, // Spring_Merge
            { 112, 74 }, // TailChasing_Merge
            { 113, 72 }, // Tide_Merge
            { 114, 73 }, // Wave_Merge
        },
        expandColors: ExpandAlV2);

    // SL: Breathing (1) and StaticColor (26) fill 4 fans x 16 LEDs, one saved colour per fan;
    // every other mode uses the 16-slot fan-group palette (4 fans x 4 slots).
    private static RgbColor[] ExpandSl(int mode, IReadOnlyList<RgbColor> colors)
    {
        if (mode == 1 || mode == 26)
        {
            return ReleasedUniFanLightingEncoder.ExpandPerFan(fanCount: 4, ledsPerFan: 16, colors);
        }

        return ReleasedUniFanLightingEncoder.ExpandFanGroup(fanCount: 4, slots: 4, colors, cycleFill: false);
    }

    // AL: inner (32) and outer (48) rings fill one colour per fan; the colourful outer modes use
    // the outer-corner model (4 corners x 3 LEDs per fan); every other mode uses the 16-slot
    // fan-group palette.
    private static RgbColor[] ExpandAl(int mode, IReadOnlyList<RgbColor> colors)
    {
        switch (mode)
        {
            case 36: // Breathing_Inner
            case 62: // StaticColor_Inner
                return ReleasedUniFanLightingEncoder.ExpandPerFan(fanCount: 4, ledsPerFan: 8, colors);
            case 70: // Breathing_Outer
            case 92: // StaticColor_Outer
                return ReleasedUniFanLightingEncoder.ExpandPerFan(fanCount: 4, ledsPerFan: 12, colors);
            case 71: // BreathingColorful_Outer
            case 93: // StaticColorful_Outer
                return ReleasedUniFanLightingEncoder.ExpandOuterCorner(fanCount: 4, colors);
            default:
                return ReleasedUniFanLightingEncoder.ExpandFanGroup(fanCount: 4, slots: 4, colors, cycleFill: false);
        }
    }

    // SL v2: Breathing (1) and StaticColor (26) fill 6 fans x 16 LEDs, one saved colour per fan;
    // every other mode uses the 24-slot fan-group palette (6 fans x 4 slots).
    private static RgbColor[] ExpandSlV2(int mode, IReadOnlyList<RgbColor> colors)
    {
        if (mode == 1 || mode == 26)
        {
            return ReleasedUniFanLightingEncoder.ExpandPerFan(fanCount: 6, ledsPerFan: 16, colors);
        }

        return ReleasedUniFanLightingEncoder.ExpandFanGroup(fanCount: 6, slots: 4, colors, cycleFill: false);
    }

    // AL v2: the Meteor modes cycle-fill the 6 fans x 6 fan-group palette; inner (48) and outer
    // (72) rings fill one colour per fan; the colourful outer modes use the outer-corner model;
    // every other mode uses the 36-slot fan-group palette with black past the supplied colours.
    private static RgbColor[] ExpandAlV2(int mode, IReadOnlyList<RgbColor> colors)
    {
        switch (mode)
        {
            case 12: // Meteor
            case 47: // Meteor_Inner
            case 80: // Meteor_Outer
                return ReleasedUniFanLightingEncoder.ExpandFanGroup(fanCount: 6, slots: 6, colors, cycleFill: true);
            case 36: // Breathing_Inner
            case 62: // StaticColor_Inner
                return ReleasedUniFanLightingEncoder.ExpandPerFan(fanCount: 6, ledsPerFan: 8, colors);
            case 70: // Breathing_Outer
            case 92: // StaticColor_Outer
                return ReleasedUniFanLightingEncoder.ExpandPerFan(fanCount: 6, ledsPerFan: 12, colors);
            case 71: // BreathingColorful_Outer
            case 93: // StaticColorful_Outer
                return ReleasedUniFanLightingEncoder.ExpandOuterCorner(fanCount: 6, colors);
            default:
                return ReleasedUniFanLightingEncoder.ExpandFanGroup(fanCount: 6, slots: 6, colors, cycleFill: false);
        }
    }
}
#endif

#pragma warning restore CA1510, CA1512
