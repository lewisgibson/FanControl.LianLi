#if ENABLE_LIGHTING
using System.Collections.Generic;
using FanControl.LianLi.Protocol;
using Xunit;

namespace FanControl.LianLi.Tests.Protocol;

/// <summary>
/// Byte-level tests for the parameterized Uni fan lighting encoder, one section per family (SL,
/// AL, SL v2, AL v2). Each asserts the exact wire output: the fan-quantity reports (family
/// register and packing), a colour output report for a per-fan/full-expansion mode and a
/// fan-group mode, the effect report (a looked-up wire byte and the Lowest->Off brightness fold),
/// the apply order, and that an unrecognised mode leaves its port untouched.
/// </summary>
public sealed class UniFanLightingEncoderTests
{
    // ---- SL (0xA100 / Redragon 0xA106): 4 ports, forward order, register 0x32 packed, frame 1 ----

    [Fact]
    public void Sl_StaticColor_EmitsPackedQuantityFullExpansionEffectThenFrame()
    {
        // StaticColor (mode 26 -> wire 1) fills 4 fans x 16 LEDs, one saved colour per fan.
        var ports = new[]
        {
            Port(port: 0, mode: 26, speed: 0, direction: 0, brightness: 0, Rgb(255, 0, 0)),
        };

        IReadOnlyList<LightingTransfer> transfers = UniFanLightingEncoder.Encode(UniFanLightingProfiles.Sl, ports, new[] { 1, 2, 3, 4 });

        Assert.Equal(7, transfers.Count); // 4x SetQuantity + colour + effect + SetFrame

        // Register 0x32, group (high nibble) + quantity (low nibble) packed into byte[3].
        AssertTransfer(transfers[0], feature: true, Feature(0xE0, 0x10, 0x32, 0x01));
        AssertTransfer(transfers[1], feature: true, Feature(0xE0, 0x10, 0x32, 0x12));
        AssertTransfer(transfers[2], feature: true, Feature(0xE0, 0x10, 0x32, 0x23));
        AssertTransfer(transfers[3], feature: true, Feature(0xE0, 0x10, 0x32, 0x34));

        AssertTransfer(transfers[4], feature: false, ColorReport(0, PerFanLeds(fanCount: 4, ledsPerFan: 16, Rgb(255, 0, 0))));
        AssertTransfer(transfers[5], feature: true, Feature(0xE0, 0x10, 1, 0, 0, 0));
        AssertTransfer(transfers[6], feature: true, Feature(0xE0, 0x60, 0, 1)); // SetFrame(1)
    }

    [Fact]
    public void Sl_FanGroupMode_AppliesPortsLowToHigh()
    {
        // ColorCycle (mode 3 -> wire 35) uses the 16-slot fan-group palette (4 fans x 4 slots).
        var low = Port(port: 0, mode: 3, speed: 1, direction: 0, brightness: 0, Rgb(0, 215, 255), Rgb(0, 8, 255));
        var high = Port(port: 1, mode: 3, speed: 255, direction: 1, brightness: 0, Rgb(0, 215, 255), Rgb(0, 8, 255));

        IReadOnlyList<LightingTransfer> transfers = UniFanLightingEncoder.Encode(UniFanLightingProfiles.Sl, new[] { high, low }, new[] { 4, 4, 4, 4 });

        // Forward order: port 0 before port 1.
        AssertTransfer(transfers[4], feature: false, ColorReport(0, FanGroupLeds(fanCount: 4, slots: 4, cycleFill: false, Rgb(0, 215, 255), Rgb(0, 8, 255))));
        AssertTransfer(transfers[5], feature: true, Feature(0xE0, 0x10, 35, 1, 0, 0));
        AssertTransfer(transfers[6], feature: false, ColorReport(1, FanGroupLeds(fanCount: 4, slots: 4, cycleFill: false, Rgb(0, 215, 255), Rgb(0, 8, 255))));
        AssertTransfer(transfers[7], feature: true, Feature(0xE0, 0x11, 35, 255, 1, 0));
        AssertTransfer(transfers[8], feature: true, Feature(0xE0, 0x60, 0, 1));
    }

