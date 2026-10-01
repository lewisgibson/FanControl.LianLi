using System;
using FanControl.LianLi.Protocol;
using Xunit;

namespace FanControl.LianLi.Tests.Protocol;

/// <summary>How an LCD FLEX group's saved screen settings become the clock broadcast's table entry.</summary>
public sealed class WirelessFanScreenPresentationTests {
    [Fact]
    public void CarriesEveryFansThemeAndSourceTheBrightnessAndTheDirection() {
        var presentation = new WirelessFanScreenPresentation(new byte[] { 1, 2, 3, 4 }, new byte[] { 5, 6, 7, 8 }, 90, 3, false);

        Assert.Equal(1, presentation.ThemeOf(0));
        Assert.Equal(4, presentation.ThemeOf(3));
        Assert.Equal(5, presentation.DataSourceOf(0));
        Assert.Equal(8, presentation.DataSourceOf(3));
        Assert.Equal(90, presentation.Brightness);
        Assert.Equal(3, presentation.Direction);
        Assert.False(presentation.AdvanceMode);
        Assert.True(new WirelessFanScreenPresentation(new byte[4], new byte[4], 60, 0, true).AdvanceMode);
    }

    // applyWirelessLCDMode: FanBrightness = brightness > 0 ? brightness : 60.
    [Theory]
    [InlineData(0, 60)]
    [InlineData(1, 1)]
    [InlineData(100, 100)]
    [InlineData(255, 255)]
    public void AZeroBrightness_IsSixty(int saved, int expected)
        => Assert.Equal((byte)expected, new WirelessFanScreenPresentation(new byte[4], new byte[4], saved, 0, false).Brightness);

    // InitSensorDataByWiredLess's entry for a group with nothing saved: 255, 0, brightness 60, direction 0.
    [Fact]
    public void Default_IsLConnectsEntryForAGroupWithNothingSaved() {
        WirelessFanScreenPresentation presentation = WirelessFanScreenPresentation.Default;

        for (int fan = 0; fan < 4; fan++) {
            Assert.Equal(255, presentation.ThemeOf(fan));
            Assert.Equal(0, presentation.DataSourceOf(fan));
        }

        Assert.Equal(60, presentation.Brightness);
        Assert.Equal(0, presentation.Direction);
        Assert.False(presentation.AdvanceMode);
    }

    // toWirelessFanDirection((4 - rotation) % 4 + 1) on normalizeWirelessRotation's 0-3.
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 4)]
    [InlineData(2, 3)]
    [InlineData(3, 2)]
    [InlineData(-1, 1)]
    [InlineData(4, 2)]
    [InlineData(255, 2)]
    public void DirectionFromRotation_IsLConnectsFormulaOnTheClampedRotation(int rotation, int expected)
        => Assert.Equal((byte)expected, WirelessFanScreenPresentation.DirectionFromRotation(rotation));

    // Each screen's theme gets L-Connect's own colours (GetWiredlessThemeDefColors) unless the
    // saved fan setting's are given for it.
    [Fact]
    public void ColoursOf_AreLConnectsOwnForTheTheme_UnlessGiven() {
        var presentation = new WirelessFanScreenPresentation(new byte[] { 3, 5, 13, 40 }, new byte[4], 60, 1, false);

        Assert.Equal(WirelessThemeColours.ForTheme(3).Colours, presentation.ColoursOf(0).Colours);
        Assert.Equal(WirelessThemeColours.ForTheme(5).Colours, presentation.ColoursOf(1).Colours);
        Assert.Empty(presentation.ColoursOf(2).Colours);
        Assert.Equal(new uint[] { 0, 0, 0, 0, 0, 0 }, presentation.ColoursOf(3).Colours);

        WirelessThemeColours saved = WirelessThemeColours.FromSaved(3, 1, 2, 3, 4, 5, 6, 7, 8);
        var given = new WirelessFanScreenPresentation(
            new byte[] { 3, 5, 13, 40 }, new byte[4], 60, 1, false, new[] { saved, saved, saved, saved });
        Assert.Same(saved, given.ColoursOf(0));
        Assert.Throws<ArgumentException>(() => new WirelessFanScreenPresentation(new byte[4], new byte[4], 60, 1, false, new[] { saved }));
    }

    [Fact]
    public void Constructor_RejectsMissingOrMisSizedValues() {
        Assert.Throws<ArgumentNullException>(() => new WirelessFanScreenPresentation(null!, new byte[4], 60, 0, false));
        Assert.Throws<ArgumentNullException>(() => new WirelessFanScreenPresentation(new byte[4], null!, 60, 0, false));
        Assert.Throws<ArgumentException>(() => new WirelessFanScreenPresentation(new byte[3], new byte[4], 60, 0, false));
        Assert.Throws<ArgumentException>(() => new WirelessFanScreenPresentation(new byte[4], new byte[5], 60, 0, false));
    }
}
