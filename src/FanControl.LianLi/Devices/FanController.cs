using System;
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
internal sealed class FanController : IDisposable {
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

    /// <summary>Read every channel's RPM into the cache.</summary>
    public void PollRpm() {
        PrimeRpmReport();
        byte[] buffer = _transport.GetInputReport(RpmReportId, RpmReportLength);
        lock (_lock) {
            for (int ch = 0; ch < Channels; ch++) {
                _rpm[ch] = _protocol.DecodeRpm(buffer, ch);
            }
        }
    }

    private void PrimeRpmReport() {
        byte[]? request = _protocol.EncodeRpmRequest();
        if (request != null) {
            _transport.SetFeature(request);
        }
    }

    public void Dispose() {
        _transport.Dispose();
    }

    private void WriteSpeed(int channel, int duty) {
        // Re-assert manual (software) mode BEFORE the speed write: a channel that
        // slipped back to PWM/RPM-sync mode IGNORES speed writes, so without this
        // the commanded speed never sticks.
        _transport.Write(_protocol.EncodeManualMode(channel));
        _transport.Write(_protocol.EncodeSetSpeed(channel, duty));
    }
}
