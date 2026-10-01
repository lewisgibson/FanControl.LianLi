#if ENABLE_LIGHTING
using System.Collections.Generic;
using FanControl.LianLi.Protocol;
using Xunit;

namespace FanControl.LianLi.Tests.Protocol;

/// <summary>
/// Byte-level tests for the SL-Infinity lighting encoder: the mode-to-wire lookup, per-LED
/// colour expansion in R,B,G order, the fixed 7-byte feature and 353-byte colour reports, the
/// apply order (fan-quantity groups 0-3, the merge order, then ports high-to-low, then the frame
/// latch), the merge-mode sequence (L-Connect's default look on every port and a frame, then ports
/// 7-1 blanked, then port 0, no further frame) and the motherboard ARGB-sync hand-over (fan-quantity
/// and merge order, then the sync register alone).
/// </summary>
public sealed class SlInfinityLightingEncoderTests
{
    [Fact]
    public void Encode_StaticColor_EmitsQuantityThenColourEffectThenFrame()
    {
        // StaticColor (mode 26 -> wire 1) uses the 16-slot fan-group palette.
        var ports = new[]
        {
            Port(port: 0, mode: 26, speed: 0, direction: 0, brightness: 0, Rgb(255, 0, 0)),
        };

        IReadOnlyList<LightingTransfer> transfers = SlInfinityLightingEncoder.Encode(ports, new[] { 4, 4, 4, 4 });

        Assert.Equal(8, transfers.Count); // 4x SetQuantity + colour + effect + SetFrame + merge order

        AssertTransfer(transfers[0], feature: true, Feature(0xE0, 16, 96, 1, 4, 0));
        AssertTransfer(transfers[1], feature: true, Feature(0xE0, 16, 96, 2, 4, 0));
        AssertTransfer(transfers[2], feature: true, Feature(0xE0, 16, 96, 3, 4, 0));
        AssertTransfer(transfers[3], feature: true, Feature(0xE0, 16, 96, 4, 4, 0));

        AssertTransfer(transfers[4], feature: false, ColorReport(0, FanGroupLeds(Rgb(255, 0, 0))));
        AssertTransfer(transfers[5], feature: true, Feature(0xE0, 0x10, 1, 0, 0, 0)); // wire 1, speed/dir/bright 0
        AssertTransfer(transfers[6], feature: true, Feature(0xE0, 96, 0, 1));         // SetFrame(1)

        // The merge order L-Connect writes on every start and resume after setFanQuantity's look
        // and frame (Init and ResumeSuspend: setFanQuantity, then setMergeOrder): its default
        // 0,1,2,3 when none is saved, an 8-byte report on register 0x63 ending in 8.
        AssertTransfer(transfers[7], feature: true, MergeOrder(0, 1, 2, 3));
    }

    [Fact]
    public void Encode_Lottery_UsesLookedUpWireByteAndFanGroupPalette()
    {
        // The hardware-verified user look: Lottery_Inner (46) and Lottery_Outer (79) both map
        // to wire 38 and both use the 16-slot fan-group palette (two colours per fan).
        var inner = Port(port: 0, mode: 46, speed: 1, direction: 0, brightness: 0, Rgb(0, 215, 255), Rgb(0, 8, 255));
        var outer = Port(port: 1, mode: 79, speed: 255, direction: 0, brightness: 0, Rgb(0, 215, 255), Rgb(0, 8, 255));

        IReadOnlyList<LightingTransfer> transfers = SlInfinityLightingEncoder.Encode(new[] { inner, outer }, new[] { 4, 4, 4, 4 });

        // Ports apply high-to-low: port 1 (outer) before port 0 (inner).
        AssertTransfer(transfers[4], feature: false, ColorReport(1, FanGroupLeds(Rgb(0, 215, 255), Rgb(0, 8, 255))));
        AssertTransfer(transfers[5], feature: true, Feature(0xE0, 0x11, 38, 255, 0, 0));
        AssertTransfer(transfers[6], feature: false, ColorReport(0, FanGroupLeds(Rgb(0, 215, 255), Rgb(0, 8, 255))));
        AssertTransfer(transfers[7], feature: true, Feature(0xE0, 0x10, 38, 1, 0, 0));
        AssertTransfer(transfers[8], feature: true, Feature(0xE0, 96, 0, 1));
        AssertTransfer(transfers[9], feature: true, MergeOrder(0, 1, 2, 3));
    }

