using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using FanControl.LianLi.Logging;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Transport;

namespace FanControl.LianLi.Devices;

/// <summary>
/// Drives one FLEX or P28 V2 chain through its USB receiver (vendor 0x43A8), the way L-Connect's
/// wired controllers drive it (<c>docs/protocol.md</c>, "The FLEX receivers over USB"): the status
/// record read once a second for the fan count and each fan's RPM, and one speed command for the
/// whole chain whenever its duty changes or is due its re-send. Each command is written and its
/// one-packet reply read, and a reply that does not echo the command is refused rather than
/// parsed, which L-Connect does not check.
///
/// <para>The receiver is the same device the L-Wireless list carries, and its RF address, read
/// from the status reply, is its identity in both modes; so every sensor id is the one the
/// wireless controller gives the same chain (<see cref="WirelessSensorIds"/>), and a chain moved
/// between its receiver's USB port and the dongles keeps its curves. One address is never driven
/// both ways, and which way is decided again from the shared state at every write: a speed goes
/// out over USB only while <see cref="WirelessProcessState"/> says no wireless controller has the
/// chain bound to its master (L-Connect's <c>IsWirelessBound</c> rule), the duty comes from the
/// same state (<see cref="WirelessProcessState.ChainTarget"/>, so the host's curve reaches the
/// chain whichever controller it is registered on), and only after the device on the path has
/// answered the status request with this address since the transport last lost its handle to it.
/// A device that answers with another address is never written to again.</para>
///
/// <para>Sensors are only added, so an index keeps naming the same fan, and they are added
/// only while the chain is driven here: a receiver built while the radio has the chain has none
/// until the radio lets it go. A change of hands either way, a fan reported later and another
/// receiver answering raise <see cref="Changed"/> for the plugin. The FanControl-thread methods
/// only mutate locked state; the constructor and the worker-thread methods are the only places
/// the receiver is touched.</para>
/// </summary>
internal sealed class FlexReceiverController : IFanDevice, IFanSpeedSource, IDrivenSensorSource {
    private readonly int _index;
    private readonly IDeviceTransport _transport;
    private readonly FlexReceiverFamily _family;
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly WirelessProcessState _processState;

    // The receiver's RF address, read at construction: the key of every sensor id, and what every
    // later status reply must repeat.
    private readonly byte[] _mac;
    private readonly string _label;

    // The host reads the sensors; the worker publishes readings and adds sensors; both under
    // _lock. The readings are append-only.
    private readonly object _lock = new object();
    private readonly List<float> _speeds = new List<float>();
    private bool _hasControl;
    private int _fanCount;
    private int _lastWritten = -2;             // last duty written; -2 forces the next write
    private DateTime _lastWriteUtc = DateTime.MinValue;
    private int _loggedDuty = -1;
    private bool _resendAfterReconnect;
    private WirelessDeviceRecord _lastStatus;

    // Whether the chain was the radio's when the worker last looked, so a change of hands is
    // noticed once and reported; never what a write is decided on. Worker-thread only.
    private bool _noticedBound;

    // The transport generation under which the status last named _mac. A newer one means the
    // transport lost its handle to the device and its next transfer reopens the path, so whatever
    // answers there is identified before anything is sent to it: a re-enumerated receiver may have
    // been power-cycled, and the path may even carry another receiver.
    private int _identifiedGeneration;

    // Set, and never cleared, once the device on the path answered with another address.
    private volatile bool _anotherReceiver;
    private readonly ReconnectReplay _reconnectReplay;

    /// <summary>
    /// Take ownership of the receiver and read its status once for its address and fan count.
    /// Throws when the receiver does not answer, or answers with something other than its status.
    /// The chain gets no sensors until <see cref="SettleOwnership"/> says it is driven here.
    /// </summary>
    public FlexReceiverController(
        int index, IDeviceTransport transport, FlexReceiverFamily family, IClock clock, ILog log, WirelessProcessState processState) {
        _index = index;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _family = family;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _processState = processState ?? throw new ArgumentNullException(nameof(processState));
        _reconnectReplay = new ReconnectReplay(_clock);

        WirelessDeviceRecord status = ReadStatus();
        _mac = status.Mac;
        MacText = status.MacText;
        _label = "Lian Li " + FlexReceiverProtocol.ProductName(family) + " USB " + MacText.Substring(MacText.Length - 6);
        _fanCount = status.FanCount;
        _lastStatus = status;
        _identifiedGeneration = _transport.Generation;
    }

