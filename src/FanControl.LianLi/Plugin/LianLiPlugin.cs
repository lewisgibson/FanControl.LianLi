using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Logging;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Transport;
using FanControl.LianLi.Worker;
using FanControl.Plugins;

namespace FanControl.LianLi.Plugin;

/// <summary>
/// FanControl entry point for Lian Li controllers. This is the only public type in
/// the assembly: it composes the transports, the per-device protocol encoders, the
/// injectable clock and the keepalive worker, and exposes the
/// <see cref="IPlugin3"/> surface to the host. FanControl creates a new one of these
/// on every refresh, so what must outlive it lives in <see cref="PluginRuntime"/>.
/// The host's threads only touch in-memory state, apart from the bounded device
/// scan and builds in <see cref="Initialize"/>; every other USB transfer happens on
/// the worker thread.
/// </summary>
public sealed class LianLiPlugin : IPlugin3, IDisposable {

    private readonly IDeviceEnumerator _enumerator;
    private readonly DeviceCatalog _catalog;
    private readonly IClock _clock;
    private readonly IDelay _delay;
    private readonly ILog _log;

    // FanControl calls Initialize/Update/Close on its own threads. This serializes them, so a
    // Close cannot tear down what a concurrent Initialize is still building.
    private readonly object _sync = new object();
    private readonly List<IFanDevice> _controllers = new List<IFanDevice>();

    // The stand-in registered for each plan whose build was still running at the deadline, so the
    // controller that build produces is handed to it rather than opened a second time.
    private readonly Dictionary<string, ReconnectingFanDevice> _lateStandIns = new Dictionary<string, ReconnectingFanDevice>(StringComparer.Ordinal);

    // Whether a sensor may still turn up after Load: a wireless pair is driven (built, stood in for,
    // or still opening) whose devices may check in, or a build was still running at the deadline.
    // Only then is a WaitingSensor registered when nothing else is.
    private bool _mayStillRegister;

    // The runtime's numbering epoch this instance's scan ran under. A controller this instance built
    // or rebuilt is remembered only while it still holds: once the saved controllers have been read
    // late, its index may be one the file gives another controller.
    private volatile int _numberingEpoch;

    // The key of every stand-in this scan registered: one that took its controller before Load ran
    // may have learned sensors Load never saw, which Load checks for.
    private readonly List<string> _standInKeys = new List<string>();

    // What must outlive this instance - the running worker and the controllers remembered across
    // scans - lives in the process-wide runtime, because FanControl creates a new plugin object on
    // every refresh and never closes one that registered no sensors.
    private readonly PluginRuntime _runtime;

    // Where L-Connect's saved documents are: the machine's own in the host, a fixture in a test.
    private readonly LConnectLocations _lConnect;

    // The sensor ids the last Load registered, or null before it has run. A controller that gains a
    // sensor asks for a refresh only for one not in here: one gained before Load was registered by it.
    // Volatile rather than under the instance lock: the worker reads it when a device checks in, and
    // Close holds that lock while it waits for the worker, so a lock here would hold Close up for
    // the worker's whole stop bound. The set is built before it is published and never changed after.
    private volatile HashSet<string>? _registeredSensorIds;

#if ENABLE_LIGHTING
    // L-Connect's saved looks, read at the start of each Initialize and only read after that, by
    // the builds and by any stand-in rebuilding a controller later.
    private volatile IReadOnlyList<LConnectControllerConfiguration> _lightingConfigurations = Array.Empty<LConnectControllerConfiguration>();
#endif

    /// <summary>
    /// Host-injected constructor. FanControl supplies the logger; the plugin
    /// logs through both it and a local file as a fallback.
    /// </summary>
    public LianLiPlugin(IPluginLogger logger)
        : this(logger, new FileLogger()) {
    }

    // The file log is also where a failure of the host's logger is told.
    private LianLiPlugin(IPluginLogger logger, FileLogger file)
        : this(new CompositeLog(new PluginLoggerLog(logger, file), file)) {
    }

    // Wire the real HID enumerator with the same log the rest of the plugin uses, so an
    // enumeration probe that fails leaves a trace. A separate ctor because the enumerator and the
    // log are siblings the public ctor cannot reference from a single chained call.
    private LianLiPlugin(ILog log)
        : this(new WindowsDeviceEnumerator(log), new DeviceCatalog(), new SystemClock(), new SystemDelay(), log, LConnectLocations.Machine, PluginRuntime.Process) {
    }