    [Fact]
    public void Encode_StaticColorInner_ExpandsToThirtyTwoLedsOnePerFan()
    {
        // StaticColor_Inner (mode 62 -> wire 1) fills 4 fans x 8 LEDs, one colour per fan.
        var ports = new[]
        {
            Port(port: 0, mode: 62, speed: 0, direction: 0, brightness: 0, Rgb(10, 20, 30), Rgb(40, 50, 60)),
        };

        IReadOnlyList<LightingTransfer> transfers = SlInfinityLightingEncoder.Encode(ports, new[] { 4, 4, 4, 4 });

        AssertTransfer(transfers[4], feature: false, ColorReport(0, PerFanLeds(8, Rgb(10, 20, 30), Rgb(40, 50, 60))));
        AssertTransfer(transfers[5], feature: true, Feature(0xE0, 0x10, 1, 0, 0, 0));
    }

    [Fact]
    public void Encode_StaticColorOuter_ExpandsToFortyEightLedsOnePerFan()
    {
        // StaticColor_Outer (mode 92 -> wire 1) fills 4 fans x 12 LEDs, one colour per fan.
        var ports = new[]
        {
            Port(port: 0, mode: 92, speed: 0, direction: 0, brightness: 0, Rgb(1, 2, 3)),
        };

        IReadOnlyList<LightingTransfer> transfers = SlInfinityLightingEncoder.Encode(ports, new[] { 4, 4, 4, 4 });

        AssertTransfer(transfers[4], feature: false, ColorReport(0, PerFanLeds(12, Rgb(1, 2, 3))));
    }

    [Fact]
    public void Encode_LowestBrightness_IsSentAsOff()
    {
        // NormalizeBrightness maps Brightness.Lowest (4) to Off on the wire. For the Uni fan family
        // Off is byte 8 (Ene6K77Fan LightingBrightness.Off), NOT the Strimer Plus value 255.
        var ports = new[]
        {
            Port(port: 0, mode: 26, speed: 0, direction: 0, brightness: 4, Rgb(1, 1, 1)),
        };

        IReadOnlyList<LightingTransfer> transfers = SlInfinityLightingEncoder.Encode(ports, new[] { 4, 4, 4, 4 });

        AssertTransfer(transfers[5], feature: true, Feature(0xE0, 0x10, 1, 0, 0, 8));
    }

    [Fact]
    public void Encode_UnknownMode_SkipsThatPortButKeepsQuantityAndFrame()
    {
        // A mode L-Connect's lookup does not contain leaves that port untouched.
        var ports = new[]
        {
            Port(port: 0, mode: 9999, speed: 0, direction: 0, brightness: 0, Rgb(1, 1, 1)),
        };

        IReadOnlyList<LightingTransfer> transfers = SlInfinityLightingEncoder.Encode(ports, new[] { 4, 4, 4, 4 });

        Assert.Equal(6, transfers.Count); // only 4x SetQuantity + SetFrame + merge order, no colour/effect
        AssertTransfer(transfers[4], feature: true, Feature(0xE0, 96, 0, 1));
        AssertTransfer(transfers[5], feature: true, MergeOrder(0, 1, 2, 3));
    }

    [Fact]
    public void Encode_NullQuantity_DefaultsToThreePerGroup()
    {
        // L-Connect's SLInfinityController starts its fanQuantityList as four 3s and writes that
        // at Init; a controller with no saved FanQuantity keeps it, so the plugin sends the same.
        IReadOnlyList<LightingTransfer> transfers = SlInfinityLightingEncoder.Encode(
            new[] { Port(port: 0, mode: 26, speed: 0, direction: 0, brightness: 0, Rgb(1, 0, 0)) },
            quantity: null);

        AssertTransfer(transfers[0], feature: true, Feature(0xE0, 16, 96, 1, 3, 0));
        AssertTransfer(transfers[1], feature: true, Feature(0xE0, 16, 96, 2, 3, 0));
        AssertTransfer(transfers[2], feature: true, Feature(0xE0, 16, 96, 3, 3, 0));
        AssertTransfer(transfers[3], feature: true, Feature(0xE0, 16, 96, 4, 3, 0));
    }

