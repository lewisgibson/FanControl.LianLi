namespace FanControl.LianLi.Protocol;

/// <summary>
/// How a wireless water block's screen is set up: every part of its parameter block that is a
/// saved setting rather than the pump or a live reading. The block is written as one unit, so
/// driving the pump means restating all of this - the plugin carries the user's own settings
/// through rather than flattening their screen (see <see cref="WirelessProtocol.EncodeAioParameters"/>).
/// Each value is narrowed to its byte exactly as L-Connect's <c>RFController.SetAioParams</c>
/// narrows it (a plain cast), and the theme as <c>RFController.SetAioThemeIndex</c> accepts it.
/// </summary>
internal sealed class WirelessAioPresentation {
    // RFController.SetAioThemeIndex ignores an index outside 0-12, leaving the theme at 0.
    private const int HighestThemeIndex = 12;

    /// <summary>The colour of one screen element, as the parameter block carries it.</summary>
    internal readonly struct Argb {
        /// <summary>Create a colour from its four components.</summary>
        public Argb(byte alpha, byte red, byte green, byte blue) {
            Alpha = alpha;
            Red = red;
            Green = green;
            Blue = blue;
        }

        /// <summary>Opaque white, which is what L-Connect starts every element at.</summary>
        public static Argb White => new Argb(0xFF, 0xFF, 0xFF, 0xFF);

        /// <summary>The alpha component.</summary>
        public byte Alpha { get; }

        /// <summary>The red component.</summary>
        public byte Red { get; }

        /// <summary>The green component.</summary>
        public byte Green { get; }

        /// <summary>The blue component.</summary>
        public byte Blue { get; }
    }

    /// <summary>
    /// Create a presentation from L-Connect's saved values (<c>AioParams</c>, <c>WirelessTemplateIndex</c>
    /// and <c>IsAdvanceMode</c>). A screen in advance mode plays content streamed from the PC and
    /// L-Connect never applies the wireless theme to it (<c>LWirelessController</c> calls
    /// <c>applyWirelessMode</c> only when <c>IsAdvanceMode</c> is false), so its theme is 0, as
    /// L-Connect's own parameter block leaves it.
    /// </summary>
    public WirelessAioPresentation(
        int refreshInterval,
        bool pumpTemperatureShown,
        int fanSpeed,
        bool fanSpeedShown,
        int brightness,
        int themeIndex,
        int rotation,
        Argb title,
        Argb value,
        Argb unit,
        bool advanceMode) {
        RefreshInterval = unchecked((byte)refreshInterval);
        PumpTemperatureShown = pumpTemperatureShown;
        FanSpeed = fanSpeed;
        FanSpeedShown = fanSpeedShown;
        Brightness = unchecked((byte)brightness);
        ThemeIndex = !advanceMode && themeIndex >= 0 && themeIndex <= HighestThemeIndex ? (byte)themeIndex : (byte)0;
        AdvanceMode = advanceMode;
        Rotation = unchecked((byte)rotation);
        Title = title;
        Value = value;
        Unit = unit;
    }

    /// <summary>
    /// What L-Connect's service sets up a newly bound water block with
    /// (<c>LWirelessController.addSettingDevice</c>): refresh interval 2, the pump temperature shown,
    /// fan speed 2000 hidden, white text, full brightness, theme 0, no rotation, out of advance mode.
    /// </summary>
    public static WirelessAioPresentation Default { get; } =
        new WirelessAioPresentation(2, true, 2000, false, 100, 0, 0, Argb.White, Argb.White, Argb.White, false);

    /// <summary>How often the screen cycles its figures (<c>AioParams.LoopInterval</c>).</summary>
    public byte RefreshInterval { get; }

    /// <summary>Whether the screen shows the coolant (pump) temperature (<c>AioParams.PumpEnable</c>, byte 7).</summary>
    public bool PumpTemperatureShown { get; }

    /// <summary>The fan speed figure (<c>AioParams.FanSpeed</c>), a fixed saved value in L-Connect.</summary>
    public int FanSpeed { get; }

    /// <summary>Whether the screen shows the fan speed figure (<c>AioParams.FanSpeedEnable</c>).</summary>
    public bool FanSpeedShown { get; }

    /// <summary>Screen brightness, 0-100.</summary>
    public byte Brightness { get; }

    /// <summary>Which screen theme the block shows.</summary>
    public byte ThemeIndex { get; }

    /// <summary>
    /// Whether the screen is saved in advance mode, playing content streamed from the PC; out of it,
    /// the screen is switched to its wireless theme (<c>LWirelessController.applyWirelessMode</c>).
    /// </summary>
    public bool AdvanceMode { get; }

    /// <summary>Screen rotation.</summary>
    public byte Rotation { get; }

    /// <summary>The colour of a figure's title.</summary>
    public Argb Title { get; }

    /// <summary>The colour of a figure's value.</summary>
    public Argb Value { get; }

    /// <summary>The colour of a figure's unit.</summary>
    public Argb Unit { get; }
}