    [Fact]
    public void Sl_LowestBrightness_IsSentAsOff()
    {
        var ports = new[] { Port(port: 0, mode: 26, speed: 0, direction: 0, brightness: 4, Rgb(1, 1, 1)) };

        IReadOnlyList<LightingTransfer> transfers = UniFanLightingEncoder.Encode(UniFanLightingProfiles.Sl, ports, new[] { 4, 4, 4, 4 });

        AssertTransfer(transfers[5], feature: true, Feature(0xE0, 0x10, 1, 0, 0, 8));
    }

    [Fact]
    public void Sl_UnknownMode_SkipsPortButKeepsQuantityAndFrame()
    {
        var ports = new[] { Port(port: 0, mode: 9999, speed: 0, direction: 0, brightness: 0, Rgb(1, 1, 1)) };

        IReadOnlyList<LightingTransfer> transfers = UniFanLightingEncoder.Encode(UniFanLightingProfiles.Sl, ports, new[] { 4, 4, 4, 4 });

        Assert.Equal(5, transfers.Count); // only 4x SetQuantity + SetFrame
        AssertTransfer(transfers[4], feature: true, Feature(0xE0, 0x60, 0, 1));
    }

    [Fact]
    public void Sl_NullQuantity_DefaultsToThreePerGroup()
    {
        IReadOnlyList<LightingTransfer> transfers = UniFanLightingEncoder.Encode(
            UniFanLightingProfiles.Sl,
            new[] { Port(port: 0, mode: 26, speed: 0, direction: 0, brightness: 0, Rgb(1, 0, 0)) },
            quantity: null);

        AssertTransfer(transfers[0], feature: true, Feature(0xE0, 0x10, 0x32, 0x03));
        AssertTransfer(transfers[3], feature: true, Feature(0xE0, 0x10, 0x32, 0x33));
    }

    // ---- AL (0xA101): 8 ports, reverse order, register 0x40 separate bytes, frame 1 ----

    [Fact]
    public void Al_InnerMode_EmitsSeparateQuantityPerFanExpansionEffectThenFrame()
    {
        // Breathing_Inner (mode 36 -> wire 2) fills 4 fans x 8 LEDs, one saved colour per fan.
        var ports = new[]
        {
            Port(port: 0, mode: 36, speed: 0, direction: 0, brightness: 0, Rgb(10, 20, 30), Rgb(40, 50, 60), Rgb(70, 80, 90), Rgb(100, 110, 120)),
        };

        IReadOnlyList<LightingTransfer> transfers = UniFanLightingEncoder.Encode(UniFanLightingProfiles.Al, ports, new[] { 1, 2, 3, 4 });

        Assert.Equal(7, transfers.Count);

        // Register 0x40, group+1 (1-based) in byte[3], quantity in byte[4].
        AssertTransfer(transfers[0], feature: true, Feature(0xE0, 0x10, 0x40, 1, 1, 0));
        AssertTransfer(transfers[1], feature: true, Feature(0xE0, 0x10, 0x40, 2, 2, 0));
        AssertTransfer(transfers[2], feature: true, Feature(0xE0, 0x10, 0x40, 3, 3, 0));
        AssertTransfer(transfers[3], feature: true, Feature(0xE0, 0x10, 0x40, 4, 4, 0));

        AssertTransfer(transfers[4], feature: false, ColorReport(0, PerFanLeds(fanCount: 4, ledsPerFan: 8, Rgb(10, 20, 30), Rgb(40, 50, 60), Rgb(70, 80, 90), Rgb(100, 110, 120))));
        AssertTransfer(transfers[5], feature: true, Feature(0xE0, 0x10, 2, 0, 0, 0));
        AssertTransfer(transfers[6], feature: true, Feature(0xE0, 0x60, 0, 1));
    }

