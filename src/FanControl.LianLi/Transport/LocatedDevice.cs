namespace FanControl.LianLi.Transport;

/// <summary>
/// A located device - a HID interface, or one of the WinUSB-bound L-Wireless dongles - as the
/// identifying ids and path the enumerator opens a transport on, plus what the scan read about a HID
/// interface. Plain data: nothing here holds a handle, so a located device can be kept, remembered
/// across runs and compared without owning anything.
/// </summary>
internal sealed class LocatedDevice {
    public LocatedDevice(
        int vendorId,
        int productId,
        string devicePath,
        HidInterface? device,
        string? containerId = null,
        int maxOutputReportLength = 0) {
        VendorId = vendorId;
        ProductId = productId;
        DevicePath = devicePath;
        Device = device;
        ContainerId = containerId;
        MaxOutputReportLength = maxOutputReportLength;
    }

    /// <summary>USB vendor id (0x0CF2 for the Lian Li Uni family).</summary>
    public int VendorId { get; }

    /// <summary>USB product id identifying the controller family.</summary>
    public int ProductId { get; }

    /// <summary>OS device path. Used for logging and to open the raw handle for RPM input reports.</summary>
    public string DevicePath { get; }

    /// <summary>
    /// The Windows ContainerId of the physical device, or null when it could not be resolved. Every
    /// HID interface a single controller exposes shares this GUID and it differs across physical
    /// controllers, so it is the de-duplication key (see <see cref="HidDeviceDeduplicator"/>). The
    /// USB serial is deliberately not used: the Lian Li Uni controllers all report the same
    /// firmware-fixed serial, which would wrongly collapse distinct controllers into one.
    /// </summary>
    public string? ContainerId { get; }

    /// <summary>
    /// Maximum output-report length the interface accepts. When one physical controller exposes
    /// several HID interfaces, the fan-control interface is the one that accepts output reports, so
    /// the largest value is used to pick the interface to keep during de-duplication.
    /// </summary>
    public int MaxOutputReportLength { get; }

    /// <summary>
    /// The HID interface as the scan read it - its report capabilities - which the enumerator opens a
    /// transport with. Null for a WinUSB device (the L-Wireless dongles), for a HID interface whose
    /// capabilities the scan could not read, and for a device remembered from an earlier run; the
    /// enumerator reads a HID interface's capabilities afresh when it opens one of those.
    /// </summary>
    public HidInterface? Device { get; }
}