    [Fact]
    public void Encode_WritesTheSavedMergeOrder_AndFallsBackToTheDefaultWhenItFailsValidation()
    {
        // SLInfinityController.setMergeOrder: four entries each 0..4 go to SLInfinityDevice.SetMergeOrder
        // {E0,10,63,o0,o1,o2,o3,8}; anything else returns early and the default written at Init stands.
        // It follows the look and the frame (index 7 with one port: four quantities, colour, effect, frame).
        var ports = new[] { Port(port: 0, mode: 26, speed: 0, direction: 0, brightness: 0, Rgb(1, 0, 0)) };

        AssertTransfer(SlInfinityLightingEncoder.Encode(ports, null, mergeOrder: new[] { 3, 2, 1, 0 })[7], feature: true, MergeOrder(3, 2, 1, 0));
        AssertTransfer(SlInfinityLightingEncoder.Encode(ports, null, mergeOrder: new[] { 0, 1, 2, 5 })[7], feature: true, MergeOrder(0, 1, 2, 3));
        AssertTransfer(SlInfinityLightingEncoder.Encode(ports, null, mergeOrder: new[] { 0, 1, 2 })[7], feature: true, MergeOrder(0, 1, 2, 3));
        AssertTransfer(SlInfinityLightingEncoder.Encode(ports, null, mergeOrder: new[] { -1, 1, 2, 3 })[7], feature: true, MergeOrder(0, 1, 2, 3));
    }

    [Fact]
    public void Encode_HonoursSavedQuantityPerGroup()
    {
        IReadOnlyList<LightingTransfer> transfers = SlInfinityLightingEncoder.Encode(
            new[] { Port(port: 0, mode: 26, speed: 0, direction: 0, brightness: 0, Rgb(1, 0, 0)) },
            new[] { 1, 2, 3, 4 });

        AssertTransfer(transfers[0], feature: true, Feature(0xE0, 16, 96, 1, 1, 0));
        AssertTransfer(transfers[1], feature: true, Feature(0xE0, 16, 96, 2, 2, 0));
        AssertTransfer(transfers[2], feature: true, Feature(0xE0, 16, 96, 3, 3, 0));
        AssertTransfer(transfers[3], feature: true, Feature(0xE0, 16, 96, 4, 4, 0));
    }

