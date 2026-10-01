#if ENABLE_LIGHTING
using System;
using System.Collections.Generic;

namespace FanControl.LianLi.Protocol;

/// <summary>
/// The Galahad II AIO pump cap's saved lighting look. The pump light leads with a scope byte
/// (inner / outer / all) the fan light does not, and its mode is taken modulo 1000 on the wire (the
/// thousands band encodes the scope, which travels separately in <see cref="Scope"/>).
/// <see cref="Speed"/>, <see cref="Direction"/>, and <see cref="Brightness"/> are raw L-Connect enum
/// values; colours are written R,G,B.
/// </summary>
internal sealed class Galahad2PumpLightingState
{
    /// <summary>Create the saved pump-light state.</summary>
    public Galahad2PumpLightingState(int scope, int mode, int speed, int direction, int brightness, IReadOnlyList<RgbColor> colors)
    {
        Scope = scope;
        Mode = mode;
        Speed = speed;
        Direction = direction;
        Brightness = brightness;
        Colors = colors ?? throw new ArgumentNullException(nameof(colors));
    }

    /// <summary>
    /// The look L-Connect's <c>Galahad2TrinityController</c> starts with and writes when no pump light
    /// was ever saved (<c>new PumpLightingSetting()</c>): scope All (2), Rainbow (2001, wire 1),
    /// brightness 2, speed 2, clockwise (0), no colours.
    /// </summary>
    public static Galahad2PumpLightingState LConnectDefault { get; } = new Galahad2PumpLightingState(
        scope: 2, mode: 2001, speed: 2, direction: 0, brightness: 2, Array.Empty<RgbColor>());

    /// <summary>Pump LED scope: 0 inner, 1 outer, 2 all.</summary>
    public int Scope { get; }

    /// <summary>Raw L-Connect pump lighting-mode value (the encoder takes it modulo 1000 for the wire).</summary>
    public int Mode { get; }

    /// <summary>Raw L-Connect speed value.</summary>
    public int Speed { get; }

    /// <summary>Raw L-Connect direction value (0-5).</summary>
    public int Direction { get; }

    /// <summary>Raw L-Connect brightness value.</summary>
    public int Brightness { get; }

    /// <summary>The saved colours; the encoder writes up to four in R,G,B order.</summary>
    public IReadOnlyList<RgbColor> Colors { get; }
}
#endif