    [Fact]
    public void Al_OuterCornerMode_ExpandsFourCornersOfThreeLeds()
    {
        // BreathingColorful_Outer (mode 71 -> wire 2) fills 4 fans x 12 LEDs as 4 corners x 3.
        var ports = new[]
        {
            Port(port: 0, mode: 71, speed: 0, direction: 0, brightness: 0, Rgb(1, 2, 3), Rgb(4, 5, 6), Rgb(7, 8, 9), Rgb(10, 11, 12)),
        };

        IReadOnlyList<LightingTransfer> transfers = UniFanLightingEncoder.Encode(UniFanLightingProfiles.Al, ports, new[] { 4, 4, 4, 4 });

        AssertTransfer(transfers[4], feature: false, ColorReport(0, OuterCornerLeds(fanCount: 4, Rgb(1, 2, 3), Rgb(4, 5, 6), Rgb(7, 8, 9), Rgb(10, 11, 12))));
        AssertTransfer(transfers[5], feature: true, Feature(0xE0, 0x10, 2, 0, 0, 0));
    }

    [Fact]
    public void Al_FanGroupMode_AppliesPortsHighToLow()
    {
        // Contest (mode 5 -> wire 51) uses the 16-slot fan-group palette; AL applies ports 7->0.
        var low = Port(port: 0, mode: 5, speed: 0, direction: 0, brightness: 0, Rgb(9, 9, 9));
        var high = Port(port: 3, mode: 5, speed: 0, direction: 0, brightness: 0, Rgb(9, 9, 9));

        IReadOnlyList<LightingTransfer> transfers = UniFanLightingEncoder.Encode(UniFanLightingProfiles.Al, new[] { low, high }, new[] { 4, 4, 4, 4 });

        // Reverse order: port 3 before port 0.
        AssertTransfer(transfers[4], feature: false, ColorReport(3, FanGroupLeds(fanCount: 4, slots: 4, cycleFill: false, Rgb(9, 9, 9))));
        AssertTransfer(transfers[5], feature: true, Feature(0xE0, 0x13, 51, 0, 0, 0));
        AssertTransfer(transfers[6], feature: false, ColorReport(0, FanGroupLeds(fanCount: 4, slots: 4, cycleFill: false, Rgb(9, 9, 9))));
        AssertTransfer(transfers[7], feature: true, Feature(0xE0, 0x10, 51, 0, 0, 0));
    }

    [Fact]
    public void Al_LowestBrightness_IsSentAsOff()
    {
        var ports = new[] { Port(port: 0, mode: 26, speed: 0, direction: 0, brightness: 4, Rgb(1, 1, 1)) };

        IReadOnlyList<LightingTransfer> transfers = UniFanLightingEncoder.Encode(UniFanLightingProfiles.Al, ports, new[] { 4, 4, 4, 4 });

        AssertTransfer(transfers[5], feature: true, Feature(0xE0, 0x10, 1, 0, 0, 8));
    }

    [Fact]
    public void Al_UnknownMode_SkipsPortButKeepsQuantityAndFrame()
    {
        var ports = new[] { Port(port: 0, mode: 9999, speed: 0, direction: 0, brightness: 0, Rgb(1, 1, 1)) };

        IReadOnlyList<LightingTransfer> transfers = UniFanLightingEncoder.Encode(UniFanLightingProfiles.Al, ports, new[] { 4, 4, 4, 4 });

        Assert.Equal(5, transfers.Count);
        AssertTransfer(transfers[4], feature: true, Feature(0xE0, 0x60, 0, 1));
    }

    // ---- SL v2 (0xA103 / 0xA105): 4 ports, forward order, register 0x60 packed, frame 4 ----

    [Fact]
    public void SlV2_StaticColor_EmitsPackedQuantityFullExpansionEffectThenFrameFour()
    {
        // StaticColor (mode 26 -> wire 1) fills 6 fans x 16 LEDs, one saved colour per fan.
        var ports = new[]
        {
            Port(port: 0, mode: 26, speed: 0, direction: 0, brightness: 0, Rgb(255, 0, 0), Rgb(0, 255, 0)),
        };

        IReadOnlyList<LightingTransfer> transfers = UniFanLightingEncoder.Encode(UniFanLightingProfiles.SlV2, ports, new[] { 1, 2, 5, 6 });

        Assert.Equal(7, transfers.Count);

        // Register 0x60, group + quantity packed; SL v2 accepts quantity up to 6.
        AssertTransfer(transfers[0], feature: true, Feature(0xE0, 0x10, 0x60, 0x01));
        AssertTransfer(transfers[1], feature: true, Feature(0xE0, 0x10, 0x60, 0x12));
        AssertTransfer(transfers[2], feature: true, Feature(0xE0, 0x10, 0x60, 0x25));
        AssertTransfer(transfers[3], feature: true, Feature(0xE0, 0x10, 0x60, 0x36));

        AssertTransfer(transfers[4], feature: false, ColorReport(0, PerFanLeds(fanCount: 6, ledsPerFan: 16, Rgb(255, 0, 0), Rgb(0, 255, 0))));
        AssertTransfer(transfers[5], feature: true, Feature(0xE0, 0x10, 1, 0, 0, 0));
        AssertTransfer(transfers[6], feature: true, Feature(0xE0, 0x60, 0, 4)); // SetFrame(4), not 1
    }

