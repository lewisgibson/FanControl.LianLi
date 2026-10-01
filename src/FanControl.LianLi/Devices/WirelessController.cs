using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using FanControl.LianLi.Logging;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Transport;

namespace FanControl.LianLi.Devices;

/// <summary>
/// Drives one L-Wireless SYNC master - the transmitter dongle that carries RF commands and the
/// receiver dongle that reports every device it can hear - the way L-Connect's <c>MasterDevice</c>
/// drives it (<c>docs/wireless.md</c> walks through each part with its L-Connect source).
///
/// <para>Every list read (<see cref="PollRpm"/>, <c>MasterDevice.RefreshList</c>) updates a
/// <see cref="WirelessDeviceTable"/> kept in first-heard order. A device whose latest record names
/// this master is driven; one heard naming it for the first time gets its FanControl sensors -
/// one control per fan group (L-Connect repeats one duty across the group's four slots), the water
/// block's pump, the Lancool 217's and the V150's front pair and rear fan, one RPM reading per fan
/// and a water block's coolant temperature - and <see cref="TopologyChanged"/> tells the plugin to
/// ask the host for a refresh. Sensors are only added, so an index always names the same
/// thing; a device that goes unheard keeps them and reads 0 rpm and no temperature until it is
/// heard again, whether the table has dropped it meanwhile, as L-Connect's does, or not.</para>
///
/// <para>Once a second (<see cref="ApplyPending"/>, the one-second block of <c>MasterDevice.Run</c>)
/// the controller resends each driven device's speed whenever what it reports is more than 5 off
/// its target (<c>SyncPwm</c>), hands a device whose L-Connect sync switch is saved on its lighting
/// to the motherboard (<c>SyncControlInfo</c>'s 0x27), checks the periodic save, re-sends the
/// master query - which also re-asserts the master's RF channel and is how a master that has not
/// answered yet is found -, sends each driven water block its parameter block (<c>SendAioInfo</c>),
/// broadcasts the clock pulse with the LCD FLEX screen table (<c>SyncMasterClock</c>), sends each
/// LCD FLEX screen its theme's colours (0x28) and switches the screens that are off their wireless
/// theme back onto it (0x29). The commands sent under a command sequence go one at a time per
/// device, so the sequence it reports back acknowledges exactly the command that carried it. On
/// every call it also streams any saved lighting effect a device is not running
/// (<c>SyncRgbData</c>, whatever its sync switch says) and sends the debounced save.</para>
///
/// <para>Construction does not wait on the hardware for long and never throws for it: it asks for
/// the master a few times and, once it answers, reads the list until two reads in a row agree - at
/// most two and a half seconds of waiting. A master that never answered leaves a controller with no
/// sensors that keeps asking every second. Every piece of per-device work is isolated, so one
/// device's failure is logged and costs only that device that second. The FanControl-thread methods
/// only touch locked state; all I/O is on the worker-thread methods and the constructor.</para>
/// </summary>
internal sealed class WirelessController : IFanDevice, ITemperatureSource, IFanSpeedSource, IDrivenSensorSource {
    // MasterDevice.Run's one-second block.
    private static readonly TimeSpan CycleInterval = TimeSpan.FromSeconds(1);

    // How long construction may spend on a master that has not answered, and on a list that is
    // still changing: at most three queries, then at most four list reads, half a second apart -
    // two and a half seconds of waiting in all, well inside the host's deadline for the whole of
    // Initialize. MasterDevice.Run reads the list at that pace (once more than 500 ms have passed).
    // L-Connect itself does not wait - Run keeps going - so the wait is the plugin's, there so
    // the host's first load has the devices that are already there , not one refresh each.
    private static readonly TimeSpan StartupInterval = TimeSpan.FromMilliseconds(500);
    private const int StartupMasterQueries = 3;
    private const int StartupListReads = 4;

    // SyncPwm sleeps 5 ms after each bound device, sent or not.
    private static readonly TimeSpan SpeedPacing = TimeSpan.FromMilliseconds(5);

    // SyncRgbData repeats the descriptor chunk three more times 20 ms apart, and waits 10 ms after
    // a stream before asking for the save.
    private const int EffectHeaderRepeats = 3;
    private static readonly TimeSpan EffectHeaderGap = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan EffectSettle = TimeSpan.FromMilliseconds(10);

    // L-Connect keeps streaming a saved effect, and keeps the device's speed back meanwhile, until
    // the device reports running it. The plugin gives up once the device has gone ten seconds and at
    // least three streams without taking it - whether a stream was refused or failed to send - so a
    // device whose firmware never reports the effect, or a dongle that drops a long stream, still
    // leaves the fans driven and stops costing a stream a second. Lighting never costs fan control.
    // A dongle that recovers by resetting, and a device heard again, are tried again. Time, not a
    // count of calls, because every control change wakes the worker and so brings streams forward.
    private static readonly TimeSpan GiveUpLightingAfter = TimeSpan.FromSeconds(10);
    private const int MinimumUntakenStreams = 3;

    // FlexLCDSdkHelper.SaveThemeSwitchAfterReadback waits four seconds (twenty 200 ms polls) for
    // the record's switch bits to show the switch, queues the sends once more if they do not, and
    // waits four seconds again before giving up: two rounds, with a readback of four list reads
    // after each at one read a second. The lighting handover, whose result the record reports the
    // same way, is confirmed with the same rounds and readback, where L-Connect only logs the flag
    // once its sends are done (docs/wireless.md lists the departure).
    private const int ConfirmationRounds = 2;
    private const int ConfirmationReadbackReads = 4;

    // RFController.UpdateSensorSettingByWiredLess writes a fan's table entry, broadcasts the clock
    // that carries it, sleeps 1200 ms once the broadcast has returned and only then queues the fan's
    // colours (UpdateSensorColors), so a screen has 1.2 s with its theme before its palette arrives.
    // Measured from the broadcast that completed carrying the entry the group needs now
    // (WirelessScreenEntryPublication), as a due time on the injected clock not a wait on
    // the worker, so the second's other work goes on meanwhile.
    private static readonly TimeSpan ColoursAfterScreenTable = TimeSpan.FromMilliseconds(1200);

    // MasterDevice.CheckChannelConflict spaces the masters in range four channels apart from 8.
    private const int MasterChannelSpacing = 4;

    // The Lancool 217 and the V150 have fans in slots 0-2 only: the front pair and the rear fan.
    private const int CaseFanSlots = 3;
    private const int CaseRearSlot = 2;

    private readonly int _index;
    private readonly WirelessDonglePair _dongles;
    private readonly IWirelessConfigurationSource _configuration;
    private readonly IClock _clock;
    private readonly IDelay _delay;
    private readonly ILog _log;
    private readonly WirelessDeviceTable _table;
    private readonly WirelessSaveSchedule _saves;
    private readonly FaultLog _faults;

    // The sensors, append-only so an index keeps naming the same control, fan or temperature. The
    // host reads them and sets duties; the worker appends and publishes readings; both under _lock.
    private readonly object _lock = new object();
    private readonly List<Control> _controls = new List<Control>();
    private readonly List<Reading> _readings = new List<Reading>();
    private readonly List<Temperature> _temperatures = new List<Temperature>();
    private readonly Dictionary<string, DeviceSensors> _sensorsByDevice = new Dictionary<string, DeviceSensors>(StringComparer.Ordinal);

    // The devices driven at the last list read: those whose sensors are driven, not retained.
    private readonly HashSet<string> _drivenMacs = new HashSet<string>(StringComparer.Ordinal);
    private int _drivenDevices;
    private int _litDevices;
    private string? _masterMacText;
    private int _channel;
    private readonly WirelessProcessState _processState;

    // Worker-thread only. The master is null until the transmitter reports one, and again while it
    // reports an all-zero address, as MasterDevice.QuerryMasterMac leaves it.
    private byte[]? _masterMac;
    private string? _learnedMasterText;

    // RFController.Init checks for a locked device list once, for the first master it queries.
    private bool _lockChecked;
    private long _masterClock;
    private int _pages = 1;
    private DateTime _lastCycleUtc = DateTime.MinValue;

    // The worker ticks at once whenever a control changes, but a list read is what every "30
    // reads" rule counts (lost, dropped, the master list), so reads stay at one a second whatever
    // FanControl does and those rules keep a fixed time: about thirty seconds, where L-Connect, which
    // reads about three times a second, takes about ten (docs/wireless.md).
    private DateTime _lastReadUtc = DateTime.MinValue;
    private bool _heardDevices;
    private int _disposed;
    private bool _listRead;
    private int _setUpTransmitterGeneration;
    private int _setUpReceiverGeneration;
    private readonly ReconnectReplay _reconnectReplay;

    // The transmitter's generation as last counted for the process (CountTransmitterLoss), so
    // that each loss of its handle moves the process's transmitter lifetime once.
    private int _countedTransmitterGeneration;

    /// <summary>
    /// Take ownership of both dongles of one master and discover what is bound to it. Never throws
    /// for a silent or failing dongle; <see cref="Dispose"/> releases both.
    /// </summary>
    public WirelessController(
        int index,
        IDeviceTransport transmitter,
        IDeviceTransport receiver,
        IWirelessConfigurationSource configuration,
        IClock clock,
        IDelay delay,
        ILog log,
        WirelessProcessState processState) {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _processState = processState ?? throw new ArgumentNullException(nameof(processState));
        _channel = processState.Last ?? WirelessProtocol.DefaultChannel;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _dongles = new WirelessDonglePair(transmitter, receiver, index, log);
        _index = index;
        _table = new WirelessDeviceTable(index, log);
        _saves = processState.SaveSchedule(clock.UtcNow);
        _faults = new FaultLog(index, log);
        _reconnectReplay = new ReconnectReplay(_clock);
        _countedTransmitterGeneration = _dongles.TransmitterGeneration;

        Discover();
        CountTransmitterLoss();
        _setUpTransmitterGeneration = _dongles.TransmitterGeneration;
        _setUpReceiverGeneration = _dongles.ReceiverGeneration;
    }

    /// <summary>
    /// Raised on the worker thread when a device is heard that needs sensors the host has not
    /// loaded yet (a newly bound device, or a group whose fan count grew), and when a device whose
    /// sensors this controller retained is driven again (a FLEX chain bound to the master once
    /// more, back from its USB receiver), since the plugin instance that has those sensors registered elsewhere
    /// needs to know. The plugin answers it by remembering the controller, which claims the ids it
    /// drives from any other controller remembered with them, and then, where the host's sensors no
    /// longer match, asking it to refresh (FanControl's <c>IPlugin3.RefreshRequested</c>).
    /// </summary>
    public event EventHandler? TopologyChanged;

