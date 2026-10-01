using System.Linq;
using FanControl.LianLi.Protocol;
using Xunit;

namespace FanControl.LianLi.Tests.Protocol;

/// <summary>
/// The six colours RF command 0x28 carries for one LCD FLEX screen's theme: chosen from the eight
/// a theme has as RFController.GetWiredlessUserColors chooses them, from the saved fan setting or
/// from L-Connect's own table for the theme (GetWiredlessThemeDefColors).
/// </summary>
public sealed class WirelessThemeColoursTests {
    // GetWiredlessThemeDefColors' rows: four values (theme 3) are graph 1 and the three fonts,
    // five (theme 0) the two graphs and the fonts, six (theme 19) the fonts and their shadows, one
    // (theme 13) nothing; a theme past the table has every colour black.
    [Fact]
    public void ForTheme_ArrangesLConnectsOwnRowAsTheCommandWantsIt() {
        Assert.Equal(new uint[] { 64507, 16777215, 16777215, 16777215, 0, 0 }, WirelessThemeColours.ForTheme(3).Colours);
        Assert.Equal(new uint[] { 11088639, 16777215, 16777215, 16777215, 16777215, 16777215 }, WirelessThemeColours.ForTheme(0).Colours);
        Assert.Equal(new uint[] { 16777215, 16777215, 16777215, 0, 0, 0 }, WirelessThemeColours.ForTheme(19).Colours);
        Assert.Equal(new uint[] { 3158064, 3158064, 3158064, 16512752, 16512752, 16512752 }, WirelessThemeColours.ForTheme(23).Colours);
        Assert.Empty(WirelessThemeColours.ForTheme(13).Colours);
        Assert.Equal(new uint[] { 0, 0, 0, 0, 0, 0 }, WirelessThemeColours.ForTheme(29).Colours);
        Assert.Equal(new uint[] { 0, 0, 0, 0, 0, 0 }, WirelessThemeColours.ForTheme(255).Colours);
        Assert.Equal(new uint[] { 0, 0, 0, 0, 0, 0 }, WirelessThemeColours.ForTheme(-1).Colours);
    }

    // GetWiredlessUserColors by iColorNum: five sends graph 1, graph 2, title, data, unit and the
    // unit again; four graph 1, the three fonts and the data and unit shadows; three the fonts
    // and the theme's own shadows, never the saved ones; none nothing at all.
    [Theory]
    [InlineData(0, new uint[] { 1, 2, 3, 4, 5, 5 })]
    [InlineData(12, new uint[] { 1, 2, 3, 4, 5, 5 })]
    [InlineData(30, new uint[] { 1, 2, 3, 4, 5, 5 })]
    [InlineData(3, new uint[] { 1, 3, 4, 5, 7, 8 })]
    [InlineData(10, new uint[] { 1, 3, 4, 5, 7, 8 })]
    [InlineData(19, new uint[] { 3, 4, 5, 0, 0, 0 })]
    [InlineData(20, new uint[] { 3, 4, 5, 16777215, 16777215, 16777215 })]
    [InlineData(28, new uint[] { 3, 4, 5, 0, 0, 0 })]
    [InlineData(13, new uint[0])]
    [InlineData(18, new uint[0])]
    public void FromSaved_ChoosesTheColoursTheThemeUses(int theme, uint[] expected)
        => Assert.Equal(expected, WirelessThemeColours.FromSaved(theme, 1, 2, 3, 4, 5, 6, 7, 8).Colours);

    // Red, green and blue of each packed 0xAARRGGBB in turn, zero past the colours the theme uses.
    [Fact]
    public void ToBytes_IsRedGreenBlueOfEachColour_ZeroPastThem() {
        Assert.Equal(18, WirelessThemeColours.Length);
        Assert.Equal(
            new byte[] { 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            WirelessThemeColours.FromSaved(19, 0, 0, 0xFFFF0000, 0x0000FF00, 0x000000FF, 0, 0, 0).ToBytes());
        Assert.Equal(new byte[18], WirelessThemeColours.ForTheme(13).ToBytes());
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, WirelessThemeColours.FromSaved(28, 0, 0, 0x00102030, 0, 0, 0, 0, 0).ToBytes().Take(3));
    }
}
