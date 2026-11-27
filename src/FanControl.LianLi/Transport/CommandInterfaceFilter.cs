namespace FanControl.LianLi.Transport;

/// <summary>
/// Decides which HID interface of a device to keep during location. The 0x0416 command-packet
/// family (Uni Fan TL, Galahad II) exposes several interfaces but accepts its command packets only
/// on the vendor-defined usage page 0xFF1B; binding a controller to any other interface would give
/// a device that never answers a handshake. The Galahad II Vision LCD is the exception: Windows
/// exposes a single collection for it, on usage page 0xFF1A, which carries the command packets and
/// the screen data together. The Uni 0x0CF2 family needs no usage filtering and is always kept.
/// Pure decision logic so the rule is testable without real hardware.
/// </summary>
internal static class CommandInterfaceFilter {
    // Vendor and usage page of the command-packet family. Duplicated from the device catalog
    // because the Transport layer cannot depend upward on Devices; each layer needs it for its own
    // concern (the catalog to classify, this to pick the interface).
    private const int CommandPacketVendorId = 0x0416;
    private const int CommandUsagePage = 0xFF1B;

    // The Galahad II Vision LCD's one collection (usage page 0xFF1A, usage 0x93, 512-byte input and
    // 1024-byte output reports): report id 0x01 on it is the 64-byte command packet, ids 0x02 and
    // 0x03 the screen data. Measured on a 0x7395 (firmware V2.01.041), which exposes no 0xFF1B
    // interface at all, so a 0xFF1B-only rule drops the cooler. Duplicated from the device catalog
    // for the same reason as the vendor id.
    private const int SingleCollectionUsagePage = 0xFF1A;
    private const int VisionFirstProductId = 0x7391;
    private const int VisionSecondProductId = 0x7395;

    /// <summary>Whether a device of this vendor needs its interface usage page probed and filtered.</summary>
    public static bool RequiresUsageFilter(int vendorId) => vendorId == CommandPacketVendorId;

    /// <summary>
    /// Whether an interface should be located. A command-packet interface is kept only when its
    /// usage page is the command page, or, for the Galahad II Vision LCD, the page of its single
    /// collection; an unknown page (the probe failed) is kept so location falls back to the
    /// output-report de-duplication instead of dropping an otherwise usable device.
    /// </summary>
    public static bool Keep(int vendorId, int productId, int? usagePage) {
        if (!RequiresUsageFilter(vendorId)) {
            return true;
        }

        if (usagePage is null || usagePage == CommandUsagePage) {
            return true;
        }

        return usagePage == SingleCollectionUsagePage && IsSingleCollectionProduct(productId);
    }

    private static bool IsSingleCollectionProduct(int productId)
        => productId == VisionFirstProductId || productId == VisionSecondProductId;
}
