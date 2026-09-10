using System;
using System.Collections.Generic;
using System.Globalization;
using FanControl.LianLi.Hid;
using FanControl.LianLi.Logging;
using FanControl.LianLi.Protocol;

namespace FanControl.LianLi.Devices;

/// <summary>
/// Coordinates one Galahad II Trinity AIO (vendor 0x0416): channel 0 is the fan, channel 1 the
/// pump. Like <see cref="FanController"/> the FanControl-thread surface only mutates locked
/// in-memory state and every USB transfer happens on the worker-thread methods, but the 0x0416
/// family writes a command packet and reads its reply rather than pulling an input report: a
/// keepalive write per channel, and an RPM poll that writes a handshake and reads the answer. The
/// pump duty is floored in <see cref="Galahad2Protocol"/> so a curve can never stop the pump.
/// </summary>
internal sealed class Galahad2Controller : IFanDevice {
    private const int FanChannel = 0;
    private const int PumpChannel = 1;
    private const int Channels = 2;

    // The 0x0416 handshake reply is one 64-byte command-packet frame.
    private const int HandshakeReplyLength = 64;

    private readonly int _index;
    private readonly IHidTransport _transport;
    private readonly IClock _clock;
    private readonly ILog _log;

    private readonly object _lock = new object();
    private readonly int[] _target = { -1, -1 };          // commanded duty %, -1 = unassigned
    private readonly int[] _lastWritten = { -2, -2 };     // last duty actually written
    private readonly DateTime[] _lastWriteUtc = { DateTime.MinValue, DateTime.MinValue };
    private readonly float[] _rpm = { 0f, 0f };           // last measured RPM
    private readonly bool[] _rpmImplausible = { false, false }; // last read rejected as garbage

    // The transport generation this cooler was last set up under. A newer one means the transport
    // reopened the device after a handle fault; the cooler needs no setup writes of its own, but a
    // re-enumerated one may have been power-cycled and lost its saved look, so that is replayed and
    // both duties re-sent before the next write. Worker-thread only; the replay is registered
    // before the worker starts.
    private int _setUpGeneration;
    private Action? _reconnectReplay;

    public Galahad2Controller(int index, IHidTransport transport, IClock clock, ILog log) {
        _index = index;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _setUpGeneration = _transport.Generation;
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

            bool changed = target != lastWritten;
            WriteDuty(ch, target);

            DateTime writtenAt = _clock.UtcNow;
            lock (_lock) {
                _lastWritten[ch] = target;
                _lastWriteUtc[ch] = writtenAt;
            }

            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "Set G{0}:{1} = {2}% ({3})",
                _index,
                ch == PumpChannel ? "pump" : "fan",
                target,
                changed ? "change" : "refresh"));
        }
    }

    /// <summary>Write a handshake and decode the reply into the fan and pump RPM cache.</summary>
    public void PollRpm() {
        _transport.Write(Galahad2Protocol.EncodeHandshakeRequest());
        byte[] reply = _transport.Read(HandshakeReplyLength);
        Galahad2Reading reading = Galahad2Protocol.DecodeHandshake(reply);

        List<string>? transitions = null;
        lock (_lock) {
            UpdateRpm(FanChannel, reading.FanRpm, ref transitions);
            UpdateRpm(PumpChannel, reading.PumpRpm, ref transitions);
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

    // A reopened transport means the cooler was re-enumerated and may have come back reset. Replay
    // the saved look (if the Lighting build registered one) and forget every last-written duty so
    // the loop that follows re-sends the fan and pump now. A throw leaves the generation
    // unrecorded, so the replay is retried on the next tick.
    private void ReplaySetupIfReconnected() {
        int generation = _transport.Generation;
        if (generation == _setUpGeneration) {
            return;
        }

        _reconnectReplay?.Invoke();
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
                CultureInfo.InvariantCulture, "G{0}:{1} implausible rpm {2} ignored, keeping {3}", _index, channel, rpm, _rpm[channel]));
        }
    }
}