    [Fact]
    public void SlV2_FanGroupMode_AppliesPortsLowToHigh()
    {
        // ColorCycle (mode 3 -> wire 35) uses the 24-slot fan-group palette (6 fans x 4 slots).
        var low = Port(port: 0, mode: 3, speed: 0, direction: 0, brightness: 0, Rgb(1, 2, 3), Rgb(4, 5, 6));
        var high = Port(port: 2, mode: 3, speed: 0, direction: 0, brightness: 0, Rgb(1, 2, 3), Rgb(4, 5, 6));

        IReadOnlyList<LightingTransfer> transfers = UniFanLightingEncoder.Encode(UniFanLightingProfiles.SlV2, new[] { high, low }, new[] { 3, 3, 3, 3 });

        AssertTransfer(transfers[4], feature: false, ColorReport(0, FanGroupLeds(fanCount: 6, slots: 4, cycleFill: false, Rgb(1, 2, 3), Rgb(4, 5, 6))));
        AssertTransfer(transfers[5], feature: true, Feature(0xE0, 0x10, 35, 0, 0, 0));
        AssertTransfer(transfers[6], feature: false, ColorReport(2, FanGroupLeds(fanCount: 6, slots: 4, cycleFill: false, Rgb(1, 2, 3), Rgb(4, 5, 6))));
        AssertTransfer(transfers[7], feature: true, Feature(0xE0, 0x12, 35, 0, 0, 0));
        AssertTransfer(transfers[8], feature: true, Feature(0xE0, 0x60, 0, 4));
    }

    [Fact]
    public void SlV2_LowestBrightness_IsSentAsOff()
    {
        var ports = new[] { Port(port: 0, mode: 26, speed: 0, direction: 0, brightness: 4, Rgb(1, 1, 1)) };

        IReadOnlyList<LightingTransfer> transfers = UniFanLightingEncoder.Encode(UniFanLightingProfiles.SlV2, ports, new[] { 3, 3, 3, 3 });

        AssertTransfer(transfers[5], feature: true, Feature(0xE0, 0x10, 1, 0, 0, 8));
    }

    [Fact]
    public void SlV2_QuantityOutOfSixRange_FallsBackToDefaultThree()
    {
        // 7 exceeds SL v2's max of 6, so the whole quantity falls back to the default {3,3,3,3}.
        IReadOnlyList<LightingTransfer> transfers = UniFanLightingEncoder.Encode(
            UniFanLightingProfiles.SlV2,
            new[] { Port(port: 0, mode: 26, speed: 0, direction: 0, brightness: 0, Rgb(1, 0, 0)) },
            new[] { 7, 6, 6, 6 });

        AssertTransfer(transfers[0], feature: true, Feature(0xE0, 0x10, 0x60, 0x03));
        AssertTransfer(transfers[3], feature: true, Feature(0xE0, 0x10, 0x60, 0x33));
    }

    [Fact]
    public void SlV2_UnknownMode_SkipsPortButKeepsQuantityAndFrame()
    {
        var ports = new[] { Port(port: 0, mode: 9999, speed: 0, direction: 0, brightness: 0, Rgb(1, 1, 1)) };

        IReadOnlyList<LightingTransfer> transfers = UniFanLightingEncoder.Encode(UniFanLightingProfiles.SlV2, ports, new[] { 3, 3, 3, 3 });

        Assert.Equal(5, transfers.Count);
        AssertTransfer(transfers[4], feature: true, Feature(0xE0, 0x60, 0, 4));
    }

