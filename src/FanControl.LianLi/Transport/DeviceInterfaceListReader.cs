using System;
using System.Collections.Generic;
using System.Threading;

namespace FanControl.LianLi.Transport;

/// <summary>
/// Reads the present device interfaces of one interface class from the configuration manager - every
/// HID interface for <see cref="HidDeviceLocator"/>, a dongle's WinUSB interface for
/// <see cref="WinUsbDeviceLocator"/> - as the paths a handle is opened on. Sizing and filling the list
/// are two calls that a device arriving in between can make disagree, so the pair is retried a bounded
/// number of times, as Microsoft's own sample for these calls does.
/// </summary>
internal static class DeviceInterfaceListReader {
    // CONFIGRET codes: the fill reports CR_BUFFER_SMALL when the list grew after it was sized.
    private const int CrSuccess = 0;
    private const int CrBufferSmall = 26;
    private const int ListAttempts = 3;

    // CM_GET_DEVICE_INTERFACE_LIST_PRESENT: only interfaces that are enabled right now. (1 is
    // CM_GET_DEVICE_INTERFACE_LIST_ALL_DEVICES, which would also return a disabled one.)
    private const uint InterfaceListPresent = 0x00000000;

    /// <summary>
    /// The present interfaces of <paramref name="interfaceClass"/>, optionally only those of the device
    /// instance <paramref name="deviceId"/>. Null when the configuration manager fails, having first
    /// handed <paramref name="onFailure"/> the step that failed and its <c>CONFIGRET</c> code. Checks
    /// <paramref name="token"/> before each native call.
    /// </summary>
    public static IReadOnlyList<string>? Read(
        IConfigurationManagerApi configurationManager,
        Guid interfaceClass,
        string? deviceId,
        Action<string, int> onFailure,
        CancellationToken token) {
        if (configurationManager is null) {
            throw new ArgumentNullException(nameof(configurationManager));
        }

        if (onFailure is null) {
            throw new ArgumentNullException(nameof(onFailure));
        }

        for (int attempt = 0; attempt < ListAttempts; attempt++) {
            token.ThrowIfCancellationRequested();
            int sizing = configurationManager.GetDeviceInterfaceListSize(out uint length, interfaceClass, deviceId, InterfaceListPresent);
            if (sizing != CrSuccess) {
                onFailure("sizing the interface list", sizing);
                return null;
            }

            if (length == 0) {
                return Array.Empty<string>();
            }

            var buffer = new char[length];
            token.ThrowIfCancellationRequested();
            int result = configurationManager.GetDeviceInterfaceList(interfaceClass, deviceId, buffer, length, InterfaceListPresent);
            if (result == CrSuccess) {
                return UsbDevicePath.SplitNullTerminatedList(buffer, (int)length);
            }

            if (result != CrBufferSmall) {
                onFailure("listing the interfaces", result);
                return null;
            }
        }

        onFailure("listing the interfaces (it kept growing)", CrBufferSmall);
        return null;
    }
}