    /// <summary>Composition/test constructor that accepts fakes for every dependency.</summary>
    internal LianLiPlugin(
        IDeviceEnumerator enumerator,
        DeviceCatalog catalog,
        IClock clock,
        IDelay delay,
        ILog log,
        LConnectLocations lConnect,
        PluginRuntime runtime) {
        _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _lConnect = lConnect ?? throw new ArgumentNullException(nameof(lConnect));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
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

    /// <summary>
    /// How long a scan's controller builds may keep FanControl waiting. FanControl holds the lock
    /// that serialises every device it drives while it initialises a plugin, so this is time the
    /// whole application stands still; each build is bounded on its own (a 2 s open, a handshake),
    /// and side by side they fit. It is one of three bounds on an Initialize - stopping the previous
    /// worker (2 s) and the scan (5 s) come first - so the worst case is about 12 s, once. Settable so a test can see the deadline pass without waiting it out.
    /// </summary>
    internal int BuildDeadlineMilliseconds { get; set; } = 5000;

    /// <summary>
    /// Raised on FanControl's thread, from <see cref="Update"/>, when the devices changed in a way
    /// only a refresh can show: a wireless device checked in after the scan, or a controller
    /// finished opening after the scan had moved on.
    /// </summary>
    public event Action? RefreshRequested;

    private void InitializeLocked() {
        // Whatever is running goes first: this instance's own last run, or an earlier instance's
        // that FanControl never closed because it had registered no sensors.
        _runtime.Stop();
        _controllers.Clear();
        _lateStandIns.Clear();
        _mayStillRegister = false;
        _standInKeys.Clear();
        _runtime.Restore(_log);
        _numberingEpoch = _runtime.NumberingEpoch;
        _runtime.ForgetExpired(_log);

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
        _lightingConfigurations = ReadLConnectLighting();
#endif

        // Every build also locates the 0x0416 command-packet controllers (Uni Fan TL, Galahad II);
        // the Lighting build additionally locates lighting-only products (Strimer Plus) to drive
        // their RGB. The enumerator requires both vendor and product to match, so listing a product
        // id here is what opts a family into discovery.
        var productIds = new List<int>(_catalog.ProductIds);
        productIds.AddRange(_catalog.CommandPacketProductIds);
        productIds.AddRange(_catalog.WirelessProductIds);
#if ENABLE_LIGHTING
        productIds.AddRange(_catalog.LightingProductIds);
#endif

        IReadOnlyList<LocatedDevice> located;
        bool scanFailed = false;
        try {
            located = _enumerator.Locate(_catalog.VendorIds, productIds);
        }
#pragma warning disable CA1031 // host seam: a device scan failure must not crash FanControl
        catch (Exception ex) {
            // The scan asks Windows' configuration manager and HID stack for every candidate
            // interface, any of which can fail in some host or session contexts. Degrade to the
            // remembered controllers and log it rather than let the exception propagate into the
            // host - the same host-seam resilience the per-device open catch below applies, one
            // level up.
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "Initialize: device scan failed: {0}; standing in for the {1} controller(s) remembered from earlier scans",
                ex.Message,
                _runtime.RememberedCount));
            located = Array.Empty<LocatedDevice>();
            scanFailed = true;
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

        // The wireless dongles come in pairs that make one controller, so they are collected
        // during the walk and planned once it is done.
        var plans = new List<ControllerPlan>();
        var transmitters = new List<LocatedDevice>();
        var receivers = new List<LocatedDevice>();
        foreach (LocatedDevice info in located) {
            DeviceKind kind = _catalog.Classify(info.VendorId, info.ProductId);
            switch (kind) {
                case DeviceKind.UniFan:
                case DeviceKind.TlFan:
                case DeviceKind.Galahad2:
                    plans.Add(new ControllerPlan(kind, info));
                    break;
                case DeviceKind.WirelessTransmitter:
                    transmitters.Add(info);
                    break;
                case DeviceKind.WirelessReceiver:
                    receivers.Add(info);
                    break;
#if ENABLE_LIGHTING
                case DeviceKind.LightingOnly:
                    // A lighting-only device (e.g. Strimer Plus) has no fan control: drive its saved
                    // look once, never registering a controller or worker for it. Nothing waits on
                    // it, so it runs on a thread of its own rather than inside FanControl's
                    // Initialize, where a wedged one would hold the whole application up.
                    IReadOnlyList<LConnectControllerConfiguration> configurations = _lightingConfigurations;
                    new Thread(() => DriveLightingOnlyDevice(info, configurations)) { IsBackground = true, Name = "LianLiLightingOnly" }.Start();
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

        PlanWirelessPair(transmitters, receivers, plans);
        BuildAll(plans, scanFailed);
        _runtime.Persist(_log);
        _runtime.Start(this, _controllers.ToArray(), _log);
    }

    // L-Connect drives one transmitter and one receiver - the first of each it finds - and so does
    // the plugin; a dongle without a partner, or beyond the first pair, is logged and left alone.
    // Where two kits are plugged in, the first transmitter and receiver by path may belong to
    // different kits, and a receiver names no device of another kit's master. So the pair is the
    // first transmitter and receiver Windows puts in the same physical device (their container),
    // and only when no two share one, the first of each, as L-Connect takes them.
    private void PlanWirelessPair(List<LocatedDevice> transmitters, List<LocatedDevice> receivers, List<ControllerPlan> plans) {
        LocatedDevice? transmitter = null;
        LocatedDevice? receiver = null;
        foreach (LocatedDevice candidate in transmitters) {
            receiver = receivers.FirstOrDefault(r => candidate.ContainerId != null && r.ContainerId == candidate.ContainerId);
            if (receiver != null) {
                transmitter = candidate;
                break;
            }
        }

        if (transmitter is null && transmitters.Count > 0 && receivers.Count > 0) {
            transmitter = transmitters[0];
            receiver = receivers[0];
        }

        if (transmitter != null) {
            plans.Add(new ControllerPlan(DeviceKind.WirelessTransmitter, transmitter, receiver!)); // set with the transmitter
        }

        LogUnused(transmitters, transmitter, receivers.Count > 0, "transmitter", "receiver");
        LogUnused(receivers, receiver, transmitters.Count > 0, "receiver", "transmitter");
    }

    private void LogUnused(List<LocatedDevice> dongles, LocatedDevice? used, bool paired, string role, string partner) {
        foreach (LocatedDevice dongle in dongles) {
            if (ReferenceEquals(dongle, used)) {
                continue;
            }

            _log.Write(paired
                ? "  wireless " + role + " not used (one pair is driven, as L-Connect does): " + dongle.DevicePath
                : "  wireless " + role + " without a " + partner + ", skipped: " + dongle.DevicePath);
        }
    }

    // Build every planned controller side by side under one deadline, at the index it keeps for
    // the life of the process, and register it; remember each one built, so a later scan that
    // cannot reach it can stand in for it. A device that failed, or was still opening at the
    // deadline, is stood in for if an earlier scan built it and skipped otherwise, never crashing
    // the host. So is every remembered controller this scan did not find at all - a scan that
    // failed, or a device that had not re-appeared yet - so the user's curves stay bound to it.
    // First, a planned device on a path nothing is remembered under takes over a remembered one
    // that is the same device moved (PluginRuntime.TakeOverMoved), so a move to another port keeps
    // its sensors rather than registering them twice.
    private void BuildAll(List<ControllerPlan> plans, bool scanFailed) {
        var plannedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (ControllerPlan plan in plans) {
            _ = plannedKeys.Add(plan.Key);
        }

        foreach (ControllerPlan plan in plans) {
            if (_runtime.TakeOverMoved(plan, plannedKeys) is string moved) {
                _log.Write("  " + plan.Key + " is taken as " + moved + " on a new path; it keeps that controller's sensors");
            }
        }

        // A device whose build from an earlier scan (or a stand-in's rebuild) is still running is not
        // opened a second time: it counts as still opening, and is stood in for until that finishes.
        var taken = new HashSet<int>();
        var indices = new int[plans.Count];
        var owned = new List<int>();
        for (int i = 0; i < plans.Count; i++) {
            indices[i] = _runtime.IndexFor(plans[i].Key, taken);
            if (_runtime.TryBeginBuildUnder(_numberingEpoch, plans[i].Key, indices[i])) {
                owned.Add(i);
            } else {
                _log.Write("  " + plans[i].Key + " is still being opened by an earlier build; not opened again");
            }
        }

        var builds = new Func<IFanDevice>[owned.Count];
        for (int b = 0; b < owned.Count; b++) {
            ControllerPlan plan = plans[owned[b]];
            int index = indices[owned[b]];
            builds[b] = () => Build(plan, index);
        }

        BuildOutcome[] ownedOutcomes = ControllerBuilder.Run(
            builds,
            BuildDeadlineMilliseconds,
            (b, controller) => OnLateController(plans[owned[b]], indices[owned[b]], controller),
            (b, error) => OnLateFailure(plans[owned[b]], error),
            _log);
        var outcomes = new BuildOutcome[plans.Count];
        for (int i = 0; i < plans.Count; i++) {
            outcomes[i] = BuildOutcome.Late();
        }

        for (int b = 0; b < owned.Count; b++) {
            outcomes[owned[b]] = ownedOutcomes[b];
            if (ownedOutcomes[b].Controller != null || ownedOutcomes[b].Error != null) {
                _runtime.EndBuild(plans[owned[b]].Key);
            }
        }


        // Keyed by index, so the controllers register in index order.
        var built = new SortedDictionary<int, IFanDevice>();
        for (int i = 0; i < plans.Count; i++) {
            ControllerPlan plan = plans[i];
            BuildOutcome outcome = outcomes[i];
            // A pair whose receiver has reported nothing yet, or that may still come - stood in for,
            // or still opening - can have devices check in after Load; one whose receiver already
            // reported devices, or whose open failed, has nothing more to wait for. A TL hub whose
            // handshake reported no fan yet is waiting the same way: its fans are added as they answer.
            // Each waits only while the controller is new to the plugin (IsNew): one an earlier run
            // remembered with nothing - a pair that only hears a neighbour's kit, say - must not show
            // the placeholder for good. A failed open waits only for a pair or hub already remembered.
            bool isNew = _runtime.IsNew(plan.Key);
            bool mayCheckIn = isNew && (outcome.Controller is WirelessController wireless
                ? !wireless.HasHeardDevices
                : outcome.Controller is TlFanController hub
                    ? hub.ChannelCount == 0
                    : outcome.Error is null || _runtime.Recall(plan.Key) != null);
            _mayStillRegister |= (GainsSensorsAfterLoad(plan.Kind) && mayCheckIn)
                || (outcome.Controller is null && outcome.Error is null && isNew);
            if (outcome.Controller != null) {
                built.Add(indices[i], Registered(plan, indices[i], outcome.Controller));
                continue;
            }

            // A build this scan did not start (another still had the device) was logged above; its
            // result goes to whoever started it, and the stand-in rebuilds once that has finished.
            bool startedHere = owned.Contains(i);
            if (outcome.Error != null) {
                _log.Write(string.Format(CultureInfo.InvariantCulture, "  open failed for {0}: {1}", plan.Key, outcome.Error.Message));
            } else if (startedHere) {
                _log.Write(string.Format(
                    CultureInfo.InvariantCulture,
                    "  {0} still opening after {1} ms; it is used as soon as it finishes",
                    plan.Key,
                    BuildDeadlineMilliseconds));
            }

            RememberedController? remembered = _runtime.Recall(plan.Key);
            if (remembered != null) {
                ReconnectingFanDevice standIn = StandIn(remembered);
                if (outcome.Error is null && startedHere) {
                    _lateStandIns[plan.Key] = standIn;
                }

                built.Add(remembered.Index, standIn);
            }
        }

        // One wireless pair is driven, as L-Connect drives one: a remembered pair other than the
        // one this scan chose (the dongles re-enumerated in another order, or a second pair was
        // unplugged) is not reopened alongside it.
        bool wirelessPlanned = plans.Any(p => p.Kind == DeviceKind.WirelessTransmitter);
        foreach (KeyValuePair<string, RememberedController> entry in _runtime.Remembered()) {
            if (plannedKeys.Contains(entry.Key)) {
                continue;
            }

            if (entry.Value.Plan.Kind == DeviceKind.WirelessTransmitter) {
                if (wirelessPlanned) {
                    _log.Write("  wireless pair " + entry.Key + " from an earlier scan not used: another pair is driven");
                    continue;
                }

                wirelessPlanned = true;
            }

            if (GainsSensorsAfterLoad(entry.Value.Plan.Kind)) {
                _mayStillRegister |= _runtime.IsNew(entry.Key);
            }

            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "  {0} not found by this scan{1}",
                entry.Key,
                scanFailed ? " (the scan failed)" : string.Empty));
            built.Add(entry.Value.Index, StandIn(entry.Value));
        }

        _controllers.AddRange(built.Values);
    }

