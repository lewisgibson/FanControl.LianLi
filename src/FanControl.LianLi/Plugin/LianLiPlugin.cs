using System;
using System.Collections.Generic;
using System.Globalization;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Hid;
using FanControl.LianLi.Logging;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Worker;
using FanControl.Plugins;

namespace FanControl.LianLi.Plugin;

/// <summary>
/// FanControl entry point for Lian Li Uni controllers. This is the only public
/// type in the assembly: it composes the HID transport, the per-device protocol
/// encoders, the injectable clock, and the keepalive worker, and exposes the
/// <see cref="IPlugin2"/> surface to the host. The control hot path only mutates
/// in-memory state; all USB I/O is driven from <see cref="Update"/> and the
/// background keepalive thread.
/// </summary>
public sealed class LianLiPlugin : IPlugin2, IDisposable {
    private readonly IHidDeviceEnumerator _enumerator;
    private readonly DeviceCatalog _catalog;
    private readonly IClock _clock;
    private readonly ILog _log;

    // FanControl calls Initialize/Update/Close on its own threads. This serializes
    // the worker lifecycle so a host Update() cannot tick a worker that a concurrent
    // Close()/Dispose() is tearing down.
    private readonly object _sync = new object();
    private readonly List<FanController> _controllers = new List<FanController>();
    private KeepAliveWorker? _worker;

#if ENABLE_LIGHTING
    // The directory the Lighting build reads L-Connect's saved config from. Defaults to
    // L-Connect's real location; a test points it at a fixture directory.
    private readonly string _lConnectConfigDirectory;
#endif

    /// <summary>
    /// Host-injected constructor. FanControl supplies the logger; the plugin
    /// logs through both it and a local file as a fallback.
    /// </summary>
    public LianLiPlugin(IPluginLogger logger)
        : this(
            new HidSharpEnumerator(),
            new DeviceCatalog(),
            new SystemClock(),
            new CompositeLog(new PluginLoggerLog(logger), new FileLogger())) {
    }

    /// <summary>Composition/test constructor that accepts fakes for every dependency.</summary>
    internal LianLiPlugin(
        IHidDeviceEnumerator enumerator,
        DeviceCatalog catalog,
        IClock clock,
        ILog log
#if ENABLE_LIGHTING
        ,
        string? lConnectConfigDirectory = null
#endif
        ) {
        _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
#if ENABLE_LIGHTING
        _lConnectConfigDirectory = lConnectConfigDirectory ?? LConnectConfigReader.DefaultConfigDirectory;
#endif
    }

    /// <summary>Plugin name shown in the FanControl UI. Each build variant advertises a
    /// distinct name so a user can tell which DLL is loaded at a glance.</summary>
    // Each shippable variant advertises a distinct name so a user (and the plugin log)
    // can tell at a glance which DLL is loaded. Note the name is part of FanControl's
    // sensor identifiers, so switching variants re-keys the controls: existing
    // fan-curve bindings must be re-pointed (or the config remapped) on a swap.
#if ENABLE_ARGB
    public string Name => "Lian Li Uni (ARGB)";
#elif ENABLE_LIGHTING
    public string Name => "Lian Li Uni (Lighting)";
#else
    public string Name => "Lian Li Uni";
#endif

    /// <summary>
    /// Scan for controllers, build a <see cref="FanController"/> for each known
    /// device, and start the keepalive worker. Safe to call repeatedly: any
    /// previous state is torn down first.
    /// </summary>
    public void Initialize() {
        lock (_sync) {
            InitializeLocked();
        }
    }

    private void InitializeLocked() {
        TearDownLocked();

        // Compile-time variant tag, logged so a user's log file reveals which DLL is installed.
#if ENABLE_ARGB
        const string buildVariant = "ARGB";
#elif ENABLE_LIGHTING
        const string buildVariant = "Lighting";
#else
        const string buildVariant = "standard";
#endif

#if ENABLE_LIGHTING
        // Lighting build only: read L-Connect's own saved look from its config directory
        // (opt-in). Absent (L-Connect not installed) or unreadable config means no looks, so
        // no lighting is driven at all.
        IReadOnlyList<LConnectControllerConfig> lightingConfigs = ReadLConnectLighting();
#endif

        IReadOnlyList<HidDeviceInfo> located = _enumerator.Locate(_catalog.VendorIds, _catalog.ProductIds);
        _log.Write(string.Format(
            CultureInfo.InvariantCulture,
            "Initialize: {0} build, scan located {1} Lian Li controller(s)",
            buildVariant,
            located.Count));

        foreach (HidDeviceInfo info in located) {
            if (!_catalog.TryGetProtocol(info.ProductId, out IFanProtocol? protocol)) {
                continue;
            }

            IHidTransport? transport = null;
            try {
                transport = _enumerator.Open(info);
#if ENABLE_LIGHTING
                // Re-apply L-Connect's saved look before fan setup, for a controller that has
                // a matching saved config. No match -> no lighting, leaving the device exactly
                // as another tool (OpenRGB, the motherboard) left it.
                ApplyLighting(transport, info, lightingConfigs);
#endif
                var controller = new FanController(
                    _controllers.Count, transport, protocol, _clock, _log);
                _controllers.Add(controller);
                transport = null; // ownership passed to the controller, which disposes it
                _log.Write(string.Format(
                    CultureInfo.InvariantCulture,
                    "  controller pid=0x{0:x4} family={1} path={2}",
                    info.ProductId,
                    protocol.Family,
                    info.DevicePath));
            }
#pragma warning disable CA1031 // resilience: a device that fails to open is skipped, not fatal
            catch (Exception ex) {
                // Open, or the controller's in-constructor setup writes, threw before the
                // controller took ownership - dispose the transport here rather than leak
                // the HID handle.
                transport?.Dispose();
                _log.Write(string.Format(
                    CultureInfo.InvariantCulture,
                    "  open failed pid=0x{0:x4}: {1}",
                    info.ProductId,
                    ex.Message));
            }
#pragma warning restore CA1031
        }

        _worker = new KeepAliveWorker(_controllers.ToArray(), _log);
        _worker.Start();
    }

