using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using FanControl.LianLi.Logging;

namespace FanControl.LianLi.Transport;

/// <summary>
/// Locates the wired controllers: the HID interfaces whose vendor and product id the plugin drives. The
/// walk is the plugin's own - the configuration manager's list of present HID interfaces, then, per
/// interface, a handle opened with no access rights, <c>HidD_GetAttributes</c> for its ids and
/// <c>HidP_GetCaps</c> for its usage page and report lengths - on the scan's thread and nobody else's.
/// It takes no lock, queues no work and waits on nothing another component in the host shares, so a
/// controller that wedges mid-scan costs this scan its deadline and nothing more: the bounded scan
/// cancels the stuck call, and this walk checks the scan's token before each native call so an
/// abandoned scan stops there.
/// </summary>
internal sealed class HidDeviceLocator {
    // ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND: nothing is listening on the path - the device is
    // unplugged or has not re-enumerated yet - which an open of a remembered device reports plainly.
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    private readonly IHidApi _hid;
    private readonly IConfigurationManagerApi _configurationManager;
    private readonly ContainerIdResolver _containers;
    private readonly ILog _log;

    public HidDeviceLocator(
        IHidApi hid, IConfigurationManagerApi configurationManager, ContainerIdResolver containers, ILog log) {
        _hid = hid ?? throw new ArgumentNullException(nameof(hid));
        _configurationManager = configurationManager ?? throw new ArgumentNullException(nameof(configurationManager));
        _containers = containers ?? throw new ArgumentNullException(nameof(containers));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Every present HID interface whose vendor and product id appear in both allow-lists, less the
    /// 0x0416 family's non-command interfaces (see <see cref="CommandInterfaceFilter"/>). Throws
    /// <see cref="OperationCanceledException"/> once <paramref name="token"/> is cancelled, before the
    /// next native call.
    /// </summary>
    public IReadOnlyList<LocatedDevice> Locate(
        IReadOnlyList<int> vendorIds, IReadOnlyList<int> productIds, CancellationToken token) {
        if (vendorIds is null) {
            throw new ArgumentNullException(nameof(vendorIds));
        }

        if (productIds is null) {
            throw new ArgumentNullException(nameof(productIds));
        }

        token.ThrowIfCancellationRequested();
        IReadOnlyList<string>? paths = DeviceInterfaceListReader.Read(
            _configurationManager,
            _hid.GetInterfaceClass(),
            null,
            (step, code) => _log.Write(string.Format(
                CultureInfo.InvariantCulture, "  HID scan: {0} failed (code {1})", step, code)),
            token);

        var located = new List<LocatedDevice>();
        foreach (string listed in paths ?? Array.Empty<string>()) {
            // The configuration manager spells a path with the device instance's own capitals
            // ("\\?\HID#VID_0CF2&..."), where the setup API reports it in lower case - the form every
            // earlier release keyed controllers on. Windows opens either, but the path is also the
            // controller's identity above the transport - its key in the remembered-controller file and
            // the order controllers are numbered in - so it keeps that form, or an upgrade would re-key
            // every controller and could move a user's fan-curve bindings.
            string path = listed.ToLowerInvariant();

            // Every controller the plugin drives is a USB device, and the path of a USB device's HID
            // interface carries its ids ("\\?\hid#vid_0cf2&pid_a102#..."). So an interface whose path
            // names another device - or no USB ids at all, as a Bluetooth one does - is passed over
            // without being opened: no device the plugin does not drive can hold up its scan.
            if (!UsbDevicePath.TryParseIds(path, out int pathVendorId, out int pathProductId)
                || !vendorIds.Contains(pathVendorId)
                || !productIds.Contains(pathProductId)) {
                continue;
            }

            LocatedDevice? device = ReadCandidate(path, vendorIds, productIds, token);
            if (device != null) {
                located.Add(device);
            }
        }

        return located;
    }

    /// <summary>
    /// Read the capabilities of a HID interface that is not already known - one remembered from an
    /// earlier run, or one whose capabilities the scan could not read - to open a transport on it.
    /// Throws <see cref="IOException"/>, saying why, when the interface is not there or will not say;
    /// <see cref="OperationCanceledException"/> once <paramref name="token"/> is cancelled.
    /// </summary>
    public HidInterface ReadInterface(LocatedDevice located, CancellationToken token) {
        if (located is null) {
            throw new ArgumentNullException(nameof(located));
        }

        token.ThrowIfCancellationRequested();
        using SafeHandle handle = _hid.OpenQueryHandle(located.DevicePath, out int error)
            ?? throw new IOException(error == ErrorFileNotFound || error == ErrorPathNotFound
                ? "No HID device is present at " + located.DevicePath
                : string.Format(
                    CultureInfo.InvariantCulture, "Failed to open HID device at {0} (error {1}).", located.DevicePath, error));

        token.ThrowIfCancellationRequested();
        if (!ReadCapabilities(_hid, handle, out HidCapabilities capabilities, out error, token)) {
            throw new IOException(string.Format(
                CultureInfo.InvariantCulture,
                "Report capabilities of {0} could not be read (error {1}).",
                located.DevicePath,
                error));
        }

        return new HidInterface(located.VendorId, located.ProductId, located.DevicePath, capabilities);
    }

    // Read one interface the path says is a candidate, or null when it cannot be used. Every native
    // failure is logged with the call, the interface and the code: the path already names a device the
    // plugin drives, so a failure here is one of its controllers not answering. The handle is closed
    // before the container id is resolved, which reaches the configuration manager, not the device.
    private LocatedDevice? ReadCandidate(
        string path, IReadOnlyList<int> vendorIds, IReadOnlyList<int> productIds, CancellationToken token) {
        int vendorId;
        int productId;
        HidCapabilities? capabilities = null;

        token.ThrowIfCancellationRequested();
        using (SafeHandle? handle = _hid.OpenQueryHandle(path, out int error)) {
            if (handle is null) {
                Failed("opening", path, error);
                return null;
            }

            token.ThrowIfCancellationRequested();
            if (!_hid.GetAttributes(handle, out vendorId, out productId, out error)) {
                Failed("HidD_GetAttributes", path, error);
                return null;
            }

            if (!vendorIds.Contains(vendorId) || !productIds.Contains(productId)) {
                _log.Write(string.Format(
                    CultureInfo.InvariantCulture,
                    "  HID interface {0} reports {1:X4}:{2:X4}, not ids the plugin drives; passed over",
                    path,
                    vendorId,
                    productId));
                return null;
            }

            token.ThrowIfCancellationRequested();
            if (ReadCapabilities(_hid, handle, out HidCapabilities read, out error, token)) {
                capabilities = read;
            } else {
                // A refused probe leaves the interface located, with nothing known about it: the usage
                // filter keeps it, the de-duplicator ranks it last, and the open reads it again.
                _log.Write(string.Format(
                    CultureInfo.InvariantCulture, "  report-capabilities probe refused for {0} (error {1})", path, error));
            }
        }

        // Only the 0x0416 family is filtered on its usage page; the Uni family is always kept.
        int? usagePage = CommandInterfaceFilter.RequiresUsageFilter(vendorId) ? capabilities?.UsagePage : null;
        if (!CommandInterfaceFilter.Keep(vendorId, usagePage)) {
            return null;
        }

        return new LocatedDevice(
            vendorId,
            productId,
            path,
            capabilities is HidCapabilities known ? new HidInterface(vendorId, productId, path, known) : null,
            TryGetContainerId(path, token),
            capabilities?.OutputReportLength ?? 0);
    }

    // Resolve the physical device's ContainerId so the de-duplicator can collapse a controller's
    // several HID interfaces while keeping distinct controllers apart (even ones sharing a serial).
    // An unresolved id is a missing-metadata case the de-duplicator handles safely (it falls back to
    // the per-interface device path, which never collapses), so log a trace and locate the device
    // either way rather than surface it as a fault.
    private string? TryGetContainerId(string path, CancellationToken token) {
        string? containerId = _containers.Resolve(path, token);
        if (containerId is null) {
            _log.Write("  container-id probe unresolved for " + path + ", de-dup falls back to the device path");
        }

        return containerId;
    }

    private void Failed(string operation, string path, int error)
        => _log.Write(string.Format(
            CultureInfo.InvariantCulture, "  HID interface {0}: {1} failed (error {2}); passed over", path, operation, error));

    /// <summary>
    /// The interface's capabilities: its report descriptor fetched (the one step that reaches the
    /// device), parsed, and released. A bounded call given up on while the fetch was out stops before
    /// the parse, and the descriptor is released either way. On failure <paramref name="error"/> is the
    /// fetch's Win32 error, or the parse's <c>HIDP_STATUS</c> code.
    /// </summary>
    internal static bool ReadCapabilities(IHidApi hid, SafeHandle handle, out HidCapabilities capabilities, out int error, CancellationToken token) {
        capabilities = default;
        if (!hid.GetPreparsedData(handle, out IntPtr preparsed, out error)) {
            return false;
        }

        try {
            token.ThrowIfCancellationRequested();
            return hid.GetCapabilities(preparsed, out capabilities, out error);
        } finally {
            hid.FreePreparsedData(preparsed);
        }
    }
}
