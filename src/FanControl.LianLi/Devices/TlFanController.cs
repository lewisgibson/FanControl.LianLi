using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FanControl.LianLi.Logging;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Transport;

namespace FanControl.LianLi.Devices;

/// <summary>
/// Coordinates one Uni Fan TL hub (vendor 0x0416). Unlike the fixed four-channel Uni controllers,
/// the TL exposes a variable set of fans addressed by (port, fan-index); the set is discovered from
/// the construction handshake, each detected fan becoming a channel, and a fan that first answers a
/// later RPM poll (one that was slow to come back after a wake) is added once three polls in a row
/// report it, after the others. Like the other 0x0416
/// devices it writes a command packet and reads the reply: a keepalive speed write per fan, and an
/// RPM poll that writes a handshake, reads to the reply that answers it, and matches that reply back
/// to the fans; a poll the hub does not answer leaves every reading as it was. The FanControl-thread
/// methods only mutate locked state; all USB I/O is on the worker-thread methods.
/// </summary>
internal sealed class TlFanController : IFanDevice {
    // The 0x0416 handshake reply is one 64-byte command-packet frame.
    private const int HandshakeReplyLength = 64;

    // The transport empties the input queue before the handshake is written, so what a poll reads past
    // is only what arrives after that: the answer to a write made a moment earlier, or to another
    // program's command. Up to this many per ask; each read returns at once while reports keep coming,
    // and the transport faults the handle when one does not come.
    private const int MaxReportsPerHandshake = 32;

    // L-Connect's getHandshakeInfo asks once more when the first answer is not a handshake.
    private const int HandshakeAttempts = 2;

    // The longest a poll keeps reading for the handshake's answer: L-Connect's own worst case. Its
    // getHandshakeInfo reads the answer with TLFanDevice's 100 ms timeout, then discards up to five
    // packets at 50 ms each (discardInputPacket) and reads the second ask's answer for 100 ms more:
    // 100 + 5 x 50 + 100 = 450 ms.
    private static readonly TimeSpan HandshakeTimeLimit = TimeSpan.FromMilliseconds(450);

    // L-Connect's TLFanController.updateFanGroupStatus acts on a change in the fans a hub reports only
    // once three valid handshakes in a row differ from the last it accepted (MaxHandshakeInfoChangedCount),
    // so one garbled reply after a wake cannot add a fan that is not there. The plugin is stricter: a
    // new fan is added once three polls in a row report that same fan, and a poll without it starts again.
    private const int LateFanConfirmations = 3;

    private readonly int _index;
    private readonly IDeviceTransport _transport;
    private readonly IClock _clock;
    private readonly ILog _log;

    // One per fan, in the order found: the construction handshake's by (port, fan-index), then any
    // fan a later poll finds. A channel keeps its address from then on, and the sensor id is keyed on
    // the address, not the channel ordinal, so a fan found late re-keys no other fan. Only the worker adds
    // one; every read from another thread is under _lock.
    private readonly List<Channel> _channels = new List<Channel>();
    private readonly Dictionary<int, int> _channelByAddress = new Dictionary<int, int>();

    private readonly object _lock = new object();

    // How many polls in a row have reported each address that is not a channel yet. Worker only.
    private readonly Dictionary<int, int> _unconfirmed = new Dictionary<int, int>();

    // Whether the last handshake went unanswered, so the log says it once, and whether any has been
    // answered, so the line when one is says which. Set at construction, then worker only.
    private bool _handshakeMissing;
    private bool _handshakeAnswered;

    // The transport generation this hub last took software control under. A newer one means the
    // transport lost its handle to the device after that, and a re-enumerated hub may have been
    // power-cycled and reverted to motherboard sync with its saved look lost, so once the transport
    // has reopened it, both are replayed and every duty re-sent before the next write.
    // Worker-thread only; the replay is registered before the worker starts.
    private int _setUpGeneration;
    private readonly ReconnectReplay _reconnectReplay;