    /// <summary>
    /// Whether the receiver has reported a device this pair drives: one that has is not waiting for its
    /// devices to check in, even when none of them has a sensor (only Strimers, say). A locked-list entry
    /// not yet heard, or a device of another master, does not count.
    /// </summary>
    public bool HasHeardDevices => Volatile.Read(ref _heardDevices);

    /// <summary>
    /// The sensors of the devices driven at the last list read. The rest are retained: a device
    /// unbound, dropped or unheard keeps its sensors so an index keeps naming the same thing, but
    /// they are not this controller's to claim from another that drives the same device (a FLEX
    /// chain on its USB receiver).
    /// </summary>
    public IEnumerable<string> DrivenSensorIds {
        get {
            lock (_lock) {
                var ids = new List<string>();
                foreach (Control control in _controls) {
                    if (_drivenMacs.Contains(control.Owner.MacText)) {
                        ids.Add(control.Id);
                    }
                }

                foreach (Reading reading in _readings) {
                    if (_drivenMacs.Contains(reading.Device)) {
                        ids.Add(reading.Id);
                    }
                }

                foreach (Temperature temperature in _temperatures) {
                    if (_drivenMacs.Contains(temperature.Device)) {
                        ids.Add(temperature.Id);
                    }
                }

                return ids;
            }
        }
    }

    /// <summary>The master's RF address as twelve hex digits, or null while the transmitter has not reported it.</summary>
    public string? MasterMacText {
        get {
            lock (_lock) {
                return _masterMacText;
            }
        }
    }

    /// <summary>The RF channel the master is driven on: the one L-Connect saved for it, or the default.</summary>
    public int Channel {
        get {
            lock (_lock) {
                return _channel;
            }
        }
    }

    /// <summary>How many devices are bound to this master and driven (fan groups, water blocks, case fans, the V150 and lighting-only products).</summary>
    public int DeviceCount {
        get {
            lock (_lock) {
                return _drivenDevices;
            }
        }
    }

    /// <summary>How many of the driven devices have a saved lighting effect to replay.</summary>
    public int LitDeviceCount {
        get {
            lock (_lock) {
                return _litDevices;
            }
        }
    }

    /// <summary>How many controls the devices have had so far; each is one FanControl control.</summary>
    public int ChannelCount {
        get {
            lock (_lock) {
                return _controls.Count;
            }
        }
    }

    /// <inheritdoc />
    public int FanSpeedCount {
        get {
            lock (_lock) {
                return _readings.Count;
            }
        }
    }

    /// <inheritdoc />
    public int TemperatureCount {
        get {
            lock (_lock) {
                return _temperatures.Count;
            }
        }
    }

    /// <summary>Every control belongs to a device that reported it, so all are populated.</summary>
    public bool IsChannelPopulated(int channel) => true;

    /// <summary>
    /// The sensor identity for a control. Every id is keyed on the device's RF address, so a device
    /// heard earlier or later never re-keys another's saved curve bindings. The RPM half names the
    /// control's first fan's reading, which the plugin registers through <see cref="IFanSpeedSource"/>.
    /// </summary>
    public ChannelDescriptor Describe(int channel) {
        lock (_lock) {
            Control control = _controls[channel];
            return new ChannelDescriptor(
                control.Id, control.Name, Reading.IdOf(control.Owner, control.RpmSlot), Reading.NameOf(control.Owner, control.RpmSlot));
        }
    }

    /// <inheritdoc />
    public FanSpeedDescriptor DescribeFanSpeed(int index) {
        lock (_lock) {
            Reading reading = _readings[index];
            return new FanSpeedDescriptor(reading.Id, reading.Name);
        }
    }

    /// <inheritdoc />
    public TemperatureDescriptor DescribeTemperature(int index) {
        lock (_lock) {
            Temperature temperature = _temperatures[index];
            return new TemperatureDescriptor(temperature.Id, temperature.Name);
        }
    }

    /// <inheritdoc />
    public void ReplayOnReconnect(Func<bool> replay) => _reconnectReplay.Register(replay);

    // ---------- FanControl-thread methods (no I/O) ----------

    /// <summary>
    /// Set the commanded duty for a control. The worker applies it on its next cycle. A fan group's
    /// duty is kept in the shared state as well, since its chain may be a FLEX chain that a USB
    /// receiver drives when this controller does not (<see cref="WirelessProcessState.ChainTarget"/>).
    /// </summary>
    public void SetTarget(int channel, int duty) {
        string? group;
        lock (_lock) {
            Control control = _controls[channel];
            control.Duty = duty;
            group = control.Kind == ControlKind.Group ? control.Owner.MacText : null;
        }

        if (group != null) {
            _processState.SetChainTarget(group, duty);
        }
    }

    /// <summary>Release a control: its slots are no longer resent, and the device keeps what it last had.</summary>
    public void ReleaseChannel(int channel) {
        string? group;
        lock (_lock) {
            Control control = _controls[channel];
            control.Duty = -1;
            group = control.Kind == ControlKind.Group ? control.Owner.MacText : null;
        }

        if (group != null) {
            _processState.ReleaseChainTarget(group);
        }
    }

    /// <summary>The control's first fan's last speed; 0 while the device is not heard.</summary>
    public float GetRpm(int channel) {
        lock (_lock) {
            Control control = _controls[channel];
            return control.Owner.Readings[control.RpmSlot]?.Value ?? 0f;
        }
    }

    /// <inheritdoc />
    public float GetFanSpeed(int index) {
        lock (_lock) {
            return _readings[index].Value;
        }
    }

    /// <inheritdoc />
    public float? GetTemperature(int index) {
        lock (_lock) {
            return _temperatures[index].Value;
        }
    }

    // ---------- worker-thread I/O (the only place the dongles are touched) ----------

    /// <summary>
    /// Run the one-second cycle when it is due, then stream any saved effect a device is not
    /// running and send the debounced save when it is due.
    /// </summary>
    public void ApplyPending() {
        CountTransmitterLoss();
        ReplaySetupIfReconnected();

        DateTime now = _clock.UtcNow;
        if (ClockSpan.Since(now, _lastCycleUtc) >= CycleInterval) {
            _lastCycleUtc = now;
            RunCycle();
        }

        byte[]? master = _masterMac;
        if (master is null) {
            return;
        }

        SyncEffects(master);
        if (_saves.TakeDebouncedSave(_clock.UtcNow)) {
            SaveConfiguration(master);
        }
    }

    /// <summary>
    /// Read the receiver's list, at most once a second, and apply it: adopt newly bound devices, publish every fan's
    /// speed and every water block's coolant temperature, and mark devices lost or heard again.
    /// Nothing is read while the master is unknown, as <c>RefreshList</c> reads nothing without one.
    /// A sensor added, or a device driven again that kept its sensors, raises <see cref="TopologyChanged"/>.
    /// </summary>
    public void PollRpm() {
        DateTime now = _clock.UtcNow;
        if (ClockSpan.Since(now, _lastReadUtc) < CycleInterval) {
            return;
        }

        _lastReadUtc = now;
        if (Refresh()) {
            TopologyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Release both dongles.</summary>
    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) {
            return;
        }

        // L-Connect's service asks every device to save when it stops (MainService's stop calls
        // RFController.SaveCfg). The plugin's close also runs on every FanControl refresh - every
        // wake, logon and unlock - so it saves only when a streamed look is still waiting for its
        // debounced save, instead of writing the devices' flash on every refresh.
        byte[]? master = _masterMac;
        if (master != null && _saves.HasPendingSave) {
            SaveConfiguration(master);
        }

        // A transmitter lost on the last tick, or in the closing save itself, is counted before the
        // dongles are let go, so the controller built in this one's place finds the looks owed.
        CountTransmitterLoss();
        _dongles.Dispose();
        _processState.ForgetBoundDevices(this);
    }

    // MainService.usbDeviceWatcher_Removed disposes L-Connect's LWirelessController when the
    // transmitter dongle leaves the USB bus, and usbDeviceWatcher_Inserted builds a new one when it
    // returns, whose ApplyAll sends every bound water block its screen switch and every bound LCD
    // FLEX group its screen colours again. The transmitter transport's generation, moved the moment
    // its handle is lost, is what the plugin sees of that: each time it moves, whichever controller
    // sees it first (on its tick, at the end of its discovery or on its closing save) drops every
    // look mark of the process, so a controller disposed before the reopen leaves the looks owed to
    // the next. The receiver is not counted: L-Connect's service has no controller for it, and its
    // RF layer reopens the receiver by itself, applying nothing again.
    private void CountTransmitterLoss() {
        int generation = _dongles.TransmitterGeneration;
        if (generation == _countedTransmitterGeneration) {
            return;
        }

        _countedTransmitterGeneration = generation;
        _processState.TransmitterLost();
        _log.Write(string.Format(
            CultureInfo.InvariantCulture,
            "W{0}: the transmitter was lost (generation {1}); every device will be sent its screen switch or screen colours again once it is back, as L-Connect's service sends them again when the transmitter is plugged in again",
            _index,
            generation));
    }

    // Where a look (the screen switch, the screen colours) stands against the process's transmitter
    // lifetime: the lifetime its pass works under, or null when there is nothing to do. The look's
    // rounds and its Done belong to the lifetime stamped on it and count only while that is still
    // the process's. Done under it, nothing is owed; marked for the process under it (markedUnder),
    // another controller sent it and this one takes it as done. Begun or done under an earlier
    // lifetime - the transmitter lost since, as counted by any controller of the process - it is
    // owed again: a round still being sent is the device's to end first (ServiceRounds), otherwise
    // the look is started over. The lifetime is read once and carried through everything the pass
    // decides and stamps (AreColoursDue, MayBeginLook), so a loss counted while the pass runs
    // leaves the round stamped with the lifetime its readiness was judged under, which the next
    // pass finds passed.
    private int? TakeUpLook(WirelessDevice device, WirelessDeviceCommand command, int? markedUnder, string look) {
        int lifetime = _processState.TransmitterLifetime;
        if (command.TransmitterLifetime == lifetime) {
            if (command.Done) {
                return null;
            }
        } else if (command.Done || command.Rounds > 0) {
            if (command.Sequence != null) {
                return null;
            }

            StartLookOver(device, command, look, lifetime);
        }

        if (markedUnder is int marked) {
            command.Done = true;
            command.TransmitterLifetime = marked;
            return null;
        }

        return lifetime;
    }

    // A look begun under a transmitter lifetime that has passed is started over, the end of its last
    // round discarded; a group's count of coloured screens starts over with it.
    private void StartLookOver(WirelessDevice device, WirelessDeviceCommand command, string look, int lifetime) {
        command.Reset();
        device.ColouredScreens = 0;
        _log.Write(string.Format(
            CultureInfo.InvariantCulture,
            "W{0}:{1} {2} begun before the transmitter was lost (now lifetime {3}); started over, to be sent again once the transmitter carries it",
            _index,
            device.MacText,
            look,
            lifetime));
    }

