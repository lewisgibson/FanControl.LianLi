#if ENABLE_LIGHTING
using System.Collections.Generic;
using FanControl.LianLi.Protocol;
using Xunit;

namespace FanControl.LianLi.Tests.Protocol;

/// <summary>
/// Byte-level tests for the SL-Infinity lighting encoder: the mode-to-wire lookup, per-LED
/// colour expansion in R,B,G order, the fixed 7-byte feature and 353-byte colour reports, and
/// the apply order (fan-quantity groups 0-3, then ports high-to-low, then the frame latch).
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

        Assert.Equal(7, transfers.Count); // 4x SetQuantity + colour + effect + SetFrame

        AssertTransfer(transfers[0], feature: true, Feature(0xE0, 16, 96, 1, 4, 0));
        AssertTransfer(transfers[1], feature: true, Feature(0xE0, 16, 96, 2, 4, 0));
        AssertTransfer(transfers[2], feature: true, Feature(0xE0, 16, 96, 3, 4, 0));
        AssertTransfer(transfers[3], feature: true, Feature(0xE0, 16, 96, 4, 4, 0));

        AssertTransfer(transfers[4], feature: false, ColorReport(0, FanGroupLeds(Rgb(255, 0, 0))));
        AssertTransfer(transfers[5], feature: true, Feature(0xE0, 0x10, 1, 0, 0, 0)); // wire 1, speed/dir/bright 0
        AssertTransfer(transfers[6], feature: true, Feature(0xE0, 96, 0, 1));         // SetFrame(1)
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

        Assert.Equal(5, transfers.Count); // only 4x SetQuantity + SetFrame, no colour/effect
        AssertTransfer(transfers[4], feature: true, Feature(0xE0, 96, 0, 1));
    }

    [Fact]
    public void Encode_NullQuantity_DefaultsToFourPerGroup()
    {
        IReadOnlyList<LightingTransfer> transfers = SlInfinityLightingEncoder.Encode(
            new[] { Port(port: 0, mode: 26, speed: 0, direction: 0, brightness: 0, Rgb(1, 0, 0)) },
            quantity: null);

        AssertTransfer(transfers[0], feature: true, Feature(0xE0, 16, 96, 1, 4, 0));
        AssertTransfer(transfers[3], feature: true, Feature(0xE0, 16, 96, 4, 4, 0));
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
    public void Encode_ImplausibleSavedQuantity_FallsBackToFourPerGroup(int[] quantity)
    {
        IReadOnlyList<LightingTransfer> transfers = SlInfinityLightingEncoder.Encode(
            new[] { Port(port: 0, mode: 26, speed: 0, direction: 0, brightness: 0, Rgb(1, 0, 0)) },
            quantity);

        for (int group = 0; group < 4; group++)
        {
            AssertTransfer(transfers[group], feature: true, Feature(0xE0, 16, 96, (byte)(group + 1), 4, 0));
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
