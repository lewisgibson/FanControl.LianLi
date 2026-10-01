using System;
using System.Collections.Generic;

namespace FanControl.LianLi.Protocol;

/// <summary>
/// The colours of one LCD FLEX screen's wireless theme, as L-Connect sends them to the fan in
/// RF command <c>0x28</c> (<c>RFController.UpdateSensorColors</c>): six colours, each as
/// its red, green and blue bytes, chosen from the eight a theme has by
/// <c>RFController.GetWiredlessUserColors</c> according to how many the theme uses
/// (<c>WiredlessFansInfo.iColorNum</c>). The eight come from the fan setting the user saved for
/// the theme (<c>WirelessFanInfoDto</c>, colours packed as <c>0xAARRGGBB</c>) or, with none
/// saved, from L-Connect's own table for the theme (<c>RFController.GetWiredlessThemeDefColors</c>).
/// </summary>
internal sealed class WirelessThemeColours {
    /// <summary>How many colours the command carries for one screen.</summary>
    public const int ColourCount = 6;

    /// <summary>How many bytes the six colours take: red, green and blue each.</summary>
    public const int Length = ColourCount * 3;

    // GetWiredlessThemeDefColors' table, by theme index: a row of four is the graph colour, the
    // title, the data and the unit; of five the two graph colours and the three fonts; of six the
    // three fonts and their three shadows; of one (themes 13 to 18) nothing, every colour black.
    // A theme past the table has every colour black too (a fresh WiredlessFansInfo).
    private static readonly uint[][] Defaults = {
        new uint[] { 11088639u, 16777215u, 16777215u, 16777215u, 16777215u },
        new uint[] { 1260991u, 6922239u, 16777215u, 16777215u, 16777215u },
        new uint[] { 16744703u, 11698431u, 5905693u, 5905693u, 5905693u },
        new uint[] { 64507u, 16777215u, 16777215u, 16777215u },
        new uint[] { 10092543u, 16770093u, 16777215u, 16777215u, 16777215u },
        new uint[] { 10299332u, 3405311u, 16766718u, 14089727u, 16766718u },
        new uint[] { 10038883u, 16777215u, 16777215u, 16777215u },
        new uint[] { 65445u, 35071u, 16777215u, 35071u, 16777215u },
        new uint[] { 16711680u, 16777215u, 16777215u, 16777215u },
        new uint[] { 255u, 16777215u, 16777215u, 16777215u },
        new uint[] { 255u, 16777215u, 16777215u, 16777215u },
        new uint[] { 16711680u, 65280u, 16777215u, 16777215u, 16777215u },
        new uint[] { 16711830u, 16711680u, 0u, 0u, 0u },
        new uint[1],
        new uint[1],
        new uint[1],
        new uint[1],
        new uint[1],
        new uint[1],
        new uint[] { 16777215u, 16777215u, 16777215u, 0u, 0u, 0u },
        new uint[] { 3355443u, 3355443u, 3355443u, 16777215u, 16777215u, 16777215u },
        new uint[] { 16777215u, 16777215u, 16777215u, 2495249u, 2495249u, 2495249u },
        new uint[] { 16777215u, 16777215u, 16777215u, 16777215u, 16777215u, 16777215u },
        new uint[] { 3158064u, 3158064u, 3158064u, 16512752u, 16512752u, 16512752u },
        new uint[] { 3158064u, 3158064u, 3158064u, 16644338u, 16644338u, 16644338u },
        new uint[] { 16777215u, 16777215u, 16777215u, 16777215u, 16777215u, 16777215u },
        new uint[] { 16777215u, 16777215u, 16777215u, 16777215u, 16777215u, 16777215u },
        new uint[] { 16777215u, 16777215u, 16777215u, 0u, 0u, 0u },
        new uint[] { 16777215u, 16777215u, 16777215u, 0u, 0u, 0u },
    };

    private readonly uint[] _colours;

    private WirelessThemeColours(uint[] colours) {
        _colours = colours;
    }

    /// <summary>The six colours the command carries, packed as <c>0xAARRGGBB</c>, in the command's order; fewer than six for a theme that uses fewer, and none for one that uses none.</summary>
    public IReadOnlyList<uint> Colours => _colours;

