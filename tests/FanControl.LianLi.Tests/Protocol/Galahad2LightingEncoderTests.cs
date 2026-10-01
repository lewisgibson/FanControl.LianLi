#if ENABLE_LIGHTING
using System.Collections.Generic;
using FanControl.LianLi.Protocol;
using Xunit;

namespace FanControl.LianLi.Tests.Protocol;

/// <summary>
/// Byte-level tests for the 0x0416 AIO lighting encoder: the Trinity's 0x85 fan light (direct mode
/// byte, LED count and sync-to-pump flag) then its 0x83 pump light (leading scope byte, mode modulo
/// 1000), both as output reports with R,G,B colours and the MCU signal source; the Vision's fan
/// light then its screen ring through the same 0x83 with the sliders scaled; and the HydroShift
/// LCD's fan light alone.
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

        IReadOnlyList<LightingTransfer> transfers = Galahad2LightingEncoder.Encode(fan, new[] { pump });

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
    public void Encode_MotherboardArgbSync_SetsTheMotherboardAsTheSourceOnBoth()
    {
        // L-Connect's setFanLEDLighting(isSync: true) / setPumpLEDLighting(isSync: true): the
        // saved settings are still written, with Source = ARGBSignalSource.Motherboard (1) and
        // Disabled = false, so the colours ride along and the motherboard's header shows.
        var fan = new Galahad2FanLightingState(
            mode: 1, speed: 3, direction: 4, brightness: 2, numberOfLed: 24, syncToPump: false,
            colors: new[] { Rgb(255, 0, 0) });
        var pump = new Galahad2PumpLightingState(
            scope: 2, mode: 2001, speed: 3, direction: 5, brightness: 2,
            colors: new[] { Rgb(0, 0, 255) });

        IReadOnlyList<LightingTransfer> synced = Galahad2LightingEncoder.Encode(fan, new[] { pump }, motherboardArgbSync: true);
        IReadOnlyList<LightingTransfer> own = Galahad2LightingEncoder.Encode(fan, new[] { pump });

        Assert.Equal(1, synced[0].Report[23]); // fan payload[17] = source
        Assert.Equal(0, synced[0].Report[22]); // fan payload[16] = not disabled
        Assert.Equal(1, synced[1].Report[24]); // pump payload[18] = source
        Assert.Equal(0, synced[1].Report[23]); // pump payload[17] = not disabled
        for (int i = 0; i < 64; i++)
        {
            if (i != 23)
            {
                Assert.Equal(own[0].Report[i], synced[0].Report[i]);
            }

            if (i != 24)
            {
                Assert.Equal(own[1].Report[i], synced[1].Report[i]);
            }
        }
    }

    // setPumpLEDLighting writes every saved PumpLightingSetting in order: an individual-mode look
    // is an Inner (0) and an Outer (1) scope, each its own 0x83, after the one 0x85.
    [Fact]
    public void Encode_WritesOnePumpLightPerSavedScope_InOrder()
    {
        var fan = new Galahad2FanLightingState(1, 0, 0, 0, 24, false, new List<RgbColor>());
        var inner = new Galahad2PumpLightingState(scope: 0, mode: 1, speed: 1, direction: 0, brightness: 2, new[] { Rgb(1, 2, 3) });
        var outer = new Galahad2PumpLightingState(scope: 1, mode: 1002, speed: 3, direction: 1, brightness: 4, new[] { Rgb(4, 5, 6) });

        IReadOnlyList<LightingTransfer> transfers = Galahad2LightingEncoder.Encode(fan, new[] { inner, outer }, motherboardArgbSync: true);

        Assert.Equal(3, transfers.Count);
        Assert.Equal(0x85, transfers[0].Report[1]);
        Assert.Equal(new byte[] { 0x83, 0, 0, 0, 19, 0, 1, 2, 1, 1, 2, 3 }, transfers[1].Report[1..13]);   // scope 0, mode 1, brightness 2, speed 1, colour
        Assert.Equal(new byte[] { 0x83, 0, 0, 0, 19, 1, 2, 4, 3, 4, 5, 6 }, transfers[2].Report[1..13]);   // scope 1, mode 1002 % 1000
        Assert.Equal(1, transfers[1].Report[24]);
        Assert.Equal(1, transfers[2].Report[24]);
    }

    // new FanLightingSetting() and new PumpLightingSetting(): what Galahad2TrinityController holds
    // and writes for a half the user never saved.
    [Fact]
    public void LConnectDefaults_AreTheControllersInitialSettings()
    {
        IReadOnlyList<LightingTransfer> transfers = Galahad2LightingEncoder.Encode(
            Galahad2FanLightingState.LConnectDefault, new[] { Galahad2PumpLightingState.LConnectDefault }, motherboardArgbSync: true);

        var expectedFan = new byte[64];
        expectedFan[0] = 0x01;
        expectedFan[1] = 0x85;
        expectedFan[5] = 20;
        expectedFan[6] = 1;   // Rainbow
        expectedFan[7] = 2;   // brightness
        expectedFan[8] = 2;   // speed
        expectedFan[23] = 1;  // source = motherboard
        expectedFan[25] = 24; // LEDs
        Assert.Equal(expectedFan, transfers[0].Report);

        var expectedPump = new byte[64];
        expectedPump[0] = 0x01;
        expectedPump[1] = 0x83;
        expectedPump[5] = 19;
        expectedPump[6] = 2;  // scope All
        expectedPump[7] = 1;  // Rainbow 2001 % 1000
        expectedPump[8] = 2;  // brightness
        expectedPump[9] = 2;  // speed
        expectedPump[24] = 1; // source = motherboard
        Assert.Equal(expectedPump, transfers[1].Report);
    }

    [Fact]
    public void Encode_CapsColoursAtFourSlots()
    {
        var fan = new Galahad2FanLightingState(1, 0, 0, 0, 24, false,
            new[] { Rgb(1, 1, 1), Rgb(2, 2, 2), Rgb(3, 3, 3), Rgb(4, 4, 4), Rgb(5, 5, 5) });
        var pump = new Galahad2PumpLightingState(0, 1, 0, 0, 0, new List<RgbColor>());

        IReadOnlyList<LightingTransfer> transfers = Galahad2LightingEncoder.Encode(fan, new[] { pump });

        // The fan's fourth colour R is at payload[3 + 3*3] = payload[12] -> frame[18]; a fifth would
        // overrun into payload[15] -> frame[21] (the direction byte), which must stay 0.
        Assert.Equal(4, transfers[0].Report[18]);
        Assert.Equal(0, transfers[0].Report[21]);
    }

    [Fact]
    public void EncodeVision_WritesFanLightThenScreenRingWithTheSlidersScaled()
    {
        // Galahad2VisionController.ApplyAll: setFanLEDLighting (0x85, source MCU) then
        // device.SetPumpLighting with the ScreenLEDLighting static look mapped by
        // setScreenLEDLighting: scope Inner (0), the ring mode as-is, brightness and speed each
        // divided by 25 (100 -> 4, 50 -> 2), the static colours, its direction, not disabled, MCU.
        var fan = new Galahad2FanLightingState(
            mode: 5, speed: 2, direction: 1, brightness: 3, numberOfLed: 24, syncToPump: false,
            colors: new[] { Rgb(1, 2, 3), Rgb(4, 5, 6), Rgb(7, 8, 9), Rgb(10, 11, 12) });
        var screen = new Galahad2ScreenLightingState(
            mode: 12, isDynamicMode: false, speed: 50, brightness: 100, direction: 1,
            colors: new[] { Rgb(255, 0, 0), Rgb(0, 255, 0) });

        IReadOnlyList<LightingTransfer> transfers = Galahad2LightingEncoder.EncodeVision(fan, screen);

        Assert.Equal(2, transfers.Count);
        Assert.All(transfers, t => Assert.False(t.IsFeature));

        var expectedFan = new byte[64];
        expectedFan[0] = 0x01; // report id
        expectedFan[1] = 0x85; // SetFanLighting
        expectedFan[5] = 20;   // payload length
        expectedFan[6] = 5;    // mode (Runway)
        expectedFan[7] = 3;    // brightness
        expectedFan[8] = 2;    // speed
        for (int i = 0; i < 12; i++)
        {
            expectedFan[9 + i] = (byte)(i + 1); // Color1..Color4 as R,G,B
        }

        expectedFan[21] = 1;   // direction
        expectedFan[22] = 0;   // not disabled
        expectedFan[23] = 0;   // source = MCU
        expectedFan[24] = 0;   // sync-to-pump off
        expectedFan[25] = 24;  // number of LEDs
        Assert.Equal(expectedFan, transfers[0].Report);

        var expectedScreen = new byte[64];
        expectedScreen[0] = 0x01; // report id
        expectedScreen[1] = 0x83; // SetPumpLighting carries the screen ring
        expectedScreen[5] = 19;   // payload length
        expectedScreen[6] = 0;    // scope = Inner, the only value L-Connect gives the ring
        expectedScreen[7] = 12;   // mode (Voice), direct
        expectedScreen[8] = 4;    // brightness 100 / 25
        expectedScreen[9] = 2;    // speed 50 / 25
        expectedScreen[10] = 255; // colour 0 R
        expectedScreen[14] = 255; // colour 1 G
        expectedScreen[22] = 1;   // direction
        expectedScreen[23] = 0;   // not disabled
        expectedScreen[24] = 0;   // source = MCU
        Assert.Equal(expectedScreen, transfers[1].Report);
    }

    [Theory]
    [InlineData(int.MinValue, 0)] // L-Connect's "unset" slider writes 0
    [InlineData(0, 0)]
    [InlineData(25, 1)]
    [InlineData(75, 3)]
    [InlineData(80, 3)]           // integer division, as L-Connect's (uint)(value / 25)
    public void EncodeVision_ScalesEachScreenSliderByTwentyFive(int slider, byte wire)
    {
        var screen = new Galahad2ScreenLightingState(1, false, speed: slider, brightness: slider, direction: 0, new List<RgbColor>());

        IReadOnlyList<LightingTransfer> transfers = Galahad2LightingEncoder.EncodeVision(null, screen);

        byte[] ring = Assert.Single(transfers).Report;
        Assert.Equal(wire, ring[8]); // brightness
        Assert.Equal(wire, ring[9]); // speed
    }

    [Fact]
    public void EncodeVision_WritesOnlyTheHalfThatWasSaved_AndNeedsOne()
    {
        var fan = new Galahad2FanLightingState(1, 0, 0, 0, 24, false, new List<RgbColor>());
        var screen = new Galahad2ScreenLightingState(1, false, 0, 0, 0, new List<RgbColor>());

        Assert.Equal(0x85, Assert.Single(Galahad2LightingEncoder.EncodeVision(fan, null)).Report[1]);
        Assert.Equal(0x83, Assert.Single(Galahad2LightingEncoder.EncodeVision(null, screen)).Report[1]);
        Assert.Throws<System.ArgumentException>(() => Galahad2LightingEncoder.EncodeVision(null, null));
    }

    [Fact]
    public void EncodeHydroShiftLcd_WritesTheFanLightAlone()
    {
        // HydroShiftLCDController.ApplyAll ends in setFanLEDLighting and nothing else: the 0x85
        // packet with the Colors array (black past its end), source MCU, not disabled.
        var fan = new Galahad2FanLightingState(
            mode: 16, speed: 1, direction: 2, brightness: 4, numberOfLed: 36, syncToPump: false,
            colors: new[] { Rgb(9, 8, 7) });

        IReadOnlyList<LightingTransfer> transfers = Galahad2LightingEncoder.EncodeHydroShiftLcd(fan);

        var expected = new byte[64];
        expected[0] = 0x01; // report id
        expected[1] = 0x85; // SetFanLighting
        expected[5] = 20;   // payload length
        expected[6] = 16;   // mode (Bounce)
        expected[7] = 4;    // brightness
        expected[8] = 1;    // speed
        expected[9] = 9;    // colour 0 R
        expected[10] = 8;   // colour 0 G
        expected[11] = 7;   // colour 0 B
        expected[21] = 2;   // direction
        expected[25] = 36;  // number of LEDs
        LightingTransfer only = Assert.Single(transfers);
        Assert.False(only.IsFeature);
        Assert.Equal(expected, only.Report);
        Assert.Throws<System.ArgumentNullException>(() => Galahad2LightingEncoder.EncodeHydroShiftLcd(null!));
    }

    [Fact]
    public void Encode_NullLook_Throws()
    {
        var fan = new Galahad2FanLightingState(0, 0, 0, 0, 0, false, new List<RgbColor>());
        var pump = new Galahad2PumpLightingState(0, 0, 0, 0, 0, new List<RgbColor>());

        Assert.Throws<System.ArgumentNullException>(() => Galahad2LightingEncoder.Encode(null!, new[] { pump }));
        Assert.Throws<System.ArgumentNullException>(() => Galahad2LightingEncoder.Encode(fan, null!));
        Assert.Throws<System.ArgumentException>(() => Galahad2LightingEncoder.Encode(fan, System.Array.Empty<Galahad2PumpLightingState>()));
        Assert.Throws<System.ArgumentException>(() => Galahad2LightingEncoder.Encode(fan, new Galahad2PumpLightingState?[] { null }!));
    }

    [Fact]
    public void States_RejectMissingColours()
    {
        Assert.Throws<System.ArgumentNullException>(() => new Galahad2FanLightingState(0, 0, 0, 0, 0, false, null!));
        Assert.Throws<System.ArgumentNullException>(() => new Galahad2PumpLightingState(0, 0, 0, 0, 0, null!));
        Assert.Throws<System.ArgumentNullException>(() => new Galahad2ScreenLightingState(0, false, 0, 0, 0, null!));
    }
}
#endif