    // Remember a controller that has just been built, and choose what registers for it: the
    // controller itself, or - when it came back without some sensor it registered before - a
    // stand-in around it carrying the union, so a curve bound to the missing one is not dropped.
    private IFanDevice Registered(ControllerPlan plan, int index, IFanDevice controller) {
        var current = new RememberedController(plan, index, controller, _runtime.UtcNow);
        RememberedController merged = _runtime.Remember(plan.Key, current);
        if (current.Covers(merged)) {
            return controller;
        }

        // The wrapper registers the remembered sensors, not the controller's; one the controller
        // learns before Load (a wireless device checking in) is caught by Load's check of stand-ins.
        _standInKeys.Add(plan.Key);
        return ReconnectingFanDevice.Around(
            controller, index, merged.Channels, merged.FanSpeeds, merged.Temperatures, () => Rebuild(plan, index), _log);
    }

    // A controller reported sensors after the scan: a wireless device checked in, or a stand-in took
    // the controller it was standing in for, which may have more than was remembered. They are
    // remembered and saved first - the refresh that registers them closes this instance, and the
    // next one may not reach the device at once, so it must stand in with them - and only a sensor
    // Load has not registered needs that refresh. This runs on the worker's or a rebuild's thread,
    // so nothing may escape it.
    internal void OnSensorsReported(ControllerPlan plan, int index, IFanDevice controller, string reason) {
        try {
            if (_runtime.RememberUnder(_numberingEpoch, plan.Key, new RememberedController(plan, index, controller, _runtime.UtcNow)) is null) {
                _log.Write("  " + plan.Key + ": its sensors were numbered before the saved controllers were read; not remembered");
                return;
            }

            _runtime.Persist(_log);
            if (HasUnregisteredSensor(SensorIds(controller), _registeredSensorIds)) {
                _runtime.RequestRefresh(reason, _log);
            }
        }
#pragma warning disable CA1031 // resilience: this runs on a thread of the plugin's own, where an escaping exception ends FanControl's process
        catch (Exception ex) {
            _log.Write("  " + plan.Key + ": the sensors it reported could not be remembered: " + ex.Message);
        }
#pragma warning restore CA1031
    }