    /// <summary>
    /// The colours a theme has when the user saved no fan setting for it: L-Connect's table for
    /// the theme, arranged as the command wants them.
    /// </summary>
    public static WirelessThemeColours ForTheme(int theme) {
        uint[] defaults = DefaultsOf(theme);
        return FromSaved(theme, defaults[0], defaults[1], defaults[2], defaults[3], defaults[4], defaults[5], defaults[6], defaults[7]);
    }

    /// <summary>
    /// The colours of a saved fan setting for <paramref name="theme"/>, arranged as
    /// <c>GetWiredlessUserColors</c> arranges them by how many colours the theme uses
    /// (<c>iColorNum</c>): five (themes 0, 1, 2, 4, 5, 7, 11, 12 and any past the table) sends the
    /// two graph colours, the title, the data and the unit, and the unit again; four (3, 6, 8, 9,
    /// 10) the first graph colour, the three fonts, then the data and unit shadows; three (19 to
    /// 28) the three fonts, then the three shadows of L-Connect's own table for the theme, never
    /// the saved ones; none (13 to 18) sends no colour at all.
    /// </summary>
    public static WirelessThemeColours FromSaved(
        int theme, uint graph1, uint graph2, uint title, uint data, uint unit, uint titleShadow, uint dataShadow, uint unitShadow) {
        switch (ColourCountOf(theme)) {
            case 5:
                return new WirelessThemeColours(new[] { graph1, graph2, title, data, unit, unit });
            case 4:
                return new WirelessThemeColours(new[] { graph1, title, data, unit, dataShadow, unitShadow });
            case 3:
                uint[] defaults = DefaultsOf(theme);
                return new WirelessThemeColours(new[] { title, data, unit, defaults[5], defaults[6], defaults[7] });
            default:
                return new WirelessThemeColours(Array.Empty<uint>());
        }
    }

    /// <summary>
    /// The colours as the command's slot carries them: red, green and blue of each in turn,
    /// <see cref="Length"/> bytes, zero past the colours the theme uses (<c>UpdateSensorColors</c>
    /// writes into a fresh buffer).
    /// </summary>
    public byte[] ToBytes() {
        var bytes = new byte[Length];
        for (int i = 0; i < _colours.Length; i++) {
            bytes[i * 3] = (byte)((_colours[i] >> 16) & 0xFF);
            bytes[(i * 3) + 1] = (byte)((_colours[i] >> 8) & 0xFF);
            bytes[(i * 3) + 2] = (byte)(_colours[i] & 0xFF);
        }

        return bytes;
    }

    // WiredlessFansInfo.iColorNum.
    private static int ColourCountOf(int theme) {
        switch (theme) {
            case 3:
            case 6:
            case 8:
            case 9:
            case 10:
                return 4;
            case 13:
            case 14:
            case 15:
            case 16:
            case 17:
            case 18:
                return 0;
            case 19:
            case 20:
            case 21:
            case 22:
            case 23:
            case 24:
            case 25:
            case 26:
            case 27:
            case 28:
                return 3;
            default:
                return 5;
        }
    }

    // GetWiredlessThemeDefColors' WiredlessFansInfo for the theme, as its eight colours in the
    // saved setting's order: graph 1, graph 2, title, data, unit, title shadow, data shadow, unit
    // shadow; a colour the row does not set is black.
    private static uint[] DefaultsOf(int theme) {
        var colours = new uint[8];
        if (theme < 0 || theme >= Defaults.Length) {
            return colours;
        }

        uint[] row = Defaults[theme];
        switch (row.Length) {
            case 4:
                colours[0] = row[0];
                colours[2] = row[1];
                colours[3] = row[2];
                colours[4] = row[3];
                break;
            case 5:
                colours[0] = row[0];
                colours[1] = row[1];
                colours[2] = row[2];
                colours[3] = row[3];
                colours[4] = row[4];
                break;
            case 6:
                colours[2] = row[0];
                colours[3] = row[1];
                colours[4] = row[2];
                colours[5] = row[3];
                colours[6] = row[4];
                colours[7] = row[5];
                break;
        }

        return colours;
    }
}
