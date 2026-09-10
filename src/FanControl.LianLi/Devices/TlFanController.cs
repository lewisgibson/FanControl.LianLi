using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FanControl.LianLi.Hid;
using FanControl.LianLi.Logging;
using FanControl.LianLi.Protocol;

namespace FanControl.LianLi.Devices;

/// <summary>
/// Coordinates one Uni Fan TL hub (vendor 0x0416). Unlike the fixed four-channel Uni controllers,
/// the TL exposes a variable set of fans addressed by (port, fan-index); the set is discovered once
/// from the construction handshake and each detected fan becomes a channel. Like the other 0x0416
/// devices it writes a command packet and reads the reply: a keepalive speed write per fan, and an
/// RPM poll that writes a handshake and matches the reply back to the fans. The FanControl-thread
/// surface only mutates locked state; all USB I/O is on the worker-thread methods.
/// </summary>
internal sealed class TlFanController : IFanDevice {
    // The 0x0416 handshake reply is one 64-byte command-packet frame.
    private const int HandshakeReplyLength = 64;

    private readonly int _index;
    private readonly IHidTransport _transport;
    private readonly IClock _clock;
    private readonly ILog _log;

    // Per-channel address, fixed at construction so a channel keeps mapping to the same physical
    // fan run to run (the sensor id is keyed on the address, not the channel ordinal).
    private readonly int[] _ports;
    private readonly int[] _fans;
    private readonly Dictionary<int, int> _channelByAddress;

    private readonly object _lock = new object();
    private readonly int[] _target;          // commanded duty %, -1 = unassigned
    private readonly int[] _lastWritten;     // last duty actually written
    private readonly DateTime[] _lastWriteUtc;
    private readonly float[] _rpm;            // last measured RPM
    private readonly bool[] _rpmImplausible;  // last read rejected as garbage

    // The transport generation this hub last took software control under. A newer one means the
    // transport reopened the device after a handle fault, and a re-enumerated hub may have been
    // power-cycled and reverted to motherboard sync with its saved look lost, so both are replayed
    // and every duty re-sent before the next write. Worker-thread only; the replay is registered
    // before the worker starts.
    private int _setUpGeneration;
    private Action? _reconnectReplay;

    public TlFanController(int index, IHidTransport transport, IClock clock, ILog log) {
        _index = index;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        // Discover the fans once. Order by (port, fan-index) so the channel order - and therefore the
        // sensor ids below - is deterministic regardless of the reply's record order.
        _transport.Write(TlFanProtocol.EncodeHandshakeRequest());
        byte[] reply = _transport.Read(HandshakeReplyLength);
        TlFanReading[] detected = TlFanProtocol.DecodeHandshake(reply)
            .OrderBy(r => r.Port)
            .ThenBy(r => r.FanIndex)
            .ToArray();

        int count = detected.Length;
        _ports = new int[count];
        _fans = new int[count];
        _channelByAddress = new Dictionary<int, int>(count);
        _target = new int[count];
        _lastWritten = new int[count];
        _lastWriteUtc = new DateTime[count];
        _rpm = new float[count];
        _rpmImplausible = new bool[count];
        for (int ch = 0; ch < count; ch++) {
            _ports[ch] = detected[ch].Port;
            _fans[ch] = detected[ch].FanIndex;
            _channelByAddress[Address(detected[ch].Port, detected[ch].FanIndex)] = ch;
            _target[ch] = -1;
            _lastWritten[ch] = -2;
            _lastWriteUtc[ch] = DateTime.MinValue;
        }

        // Take software control of each fan once. L-Connect sets motherboard-RPM-sync separately
        // from the speed writes, so this is asserted here rather than before every speed write.
        TakeSoftwareControl();
        _setUpGeneration = _transport.Generation;
    }

    /// <summary>How many fans the hub reported at construction.</summary>
    public int ChannelCount => _ports.Length;

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
        int port = _ports[channel];
        int fan = _fans[channel];
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
            _target[channel] = duty;
        }
    }

    /// <summary>Release a channel so the keepalive stops asserting it (used by Reset).</summary>
    public void ReleaseChannel(int channel) {
        lock (_lock) {
            _target[channel] = -1;
        }
    }

    /// <summary>Read the last measured RPM for a channel.</summary>
    public float GetRpm(int channel) {
        lock (_lock) {
            return _rpm[channel];
        }
    }

    // ---------- worker-thread I/O (the only place HID is touched) ----------

    /// <summary>Push any changed-or-stale channel targets to the hardware.</summary>
    public void ApplyPending() {
        ReplaySetupIfReconnected();

        for (int ch = 0; ch < _ports.Length; ch++) {
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

            bool changed = target != lastWritten;
            _transport.Write(TlFanProtocol.EncodeSetFanSpeed(_ports[ch], _fans[ch], target));

            DateTime writtenAt = _clock.UtcNow;
            lock (_lock) {
                _lastWritten[ch] = target;
                _lastWriteUtc[ch] = writtenAt;
            }

            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "Set T{0}:{1}/{2} = {3}% ({4})",
                _index,
                _ports[ch],
                _fans[ch],
                target,
                changed ? "change" : "refresh"));
        }
    }

    /// <summary>Write a handshake and match the reply's RPM records back to the fan channels.</summary>
    public void PollRpm() {
        _transport.Write(TlFanProtocol.EncodeHandshakeRequest());
        byte[] reply = _transport.Read(HandshakeReplyLength);
        IReadOnlyList<TlFanReading> readings = TlFanProtocol.DecodeHandshake(reply);

        List<string>? transitions = null;
        lock (_lock) {
            foreach (TlFanReading reading in readings) {
                if (_channelByAddress.TryGetValue(Address(reading.Port, reading.FanIndex), out int channel)) {
                    UpdateRpm(channel, reading.Rpm, ref transitions);
                }
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

    private void TakeSoftwareControl() {
        for (int ch = 0; ch < _ports.Length; ch++) {
            _transport.Write(TlFanProtocol.EncodeMotherboardSync(_ports[ch], _fans[ch], sync: false));
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
            for (int ch = 0; ch < _ports.Length; ch++) {
                _lastWritten[ch] = -2;
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
    private void UpdateRpm(int channel, int rpm, ref List<string>? transitions) {
        if (ChannelReadDecision.IsPlausible(rpm)) {
            _rpm[channel] = rpm;
            if (_rpmImplausible[channel]) {
                _rpmImplausible[channel] = false;
                (transitions ??= new List<string>()).Add(string.Format(
                    CultureInfo.InvariantCulture, "T{0}:{1}/{2} rpm recovered ({3})", _index, _ports[channel], _fans[channel], rpm));
            }
        } else if (!_rpmImplausible[channel]) {
            _rpmImplausible[channel] = true;
            (transitions ??= new List<string>()).Add(string.Format(
                CultureInfo.InvariantCulture, "T{0}:{1}/{2} implausible rpm {3} ignored, keeping {4}", _index, _ports[channel], _fans[channel], rpm, _rpm[channel]));
        }
    }
}
