#pragma warning disable CA1510, CA1512 // The released v1.1.34 source, kept verbatim as the oracle the current encoder is compared against (ReleasedLightingEncoderTests); it compiles here on net8.0, where the analyzer would have it use ThrowIfNull.
#if ENABLE_LIGHTING
using System;
using System.Collections.Generic;
using System.Linq;

namespace FanControl.LianLi.Protocol;

/// <summary>
/// Pure encoder that turns a Uni fan controller's saved L-Connect look into the exact ordered
/// HID transfers L-Connect itself sends to reproduce it. One encoder drives every Uni fan family
/// (SL, AL, SL v2, AL v2, and the Redragon SL variant); the per-family differences - apply order,
/// fan-quantity report layout, mode-to-wire table, frame-latch value, and colour expansion - come
/// in as a <see cref="ReleasedUniFanLightingProfile"/>. No I/O and no state: same input always yields the
/// same bytes, which is what makes the byte math testable in isolation.
/// </summary>
/// <remarks>
/// The apply sequence matches L-Connect: set the per-group fan quantity (groups 0-3), then for
/// each present port (in the family's apply order) write the colour output report followed by the
/// effect feature report, then latch the frame. The colour output report, effect feature report,
/// and frame latch share the SL-Infinity byte layout; only the values captured in the profile
/// differ across families.
/// </remarks>
internal static class ReleasedUniFanLightingEncoder
{
    // Every Uni report starts with report id 0xE0 (224). Feature reports are a fixed 7 bytes and
    // the colour output report a fixed 353 bytes (header + per-LED data, zero-padded) - the same
    // fixed lengths the byte-verified SL-Infinity encoder uses, large enough for every family's
    // largest LED model (SL v2's 96-LED buffer is 2 + 96*3 = 290 <= 353).
    private const byte ReportId = 0xE0;
    private const int ColorReportLength = 353;

    // byte[1] of the fan-quantity report selects the lighting command set; the frame report's
    // byte[1] selects the frame register. Both are shared across the whole Uni fan family.
    private const byte LightingCommand = 0x10;
    private const byte FrameRegister = 0x60;

    // L-Connect's brightness enum has both a Lowest (4) and an Off step, and its own normalize
    // step folds Lowest into Off - choosing the lowest brightness turns the LEDs off. We reproduce
    // that: a saved Lowest brightness is sent as Off on the wire. For the Uni fan family Off is 8.
    private const int BrightnessLowest = 4;
    private const byte BrightnessOff = 8;

    // Outer-corner ring geometry: each fan's 12 outer LEDs are grouped as 4 corners of 3 LEDs.
    private const int OuterCorners = 4;
    private const int LedsPerCorner = 3;

