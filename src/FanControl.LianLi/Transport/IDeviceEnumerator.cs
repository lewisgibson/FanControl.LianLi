using System.Collections.Generic;

namespace FanControl.LianLi.Transport;

/// <summary>
/// Discovers Lian Li controllers and opens transports for them. Implemented
/// once for Windows (the HID controllers through hid.dll, the wireless dongles
/// through WinUSB); faked in tests.
/// </summary>
internal interface IDeviceEnumerator {
    /// <summary>
    /// Locate every connected device whose vendor and product ids both
    /// appear in the supplied allow-lists. Throws when the scan cannot complete
    /// in bounded time (a device that has stopped answering Windows), so the
    /// caller can fail this scan and let the host retry rather than block.
    /// </summary>
    IReadOnlyList<LocatedDevice> Locate(
        IReadOnlyList<int> vendorIds,
        IReadOnlyList<int> productIds);

    /// <summary>
    /// Open a writable/readable transport for a located device. Throws when the
    /// device refuses the open or does not complete it in bounded time.
    /// </summary>
    IDeviceTransport Open(LocatedDevice info);
}
