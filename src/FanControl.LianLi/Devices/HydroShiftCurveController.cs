using System;
using System.Globalization;
using FanControl.LianLi.Logging;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Transport;

namespace FanControl.LianLi.Devices;

/// <summary>
/// Coordinates the pump of one HydroShift II OLED Curve AIO (vendor 0x0416, pid 0x8051): one
/// channel, the pump, and the liquid temperature as an <see cref="ITemperatureSource"/>. Like
/// <see cref="Galahad2Controller"/> the FanControl-thread surface only mutates locked in-memory
/// state and every USB transfer happens on the worker-thread methods, over the WinUSB transport
/// the L-Wireless dongles use: each command is written and its one-packet reply read, as
/// L-Connect's <c>WinUsbHS2.SendAndReadLed</c> does. A status or tachometer reply is the data, so
/// one that does not come, or echoes another command, fails or is ignored; the reply to a set-pump
/// or sync command is one L-Connect discards, so one that does not come is logged and nothing
/// more. The pump is re-sent every two seconds (<see cref="ResendInterval"/>), the cadence of
/// L-Connect's own pump control timer, and the status poll re-asserts software control if the pump
/// reports it has gone back to the motherboard's PWM header. FanControl's duty spans 1600 to 2400
/// rpm, the range every ordinary L-Connect pump mode drives, through
/// <see cref="HydroShiftCurveProtocol.PumpRpmFromDuty"/>.
/// </summary>
internal sealed class HydroShiftCurveController : IFanDevice, ITemperatureSource {
    private const int PumpChannel = 0;
    private const int Channels = 1;
    private const int Temperatures = 1;

    // L-Connect's pump control timer sends the speed every two seconds (pumpSpeedControlInterval),
    // and nothing is known about how long the pump holds an output value with nothing driving it,
    // so the plugin keeps that cadence rather than the fifteen seconds the other families refresh at.
    internal static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(2);

    private readonly int _index;
    private readonly IDeviceTransport _transport;
    private readonly IClock _clock;
    private readonly ILog _log;

    private readonly object _lock = new object();
    private int _target = -1;                  // commanded duty %, -1 = unassigned
    private int _lastWritten = -2;             // last duty actually written
    private DateTime _lastWriteUtc = DateTime.MinValue;
    private float _rpm;                        // last measured RPM
    private bool _rpmImplausible;              // last read rejected as garbage
    private float? _temperature;               // last measured liquid temperature, null until read

    // Whether the last status reply said the pump follows the motherboard's PWM header, or null
    // before the first. Worker-thread only: it decides the once-per-onset re-assert and its log line.
    private bool? _followsMotherboard;

    // The transport generation this cooler was last set up under. A newer one means the transport
    // lost its handle to the device after that; a re-enumerated MCU may have been power-cycled and
    // handed the pump back to the motherboard header, so once the transport has reopened it,
    // software control is asserted again (after any registered replay) and the duty re-sent before
    // the next write. Worker-thread only; the replay is registered before the worker starts.
    private int _setUpGeneration;
    private readonly ReconnectReplay _reconnectReplay;

    // Which optional replies are missing, and which data replies echo another command, so each is
    // logged once per run rather than every second. Worker-thread only.
    private bool _setPumpReplyMissing;
    private bool _syncReplyMissing;
    private bool _statusForeign;
    private bool _speedForeign;

    public HydroShiftCurveController(int index, IDeviceTransport transport, IClock clock, ILog log) {
        _index = index;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _setUpGeneration = _transport.Generation;
        _reconnectReplay = new ReconnectReplay(_clock);
    }

    /// <summary>The cooler exposes one channel: the pump.</summary>
    public int ChannelCount => Channels;

    /// <summary>The pump is a physical part of the AIO, so its one channel is always populated.</summary>
    public bool IsChannelPopulated(int channel) => true;

    /// <summary>
    /// The sensor identity for the pump channel. The ids share the stable LianLi/{index}/ch{channel}
    /// scheme so a saved curve binding survives a restart.
    /// </summary>
    public ChannelDescriptor Describe(int channel) {
        return new ChannelDescriptor(
            $"LianLi/{_index}/ch{channel}/ctl",
            $"Lian Li HydroShift II OLED Curve #{_index + 1} Pump",
            $"LianLi/{_index}/ch{channel}/fan",
            $"Lian Li HydroShift II OLED Curve #{_index + 1} Pump RPM");
    }

    /// <summary>The cooler reports one temperature: the liquid's.</summary>
    public int TemperatureCount => Temperatures;