    // Whether a look is done with under the process's current transmitter lifetime, for a reader
    // that does not take the look up itself (the theme switch waiting behind the colours).
    private bool IsLookDone(WirelessDeviceCommand command)
        => command.Done && command.TransmitterLifetime == _processState.TransmitterLifetime;

    // A look begins only while the transmitter can carry it: under a lost transmitter nothing
    // reaches the air, ten refused sends would mark it sent, and L-Connect has no controller at all
    // while its transmitter is off the bus. The look takes the lifetime its pass works under as it
    // begins, and returns it with its mark, so a look begun before a loss marks nothing after
    // it. A round already begun is the device's to finish (ServiceRounds), loss or no loss.
    private bool MayBeginLook(WirelessDeviceCommand command, int lifetime) {
        if (command.Rounds > 0) {
            return true;
        }

        if (_dongles.IsTransmitterFaulted) {
            return false;
        }

        command.TransmitterLifetime = lifetime;
        return true;
    }

    // One list read applied to the table and the sensors. Whether a sensor was added or a device
    // that retained its sensors is driven again; the list read's own success is left in _listRead
    // for the startup wait. Construction calls this , not PollRpm, so what it finds is part of
    // the first load and raises no TopologyChanged.
    private bool Refresh() {
        byte[]? master = _masterMac;
        if (master is null) {
            // No master, no list to read (RefreshList); but the readings stop being live, and
            // nothing is driven over the radio for another controller to leave alone.
            _listRead = false;
            _table.MissRead();
            lock (_lock) {
                _drivenMacs.Clear();
                PublishReadings();
            }

            _processState.RecordBoundDevices(this, Array.Empty<string>());
            return false;
        }

        WirelessDeviceList? list = ReadList();
        _listRead = list != null;
        _table.Apply(list, master, _masterClock);
        // Only a device this pair drives and has heard ends the wait: an entry from L-Connect's locked
        // list stays unheard until it answers, and a device of another master never registers here.
        if (_table.Devices.Any(device => IsDriven(device, master))) {
            Volatile.Write(ref _heardDevices, true);
        }

        return UpdateSensors(master);
    }