    // The kinds whose sensors can arrive after Load: a wireless pair's devices check in over radio,
    // and a TL hub's fans are added as they answer.
    private static bool GainsSensorsAfterLoad(DeviceKind kind)
        => kind == DeviceKind.WirelessTransmitter || kind == DeviceKind.TlFan;

    // Whether any of ids is one Load did not register. Before Load has run (registered null) there
    // is nothing to refresh for: Load itself registers whatever the controller has by then.
    internal static bool HasUnregisteredSensor(IEnumerable<string> ids, HashSet<string>? registered)
        => registered != null && ids.Any(id => !registered.Contains(id));

    // Every sensor id a remembered controller's stand-in registers.
    private static IEnumerable<string> SensorIds(RememberedController remembered)
        => remembered.Channels.Select(c => c.ControlId)
            .Concat(remembered.FanSpeeds.Select(f => f.Id))
            .Concat(remembered.Temperatures.Select(t => t.Id));

    // Every sensor id a controller would have Load register for it.
    private static IEnumerable<string> SensorIds(IFanDevice controller) {
        for (int ch = 0; ch < controller.ChannelCount; ch++) {
            if (controller.IsChannelPopulated(ch)) {
                yield return controller.Describe(ch).ControlId;
            }
        }

        if (controller is IFanSpeedSource speeds) {
            for (int f = 0; f < speeds.FanSpeedCount; f++) {
                yield return speeds.DescribeFanSpeed(f).Id;
            }
        }

        if (controller is ITemperatureSource temperatures) {
            for (int t = 0; t < temperatures.TemperatureCount; t++) {
                yield return temperatures.DescribeTemperature(t).Id;
            }
        }
    }