    /// <summary>Register four control sensors and four fan sensors per controller.</summary>
    public void Load(IPluginSensorsContainer container) {
        if (container is null) {
            throw new ArgumentNullException(nameof(container));
        }

        for (int ci = 0; ci < _controllers.Count; ci++) {
            FanController controller = _controllers[ci];
            for (int ch = 0; ch < 4; ch++) {
                container.ControlSensors.Add(new ControlSensor(controller, ci, ch));
                container.FanSensors.Add(new FanSensor(controller, ci, ch));
            }
        }
    }

    /// <summary>Host update tick: pump pending writes and RPM polling for every controller.</summary>
    public void Update() {
        lock (_sync) {
            _worker?.TryTick();
        }
    }

    /// <summary>Tear down the worker and controllers. Null-guarded against a never-initialized plugin.</summary>
    public void Close() {
        lock (_sync) {
            TearDownLocked();
        }
    }

    /// <summary>Dispose the plugin. Equivalent to <see cref="Close"/>; safe to call more than once.</summary>
    public void Dispose() {
        lock (_sync) {
            TearDownLocked();
        }

        GC.SuppressFinalize(this);
    }

#if ENABLE_LIGHTING
    // SL-Infinity is the only family this build drives lighting for. A located controller of
    // any other family is left untouched rather than driven with unverified bytes.
    private const int SlInfinityProductId = 0xA102;

    // Read L-Connect's saved look directly from its own config directory. Opt-in and
    // best-effort: if L-Connect is not installed the directory is absent and no lighting is
    // driven; an unreadable config is logged and lighting is disabled rather than risk a
    // wrong look. Fan control is never affected either way.
    private IReadOnlyList<LConnectControllerConfig> ReadLConnectLighting()
    {
        try
        {
            IReadOnlyList<LConnectControllerConfig> configs =
                LConnectConfigReader.Read(_lConnectConfigDirectory);
            if (configs.Count > 0)
            {
                _log.Write(string.Format(
                    CultureInfo.InvariantCulture, "Lighting: read {0} L-Connect controller look(s)", configs.Count));
            }

            return configs;
        }
#pragma warning disable CA1031 // opt-in feature: a bad lighting config disables lighting, never breaks fan control
        catch (Exception ex)
        {
            _log.Write("Lighting: config read failed, lighting disabled: " + ex.Message);
            return Array.Empty<LConnectControllerConfig>();
        }
#pragma warning restore CA1031
    }

    // Re-apply the saved look for one located controller, matched to its L-Connect config by
    // instance token. No match -> no lighting. A matched controller of an unsupported family
    // is logged and skipped (its lighting is left as-is), never driven with guessed bytes.
    private void ApplyLighting(
        IHidTransport transport, HidDeviceInfo info, IReadOnlyList<LConnectControllerConfig> configs)
    {
        LConnectControllerConfig? match = null;
        foreach (LConnectControllerConfig config in configs)
        {
            if (info.DevicePath.IndexOf(config.InstanceToken, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                match = config;
                break;
            }
        }

        if (match is null)
        {
            return;
        }

        // Gate on the located device's hardware-read product id (authoritative), not one
        // parsed from the config file.
        if (info.ProductId != SlInfinityProductId)
        {
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "  lighting skipped for {0}: family pid=0x{1:x4} not supported",
                match.InstanceToken,
                info.ProductId));
            return;
        }

        // A lighting write the device rejects must not drop the controller: lighting is opt-in
        // and isolated, so disable it for this device and let fan control proceed.
        try
        {
            IReadOnlyList<LightingTransfer> transfers = SlInfinityLightingEncoder.Encode(match.Ports, match.Quantity);
            LightingReplay.Apply(transport, transfers);
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "  lighting applied for {0} ({1} writes)",
                match.InstanceToken,
                transfers.Count));
        }
#pragma warning disable CA1031 // opt-in feature: a lighting write fault disables lighting for this controller, never breaks its fan control
        catch (Exception ex)
        {
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "  lighting apply failed for {0}, fan control continues: {1}",
                match.InstanceToken,
                ex.Message));
        }
#pragma warning restore CA1031
    }
#endif

    // Caller must hold _sync.
    private void TearDownLocked() {
        _worker?.Dispose();
        _worker = null;
        _controllers.Clear();
    }
}
