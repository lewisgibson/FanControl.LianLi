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
/// RPM poll that writes a handshake and matches the reply back to the fans. The FanControl-thread
/// surface only mutates locked state; all USB I/O is on the worker-thread methods.
/// </summary>
internal sealed class TlFanController : IFanDevice {
    // The 0x0416 handshake reply is one 64-byte command-packet frame.
    private const int HandshakeReplyLength = 64;

    // The hub answers every command, and the plugin reads only the handshake's answer, so the speed,
    // sync and lighting answers queue ahead of it (the input buffer holds 512). A poll reads past
    // those to the handshake reply, up to this many reports, which drains the queue as it goes.
    private const int MaxReportsPerHandshake = 32;

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
    // fan a later poll finds. A channel keeps its address for good, and the sensor id is keyed on
    // the address, not the channel ordinal, so a fan found late re-keys nobody. Only the worker adds
    // one; every read from another thread is under _lock.
    private readonly List<Channel> _channels = new List<Channel>();
    private readonly Dictionary<int, int> _channelByAddress = new Dictionary<int, int>();

    private readonly object _lock = new object();

    // How many polls in a row have reported each address that is not a channel yet. Worker only.
    private readonly Dictionary<int, int> _unconfirmed = new Dictionary<int, int>();

    // The transport generation this hub last took software control under. A newer one means the
    // transport reopened the device after a handle fault, and a re-enumerated hub may have been
    // power-cycled and reverted to motherboard sync with its saved look lost, so both are replayed
    // and every duty re-sent before the next write. Worker-thread only; the replay is registered
    // before the worker starts.
    private int _setUpGeneration;
    private Action? _reconnectReplay;

    public TlFanController(int index, IDeviceTransport transport, IClock clock, ILog log) {
        _index = index;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        // Discover the fans once. Order by (port, fan-index) so the channel order - and therefore the
        // sensor ids below - is deterministic regardless of the reply's record order.
        TlFanReading[] detected = TlFanProtocol.DecodeHandshake(Handshake())
            .OrderBy(r => r.Port)
            .ThenBy(r => r.FanIndex)
            .ToArray();

        foreach (TlFanReading fan in detected) {
            AddChannel(fan.Port, fan.FanIndex);
        }

        // Take software control of each fan once. L-Connect sets motherboard-RPM-sync separately
        // from the speed writes, so this is asserted here rather than before every speed write.
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
    public void ReplayOnReconnect(Action replay) {
        _reconnectReplay = replay ?? throw new ArgumentNullException(nameof(replay));
    }

    // ---------- FanControl-thread surface (no I/O) ----------

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

    /// <summary>Read the last measured RPM for a channel.</summary>
    public float GetRpm(int channel) {
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

            bool changed = target != lastWritten;
            _transport.Write(TlFanProtocol.EncodeSetFanSpeed(channel.Port, channel.Fan, target));

            DateTime writtenAt = _clock.UtcNow;
            lock (_lock) {
                channel.LastWritten = target;
                channel.LastWriteUtc = writtenAt;
            }

            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "Set T{0}:{1}/{2} = {3}% ({4})",
                _index,
                channel.Port,
                channel.Fan,
                target,
                changed ? "change" : "refresh"));
        }
    }

    /// <summary>Write a handshake and match the reply's RPM records back to the fan channels.</summary>
    public void PollRpm() {
        IReadOnlyList<TlFanReading> readings = TlFanProtocol.DecodeHandshake(Handshake());

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

    // Write a handshake and read to its reply, past the answers to earlier commands queued ahead of
    // it. With none among the reports read, the last one read is returned, and decodes to no fans.
    private byte[] Handshake() {
        _transport.Write(TlFanProtocol.EncodeHandshakeRequest());
        byte[] reply = _transport.Read(HandshakeReplyLength);
        for (int read = 1; read < MaxReportsPerHandshake && !TlFanProtocol.IsHandshakeReply(reply); read++) {
            reply = _transport.Read(HandshakeReplyLength);
        }

        return reply;
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

    // A reopened transport means the hub was re-enumerated and may have come back reset. Redo what
    // construction and Initialize did for it, in the same order - software control, then the saved
    // look - and forget every last-written duty so the loop that follows re-sends each fan now. A
    // throw leaves the generation unrecorded, so the replay is retried on the next tick.
    private void ReplaySetupIfReconnected() {
        int generation = _transport.Generation;
        if (generation == _setUpGeneration) {
            return;
        }

        TakeSoftwareControl();
        _reconnectReplay?.Invoke();
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
                CultureInfo.InvariantCulture, "T{0}:{1}/{2} implausible rpm {3} ignored, keeping {4}", _index, channel.Port, channel.Fan, rpm, channel.Rpm));
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

        public int LastWritten { get; set; } = -2;     // last duty actually written

        public DateTime LastWriteUtc { get; set; } = DateTime.MinValue;

        public float Rpm { get; set; }                 // last measured RPM

        public bool RpmImplausible { get; set; }       // last read rejected as garbage
    }
}
