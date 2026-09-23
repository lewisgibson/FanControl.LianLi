using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using FanControl.LianLi.Logging;
using FanControl.LianLi.Protocol;

namespace FanControl.LianLi.Transport;

/// <summary>
/// Locates the L-Wireless dongles. They are not HID devices - Windows binds them to WinUSB, so
/// they have no HID interface for <see cref="HidDeviceLocator"/> to list - and the way to one is
/// not the generic USB device interface but the interface GUID WinUSB registered for it in its
/// hardware key, which is what Lian Li's own USB library reads (docs/wireless.md). So this walks the present USB device instances,
/// keeps the ones whose ids are a dongle's, reads <c>DeviceInterfaceGUIDs</c> from each one's
/// hardware key, and resolves that interface to the path <see cref="WinUsbTransport"/> opens. A
/// device with no interface GUID is not WinUSB-bound - Lian Li's driver was never installed - and
/// is skipped rather than opened and failed. Every native failure along the way is logged with the
/// call, the device and the code, so "the dongles are not found" can always be told apart from
/// "Windows would not say". The walk runs inside the bounded device scan, so it checks the scan's
/// token before every native call and stops the moment the scan has been given up on.
/// </summary>
internal sealed class WinUsbDeviceLocator {
    // CONFIGRET codes. A list can grow between the call that sizes it and the call that fills it (a
    // device arriving mid-scan), which the fill reports as CR_BUFFER_SMALL; the list is then sized
    // again, a bounded number of times, as Microsoft's own sample for these calls does (and as
    // DeviceInterfaceListReader does for the interface lists).
    private const int CrSuccess = 0;
    private const int CrBufferSmall = 26;
    private const int ListAttempts = 3;

    // CM_LOCATE_DEVNODE_NORMAL: only a device node that is present.
    private const uint LocateDeviceNodeNormal = 0;

    // CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT: the present devices under one
    // enumerator branch. "USB" is every USB device; the ids are filtered afterwards.
    private const uint IdListFilterEnumerator = 0x00000001;
    private const uint IdListFilterPresent = 0x00000100;
    private const string UsbEnumerator = "USB";

    // CM_REGISTRY_HARDWARE: the device node's own "Device Parameters" key, where a WinUSB INF writes
    // the interface GUID. RegDisposition_OpenExisting: never create it. KEY_READ: read access only.
    private const uint RegistryHardware = 0x00000000;
    private const uint RegDispositionOpenExisting = 0x00000001;
    private const int KeyRead = 0x20019;

    // The hardware profile argument: 0 is the current one.
    private const uint CurrentHardwareProfile = 0;

    // The value a WinUSB INF writes: a REG_MULTI_SZ of interface GUIDs, or the older single string.
    private const string InterfaceGuidsValue = "DeviceInterfaceGUIDs";
    private const string InterfaceGuidValue = "DeviceInterfaceGuid";
    private const int RegSz = 1;
    private const int RegMultiSz = 7;

    // Win32 codes from the registry, which are not CONFIGRET codes: ERROR_MORE_DATA (234) is what
    // RegQueryValueExW returns when its buffer is too small for the value.
    private const int ErrorSuccess = 0;
    private const int ErrorFileNotFound = 2;
    private const int ErrorMoreData = 234;

    private readonly IConfigurationManagerApi _configurationManager;
    private readonly ContainerIdResolver _containers;
    private readonly ILog _log;

