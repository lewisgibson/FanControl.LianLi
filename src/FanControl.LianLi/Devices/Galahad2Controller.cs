using System;
using System.Collections.Generic;
using System.Globalization;
using FanControl.LianLi.Logging;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Transport;

namespace FanControl.LianLi.Devices;

/// <summary>
/// Coordinates one Galahad II Trinity AIO (vendor 0x0416): channel 0 is the fan, channel 1 the
/// pump. Like <see cref="FanController"/> the FanControl-thread methods only mutate locked
/// in-memory state and every USB transfer happens on the worker-thread methods, but the 0x0416
/// family writes a command packet and reads its reply instead of pulling an input report: a
/// keepalive write per channel, and an RPM poll that writes a handshake and reads to the answer. The
/// pump duty is floored in <see cref="Galahad2Protocol"/> so a curve can never stop the pump. On a
/// Galahad II Vision LCD the same answer carries the coolant temperature, published as an
/// <see cref="ITemperatureSource"/>; every other cooler of the family reports no temperature.
/// </summary>
internal sealed class Galahad2Controller : IFanDevice, ITemperatureSource {
    private const int FanChannel = 0;
    private const int PumpChannel = 1;
    private const int Channels = 2;
    private const int CoolantTemperatures = 1;

    // The 0x0416 handshake reply is one 64-byte command-packet frame.
    private const int HandshakeReplyLength = 64;

    // The transport empties the input queue before the handshake is written, so a poll reads past
    // only what arrives after that and before the cooler's answer: another program's reply, or a packet
    // the Vision and HydroShift LCD send unasked (L-Connect's own reader for those takes every packet
    // and keeps the ones that echo the handshake). Up to this many; L-Connect asks once a second and
    // no more, so there is no second ask.
    private const int MaxReportsPerHandshake = 32;

    // The longest a poll keeps reading for the handshake's answer. L-Connect waits 200 ms for the
    // Trinity's one answer (Galahad2TrinityDevice.ReadTimeout) and reads the Vision and HydroShift LCD
    // in 20 ms slices (ReadACommandPacket(20)) on a reader of its own, but on every one of them it asks
    // again each second, so an answer later than a second is overtaken by the next ask there too.
    private static readonly TimeSpan HandshakeTimeLimit = TimeSpan.FromSeconds(1);

    private readonly int _index;
    private readonly IDeviceTransport _transport;
    private readonly bool _reportsCoolant;     // a Vision LCD, whose handshake reply carries the coolant
    private readonly IClock _clock;
    private readonly ILog _log;

    private readonly object _lock = new object();
    private readonly int[] _target = { -1, -1 };          // commanded duty %, -1 = unassigned
    private readonly int[] _lastWritten = { -2, -2 };     // last duty written
    private readonly DateTime[] _lastWriteUtc = { DateTime.MinValue, DateTime.MinValue };
    private readonly float?[] _rpm = new float?[2];       // last measured RPM, null until one is read
    private readonly bool[] _rpmImplausible = { false, false }; // last read rejected as garbage
    private float? _coolant;                              // last coolant temperature, null when not measured

    // The transport generation this cooler was last set up under. A newer one means the transport
    // lost its handle to the device after that; the cooler needs no setup writes of its own, but a
    // re-enumerated one may have been power-cycled and lost its saved look, so once the transport
    // has reopened it, that is replayed and both duties re-sent before the next write.
    // Worker-thread only; the replay is registered before the worker starts.
    private int _setUpGeneration;
    private readonly ReconnectReplay _reconnectReplay;

    // Whether the last handshake went unanswered, so the log says it once, and whether any has been
    // answered, so the line when one is says which. Worker only.
    private bool _handshakeMissing;
    private bool _handshakeAnswered;

    public Galahad2Controller(int index, IDeviceTransport transport, int productId, IClock clock, ILog log) {
        _index = index;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _reportsCoolant = Galahad2Protocol.ReportsCoolant(productId);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _setUpGeneration = _transport.Generation;
        _reconnectReplay = new ReconnectReplay(_clock);
    }

    /// <summary>The Galahad exposes two channels: the fan and the pump.</summary>
    public int ChannelCount => Channels;

    /// <summary>
    /// The Galahad's two channels - fan and pump - are both physical parts of the AIO, so both are
    /// always populated; there is nothing to hide.
    /// </summary>
    public bool IsChannelPopulated(int channel) => true;