    // Ask for the master until it answers, then read the list until two reads agree - both bounded
    // by count, not by the clock, so a clock that does not move cannot hold construction.
    private void Discover() {
        for (int attempt = 0; attempt < StartupMasterQueries && _masterMac is null; attempt++) {
            if (attempt > 0) {
                _delay.Wait(StartupInterval);
            }

            QueryMaster();
        }

        if (_masterMac is null) {
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "W{0}: the transmitter has not reported its master yet; asking again every second",
                _index));
            return;
        }

        DateTime lastQueryUtc = _clock.UtcNow;
        string? previous = null;
        for (int read = 0; read < StartupListReads; read++) {
            if (read > 0) {
                _delay.Wait(StartupInterval);
            }

            if (ClockSpan.Since(_clock.UtcNow, lastQueryUtc) >= CycleInterval) {
                lastQueryUtc = _clock.UtcNow;
                QueryMaster();
            }

            _ = Refresh();
            if (!_listRead) {
                continue;
            }

            string shape = Shape();
            if (shape == previous) {
                break;
            }

            previous = shape;
        }
    }

    // What a startup read is compared on: the table, the sensors, and the page count it needed.
    private string Shape() {
        lock (_lock) {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}/{1}/{2}/{3}/{4}",
                _table.Devices.Count,
                _controls.Count,
                _readings.Count,
                _temperatures.Count,
                _pages);
        }
    }

    // MasterDevice.Run's one-second block, in its order: SyncPwm, CheckSaveConfig, QuerryMasterMac,
    // SendAioInfo, SyncMasterClock. The commands SyncControlInfo sends under a sequence are begun
    // beside the work they belong to (the lighting source with the speeds, the screens after the
    // table that goes out in the clock), and every round already under way is serviced first, as
    // SyncControlInfo services every pending command in one place.
    private void RunCycle() {
        byte[]? master = _masterMac;
        if (master != null) {
            ServiceRounds(master);
            SyncSpeeds(master);
            SyncLightingSources(master);
            if (_saves.TakePeriodicSave(_clock.UtcNow)) {
                SaveConfiguration(master);
            }
        }

        QueryMaster();
        master = _masterMac;
        if (master is null) {
            return;
        }

        SendAioInfo(master);

        // Two readings of the clock, as L-Connect takes them: the date and time the broadcast
        // carries are DateTime.Now before SyncMasterClock sends, and the colours' interval runs from
        // the moment the broadcast has completed (UpdateSensorSettingByWiredLess sleeps after
        // SyncMasterClock returns), so the four packets' writes are no part of the 1.2 s. The
        // transmitter lifetime and handle the entries go out under are read before the send: a
        // handle lost during it fails the broadcast, and nothing is published.
        DateTime sentUtc = _clock.UtcNow;
        var carried = new List<(WirelessDevice Device, byte[] Entry)>();
        WirelessScreenTable screens = ScreenTable(master, carried);
        int transmitterLifetime = _processState.TransmitterLifetime;
        int transmitterGeneration = _dongles.TransmitterGeneration;
        if (Isolated("clock", () => Broadcast(WirelessProtocol.EncodeClockPayload(master, sentUtc.ToLocalTime(), screens)))) {
            PublishScreenEntries(carried, transmitterLifetime, transmitterGeneration, _clock.UtcNow);
        }

        RestoreScreenColours(master, carried);
        RestoreThemeSwitches(master, carried);
        ReleaseStaleLock(master);
        CheckChannelConflict();
    }

    // RFController.UpdateSensorDataByWiredLess: an entry for every bound LCD FLEX group on a receiver
    // slot the table has (1-13), one per fan, from what the user saved for it or L-Connect's entry
    // for a group with nothing saved. Two groups reporting one receiver slot write over each
    // other's bytes fan by fan, the later in table order last, as L-Connect's loop over its RFList
    // does; so a group's entry counts as carried, and goes on the carried list with its bytes, only
    // when the slot's bytes that go out are the group's own, since otherwise its screens have not
    // been told the theme its colours are for.
    private WirelessScreenTable ScreenTable(byte[] master, List<(WirelessDevice Device, byte[] Entry)> carried) {
        var screens = new WirelessScreenTable();
        var entries = new List<(WirelessDevice Device, byte[] Entry)>();
        foreach (WirelessDevice device in _table.Devices) {
            if (!HasTableEntry(device, master)) {
                continue;
            }

            WirelessFanScreenPresentation presentation = _configuration.FindFanScreenPresentation(device.MacText) ?? WirelessFanScreenPresentation.Default;
            var own = new WirelessScreenTable();
            WriteScreens(own, device.Record, presentation);
            WriteScreens(screens, device.Record, presentation);
            entries.Add((device, own.EntryOf(device.Record.ReceiverType)));
        }

        foreach ((WirelessDevice device, byte[] entry) in entries) {
            if (SameBytes(entry, screens.EntryOf(device.Record.ReceiverType))) {
                carried.Add((device, entry));
            }
        }

        return screens;
    }

    // Filled the same way into the broadcast table and the group's own, whose entry is what the
    // broadcast is checked against.
    private static void WriteScreens(WirelessScreenTable table, WirelessDeviceRecord record, WirelessFanScreenPresentation presentation) {
        for (int fan = 0; fan < record.FanCount; fan++) {
            table.SetFan(record.ReceiverType, TableFanOf(record, fan), presentation.ThemeOf(fan), presentation.Direction, presentation.DataSourceOf(fan), presentation.Brightness);
        }
    }

    // The table's fan index for the service's fan: numbered in reverse on every group but a
    // left-attached SL-Infinity or SL-INF FLEX one (UpdateSensorSettingByWiredLess).
    private static int TableFanOf(WirelessDeviceRecord record, int fan)
        => WirelessProtocol.ScreensNumberedInReverse(record) ? record.FanCount - 1 - fan : fan;

    // The clock broadcast completed at completedUtc: every group whose entry it carried has that
    // entry published from then (the same entry published the same way keeps its earlier time), and
    // every other device's publication is withdrawn, so an entry that goes and comes back waits its
    // 1200 ms from the broadcast that restores it, as RFController's wait is from the broadcast that
    // carries the entry. The process's marks stand: a group coloured already is not coloured again.
    private void PublishScreenEntries(List<(WirelessDevice Device, byte[] Entry)> carried, int transmitterLifetime, int transmitterGeneration, DateTime completedUtc) {
        var published = new HashSet<WirelessDevice>();
        foreach ((WirelessDevice device, byte[] entry) in carried) {
            device.PublishScreenEntry(device.Record.ReceiverType, entry, transmitterLifetime, transmitterGeneration, completedUtc);
            _ = published.Add(device);
        }

        foreach (WirelessDevice device in _table.Devices) {
            if (!published.Contains(device)) {
                device.WithdrawScreenEntry();
            }
        }
    }

    // Whether the group is one the table carries an entry for: driven, with screens, on a receiver
    // slot the table has.
    private static bool HasTableEntry(WirelessDevice device, byte[] master) {
        WirelessDeviceRecord record = device.Record;
        return IsDriven(device, master) && WirelessProtocol.HasScreens(record) && WirelessScreenTable.HasSlot(record.ReceiverType);
    }

    // MasterDevice.CheckChannelConflict: every master in radio range takes a channel by its place
    // among them in address order, 8 for the first, then 12, 16 and so on, so two L-Wireless
    // controllers near each other do not share one. When this master reports an even channel other
    // than its own place's (the even channels are the ones L-Connect assigns; one the user chose in
    // L-Connect is odd and never moved), it moves there for this run, and its devices follow through
    // the ordinary re-homing (MasterDevice.SwitchChannel). The move is not saved, as L-Connect does
    // not save it, and it is made once, not again on every read until it shows. Run skips
    // the check while the list is locked.
    private void CheckChannelConflict() {
        if (_table.IsLocked) {
            return;
        }

        var masters = new List<WirelessMaster>(_table.Masters);
        masters.Sort((a, b) => string.CompareOrdinal(a.MacText, b.MacText));
        for (int place = 0; place < masters.Count; place++) {
            WirelessMaster master = masters[place];
            int channel = WirelessProtocol.DefaultChannel + (place * MasterChannelSpacing);
            if (master.MacText == _learnedMasterText
                && master.Channel % 2 == 0
                && master.Channel != channel
                && master.ChannelShouldBe != channel) {
                master.ChannelShouldBe = channel;
                lock (_lock) {
                    _channel = channel;
                }

                _processState.Remember(master.MacText, channel);

                _log.Write(string.Format(
                    CultureInfo.InvariantCulture,
                    "W{0}: {1} master(s) in range; this one is moved from channel {2} to {3} for this run, as L-Connect moves it",
                    _index,
                    masters.Count,
                    master.Channel,
                    channel));
                return;
            }
        }
    }

    // MasterDevice.QuerryMasterMac: the query goes out on the master channel, which is what keeps
    // the transmitter on it; the reply's clock is always taken, its address only once the clock runs.
    private void QueryMaster() {
        WirelessMasterReply? reply;
        try {
            _dongles.WriteTransmitter(WirelessProtocol.EncodeMasterQuery(Channel));
            reply = WirelessProtocol.DecodeMasterQuery(_dongles.ReadTransmitter(WirelessProtocol.PacketLength));
        }
#pragma warning disable CA1031 // resilience: a failed query is logged and retried next second, as L-Connect retries it
        catch (Exception ex) {
            _faults.Failed("master", ex.Message);
            return;
        }
#pragma warning restore CA1031

        if (reply is null) {
            _faults.Failed("master", "the transmitter did not answer the master query");
            return;
        }

        _masterClock = reply.ClockMilliseconds;
        if (!reply.IsClockRunning) {
            _faults.Failed("master", "the transmitter has not started its clock");
            return;
        }

        if (!reply.HasAddress) {
            _masterMac = null;
            _faults.Failed("master", "the transmitter reports no address");
            return;
        }

        _faults.Recovered("master");
        _masterMac = reply.Mac;
        string text = WirelessDeviceRecord.FormatMac(reply.Mac);
        if (text != _learnedMasterText) {
            LearnMaster(text);
        }
    }

    // RFController.Init reads the channel file for the master it just queried (GetChannel); a
    // master without one stays on the default channel.
    private void LearnMaster(string masterMacText) {
        _learnedMasterText = masterMacText;
        // L-Connect's saved channel wins; otherwise the one this process last drove the master on
        // (moved there by the conflict check, say), which the controller before this refresh kept.
        int? saved = _configuration.FindChannel(masterMacText);
        int? earlier = _processState.RecallFor(masterMacText);
        lock (_lock) {
            _masterMacText = masterMacText;
            _channel = saved ?? earlier ?? WirelessProtocol.DefaultChannel;
        }

        _processState.Remember(masterMacText, Channel);
        _log.Write(string.Format(
            CultureInfo.InvariantCulture,
            "W{0}: master {1}, RF channel {2} ({3})",
            _index,
            masterMacText,
            Channel,
            saved.HasValue ? "saved by L-Connect" : earlier.HasValue ? "kept from before FanControl's refresh" : "default"));

        if (_lockChecked) {
            return;
        }

        _lockChecked = true;
        IReadOnlyList<WirelessLockedDevice>? locked = _configuration.FindLockedDevices(masterMacText);
        if (locked != null) {
            _table.Lock(locked);
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "W{0}: L-Connect's device list is locked; its {1} device(s) are used in its order, and a device paired since is left alone until it is unlocked",
                _index,
                locked.Count));
        }
    }

    // MasterDevice.Run's one-second block, after the clock: a locked list none of whose devices names
    // this master any more is let go (RFController.LockDevice(false)). The plugin leaves L-Connect's
    // file alone and only stops keeping the lock.
    private void ReleaseStaleLock(byte[] master) {
        if (!_table.IsLocked || _table.Devices.Count == 0) {
            return;
        }

        foreach (WirelessDevice device in _table.Devices) {
            if (device.Record.IsBoundTo(master)) {
                return;
            }
        }

        _table.Unlock();
        _log.Write(string.Format(
            CultureInfo.InvariantCulture, "W{0}: no device on L-Connect's locked list names this master any more; the list is no longer kept", _index));
    }

    // One list request (MasterDevice.GetDev and RefreshList): the page count asked for is the one
    // the last reply needed. Null when the read failed or the reply was not a list.
    private WirelessDeviceList? ReadList() {
        WirelessDeviceList? list;
        try {
            _dongles.WriteReceiver(WirelessProtocol.EncodeDeviceListRequest(_pages));
            list = WirelessProtocol.DecodeDeviceList(_dongles.ReadReceiver(WirelessProtocol.DeviceListReplyLength(_pages)), _pages);
        }
#pragma warning disable CA1031 // resilience: a failed read counts as a read in which nothing was heard, as RefreshList counts it
        catch (Exception ex) {
            _faults.Failed("list", ex.Message);
            return null;
        }
#pragma warning restore CA1031

        if (list is null) {
            _faults.Failed("list", "the receiver did not answer the list request");
            return null;
        }

        _faults.Recovered("list");
        if (list.Total > 0) {
            _pages = WirelessProtocol.PagesFor(list.Total);
        }

        return list;
    }

    // MasterDevice.SyncPwm: number the bound devices that are neither mid-effect nor unbound, in
    // table order, and send a driven one its targets whenever NeedSyncPwm says so. L-Connect also
    // numbers a twelfth device it is unbinding; the plugin refuses that one instead and leaves it out
    // until there is room, and takes it at its own place in the table as soon as there is (the
    // bound count falls when a bound device goes unheard long enough to be dropped), so the indices
    // after it match again.
    private void SyncSpeeds(byte[] master) {
        int bindIndex = 0;
        foreach (WirelessDevice device in _table.Devices) {
            if (!device.IsBound || device.ChangingEffect || device.Record.IsUnbound) {
                continue;
            }

            int index = ++bindIndex;
            Isolated(device.MacText + "/speed", () => SyncSpeed(device, master, index));
            _delay.Wait(SpeedPacing);
        }
    }

    private void SyncSpeed(WirelessDevice device, byte[] master, int bindIndex) {
        if (!IsDriven(device, master)) {
            return;
        }

        // A drifted Lancool 217 or V150 keeps the targets it had (the RFList getter), but a control
        // released meanwhile is still let go of.
        UpdateTargets(device, !WirelessDeviceTable.IsClockDrifted(device));

        if (WirelessProtocol.IsClGroup(device.Record)) {
            for (int slot = 0; slot < WirelessProtocol.SlotsPerGroup; slot++) {
                device.TargetPwm[slot] = WirelessProtocol.StepClReserved(device.TargetPwm[slot]);
            }
        }

        // MasterDevice.SyncControlInfo sends the same payload to any bound device heard on another
        // channel or receiver slot than the one it should be on, whatever its speed: that is what
        // moves it back onto the master's channel, where the clock broadcast reaches it.
        bool misplaced = device.Record.Channel != Channel || device.Record.ReceiverType != device.TargetReceiverType;
        if (!misplaced
            && !WirelessSpeedSyncDecision.NeedsSync(device.Kind, device.Record.FanCount, device.Record.Pwm, device.TargetPwm, device.DrivenSlots)) {
            device.ResendLogged = false;
            return;
        }

        Transmit(device, WirelessProtocol.EncodeSpeedPayload(
            device.Mac, master, device.TargetReceiverType, Channel, bindIndex, device.TargetPwm));
        if (device.LastSentTarget != null && SameBytes(device.LastSentTarget, device.TargetPwm)) {
            if (!device.ResendLogged) {
                device.ResendLogged = true;
                _log.Write(string.Format(
                    CultureInfo.InvariantCulture,
                    "W{0}:{1} reports pwm {2} against {3}; resending each second until it matches",
                    _index,
                    device.MacText,
                    Bytes(device.Record.Pwm),
                    Bytes(device.TargetPwm)));
            }

            return;
        }

        device.LastSentTarget = (byte[])device.TargetPwm.Clone();
    }

    // The service's writers, per control: a group takes one duty on all four slots
    // (LWirelessDevice.SetFanSpeed), the Lancool 217 and the V150 their front pair on slots 0-1 and
    // rear fan on slot 2 with slot 3 zero (SetCaseSpeed). A slot no set control drives carries
    // whatever the device reports for it, and is left out of the comparison: the packet goes out
    // whole, so a speed sent for the front pair must not restore a rear fan FanControl has let go
    // of. With newValues false (a drifted clock) the driven slots keep the targets they had.
    private void UpdateTargets(WirelessDevice device, bool newValues) {
        var driven = new bool[WirelessProtocol.SlotsPerGroup];
        var messages = new List<string>();
        lock (_lock) {
            // Only a driven device gets here, and a device is driven only once a list read has
            // carried it (IsDriven), and that read gave it its sensors.
            foreach (Control control in _sensorsByDevice[device.MacText].Controls) {
                if (control.Kind == ControlKind.Pump) {
                    continue;
                }

                // A group's duty may have reached the host's control on the chain's USB receiver
                // instead, which keeps it in the shared state; the chain is driven here now, so it
                // is driven to that.
                int duty = control.Duty;
                if (duty < 0 && control.Kind == ControlKind.Group) {
                    duty = _processState.ChainTarget(device.MacText);
                }

                LogDutyChange(device, control, duty, messages);
                if (duty < 0) {
                    continue;
                }

                byte pwm = control.Kind == ControlKind.Group
                    ? WirelessProtocol.FanPwm(duty, WirelessProtocol.GroupDutyFloor(device.Record), WirelessProtocol.GroupIdleDuty(device.Record))
                    : WirelessProtocol.CasePwm(duty);
                foreach (int slot in control.Slots) {
                    if (newValues || !device.DrivenSlots[slot]) {
                        device.TargetPwm[slot] = pwm;
                    }

                    driven[slot] = true;
                }

                if (control.Kind != ControlKind.Group) {
                    device.TargetPwm[WirelessProtocol.SlotsPerGroup - 1] = 0;
                    driven[WirelessProtocol.SlotsPerGroup - 1] = true;
                }
            }
        }

        for (int slot = 0; slot < WirelessProtocol.SlotsPerGroup; slot++) {
            if (!driven[slot]) {
                device.TargetPwm[slot] = device.Record.Pwm[slot];
            }
        }

        Array.Copy(driven, device.DrivenSlots, WirelessProtocol.SlotsPerGroup);
        WriteAll(messages);
    }

    // MasterDevice.SendAioInfo: every bound water block, every second. The plugin sends only to one
    // it drives whose pump FanControl has set, so a pump no control has set is left as it was.
    private void SendAioInfo(byte[] master) {
        foreach (WirelessDevice device in _table.Devices) {
            if (!device.IsBound || device.Kind != WirelessDeviceKind.WaterBlock || !IsDriven(device, master)) {
                continue;
            }

            Isolated(device.MacText + "/pump", () => SendAio(device, master));
        }
    }

    private void SendAio(WirelessDevice device, byte[] master) {
        int duty;
        var messages = new List<string>();
        lock (_lock) {
            // A water block gets its pump control with its first sensors, from the list read that
            // first carried it, and only a device a read has carried is driven (IsDriven).
            Control pump = _sensorsByDevice[device.MacText].Pump!;
            duty = pump.Duty;
            LogDutyChange(device, pump, duty, messages);
        }

        WriteAll(messages);

        // The screen switch begins only for a pump FanControl drives, as the parameter block goes
        // only to one; but a round under way is the device's to finish whatever the duty is now, as
        // SyncControlInfo services a pending command whether or not SendAioInfo sends the pump
        // anything, and its end is still taken here.
        WirelessAioPresentation presentation = _configuration.FindPumpPresentation(device.MacText) ?? WirelessAioPresentation.Default;
        if (!presentation.AdvanceMode && (duty >= 0 || device.ScreenMode.IsUnderWay)) {
            SwitchScreenMode(device, master);
        }

        if (duty < 0) {
            return;
        }

        int rpm = WirelessProtocol.PumpRpmFromDuty(duty, device.Record.DeviceType);
        byte[] parameters = WirelessProtocol.EncodeAioParameters(presentation, WirelessProtocol.PumpTimerFromRpm(rpm, device.Record.DeviceType));
        Transmit(device, WirelessProtocol.EncodeAioPayload(device.Mac, master, device.TargetReceiverType, Channel, parameters));
    }

    // LWirelessController.applyWirelessMode, at startup and whenever a water block binds: a screen
    // saved out of advance mode is switched to its wireless theme ahead of its parameters, so a
    // screen left on PC-streamed content (a switch cut short by sleep, L-Connect stopped while it
    // streamed) shows its theme again. The switch goes out under the device's next sequence, once a
    // second, until its record reports that sequence or it has gone out as often as L-Connect sends
    // it; the record reports nothing about which content the screen shows, so the acknowledgement
    // is all there is to confirm it by. L-Connect switches a block when its service starts, on a
    // bind or a setting change, and when the transmitter is plugged in again, never on a dropped
    // block's return or a resume, relying on the block keeping its theme; so the switch goes to a
    // block once per transmitter lifetime of the process (WirelessProcessState), marked when its
    // sends end.
    private void SwitchScreenMode(WirelessDevice device, byte[] master) {
        WirelessDeviceCommand command = device.ScreenMode;
        if (TakeUpLook(device, command, _processState.ScreenSwitchedUnder(device.MacText), "screen switch") is not int lifetime || !MayBeginLook(command, lifetime)) {
            return;
        }

        CommandOutcome outcome = ServiceCommand(device, command, sequence => WirelessProtocol.EncodeWirelessThemePayload(
            device.Mac, master, device.TargetReceiverType, Channel, CountedBefore(device), sequence));
        if (outcome == CommandOutcome.Sent || outcome == CommandOutcome.Waiting) {
            return;
        }

        command.Done = true;
        _processState.MarkScreenSwitched(device.MacText, command.TransmitterLifetime);
        _log.Write(string.Format(
            CultureInfo.InvariantCulture,
            outcome == CommandOutcome.Acknowledged
                ? "W{0}:{1} switched its screen to its wireless theme"
                : "W{0}:{1} did not acknowledge the switch to its wireless theme in {2} sends",
            _index,
            device.MacText,
            command.Sends));
    }

    // LWirelessController.ResumeSuspend: every bound device whose MotherboardARGBSync setting is
    // saved on is sent SetMotherboardARGBSync(mac, true) again, which RFController.SyncMBLightSwitch
    // queues as ten sends of 0x27 under a fresh command sequence. The plugin is built anew on every
    // FanControl refresh - every wake - so it sends it at every build and again whenever the device
    // or its dongle comes back, and never sends the switch off.
    private void SyncLightingSources(byte[] master) {
        foreach (WirelessDevice device in _table.Devices) {
            if (!IsDriven(device, master) || !device.FollowsMotherboard) {
                continue;
            }

            Isolated(device.MacText + "/lighting-source", () => SyncLightingSource(device, master));
        }
    }

    // The handover is confirmed by the record's source flag (IsSyncMbLight), not by the
    // acknowledgement alone: the first round goes out whatever the flag says, as ResumeSuspend sends
    // it regardless, and a flag still clear after the round's readback gets one more round before
    // the device is left as it is. Its fans are driven as usual throughout.
    private void SyncLightingSource(WirelessDevice device, byte[] master) {
        WirelessDeviceCommand command = device.LightingSync;
        if (command.Done) {
            return;
        }

        switch (StepOf(command, command.Rounds > 0 && device.Record.LightingFollowsMotherboard)) {
            case RoundStep.Confirmed:
                command.Done = true;
                _log.Write(string.Format(
                    CultureInfo.InvariantCulture,
                    "W{0}:{1} handed its lighting to the motherboard's ARGB header, as L-Connect's sync switch for it says; it reports following the header",
                    _index,
                    device.MacText));
                return;
            case RoundStep.Readback:
                return;
            case RoundStep.Abandoned:
                command.Done = true;
                _log.Write(string.Format(
                    CultureInfo.InvariantCulture,
                    "W{0}:{1} still reports its lighting not following the motherboard's ARGB header after {2} rounds of the handover; left as it is",
                    _index,
                    device.MacText,
                    command.Rounds));
                return;
        }

        CommandOutcome outcome = ServiceCommand(device, command, sequence => WirelessProtocol.EncodeLightingSyncPayload(
            device.Mac, master, device.TargetReceiverType, Channel, CountedBefore(device), sequence, followMotherboard: true));
        if (outcome == CommandOutcome.Sent || outcome == CommandOutcome.Waiting) {
            return;
        }

        // The round ended: the flag is read back from the next list reads.
        command.ReadbackReads = 0;
    }

    // LWirelessController.applyWirelessLCDMode, at startup for every bound LCD FLEX group saved out
    // of advance mode and on every settings change: for each of the group's fans in turn, after its
    // table entry and the clock that carries it, RFController.UpdateSensorSettingByWiredLess sends
    // the colours of the fan's theme (UpdateSensorColors: ten sends of 0x28 under a fresh sequence).
    // The plugin does the same for every driven group with a saved, non-advance configuration whose
    // own entry the clock carried this cycle, one screen per round, before the theme switch as in
    // L-Connect, and once per transmitter lifetime of the process, as with the water block's screen
    // switch: the record reports nothing about the colours a screen holds, and L-Connect sends them
    // at its start and when the transmitter is plugged in again, never on a dropped group's return
    // or a resume. L-Connect colours both groups of a shared receiver slot over whichever entry its
    // table ended up holding; the plugin colours a group only once its own entry goes out, and holds
    // nothing meanwhile.
    private void RestoreScreenColours(byte[] master, List<(WirelessDevice Device, byte[] Entry)> carried) {
        foreach ((WirelessDevice device, byte[] entry) in carried) {
            // A group that reports no fan yet has no screen to colour (getWirelessLCDFanCount is
            // 0, and applyWirelessLCDMode returns); it is coloured once it reports one.
            if (device.Record.FanCount == 0) {
                continue;
            }

            WirelessFanScreenPresentation? presentation = _configuration.FindFanScreenPresentation(device.MacText);
            if (presentation is null || presentation.AdvanceMode) {
                continue;
            }

            Isolated(device.MacText + "/screen-colours", () => RestoreScreenColour(device, entry, master, presentation));
        }
    }

    // Whether the group's colours are owed this cycle: the broadcast carried its own entry and it
    // reports a screen to colour. Only that holds the theme switch back: a group off the table,
    // reporting no fan or with its entry written over by another group's is coloured in no cycle
    // while it stays so, and holds nothing.
    private static bool AreColoursOwed(WirelessDevice device, List<(WirelessDevice Device, byte[] Entry)> carried) {
        if (device.Record.FanCount == 0) {
            return false;
        }

        foreach ((WirelessDevice carriedDevice, _) in carried) {
            if (ReferenceEquals(carriedDevice, device)) {
                return true;
            }
        }

        return false;
    }

    // Whether a screen's colours may begin: the clock broadcast carrying the entry the group needs
    // now, under the transmitter lifetime the pass works under and on the transmitter handle as it
    // is now, has completed, and 1.2 s have passed since. An entry the group no longer needs, or
    // one that went out under a lifetime or handle since passed, is no publication: the next
    // broadcast that completes with the current entry publishes again, and the wait runs from that.
    private bool AreColoursDue(WirelessDevice device, byte[] entry, int lifetime) {
        WirelessScreenEntryPublication? published = device.ScreenEntryPublication;
        return published != null
            && published.Carries(device.Record.ReceiverType, entry, lifetime, _dongles.TransmitterGeneration)
            && ClockSpan.Since(_clock.UtcNow, published.CompletedUtc) >= ColoursAfterScreenTable;
    }

    // One round per screen, in the service's fan order, the slot in the buffer being the table's.
    // Each screen's round begins only once its colours are due and the transmitter can carry it; a
    // round ends on the acknowledgement or the tenth send (the record reports nothing about the
    // colours), moving straight on to the next screen's in the same pass, so the device's sequence
    // is held from screen to screen and the theme switch cannot take it between two. A round keeps
    // the slot and colours it started with, as L-Connect's sendColors buffer is filled once per fan,
    // and is the device's to finish (ServiceRounds): a group that reports fewer fans meanwhile is
    // done once every screen it still reports has had its round, while the round in flight for a
    // screen it no longer reports is sent to its end and then leaves the sequence free.
    private void RestoreScreenColour(WirelessDevice device, byte[] entry, byte[] master, WirelessFanScreenPresentation presentation) {
        WirelessDeviceCommand command = device.ScreenColours;
        if (TakeUpLook(device, command, _processState.ScreensColouredUnder(device.MacText), "screen colours") is not int lifetime) {
            return;
        }

        WirelessDeviceRecord record = device.Record;
        while (device.ColouredScreens < record.FanCount) {
            if (!command.IsUnderWay && (!AreColoursDue(device, entry, lifetime) || !MayBeginLook(command, lifetime))) {
                return;
            }

            int fan = device.ColouredScreens;
            int tableFan = TableFanOf(record, fan);
            byte[] colours = presentation.ColoursOf(fan).ToBytes();
            CommandOutcome outcome = ServiceCommand(device, command, sequence => WirelessProtocol.EncodeScreenColoursPayload(
                device.Mac, master, device.TargetReceiverType, Channel, CountedBefore(device), sequence, tableFan, colours));
            if (outcome == CommandOutcome.Sent || outcome == CommandOutcome.Waiting) {
                return;
            }

            if (outcome == CommandOutcome.Exhausted) {
                _log.Write(string.Format(
                    CultureInfo.InvariantCulture,
                    "W{0}:{1} did not acknowledge the colours of its screen {2}'s wireless theme in {3} sends; the next screen's follow",
                    _index,
                    device.MacText,
                    fan + 1,
                    command.Sends));
            }

            device.ColouredScreens++;
        }

        command.Done = true;
        _processState.MarkScreensColoured(device.MacText, command.TransmitterLifetime);
        _log.Write(string.Format(
            CultureInfo.InvariantCulture,
            "W{0}:{1} sent its {2} screen(s) the colours of their wireless themes",
            _index,
            device.MacText,
            record.FanCount));
    }

    // LWirelessController.applyWirelessLCDMode, at startup and on every settings change for every
    // bound LCD FLEX group saved out of advance mode: after the table entries and every screen's
    // colours, FlexLCDSdkHelper.TryRestoreWirelessThemeSwitches switches every screen whose bit in
    // the record is clear onto its wireless theme (a screen left on PC-streamed content by a mode
    // change cut short, or a reset). The plugin does the same for every driven group with a saved,
    // non-advance configuration; a group whose colours are owed this cycle and not yet done is not
    // switched yet, as the switch follows the colours in L-Connect. Nothing holds it indefinitely: once
    // their interval has passed the colours are done within ten sends a screen, and a group whose
    // colours are owed in no cycle is not held behind them at all.
    private void RestoreThemeSwitches(byte[] master, List<(WirelessDevice Device, byte[] Entry)> carried) {
        foreach (WirelessDevice device in _table.Devices) {
            if (!IsDriven(device, master) || !WirelessProtocol.HasScreens(device.Record)) {
                continue;
            }

            WirelessFanScreenPresentation? presentation = _configuration.FindFanScreenPresentation(device.MacText);
            if (presentation is null || presentation.AdvanceMode) {
                continue;
            }

            if (AreColoursOwed(device, carried) && !IsLookDone(device.ScreenColours)) {
                continue;
            }

            Isolated(device.MacText + "/screens", () => RestoreThemeSwitch(device, master));
        }
    }

    // One round is RFController.PlayWiredlessThemeSwitch's ten sends of 0x29 with the target mask
    // (the bits reported and one per screen), under a fresh sequence. After a round the record's
    // bits are read back for four reads; a mask still short of the target gets one more round
    // (SaveThemeSwitchAfterReadback), one that reaches it is saved to the devices' flash
    // (RFController.SaveCfg), and a group whose bits were all set to start with needs nothing. A
    // group that reports no fan has no screen whose bit says anything (TryRestoreWirelessThemeSwitches
    // returns for a FanNum of 0), so its switch is neither sent nor done with, and is looked at
    // again once it reports a fan; a round under way is the device's to finish meanwhile.
    private void RestoreThemeSwitch(WirelessDevice device, byte[] master) {
        WirelessDeviceCommand command = device.ThemeSwitch;
        if (command.Done || (device.Record.FanCount == 0 && !command.IsUnderWay)) {
            return;
        }

        byte reported = device.Record.ThemeSwitches;
        byte target = WirelessProtocol.ThemeSwitchesForAllScreens(reported, device.Record.FanCount);
        switch (StepOf(command, target == reported)) {
            case RoundStep.Confirmed:
                command.Done = true;
                if (command.Rounds > 0) {
                    _log.Write(string.Format(
                        CultureInfo.InvariantCulture,
                        "W{0}:{1} screens all show their wireless themes again (switches 0x{2:X}); asked every device to save",
                        _index,
                        device.MacText,
                        reported));
                    SaveConfiguration(master);
                }

                return;
            case RoundStep.Readback:
                return;
            case RoundStep.Abandoned:
                command.Done = true;
                _log.Write(string.Format(
                    CultureInfo.InvariantCulture,
                    "W{0}:{1} screens still report switches 0x{2:X} against 0x{3:X} after {4} rounds of the switch; left as they are",
                    _index,
                    device.MacText,
                    reported,
                    target,
                    command.Rounds));
                return;
        }

        CommandOutcome outcome = ServiceCommand(device, command, sequence => WirelessProtocol.EncodeThemeSwitchPayload(
            device.Mac, master, device.TargetReceiverType, Channel, CountedBefore(device), sequence, target));
        if (outcome == CommandOutcome.Sent) {
            if (command.Rounds == 1 && command.Sends == 1) {
                _log.Write(string.Format(
                    CultureInfo.InvariantCulture,
                    "W{0}:{1} switching its screens onto their wireless themes (switches 0x{2:X} against 0x{3:X})",
                    _index,
                    device.MacText,
                    reported,
                    target));
            }

            return;
        }

        if (outcome != CommandOutcome.Waiting) {
            // The round ended: the bits are read back from the next list reads.
            command.ReadbackReads = 0;
        }
    }

    // Where a command the record can confirm stands before this pass sends anything: a round under
    // way is serviced; its result showing, it is confirmed; a round just ended, the record is read
    // back for ConfirmationReadbackReads list reads; the result still missing after its last
    // round, it is abandoned; otherwise a round is due.
    private static RoundStep StepOf(WirelessDeviceCommand command, bool confirmed) {
        if (command.IsUnderWay) {
            return RoundStep.Send;
        }

        if (confirmed) {
            return RoundStep.Confirmed;
        }

        if (command.Rounds > 0 && command.ReadbackReads < ConfirmationReadbackReads) {
            command.ReadbackReads++;
            return RoundStep.Readback;
        }

        return command.Rounds >= ConfirmationRounds ? RoundStep.Abandoned : RoundStep.Send;
    }

    // MasterDevice.SyncControlInfo's pass over every device, made before anything else the second
    // does for them: a round under way gets its next send, or ends because the device reported its
    // sequence or its ten sends are out, which frees the device's sequence and leaves the end on
    // the command for the pass that services it (ServiceCommand). The device decides that from its
    // own record and count (WirelessDevice.NextSend), so a round is carried to its end however the
    // conditions that began it have changed meanwhile, and the device's one sequence is never held
    // indefinitely by a round nothing finishes. Nothing is sent to a device that is not driven; its
    // round resumes when it is driven again.
    private void ServiceRounds(byte[] master) {
        foreach (WirelessDevice device in _table.Devices) {
            if (!IsDriven(device, master) || device.SendingCommand is null) {
                continue;
            }

            Isolated(device.MacText + "/command", () => {
                byte[]? send = device.NextSend();
                if (send != null) {
                    Transmit(device, send);
                }
            });
        }
    }

    // A command's own pass: a round the device ended since the command was last serviced is
    // reported as acknowledged or exhausted, once; a round under way was sent by ServiceRounds this
    // second; otherwise a round is begun, with its first send, under the device's next sequence. The
    // device has one sequence (RfDevice's targe_cmd_seq, which every pending command L-Connect
    // queues carries, so the device reporting it ends them all, whichever it received), so a
    // command with no round waits while another has the device's: the sequence the device reports
    // then acknowledges this command and no other.
    private CommandOutcome ServiceCommand(WirelessDevice device, WirelessDeviceCommand command, Func<byte, byte[]> payload) {
        WirelessRoundEnd? ended = command.TakeEnd();
        if (ended != null) {
            return ended == WirelessRoundEnd.Acknowledged ? CommandOutcome.Acknowledged : CommandOutcome.Exhausted;
        }

        if (command.Sequence != null) {
            return CommandOutcome.Sent;
        }

        if (device.SendingCommand != null) {
            return CommandOutcome.Waiting;
        }

        Transmit(device, device.BeginCommand(command, payload));
        return CommandOutcome.Sent;
    }

    // SyncControlInfo's b: how many devices before this one on the table the pass has counted,
    // bound and not changing effect, whether or not a command of their own is pending. The device
    // is on the table, as only a device on it is ever sent anything.
    private byte CountedBefore(WirelessDevice device) {
        int counted = 0;
        for (int i = 0; !ReferenceEquals(_table.Devices[i], device); i++) {
            WirelessDevice earlier = _table.Devices[i];
            if (earlier.IsBound && !earlier.ChangingEffect) {
                counted++;
            }
        }

        return unchecked((byte)counted);
    }

    // MasterDevice.SyncRgbData: a bound device of this master with a saved effect it does not report
    // running is streamed it and marked as changing effect; one that reports it is not.
    private void SyncEffects(byte[] master) {
        foreach (WirelessDevice device in _table.Devices) {
            if (!IsDriven(device, master)) {
                continue;
            }

            WirelessSavedEffect? effect = EffectOf(device);
            if (effect is null || device.EffectAbandoned) {
                continue;
            }

            if (device.Record.IsRunningEffect(effect.EffectIndex)) {
                if (device.ChangingEffect) {
                    _log.Write(string.Format(CultureInfo.InvariantCulture, "W{0}:{1} took its saved lighting effect", _index, device.MacText));
                }

                device.ChangingEffect = false;
                device.EffectStreamLogged = false;
                device.UntakenStreams = 0;
                continue;
            }

            DateTime now = _clock.UtcNow;
            if (device.UntakenStreams >= MinimumUntakenStreams
                && ClockSpan.Since(now, device.FirstUntakenStreamUtc) >= GiveUpLightingAfter) {
                device.ChangingEffect = false;
                device.EffectAbandoned = true;
                _log.Write(string.Format(
                    CultureInfo.InvariantCulture,
                    "W{0}:{1} did not take its saved lighting effect in {2} stream(s) tried over {3} s; lighting replay stopped for it, its fans are driven as usual",
                    _index,
                    device.MacText,
                    device.UntakenStreams,
                    GiveUpLightingAfter.TotalSeconds));
                continue;
            }

            // Only a stream that was sent marks the device as changing effect: a dongle that failed
            // the send has not asked the device anything, so its fans stay driven meanwhile.
            if (device.UntakenStreams++ == 0) {
                device.FirstUntakenStreamUtc = now;
            }

            device.ChangingEffect = Isolated(device.MacText + "/lighting", () => StreamEffect(device, master, effect));
        }
    }

    private void StreamEffect(WirelessDevice device, byte[] master, WirelessSavedEffect effect) {
        byte[][] payloads = WirelessProtocol.EncodeEffectPayloads(device.Mac, master, Retimed(device, effect));
        Transmit(device, payloads[0]);
        for (int repeat = 0; repeat < EffectHeaderRepeats; repeat++) {
            _delay.Wait(EffectHeaderGap);
            Transmit(device, payloads[0]);
        }

        for (int chunk = 1; chunk < payloads.Length; chunk++) {
            Transmit(device, payloads[chunk]);
        }

        _delay.Wait(EffectSettle);
        _saves.EffectStreamed(_clock.UtcNow);
        if (!device.EffectStreamLogged) {
            device.EffectStreamLogged = true;
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "W{0}:{1} streaming its saved lighting effect {2} ({3} chunks) until it reports running it",
                _index,
                device.MacText,
                WirelessDeviceRecord.FormatMac(effect.EffectIndex),
                payloads.Length - 1));
        }
    }

    // MasterDevice.SyncStrimmer_22: a type 2 or 4 Strimer plays its effect over the same span as the
    // first type 1 or 3 Strimer on the table, whatever that one is bound to. L-Connect keeps the new
    // interval on the effect, so it stays when that Strimer is gone.
    private WirelessSavedEffect Retimed(WirelessDevice device, WirelessSavedEffect effect) {
        if (device.Record.DeviceType == 2 || device.Record.DeviceType == 4) {
            WirelessDevice? leader = null;
            foreach (WirelessDevice candidate in _table.Devices) {
                if (candidate.Record.DeviceType == 1 || candidate.Record.DeviceType == 3) {
                    leader = candidate;
                    break;
                }
            }

            WirelessSavedEffect? leading = leader is null ? null : EffectOf(leader);
            if (leading != null) {
                device.RetimedInterval = leading.Interval * leading.TotalFrame / effect.TotalFrame;
            }
        }

        return device.RetimedInterval.HasValue ? effect.WithInterval(device.RetimedInterval.Value) : effect;
    }

    // MasterDevice.SaveConfig(1): one broadcast and no pause after it, then the save is stamped.
    private void SaveConfiguration(byte[] master) {
        Isolated("save", () => {
            Broadcast(WirelessProtocol.EncodeSaveConfigurationPayload(master));
            _saves.Saved(_clock.UtcNow);
            _log.Write(string.Format(CultureInfo.InvariantCulture, "W{0}: asked every device to save its configuration", _index));
        });
    }

    // Addressed to one device: the transmit header carries the channel and receiver slot the device
    // reports now (MasterDevice.SendRfData(rf.channel, rf.rx_type, ...)).
    private void Transmit(WirelessDevice device, byte[] payload) {
        foreach (byte[] packet in WirelessProtocol.EncodeTransmitPackets(device.Record.Channel, device.Record.ReceiverType, payload)) {
            _dongles.WriteTransmitter(packet);
        }
    }

    // To every device: the master channel and every receiver slot.
    private void Broadcast(byte[] payload) {
        foreach (byte[] packet in WirelessProtocol.EncodeTransmitPackets(Channel, WirelessProtocol.BroadcastReceiverType, payload)) {
            _dongles.WriteTransmitter(packet);
        }
    }

    // One piece of work for one device (or one broadcast), isolated so that its failure is logged -
    // once until it recovers - and costs nothing else this second.
    // Returns whether the work completed.
    private bool Isolated(string key, Action work) {
        try {
            work();
            _faults.Recovered(key);
            return true;
        }
#pragma warning disable CA1031 // resilience: one wireless device's failure must not stop the others, as the worker isolates one controller from the next
        catch (Exception ex) {
            _faults.Failed(key, ex.Message);
            return false;
        }
#pragma warning restore CA1031
    }

    // A dongle the transport lost its handle to and has reopened may have come back reset. Nothing
    // is replayed while either handle is still faulted: the dongle is off the bus, a transfer of
    // the cycle is what reopens it, and a saved look tried again then would be sent, and given up
    // on, against a dead transmitter, and a command's round acknowledged by a record no read has
    // refreshed. Once both are back, the master's channel is re-asserted by the next master query,
    // sent now instead of at the next second, and every device's speed and effect are resent by
    // the ordinary comparison against what it reports. The registered replay runs first; a throw
    // leaves the generations unrecorded, so it is retried next tick, and one that reports failure
    // stays owed and is tried again on its own.
    private void ReplaySetupIfReconnected() {
        if (_dongles.IsFaulted) {
            return;
        }

        int transmitterGeneration = _dongles.TransmitterGeneration;
        int receiverGeneration = _dongles.ReceiverGeneration;
        if (transmitterGeneration == _setUpTransmitterGeneration && receiverGeneration == _setUpReceiverGeneration) {
            _reconnectReplay.Apply();
            return;
        }

        _reconnectReplay.Owe();
        _reconnectReplay.Apply();

        // A reset dongle, or devices that came back with it, get their saved look tried again. A
        // screen switch or colouring marked for the process keeps its mark after the receiver's
        // reopen, as L-Connect repeats neither on a resume; after the transmitter's, every mark was
        // dropped when its loss was counted, so both are begun again with the rest.
        foreach (WirelessDevice device in _table.Devices) {
            device.ReapplySavedLook();
        }

        _setUpTransmitterGeneration = transmitterGeneration;
        _setUpReceiverGeneration = receiverGeneration;
        _lastCycleUtc = DateTime.MinValue;
        _lastReadUtc = DateTime.MinValue;
        _log.Write(string.Format(
            CultureInfo.InvariantCulture,
            "W{0} reconnected (transmitter generation {1}, receiver generation {2})",
            _index,
            transmitterGeneration,
            receiverGeneration));
    }

    // Give every driven device the sensors it now needs, and publish the latest readings. Returns
    // whether any sensor was added, or a device whose sensors were retained from an earlier read is
    // driven again: either way the host's sensors may no longer match who drives what.
    private bool UpdateSensors(byte[] master) {
        // The saved effect is read from disk once per device, outside the lock the host waits on.
        foreach (WirelessDevice device in _table.Devices) {
            if (IsDriven(device, master)) {
                _ = EffectOf(device);
            }
        }

        bool changed = false;
        var drivenDevices = new List<string>();
        lock (_lock) {
            int lit = 0;
            foreach (WirelessDevice device in _table.Devices) {
                if (!IsDriven(device, master)) {
                    continue;
                }

                drivenDevices.Add(device.MacText);
                lit += device.Effect is null ? 0 : 1;
                changed |= _sensorsByDevice.ContainsKey(device.MacText) && !_drivenMacs.Contains(device.MacText);
                changed |= EnsureSensors(device);
            }

            _drivenDevices = drivenDevices.Count;
            _litDevices = lit;
            _drivenMacs.Clear();
            _drivenMacs.UnionWith(drivenDevices);
            PublishReadings();
        }

        // What the radio drives now, for a controller that could reach the same device another way.
        _processState.RecordBoundDevices(this, drivenDevices);
        return changed;
    }

    // Caller holds _lock. What each kind of device is given (see the class summary), only added to.
    private bool EnsureSensors(WirelessDevice device) {
        WirelessDeviceRecord record = device.Record;
        if (!_sensorsByDevice.TryGetValue(device.MacText, out DeviceSensors? sensors)) {
            sensors = new DeviceSensors(record);
            _sensorsByDevice[device.MacText] = sensors;
        }

        int before = _controls.Count + _readings.Count + _temperatures.Count;
        if (record.Kind == WirelessDeviceKind.CaseFans) {
            // The Lancool 217 reports a fitted fan as type 1 in its slot, and L-Connect only offers the
            // front control when a front fan is fitted (fans_type[0] or [1] == 1, HasFrontFan) and the
            // rear control when the rear fan is (fans_type[2] == 1), in LWirelessController's
            // Lancool217InfSubProfile; one fitted later is added then, like any new sensor.
            if (record.FanTypes[0] == 1 || record.FanTypes[1] == 1) {
                AddControl(sensors, ControlKind.CaseFront, record.FanTypes[0] == 1 ? 0 : 1);
            }

            if (record.FanTypes[CaseRearSlot] == 1) {
                AddControl(sensors, ControlKind.CaseRear);
            }

            for (int slot = 0; slot < CaseFanSlots; slot++) {
                if (record.FanTypes[slot] == 1) {
                    AddReading(sensors, slot);
                }
            }
        } else if (record.Kind == WirelessDeviceKind.WaterBlock) {
            if (record.FanCount > 0) {
                AddControl(sensors, ControlKind.Group);
            }

            AddFanReadings(sensors, Math.Min(record.FanCount, WirelessProtocol.WaterBlockPumpSlot));
            AddControl(sensors, ControlKind.Pump);
            AddReading(sensors, WirelessProtocol.WaterBlockPumpSlot);
            if (sensors.Coolant is null) {
                sensors.Coolant = new Temperature(device.MacText, sensors.Label);
                _temperatures.Add(sensors.Coolant);
            }
        } else if (record.Kind == WirelessDeviceKind.V150) {
            // The V150 is a case device with two curves: the front pair on slots 0-1, which the app
            // always offers (V150SubProfile.CreateFrom), and the rear fan on slot 2 once one is
            // fitted (fans_type[2] == 1, the Lancool 217's rule). The front control takes the
            // device's plain control id, as a group's does, so an existing curve bound to the V150
            // stays bound; its speed is slot 0's when that fan is fitted and slot 1's otherwise
            // (V150SubProfile.UpdateFrom), read again from every record. A reading is registered
            // for every slot 0-2 that reports a fitted fan, as the app reads them, and for the first
            // fan_num slots, so a V150 that reports its fans either way has them.
            Control front = AddControl(sensors, ControlKind.V150Front, FrontRpmSlot(record));
            front.RpmSlot = FrontRpmSlot(record);
            if (record.FanTypes[CaseRearSlot] == 1) {
                AddControl(sensors, ControlKind.CaseRear);
            }

            for (int slot = 0; slot < CaseFanSlots; slot++) {
                if (record.FanTypes[slot] == 1 || slot < record.FanCount) {
                    AddReading(sensors, slot);
                }
            }

            AddFanReadings(sensors, record.FanCount);
        } else if (record.Kind != WirelessDeviceKind.Strimer) {
            // A fan group, or a type L-Connect has no name for. NeedSyncPwm never writes a device
            // without fans.
            if (record.FanCount > 0) {
                AddControl(sensors, ControlKind.Group);
            }

            AddFanReadings(sensors, record.FanCount);
        }

        return _controls.Count + _readings.Count + _temperatures.Count != before;
    }

    // V150SubProfile.UpdateFrom: the front speed is fans_type[0] == 1 ? Speeds[0] : Speeds[1]; slot 0
    // stands for it while no front fan is fitted at all, when both read 0 anyway.
    private static int FrontRpmSlot(WirelessDeviceRecord record) => record.FanTypes[0] != 1 && record.FanTypes[1] == 1 ? 1 : 0;

    private Control AddControl(DeviceSensors sensors, ControlKind kind) => AddControl(sensors, kind, 0);

    // The control of that kind the device has, or the one it is given now.
    private Control AddControl(DeviceSensors sensors, ControlKind kind, int frontRpmSlot) {
        foreach (Control existing in sensors.Controls) {
            if (existing.Kind == kind) {
                return existing;
            }
        }

        var control = new Control(sensors, kind, frontRpmSlot);
        sensors.Controls.Add(control);
        if (kind == ControlKind.Pump) {
            sensors.Pump = control;
        }

        _controls.Add(control);
        return control;
    }

    private void AddFanReadings(DeviceSensors sensors, int fanCount) {
        for (int slot = 0; slot < fanCount; slot++) {
            AddReading(sensors, slot);
        }
    }

    private void AddReading(DeviceSensors sensors, int slot) {
        if (sensors.Readings[slot] != null) {
            return;
        }

        var reading = new Reading(sensors, slot);
        sensors.Readings[slot] = reading;
        _readings.Add(reading);
    }

    // Caller holds _lock. A fan of a device that is not heard - dropped from the table, or lost on
    // it - reads 0, and a coolant sensor nothing, so FanControl sees a stopped fan rather than a
    // frozen one. A decoded RPM is twelve bits (the record's flag nibbles are masked off), so no
    // reading a record can carry is beyond what a fan could produce.
    private void PublishReadings() {
        foreach (Reading reading in _readings) {
            WirelessDevice? device = _table.Find(reading.Device);
            reading.Value = device is null || device.IsLost ? 0 : device.Record.Rpm[reading.Slot];
        }

        foreach (Temperature temperature in _temperatures) {
            WirelessDevice? device = _table.Find(temperature.Device);
            temperature.Value = device is null || device.IsLost
                ? (float?)null
                : device.Record.FanTypes[WirelessProtocol.WaterBlockPumpSlot];
        }
    }

    // Caller holds _lock, and writes messages once it is released. Logged when a control's duty
    // changes, not every second it is resent.
    private void LogDutyChange(WirelessDevice device, Control control, int duty, List<string> messages) {
        if (duty == control.LoggedDuty) {
            return;
        }

        control.LoggedDuty = duty;
        messages.Add(duty < 0
            ? string.Format(CultureInfo.InvariantCulture, "W{0}:{1} {2} released", _index, device.MacText, control.Part)
            : string.Format(CultureInfo.InvariantCulture, "Set W{0}:{1} {2} = {3}%", _index, device.MacText, control.Part, duty));
    }

    private void WriteAll(List<string> messages) {
        foreach (string message in messages) {
            _log.Write(message);
        }
    }

    // The saved look is read from disk once per device: L-Connect's sync switch for it, and its
    // effect whatever the switch says. MasterDevice.SyncRgbData streams a bound device its saved
    // effect whenever it does not report running it and never reads IsSyncMbLight, so a synced
    // device that lost its effect (a reset, a flash that did not save) gets it back on its flash,
    // ready for the switch being turned off; the switch only decides what the LEDs show. The wired
    // families are different: their controllers' setLightingConfig is guarded by the switch, so no
    // look is written to them.
    private WirelessSavedEffect? EffectOf(WirelessDevice device) {
        if (!device.EffectLoaded) {
            device.EffectLoaded = true;
            device.FollowsMotherboard = _configuration.FindMotherboardArgbSync(device.MacText);
            device.Effect = _configuration.FindEffect(device.MacText);
            if (device.FollowsMotherboard) {
                _log.Write(string.Format(
                    CultureInfo.InvariantCulture,
                    "W{0}:{1} lighting is left to the motherboard's ARGB header (L-Connect's sync switch for it is on); its saved effect is still streamed to its flash, as L-Connect streams it",
                    _index,
                    device.MacText));
            }
        }

        return device.Effect;
    }

    // Driven: bound to this master, as its latest record says, and heard by this controller at least
    // once. A device that has moved to another master is never sent anything - its speed command
    // would bind it back. A lost device is still driven (see WirelessDevice.IsLost); one known only from L-Connect's
    // locked list is not, until it is heard.
    private static bool IsDriven(WirelessDevice device, byte[] master)
        => device.IsBound && device.Record.IsBoundTo(master) && device.EverHeard;

    private static bool SameBytes(byte[] a, byte[] b) {
        for (int i = 0; i < a.Length; i++) {
            if (a[i] != b[i]) {
                return false;
            }
        }

        return true;
    }

    private static string Bytes(byte[] values) => "[" + string.Join(",", values) + "]";

    private enum ControlKind {
        Group,
        Pump,
        CaseFront,
        CaseRear,
        V150Front,
    }

    // What one pass of a command sent under a sequence did (see ServiceCommand).
    private enum CommandOutcome {
        Sent,
        Waiting,
        Acknowledged,
        Exhausted,
    }

    // Where a command the record can confirm stands before a pass (see StepOf).
    private enum RoundStep {
        Send,
        Confirmed,
        Readback,
        Abandoned,
    }

    // Everything the controller has given one device, and the name the device is shown under.
    private sealed class DeviceSensors {
        public DeviceSensors(WirelessDeviceRecord record) {
            MacText = record.MacText;
            Label = "Lian Li " + ProductName(record) + " Wireless " + record.MacText.Substring(record.MacText.Length - 6);
            Kind = record.Kind;
        }

        public string MacText { get; }

        public string Label { get; }

        public WirelessDeviceKind Kind { get; }

        public List<Control> Controls { get; } = new List<Control>();

        public Control? Pump { get; set; }

        public Reading?[] Readings { get; } = new Reading?[WirelessProtocol.SlotsPerGroup];

        public Temperature? Coolant { get; set; }

        private static string ProductName(WirelessDeviceRecord record) {
            switch (record.Kind) {
                case WirelessDeviceKind.WaterBlock:
                    return "HydroShift II";
                case WirelessDeviceKind.CaseFans:
                    return "Lancool 217";
                case WirelessDeviceKind.V150:
                    return "V150";
                case WirelessDeviceKind.Unrecognised:
                    return "Type " + record.DeviceType.ToString(CultureInfo.InvariantCulture);
            }

            switch (WirelessProtocol.FamilyOf(record.FanTypes[0])) {
                case WirelessFanFamily.SlV3:
                    return "UNI FAN SL V3";
                case WirelessFanFamily.TlV2:
                    return "UNI FAN TL V2";
                case WirelessFanFamily.SlInfinity:
                    return "UNI FAN SL-Infinity";
                case WirelessFanFamily.Cl:
                    return "UNI FAN CL";
                case WirelessFanFamily.SlInfinityFlex:
                    return "UNI FAN SL-INF FLEX";
                case WirelessFanFamily.TlFlex:
                    return "UNI FAN TL FLEX";
                case WirelessFanFamily.SlV4:
                    return "UNI FAN SL FLEX";
                case WirelessFanFamily.P28V2:
                    return "UNI FAN P28 V2";
                case WirelessFanFamily.ClV2:
                    return "UNI FAN CL FLEX";
                default:
                    return "UNI FAN";
            }
        }
    }

    // One FanControl control: which device, which role, the slots its PWM goes to.
    private sealed class Control {
        private static readonly int[] AllSlots = { 0, 1, 2, 3 };
        private static readonly int[] FrontSlots = { 0, 1 };
        private static readonly int[] RearSlots = { 2 };

        // frontRpmSlot is the front fan whose speed the Lancool 217's front control reads: the first
        // one fitted, so the reading it names is one that is registered.
        public Control(DeviceSensors owner, ControlKind kind, int frontRpmSlot) {
            Owner = owner;
            Kind = kind;
            switch (kind) {
                case ControlKind.Pump:
                    Part = "pump";
                    Slots = Array.Empty<int>();
                    RpmSlot = WirelessProtocol.WaterBlockPumpSlot;
                    Name = owner.Label + " Pump";
                    break;
                case ControlKind.CaseFront:
                case ControlKind.V150Front:
                    Part = "front";
                    Slots = FrontSlots;
                    RpmSlot = frontRpmSlot;
                    Name = owner.Label + " Front Fans";
                    break;
                case ControlKind.CaseRear:
                    Part = "rear";
                    Slots = RearSlots;
                    RpmSlot = RearSlots[0];
                    Name = owner.Label + " Rear Fan";
                    break;
                default:
                    Part = "fans";
                    Slots = AllSlots;
                    Name = owner.Label;
                    break;
            }

            // A group's one control and the V150's front control are the device's plain control
            // id, so an existing curve bound to a V150 stays bound; every other control is named
            // for its part.
            Id = kind == ControlKind.Group || kind == ControlKind.V150Front
                ? WirelessSensorIds.GroupControlId(owner.MacText)
                : "LianLi/w" + owner.MacText + "/" + Part + "/ctl";
        }

        public DeviceSensors Owner { get; }

        public ControlKind Kind { get; }

        public string Part { get; }

        public int[] Slots { get; }

        // The slot whose RPM stands for the control: its first fan, or the pump. The V150's front
        // control follows the fitted fan from record to record.
        public int RpmSlot { get; set; }

        public string Id { get; }

        public string Name { get; }

        public int Duty { get; set; } = -1;

        public int LoggedDuty { get; set; } = -1;
    }

    // One fan's RPM, or a water block's pump's.
    private sealed class Reading {
        public Reading(DeviceSensors owner, int slot) {
            Device = owner.MacText;
            Slot = slot;
            Id = IdOf(owner, slot);
            Name = NameOf(owner, slot);
        }

        public string Device { get; }

        public int Slot { get; }

        public string Id { get; }

        public string Name { get; }

        public float Value { get; set; }

        public static string IdOf(DeviceSensors owner, int slot) => "LianLi/w" + owner.MacText + "/" + PartOf(owner, slot) + "/fan";

        public static string NameOf(DeviceSensors owner, int slot) {
            if (IsPump(owner, slot)) {
                return owner.Label + " Pump RPM";
            }

            if (owner.Kind == WirelessDeviceKind.CaseFans) {
                return owner.Label + (slot == 2 ? " Rear Fan RPM" : " Front Fan " + (slot + 1).ToString(CultureInfo.InvariantCulture) + " RPM");
            }

            return owner.Label + " Fan " + (slot + 1).ToString(CultureInfo.InvariantCulture) + " RPM";
        }

        private static string PartOf(DeviceSensors owner, int slot)
            => IsPump(owner, slot) ? "pump" : "f" + slot.ToString(CultureInfo.InvariantCulture);

        private static bool IsPump(DeviceSensors owner, int slot)
            => owner.Kind == WirelessDeviceKind.WaterBlock && slot == WirelessProtocol.WaterBlockPumpSlot;
    }

    // A water block's coolant temperature.
    private sealed class Temperature {
        public Temperature(string device, string label) {
            Device = device;
            Id = "LianLi/w" + device + "/coolant/temp";
            Name = label + " Coolant";
        }

        public string Device { get; }

        public string Id { get; }

        public string Name { get; }

        public float? Value { get; set; }
    }

    // A failure is logged when it starts or its message changes, and its end once, so a dongle that
    // fails every second writes two lines, not one a second.
    private sealed class FaultLog {
        private readonly Dictionary<string, string> _failing = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly int _index;
        private readonly ILog _log;

        public FaultLog(int index, ILog log) {
            _index = index;
            _log = log;
        }

        public void Failed(string key, string message) {
            if (_failing.TryGetValue(key, out string? previous) && previous == message) {
                return;
            }

            _failing[key] = message;
            _log.Write(string.Format(CultureInfo.InvariantCulture, "W{0} {1} failed: {2}", _index, key, message));
        }

        public void Recovered(string key) {
            if (_failing.Remove(key)) {
                _log.Write(string.Format(CultureInfo.InvariantCulture, "W{0} {1} recovered", _index, key));
            }
        }
    }
}
