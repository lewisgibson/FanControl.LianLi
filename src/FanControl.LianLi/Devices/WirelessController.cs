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
/// block's pump, the Lancool 217's front pair and rear fan, one RPM reading per fan and a water
/// block's coolant temperature - and <see cref="TopologyChanged"/> tells the plugin to ask the host
/// for a refresh. Sensors are only ever added, so an index always names the same thing; a device
/// that goes unheard keeps them and reads 0 rpm and no temperature until it is heard again.</para>
///
/// <para>Once a second (<see cref="ApplyPending"/>, the one-second block of <c>MasterDevice.Run</c>)
/// the controller resends each driven device's speed whenever what it reports is more than 5 off
/// its target (<c>SyncPwm</c>), checks the periodic save, re-sends the master query - which also
/// re-asserts the master's RF channel and is how a master that has not answered yet is found -,
/// sends each driven water block its parameter block (<c>SendAioInfo</c>) and broadcasts the clock
/// pulse (<c>SyncMasterClock</c>). On every call it also streams any saved lighting effect a device
/// is not running (<c>SyncRgbData</c>) and sends the debounced save.</para>
///
/// <para>Construction does not wait on the hardware for long and never throws for it: it asks for
/// the master a few times and, once it answers, reads the list until two reads in a row agree - at
/// most two and a half seconds of waiting. A master that never answered leaves a controller with no
/// sensors that keeps asking every second. Every piece of per-device work is isolated, so one
/// device's failure is logged and costs only that device that second. The FanControl-thread surface
/// only touches locked state; all I/O is on the worker-thread methods and the constructor.</para>
/// </summary>
internal sealed class WirelessController : IFanDevice, ITemperatureSource, IFanSpeedSource {
    // MasterDevice.Run's one-second block.
    private static readonly TimeSpan CycleInterval = TimeSpan.FromSeconds(1);

    // How long construction may spend on a master that has not answered, and on a list that is
    // still changing: at most three queries, then at most four list reads, half a second apart -
    // two and a half seconds of waiting in all, well inside the host's deadline for the whole of
    // Initialize. MasterDevice.Run reads the list at that pace (once more than 500 ms have passed).
    // L-Connect itself does not wait - Run simply keeps going - so the wait is the plugin's, there so
    // the host's first load has the devices that are already there rather than one refresh each.
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
    // A dongle that recovers by resetting, and a device heard again, are tried afresh. Time, not a
    // count of calls, because every control change wakes the worker and so brings streams forward.
    private static readonly TimeSpan GiveUpLightingAfter = TimeSpan.FromSeconds(10);
    private const int MinimumUntakenStreams = 3;

    // RFController.SwitchAioLcdWirelessMode queues ten sends of the screen-mode switch, one a pass
    // of MasterDevice.SyncControlInfo, until the device reports the command sequence back.
    private const int ScreenModeSends = 10;

    // MasterDevice.CheckChannelConflict spaces the masters in range four channels apart from 8.
    private const int MasterChannelSpacing = 4;

    // MasterDevice.SaveConfig(1) sleeps 200 ms after its one send.
    private static readonly TimeSpan SaveSettle = TimeSpan.FromMilliseconds(200);

    // The Lancool 217 has fans in slots 0-2 only: the front pair and the rear fan.
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
    private Action? _reconnectReplay;

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

