#if ENABLE_LIGHTING
using System;
using System.Collections.Generic;
using FanControl.LianLi.Protocol;

namespace FanControl.LianLi.Devices;

/// <summary>
/// One controller's lighting look as read from L-Connect's saved configuration, matched to a
/// located device by its USB instance token. A controller carries whichever family's look was
/// saved under its token: the Uni family's per-port looks (<see cref="Ports"/> + optional
/// <see cref="Quantity"/> and <see cref="MergeOrder"/>, consumed by <see cref="SlInfinityLightingEncoder"/> /
/// <see cref="UniFanLightingEncoder"/> / <see cref="StrimerPlusLightingEncoder"/>), the Uni Fan TL's
/// per-fan looks (<see cref="TlFans"/>), or the 0x0416 coolers' fan, pump and screen-ring looks
/// (<see cref="GalahadFan"/> / <see cref="GalahadPumps"/> / <see cref="GalahadScreen"/>), and whether
/// the user handed that controller's LEDs to the motherboard's ARGB header
/// (<see cref="MotherboardArgbSync"/>). The encoder is chosen later by the located device's product id.
/// </summary>
internal sealed class LConnectControllerConfiguration
{
    /// <summary>Create a controller look from its parsed L-Connect settings.</summary>
    public LConnectControllerConfiguration(
        string instanceToken,
        IReadOnlyList<LightingPortState> ports,
        IReadOnlyList<int>? quantity,
        IReadOnlyList<TlFanLightingState>? tlFans = null,
        Galahad2FanLightingState? galahadFan = null,
        IReadOnlyList<Galahad2PumpLightingState>? galahadPumps = null,
        bool motherboardArgbSync = false,
        Galahad2ScreenLightingState? galahadScreen = null,
        IReadOnlyList<int>? mergeOrder = null)
    {
        if (string.IsNullOrEmpty(instanceToken))
        {
            throw new ArgumentException("Instance token is required.", nameof(instanceToken));
        }

        InstanceToken = instanceToken;
        Ports = ports ?? throw new ArgumentNullException(nameof(ports));
        Quantity = quantity;
        TlFans = tlFans;
        GalahadFan = galahadFan;
        GalahadPumps = galahadPumps;
        MotherboardArgbSync = motherboardArgbSync;
        GalahadScreen = galahadScreen;
        MergeOrder = mergeOrder;
    }

    /// <summary>
    /// The USB instance token (e.g. <c>71d6ab5</c>) from the saved <c>DeviceID</c>. A located
    /// controller matches when this token appears in its OS device path.
    /// </summary>
    public string InstanceToken { get; }

    /// <summary>The Uni-family per-port looks (one per configured port); empty for other families.</summary>
    public IReadOnlyList<LightingPortState> Ports { get; }

    /// <summary>The Uni-family saved fan quantity per group, or null when L-Connect saved none.</summary>
    public IReadOnlyList<int>? Quantity { get; }

    /// <summary>The order the fan groups chain in a merge effect (SL-Infinity, SL v2, AL v2), or null when L-Connect saved none.</summary>
    public IReadOnlyList<int>? MergeOrder { get; }

    /// <summary>The Uni Fan TL per-fan looks, or null when this is not a TL controller.</summary>
    public IReadOnlyList<TlFanLightingState>? TlFans { get; }

    /// <summary>The fan-ring look of a Galahad II Trinity or Vision or a HydroShift LCD, or null when this is none of those.</summary>
    public Galahad2FanLightingState? GalahadFan { get; }

    /// <summary>
    /// The Galahad II Trinity pump looks in their saved order - one for the whole cap, or one each
    /// for the inner and outer ring in L-Connect's individual mode - or null when none was saved.
    /// </summary>
    public IReadOnlyList<Galahad2PumpLightingState>? GalahadPumps { get; }

    /// <summary>The Galahad II Vision screen-ring look, or null when this is not a Vision.</summary>
    public Galahad2ScreenLightingState? GalahadScreen { get; }

    /// <summary>
    /// True when L-Connect's per-controller "sync to motherboard" switch is on: the saved look is
    /// then not driven, and the LEDs are handed to the motherboard's ARGB header the way L-Connect
    /// hands them over. False when the setting was never saved (older releases kept one global
    /// switch instead) or does not parse.
    /// </summary>
    public bool MotherboardArgbSync { get; }
}
#endif
