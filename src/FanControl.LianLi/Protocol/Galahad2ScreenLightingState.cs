#if ENABLE_LIGHTING
using System;
using System.Collections.Generic;

namespace FanControl.LianLi.Protocol;

/// <summary>
/// The Galahad II Vision's saved screen-ring look (L-Connect's <c>ScreenLEDLighting</c> setting):
/// the twelve LEDs around the LCD, which the cooler drives through the same 0x83 command as the
/// Trinity's pump cap. <see cref="Mode"/> and <see cref="Direction"/> are the raw L-Connect enum
/// values; <see cref="Speed"/> and <see cref="Brightness"/> are the raw 0 to 100 slider values of
/// the static look (or <see cref="int.MinValue"/> when L-Connect saved them unset), which
/// <see cref="Galahad2LightingEncoder"/> scales to the wire. A dynamic look
/// (<see cref="IsDynamicMode"/>) is one L-Connect recolours every second from a live sensor
/// reading; the plugin has no such reading, so it carries the flag and leaves the ring alone.
/// </summary>
internal sealed class Galahad2ScreenLightingState
{
    /// <summary>Create the saved screen-ring state.</summary>
    public Galahad2ScreenLightingState(int mode, bool isDynamicMode, int speed, int brightness, int direction, IReadOnlyList<RgbColor> colors)
    {
        Mode = mode;
        IsDynamicMode = isDynamicMode;
        Speed = speed;
        Brightness = brightness;
        Direction = direction;
        Colors = colors ?? throw new ArgumentNullException(nameof(colors));
    }

    /// <summary>Raw L-Connect screen-ring lighting-mode value, used as the wire byte directly.</summary>
    public int Mode { get; }

    /// <summary>True when the ring follows a live sensor in L-Connect; the plugin then leaves it as found.</summary>
    public bool IsDynamicMode { get; }

    /// <summary>The static look's raw speed slider (0 to 100 in steps of 25, or <see cref="int.MinValue"/> when unset).</summary>
    public int Speed { get; }

    /// <summary>The static look's raw brightness slider (0 to 100 in steps of 25, or <see cref="int.MinValue"/> when unset).</summary>
    public int Brightness { get; }

    /// <summary>Raw L-Connect direction value (0-5).</summary>
    public int Direction { get; }

    /// <summary>The static look's saved colours; the encoder writes up to four in R,G,B order.</summary>
    public IReadOnlyList<RgbColor> Colors { get; }
}
#endif
