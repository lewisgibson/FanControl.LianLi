#if ENABLE_LIGHTING
using System.Collections.Generic;
using FanControl.LianLi.Protocol;
using Xunit;

namespace FanControl.LianLi.Tests.Protocol;

/// <summary>
/// Byte-level tests for the Uni Fan TL lighting encoder: the 0xA3 per-fan command frame, the
/// (port, fan-index) address bytes, the mode-modulo-1000 wire value, the up-to-four R,G,B colours
/// with the count byte, and the direction byte. Every transfer is an output report.
/// </summary>
public sealed class TlFanLightingEncoderTests
{
    private static RgbColor Rgb(int r, int g, int b) => new RgbColor((byte)r, (byte)g, (byte)b);

    [Fact]
    public void Encode_WritesOne0xA3FrameWithAddressModeColoursAndCount()
    {
        var fans = new[]
        {
            new TlFanLightingState(port: 1, fanIndex: 2, mode: 1003, speed: 5, direction: 4, brightness: 2,
                colors: new[] { Rgb(255, 0, 0), Rgb(0, 255, 0) }),
        };

        IReadOnlyList<LightingTransfer> transfers = TlFanLightingEncoder.Encode(fans);

        Assert.Single(transfers);
        Assert.False(transfers[0].IsFeature); // output report, not feature

        var expected = new byte[64];
        expected[0] = 0x01;  // report id
        expected[1] = 0xA3;  // SetFanLight
        expected[5] = 20;    // payload length
        expected[6] = 0x10;  // sync(0) | port 1
        expected[7] = 0x12;  // port 1 | fan 2
        expected[8] = 3;     // mode 1003 % 1000
        expected[9] = 2;     // brightness
        expected[10] = 5;    // speed
        expected[11] = 255;  // colour 0 R (G=0, B=0)
        expected[15] = 255;  // colour 1 G (frame[14]=R=0, [15]=G, [16]=B=0)
        expected[23] = 4;    // direction
        expected[25] = 2;    // colour count
        Assert.Equal(expected, transfers[0].Report);
    }

    [Fact]
    public void Encode_CapsAtFourColoursAndReportsThatCount()
    {
        var fans = new[]
        {
            new TlFanLightingState(0, 0, mode: 3, speed: 0, direction: 0, brightness: 0,
                colors: new[] { Rgb(1, 1, 1), Rgb(2, 2, 2), Rgb(3, 3, 3), Rgb(4, 4, 4), Rgb(5, 5, 5) }),
        };

        IReadOnlyList<LightingTransfer> transfers = TlFanLightingEncoder.Encode(fans);

        byte[] report = transfers[0].Report;
        Assert.Equal(4, report[25]); // colour count clamped to four
        Assert.Equal(4, report[20]); // the fourth colour's R (payload[14] -> frame[20]) is written
        // The fifth colour would land on payload[17] -> frame[23], the direction byte; it is not
        // written as a colour, so with direction 0 that byte stays 0.
        Assert.Equal(0, report[23]);
    }

    [Fact]
    public void Encode_NoFans_ProducesNoTransfers()
    {
        Assert.Empty(TlFanLightingEncoder.Encode(new List<TlFanLightingState>()));
    }

    [Fact]
    public void Encode_NullFans_Throws()
        => Assert.Throws<System.ArgumentNullException>(() => TlFanLightingEncoder.Encode(null!));

    [Fact]
    public void State_RejectsMissingColours()
        => Assert.Throws<System.ArgumentNullException>(() => new TlFanLightingState(0, 0, 0, 0, 0, 0, null!));
}
#endif