    /// <summary>
    /// Encode the full apply sequence for one controller: fan-quantity for each group, each present
    /// port (in the profile's apply order) as a colour output report and an effect feature report,
    /// then the frame latch. Ports whose mode the family does not recognise are skipped, as
    /// L-Connect skips them.
    /// </summary>
    /// <param name="profile">The family parameters that drive the encoding.</param>
    /// <param name="ports">The saved per-port looks to replay.</param>
    /// <param name="quantity">The saved per-group fan quantity, or null to use the family default.</param>
    /// <returns>The ordered transfers to write verbatim to the device.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> or <paramref name="ports"/> is null.</exception>
    public static IReadOnlyList<LightingTransfer> Encode(
        ReleasedUniFanLightingProfile profile, IReadOnlyList<LightingPortState> ports, IReadOnlyList<int>? quantity)
    {
        if (profile is null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        if (ports is null)
        {
            throw new ArgumentNullException(nameof(ports));
        }

        var transfers = new List<LightingTransfer>();

        IReadOnlyList<int> fanQuantity = NormalizeQuantity(profile, quantity);
        for (int group = 0; group < profile.QuantityGroupCount; group++)
        {
            transfers.Add(Feature(EncodeQuantity(profile, group, fanQuantity[group])));
        }

        IEnumerable<LightingPortState> ordered = profile.ReverseApplyOrder
            ? ports.OrderByDescending(p => p.Port)
            : ports.OrderBy(p => p.Port);
        foreach (LightingPortState port in ordered)
        {
            AppendPort(profile, transfers, port);
        }

        transfers.Add(Feature(EncodeFrame(profile.FrameValue)));
        return transfers;
    }

    // Fan i takes colours[i] across all its LEDs; fans past the supplied colours go black. Used by
    // the per-fan ring models (SL/SL v2 full, AL/AL v2 inner and outer).
    internal static RgbColor[] ExpandPerFan(int fanCount, int ledsPerFan, IReadOnlyList<RgbColor> colors)
    {
        var leds = new RgbColor[fanCount * ledsPerFan];
        for (int fan = 0; fan < fanCount; fan++)
        {
            RgbColor color = fan < colors.Count ? colors[fan] : default;
            for (int led = 0; led < ledsPerFan; led++)
            {
                leds[(fan * ledsPerFan) + led] = color;
            }
        }

        return leds;
    }

    // Every fan shows the same palette across its slots; slot j takes colours[j]. cycleFill wraps
    // the palette (colours[j % count]) instead of padding past its end with black - AL v2's Meteor
    // modes cycle-fill, every other fan-group mode black-pads.
    internal static RgbColor[] ExpandFanGroup(int fanCount, int slots, IReadOnlyList<RgbColor> colors, bool cycleFill)
    {
        var leds = new RgbColor[fanCount * slots];
        for (int fan = 0; fan < fanCount; fan++)
        {
            for (int slot = 0; slot < slots; slot++)
            {
                RgbColor color;
                if (cycleFill)
                {
                    color = colors.Count > 0 ? colors[slot % colors.Count] : default;
                }
                else
                {
                    color = slot < colors.Count ? colors[slot] : default;
                }

                leds[(fan * slots) + slot] = color;
            }
        }

        return leds;
    }

    // Outer-corner ring (AL / AL v2 colourful outer modes): each fan's 12 outer LEDs split into 4
    // corners of 3 LEDs; corner j shows colours[j], black past the end.
    internal static RgbColor[] ExpandOuterCorner(int fanCount, IReadOnlyList<RgbColor> colors)
    {
        int ledsPerFan = OuterCorners * LedsPerCorner;
        var leds = new RgbColor[fanCount * ledsPerFan];
        for (int fan = 0; fan < fanCount; fan++)
        {
            for (int corner = 0; corner < OuterCorners; corner++)
            {
                RgbColor color = corner < colors.Count ? colors[corner] : default;
                for (int led = 0; led < LedsPerCorner; led++)
                {
                    leds[(fan * ledsPerFan) + (corner * LedsPerCorner) + led] = color;
                }
            }
        }

        return leds;
    }

    private static void AppendPort(ReleasedUniFanLightingProfile profile, List<LightingTransfer> transfers, LightingPortState port)
    {
        // Only a port whose saved mode is in the family lookup is applied; an unrecognised mode
        // leaves that port untouched while the others still apply.
        if (!profile.ModeToWire.TryGetValue(port.Mode, out byte wireMode))
        {
            return;
        }

        RgbColor[] leds = profile.ExpandColors(port.Mode, port.Colors);
        transfers.Add(Output(EncodeColorSetting(port.Port, leds)));
        transfers.Add(Feature(EncodeEffectSetting(
            port.Port, wireMode, port.Speed, port.Direction, NormalizeBrightness(port.Brightness))));
    }

    private static byte NormalizeBrightness(int brightness)
    {
        return brightness == BrightnessLowest ? BrightnessOff : (byte)brightness;
    }

    private static IReadOnlyList<int> NormalizeQuantity(ReleasedUniFanLightingProfile profile, IReadOnlyList<int>? quantity)
    {
        if (quantity is null || quantity.Count != profile.QuantityGroupCount)
        {
            return profile.DefaultQuantity;
        }

        foreach (int value in quantity)
        {
            if (value < 0 || value > profile.MaxQuantity)
            {
                return profile.DefaultQuantity;
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
        new byte[] { ReportId, (byte)(LightingCommand | (port & 0x0F)), wireMode, (byte)speed, (byte)direction, brightness, 0 };

    // Fan-quantity feature report (7 bytes). Packed families put group in the high nibble and
    // quantity in the low nibble of byte[3]; the others send group+1 (1-based) then quantity.
    private static byte[] EncodeQuantity(ReleasedUniFanLightingProfile profile, int group, int quantity)
    {
        if (profile.PackQuantityNibbles)
        {
            return new byte[] { ReportId, LightingCommand, profile.QuantityRegister, (byte)((group << 4) | (quantity & 0x0F)), 0, 0, 0 };
        }

        return new byte[] { ReportId, LightingCommand, profile.QuantityRegister, (byte)(group + 1), (byte)quantity, 0, 0 };
    }

    // Frame-latch feature report: {E0, 0x60, hi, lo} latches the assembled frame to display it
    // (7 bytes). The latch value is 1 on every family except SL v2, which latches 4.
    private static byte[] EncodeFrame(byte frameValue) =>
        new byte[] { ReportId, FrameRegister, (byte)((frameValue >> 8) & 0xFF), (byte)(frameValue & 0xFF), 0, 0, 0 };

    private static LightingTransfer Feature(byte[] report) => new LightingTransfer(true, report);

    private static LightingTransfer Output(byte[] report) => new LightingTransfer(false, report);
}
#endif

#pragma warning restore CA1510, CA1512
