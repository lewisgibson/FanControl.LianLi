#if ENABLE_LIGHTING
using System;
using System.Collections.Generic;
using FanControl.LianLi.Protocol;

namespace FanControl.LianLi.Devices;

/// <summary>
/// One controller's lighting look as read from L-Connect's saved configuration, matched to a
/// located device by its USB instance token. A controller carries whichever family's look was
/// saved under its token: the Uni family's per-port looks (<see cref="Ports"/> + optional
/// <see cref="Quantity"/>, consumed by <see cref="SlInfinityLightingEncoder"/> /
/// <see cref="StrimerPlusLightingEncoder"/>), the Uni Fan TL's per-fan looks (<see cref="TlFans"/>),
/// or the Galahad II's fan and pump looks (<see cref="GalahadFan"/> / <see cref="GalahadPump"/>).
/// The encoder is chosen later by the located device's product id.
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
        Galahad2PumpLightingState? galahadPump = null)
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
        GalahadPump = galahadPump;
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

    /// <summary>The Uni Fan TL per-fan looks, or null when this is not a TL controller.</summary>
    public IReadOnlyList<TlFanLightingState>? TlFans { get; }

    /// <summary>The Galahad II fan-ring look, or null when this is not a Galahad controller.</summary>
    public Galahad2FanLightingState? GalahadFan { get; }

    /// <summary>The Galahad II pump look, or null when this is not a Galahad controller.</summary>
    public Galahad2PumpLightingState? GalahadPump { get; }
}
#endif
