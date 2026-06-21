using HidSharp;

namespace FanControl.LianLi.Hid;

/// <summary>
/// A located HID device: the identifying ids plus an opaque handle the
/// enumerator uses to open a transport. The underlying HidSharp handle is
/// kept internal so no HidSharp type leaks past the <c>Hid/</c> layer.
/// </summary>
internal sealed class HidDeviceInfo {
    public HidDeviceInfo(
        int vendorId,
        int productId,
        string devicePath,
        HidDevice? device,
        string? serialNumber = null,
        int maxOutputReportLength = 0) {
        VendorId = vendorId;
        ProductId = productId;
        DevicePath = devicePath;
        Device = device;
        SerialNumber = serialNumber;
        MaxOutputReportLength = maxOutputReportLength;
    }

    /// <summary>USB vendor id (0x0CF2 for the Lian Li Uni family).</summary>
    public int VendorId { get; }

    /// <summary>USB product id identifying the controller family.</summary>
    public int ProductId { get; }

    /// <summary>OS device path. Used for logging and to open the raw handle for RPM input reports.</summary>
    public string DevicePath { get; }

    /// <summary>
    /// USB serial number, or null/empty when the controller does not report one. A non-empty serial
    /// is the only reliable token shared by the several HID interfaces a single physical controller
    /// can expose, so it is the de-duplication key (see <see cref="HidDeviceDeduplicator"/>).
    /// </summary>
    public string? SerialNumber { get; }

    /// <summary>
    /// Maximum output-report length the interface accepts. When one physical controller exposes
    /// several HID interfaces, the fan-control interface is the one that accepts output reports, so
    /// the largest value is used to pick the interface to keep during de-duplication.
    /// </summary>
    public int MaxOutputReportLength { get; }

    /// <summary>
    /// The HidSharp handle the enumerator uses to open a stream. Null only for
    /// test-constructed instances that go through a fake enumerator.
    /// </summary>
    public HidDevice? Device { get; }
}
