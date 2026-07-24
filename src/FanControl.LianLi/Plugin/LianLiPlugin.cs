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
    private readonly List<IFanDevice> _controllers = new List<IFanDevice>();
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
        : this(new CompositeLog(new PluginLoggerLog(logger), new FileLogger())) {
    }

    // Wire the real HID enumerator with the same log the rest of the plugin uses, so an
    // enumeration probe that fails leaves a trace. A separate ctor because the enumerator and the
    // log are siblings the public ctor cannot reference from a single chained call.
    private LianLiPlugin(ILog log)
        : this(new HidSharpEnumerator(log), new DeviceCatalog(), new SystemClock(), log) {
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

        // Every build also locates the 0x0416 command-packet controllers (Uni Fan TL, Galahad II);
        // the Lighting build additionally locates lighting-only products (Strimer Plus) to drive
        // their RGB. The enumerator requires both vendor and product to match, so listing a product
        // id here is what opts a family into discovery.
        var productIds = new List<int>(_catalog.ProductIds);
        productIds.AddRange(_catalog.CommandPacketProductIds);
#if ENABLE_LIGHTING
        productIds.AddRange(_catalog.LightingProductIds);
#endif

        IReadOnlyList<HidDeviceInfo> located;
        try {
            located = _enumerator.Locate(_catalog.VendorIds, productIds);
        }
#pragma warning disable CA1031 // host seam: a HidSharp enumeration failure must not crash FanControl
        catch (Exception ex) {
            // The first enumeration spins up HidSharp's device-manager window (RegisterClass /
            // WM_DEVICECHANGE), which can fail in some host/desktop contexts. Degrade to zero
            // controllers and log it rather than let the exception propagate into the host - the
            // same host-seam resilience the per-device open catch below applies, one level up.
            _log.Write("Initialize: device enumeration failed, no controllers: " + ex.Message);
            located = Array.Empty<HidDeviceInfo>();
        }
#pragma warning restore CA1031

        int interfaceCount = located.Count;

        // Collapse the several HID interfaces one physical controller can expose into a single
        // logical device, so one controller does not register a duplicate set of channel sensors.
        located = HidDeviceDeduplicator.Deduplicate(located);

        // Order controllers by a stable per-device token (the OS device path) instead of the OS
        // enumeration order, which can shift across reboot/sleep/hibernate. Sensor ids are keyed on
        // the resulting index, so a stable order keeps a user's saved fan-curve bindings pointing at
        // the same physical channel run to run.
        located = SortByDevicePath(located);

        _log.Write(string.Format(
            CultureInfo.InvariantCulture,
            "Initialize: {0} build, scan located {1} HID interface(s), {2} controller(s)",
            buildVariant,
            interfaceCount,
            located.Count));

        foreach (HidDeviceInfo info in located) {
            switch (_catalog.Classify(info.VendorId, info.ProductId)) {
                case DeviceKind.UniFan:
                    BuildUniController(info
#if ENABLE_LIGHTING
                        , lightingConfigs
#endif
                        );
                    break;
                case DeviceKind.TlFan:
                    BuildCommandPacketController(info, DeviceKind.TlFan
#if ENABLE_LIGHTING
                        , lightingConfigs
#endif
                        );
                    break;
                case DeviceKind.Galahad2:
                    BuildCommandPacketController(info, DeviceKind.Galahad2
#if ENABLE_LIGHTING
                        , lightingConfigs
#endif
                        );
                    break;
#if ENABLE_LIGHTING
                case DeviceKind.LightingOnly:
                    // A lighting-only device (e.g. Strimer Plus) has no fan control: drive its saved
                    // look once and move on, never registering a controller or worker for it.
                    DriveLightingOnlyDevice(info, lightingConfigs);
                    break;
#endif
                default:
                    _log.Write(string.Format(
                        CultureInfo.InvariantCulture,
                        "  skipped unrecognised device pid=0x{0:x4} path={1}",
                        info.ProductId,
                        info.DevicePath));
                    break;
            }
        }

        _worker = new KeepAliveWorker(_controllers.ToArray(), _log);
        _worker.Start();
    }

    // Open a Uni 0x0CF2 controller, apply its saved lighting (Lighting build), and register it. A
    // device that fails to open is skipped rather than crashing the host; once the controller is
    // registered, its setup writes and population probe are guarded individually so a rejected
    // write degrades the feature, never the controller.
    private void BuildUniController(HidDeviceInfo info
#if ENABLE_LIGHTING
        , IReadOnlyList<LConnectControllerConfig> lightingConfigs
#endif
        ) {
        if (!_catalog.TryGetProtocol(info.ProductId, out IFanProtocol? protocol)) {
            return;
        }

        IHidTransport? transport = null;
        try {
            transport = _enumerator.Open(info);
#if ENABLE_LIGHTING
            // Re-apply L-Connect's saved look before fan setup, for a controller that has a matching
            // saved config. No match -> no lighting, leaving the device exactly as another tool
            // (OpenRGB, the motherboard) left it.
            ApplyLighting(transport, info, lightingConfigs);
#endif
            bool[] startStopEnabled = ReadStartStop(info, protocol.ChannelCount);
            var controller = new FanController(_controllers.Count, transport, protocol, startStopEnabled, _clock, _log);
            _controllers.Add(controller);
            transport = null; // ownership passed to the controller, which disposes it
            AssertManualMode(controller, info);
            DetectPopulation(controller, info);
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "  controller pid=0x{0:x4} family={1} startStop={2} path={3}",
                info.ProductId,
                protocol.Family,
                FormatStartStop(startStopEnabled),
                info.DevicePath));
        }