    public WinUsbDeviceLocator(IConfigurationManagerApi configurationManager, ContainerIdResolver containers, ILog log) {
        _configurationManager = configurationManager ?? throw new ArgumentNullException(nameof(configurationManager));
        _containers = containers ?? throw new ArgumentNullException(nameof(containers));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Every present dongle whose vendor and product id appear in both allow-lists, as the path to
    /// its WinUSB interface. Throws <see cref="OperationCanceledException"/> once
    /// <paramref name="token"/> is cancelled, before the next native call.
    /// </summary>
    public IReadOnlyList<LocatedDevice> Locate(
        IReadOnlyList<int> vendorIds, IReadOnlyList<int> productIds, CancellationToken token) {
        if (vendorIds is null) {
            throw new ArgumentNullException(nameof(vendorIds));
        }

        if (productIds is null) {
            throw new ArgumentNullException(nameof(productIds));
        }

        var located = new List<LocatedDevice>();
        foreach (string instanceId in ListUsbDeviceInstances(token)) {
            if (!UsbDevicePath.TryParseIds(instanceId, out int vendorId, out int productId)
                || !WirelessProtocol.IsDongle(vendorId, productId)
                || !Contains(vendorIds, vendorId)
                || !Contains(productIds, productId)) {
                continue;
            }

            string? path = ResolveInterfacePath(instanceId, token);
            if (path is null) {
                continue;
            }

            located.Add(new LocatedDevice(vendorId, productId, path, null, _containers.Resolve(path, token)));
        }

        return located;
    }

    private IReadOnlyList<string> ListUsbDeviceInstances(CancellationToken token) {
        const uint flags = IdListFilterEnumerator | IdListFilterPresent;
        for (int attempt = 0; attempt < ListAttempts; attempt++) {
            token.ThrowIfCancellationRequested();
            int sizing = _configurationManager.GetDeviceIdListSize(out uint length, UsbEnumerator, flags);
            if (sizing != CrSuccess) {
                Failed("sizing the USB device list", UsbEnumerator, sizing);
                return Array.Empty<string>();
            }

            if (length == 0) {
                return Array.Empty<string>();
            }

            var buffer = new char[length];
            token.ThrowIfCancellationRequested();
            int result = _configurationManager.GetDeviceIdList(UsbEnumerator, buffer, length, flags);
            if (result == CrSuccess) {
                return UsbDevicePath.SplitNullTerminatedList(buffer, (int)length);
            }

            if (result != CrBufferSmall) {
                Failed("listing the USB devices", UsbEnumerator, result);
                return Array.Empty<string>();
            }
        }

        Failed("listing the USB devices (it kept growing)", UsbEnumerator, CrBufferSmall);
        return Array.Empty<string>();
    }

    // The interface path for a device instance: read the GUIDs its driver registered, then ask for
    // that interface class filtered to this instance. The first path a GUID yields is the device.
    private string? ResolveInterfacePath(string instanceId, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        int located = _configurationManager.LocateDeviceNode(out uint deviceInstance, instanceId, LocateDeviceNodeNormal);
        if (located != CrSuccess) {
            Failed("locating the device node", instanceId, located);
            return null;
        }

        IReadOnlyList<string> guids = ReadInterfaceGuids(deviceInstance, instanceId, token);
        foreach (string guidText in guids) {
            if (!Guid.TryParse(guidText, out Guid interfaceGuid)) {
                _log.Write("  wireless dongle " + instanceId + ": registered interface GUID '" + guidText + "' is not a GUID");
                continue;
            }

            string? path = FirstInterfacePath(interfaceGuid, instanceId, token);
            if (path != null) {
                return path;
            }
        }

        _log.Write(guids.Count == 0
            ? "  wireless dongle " + instanceId + " has no WinUSB interface registered: Windows has not bound it to WinUSB, so L-Connect cannot reach it either"
            : "  wireless dongle " + instanceId + " has a WinUSB interface registered but none present: it may still be starting, and the next scan looks again");
        return null;
    }

    private IReadOnlyList<string> ReadInterfaceGuids(uint deviceInstance, string instanceId, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        int opened = _configurationManager.OpenDeviceNodeKey(
            deviceInstance, KeyRead, CurrentHardwareProfile, RegDispositionOpenExisting, out IntPtr key, RegistryHardware);
        if (opened != CrSuccess) {
            Failed("opening the hardware key", instanceId, opened);
            return Array.Empty<string>();
        }

        // The key is closed even when the scan has been given up on: a registry handle left open
        // would leak for the life of the host process.
        try {
            IReadOnlyList<string> guids = ReadStringValue(key, InterfaceGuidsValue, instanceId, token);
            return guids.Count > 0 ? guids : ReadStringValue(key, InterfaceGuidValue, instanceId, token);
        } finally {
            _configurationManager.CloseKey(key);
        }
    }

    // Read one REG_SZ or REG_MULTI_SZ value as a list of strings; anything else reads as nothing. The
    // sizing call passes no buffer, which RegQueryValueExW documents as returning ERROR_SUCCESS with
    // the size; ERROR_MORE_DATA also carries the size, so either is taken as sized.
    // A value that is simply not there (ERROR_FILE_NOT_FOUND) is the normal case for one of the two
    // names; any other failure is logged.
    private IReadOnlyList<string> ReadStringValue(IntPtr key, string name, string instanceId, CancellationToken token) {
        uint size = 0;
        token.ThrowIfCancellationRequested();
        int sizing = _configurationManager.QueryValue(key, name, out int type, null, ref size);
        if (sizing != ErrorSuccess && sizing != ErrorMoreData) {
            if (sizing != ErrorFileNotFound) {
                Failed("reading " + name, instanceId, sizing);
            }

            return Array.Empty<string>();
        }

        if (size == 0 || (type != RegSz && type != RegMultiSz)) {
            return Array.Empty<string>();
        }

        // The size is in bytes; one spare character keeps an odd byte count and an unterminated value
        // inside the buffer.
        var buffer = new char[(size / sizeof(char)) + 1];
        token.ThrowIfCancellationRequested();
        int read = _configurationManager.QueryValue(key, name, out _, buffer, ref size);
        if (read != ErrorSuccess) {
            Failed("reading " + name, instanceId, read);
            return Array.Empty<string>();
        }

        return UsbDevicePath.SplitNullTerminatedList(buffer, (int)(size / sizeof(char)));
    }

    private string? FirstInterfacePath(Guid interfaceGuid, string instanceId, CancellationToken token) {
        IReadOnlyList<string>? paths = DeviceInterfaceListReader.Read(
            _configurationManager, interfaceGuid, instanceId, (step, code) => Failed(step, instanceId, code), token);

        // Only present, enabled interfaces are listed, so any entry reaches the device; the first is
        // the one a device with a single WinUSB interface - both dongles - has.
        return paths?.Count > 0 ? paths[0] : null;
    }

    private void Failed(string operation, string subject, int code)
        => _log.Write(string.Format(
            CultureInfo.InvariantCulture, "  wireless dongle scan: {0} failed for {1} (code {2})", operation, subject, code));

    private static bool Contains(IReadOnlyList<int> list, int value) {
        for (int i = 0; i < list.Count; i++) {
            if (list[i] == value) {
                return true;
            }
        }

        return false;
    }
}
