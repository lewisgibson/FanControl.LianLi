using FanControl.LianLi.Protocol;
using Xunit;

namespace FanControl.LianLi.Tests.Protocol;

/// <summary>
/// The saved screen settings a pump write restates, narrowed to their bytes as
/// RFController.SetAioParams narrows them and with the theme as SetAioThemeIndex accepts it.
/// </summary>
public sealed class WirelessAioPresentationTests {
    private static readonly WirelessAioPresentation.Argb Red = new WirelessAioPresentation.Argb(1, 2, 3, 4);

    private static WirelessAioPresentation Presentation(int interval = 2, int brightness = 100, int theme = 0, int rotation = 0)
        => new WirelessAioPresentation(interval, true, 1500, true, brightness, theme, rotation, Red, Red, Red, false);

    [Fact]
    public void Constructor_KeepsEverySetting() {
        var presentation = new WirelessAioPresentation(
            3, false, 1500, true, 70, 5, 2, Red, new WirelessAioPresentation.Argb(5, 6, 7, 8), WirelessAioPresentation.Argb.White, false);

        Assert.False(presentation.AdvanceMode);
        Assert.Equal(3, presentation.RefreshInterval);
        Assert.False(presentation.PumpTemperatureShown);
        Assert.Equal(1500, presentation.FanSpeed);
        Assert.True(presentation.FanSpeedShown);
        Assert.Equal(70, presentation.Brightness);
        Assert.Equal(5, presentation.ThemeIndex);
        Assert.Equal(2, presentation.Rotation);
        Assert.Equal(1, presentation.Title.Alpha);
        Assert.Equal(2, presentation.Title.Red);
        Assert.Equal(3, presentation.Title.Green);
        Assert.Equal(4, presentation.Title.Blue);
        Assert.Equal(8, presentation.Value.Blue);
        Assert.Equal(0xFF, presentation.Unit.Alpha);
    }

    // A screen in advance mode never has the wireless theme applied, so its theme is 0.
    [Fact]
    public void Constructor_InAdvanceMode_HasNoTheme() {
        var presentation = new WirelessAioPresentation(2, true, 1500, true, 100, 5, 0, Red, Red, Red, true);

        Assert.True(presentation.AdvanceMode);
        Assert.Equal(0, presentation.ThemeIndex);
    }

    [Fact]
    public void Default_IsOutOfAdvanceMode() => Assert.False(WirelessAioPresentation.Default.AdvanceMode);

    // SetAioParams casts: (byte)loopInterval, (byte)lcdBrightness, (byte)rotation.
    [Fact]
    public void Constructor_NarrowsLikeACast() {
        WirelessAioPresentation presentation = Presentation(interval: 258, brightness: -1, rotation: 260);

        Assert.Equal(2, presentation.RefreshInterval);
        Assert.Equal(255, presentation.Brightness);
        Assert.Equal(4, presentation.Rotation);
    }

    // SetAioThemeIndex ignores an index outside 0-12.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(12, 12)]
    [InlineData(13, 0)]
    [InlineData(-1, 0)]
    public void Constructor_KeepsOnlyAThemeLConnectAccepts(int theme, int expected)
        => Assert.Equal(expected, Presentation(theme: theme).ThemeIndex);

    // LWirelessController.addSettingDevice's new AioParams.
    [Fact]
    public void Default_IsTheScreenLConnectStartsABlockWith() {
        WirelessAioPresentation presentation = WirelessAioPresentation.Default;

        Assert.Equal(2, presentation.RefreshInterval);
        Assert.True(presentation.PumpTemperatureShown);
        Assert.Equal(2000, presentation.FanSpeed);
        Assert.False(presentation.FanSpeedShown);
        Assert.Equal(100, presentation.Brightness);
        Assert.Equal(0, presentation.ThemeIndex);
        Assert.Equal(0, presentation.Rotation);
        Assert.Equal(0xFF, presentation.Title.Red);
    }
}
