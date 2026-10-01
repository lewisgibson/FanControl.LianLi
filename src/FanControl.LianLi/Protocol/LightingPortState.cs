#if ENABLE_LIGHTING
using System;
using System.Collections.Generic;

namespace FanControl.LianLi.Protocol;

/// <summary>
/// One controller port's saved lighting look, as read from L-Connect's configuration:
/// the effect mode and its parameters and the per-fan colours. <see cref="Mode"/>,
/// <see cref="Speed"/>, <see cref="Direction"/>, and <see cref="Brightness"/> are the raw
/// integer enum values L-Connect stores; <see cref="SlInfinityLightingEncoder"/> translates
/// the mode to its wire byte and expands the colours per the family's LED layout.
/// </summary>
internal sealed class LightingPortState
{
    /// <summary>Create the saved state for one port.</summary>
    public LightingPortState(int port, int mode, int speed, int direction, int brightness, IReadOnlyList<RgbColor> colors)
    {
        Port = port;
        Mode = mode;
        Speed = speed;
        Direction = direction;
        Brightness = brightness;
        Colors = colors ?? throw new ArgumentNullException(nameof(colors));
    }

    /// <summary>The controller port this look applies to (0-7 on SL-Infinity).</summary>
    public int Port { get; }

    /// <summary>Raw L-Connect <c>LightingMode</c> enum value (translated to a wire byte by the encoder).</summary>
    public int Mode { get; }

    /// <summary>Raw L-Connect <c>LightingSpeed</c> enum value, sent through as the effect speed byte.</summary>
    public int Speed { get; }

    /// <summary>Raw L-Connect <c>LightingDirection</c> enum value, sent through as the effect direction byte.</summary>
    public int Direction { get; }

    /// <summary>Raw L-Connect <c>LightingBrightness</c> enum value (the encoder maps Lowest to Off).</summary>
    public int Brightness { get; }

    /// <summary>The per-fan UI colours L-Connect saved (the encoder expands these per the LED layout).</summary>
    public IReadOnlyList<RgbColor> Colors { get; }
}
#endif
