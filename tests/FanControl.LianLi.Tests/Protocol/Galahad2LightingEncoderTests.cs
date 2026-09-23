#if ENABLE_LIGHTING
using System.Collections.Generic;
using FanControl.LianLi.Protocol;
using Xunit;

namespace FanControl.LianLi.Tests.Protocol;

/// <summary>
/// Byte-level tests for the Galahad II lighting encoder: the 0x85 fan light (direct mode byte, LED
/// count and sync-to-pump flag) then the 0x83 pump light (leading scope byte, mode modulo 1000),
/// both as output reports with R,G,B colours and the MCU signal source.
/// </summary>
public sealed class Galahad2LightingEncoderTests
{
    private static RgbColor Rgb(int r, int g, int b) => new RgbColor((byte)r, (byte)g, (byte)b);

    [Fact]
    public void Encode_WritesFanLightThenPumpLight()
    {
        var fan = new Galahad2FanLightingState(
            mode: 1, speed: 3, direction: 4, brightness: 2, numberOfLed: 24, syncToPump: true,
            colors: new[] { Rgb(255, 0, 0) });
        var pump = new Galahad2PumpLightingState(
            scope: 2, mode: 2001, speed: 3, direction: 5, brightness: 2,
            colors: new[] { Rgb(0, 0, 255) });

        IReadOnlyList<LightingTransfer> transfers = Galahad2LightingEncoder.Encode(fan, pump);

        Assert.Equal(2, transfers.Count);
        Assert.False(transfers[0].IsFeature);
        Assert.False(transfers[1].IsFeature);

        var expectedFan = new byte[64];
        expectedFan[0] = 0x01; // report id
        expectedFan[1] = 0x85; // SetFanLighting
        expectedFan[5] = 20;   // payload length
        expectedFan[6] = 1;    // mode (direct, no modulo)
        expectedFan[7] = 2;    // brightness
        expectedFan[8] = 3;    // speed
        expectedFan[9] = 255;  // colour 0 R
        expectedFan[21] = 4;   // direction
        expectedFan[23] = 0;   // source = MCU
        expectedFan[24] = 1;   // sync-to-pump
        expectedFan[25] = 24;  // number of LEDs
        Assert.Equal(expectedFan, transfers[0].Report);

        var expectedPump = new byte[64];
        expectedPump[0] = 0x01; // report id
        expectedPump[1] = 0x83; // SetPumpLighting
        expectedPump[5] = 19;   // payload length
        expectedPump[6] = 2;    // scope = all
        expectedPump[7] = 1;    // mode 2001 % 1000
        expectedPump[8] = 2;    // brightness
        expectedPump[9] = 3;    // speed
        expectedPump[12] = 255; // colour 0 B (R=0, G=0)
        expectedPump[22] = 5;   // direction
        expectedPump[24] = 0;   // source = MCU
        Assert.Equal(expectedPump, transfers[1].Report);
    }

    [Fact]
    public void Encode_CapsColoursAtFourSlots()
    {
        var fan = new Galahad2FanLightingState(1, 0, 0, 0, 24, false,
            new[] { Rgb(1, 1, 1), Rgb(2, 2, 2), Rgb(3, 3, 3), Rgb(4, 4, 4), Rgb(5, 5, 5) });
        var pump = new Galahad2PumpLightingState(0, 1, 0, 0, 0, new List<RgbColor>());

        IReadOnlyList<LightingTransfer> transfers = Galahad2LightingEncoder.Encode(fan, pump);

        // The fan's fourth colour R is at payload[3 + 3*3] = payload[12] -> frame[18]; a fifth would
        // overrun into payload[15] -> frame[21] (the direction byte), which must stay 0.
        Assert.Equal(4, transfers[0].Report[18]);
        Assert.Equal(0, transfers[0].Report[21]);
    }

    [Fact]
    public void Encode_NullLook_Throws()
    {
        var fan = new Galahad2FanLightingState(0, 0, 0, 0, 0, false, new List<RgbColor>());
        var pump = new Galahad2PumpLightingState(0, 0, 0, 0, 0, new List<RgbColor>());

        Assert.Throws<System.ArgumentNullException>(() => Galahad2LightingEncoder.Encode(null!, pump));
        Assert.Throws<System.ArgumentNullException>(() => Galahad2LightingEncoder.Encode(fan, null!));
    }

    [Fact]
    public void States_RejectMissingColours()
    {
        Assert.Throws<System.ArgumentNullException>(() => new Galahad2FanLightingState(0, 0, 0, 0, 0, false, null!));
        Assert.Throws<System.ArgumentNullException>(() => new Galahad2PumpLightingState(0, 0, 0, 0, 0, null!));
    }
}
#endif