    public TlFanController(int index, IDeviceTransport transport, IClock clock, ILog log) {
        _index = index;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _reconnectReplay = new ReconnectReplay(_clock);

        // Discover the fans once. Order by (port, fan-index) so the channel order - and therefore the
        // sensor ids below - is deterministic regardless of the reply's record order.
        // A hub that does not answer starts with no fans, and the late-fan path adds them once it does.
        byte[]? reply = Handshake();
        TlFanReading[] detected = reply is null
            ? Array.Empty<TlFanReading>()
            : TlFanProtocol.DecodeHandshake(reply)
                .OrderBy(r => r.Port)
                .ThenBy(r => r.FanIndex)
                .ToArray();

        // Each fan takes the reading it was discovered with, so a hub whose later handshakes go
        // unanswered still reports what it measured rather than nothing.
        List<string>? transitions = null;
        foreach (TlFanReading fan in detected) {
            int channel = AddChannel(fan.Port, fan.FanIndex);
            lock (_lock) {
                UpdateRpm(channel, fan.Rpm, ref transitions);
            }
        }

        if (transitions != null) {
            foreach (string line in transitions) {
                _log.Write(line);
            }
        }

        // Take software control of each fan once. L-Connect sets motherboard-RPM-sync separately
        // from the speed writes, so this is asserted here instead of before every speed write.
        TakeSoftwareControl();
        _setUpGeneration = _transport.Generation;
    }

    /// <summary>
    /// Raised on the worker thread when a poll finds a fan the hub did not report before, so its
    /// sensors are new to the host. The plugin answers it by remembering the fan and asking the host
    /// to refresh (FanControl's <c>IPlugin3.RefreshRequested</c>).
    /// </summary>
    public event EventHandler? TopologyChanged;

    /// <summary>How many fans the hub has reported: at construction, and since.</summary>
    public int ChannelCount {
        get {
            lock (_lock) {
                return _channels.Count;
            }
        }
    }

    /// <summary>
    /// Every TL channel is a fan the hub reported in its discovery handshake, so all are populated
    /// by construction - there are no empty slots to hide.
    /// </summary>
    public bool IsChannelPopulated(int channel) => true;

    /// <summary>
    /// The sensor identity for a TL fan. The id is keyed on the fan's (port, fan-index) address, so
    /// adding or removing one fan does not re-key the others' saved curve bindings.
    /// </summary>
    public ChannelDescriptor Describe(int channel) {
        Channel described;
        lock (_lock) {
            described = _channels[channel];
        }

        int port = described.Port;
        int fan = described.Fan;
        return new ChannelDescriptor(
            $"LianLi/{_index}/p{port}f{fan}/ctl",
            $"Lian Li Uni TL #{_index + 1} Port {port + 1} Fan {fan + 1}",
            $"LianLi/{_index}/p{port}f{fan}/fan",
            $"Lian Li Uni TL #{_index + 1} Port {port + 1} Fan {fan + 1} RPM");
    }

    /// <inheritdoc />
    public void ReplayOnReconnect(Func<bool> replay) => _reconnectReplay.Register(replay);

    // ---------- FanControl-thread methods (no I/O) ----------

    /// <summary>Set the commanded duty for a channel. The worker pushes it to hardware.</summary>
    public void SetTarget(int channel, int duty) {
        lock (_lock) {
            _channels[channel].Target = duty;
        }
    }

    /// <summary>Release a channel so the keepalive stops asserting it (used by Reset).</summary>
    public void ReleaseChannel(int channel) {
        lock (_lock) {
            _channels[channel].Target = -1;
        }
    }

    /// <summary>Read the last measured RPM for a channel, or null until a plausible one is read (a fan whose discovery reading was garbage).</summary>
    public float? GetRpm(int channel) {
        lock (_lock) {
            return _channels[channel].Rpm;
        }
    }

    // ---------- worker-thread I/O (the only place HID is touched) ----------

    /// <summary>Push any changed-or-stale channel targets to the hardware.</summary>
    public void ApplyPending() {
        ReplaySetupIfReconnected();

        for (int ch = 0; ch < ChannelCount; ch++) {
            Channel channel;
            int target;
            int lastWritten;
            DateTime lastWrite;
            lock (_lock) {
                channel = _channels[ch];
                target = channel.Target;
                lastWritten = channel.LastWritten;
                lastWrite = channel.LastWriteUtc;
            }

            if (!ChannelWriteDecision.ShouldWrite(
                    target, lastWritten, lastWrite, _clock.UtcNow, ChannelWriteDecision.RefreshInterval)) {
                continue;
            }

            _transport.Write(TlFanProtocol.EncodeSetFanSpeed(channel.Port, channel.Fan, target));

            DateTime writtenAt = _clock.UtcNow;
            lock (_lock) {
                channel.LastWritten = target;
                channel.LastWriteUtc = writtenAt;
            }

            // Only the first speed after the controller is set up (at start or after a reconnect) is
            // logged, to show it is being driven. Each later change and the 15-second keepalive
            // re-send are not: a curve moves the speed hundreds of times a day.
            if (lastWritten < 0) {
                _log.Write(string.Format(
                    CultureInfo.InvariantCulture,
                    "Set T{0}:{1}/{2} = {3}%",
                    _index,
                    channel.Port,
                    channel.Fan,
                    target));
            }
        }
    }

