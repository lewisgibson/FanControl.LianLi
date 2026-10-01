#if ENABLE_LIGHTING
using System;
using System.Collections.Generic;

namespace FanControl.LianLi.Protocol;

/// <summary>
/// The Galahad II AIO fan ring's saved lighting look. The fan light carries an LED count and a
/// sync-to-pump flag the pump light does not. <see cref="Mode"/>, <see cref="Speed"/>,
/// <see cref="Direction"/>, and <see cref="Brightness"/> are the raw L-Connect enum values; unlike
/// the pump, the fan mode is the wire byte directly (no modulo). Colours are written R,G,B.
/// </summary>
internal sealed class Galahad2FanLightingState
{
    /// <summary>Create the saved fan-light state.</summary>
    public Galahad2FanLightingState(int mode, int speed, int direction, int brightness, int numberOfLed, bool syncToPump, IReadOnlyList<RgbColor> colors)
    {
        Mode = mode;
        Speed = speed;
        Direction = direction;
        Brightness = brightness;
        NumberOfLed = numberOfLed;
        SyncToPump = syncToPump;
        Colors = colors ?? throw new ArgumentNullException(nameof(colors));
    }

    /// <summary>
    /// The look L-Connect's <c>Galahad2TrinityController</c> starts with and writes when no fan light
    /// was ever saved (<c>new FanLightingSetting()</c>): Rainbow (1), brightness 2, speed 2, clockwise
    /// (0), 24 LEDs, not synced to the pump, no colours.
    /// </summary>
    public static Galahad2FanLightingState LConnectDefault { get; } = new Galahad2FanLightingState(
        mode: 1, speed: 2, direction: 0, brightness: 2, numberOfLed: 24, syncToPump: false, Array.Empty<RgbColor>());

    /// <summary>Raw L-Connect fan lighting-mode value, used as the wire byte directly.</summary>
    public int Mode { get; }

    /// <summary>Raw L-Connect speed value.</summary>
    public int Speed { get; }

    /// <summary>Raw L-Connect direction value (0-5).</summary>
    public int Direction { get; }

    /// <summary>Raw L-Connect brightness value.</summary>
    public int Brightness { get; }

    /// <summary>How many LEDs the fan ring has (default 24); the device addresses that many.</summary>
    public int NumberOfLed { get; }

    /// <summary>Whether the fan lighting mirrors the pump's.</summary>
    public bool SyncToPump { get; }

    /// <summary>The saved colours; the encoder writes up to four in R,G,B order.</summary>
    public IReadOnlyList<RgbColor> Colors { get; }
}
#endif
