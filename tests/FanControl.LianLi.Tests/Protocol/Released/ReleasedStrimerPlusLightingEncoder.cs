#pragma warning disable CA1510, CA1512 // The released v1.1.34 source, kept verbatim as the oracle the current encoder is compared against (ReleasedLightingEncoderTests); it compiles here on net8.0, where the analyzer would have it use ThrowIfNull.
#if ENABLE_LIGHTING
using System;
using System.Collections.Generic;

namespace FanControl.LianLi.Protocol;

/// <summary>
/// Pure encoder that turns a Strimer Plus (PID 0xA200) controller's saved L-Connect look into
/// the exact ordered HID transfers L-Connect itself sends to reproduce it. No I/O and no state:
/// same input always yields the same bytes, which is what makes the byte math testable in
/// isolation. The Strimer Plus is a lighting-only RGB extension cable (no fans), so it appears
/// only on the Lighting variant's apply path, never in the fan <c>DeviceCatalog</c>.
/// </summary>
/// <remarks>
/// Per port the apply is two writes: the effect feature report then the colour output report.
/// After every configured port is written, a single enable latch is sent - a per-port enable
/// when exactly one port is configured, otherwise a multi-port enable carrying a 12-bit bitmask
/// of the configured ports. Unlike SL-Infinity the saved mode integer is the wire byte directly
/// (no lookup); colours expand per mode before they are serialised in R,B,G order.
/// </remarks>
internal static class ReleasedStrimerPlusLightingEncoder
{
    // Every Uni-family report starts with report id 0xE0 (224). The Strimer Plus exposes
    // 2 channels x 6 ports = 12 logical ports.
    private const byte ReportId = 0xE0;
    private const int PortCount = 12;

    // L-Connect's normalize step folds the lowest brightness step into Off, and an unset
    // direction into Right - selecting the lowest brightness turns the LEDs off.
    private const int BrightnessLowest = 4;
    private const byte BrightnessOff = 255;
    private const int DirectionNone = -1;
    private const byte DirectionRight = 0;

    // StaticColor/Breathing repeat the single chosen colour across the full per-port LED buffer
    // (27 LEDs is the Strimer Plus per-port maximum).
    private const int MaxLedCountPerPort = 27;

    // Modes whose colours L-Connect replaces with its fixed six-colour rainbow palette before
    // sending: RainbowMorph_Individual(4), Rainbow_Individual(5), BulletStack_Complete(36),
    // Twinkle_Complete(41).
    private static readonly HashSet<int> RainbowPaletteModes = new HashSet<int> { 4, 5, 36, 41 };

    // Modes whose single saved colour is repeated across every LED of the port:
    // StaticColor_Individual(1), Breathing_Individual(2).
    private static readonly HashSet<int> SingleColourModes = new HashSet<int> { 1, 2 };

    // L-Connect's default rainbow palette (controller defaultColors), applied to the rainbow modes.
    private static readonly RgbColor[] RainbowPalette =
    {
        new RgbColor(255, 0, 0),
        new RgbColor(255, 105, 0),
        new RgbColor(255, 215, 0),
        new RgbColor(0, 255, 0),
        new RgbColor(0, 0, 255),
        new RgbColor(170, 0, 255),
    };

    /// <summary>
    /// Encode the ordered transfers that reproduce <paramref name="ports"/> on a Strimer Plus.
    /// Ports outside 0-11 are ignored. Returns an empty list when no port is configured.
    /// </summary>
    public static IReadOnlyList<LightingTransfer> Encode(IReadOnlyList<LightingPortState> ports)
    {
        if (ports is null)
        {
            throw new ArgumentNullException(nameof(ports));
        }

        var transfers = new List<LightingTransfer>();
        int portMask = 0;
        int configuredCount = 0;
        int lastPort = 0;

        foreach (LightingPortState port in ports)
        {
            if (port.Port < 0 || port.Port >= PortCount)
            {
                continue;
            }

            transfers.Add(EncodeEffect(port));

            LightingTransfer? colour = EncodeColour(port);
            if (colour != null)
            {
                transfers.Add(colour.Value);
            }

            portMask |= 1 << port.Port;
            configuredCount++;
            lastPort = port.Port;
        }

        if (configuredCount == 0)
        {
            return transfers;
        }

        transfers.Add(configuredCount == 1 ? EncodeSinglePortEnable(lastPort) : EncodeMultiPortEnable(portMask));
        return transfers;
    }

    // SetEffectSetting: a fixed 6-byte feature report {0xE0, 0x10|port, mode, speed, direction, brightness}.
    // The mode integer is the wire byte directly (no lookup table).
    private static LightingTransfer EncodeEffect(LightingPortState port)
    {
        byte brightness = port.Brightness == BrightnessLowest ? BrightnessOff : (byte)port.Brightness;
        byte direction = port.Direction == DirectionNone ? DirectionRight : (byte)port.Direction;
        byte[] report =
        {
            ReportId,
            (byte)(0x10 | (port.Port & 0x0F)),
            (byte)port.Mode,
            (byte)port.Speed,
            direction,
            brightness,
        };
        return new LightingTransfer(isFeature: true, report);
    }

    // SetColorSetting: a variable-length output report {0xE0, 0x30|port, R,B,G, R,B,G, ...} -
    // note the wire order is R,B,G (green and blue swapped). Skipped when there are no colours.
    private static LightingTransfer? EncodeColour(LightingPortState port)
    {
        IReadOnlyList<RgbColor> colors = ExpandColours(port);
        if (colors.Count == 0)
        {
            return null;
        }

        byte[] report = new byte[2 + (colors.Count * 3)];
        report[0] = ReportId;
        report[1] = (byte)(0x30 | (port.Port & 0x0F));
        for (int i = 0; i < colors.Count; i++)
        {
            int offset = 2 + (i * 3);
            report[offset] = colors[i].R;
            report[offset + 1] = colors[i].B;
            report[offset + 2] = colors[i].G;
        }

        return new LightingTransfer(isFeature: false, report);
    }

    private static IReadOnlyList<RgbColor> ExpandColours(LightingPortState port)
    {
        if (RainbowPaletteModes.Contains(port.Mode))
        {
            return RainbowPalette;
        }

        if (SingleColourModes.Contains(port.Mode))
        {
            if (port.Colors.Count == 0)
            {
                return Array.Empty<RgbColor>();
            }

            RgbColor single = port.Colors[0];
            var repeated = new RgbColor[MaxLedCountPerPort];
            for (int i = 0; i < MaxLedCountPerPort; i++)
            {
                repeated[i] = single;
            }

            return repeated;
        }

        return port.Colors;
    }

    // Single-port enable latch: feature report {0xE0, 0x20|port, 0, 0}.
    private static LightingTransfer EncodeSinglePortEnable(int port)
    {
        byte[] report = { ReportId, (byte)(0x20 | (port & 0x0F)), 0, 0 };
        return new LightingTransfer(isFeature: true, report);
    }

    // Multi-port enable latch: feature report {0xE0, 0x2C, maskHigh, maskLow} where 0x2C is
    // 0x20 | 12 (the multi-port form) and the 12-bit mask has one bit set per enabled port.
    private static LightingTransfer EncodeMultiPortEnable(int portMask)
    {
        byte[] report =
        {
            ReportId,
            (byte)(0x20 | 0x0C),
            (byte)((portMask >> 8) & 0xFF),
            (byte)(portMask & 0xFF),
        };
        return new LightingTransfer(isFeature: true, report);
    }
}
#endif

#pragma warning restore CA1510, CA1512