    // A build that finished after the deadline is remembered, so every later scan stands in for it
    // if it is slow again, and then goes to the stand-in this scan registered for it, if there is
    // one and it has not connected by itself meanwhile. Only a controller no scan had built before
    // - one with no stand-in - needs a FanControl refresh to be registered at all, and a refresh
    // restarts every FanControl source, not just this plugin, so it is asked for only then: once
    // per new device, however often it is slow. Under the instance's lock, which Initialize holds
    // until the stand-ins are recorded; this runs on the build's own thread, so waiting costs
    // FanControl nothing.
    private void OnLateController(ControllerPlan plan, int index, IFanDevice controller) {
        bool known;
        lock (_sync) {
            known = _runtime.Recall(plan.Key) != null;
            RememberedController? remembered = _runtime.RememberUnder(
                _numberingEpoch, plan.Key, new RememberedController(plan, index, controller, _runtime.UtcNow));

            // Only now that the index is remembered may it stop being held for the build.
            _runtime.EndBuild(plan.Key);
            if (remembered is null) {
                // Numbered before the saved controllers were read, so the index may be another's: it
                // is built again by the next scan, under the saved numbering.
                _log.Write("  " + plan.Key + " finished opening after the scan, numbered before the saved controllers were read; built again by the next scan");
            } else {
                _runtime.Persist(_log);
                if (_lateStandIns.TryGetValue(plan.Key, out ReconnectingFanDevice? standIn) && standIn.Offer(controller)) {
                    _log.Write("  " + plan.Key + " finished opening after the scan; its stand-in took it");
                    return;
                }
            }
        }

        controller.Dispose();
        if (!known) {
            _runtime.RequestRefresh("a controller finished opening after the scan", _log);
        }
    }

    private void OnLateFailure(ControllerPlan plan, Exception error) {
        _runtime.EndBuild(plan.Key);
        _log.Write("  " + plan.Key + ": the build that ran past the deadline failed: " + error.Message);
    }

