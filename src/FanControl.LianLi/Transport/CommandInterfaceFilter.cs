namespace FanControl.LianLi.Transport;

/// <summary>
/// Decides which HID interface of a device to keep during location. The 0x0416 command-packet
/// family (Uni Fan TL, Galahad II) exposes several interfaces but accepts its command packets only
/// on the vendor-defined usage page 0xFF1B; binding a controller to any other interface would give
/// a device that never answers a handshake. The Uni 0x0CF2 family needs no usage filtering and is
/// always kept. Pure decision logic so the rule is testable without real hardware.
/// </summary>
internal static class CommandInterfaceFilter {
    // Vendor and usage page of the command-packet family. Duplicated from the device catalog
    // because the Transport layer cannot depend upward on Devices; each layer needs it for its own
    // concern (the catalog to classify, this to pick the interface).
    private const int CommandPacketVendorId = 0x0416;
    private const int CommandUsagePage = 0xFF1B;

    /// <summary>Whether a device of this vendor needs its interface usage page probed and filtered.</summary>
    public static bool RequiresUsageFilter(int vendorId) => vendorId == CommandPacketVendorId;

    /// <summary>
    /// Whether an interface should be located. A command-packet interface is kept only when its
    /// usage page is the command page; an unknown page (the probe failed) is kept so location falls
    /// back to the output-report de-duplication rather than dropping an otherwise usable device.
    /// </summary>
    public static bool Keep(int vendorId, int? usagePage) {
        if (!RequiresUsageFilter(vendorId)) {
            return true;
        }

        return usagePage is null || usagePage == CommandUsagePage;
    }
}