    /// <summary>
    /// The sensor identity for a Galahad channel - fan (0) or pump (1). The ids share the stable
    /// LianLi/{index}/ch{channel} scheme so a saved curve binding survives a restart.
    /// </summary>
    public ChannelDescriptor Describe(int channel) {
        string role = channel == PumpChannel ? "Pump" : "Fan";
        return new ChannelDescriptor(
            $"LianLi/{_index}/ch{channel}/ctl",
            $"Lian Li Galahad #{_index + 1} {role}",
            $"LianLi/{_index}/ch{channel}/fan",
            $"Lian Li Galahad #{_index + 1} {role} RPM");
    }

    /// <summary>
    /// A Galahad II Vision LCD reports one temperature, the coolant's; the Trinity and the HydroShift
    /// LCD report none, so no temperature sensor is registered for them.
    /// </summary>
    public int TemperatureCount => _reportsCoolant ? CoolantTemperatures : 0;

    /// <summary>
    /// The sensor identity for the coolant temperature, keyed on the controller's index like the
    /// fan and pump and named as the HydroShift II OLED Curve names its own.
    /// </summary>
    public TemperatureDescriptor DescribeTemperature(int index) {
        return new TemperatureDescriptor(
            $"LianLi/{_index}/coolant/temp",
            $"Lian Li Galahad #{_index + 1} Coolant");
    }

    /// <inheritdoc />
    public void ReplayOnReconnect(Func<bool> replay) => _reconnectReplay.Register(replay);

    // ---------- FanControl-thread methods (no I/O) ----------

    /// <summary>Set the commanded duty for a channel. The worker pushes it to hardware.</summary>
    public void SetTarget(int channel, int duty) {
        lock (_lock) {
            _target[channel] = duty;
        }
    }

    /// <summary>Release a channel so the keepalive stops asserting it (used by Reset).</summary>
    public void ReleaseChannel(int channel) {
        lock (_lock) {
            _target[channel] = -1;
        }
    }

    /// <summary>Read the last measured RPM for a channel, or null until one is read.</summary>
    public float? GetRpm(int channel) {
        lock (_lock) {
            return _rpm[channel];
        }
    }

    /// <inheritdoc />
    public float? GetTemperature(int index) {
        lock (_lock) {
            return _coolant;
        }
    }

    // ---------- worker-thread I/O (the only place HID is touched) ----------

    /// <summary>Push any changed-or-stale channel targets to the hardware.</summary>
    public void ApplyPending() {
        ReplaySetupIfReconnected();

        for (int ch = 0; ch < Channels; ch++) {
            int target;
            int lastWritten;
            DateTime lastWrite;
            lock (_lock) {
                target = _target[ch];
                lastWritten = _lastWritten[ch];
                lastWrite = _lastWriteUtc[ch];
            }

            if (!ChannelWriteDecision.ShouldWrite(
                    target, lastWritten, lastWrite, _clock.UtcNow, ChannelWriteDecision.RefreshInterval)) {
                continue;
            }

            WriteDuty(ch, target);

            DateTime writtenAt = _clock.UtcNow;
            lock (_lock) {
                _lastWritten[ch] = target;
                _lastWriteUtc[ch] = writtenAt;
            }

            // Only the first speed after the controller is set up (at start or after a reconnect) is
            // logged, to show it is being driven. Each later change and the 15-second keepalive
            // re-send are not: a curve moves the speed hundreds of times a day.
            if (lastWritten < 0) {
                _log.Write(string.Format(
                    CultureInfo.InvariantCulture,
                    "Set G{0}:{1} = {2}%",
                    _index,
                    ch == PumpChannel ? "pump" : "fan",
                    target));
            }
        }
    }

    /// <inheritdoc />
    public void ForgetReadings() {
        lock (_lock) {
            for (int ch = 0; ch < Channels; ch++) {
                _rpm[ch] = null;
            }

            _coolant = null;
        }
    }

    /// <summary>
    /// Write a handshake and decode the reply into the fan and pump RPM cache and, on a Vision LCD,
    /// the coolant temperature. A reply that answers without a coolant reading (its valid flag clear)
    /// leaves the coolant not measured rather than at its last value.
    /// </summary>
    public void PollRpm() {
        // Without an answer both readings stay as they were, as L-Connect leaves them.
        if (Handshake() is not byte[] reply) {
            return;
        }

        Galahad2Reading reading = Galahad2Protocol.DecodeHandshake(reply);

        List<string>? transitions = null;
        lock (_lock) {
            UpdateRpm(FanChannel, reading.FanRpm, ref transitions);
            UpdateRpm(PumpChannel, reading.PumpRpm, ref transitions);
            if (_reportsCoolant) {
                _coolant = reading.CoolantTemperature;
            }
        }

        if (transitions != null) {
            foreach (string line in transitions) {
                _log.Write(line);
            }
        }
    }

    public void Dispose() {
        _transport.Dispose();
    }