    /// <summary>
    /// The sensor identity for the liquid temperature, keyed on the controller's index like the
    /// pump and named as the wireless water blocks name theirs.
    /// </summary>
    public TemperatureDescriptor DescribeTemperature(int index) {
        return new TemperatureDescriptor(
            $"LianLi/{_index}/coolant/temp",
            $"Lian Li HydroShift II OLED Curve #{_index + 1} Coolant");
    }

    /// <inheritdoc />
    public void ReplayOnReconnect(Func<bool> replay) => _reconnectReplay.Register(replay);

    // ---------- FanControl-thread surface (no I/O) ----------

    /// <summary>Set the commanded duty for the pump. The worker pushes it to hardware.</summary>
    public void SetTarget(int channel, int duty) {
        lock (_lock) {
            _target = duty;
        }
    }

    /// <summary>Release the pump so the keepalive stops asserting it (used by Reset).</summary>
    public void ReleaseChannel(int channel) {
        lock (_lock) {
            _target = -1;
        }
    }

    /// <summary>Read the last measured pump RPM.</summary>
    public float GetRpm(int channel) {
        lock (_lock) {
            return _rpm;
        }
    }

    /// <inheritdoc />
    public float? GetTemperature(int index) {
        lock (_lock) {
            return _temperature;
        }
    }

    // ---------- worker-thread I/O (the only place the device is touched) ----------

    /// <summary>
    /// Take the pump off the motherboard's PWM header so the host owns its speed: the command
    /// L-Connect sends when its motherboard sync is switched off (<c>setMotherboardRPMSync(false)</c>).
    /// The composition root calls it once after construction, behind its own guard; the worker
    /// sends it again after a reconnect, and when the pump reports it follows the header.
    /// </summary>
    public void AssertSoftwareControl() {
        Send(HydroShiftCurveProtocol.EncodeMotherboardSync(sync: false), "software-control", ref _syncReplyMissing);
    }

