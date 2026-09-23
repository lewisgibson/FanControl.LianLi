#if ENABLE_LIGHTING
using System.Collections.Generic;
using System.Linq;
using FanControl.LianLi.Protocol;
using Xunit;

namespace FanControl.LianLi.Tests.Protocol;

/// <summary>
/// Byte-level tests for the Strimer Plus lighting encoder: the 6-byte effect feature report,
/// the variable-length colour output report in R,B,G order, the per-mode colour expansion
/// (rainbow palette / single colour x27 / passthrough), the brightness and direction folds, and
/// the single- vs multi-port enable latch. Mode integers are the wire bytes directly.
/// </summary>
public sealed class StrimerPlusLightingEncoderTests
{
    private static LightingPortState Port(int port, int mode, int speed, int direction, int brightness, params RgbColor[] colors)
        => new LightingPortState(port, mode, speed, direction, brightness, colors);

    private static RgbColor Rgb(int r, int g, int b) => new RgbColor((byte)r, (byte)g, (byte)b);

    private static void AssertTransfer(LightingTransfer transfer, bool feature, byte[] expected)
    {
        Assert.Equal(feature, transfer.IsFeature);
        Assert.Equal(expected, transfer.Report);
    }

    // Build the expected R,B,G colour output report for a port (green/blue swapped on the wire).
    private static byte[] ColorReport(int port, params RgbColor[] colors)
    {
        var bytes = new List<byte> { 0xE0, (byte)(0x30 | port) };
        foreach (RgbColor c in colors)
        {
            bytes.Add(c.R);
            bytes.Add(c.B);
            bytes.Add(c.G);
        }

        return bytes.ToArray();
    }

    [Fact]
    public void Encode_StaticColor_RepeatsTheColourAcross27Leds_ThenSinglePortEnable()
    {
        var ports = new[] { Port(port: 0, mode: 1, speed: 0, direction: 0, brightness: 0, Rgb(255, 0, 0)) };

        IReadOnlyList<LightingTransfer> transfers = StrimerPlusLightingEncoder.Encode(ports);

        Assert.Equal(3, transfers.Count); // effect + colour + enable
        AssertTransfer(transfers[0], feature: true, new byte[] { 0xE0, 0x10, 1, 0, 0, 0 });

        RgbColor[] expanded = Enumerable.Repeat(Rgb(255, 0, 0), 27).ToArray();
        AssertTransfer(transfers[1], feature: false, ColorReport(0, expanded));
        Assert.Equal(2 + (27 * 3), transfers[1].Report.Length);

        AssertTransfer(transfers[2], feature: true, new byte[] { 0xE0, 0x20, 0, 0 }); // single-port enable
    }

    [Fact]
    public void Encode_ColourOrderIsRBG()
    {
        // A passthrough mode (Tide_Individual = 30) sends the saved colours unchanged, R,B,G order.
        var ports = new[] { Port(port: 0, mode: 30, speed: 0, direction: 0, brightness: 0, Rgb(0x11, 0x22, 0x33)) };

        IReadOnlyList<LightingTransfer> transfers = StrimerPlusLightingEncoder.Encode(ports);

        // R=0x11, B=0x33, G=0x22 on the wire.
        AssertTransfer(transfers[1], feature: false, new byte[] { 0xE0, 0x30, 0x11, 0x33, 0x22 });
    }

    [Fact]
    public void Encode_RainbowMode_UsesTheFixedSixColourPalette()
    {
        // Rainbow_Individual (5) replaces the saved colours with L-Connect's rainbow palette.
        var ports = new[] { Port(port: 2, mode: 5, speed: 0, direction: 0, brightness: 0, Rgb(1, 2, 3)) };

        IReadOnlyList<LightingTransfer> transfers = StrimerPlusLightingEncoder.Encode(ports);

        byte[] expected = ColorReport(
            2,
            Rgb(255, 0, 0), Rgb(255, 105, 0), Rgb(255, 215, 0), Rgb(0, 255, 0), Rgb(0, 0, 255), Rgb(170, 0, 255));
        AssertTransfer(transfers[1], feature: false, expected);
    }

    [Fact]
    public void Encode_BrightnessLowestFoldsToOff_AndDirectionNoneFoldsToRight()
    {
        var ports = new[] { Port(port: 0, mode: 30, speed: 2, direction: -1, brightness: 4) };

        IReadOnlyList<LightingTransfer> transfers = StrimerPlusLightingEncoder.Encode(ports);

        // brightness 4 (Lowest) -> 255 (Off); direction -1 (None) -> 0 (Right).
        AssertTransfer(transfers[0], feature: true, new byte[] { 0xE0, 0x10, 30, 2, 0, 255 });
    }

    [Fact]
    public void Encode_NoColours_SkipsTheColourReport()
    {
        // A passthrough mode with no colours emits only the effect and the enable.
        var ports = new[] { Port(port: 0, mode: 30, speed: 0, direction: 0, brightness: 0) };

        IReadOnlyList<LightingTransfer> transfers = StrimerPlusLightingEncoder.Encode(ports);

        Assert.Equal(2, transfers.Count);
        AssertTransfer(transfers[0], feature: true, new byte[] { 0xE0, 0x10, 30, 0, 0, 0 });
        AssertTransfer(transfers[1], feature: true, new byte[] { 0xE0, 0x20, 0, 0 });
    }

    [Fact]
    public void Encode_MultiplePorts_EndsWithMultiPortEnableBitmask()
    {
        var ports = new[]
        {
            Port(port: 0, mode: 30, speed: 0, direction: 0, brightness: 0, Rgb(1, 2, 3)),
            Port(port: 6, mode: 30, speed: 0, direction: 0, brightness: 0, Rgb(4, 5, 6)),
        };

        IReadOnlyList<LightingTransfer> transfers = StrimerPlusLightingEncoder.Encode(ports);

        // Last transfer is a multi-port enable: 0x2C, then the 12-bit mask (bits 0 and 6 = 0x041) big-endian.
        LightingTransfer enable = transfers[transfers.Count - 1];
        AssertTransfer(enable, feature: true, new byte[] { 0xE0, 0x2C, 0x00, 0x41 });
    }

    [Fact]
    public void Encode_IgnoresOutOfRangePorts()
    {
        var ports = new[] { Port(port: 99, mode: 30, speed: 0, direction: 0, brightness: 0, Rgb(1, 2, 3)) };

        Assert.Empty(StrimerPlusLightingEncoder.Encode(ports));
    }

    [Fact]
    public void Encode_NullPorts_Throws()
        => Assert.Throws<System.ArgumentNullException>(() => StrimerPlusLightingEncoder.Encode(null!));

    [Fact]
    public void Encode_SingleColourModeWithNoColourSaved_SendsTheEffectButNoColourReport()
    {
        var ports = new[] { Port(port: 3, mode: 1, speed: 0, direction: 0, brightness: 0) };

        IReadOnlyList<LightingTransfer> transfers = StrimerPlusLightingEncoder.Encode(ports);

        Assert.Equal(2, transfers.Count); // effect, then the single-port enable
        Assert.True(transfers[0].IsFeature);
        Assert.Equal(0x13, transfers[0].Report[1]);
        Assert.Equal(new byte[] { 0xE0, 0x23, 0x00, 0x00 }, transfers[1].Report);
    }
}
#endif