    // Build the controller a plan describes, at index. Throws when the device will not open or
    // answer, for BuildAll (or a stand-in's rebuild) to handle.
    private IFanDevice Build(ControllerPlan plan, int index) {
        LocatedDevice first = plan.Devices[0];
        switch (plan.Kind) {
            case DeviceKind.UniFan:
                return BuildUniController(index, first, _catalog.ProtocolFor(first.ProductId));
            case DeviceKind.WirelessTransmitter:
                return BuildWirelessController(plan, index);
            default:
                return BuildCommandPacketController(plan, index, first);
        }
    }

    // A stand-in with the remembered controller's sensors, rebuilding it in the background through
    // this instance - the plan says what to build, so the instance that remembered it need not exist.
    private ReconnectingFanDevice StandIn(RememberedController remembered) {
        _log.Write(string.Format(
            CultureInfo.InvariantCulture,
            "  kept {0} channel(s) of {1} from an earlier scan; reconnecting in the background",
            remembered.Channels.Count,
            remembered.Plan.Key));
        var standIn = new ReconnectingFanDevice(
            remembered.Index,
            remembered.Channels,
            remembered.FanSpeeds,
            remembered.Temperatures,
            () => Rebuild(remembered.Plan, remembered.Index),
            _log);
        standIn.Adopted += (_, controller) => OnSensorsReported(
            remembered.Plan, remembered.Index, controller, "a controller came back with sensors the scan did not have");
        _standInKeys.Add(remembered.Plan.Key);
        return standIn;
    }

    // A stand-in's rebuild takes the device's build ownership like a scan's build does, so it never
    // opens a device another build still has; that attempt fails, and the backoff tries again.
    internal IFanDevice Rebuild(ControllerPlan plan, int index) {
        if (!_runtime.TryBeginBuildUnder(_numberingEpoch, plan.Key, index)) {
            // Either another build still has the device, or this instance numbered it before the
            // saved controllers were read and the next scan opens it under the saved numbering.
            throw new InvalidOperationException(plan.Key + " is still being opened by another build, or by the next scan");
        }

        try {
            return Build(plan, index);
        } finally {
            _runtime.EndBuild(plan.Key);
        }
    }

    private WirelessController BuildWirelessController(ControllerPlan plan, int index) {
        LocatedDevice transmitterInfo = plan.Devices[0];
        LocatedDevice receiverInfo = plan.Devices[1];
        // Every build reads the saved channel and pump settings, so the master stays on the user's
        // channel and driving a wireless pump leaves the AIO screen as they set it. Only the
        // Lighting build reads the saved effects. A bad file costs only its own feature.
#if ENABLE_LIGHTING
        const bool readsEffects = true;
#else
        const bool readsEffects = false;
#endif
        var configuration = new LConnectWirelessConfiguration(
            _lConnect.WirelessDirectory, _lConnect.WirelessPumpSettingPath, readsEffects, _log);

        // The controller owns both dongles once it is made. Until then they are closed here on any
        // failure - the receiver refusing to open, or the controller's first reads throwing - since
        // WinUSB lets a device be open only once and a leaked handle would fail every later open.
        IDeviceTransport transmitter = _enumerator.Open(transmitterInfo);
        IDeviceTransport? receiver = null;
        WirelessController controller;
        try {
            receiver = _enumerator.Open(receiverInfo);
            controller = new WirelessController(index, transmitter, receiver, configuration, _clock, _delay, _log, _runtime.WirelessState);
        } catch {
            receiver?.Dispose();
            transmitter.Dispose();
            throw;
        }

        controller.TopologyChanged += (_, _) => OnSensorsReported(plan, index, controller, "a wireless device checked in after the scan");
        _log.Write(string.Format(
            CultureInfo.InvariantCulture,
            "  controller wireless master={0} channel={1} devices={2} controls={3} fans={4} lit={5} transmitter={6} receiver={7}",
            controller.MasterMacText ?? "not answering yet",
            controller.Channel,
            controller.DeviceCount,
            controller.ChannelCount,
            controller.FanSpeedCount,
            controller.LitDeviceCount,
            transmitterInfo.DevicePath,
            receiverInfo.DevicePath));
        return controller;
    }