    /// <summary>
    /// Raised on the worker thread when the chain changed hands with the wireless controller
    /// either way, reported a fan it had not before, or turned out to be another receiver. The
    /// plugin answers each as <see cref="FlexReceiverChange"/> says.
    /// </summary>
    public event EventHandler<FlexReceiverChange>? Changed;

    /// <summary>The receiver's RF address as twelve lowercase hex digits: the key of its sensor ids, in either mode.</summary>
    public string MacText { get; }

    /// <summary>How many fans the receiver last reported, 0-4.</summary>
    public int FanCount {
        get {
            lock (_lock) {
                return _fanCount;
            }
        }
    }

    /// <summary>
    /// Whether the chain is the wireless controller's at this moment: bound to its master, heard by
    /// it and naming it, so nothing is sent to it over USB. Read from the shared state every time.
    /// </summary>
    public bool IsLeftToWireless => _processState.IsBoundToMaster(MacText);

    /// <summary>
    /// Whether a speed may go to the chain over USB at this moment: the device on the path has
    /// answered as this receiver since the transport last opened it, and the radio does not have
    /// the chain.
    /// </summary>
    public bool IsDrivingChain
        => !_anotherReceiver && Volatile.Read(ref _identifiedGeneration) == _transport.Generation && !IsLeftToWireless;

    /// <summary>The chain's sensors while it is driven here (see <see cref="IsDrivingChain"/>); none while it is retained only.</summary>
    public IEnumerable<string> DrivenSensorIds {
        get {
            if (!IsDrivingChain) {
                yield break;
            }

            bool hasControl;
            int readings;
            lock (_lock) {
                hasControl = _hasControl;
                readings = _speeds.Count;
            }

            if (hasControl) {
                yield return ControlId;
            }

            for (int slot = 0; slot < readings; slot++) {
                yield return FanId(slot);
            }
        }
    }

    /// <summary>One control for the chain once a fan is reported while it is driven here; none before.</summary>
    public int ChannelCount {
        get {
            lock (_lock) {
                return _hasControl ? 1 : 0;
            }
        }
    }

    /// <inheritdoc />
    public int FanSpeedCount {
        get {
            lock (_lock) {
                return _speeds.Count;
            }
        }
    }

    /// <summary>The one control exists only once a fan is reported, so it is populated.</summary>
    public bool IsChannelPopulated(int channel) => true;

    /// <summary>
    /// The sensor identity of the chain's control, keyed on the receiver's RF address exactly as the
    /// wireless controller keys the same chain's. The RPM half names the first fan's reading, which
    /// the plugin registers through <see cref="IFanSpeedSource"/>.
    /// </summary>
    public ChannelDescriptor Describe(int channel)
        => new ChannelDescriptor(ControlId, _label, FanId(0), FanName(0));

    /// <inheritdoc />
    public FanSpeedDescriptor DescribeFanSpeed(int index) => new FanSpeedDescriptor(FanId(index), FanName(index));

    /// <inheritdoc />
    public void ReplayOnReconnect(Func<bool> replay) => _reconnectReplay.Register(replay);