#pragma warning disable CA1031 // resilience: a device that fails to open is skipped, not fatal
        catch (Exception ex) {
            // Open (or a Lighting-build pre-construction step) threw before the controller took
            // ownership - dispose the transport here rather than leak the HID handle.
            transport?.Dispose();
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "  open failed pid=0x{0:x4}: {1}",
                info.ProductId,
                ex.Message));
        }
#pragma warning restore CA1031
    }

    // Assert manual (software) mode on the registered Uni controller so the host owns the speed. A
    // rejected setup write must not lose the controller: the worker re-asserts manual mode before
    // every speed write, so control recovers on the first accepted write. The fault is isolated here
    // at the composition seam - the same host-seam resilience intent as the per-device open guard -
    // and logged.
    private void AssertManualMode(FanController controller, HidDeviceInfo info) {
        try {
            controller.AssertManualMode();
        }
#pragma warning disable CA1031 // host seam: a rejected setup write degrades to worker-time re-asserts, never loses the controller
        catch (Exception ex) {
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "  manual-mode assert failed for {0}: {1}; the worker re-asserts before each write",
                info.DevicePath,
                ex.Message));
        }
#pragma warning restore CA1031
    }

    // Probe the Uni controller for which channels have a fan, so an empty slot is not surfaced as a
    // dead sensor. A probe read fault must not lose the controller (it stays with all channels
    // shown), so the fault is isolated here at the composition seam - the same host-seam resilience
    // intent as the per-device open guard - and logged. The controller defaults to
    // all-populated until this narrows it.
    private void DetectPopulation(FanController controller, HidDeviceInfo info) {
        try {
            controller.DetectPopulation();
        }
#pragma warning disable CA1031 // host seam: a probe read fault leaves all channels shown, never loses the controller
        catch (Exception ex) {
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "  population probe failed for {0}: {1}; showing all channels",
                info.DevicePath,
                ex.Message));
        }
#pragma warning restore CA1031
    }

    // Read L-Connect's per-group start/stop toggle for this Uni controller. A missing profile is
    // the normal "L-Connect not installed / no saved profile" case and yields all-off; a corrupt
    // profile must not stop the controller loading, so it degrades to all-off and logs - the same
    // isolate-the-fault intent as the lighting guard, applied at the composition seam.
    private bool[] ReadStartStop(HidDeviceInfo info, int channelCount) {
        // Log whether the profile file was located, so "start/stop does nothing" can be told apart
        // from "profile found but the toggle is off" without guessing at the MD5 filename mapping.
        string profilePath = System.IO.Path.Combine(
            StartStopConfigReader.DefaultProfileDirectory, StartStopConfigReader.ProfileFileName(info.DevicePath));
        _log.Write(string.Format(
            CultureInfo.InvariantCulture,
            "  start/stop profile {0}: {1}",
            System.IO.File.Exists(profilePath) ? "found" : "absent",
            profilePath));

        try {
            return StartStopConfigReader.Read(
                StartStopConfigReader.DefaultProfileDirectory, info.DevicePath, channelCount);
        }
#pragma warning disable CA1031 // resilience: a bad start/stop profile disables the feature, never blocks the controller
        catch (Exception ex) {
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "  start/stop profile unreadable for {0}: {1}",
                info.DevicePath,
                ex.Message));
            return new bool[channelCount];
        }
