using System;

namespace FanControl.LianLi.Protocol;

/// <summary>
/// How the screens of a wireless LCD FLEX fan group (TL FLEX LCD, SL-INF FLEX LCD) are set up: the
/// theme and data source each fan's screen shows, the brightness, and which way the screens face.
/// L-Connect puts these into a per-receiver-slot table inside the clock broadcast, from what the
/// user saved for the group (<c>LWirelessLCDConfig.TemplateParams</c>, applied by
/// <c>LWirelessController.applyWirelessLCDMode</c>); the plugin carries the saved values through
/// the same table instead of blanking the screens (see <see cref="WirelessScreenTable"/>).
/// </summary>
internal sealed class WirelessFanScreenPresentation {
    /// <summary>The theme byte L-Connect writes for a fan it has no saved theme for: no wireless theme.</summary>
    public const byte NoTheme = 255;

    /// <summary>The brightness L-Connect writes for a fan with no saved brightness, and for a saved brightness of 0.</summary>
    public const byte DefaultBrightness = 60;

    // A group has at most four screens, one per slot.
    private readonly byte[] _themes;
    private readonly byte[] _dataSources;
    private readonly WirelessThemeColours[] _colours;

    /// <summary>
    /// Create a presentation: the theme and data source per fan (in the service's fan order, index
    /// 0-3), the brightness, the direction byte the screens are told to face, and whether the group
    /// is saved in advance mode (<c>LWirelessLCDConfig.IsAdvanceMode</c>: its screens play content
    /// streamed from the PC, and L-Connect does not switch them onto their wireless themes). A
    /// brightness of 0 is sent as 60, as <c>applyWirelessLCDMode</c> sends it. Each screen's theme
    /// gets L-Connect's own colours for it, as a fan with no saved setting does.
    /// </summary>
    public WirelessFanScreenPresentation(byte[] themes, byte[] dataSources, int brightness, byte direction, bool advanceMode)
        : this(themes, dataSources, brightness, direction, advanceMode, null) {
    }

    /// <summary>
    /// As the other constructor, with the colours of each screen's theme (in the service's fan
    /// order, index 0-3) from the fan settings the user saved
    /// (<see cref="WirelessThemeColours.FromSaved"/>); null gives every screen L-Connect's own
    /// colours for its theme.
    /// </summary>
    public WirelessFanScreenPresentation(
        byte[] themes, byte[] dataSources, int brightness, byte direction, bool advanceMode, WirelessThemeColours[]? colours) {
        _themes = Copy(themes, nameof(themes));
        _dataSources = Copy(dataSources, nameof(dataSources));
        Brightness = brightness > 0 ? unchecked((byte)brightness) : DefaultBrightness;
        Direction = direction;
        AdvanceMode = advanceMode;
        _colours = new WirelessThemeColours[WirelessProtocol.SlotsPerGroup];
        if (colours != null && colours.Length != WirelessProtocol.SlotsPerGroup) {
            throw new ArgumentException("Expected one value per slot (" + WirelessProtocol.SlotsPerGroup + ").", nameof(colours));
        }

        for (int fan = 0; fan < WirelessProtocol.SlotsPerGroup; fan++) {
            _colours[fan] = colours?[fan] ?? WirelessThemeColours.ForTheme(_themes[fan]);
        }
    }

    /// <summary>
    /// What L-Connect's <c>RFController.InitSensorDataByWiredLess</c> writes for a bound LCD FLEX group
    /// it has no saved settings for: no theme (255), data source 0, brightness 60, direction 0.
    /// </summary>
    public static WirelessFanScreenPresentation Default { get; } = new WirelessFanScreenPresentation(
        new[] { NoTheme, NoTheme, NoTheme, NoTheme }, new byte[WirelessProtocol.SlotsPerGroup], DefaultBrightness, 0, advanceMode: false);

    /// <summary>The screen brightness, 1-255.</summary>
    public byte Brightness { get; }

    /// <summary>
    /// The direction byte, which shares a byte with each fan's data source in the table (the
    /// direction in the top three bits). See <see cref="DirectionFromRotation"/>.
    /// </summary>
    public byte Direction { get; }

    /// <summary>
    /// Whether the group is saved in advance mode. Its screens then play content streamed from the
    /// PC, and L-Connect neither applies its template (<c>applyConfiguredWirelessLCDModes</c> skips
    /// it) nor switches its screens onto their wireless themes.
    /// </summary>
    public bool AdvanceMode { get; }

    /// <summary>The theme index of the screen on the fan at <paramref name="fanIndex"/> (0-3).</summary>
    public byte ThemeOf(int fanIndex) => _themes[fanIndex];

    /// <summary>The data source index of the screen on the fan at <paramref name="fanIndex"/> (0-3).</summary>
    public byte DataSourceOf(int fanIndex) => _dataSources[fanIndex];

    /// <summary>The colours of the theme of the screen on the fan at <paramref name="fanIndex"/> (0-3), as RF command <c>0x28</c> carries them.</summary>
    public WirelessThemeColours ColoursOf(int fanIndex) => _colours[fanIndex];

    /// <summary>
    /// The direction byte for a saved rotation, as <c>LWirelessController.toWirelessFanDirection</c>
    /// computes it from the rotation <c>normalizeWirelessRotation</c> clamps to 0-3:
    /// <c>(4 - rotation) % 4 + 1</c>, so 1-4. L-Connect's saved rotation defaults to 3.
    /// </summary>
    public static byte DirectionFromRotation(int rotation) {
        int clamped = Math.Max(0, Math.Min(3, rotation));
        return (byte)(((4 - clamped) % 4) + 1);
    }

    private static byte[] Copy(byte[] source, string name) {
        if (source is null) {
            throw new ArgumentNullException(name);
        }

        if (source.Length != WirelessProtocol.SlotsPerGroup) {
            throw new ArgumentException("Expected one value per slot (" + WirelessProtocol.SlotsPerGroup + ").", name);
        }

        return (byte[])source.Clone();
    }
}