    // ---- AL v2 (0xA104): 8 ports, reverse order, register 0x60 separate bytes, frame 1 ----

    [Fact]
    public void AlV2_InnerMode_EmitsSeparateQuantityPerFanExpansionEffectThenFrame()
    {
        // StaticColor_Inner (mode 62 -> wire 1) fills 6 fans x 8 LEDs, one saved colour per fan.
        var colors = new[] { Rgb(1, 1, 1), Rgb(2, 2, 2), Rgb(3, 3, 3), Rgb(4, 4, 4), Rgb(5, 5, 5), Rgb(6, 6, 6) };
        var ports = new[] { Port(port: 0, mode: 62, speed: 0, direction: 0, brightness: 0, colors) };

        IReadOnlyList<LightingTransfer> transfers = UniFanLightingEncoder.Encode(UniFanLightingProfiles.AlV2, ports, new[] { 1, 2, 5, 6 });

        Assert.Equal(7, transfers.Count);

        // Register 0x60, group+1 (1-based) in byte[3], quantity in byte[4]; AL v2 accepts up to 6.
        AssertTransfer(transfers[0], feature: true, Feature(0xE0, 0x10, 0x60, 1, 1, 0));
        AssertTransfer(transfers[1], feature: true, Feature(0xE0, 0x10, 0x60, 2, 2, 0));
        AssertTransfer(transfers[2], feature: true, Feature(0xE0, 0x10, 0x60, 3, 5, 0));
        AssertTransfer(transfers[3], feature: true, Feature(0xE0, 0x10, 0x60, 4, 6, 0));

        AssertTransfer(transfers[4], feature: false, ColorReport(0, PerFanLeds(fanCount: 6, ledsPerFan: 8, colors)));
        AssertTransfer(transfers[5], feature: true, Feature(0xE0, 0x10, 1, 0, 0, 0));
        AssertTransfer(transfers[6], feature: true, Feature(0xE0, 0x60, 0, 1));
    }

    [Theory]
    [InlineData(70, 2)] // Breathing_Outer
    [InlineData(92, 1)] // StaticColor_Outer
    public void AlAndAlV2_OuterRingModes_FillTwelveLedsPerFan(int mode, int wire)
    {
        var colors = new[] { Rgb(1, 1, 1), Rgb(2, 2, 2), Rgb(3, 3, 3), Rgb(4, 4, 4), Rgb(5, 5, 5), Rgb(6, 6, 6) };
        var ports = new[] { Port(port: 0, mode: mode, speed: 0, direction: 0, brightness: 0, colors) };

        IReadOnlyList<LightingTransfer> al = UniFanLightingEncoder.Encode(UniFanLightingProfiles.Al, ports, new[] { 4, 4, 4, 4 });
        IReadOnlyList<LightingTransfer> alV2 = UniFanLightingEncoder.Encode(UniFanLightingProfiles.AlV2, ports, new[] { 6, 6, 6, 6 });

        AssertTransfer(al[4], feature: false, ColorReport(0, PerFanLeds(fanCount: 4, ledsPerFan: 12, colors)));
        AssertTransfer(al[5], feature: true, Feature(0xE0, 0x10, (byte)wire, 0, 0, 0));
        AssertTransfer(alV2[4], feature: false, ColorReport(0, PerFanLeds(fanCount: 6, ledsPerFan: 12, colors)));
        AssertTransfer(alV2[5], feature: true, Feature(0xE0, 0x10, (byte)wire, 0, 0, 0));
    }

