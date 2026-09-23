using System;

namespace FanControl.LianLi.Transport;

/// <summary>
/// One HID interface as the plugin read it: its ids, its path, and the capabilities that size its
/// transfers. Plain data, read once per scan (or once per open of a device remembered from an earlier
/// run) and handed to <see cref="HidTransport"/>, which opens its own handles on the path. The
/// capabilities do not change while the device is attached, and a reopen after a re-enumeration opens
/// the same device on the same path, so they are never read again for the life of a transport.
/// </summary>
internal sealed class HidInterface {
    public HidInterface(int vendorId, int productId, string devicePath, HidCapabilities capabilities) {
        if (string.IsNullOrEmpty(devicePath)) {
            throw new ArgumentException("Device path is required.", nameof(devicePath));
        }

        VendorId = vendorId;
        ProductId = productId;
        DevicePath = devicePath;
        Capabilities = capabilities;
    }

    /// <summary>USB vendor id, from <c>HidD_GetAttributes</c>.</summary>
    public int VendorId { get; }

    /// <summary>USB product id, from <c>HidD_GetAttributes</c>.</summary>
    public int ProductId { get; }

    /// <summary>The interface's device path, which every handle on it is opened on.</summary>
    public string DevicePath { get; }

    /// <summary>The top-level collection's usage page and report lengths.</summary>
    public HidCapabilities Capabilities { get; }
}
