using System;
using System.Collections.Generic;
using System.Globalization;
using FanControl.LianLi.Hid;
using FanControl.LianLi.Logging;
using FanControl.LianLi.Protocol;

namespace FanControl.LianLi.Devices;

/// <summary>
/// Coordinates one physical controller (4 channels). The FanControl-thread
/// surface (<see cref="SetTarget"/>, <see cref="ReleaseChannel"/>,
/// <see cref="GetRpm"/>) only mutates locked in-memory state; every USB
/// transfer happens on the worker-thread methods (<see cref="ApplyPending"/>,
/// <see cref="PollRpm"/>), so the host UI thread never blocks on HID I/O.
/// </summary>
internal sealed class FanController : IFanDevice {
    private const int Channels = 4;
    private const byte RpmReportId = 224;
    private const int RpmReportLength = 65;

    private readonly int _index;
    private readonly IHidTransport _transport;
    private readonly IFanProtocol _protocol;
    private readonly IClock _clock;
    private readonly ILog _log;

    private readonly object _lock = new object();
    private readonly int[] _target = { -1, -1, -1, -1 };         // commanded duty %, -1 = unassigned
    private readonly int[] _lastWritten = { -2, -2, -2, -2 };    // last duty actually written
    private readonly DateTime[] _lastWriteUtc =
    {
        DateTime.MinValue, DateTime.MinValue, DateTime.MinValue, DateTime.MinValue,
    };
    private readonly float[] _rpm = { 0f, 0f, 0f, 0f };          // last measured RPM
    private readonly bool[] _rpmImplausible = { false, false, false, false }; // last read rejected as garbage

    public FanController(
        int index,
        IHidTransport transport,
        IFanProtocol protocol,
        IClock clock,
        ILog log) {
        _index = index;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        // One-time setup I/O (runs during Initialize, not on the periodic UI path).
#if ENABLE_ARGB
        // ARGB build only: enable LED ARGB-header sync so the fans' lighting follows
        // the motherboard's ARGB header. On controllers that do not persist config to
        // hardware (e.g. SL-Infinity 120 V1) this resets lighting to factory on every
        // startup - the documented trade-off of the ARGB variant.
        _transport.Write(_protocol.EncodeArgbSync(true));
#endif

        // Assert manual (software) mode on every channel so the host owns the speed.
        for (int ch = 0; ch < Channels; ch++) {
            _transport.Write(_protocol.EncodeManualMode(ch));
        }
    }

    /// <summary>The controller family, for logging/diagnostics.</summary>
    public DeviceFamily Family => _protocol.Family;

    /// <summary>The Uni controllers expose four fan channels.</summary>
    public int ChannelCount => Channels;

    /// <summary>
    /// The sensor identity for a Uni channel. These id/name strings are the contract with the
    /// user's saved fan-curve bindings - keep them byte-stable; a change re-keys every control.
    /// </summary>
    public ChannelDescriptor Describe(int channel) {
        return new ChannelDescriptor(
            $"LianLi/{_index}/ch{channel}/ctl",
            $"Lian Li Uni #{_index + 1} Ch {channel + 1}",
            $"LianLi/{_index}/ch{channel}/fan",
            $"Lian Li Uni #{_index + 1} Ch {channel + 1} RPM");
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
            WriteSpeed(ch, target);

            DateTime writtenAt = _clock.UtcNow;
            lock (_lock) {
                _lastWritten[ch] = target;
                _lastWriteUtc[ch] = writtenAt;
            }

            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "Set C{0}:{1} = {2}% ({3})",
                _index,
                ch,
                target,
                changed ? "change" : "refresh"));
        }
    }

    /// <summary>Read every channel's RPM into the cache, ignoring implausible (garbage) readings.</summary>
    public void PollRpm() {
        byte[] buffer = ReadRpmReport();

        // Decode and validate under the lock; collect any state-transition messages and write them
        // to the file log AFTER releasing the lock, so log I/O never runs while the lock is held.
        List<string>? transitions = null;
        lock (_lock) {
            for (int ch = 0; ch < Channels; ch++) {
                float rpm = _protocol.DecodeRpm(buffer, ch);
                if (ChannelReadDecision.IsPlausible(rpm)) {
                    _rpm[ch] = rpm;
                    if (_rpmImplausible[ch]) {
                        _rpmImplausible[ch] = false;
                        (transitions ??= new List<string>()).Add(string.Format(
                            CultureInfo.InvariantCulture, "C{0}:{1} rpm recovered ({2})", _index, ch, rpm));
                    }
                } else if (!_rpmImplausible[ch]) {
                    // Idle/garbage read (e.g. ~50000 after hibernate): keep the last good value and
                    // log the onset once, so a persistent garbage read is visible without spamming.
                    _rpmImplausible[ch] = true;
                    (transitions ??= new List<string>()).Add(string.Format(
                        CultureInfo.InvariantCulture, "C{0}:{1} implausible rpm {2} ignored, keeping {3}", _index, ch, rpm, _rpm[ch]));
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

    // Prime the device, then pull the RPM input report. The Uni controllers are request-response:
    // HidD_GetInputReport returns a stale idle buffer until the primer feature report asks the device
    // to refresh it, which is why L-Connect sends it before every read (see EncodeRpmPrimer).
    private byte[] ReadRpmReport() {
        _transport.SetFeature(_protocol.EncodeRpmPrimer());
        return _transport.GetInputReport(RpmReportId, RpmReportLength);
    }

    private void WriteSpeed(int channel, int duty) {
        // Re-assert manual (software) mode BEFORE the speed write: a channel that
        // slipped back to PWM/RPM-sync mode IGNORES speed writes, so without this
        // the commanded speed never sticks.
        _transport.Write(_protocol.EncodeManualMode(channel));
        _transport.Write(_protocol.EncodeSetSpeed(channel, duty));
    }
}