    // Open a Uni 0x0CF2 controller and apply its saved lighting (Lighting build). A device that
    // fails to open throws to Register, which skips it rather than crashing the host; once the
    // controller exists, its setup writes and population probe are guarded individually so a
    // rejected write degrades the feature, never the controller.
    private FanController BuildUniController(int index, LocatedDevice info, IFanProtocol protocol) {
        bool[] startStopEnabled = ReadStartStop(info, protocol.ChannelCount);

        // The transport goes straight to the controller, which owns and disposes it from here; its
        // constructor does no I/O, so the device still sees the look first, then the fan setup. A
        // device that will not open throws to the caller with nothing left to release.
        IDeviceTransport transport = _enumerator.Open(info);
        var controller = new FanController(index, transport, protocol, startStopEnabled, _clock, _log);
#if ENABLE_LIGHTING
        // Re-apply L-Connect's saved look before fan setup, for a controller that has a matching
        // saved config. No match -> no lighting, leaving the device exactly as another tool
        // (OpenRGB, the motherboard) left it. The look is volatile on the device, so it is also
        // replayed whenever the transport reconnects a re-enumerated (possibly reset) controller -
        // the same guarded apply, driven on the worker thread through the controller's replay.
        ApplyLighting(transport, info, _lightingConfigurations);
        controller.ReplayOnReconnect(() => ApplyLighting(transport, info, _lightingConfigurations));
#endif
        AssertManualMode(controller, info);
        DetectPopulation(controller, info);
        _log.Write(string.Format(
            CultureInfo.InvariantCulture,
            "  controller pid=0x{0:x4} family={1} startStop={2} path={3}",
            info.ProductId,
            protocol.Family,
            FormatStartStop(startStopEnabled),
            info.DevicePath));
        return controller;
    }