        Discover();
        _setUpTransmitterGeneration = _dongles.TransmitterGeneration;
        _setUpReceiverGeneration = _dongles.ReceiverGeneration;
    }

    /// <summary>
    /// Raised on the worker thread when a device is heard that needs sensors the host has not
    /// loaded yet (a newly bound device, or a group whose fan count grew). The plugin answers it by
    /// asking the host to refresh (FanControl's <c>IPlugin3.RefreshRequested</c>).
    /// </summary>
    public event EventHandler? TopologyChanged;

    /// <summary>
    /// Whether the receiver has reported a device this pair drives: one that has is not waiting for its
    /// devices to check in, even when none of them has a sensor (only Strimers, say). A locked-list entry
    /// not yet heard, or a device of another master, does not count.
    /// </summary>
    public bool HasHeardDevices => Volatile.Read(ref _heardDevices);

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
    public void ReplayOnReconnect(Action replay) {
        _reconnectReplay = replay ?? throw new ArgumentNullException(nameof(replay));
    }

    // ---------- FanControl-thread surface (no I/O) ----------

    /// <summary>Set the commanded duty for a control. The worker applies it on its next cycle.</summary>
    public void SetTarget(int channel, int duty) {
        lock (_lock) {
            _controls[channel].Duty = duty;
        }
    }

    /// <summary>Release a control: its slots are no longer resent, and the device keeps what it last had.</summary>
    public void ReleaseChannel(int channel) {
        lock (_lock) {
            _controls[channel].Duty = -1;
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
        // debounced save, rather than write the devices' flash on every refresh.
        byte[]? master = _masterMac;
        if (master != null && _saves.HasPendingSave) {
            SaveConfiguration(master);
        }

        _dongles.Dispose();
    }

    // One list read applied to the table and the sensors. Whether a sensor was added; the list
    // read's own success is left in _listRead for the startup wait. Construction calls this rather
    // than PollRpm, so what it finds is part of the first load and raises no TopologyChanged.
    private bool Refresh() {
        byte[]? master = _masterMac;
        if (master is null) {
            // No master, no list to read (RefreshList); but the readings stop being live.
            _listRead = false;
            _table.MissRead();
            var messages = new List<string>();
            lock (_lock) {
                PublishReadings(messages);
            }

            WriteAll(messages);
            return false;
        }

        WirelessDeviceList? list = ReadList();
        _listRead = list != null;
        _table.Apply(list, master, _masterClock);
        // Only a device this pair drives and has heard settles the wait: an entry from L-Connect's locked
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
    // SendAioInfo, SyncMasterClock.
    private void RunCycle() {
        byte[]? master = _masterMac;
        if (master != null) {
            SyncSpeeds(master);
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
        Isolated("clock", () => Broadcast(WirelessProtocol.EncodeClockPayload(master)));
        ReleaseStaleLock(master);
        CheckChannelConflict();
    }

    // MasterDevice.CheckChannelConflict: every master in radio range takes a channel by its place
    // among them in address order, 8 for the first, then 12, 16 and so on, so two L-Wireless
    // controllers near each other do not share one. When this master reports an even channel other
    // than its own place's (the even channels are the ones L-Connect hands out; one the user chose in
    // L-Connect is odd and never moved), it moves there for this run, and its devices follow through
    // the ordinary re-homing (MasterDevice.SwitchChannel). The move is not saved, as L-Connect does
    // not save it, and it is made once rather than again on every read until it shows.
    private void CheckChannelConflict() {
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
#pragma warning disable CA1031 // resilience: a failed read counts as a read nobody was heard in, as RefreshList counts it
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
    // bound count falls only when a bound V150 is dropped), so the indices after it match again.
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
    // (LWirelessDevice.SetFanSpeed), the Lancool 217 its front pair on slots 0-1 and rear fan on slot
    // 2 with slot 3 zero (SetCaseSpeed). A slot no set control drives carries whatever the device
    // reports for it, and is left out of the comparison: the packet goes out whole, so a speed sent
    // for the front pair must not restore a rear fan FanControl has let go of. With newValues false
    // (a drifted clock) the driven slots keep the targets they had.
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

                int duty = control.Duty;
                LogDutyChange(device, control, duty, messages);
                if (duty < 0) {
                    continue;
                }

                byte pwm = control.Kind == ControlKind.Group
                    ? WirelessProtocol.FanPwm(duty, WirelessProtocol.GroupDutyFloor(device.Record))
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
    // it drives whose pump FanControl has set, so a pump nobody controls is left as it was.
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

        if (duty < 0) {
            return;
        }

        int rpm = WirelessProtocol.PumpRpmFromDuty(duty, device.Record.DeviceType);
        WirelessAioPresentation presentation = _configuration.FindPumpPresentation(device.MacText) ?? WirelessAioPresentation.Default;
        if (!presentation.AdvanceMode) {
            SwitchScreenMode(device, master);
        }

        byte[] parameters = WirelessProtocol.EncodeAioParameters(presentation, WirelessProtocol.PumpTimerFromRpm(rpm, device.Record.DeviceType));
        Transmit(device, WirelessProtocol.EncodeAioPayload(device.Mac, master, device.TargetReceiverType, Channel, parameters));
    }

    // LWirelessController.applyWirelessMode, at startup and whenever a water block binds: a screen
    // saved out of advance mode is switched to its wireless theme ahead of its parameters, so a
    // screen left on PC-streamed content (a switch cut short by sleep, L-Connect stopped while it
    // streamed) shows its theme again. The switch goes out under a command sequence the device has
    // not acknowledged, once a second, until its record reports that sequence or it has gone out as
    // often as L-Connect sends it. Each send is counted before it goes, so a failing dongle cannot
    // keep it going for ever.
    // A screen an earlier controller in this process switched is not switched again after a refresh,
    // as L-Connect switches it only when its service starts, unless the block came back since.
    private void SwitchScreenMode(WirelessDevice device, byte[] master) {
        if (device.ScreenModeSwitched) {
            return;
        }

        if (!device.LookReapplied && _processState.IsScreenSwitched(device.MacText)) {
            device.ScreenModeSwitched = true;
            return;
        }

        byte sequence;
        if (device.ScreenModeSequence is byte sending) {
            if (device.Record.CommandSequence == sending || device.ScreenModeSends >= ScreenModeSends) {
                device.ScreenModeSwitched = true;
                device.ScreenModeSequence = null;
                _processState.MarkScreenSwitched(device.MacText);
                _log.Write(string.Format(
                    CultureInfo.InvariantCulture,
                    device.Record.CommandSequence == sending
                        ? "W{0}:{1} switched its screen to its wireless theme"
                        : "W{0}:{1} did not acknowledge the switch to its wireless theme in {2} sends",
                    _index,
                    device.MacText,
                    device.ScreenModeSends));
                return;
            }

            sequence = sending;
        } else {
            sequence = WirelessProtocol.NextCommandSequence(device.Record.CommandSequence);
            device.ScreenModeSequence = sequence;
            device.ScreenModeSends = 0;
        }

        device.ScreenModeSends++;
        Transmit(device, WirelessProtocol.EncodeWirelessThemePayload(
            device.Mac, master, device.TargetReceiverType, Channel, CountedBefore(device), sequence));
    }

    // SyncControlInfo's b: how many devices before this one on the table the pass has counted -
    // bound, not changing effect, and with no command of their own pending (the plugin's only one is
    // this screen switch).
    private byte CountedBefore(WirelessDevice device) {
        // The device is on the table: only a device on it is ever sent anything.
        int counted = 0;
        for (int i = 0; !ReferenceEquals(_table.Devices[i], device); i++) {
            WirelessDevice earlier = _table.Devices[i];
            if (earlier.IsBound && !earlier.ChangingEffect && earlier.ScreenModeSequence is null) {
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

    // MasterDevice.SaveConfig(1): one broadcast, then a 200 ms pause, then the save is stamped.
    private void SaveConfiguration(byte[] master) {
        Isolated("save", () => {
            Broadcast(WirelessProtocol.EncodeSaveConfigurationPayload(master));
            _delay.Wait(SaveSettle);
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

    // A reopened dongle may have come back reset. Its channel is re-asserted by the next master
    // query, which is sent now rather than at the next second, and every device's speed and effect
    // are resent by the ordinary comparison against what it reports. The registered replay runs
    // first; a throw leaves the generations unrecorded, so it is retried next tick.
    private void ReplaySetupIfReconnected() {
        int transmitterGeneration = _dongles.TransmitterGeneration;
        int receiverGeneration = _dongles.ReceiverGeneration;
        if (transmitterGeneration == _setUpTransmitterGeneration && receiverGeneration == _setUpReceiverGeneration) {
            return;
        }

        _reconnectReplay?.Invoke();

        // A reset dongle, or devices that came back with it, get their saved lighting tried afresh.
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
    // whether any sensor was added.
    private bool UpdateSensors(byte[] master) {
        // The saved effect is read from disk once per device, outside the lock the host waits on.
        foreach (WirelessDevice device in _table.Devices) {
            if (IsDriven(device, master)) {
                _ = EffectOf(device);
            }
        }

        bool added = false;
        var messages = new List<string>();
        lock (_lock) {
            int driven = 0;
            int lit = 0;
            foreach (WirelessDevice device in _table.Devices) {
                if (!IsDriven(device, master)) {
                    continue;
                }

                driven++;
                lit += device.Effect is null ? 0 : 1;
                added |= EnsureSensors(device);
            }

            _drivenDevices = driven;
            _litDevices = lit;
            PublishReadings(messages);
        }

        WriteAll(messages);
        return added;
    }

    // Caller holds _lock. What each kind of device is given (see the class summary), only ever added to.
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
        } else if (record.Kind != WirelessDeviceKind.Strimer) {
            // A fan group, the V150, or a type L-Connect has no name for. NeedSyncPwm never writes a
            // device without fans, the V150 excepted.
            if (record.FanCount > 0 || record.Kind == WirelessDeviceKind.V150) {
                AddControl(sensors, ControlKind.Group);
            }

            AddFanReadings(sensors, record.FanCount);
        }

        return _controls.Count + _readings.Count + _temperatures.Count != before;
    }

    private void AddControl(DeviceSensors sensors, ControlKind kind) => AddControl(sensors, kind, 0);

    private void AddControl(DeviceSensors sensors, ControlKind kind, int frontRpmSlot) {
        foreach (Control existing in sensors.Controls) {
            if (existing.Kind == kind) {
                return;
            }
        }

        var control = new Control(sensors, kind, frontRpmSlot);
        sensors.Controls.Add(control);
        if (kind == ControlKind.Pump) {
            sensors.Pump = control;
        }

        _controls.Add(control);
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

    // Caller holds _lock, so the lines go to messages and are written once it is released: FanControl
    // reads these sensors on its own thread, holding a lock of its own, and must never wait on a log
    // write. A fan of a device that is not heard reads 0, and a coolant sensor nothing,
    // so FanControl sees a stopped fan rather than a frozen one; a reading no fan could produce keeps
    // the last good value and is logged once, as every other family here does it.
    private void PublishReadings(List<string> messages) {
        foreach (Reading reading in _readings) {
            WirelessDevice? device = _table.Find(reading.Device);
            if (device is null || device.IsLost) {
                reading.Value = 0;
                continue;
            }

            int rpm = device.Record.Rpm[reading.Slot];
            if (ChannelReadDecision.IsPlausible(rpm)) {
                if (reading.Implausible) {
                    messages.Add(string.Format(CultureInfo.InvariantCulture, "W{0}:{1} {2} recovered ({3} rpm)", _index, reading.Device, reading.Part, rpm));
                }

                reading.Implausible = false;
                reading.Value = rpm;
            } else if (!reading.Implausible) {
                reading.Implausible = true;
                messages.Add(string.Format(
                    CultureInfo.InvariantCulture, "W{0}:{1} {2} implausible {3} rpm ignored, keeping {4}", _index, reading.Device, reading.Part, rpm, reading.Value));
            }
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

    private WirelessSavedEffect? EffectOf(WirelessDevice device) {
        if (!device.EffectLoaded) {
            device.Effect = _configuration.FindEffect(device.MacText);
            device.EffectLoaded = true;
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

            Id = kind == ControlKind.Group ? "LianLi/w" + owner.MacText + "/ctl" : "LianLi/w" + owner.MacText + "/" + Part + "/ctl";
        }

        public DeviceSensors Owner { get; }

        public ControlKind Kind { get; }

        public string Part { get; }

        public int[] Slots { get; }

        // The slot whose RPM stands for the control: its first fan, or the pump.
        public int RpmSlot { get; }

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
            Part = PartOf(owner, slot);
            Id = IdOf(owner, slot);
            Name = NameOf(owner, slot);
        }

        public string Device { get; }

        public int Slot { get; }

        public string Part { get; }

        public string Id { get; }

        public string Name { get; }

        public float Value { get; set; }

        public bool Implausible { get; set; }

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