    /// <summary>Write a handshake and match the reply's RPM records back to the fan channels.</summary>
    public void PollRpm() {
        // A poll the hub did not answer says nothing about which fans exist, so it neither updates a
        // reading nor restarts a late fan's count (L-Connect's updateFanGroupStatus ignores an invalid
        // handshake the same way).
        if (Handshake() is not byte[] reply) {
            return;
        }

        IReadOnlyList<TlFanReading> readings = TlFanProtocol.DecodeHandshake(reply);

        List<string>? transitions = null;
        var unknown = new List<TlFanReading>();
        lock (_lock) {
            foreach (TlFanReading reading in readings) {
                if (_channelByAddress.TryGetValue(Address(reading.Port, reading.FanIndex), out int channel)) {
                    UpdateRpm(channel, reading.Rpm, ref transitions);
                } else {
                    unknown.Add(reading);
                }
            }
        }

        var found = new List<TlFanReading>();
        var sightings = new Dictionary<int, int>();
        foreach (TlFanReading reading in unknown) {
            int address = Address(reading.Port, reading.FanIndex);
            int seen = (_unconfirmed.TryGetValue(address, out int before) ? before : 0) + 1;
            if (seen >= LateFanConfirmations) {
                found.Add(reading);
            } else {
                sightings[address] = seen;
            }
        }

        _unconfirmed.Clear();
        foreach (KeyValuePair<int, int> sighting in sightings) {
            _unconfirmed[sighting.Key] = sighting.Value;
        }

        // A fan the hub did not report at construction - one that came back after the rest - becomes
        // a channel once confirmed, under software control like the others, and takes its reading.
        // A fan added before a later write throws is reported all the same: the next poll finds it
        // already a channel and would never report it.
        int added = 0;
        try {
            foreach (TlFanReading fan in found.OrderBy(r => r.Port).ThenBy(r => r.FanIndex)) {
                _transport.Write(TlFanProtocol.EncodeMotherboardSync(fan.Port, fan.FanIndex, sync: false));
                int channel = AddChannel(fan.Port, fan.FanIndex);
                added++;
                lock (_lock) {
                    UpdateRpm(channel, fan.Rpm, ref transitions);
                }

                _log.Write(string.Format(CultureInfo.InvariantCulture, "T{0}:{1}/{2} answered after the others; added", _index, fan.Port, fan.FanIndex));
            }
        } finally {
            if (added > 0) {
                TopologyChanged?.Invoke(this, EventArgs.Empty);
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
    // write; ask once more, as L-Connect does, when none comes. Gives up once HandshakeTimeLimit has
    // passed as well as after MaxReportsPerHandshake reports an ask, so a stream of other reports
    // cannot hold the tick. Null when the hub answered neither ask.
    private byte[]? Handshake() {
        DateTime started = _clock.UtcNow;
        int asks = 0;
        int reads = 0;
        byte lastCommand = 0;
        for (int attempt = 0; attempt < HandshakeAttempts && !HandshakeTimedOut(attempt, started); attempt++) {
            _transport.Write(TlFanProtocol.EncodeHandshakeRequest());
            asks++;
            for (int read = 0; read < MaxReportsPerHandshake && !HandshakeTimedOut(read, started); read++) {
                byte[] reply = _transport.Read(HandshakeReplyLength);
                reads++;
                if (TlFanProtocol.IsHandshakeReply(reply)) {
                    RecordHandshake(answered: true, asks, reads, lastCommand);
                    return reply;
                }

                lastCommand = CommandPacket.CommandOf(reply);
            }
        }

        RecordHandshake(answered: false, asks, reads, lastCommand);
        return null;
    }

    // The first ask and the first read of each ask always go ahead, so a slow write never costs the
    // poll its answer; every later step waits on the limit. A clock that stepped back counts as within
    // it, and the report count still bounds the poll.
    private bool HandshakeTimedOut(int step, DateTime started)
        => step > 0 && ClockSpan.IsOlderThan(_clock.UtcNow, started, HandshakeTimeLimit);

    // A hub that stops answering the handshake tends to stay that way for a while, so the log says so
    // on the change only: once when it stops, with how far the poll got and the command the last
    // report read echoed, and once when it answers - again, or for the first time when it never
    // answered at construction.
    private void RecordHandshake(bool answered, int asks, int reads, byte lastCommand) {
        if (answered) {
            bool answeredBefore = _handshakeAnswered;
            _handshakeAnswered = true;
            if (_handshakeMissing) {
                _handshakeMissing = false;
                _log.Write(string.Format(
                    CultureInfo.InvariantCulture,
                    answeredBefore ? "T{0}: the hub answers the handshake again" : "T{0}: the hub answers the handshake for the first time",
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
            "T{0}: the hub did not answer the handshake ({1} ask(s), {2} report(s) read, the last echoing command 0x{3:X2}); fans and RPM left as they were (logged once until it does)",
            _index,
            asks,
            reads,
            lastCommand));
    }

    private void TakeSoftwareControl() {
        for (int ch = 0; ch < ChannelCount; ch++) {
            Channel channel;
            lock (_lock) {
                channel = _channels[ch];
            }

            _transport.Write(TlFanProtocol.EncodeMotherboardSync(channel.Port, channel.Fan, sync: false));
        }
    }

    private int AddChannel(int port, int fan) {
        lock (_lock) {
            _channels.Add(new Channel(port, fan));
            _channelByAddress[Address(port, fan)] = _channels.Count - 1;
            return _channels.Count - 1;
        }
    }

    // A reopened transport means the hub was re-enumerated and may have come back reset. Nothing is
    // replayed while the handle is still faulted: the hub is off the bus, and the next poll's
    // handshake is what reopens it, on the backoff. Once it is back, redo what construction and
    // Initialize did for it, in the same order - software control, then the saved look - and forget
    // every last-written duty so the loop that follows re-sends each fan now. A throw leaves the
    // generation unrecorded, so the replay is retried on the next tick; a look that reports failure
    // stays owed and is tried again on its own.
    private void ReplaySetupIfReconnected() {
        if (_transport.IsFaulted) {
            return;
        }

        int generation = _transport.Generation;
        if (generation == _setUpGeneration) {
            _reconnectReplay.Apply();
            return;
        }

        TakeSoftwareControl();
        _reconnectReplay.Owe();
        _reconnectReplay.Apply();
        lock (_lock) {
            foreach (Channel channel in _channels) {
                channel.LastWritten = -2;
            }
        }

        _setUpGeneration = generation;
        _log.Write(string.Format(
            CultureInfo.InvariantCulture,
            "T{0} reconnected: setup replayed (transport generation {1})",
            _index,
            generation));
    }

    private static int Address(int port, int fanIndex) => ((port & 0x0F) << 4) | (fanIndex & 0x0F);

    // Caller holds _lock. Mirrors FanController: cache plausible readings, keep the last good value
    // on a garbage read, and log each onset/recovery transition once.
    private void UpdateRpm(int index, int rpm, ref List<string>? transitions) {
        Channel channel = _channels[index];
        if (ChannelReadDecision.IsPlausible(rpm)) {
            channel.Rpm = rpm;
            if (channel.RpmImplausible) {
                channel.RpmImplausible = false;
                (transitions ??= new List<string>()).Add(string.Format(
                    CultureInfo.InvariantCulture, "T{0}:{1}/{2} rpm recovered ({3})", _index, channel.Port, channel.Fan, rpm));
            }
        } else if (!channel.RpmImplausible) {
            channel.RpmImplausible = true;
            (transitions ??= new List<string>()).Add(string.Format(
                CultureInfo.InvariantCulture, "T{0}:{1}/{2} implausible rpm {3} ignored, keeping {4}", _index, channel.Port, channel.Fan, rpm, ChannelReadDecision.Describe(channel.Rpm)));
        }
    }

    // One fan: its address, fixed, and its state, guarded by the controller's _lock.
    private sealed class Channel {
        public Channel(int port, int fan) {
            Port = port;
            Fan = fan;
        }

        public int Port { get; }

        public int Fan { get; }

        public int Target { get; set; } = -1;          // commanded duty %, -1 = unassigned

        public int LastWritten { get; set; } = -2;     // last duty written

        public DateTime LastWriteUtc { get; set; } = DateTime.MinValue;

        public float? Rpm { get; set; }                // last measured RPM, null until one is read

        public bool RpmImplausible { get; set; }       // last read rejected as garbage
    }
}