    /// <summary>Push the pump target to the hardware when it changed or is due for its re-send.</summary>
    public void ApplyPending() {
        ReplaySetupIfReconnected();

        int target;
        int lastWritten;
        DateTime lastWrite;
        lock (_lock) {
            target = _target;
            lastWritten = _lastWritten;
            lastWrite = _lastWriteUtc;
        }

        if (!ChannelWriteDecision.ShouldWrite(target, lastWritten, lastWrite, _clock.UtcNow, ResendInterval)) {
            return;
        }

        bool changed = target != lastWritten;
        int rpm = HydroShiftCurveProtocol.PumpRpmFromDuty(target);
        int outputValue = HydroShiftCurveProtocol.OutputValueForRpm(rpm);
        Send(HydroShiftCurveProtocol.EncodeSetPump(outputValue), "set-pump", ref _setPumpReplyMissing);

        DateTime writtenAt = _clock.UtcNow;
        lock (_lock) {
            _lastWritten = target;
            _lastWriteUtc = writtenAt;
        }

        // The re-send every two seconds is not logged: only a change is, with what was sent for it.
        if (changed) {
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "Set H{0}:pump = {1}% ({2} rpm, output {3})",
                _index,
                target,
                rpm,
                outputValue));
        }
    }

    /// <summary>
    /// Read the status (the liquid temperature and the sync state) and the tachometer, as
    /// L-Connect's pump info timer does every second, into the caches. A reply that echoes another
    /// command is a stale one and is ignored, logged once per run, so the caches keep their last
    /// good values rather than take a tachometer's bytes for a temperature.
    /// </summary>
    public void PollRpm() {
        byte[] statusReply = Exchange(HydroShiftCurveProtocol.EncodeStatusRequest());
        byte[] speedReply = Exchange(HydroShiftCurveProtocol.EncodePumpSpeedRequest());
        HydroShiftCurveStatus? status = HydroShiftCurveProtocol.DecodeStatus(statusReply);
        int? rpm = HydroShiftCurveProtocol.DecodePumpSpeed(speedReply);
        RecordEcho("status", statusReply, status.HasValue, ref _statusForeign);
        RecordEcho("tachometer", speedReply, rpm.HasValue, ref _speedForeign);

        string? transition = null;
        lock (_lock) {
            if (status.HasValue) {
                _temperature = status.Value.LiquidTemperature;
            }

            if (rpm.HasValue) {
                transition = UpdateRpm(rpm.Value);
            }
        }

        if (transition != null) {
            _log.Write(transition);
        }

        if (status.HasValue) {
            RecordSyncState(status.Value.FollowsMotherboard);
        }
    }

    public void Dispose() {
        _transport.Dispose();
    }

    // Every command is answered with one packet, which is read and returned so it is never left
    // in the pipe to be read as the next request's reply (SendAndReadLed reads after every write).
    private byte[] Exchange(byte[] command) {
        _transport.Write(command);
        return _transport.Read(HydroShiftCurveProtocol.ReplyLength);
    }

    // A command whose reply L-Connect discards (the set-pump and sync commands: SendAndReadLed reads
    // it into a buffer nobody looks at, and its Read hands back zeros for one that never comes). The
    // reply is still read, to keep it out of the next exchange, but one that does not come within
    // the pipe timeout is logged once per run and does not fail the write, since the command went
    // out and whether this MCU answers these two at all is not known. A pipe that fails still throws.
    private void Send(byte[] command, string what, ref bool missing) {
        _transport.Write(command);
        try {
            _transport.Read(HydroShiftCurveProtocol.ReplyLength);
            missing = false;
        } catch (DeviceReplyMissingException ex) {
            if (missing) {
                return;
            }

            missing = true;
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "H{0}: the pump did not answer the {1} command ({2}); the command was sent, and L-Connect does not wait for that reply either (logged once until one comes)",
                _index,
                what,
                ex.Message));
        }
    }

    // A data reply that echoes another command, logged once per run of them and once when they end.
    private void RecordEcho(string what, byte[] reply, bool echoed, ref bool foreign) {
        if (echoed) {
            if (foreign) {
                _log.Write(string.Format(CultureInfo.InvariantCulture, "H{0}: the {1} reply echoes its command again", _index, what));
            }

            foreign = false;
            return;
        }

        if (foreign) {
            return;
        }

        foreign = true;
        _log.Write(string.Format(
            CultureInfo.InvariantCulture,
            "H{0}: the {1} reply echoes another command (byte 0 = 0x{2:X2}); ignored, keeping the last reading (logged once until it echoes its own)",
            _index,
            what,
            reply[0]));
    }

    // A reopened transport means the MCU was re-enumerated and may have come back reset. Nothing is
    // replayed while the handle is still faulted: the MCU is off the bus, and the next poll's
    // request is what reopens it, on the backoff. Once it is back, replay the registered work (if
    // any), assert software control again and forget the last-written duty so the loop that follows
    // re-sends the pump now. A throw leaves the generation unrecorded, so the replay is retried on
    // the next tick; registered work that reports failure stays owed and is tried again on its own.
    private void ReplaySetupIfReconnected() {
        if (_transport.IsFaulted) {
            return;
        }

        int generation = _transport.Generation;
        if (generation == _setUpGeneration) {
            _reconnectReplay.Apply();
            return;
        }

        _reconnectReplay.Owe();
        _reconnectReplay.Apply();
        AssertSoftwareControl();
        lock (_lock) {
            _lastWritten = -2;
        }

        _setUpGeneration = generation;
        _log.Write(string.Format(
            CultureInfo.InvariantCulture,
            "H{0} reconnected: setup replayed (transport generation {1})",
            _index,
            generation));
    }

    // L-Connect never acts on the state byte, only reads it. The plugin re-asserts software control
    // once per onset - the pump reporting it follows the header after it did not, or from the first
    // reply - and logs the onset and the recovery once each, so a pump that keeps reverting is
    // visible without a line every second.
    private void RecordSyncState(bool followsMotherboard) {
        bool? previous = _followsMotherboard;
        if (followsMotherboard == previous) {
            return;
        }

        if (followsMotherboard) {
            // A throw leaves the state unrecorded, so the re-assert is tried again next poll.
            AssertSoftwareControl();
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "H{0} reports the pump following the motherboard PWM header; software control re-asserted",
                _index));
        } else if (previous == true) {
            _log.Write(string.Format(CultureInfo.InvariantCulture, "H{0} reports the pump back under software control", _index));
        }

        _followsMotherboard = followsMotherboard;
    }

    // Caller holds _lock. Mirrors Galahad2Controller: cache a plausible reading, keep the last good
    // value on a garbage read, and return the onset/recovery transition to log once, or null.
    private string? UpdateRpm(int rpm) {
        if (ChannelReadDecision.IsPlausible(rpm)) {
            _rpm = rpm;
            if (!_rpmImplausible) {
                return null;
            }

            _rpmImplausible = false;
            return string.Format(CultureInfo.InvariantCulture, "H{0}:pump rpm recovered ({1})", _index, rpm);
        }

        if (_rpmImplausible) {
            return null;
        }

        _rpmImplausible = true;
        return string.Format(
            CultureInfo.InvariantCulture, "H{0}:pump implausible rpm {1} ignored, keeping {2}", _index, rpm, _rpm);
    }
}