    [Fact]
    public void AlV2_MeteorMode_CycleFillsFanGroupPalette()
    {
        // Meteor (mode 12 -> wire 25) cycle-fills the 6 fans x 6 slots: slot j = colours[j % count].
        var palette = new[] { Rgb(1, 0, 0), Rgb(0, 2, 0), Rgb(0, 0, 3), Rgb(4, 4, 0) };
        var ports = new[] { Port(port: 0, mode: 12, speed: 0, direction: 0, brightness: 0, palette) };

        IReadOnlyList<LightingTransfer> transfers = UniFanLightingEncoder.Encode(UniFanLightingProfiles.AlV2, ports, new[] { 3, 3, 3, 3 });

        AssertTransfer(transfers[4], feature: false, ColorReport(0, FanGroupLeds(fanCount: 6, slots: 6, cycleFill: true, palette)));
        AssertTransfer(transfers[5], feature: true, Feature(0xE0, 0x10, 25, 0, 0, 0));
    }

    [Fact]
    public void AlV2_OuterCornerMode_ExpandsFourCornersOfThreeLeds()
    {
        // StaticColorful_Outer (mode 93 -> wire 1) fills 6 fans x 12 LEDs as 4 corners x 3.
        var ports = new[]
        {
            Port(port: 0, mode: 93, speed: 0, direction: 0, brightness: 0, Rgb(1, 2, 3), Rgb(4, 5, 6), Rgb(7, 8, 9), Rgb(10, 11, 12)),
        };

        IReadOnlyList<LightingTransfer> transfers = UniFanLightingEncoder.Encode(UniFanLightingProfiles.AlV2, ports, new[] { 3, 3, 3, 3 });

        AssertTransfer(transfers[4], feature: false, ColorReport(0, OuterCornerLeds(fanCount: 6, Rgb(1, 2, 3), Rgb(4, 5, 6), Rgb(7, 8, 9), Rgb(10, 11, 12))));
        AssertTransfer(transfers[5], feature: true, Feature(0xE0, 0x10, 1, 0, 0, 0));
    }

    [Fact]
    public void AlV2_FanGroupMode_AppliesPortsHighToLow()
    {
        // ColorCycle (mode 3 -> wire 46) uses the 36-slot fan-group palette; AL v2 applies 7->0.
        var low = Port(port: 0, mode: 3, speed: 0, direction: 0, brightness: 0, Rgb(9, 8, 7));
        var high = Port(port: 5, mode: 3, speed: 0, direction: 0, brightness: 0, Rgb(9, 8, 7));

        IReadOnlyList<LightingTransfer> transfers = UniFanLightingEncoder.Encode(UniFanLightingProfiles.AlV2, new[] { low, high }, new[] { 3, 3, 3, 3 });

        AssertTransfer(transfers[4], feature: false, ColorReport(5, FanGroupLeds(fanCount: 6, slots: 6, cycleFill: false, Rgb(9, 8, 7))));
        AssertTransfer(transfers[5], feature: true, Feature(0xE0, 0x15, 46, 0, 0, 0));
        AssertTransfer(transfers[6], feature: false, ColorReport(0, FanGroupLeds(fanCount: 6, slots: 6, cycleFill: false, Rgb(9, 8, 7))));
        AssertTransfer(transfers[7], feature: true, Feature(0xE0, 0x10, 46, 0, 0, 0));
    }

    [Fact]
    public void AlV2_LowestBrightness_IsSentAsOff()
    {
        var ports = new[] { Port(port: 0, mode: 26, speed: 0, direction: 0, brightness: 4, Rgb(1, 1, 1)) };

        IReadOnlyList<LightingTransfer> transfers = UniFanLightingEncoder.Encode(UniFanLightingProfiles.AlV2, ports, new[] { 3, 3, 3, 3 });

        AssertTransfer(transfers[5], feature: true, Feature(0xE0, 0x10, 1, 0, 0, 8));
    }

    [Fact]
    public void AlV2_ReflectOuterAndUnknownMode_AreSkipped()
    {
        // Reflect_Outer (86) is absent from AL v2's lookup, so its port is skipped like any
        // unrecognised mode - only the quantity reports and the frame latch remain.
        var ports = new[] { Port(port: 0, mode: 86, speed: 0, direction: 0, brightness: 0, Rgb(1, 1, 1)) };

        IReadOnlyList<LightingTransfer> transfers = UniFanLightingEncoder.Encode(UniFanLightingProfiles.AlV2, ports, new[] { 3, 3, 3, 3 });

        Assert.Equal(5, transfers.Count);
        AssertTransfer(transfers[4], feature: true, Feature(0xE0, 0x60, 0, 1));
    }

