namespace FanControl.LianLi.Protocol;

/// <summary>
/// Which wireless fan family a slot on an L-Wireless fan group holds, decoded from the group's
/// per-slot type code (see <see cref="WirelessProtocol.FamilyOf"/>). The family fixes the duty
/// floor L-Connect applies before a speed write and the name the plugin shows for the fan.
/// </summary>
internal enum WirelessFanFamily {
    /// <summary>A slot with no fan, or a type code the plugin does not drive.</summary>
    Unknown = 0,

    /// <summary>UNI FAN SL V3 (wireless), 120 or 140 mm, LED or LCD.</summary>
    SlV3,

    /// <summary>UNI FAN TL V2 (wireless), 120 or 140 mm, LED, reverse, or LCD.</summary>
    TlV2,

    /// <summary>UNI FAN SL-Infinity Wireless, 120 or 140 mm.</summary>
    SlInfinity,

    /// <summary>UNI FAN CL (wireless), 120 mm, normal or reverse.</summary>
    Cl,
}
