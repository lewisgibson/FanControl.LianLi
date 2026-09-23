namespace FanControl.LianLi.Devices;

/// <summary>
/// What a located HID device is, so the plugin knows what to build for it. The two
/// families differ at the wire level - the Uni 0x0CF2 controllers speak report 0xE0 and
/// have an <c>IFanProtocol</c>; the 0x0416 controllers speak the 64-byte command packet
/// and report RPM with a write-then-read; the L-Wireless dongles are not HID at all, a
/// transmitter/receiver pair driven over WinUSB - so the kind, not just a protocol lookup,
/// drives the dispatch.
/// </summary>
internal enum DeviceKind {
    /// <summary>Not a device this plugin drives.</summary>
    Unknown = 0,

    /// <summary>A Uni 0x0CF2 fan controller backed by an <c>IFanProtocol</c>.</summary>
    UniFan,

    /// <summary>A Uni 0x0CF2 lighting-only device (Strimer Plus); no fan control.</summary>
    LightingOnly,

    /// <summary>A Uni Fan TL hub (vendor 0x0416), fans only.</summary>
    TlFan,

    /// <summary>A Galahad II Trinity AIO (vendor 0x0416), one fan and one pump.</summary>
    Galahad2,

    /// <summary>The L-Wireless transmitter dongle (0x0416:0x8040); paired with a receiver into one wireless controller.</summary>
    WirelessTransmitter,

    /// <summary>The L-Wireless receiver dongle (0x0416:0x8041); paired with a transmitter into one wireless controller.</summary>
    WirelessReceiver,
}