    private static LightingPortState Port(int port, int mode, int speed, int direction, int brightness, params RgbColor[] colors)
        => new LightingPortState(port, mode, speed, direction, brightness, colors);

    private static RgbColor Rgb(byte r, byte g, byte b) => new RgbColor(r, g, b);

    private static byte[] Feature(params byte[] head)
    {
        var report = new byte[7];
        System.Array.Copy(head, report, head.Length);
        return report;
    }

    private static byte[] ColorReport(int port, byte[] leds)
    {
        var report = new byte[353];
        report[0] = 0xE0;
        report[1] = (byte)(0x30 | port);
        System.Array.Copy(leds, 0, report, 2, leds.Length);
        return report;
    }

    // The per-fan ring as wire bytes: fan i shows colours[i] across all its LEDs (black past the
    // end), emitted R, B, G.
    private static byte[] PerFanLeds(int fanCount, int ledsPerFan, params RgbColor[] perFan)
    {
        var bytes = new List<byte>();
        for (int fan = 0; fan < fanCount; fan++)
        {
            RgbColor c = fan < perFan.Length ? perFan[fan] : default;
            for (int led = 0; led < ledsPerFan; led++)
            {
                bytes.Add(c.R);
                bytes.Add(c.B);
                bytes.Add(c.G);
            }
        }

        return bytes.ToArray();
    }

    // The fan-group palette as wire bytes: each fan shows the same slots, slot j = palette[j]
    // (black past the end, or palette[j % count] when cycle-filled), emitted R, B, G.
    private static byte[] FanGroupLeds(int fanCount, int slots, bool cycleFill, params RgbColor[] palette)
    {
        var bytes = new List<byte>();
        for (int fan = 0; fan < fanCount; fan++)
        {
            for (int slot = 0; slot < slots; slot++)
            {
                RgbColor c;
                if (cycleFill)
                {
                    c = palette.Length > 0 ? palette[slot % palette.Length] : default;
                }
                else
                {
                    c = slot < palette.Length ? palette[slot] : default;
                }

                bytes.Add(c.R);
                bytes.Add(c.B);
                bytes.Add(c.G);
            }
        }

        return bytes.ToArray();
    }

    // The outer-corner ring as wire bytes: each fan's 12 LEDs split into 4 corners of 3, corner j
    // = corners[j] (black past the end), emitted R, B, G.
    private static byte[] OuterCornerLeds(int fanCount, params RgbColor[] corners)
    {
        var bytes = new List<byte>();
        for (int fan = 0; fan < fanCount; fan++)
        {
            for (int corner = 0; corner < 4; corner++)
            {
                RgbColor c = corner < corners.Length ? corners[corner] : default;
                for (int led = 0; led < 3; led++)
                {
                    bytes.Add(c.R);
                    bytes.Add(c.B);
                    bytes.Add(c.G);
                }
            }
        }

        return bytes.ToArray();
    }

    private static void AssertTransfer(LightingTransfer transfer, bool feature, byte[] report)
    {
        Assert.Equal(feature, transfer.IsFeature);
        Assert.Equal(report, transfer.Report);
    }

    [Fact]
    public void Encode_NullArguments_Throw()
    {
        Assert.Throws<System.ArgumentNullException>(() => UniFanLightingEncoder.Encode(null!, new List<LightingPortState>(), null));
        Assert.Throws<System.ArgumentNullException>(() => UniFanLightingEncoder.Encode(UniFanLightingProfiles.Sl, null!, null));
    }