    [Theory]
    [InlineData(101, 76)]  // Door_Merge
    [InlineData(102, 78)]  // ElectricCurrent_Merge
    [InlineData(103, 77)]  // HeartBeatRunway_Merge
    [InlineData(105, 72)]  // Mixing_Merge
    [InlineData(106, 71)]  // MopUp_Merge
    [InlineData(107, 70)]  // Runway_Merge
    [InlineData(108, 75)]  // Scan_Merge
    [InlineData(110, 73)]  // Stack_Merge
    [InlineData(113, 74)]  // Tide_Merge
    public void Encode_MergeEffectOnPortZero_WritesTheDefaultLookAndAFrame_ThenBlanksPortsSevenToOneAndWritesPortZeroWithNoFrame(int mode, byte wire)
    {
        // L-Connect's cold start: setupDefaultLightingConfig gives every port StaticColor_Inner
        // (even ports, wire 1, 32 LEDs) or StaticColor_Outer (odd, wire 1, 48 LEDs) in red, blue,
        // green and yellow at brightness Highest (0), and Init's setFanQuantity writes them 7..0
        // and latches a frame before any saved setting is read, then setMergeOrder writes the
        // order; then setMergeLighting: an effect-only Rainbow (0x32) at speed 0, direction 0 and
        // brightness Off (8) on ports 7..1 descending (its empty colour list sends no colour
        // report), then port 0's colour and effect last, and no frame latch. The saved looks of
        // ports 1..7 are stale in merge mode and are not replayed.
        var ports = new[]
        {
            Port(port: 3, mode: 26, speed: 0, direction: 0, brightness: 0, Rgb(9, 9, 9)),
            Port(port: 0, mode: mode, speed: 1, direction: 1, brightness: 2, Rgb(255, 0, 0), Rgb(0, 255, 0)),
            Port(port: 1, mode: 62, speed: 0, direction: 0, brightness: 0, Rgb(8, 8, 8)),
        };
        RgbColor[] defaultColours = { Rgb(255, 0, 0), Rgb(0, 0, 255), Rgb(0, 128, 0), Rgb(255, 255, 0) };

        IReadOnlyList<LightingTransfer> transfers = SlInfinityLightingEncoder.Encode(ports, new[] { 4, 4, 4, 4 });

        Assert.Equal(31, transfers.Count); // 4x SetQuantity + 8 x (colour + effect) + SetFrame + merge order + 7 blanks + colour + effect
        for (int port = 7; port >= 0; port--)
        {
            int at = 4 + ((7 - port) * 2);
            byte[] leds = (port & 1) == 0 ? PerFanLeds(8, defaultColours) : PerFanLeds(12, defaultColours);
            AssertTransfer(transfers[at], feature: false, ColorReport(port, leds));
            AssertTransfer(transfers[at + 1], feature: true, Feature(0xE0, (byte)(0x10 | port), 1, 0, 0, 0));
        }

        AssertTransfer(transfers[20], feature: true, Feature(0xE0, 96, 0, 1));
        AssertTransfer(transfers[21], feature: true, MergeOrder(0, 1, 2, 3));
        for (int port = 7; port >= 1; port--)
        {
            AssertTransfer(transfers[22 + (7 - port)], feature: true, Feature(0xE0, (byte)(0x10 | port), 0x32, 0, 0, 8));
        }

        AssertTransfer(transfers[29], feature: false, ColorReport(0, FanGroupLeds(Rgb(255, 0, 0), Rgb(0, 255, 0))));
        AssertTransfer(transfers[30], feature: true, Feature(0xE0, 0x10, wire, 1, 1, 2));
    }

    [Fact]
    public void Encode_MergeEffectOnAnotherPort_IsNotMergeMode()
    {
        // Only port 0 decides merge mode (L-Connect's isMergeMode reads port 0's saved mode), so a
        // merge mode saved on port 2 is replayed like any other port, frame latch and all.
        var ports = new[]
        {
            Port(port: 2, mode: 107, speed: 0, direction: 0, brightness: 0, Rgb(1, 2, 3)),
            Port(port: 0, mode: 26, speed: 0, direction: 0, brightness: 0, Rgb(4, 5, 6)),
        };

        IReadOnlyList<LightingTransfer> transfers = SlInfinityLightingEncoder.Encode(ports, new[] { 4, 4, 4, 4 });

        Assert.Equal(10, transfers.Count); // 4x SetQuantity + 2 ports x (colour + effect) + SetFrame + merge order
        AssertTransfer(transfers[5], feature: true, Feature(0xE0, 0x12, 70, 0, 0, 0));
        AssertTransfer(transfers[8], feature: true, Feature(0xE0, 96, 0, 1));
        AssertTransfer(transfers[9], feature: true, MergeOrder(0, 1, 2, 3));
    }

