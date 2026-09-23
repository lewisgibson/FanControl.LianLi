using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using FanControl.LianLi.Logging;
using FanControl.LianLi.Protocol;

namespace FanControl.LianLi.Transport;

/// <summary>
/// Finds and opens every device the plugin drives, whichever kind it is. HID controllers come from
/// <see cref="HidDeviceLocator"/>, matched on the vendor and product ids each interface reports; the
/// L-Wireless dongles are WinUSB devices with no HID interface at all, and come from
/// <see cref="WinUsbDeviceLocator"/>. An open routes to the matching transport. Both the scan and an
/// open run under a bounded wait, because both run on the host's thread during Initialize and both
/// open a device. Neither goes through any library the host shares: every handle is the plugin's own,
/// opened on a thread the plugin owns, so a device that wedges during either can cost the plugin its
/// deadline but cannot leave behind a lock another component in the host waits on.
/// </summary>
internal sealed class WindowsDeviceEnumerator : IDeviceEnumerator {
    // The scan opens each HID interface whose path names a device the plugin drives, for its
    // attributes and capabilities, and walks the configuration manager for the dongles. A controller
    // that has come back from sleep wedged - present to Windows but never completing an open - blocks
    // that walk, and Locate runs on the host's refresh thread (FanControl closes and re-initialises
    // every plugin a few seconds after a resume). So the walk is bounded: one that runs out fails this
    // Initialize, which the host retries a few times five seconds apart, instead of freezing FanControl
    // behind a blank window. A healthy walk takes a few tens of milliseconds.
    private const int LocateTimeoutMilliseconds = 5000;

    // An open is a few CreateFile/IOCTL calls: milliseconds on a healthy device, an immediate error on
    // one still re-enumerating. Only a wedged device runs it out - the same reasoning as the scan.
    private const int OpenTimeoutMilliseconds = 2000;

    // The scan's key in the call gate. It reaches every device rather than one, so a scan still stuck
    // holds back only the next scan; no device path can take this form.
    private const string ScanKey = "the device scan";

    private readonly ILog _log;
    private readonly IHidApi _hid;
    private readonly IWinUsbApi _winUsb;
    private readonly DeviceCallGate _calls;
    private readonly ITransferDelay _delay;
    private readonly HidDeviceLocator _controllers;
    private readonly WinUsbDeviceLocator _dongles;

    public WindowsDeviceEnumerator(ILog log)
        : this(
            log,
            WindowsHidApi.Instance,
            WindowsWinUsbApi.Instance,
            WindowsConfigurationManagerApi.Instance,
            DeviceCallGate.ForProcess(new BoundedDeviceCallRunner(log), StopwatchDeviceCallClock.Instance, log),
            ThreadTransferDelay.Instance) {
    }

    /// <summary>
    /// The enumerator over the supplied native surfaces, gate and delay. Every transport it opens
    /// shares <paramref name="calls"/>, so a call still stuck on a device path holds back both the
    /// transport's retries and the next open of that path.
    /// </summary>
    internal WindowsDeviceEnumerator(
        ILog log,
        IHidApi hid,
        IWinUsbApi winUsb,
        IConfigurationManagerApi configurationManager,
        DeviceCallGate calls,
        ITransferDelay delay) {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _hid = hid ?? throw new ArgumentNullException(nameof(hid));
        _winUsb = winUsb ?? throw new ArgumentNullException(nameof(winUsb));
        if (configurationManager is null) {
            throw new ArgumentNullException(nameof(configurationManager));
        }

        _calls = calls ?? throw new ArgumentNullException(nameof(calls));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        var containers = new ContainerIdResolver(configurationManager);
        _controllers = new HidDeviceLocator(hid, configurationManager, containers, _log);
        _dongles = new WinUsbDeviceLocator(configurationManager, containers, _log);
    }

    public IReadOnlyList<LocatedDevice> Locate(
        IReadOnlyList<int> vendorIds,
        IReadOnlyList<int> productIds) {
        if (vendorIds is null) {
            throw new ArgumentNullException(nameof(vendorIds));
        }

        if (productIds is null) {
            throw new ArgumentNullException(nameof(productIds));
        }

        IReadOnlyList<LocatedDevice> located = Array.Empty<LocatedDevice>();

        // Nothing to cancel by handle - the scan opens and closes its own handles as it goes; the bound
        // cancels a blocked call by thread, and the walk stops at its next native call once the bound
        // has given up on it. A scan that ran out may still finish on its abandoned thread and assign
        // 'located' after this returns; nobody reads it then.
        bool completed = _calls.TryRun(
            ScanKey,
            "device scan",
            token => {
                var found = new List<LocatedDevice>(_dongles.Locate(vendorIds, productIds, token));
                found.AddRange(_controllers.Locate(vendorIds, productIds, token));
                located = found;
            },
            LocateTimeoutMilliseconds,
            () => { });

        if (!completed) {
            throw new IOException(string.Format(
                CultureInfo.InvariantCulture,
                "Device scan timed out after {0} ms; a device is not answering Windows (still resuming?)",
                LocateTimeoutMilliseconds));
        }

        return located;
    }

    public IDeviceTransport Open(LocatedDevice info) {
        if (info is null) {
            throw new ArgumentNullException(nameof(info));
        }

        if (!_calls.TryClaim(info.DevicePath)) {
            throw new IOException(info.DevicePath + " is still open for a controller from before FanControl's last refresh; it is opened once that has closed it.");
        }

        try {
            return new ClaimedTransport(OpenClaimed(info), () => _calls.Release(info.DevicePath));
        } catch {
            _calls.Release(info.DevicePath);
            throw;
        }
    }

    private IDeviceTransport OpenClaimed(LocatedDevice info) {
        var handoff = new OpenHandoff<IDeviceTransport>();

        // A transport the abandoned thread finishes after the deadline is disposed by the handoff on
        // that thread, so a wedged device that eventually answers does not leave a handle behind. The
        // bound's own verdict is not needed: the handoff holds the transport if and only if the open
        // finished.
        _ = _calls.TryRun(
            info.DevicePath,
            "open of " + info.DevicePath,
            token => handoff.Complete(OpenCore(info, token)),
            OpenTimeoutMilliseconds,
            () => { });

        IDeviceTransport? transport = handoff.Take();
        if (transport is null) {
            throw new IOException(string.Format(
                CultureInfo.InvariantCulture,
                "Open of device at {0} timed out after {1} ms; device not answering Windows.",
                info.DevicePath,
                OpenTimeoutMilliseconds));
        }

        return transport;
    }

    private IDeviceTransport OpenCore(LocatedDevice info, CancellationToken token) {
        if (WirelessProtocol.IsDongle(info.VendorId, info.ProductId)) {
            return WinUsbTransport.Open(info.DevicePath, _log, _winUsb, _calls, _delay, token);
        }

        // A device remembered from an earlier run carries only its path, as does one whose capabilities
        // the scan could not read; its capabilities are read now, from the path, and an interface that
        // is not there yet fails the open.
        HidInterface device = info.Device ?? _controllers.ReadInterface(info, token);
        return HidTransport.Open(device, _hid, _log, _calls, _delay, token);
    }
}
