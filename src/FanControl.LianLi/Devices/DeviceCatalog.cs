using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FanControl.LianLi.Protocol;

namespace FanControl.LianLi.Devices;

/// <summary>
/// Recognises the Lian Li devices this plugin drives and classifies a located one into a
/// <see cref="DeviceKind"/> so the plugin knows what to build. The Uni 0x0CF2 controllers
/// map to a pure <see cref="IFanProtocol"/> via <see cref="ProtocolFor"/>; the 0x0416
/// command-packet controllers (Uni Fan TL, Galahad II Trinity) have no <c>IFanProtocol</c>
/// and are identified by <see cref="Classify"/> instead, as are the L-Wireless dongles in
/// <see cref="WirelessProductIds"/>. The Strimer Plus (0xA200) is a
/// lighting-only device listed in <see cref="LightingProductIds"/>. Unknown ids classify as
/// <see cref="DeviceKind.Unknown"/> and produce no controller and no writes.
/// </summary>
internal sealed class DeviceCatalog {
    // The two USB vendors. The Uni family is one vendor; the command-packet family another.
    private const int UniVendorId = 0x0CF2;
    private const int CommandPacketVendorId = 0x0416;

    // 0x0416 product ids. The Galahad ships in two SKUs (performance / regular) on distinct pids.
    private const int TlFanProductId = 0x7372;
    private const int Galahad2PerformanceProductId = 0x7371;
    private const int Galahad2RegularProductId = 0x7373;

    // Galahad II Vision (0x7391/0x7395) and HydroShift LCD (0x7398/0x7399/0x739A) are AIOs that speak
    // the SAME 64-byte command packet as the Galahad II Trinity: handshake 0x81, SetPump 0x8A, SetFan
    // 0x8B, fan+pump RPM at the same handshake-reply offsets (verified against the decompile). Under
    // host (manual) control their fan/pump write payload is byte-identical to Trinity's, so they reuse
    // Galahad2Protocol/Galahad2Controller unchanged - only the LCD screen (out of scope) differs.
    private static readonly int[] Galahad2ProductIds =
    {
        Galahad2PerformanceProductId, Galahad2RegularProductId,
        0x7391, 0x7395,                 // Galahad II Vision LCD
        0x7398, 0x7399, 0x739A,         // HydroShift LCD
    };

    private readonly Dictionary<int, IFanProtocol> _byProductId;

    public DeviceCatalog() {
        // Encoders are pure and stateless, so a single instance can back every
        // product id that shares a family.
        var sl = new SlProtocol();
        var al = new AlProtocol();
        var slInfinity = new SlInfinityProtocol();
        var slV2 = new SlV2Protocol();
        var alV2 = new AlV2Protocol();

        _byProductId = new Dictionary<int, IFanProtocol>
        {
            { 0x7750, sl },         // Uni Hub (legacy)
            { 0xA100, sl },         // Uni SL
            { 0xA101, al },         // Uni AL
            { 0xA102, slInfinity }, // Uni SL-Infinity
            { 0xA103, slV2 },       // Uni SL v2
            { 0xA104, alV2 },       // Uni AL v2
            { 0xA105, slV2 },       // Uni SL v2 (alternate pid)
            { 0xA106, sl },         // Uni SL (Redragon OEM variant) - L-Connect drives it as an SL fan
        };

        VendorIds = new[] { UniVendorId, CommandPacketVendorId, WirelessProtocol.AlternateVendorId };
        ProductIds = _byProductId.Keys.ToArray();

        // The 0x0416 fan/pump controllers. They share the transport and enumeration with the Uni
        // family but speak a different wire protocol, so they are located here yet built separately.
        CommandPacketProductIds = new[] { TlFanProductId }.Concat(Galahad2ProductIds).ToArray();

        // Lighting-only products (no fan protocol) the Lighting build still locates to drive RGB.
        LightingProductIds = new[] { 0xA200 }; // Strimer Plus

        // The L-Wireless dongles: a transmitter and a receiver, each its own USB (WinUSB) device,
        // built together into one wireless controller.
        WirelessProductIds = new[] {
            WirelessProtocol.TransmitterProductId,
            WirelessProtocol.ReceiverProductId,
            WirelessProtocol.AlternateTransmitterProductId,
            WirelessProtocol.AlternateReceiverProductId,
        };
    }

    /// <summary>The USB vendor ids the plugin scans: the Uni family (0x0CF2), the 0x0416 family, and the wireless dongles' alternate vendor.</summary>
    public IReadOnlyList<int> VendorIds { get; }

    /// <summary>Every Uni fan product id backed by an <see cref="IFanProtocol"/>.</summary>
    public IReadOnlyList<int> ProductIds { get; }

    /// <summary>The 0x0416 fan/pump product ids (Uni Fan TL, Galahad II Trinity) located but built separately.</summary>
    public IReadOnlyList<int> CommandPacketProductIds { get; }

    /// <summary>
    /// Lighting-only product ids (the Strimer Plus) that have no fan protocol but whose RGB the
    /// Lighting build drives. They are located and applied, never registered as fan controllers.
    /// </summary>
    public IReadOnlyList<int> LightingProductIds { get; }

    /// <summary>The L-Wireless dongle product ids (transmitter and receiver, both vendor pairs), located but built as a pair.</summary>
    public IReadOnlyList<int> WirelessProductIds { get; }

    /// <summary>
    /// Classify a located device by its vendor and product id so the plugin knows what to build.
    /// The vendor disambiguates the two wire families; an id from neither classifies as
    /// <see cref="DeviceKind.Unknown"/>.
    /// </summary>
    public DeviceKind Classify(int vendorId, int productId) {
        if (vendorId == UniVendorId) {
            if (_byProductId.ContainsKey(productId)) {
                return DeviceKind.UniFan;
            }

            return LightingProductIds.Contains(productId) ? DeviceKind.LightingOnly : DeviceKind.Unknown;
        }

        if (vendorId == CommandPacketVendorId) {
            if (productId == TlFanProductId) {
                return DeviceKind.TlFan;
            }

            if (Galahad2ProductIds.Contains(productId)) {
                return DeviceKind.Galahad2;
            }
        }

        if (WirelessProtocol.IsTransmitter(vendorId, productId)) {
            return DeviceKind.WirelessTransmitter;
        }

        if (WirelessProtocol.IsReceiver(vendorId, productId)) {
            return DeviceKind.WirelessReceiver;
        }

        return DeviceKind.Unknown;
    }

    /// <summary>
    /// The protocol for a Uni product id - one <see cref="Classify"/> reports as
    /// <see cref="DeviceKind.UniFan"/>. Throws <see cref="ArgumentException"/> for any other id,
    /// including the 0x0416 family, which has no <see cref="IFanProtocol"/>.
    /// </summary>
    public IFanProtocol ProtocolFor(int productId) {
        if (!_byProductId.TryGetValue(productId, out IFanProtocol? protocol)) {
            throw new ArgumentException(string.Format(
                CultureInfo.InvariantCulture, "Product id 0x{0:x4} is not a Uni fan controller.", productId), nameof(productId));
        }

        return protocol;
    }
}