#pragma warning restore CA1031
    }

    // Compact "ch0,ch2" list of the channels with start/stop enabled, or "none" - a diagnostic so a
    // misread toggle is visible in the log without dumping the whole profile.
    private static string FormatStartStop(bool[] startStopEnabled) {
        var enabled = new List<string>();
        for (int channel = 0; channel < startStopEnabled.Length; channel++) {
            if (startStopEnabled[channel]) {
                enabled.Add("ch" + channel.ToString(CultureInfo.InvariantCulture));
            }
        }

        return enabled.Count == 0 ? "none" : string.Join(",", enabled);
    }

    // Open a 0x0416 command-packet controller (Uni Fan TL or Galahad II) and register it. The
    // controller's constructor performs the discovery handshake, so a wrong interface or an absent
    // device surfaces here as a read timeout and is skipped, never crashing the host.
    private void BuildCommandPacketController(HidDeviceInfo info, DeviceKind kind
#if ENABLE_LIGHTING
        , IReadOnlyList<LConnectControllerConfig> lightingConfigs
#endif
        ) {
        IHidTransport? transport = null;
        try {
            transport = _enumerator.Open(info);
            IFanDevice controller = kind == DeviceKind.Galahad2
                ? new Galahad2Controller(_controllers.Count, transport, _clock, _log)
                : (IFanDevice)new TlFanController(_controllers.Count, transport, _clock, _log);
            // Register before the optional lighting step so the controller (and the transport it
            // now owns) is tracked for disposal regardless of what lighting does.
            _controllers.Add(controller);
#if ENABLE_LIGHTING
            // The controller owns the transport now, so clear the local BEFORE the lighting step: a
            // throw from ApplyLighting must not make the catch dispose a handle the registered
            // controller will also dispose on teardown. The borrowed reference drives the one-time
            // replay, applied AFTER the discovery handshake (the TL constructor reads it) so a
            // pending lighting ack cannot corrupt fan discovery; a later RPM poll self-corrects.
            IHidTransport ownedTransport = transport;
            transport = null;
            ApplyLighting(ownedTransport, info, lightingConfigs);
#else
            transport = null; // ownership passed to the controller, which disposes it
#endif
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "  controller pid=0x{0:x4} kind={1} channels={2} path={3}",
                info.ProductId,
                kind,
                controller.ChannelCount,
                info.DevicePath));
        }
#pragma warning disable CA1031 // resilience: a device that fails to open or handshake is skipped, not fatal
        catch (Exception ex) {
            transport?.Dispose();
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "  open failed pid=0x{0:x4}: {1}",
                info.ProductId,
                ex.Message));
        }