    // Assert manual (software) mode on the registered Uni controller so the host owns the speed. A
    // rejected setup write must not lose the controller: the worker re-asserts manual mode before
    // every speed write, so control recovers on the first accepted write. The fault is isolated here
    // at the composition seam - the same host-seam resilience intent as the per-device open guard -
    // and logged.
    private void AssertManualMode(FanController controller, LocatedDevice info) {
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
    private void DetectPopulation(FanController controller, LocatedDevice info) {
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
    private bool[] ReadStartStop(LocatedDevice info, int channelCount) {
        try {
            // Log whether the profile file was located, so "start/stop does nothing" can be told apart
            // from "profile found but the toggle is off" without guessing at the MD5 filename mapping.
            // Inside the guard: ProfileFileName's MD5 can itself throw (a FIPS-enforced .NET Framework
            // host rejects MD5.Create), and that must disable start/stop, never drop the controller.
            string profilePath = System.IO.Path.Combine(
                _lConnect.ProfileDirectory, StartStopConfigurationReader.ProfileFileName(info.DevicePath));
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "  start/stop profile {0}: {1}",
                System.IO.File.Exists(profilePath) ? "found" : "absent",
                profilePath));

            return StartStopConfigurationReader.Read(
                _lConnect.ProfileDirectory, info.DevicePath, channelCount);
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

    // Open a 0x0416 command-packet controller (Uni Fan TL or Galahad II). The controller's
    // constructor performs the discovery handshake, so a wrong interface or an absent device
    // surfaces here as a read timeout, thrown to Register, which skips it rather than crashing the host.
    private IFanDevice BuildCommandPacketController(ControllerPlan plan, int index, LocatedDevice info) {
        DeviceKind kind = plan.Kind;
        IDeviceTransport? transport = null;
        try {
            transport = _enumerator.Open(info);
            IFanDevice controller;
            if (kind == DeviceKind.Galahad2) {
                controller = new Galahad2Controller(index, transport, _clock, _log);
            } else {
                var hub = new TlFanController(index, transport, _clock, _log);
                hub.TopologyChanged += (_, _) => OnSensorsReported(plan, index, hub, "a TL fan answered after the scan");
                controller = hub;
            }
#if ENABLE_LIGHTING
            // The controller owns the transport now, so clear the local BEFORE the lighting step: a
            // throw from ApplyLighting must not make the catch dispose a handle the controller will
            // also dispose. The borrowed reference drives the one-time replay, applied AFTER the
            // discovery handshake (the TL constructor reads it) so a pending lighting ack cannot
            // corrupt fan discovery; a later RPM poll self-corrects. ApplyLighting never throws.
            IDeviceTransport ownedTransport = transport;
            transport = null;
            ApplyLighting(ownedTransport, info, _lightingConfigurations);
            // And again whenever the transport reconnects a re-enumerated (possibly reset) device.
            controller.ReplayOnReconnect(() => ApplyLighting(ownedTransport, info, _lightingConfigurations));
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
            return controller;
        } catch {
            transport?.Dispose();
            throw;
        }
    }

    /// <summary>Register a control sensor and a fan sensor for every populated channel of every controller.</summary>
    public void Load(IPluginSensorsContainer container) {
        if (container is null) {
            throw new ArgumentNullException(nameof(container));
        }

        var registered = new HashSet<string>(StringComparer.Ordinal);
        foreach (IFanDevice controller in _controllers) {
            registered.UnionWith(SensorIds(controller));
        }

        _registeredSensorIds = registered;

        foreach (string key in _standInKeys) {
            if (HasUnregisteredSensor(SensorIds(_runtime.Recall(key)!), registered)) { // a stand-in is made only for a remembered key
                _runtime.RequestRefresh("a controller came back with sensors the scan did not have", _log);
            }
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

                container.ControlSensors.Add(new ControlSensor(controller, ch, _runtime.Wake));
                if (!(controller is IFanSpeedSource)) {
                    container.FanSensors.Add(new FanSensor(controller, ch));
                }
            }

            // A device with more fans than controls (a wireless group: one control, a reading per
            // fan) reports its fan speeds on their own.
            if (controller is IFanSpeedSource speeds) {
                for (int f = 0; f < speeds.FanSpeedCount; f++) {
                    container.FanSensors.Add(new FanSpeedSensor(speeds, f));
                }
            }

            // A device that also measures temperatures (a wireless water block's coolant) gets a
            // temperature sensor per reading, usable as a curve source.
            if (controller is ITemperatureSource temperatures) {
                for (int t = 0; t < temperatures.TemperatureCount; t++) {
                    container.TempSensors.Add(new TemperatureSensor(temperatures, t));
                }
            }
        }

        if (registered.Count == 0 && _mayStillRegister) {
            container.TempSensors.Add(new WaitingSensor());
            _log.Write("nothing to register yet but a device is still to come; a placeholder sensor is registered so FanControl hears the refresh that follows it");
        }
    }

    /// <summary>
    /// Host update tick. It does no device I/O - FanControl calls it holding the lock that
    /// serialises every device it drives, so the worker thread does all of that - and only raises
    /// <see cref="RefreshRequested"/> here, on FanControl's own thread, when a refresh is wanted.
    /// </summary>
    public void Update() {
        if (_runtime.TryTakeRefresh(this, out string reason)) {
            _log.Write("requesting a FanControl refresh: " + reason);
            RefreshRequested?.Invoke();
        }
    }

    /// <summary>Stop the worker and release the controllers, if this instance is the one running them.</summary>
    public void Close() {
        lock (_sync) {
            _runtime.StopIfOwnedBy(this);
            _controllers.Clear();
            _standInKeys.Clear();
            _lateStandIns.Clear();
        }
    }

    /// <summary>Dispose the plugin. Equivalent to <see cref="Close"/>; safe to call more than once.</summary>
    public void Dispose() {
        Close();
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
    private IReadOnlyList<LConnectControllerConfiguration> ReadLConnectLighting()
    {
        try
        {
            IReadOnlyList<LConnectControllerConfiguration> configurations =
                LConnectConfigurationReader.Read(_lConnect.DeviceDirectory);
            if (configurations.Count > 0)
            {
                _log.Write(string.Format(
                    CultureInfo.InvariantCulture, "Lighting: read {0} L-Connect controller look(s)", configurations.Count));
            }

            return configurations;
        }
#pragma warning disable CA1031 // opt-in feature: a bad lighting config disables lighting, never breaks fan control
        catch (Exception ex)
        {
            _log.Write("Lighting: config read failed, lighting disabled: " + ex.Message);
            return Array.Empty<LConnectControllerConfiguration>();
        }
#pragma warning restore CA1031
    }

    // Re-apply the saved look for one located controller, matched to its L-Connect config by
    // instance token. No match -> no lighting. A matched controller of an unsupported family
    // is logged and skipped (its lighting is left as-is), never driven with guessed bytes.
    private void ApplyLighting(
        IDeviceTransport transport, LocatedDevice info, IReadOnlyList<LConnectControllerConfiguration> configurations)
    {
        LConnectControllerConfiguration? match = null;
        foreach (LConnectControllerConfiguration configuration in configurations)
        {
            if (info.DevicePath.IndexOf(configuration.InstanceToken, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                match = configuration;
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
    private void DriveLightingOnlyDevice(LocatedDevice info, IReadOnlyList<LConnectControllerConfiguration> configurations)
    {
        IDeviceTransport? transport = null;
        try
        {
            transport = _enumerator.Open(info);
            ApplyLighting(transport, info, configurations);
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

        try
        {
            transport?.Dispose();
        }
#pragma warning disable CA1031 // host seam: this runs on the plugin's own thread, where an escaping exception would end FanControl's process
        catch (Exception ex)
        {
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "  lighting-only close failed pid=0x{0:x4}: {1}",
                info.ProductId,
                ex.Message));
        }
#pragma warning restore CA1031
    }
#endif

    // Order located devices by their OS device path (ordinal) so the same physical port keeps the
    // same controller index - and therefore the same sensor ids - across restarts.
    private static List<LocatedDevice> SortByDevicePath(IReadOnlyList<LocatedDevice> devices) {
        var sorted = new List<LocatedDevice>(devices);
        sorted.Sort((a, b) => string.CompareOrdinal(a.DevicePath, b.DevicePath));
        return sorted;
    }
}
