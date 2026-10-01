namespace FanControl.LianLi.Protocol;

/// <summary>
/// The Lian Li Uni controller families this plugin drives. Unknown product ids
/// are absent from the catalog and produce no controller, so there is
/// no <c>Unknown</c> member, and none is needed.
/// </summary>
internal enum DeviceFamily {
    /// <summary>Uni Hub (legacy, 0x7750) and Uni SL (0xA100).</summary>
    Sl,

    /// <summary>Uni AL (0xA101).</summary>
    Al,

    /// <summary>Uni SL-Infinity (0xA102).</summary>
    SlInfinity,

    /// <summary>Uni SL v2 (0xA103, 0xA105).</summary>
    SlV2,

    /// <summary>Uni AL v2 (0xA104).</summary>
    AlV2,
}