    [Fact]
    public void Encode_MotherboardArgbSync_WritesQuantityThenTheSyncRegisterAndNoLook()
    {
        // The controller's L-Connect "sync to motherboard" switch: the fan quantity and merge order
        // are still set, in that order, then register 97 is written 1 and nothing else (Init and
        // ResumeSuspend: setMergeOrder, then setMotherboardARGBSync). The saved look, merge mode or
        // not, is not replayed and no frame is latched.
        var ports = new[]
        {
            Port(port: 0, mode: 107, speed: 0, direction: 0, brightness: 0, Rgb(255, 0, 0)),
            Port(port: 1, mode: 26, speed: 0, direction: 0, brightness: 0, Rgb(0, 255, 0)),
        };

        IReadOnlyList<LightingTransfer> transfers = SlInfinityLightingEncoder.Encode(ports, new[] { 1, 2, 3, 4 }, motherboardArgbSync: true);

        Assert.Equal(6, transfers.Count);
        AssertTransfer(transfers[0], feature: true, Feature(0xE0, 16, 96, 1, 1, 0));
        AssertTransfer(transfers[3], feature: true, Feature(0xE0, 16, 96, 4, 4, 0));
        AssertTransfer(transfers[4], feature: true, MergeOrder(0, 1, 2, 3));
        AssertTransfer(transfers[5], feature: true, Feature(0xE0, 0x10, 97, 1));
        Assert.Equal(new SlInfinityProtocol().EncodeArgbSync(true), transfers[5].Report); // the ARGB build's own report
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

    // The merge-order report: {E0, 0x10, 0x63, o0, o1, o2, o3, 8}, 8 bytes (L-Connect's own length).
    private static byte[] MergeOrder(byte o0, byte o1, byte o2, byte o3) => new byte[] { 0xE0, 0x10, 0x63, o0, o1, o2, o3, 8 };

    private static byte[] ColorReport(int port, byte[] leds)
    {
        var report = new byte[353];
        report[0] = 0xE0;
        report[1] = (byte)(0x30 | port);
        System.Array.Copy(leds, 0, report, 2, leds.Length);
        return report;
    }

    // The 16-slot fan-group palette as wire bytes: each fan shows the same 4 slots, slot j =
    // palette[j] (black past the end), emitted R, B, G.
    private static byte[] FanGroupLeds(params RgbColor[] palette)
    {
        var bytes = new List<byte>();
        for (int fan = 0; fan < 4; fan++)
        {
            for (int slot = 0; slot < 4; slot++)
            {
                RgbColor c = slot < palette.Length ? palette[slot] : default;
                bytes.Add(c.R);
                bytes.Add(c.B);
                bytes.Add(c.G);
            }
        }

        return bytes.ToArray();
    }

    // The inner/outer ring as wire bytes: fan i shows colours[i] across all its LEDs (black
    // past the end), emitted R, B, G.
    private static byte[] PerFanLeds(int ledsPerFan, params RgbColor[] perFan)
    {
        var bytes = new List<byte>();
        for (int fan = 0; fan < 4; fan++)
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

    private static void AssertTransfer(LightingTransfer transfer, bool feature, byte[] report)
    {
        Assert.Equal(feature, transfer.IsFeature);
        Assert.Equal(report, transfer.Report);
    }

    [Fact]
    public void Encode_NullPorts_Throws()
        => Assert.Throws<System.ArgumentNullException>(() => SlInfinityLightingEncoder.Encode(null!, null));

    [Theory]
    [InlineData(new[] { 4, 5, 4, 4 })]  // a group above the four fans a port can carry
    [InlineData(new[] { 4, -1, 4, 4 })] // a negative count
    [InlineData(new[] { 4, 4, 4 })]     // the wrong number of groups
    public void Encode_ImplausibleSavedQuantity_FallsBackToThreePerGroup(int[] quantity)
    {
        // L-Connect's setFanQuantity returns early on such a list, so the 3s it wrote at Init stand.
        IReadOnlyList<LightingTransfer> transfers = SlInfinityLightingEncoder.Encode(
            new[] { Port(port: 0, mode: 26, speed: 0, direction: 0, brightness: 0, Rgb(1, 0, 0)) },
            quantity);

        for (int group = 0; group < 4; group++)
        {
            AssertTransfer(transfers[group], feature: true, Feature(0xE0, 16, 96, (byte)(group + 1), 3, 0));
        }
    }

    [Fact]
    public void PortStateAndTransfer_RejectMissingData()
    {
        Assert.Throws<System.ArgumentNullException>(() => new LightingPortState(0, 0, 0, 0, 0, null!));
        Assert.Throws<System.ArgumentNullException>(() => new LightingTransfer(true, null!));
    }
}
#endif