#pragma warning restore CA1031
    }

    /// <summary>Register a control sensor and a fan sensor for every populated channel of every controller.</summary>
    public void Load(IPluginSensorsContainer container) {
        if (container is null) {
            throw new ArgumentNullException(nameof(container));
        }

        foreach (IFanDevice controller in _controllers) {
            for (int ch = 0; ch < controller.ChannelCount; ch++) {
                // Skip a channel with no fan attached so an empty slot is not surfaced as a dead
                // sensor. The sensor id is keyed to the physical channel (see Describe), so the
                // populated channels keep their ids and the user's saved curve bindings survive;
                // FanControl greys out a binding whose sensor is absent and re-links it if the fan
                // reappears, rather than discarding the rest of the config.
                if (!controller.IsChannelPopulated(ch)) {
                    continue;
                }

                container.ControlSensors.Add(new ControlSensor(controller, ch));
                container.FanSensors.Add(new FanSensor(controller, ch));
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
    // The product ids this build drives lighting for: the Uni fan families (SL, AL, SL-Infinity,
    // SL v2, AL v2, and the Redragon SL variant), the lighting-only Strimer Plus, and the 0x0416
    // controllers (Uni Fan TL, Galahad II). A located device of any other family is left untouched
    // rather than driven with unverified bytes.
    private const int SlProductId = 0xA100;
    private const int AlProductId = 0xA101;
    private const int SlInfinityProductId = 0xA102;
    private const int SlV2ProductId = 0xA103;
    private const int AlV2ProductId = 0xA104;
    private const int SlV2AlternateProductId = 0xA105;
    private const int SlRedragonProductId = 0xA106;
    private const int StrimerPlusProductId = 0xA200;
    private const int TlFanProductId = 0x7372;
    private const int Galahad2PerformanceProductId = 0x7371;
    private const int Galahad2RegularProductId = 0x7373;

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

        // Choose the encoder by the located device's hardware-read product id (authoritative),
        // not one parsed from the config file. An unsupported family is left exactly as-is.
        IReadOnlyList<LightingTransfer> transfers;
        switch (info.ProductId)
        {
            case SlInfinityProductId:
                transfers = SlInfinityLightingEncoder.Encode(match.Ports, match.Quantity);
                break;
            case SlProductId:
            case SlRedragonProductId:
                transfers = UniFanLightingEncoder.Encode(UniFanLightingProfiles.Sl, match.Ports, match.Quantity);
                break;
            case AlProductId:
                transfers = UniFanLightingEncoder.Encode(UniFanLightingProfiles.Al, match.Ports, match.Quantity);
                break;
            case SlV2ProductId:
            case SlV2AlternateProductId:
                transfers = UniFanLightingEncoder.Encode(UniFanLightingProfiles.SlV2, match.Ports, match.Quantity);
                break;
            case AlV2ProductId:
                transfers = UniFanLightingEncoder.Encode(UniFanLightingProfiles.AlV2, match.Ports, match.Quantity);
                break;
            case StrimerPlusProductId:
                transfers = StrimerPlusLightingEncoder.Encode(match.Ports);
                break;
            case TlFanProductId:
                if (match.TlFans is null || match.TlFans.Count == 0)
                {
                    // Only per-fan TL looks replay; a purely grouped/merged saved look carries no
                    // per-fan address here, so there is nothing to drive.
                    _log.Write(string.Format(
                        CultureInfo.InvariantCulture, "  lighting skipped for {0}: no per-fan TL look saved", match.InstanceToken));
                    return;
                }

                transfers = TlFanLightingEncoder.Encode(match.TlFans);
                break;
            case Galahad2PerformanceProductId:
            case Galahad2RegularProductId:
                if (match.GalahadFan is null || match.GalahadPump is null)
                {
                    _log.Write(string.Format(
                        CultureInfo.InvariantCulture, "  lighting skipped for {0}: incomplete Galahad look saved", match.InstanceToken));
                    return;
                }

                transfers = Galahad2LightingEncoder.Encode(match.GalahadFan, match.GalahadPump);
                break;
            default:
                _log.Write(string.Format(
                    CultureInfo.InvariantCulture,
                    "  lighting skipped for {0}: family pid=0x{1:x4} not supported",
                    match.InstanceToken,
                    info.ProductId));
                return;
        }

        // A lighting write the device rejects must not drop the device: lighting is opt-in and
        // isolated, so disable it for this device and let fan control (if any) proceed.
        try
        {
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

    // Drive a lighting-only device (no fan protocol, e.g. Strimer Plus): open it, apply the saved
    // look, then dispose - nothing owns it afterwards since there is no fan control to keep alive.
    private void DriveLightingOnlyDevice(HidDeviceInfo info, IReadOnlyList<LConnectControllerConfig> configs)
    {
        IHidTransport? transport = null;
        try
        {
            transport = _enumerator.Open(info);
            ApplyLighting(transport, info, configs);
        }
#pragma warning disable CA1031 // host seam: a lighting-only device that fails to open is skipped, never fatal
        catch (Exception ex)
        {
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "  lighting-only open failed pid=0x{0:x4}: {1}",
                info.ProductId,
                ex.Message));
        }
#pragma warning restore CA1031
        finally
        {
            transport?.Dispose();
        }
    }
#endif

    // Order located devices by their OS device path (ordinal) so the same physical port keeps the
    // same controller index - and therefore the same sensor ids - across restarts.
    private static List<HidDeviceInfo> SortByDevicePath(IReadOnlyList<HidDeviceInfo> devices) {
        var sorted = new List<HidDeviceInfo>(devices);
        sorted.Sort((a, b) => string.CompareOrdinal(a.DevicePath, b.DevicePath));
        return sorted;
    }

    // Caller must hold _sync.
    private void TearDownLocked() {
        _worker?.Dispose();
        _worker = null;
        _controllers.Clear();
    }
}