    [Fact]
    public void Profile_RejectsAnInconsistentDefinition()
    {
        var modes = new Dictionary<int, byte>();
        System.Func<int, IReadOnlyList<RgbColor>, RgbColor[]> expand = (_, colors) => System.Array.Empty<RgbColor>();

        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => new UniFanLightingProfile(false, 0, 0x10, true, 4, System.Array.Empty<int>(), 1, modes, expand));
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => new UniFanLightingProfile(false, 1, 0x10, true, -1, new[] { 1 }, 1, modes, expand));
        Assert.Throws<System.ArgumentNullException>(
            () => new UniFanLightingProfile(false, 1, 0x10, true, 4, null!, 1, modes, expand));
        Assert.Throws<System.ArgumentException>(
            () => new UniFanLightingProfile(false, 2, 0x10, true, 4, new[] { 1 }, 1, modes, expand));
        Assert.Throws<System.ArgumentNullException>(
            () => new UniFanLightingProfile(false, 1, 0x10, true, 4, new[] { 1 }, 1, null!, expand));
        Assert.Throws<System.ArgumentNullException>(
            () => new UniFanLightingProfile(false, 1, 0x10, true, 4, new[] { 1 }, 1, modes, null!));
    }

    [Fact]
    public void ExpandFanGroup_CycleFillWithNoColours_IsAllBlack()
        => Assert.All(UniFanLightingEncoder.ExpandFanGroup(2, 3, new List<RgbColor>(), cycleFill: true), c => Assert.Equal(default, c));

    [Fact]
    public void ExpandOuterCorner_PastTheSuppliedColours_IsBlack()
    {
        RgbColor[] leds = UniFanLightingEncoder.ExpandOuterCorner(1, new[] { Rgb(9, 9, 9) });

        Assert.Equal(Rgb(9, 9, 9), leds[0]);
        Assert.Equal(default, leds[3]); // the second corner has no colour
    }

    // Every mode a profile expands specially, checked against the expansion it should choose.
    [Theory]
    [InlineData("Sl", 1, "perfan:4:16")]
    [InlineData("Sl", 26, "perfan:4:16")]
    [InlineData("SlV2", 1, "perfan:6:16")]
    [InlineData("SlV2", 26, "perfan:6:16")]
    [InlineData("Al", 36, "perfan:4:8")]
    [InlineData("Al", 62, "perfan:4:8")]
    [InlineData("AlV2", 12, "cycle:6:6")]
    [InlineData("AlV2", 47, "cycle:6:6")]
    [InlineData("AlV2", 80, "cycle:6:6")]
    [InlineData("AlV2", 36, "perfan:6:8")]
    [InlineData("AlV2", 62, "perfan:6:8")]
    [InlineData("Al", 70, "perfan:4:12")]
    [InlineData("Al", 92, "perfan:4:12")]
    [InlineData("Al", 71, "corner:4:0")]
    [InlineData("Al", 93, "corner:4:0")]
    [InlineData("Al", 2, "group:4:4")]
    [InlineData("AlV2", 70, "perfan:6:12")]
    [InlineData("AlV2", 92, "perfan:6:12")]
    [InlineData("AlV2", 71, "corner:6:0")]
    [InlineData("AlV2", 93, "corner:6:0")]
    [InlineData("AlV2", 2, "group:6:6")]
    [InlineData("Sl", 2, "group:4:4")]
    [InlineData("SlV2", 2, "group:6:4")]
    public void Profiles_ExpandEachSpecialModeTheWayItsFamilyDoes(string family, int mode, string expansion)
    {
        UniFanLightingProfile profile = family switch
        {
            "Sl" => UniFanLightingProfiles.Sl,
            "SlV2" => UniFanLightingProfiles.SlV2,
            "Al" => UniFanLightingProfiles.Al,
            _ => UniFanLightingProfiles.AlV2,
        };
        var colors = new[] { Rgb(1, 1, 1), Rgb(2, 2, 2), Rgb(3, 3, 3), Rgb(4, 4, 4), Rgb(5, 5, 5), Rgb(6, 6, 6) };
        string[] parts = expansion.Split(':');
        int fans = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        int size = int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
        RgbColor[] expected = parts[0] switch
        {
            "perfan" => UniFanLightingEncoder.ExpandPerFan(fans, size, colors),
            "cycle" => UniFanLightingEncoder.ExpandFanGroup(fans, size, colors, cycleFill: true),
            "corner" => UniFanLightingEncoder.ExpandOuterCorner(fans, colors),
            _ => UniFanLightingEncoder.ExpandFanGroup(fans, size, colors, cycleFill: false),
        };

        Assert.Equal(expected, profile.ExpandColors(mode, colors));
    }
}
#endif