    // Write a handshake and read to the reply that answers it, past whatever else arrives after the
    // write. Gives up once HandshakeTimeLimit has passed as well as after MaxReportsPerHandshake
    // reports, so a stream of other reports cannot hold the tick. Null when none answers it.
    private byte[]? Handshake() {
        DateTime started = _clock.UtcNow;
        _transport.Write(Galahad2Protocol.EncodeHandshakeRequest());
        int reads = 0;
        byte lastCommand = 0;
        for (int read = 0; read < MaxReportsPerHandshake && !HandshakeTimedOut(read, started); read++) {
            byte[] reply = _transport.Read(HandshakeReplyLength);
            reads++;
            if (Galahad2Protocol.IsHandshakeReply(reply)) {
                RecordHandshake(answered: true, reads, lastCommand);
                return reply;
            }

            lastCommand = CommandPacket.CommandOf(reply);
        }

        RecordHandshake(answered: false, reads, lastCommand);
        return null;
    }

    // The first read always goes ahead, so a slow write never costs the poll its answer; every later
    // read waits on the limit. A clock that stepped back counts as within it, and the report count
    // still bounds the poll.
    private bool HandshakeTimedOut(int read, DateTime started)
        => read > 0 && ClockSpan.IsOlderThan(_clock.UtcNow, started, HandshakeTimeLimit);

    // A cooler that stops answering the handshake tends to stay that way for a while, so the log says
    // so on the change only: once when it stops, with how far the poll got and the command the last
    // report read echoed, and once when it answers - again, or for the first time when it never had.
    private void RecordHandshake(bool answered, int reads, byte lastCommand) {
        if (answered) {
            bool answeredBefore = _handshakeAnswered;
            _handshakeAnswered = true;
            if (_handshakeMissing) {
                _handshakeMissing = false;
                _log.Write(string.Format(
                    CultureInfo.InvariantCulture,
                    answeredBefore ? "G{0}: the cooler answers the handshake again" : "G{0}: the cooler answers the handshake for the first time",
                    _index));
            }

            return;
        }

        if (_handshakeMissing) {
            return;
        }

        _handshakeMissing = true;
        _log.Write(string.Format(
            CultureInfo.InvariantCulture,
            "G{0}: the cooler did not answer the handshake ({1} report(s) read, the last echoing command 0x{2:X2}); RPM left as it was (logged once until it does)",
            _index,
            reads,
            lastCommand));
    }

    // A reopened transport means the cooler was re-enumerated and may have come back reset. Nothing
    // is replayed while the handle is still faulted: the cooler is off the bus, and the next poll's
    // handshake is what reopens it, on the backoff. Once it is back, replay the saved look (if the
    // Lighting build registered one) and forget every last-written duty so the loop that follows
    // re-sends the fan and pump now. A throw leaves the generation unrecorded, so the replay is
    // retried on the next tick; a look that reports failure stays owed and is tried again on its own.
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
        lock (_lock) {
            for (int ch = 0; ch < Channels; ch++) {
                _lastWritten[ch] = -2;
            }
        }

        _setUpGeneration = generation;
        _log.Write(string.Format(
            CultureInfo.InvariantCulture,
            "G{0} reconnected: setup replayed (transport generation {1})",
            _index,
            generation));
    }

    private void WriteDuty(int channel, int duty) {
        // Fan and pump take distinct commands; the pump duty is floored inside the encoder so a
        // FanControl curve can never command a stopped pump on an AIO.
        byte[] command = channel == PumpChannel
            ? Galahad2Protocol.EncodeSetPump(motherboardSync: false, dutyPercent: duty)
            : Galahad2Protocol.EncodeSetFan(motherboardSync: false, dutyPercent: duty);
        _transport.Write(command);
    }

    // Caller holds _lock. Mirrors FanController: cache plausible readings, keep the last good value
    // on a garbage read, and log each onset/recovery transition once.
    private void UpdateRpm(int channel, int rpm, ref List<string>? transitions) {
        if (ChannelReadDecision.IsPlausible(rpm)) {
            _rpm[channel] = rpm;
            if (_rpmImplausible[channel]) {
                _rpmImplausible[channel] = false;
                (transitions ??= new List<string>()).Add(string.Format(
                    CultureInfo.InvariantCulture, "G{0}:{1} rpm recovered ({2})", _index, channel, rpm));
            }
        } else if (!_rpmImplausible[channel]) {
            _rpmImplausible[channel] = true;
            (transitions ??= new List<string>()).Add(string.Format(
                CultureInfo.InvariantCulture, "G{0}:{1} implausible rpm {2} ignored, keeping {3}", _index, channel, rpm, ChannelReadDecision.Describe(_rpm[channel])));
        }
    }
}