    /// <summary>
    /// Decide, once whose the chain is can be known (after the wireless pair's build in a scan, or
    /// straight after construction on a rebuild), whether it is driven here now: if the radio does
    /// not have it, it gets its sensors. Called once after construction; every poll decides again.
    /// </summary>
    public void SettleOwnership() {
        bool bound = IsLeftToWireless;
        _noticedBound = bound;
        if (bound) {
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "F{0}:{1} is bound to the L-Wireless controller's master, which drives it; left to the radio, with no sensors of its own",
                _index,
                MacText));
            return;
        }

        _ = EnsureSensors();
        Publish(_lastStatus);
    }

    // ---------- FanControl-thread methods (no I/O) ----------

    /// <summary>Set the commanded duty for the chain, kept for it in the shared state: whichever controller drives the chain pushes it.</summary>
    public void SetTarget(int channel, int duty) => _processState.SetChainTarget(MacText, duty);

    /// <summary>Release the chain so the keepalive stops asserting it (used by Reset); the receiver keeps what it last had.</summary>
    public void ReleaseChannel(int channel) => _processState.ReleaseChainTarget(MacText);

    /// <summary>The first fan's last reported speed, which stands for the control.</summary>
    public float GetRpm(int channel) => GetFanSpeed(0);

    /// <inheritdoc />
    public float GetFanSpeed(int index) {
        lock (_lock) {
            return index < _speeds.Count ? _speeds[index] : 0f;
        }
    }

    // ---------- worker-thread I/O (the only place the receiver is touched) ----------

    /// <summary>
    /// Send the chain its duty when it changed or is due its re-send, if a speed may go to it over
    /// USB at this moment (<see cref="IsDrivingChain"/>). L-Connect sends a speed only when the user
    /// changes something and relies on the receiver holding it; the plugin re-asserts it on the
    /// usual keepalive interval, the same command, so a receiver that reset or reconnected is driven
    /// again within it.
    /// </summary>
    public void ApplyPending() {
        if (_anotherReceiver) {
            return;
        }

        // After the handle was lost the device on the path is identified before anything is sent
        // to it (the status request is what reopens the path); a failed read throws to the worker's
        // catch and is tried again next tick.
        if (_transport.Generation != _identifiedGeneration && !TryReadStatus(out _)) {
            return;
        }

        // Registered work still owed after the reconnect (it reported failure then) is tried again
        // here, on the identified path, before the duty.
        _reconnectReplay.Apply();

        // The decision, from the shared state now: the radio's chain gets nothing over USB, and the
        // duty is the one the host last commanded for the chain, whichever controller it told.
        if (IsLeftToWireless) {
            return;
        }

        int target = _processState.ChainTarget(MacText);
        int lastWritten;
        DateTime lastWrite;
        int fanCount;
        bool hasControl;
        lock (_lock) {
            lastWritten = _lastWritten;
            lastWrite = _lastWriteUtc;
            fanCount = _fanCount;
            hasControl = _hasControl;
        }

        // A chain that has never reported a fan has no control, so nothing to send; once it has, a
        // count that drops to 0 still gets one slot, as L-Connect sends Math.Max(1, fanNum).
        if (!hasControl || !ChannelWriteDecision.ShouldWrite(target, lastWritten, lastWrite, _clock.UtcNow, ChannelWriteDecision.RefreshInterval)) {
            return;
        }

        byte[] command = FlexReceiverProtocol.EncodeSpeed(_family, target, fanCount);
        byte[] reply = Exchange(command);
        if (!FlexReceiverProtocol.IsSpeedAccepted(reply)) {
            throw new IOException(string.Format(
                CultureInfo.InvariantCulture,
                "{0} refused the speed write: reply {1:x2} {2:x2}",
                MacText,
                reply[0],
                reply[1]));
        }

        DateTime writtenAt = _clock.UtcNow;
        bool changed;
        bool afterReconnect;
        lock (_lock) {
            _lastWritten = target;
            _lastWriteUtc = writtenAt;
            changed = target != _loggedDuty;
            _loggedDuty = target;
            afterReconnect = _resendAfterReconnect;
            _resendAfterReconnect = false;
        }

        // The keepalive re-send is not logged: only a change is, with what was sent for it, and the
        // one re-send that follows a reconnect, since that is the receiver being driven again.
        if (changed) {
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "Set F{0}:{1} = {2}% (PWM {3} to {4} fan(s))",
                _index,
                MacText,
                target,
                command[1],
                Math.Max(1, fanCount)));
        } else if (afterReconnect) {
            _log.Write(string.Format(CultureInfo.InvariantCulture, "F{0}:{1} duty re-sent after the reconnect", _index, MacText));
        }
    }

    /// <summary>
    /// Read the status record, as L-Connect's receiver timer does every second: publish every fan's
    /// speed, add a reading for a fan reported for the first time while the chain is driven here,
    /// and notice the chain changing hands with the wireless controller.
    /// </summary>
    public void PollRpm() {
        if (_anotherReceiver || !TryReadStatus(out WirelessDeviceRecord status)) {
            return;
        }

        _lastStatus = status;
        lock (_lock) {
            _fanCount = status.FanCount;
        }

        bool bound = IsLeftToWireless;
        FlexReceiverChange? change = null;
        if (bound != _noticedBound) {
            _noticedBound = bound;
            if (bound) {
                _log.Write(string.Format(
                    CultureInfo.InvariantCulture,
                    "F{0}:{1} is now bound to the L-Wireless controller's master, which drives it; nothing more is sent over USB",
                    _index,
                    MacText));
                change = FlexReceiverChange.TakenByRadio;
            } else {
                // Back from the radio: whatever the receiver holds now is the radio's or the
                // motherboard's, so the duty goes out on the next tick whether or not it changed.
                lock (_lock) {
                    _lastWritten = -2;
                }

                _log.Write(string.Format(
                    CultureInfo.InvariantCulture,
                    "F{0}:{1} is no longer bound to the L-Wireless controller's master; driven over USB",
                    _index,
                    MacText));
                _ = EnsureSensors();
                change = FlexReceiverChange.ReleasedByRadio;
            }
        } else if (!bound && EnsureSensors()) {
            change = FlexReceiverChange.FanReported;
        }

        // After the sensors, so a reading added for a fan just reported carries this poll's value.
        Publish(status);
        if (change is FlexReceiverChange noticed) {
            Changed?.Invoke(this, noticed);
        }
    }

    public void Dispose() {
        _transport.Dispose();
    }

    private string ControlId => WirelessSensorIds.GroupControlId(MacText);

    private string FanId(int slot) => WirelessSensorIds.FanId(MacText, slot);

    private string FanName(int slot) => _label + " Fan " + (slot + 1).ToString(CultureInfo.InvariantCulture) + " RPM";

    // Every command is answered with one packet, which is read and returned so it is never left in
    // the pipe to be read as the next request's reply (SendAndRead reads after every write).
    private byte[] Exchange(byte[] command) {
        _transport.Write(command);
        return _transport.Read(FlexReceiverProtocol.PacketLength);
    }

    // The status record, refused unless it echoes the command: a reply that is another command's
    // is not a status. The address is checked by TryReadStatus, once there is one to check against.
    private WirelessDeviceRecord ReadStatus() {
        byte[] reply = Exchange(FlexReceiverProtocol.EncodeStatusRequest());
        WirelessDeviceRecord? status = FlexReceiverProtocol.DecodeStatus(reply);
        if (status is null) {
            throw new IOException(string.Format(
                CultureInfo.InvariantCulture, "the receiver answered the status request with {0:x2}, not its status", reply[0]));
        }

        return status;
    }

    // The status of the receiver on the path, which must name this address: false, with the
    // receiver given up on permanently, when it names another (two receivers swapped between ports while
    // the host slept, say), so nothing is sent to it again, its readings read 0 and the plugin is
    // told once. When the transport has reopened the device since it was last identified, the same
    // read identifies it again, and the registered replay and the duty follow. A read that throws
    // leaves everything as it was, to be tried again next tick.
    private bool TryReadStatus(out WirelessDeviceRecord status) {
        int generation = _transport.Generation;
        status = ReadStatus();
        if (!SameBytes(status.Mac, _mac)) {
            _anotherReceiver = true;
            lock (_lock) {
                for (int slot = 0; slot < _speeds.Count; slot++) {
                    _speeds[slot] = 0f;
                }
            }

            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "F{0}:{1}: the receiver on its path now answers as {2}; nothing more is sent on it, and a refresh is asked for to plan it again",
                _index,
                MacText,
                status.MacText));
            Changed?.Invoke(this, FlexReceiverChange.AnotherReceiverAnswered);
            return false;
        }

        if (generation != _identifiedGeneration) {
            _reconnectReplay.Owe();
            _reconnectReplay.Apply();
            lock (_lock) {
                _lastWritten = -2;
                _resendAfterReconnect = true;
            }

            Volatile.Write(ref _identifiedGeneration, generation);
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "F{0}:{1} reconnected and answers as the same receiver (transport generation {2})",
                _index,
                MacText,
                generation));
        }

        return true;
    }

    // Every registered reading, from the record. A decoded RPM is twelve bits, so no reading a
    // record can carry is beyond what a fan could produce.
    private void Publish(WirelessDeviceRecord status) {
        lock (_lock) {
            for (int slot = 0; slot < _speeds.Count; slot++) {
                _speeds[slot] = status.Rpm[slot];
            }
        }
    }

    // Give the chain the sensors its fan count now needs: the control once a fan is reported (a
    // chain without fans is never written, as NeedSyncPwm never writes a device without fans), and a
    // reading per reported fan. Only added to. Returns whether any was added.
    private bool EnsureSensors() {
        lock (_lock) {
            int before = (_hasControl ? 1 : 0) + _speeds.Count;
            if (_fanCount > 0) {
                _hasControl = true;
            }

            while (_speeds.Count < _fanCount) {
                _speeds.Add(0f);
            }

            return (_hasControl ? 1 : 0) + _speeds.Count != before;
        }
    }

    private static bool SameBytes(byte[] a, byte[] b) {
        for (int i = 0; i < a.Length; i++) {
            if (a[i] != b[i]) {
                return false;
            }
        }

        return true;
    }
}
