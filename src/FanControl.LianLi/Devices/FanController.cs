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

    // After a wake/resume the SL-Infinity returns a fixed idle-state buffer for a short window
    // before real tachometer data arrives. A single detection read landing in that window decodes
    // garbage (some channels above MaxPlausibleRpm, others below it) and would register the wrong
    // channels. Detection re-reads until the whole report is plausible (the device has settled),
    // capped so a device that never settles still falls back to showing all four channels. On a
    // normal boot the first read is already settled, so the retry costs nothing there.
    private const int DetectionMaxAttempts = 15;
    private static readonly TimeSpan DetectionRetryDelay = TimeSpan.FromMilliseconds(200);

    private readonly int _index;
    private readonly IHidTransport _transport;
    private readonly IFanProtocol _protocol;
    private readonly IClock _clock;
    private readonly ILog _log;

    // Maps external channel index (0..ChannelCount-1) to physical channel (0..3).
    // Set at construction by a one-shot RPM read that finds which channels have fans.
    // Falls back to [0,1,2,3] if detection fails or all channels read zero (fans off at boot).
    private readonly int[] _physicalChannels;

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

        // Detect which channels have fans connected. If all channels read zero (fans not yet
        // spinning at boot, or detection fails) fall back to exposing all four channels so the
        // controller is not hidden entirely.
        _physicalChannels = DetectPhysicalChannels();
    }

    /// <summary>The controller family, for logging/diagnostics.</summary>
    public DeviceFamily Family => _protocol.Family;

    /// <summary>The number of channels with detected fans (1–4); four if detection found none or failed.</summary>
    public int ChannelCount => _physicalChannels.Length;

    /// <summary>
    /// The sensor identity for a channel. The id is keyed on the physical channel number so it
    /// is stable across restarts and is unaffected by how many other channels are populated.
    /// </summary>
    public ChannelDescriptor Describe(int channel) {
        int phys = _physicalChannels[channel];
        return new ChannelDescriptor(
            $"LianLi/{_index}/ch{phys}/ctl",
            $"Lian Li Uni #{_index + 1} Ch {phys + 1}",
            $"LianLi/{_index}/ch{phys}/fan",
            $"Lian Li Uni #{_index + 1} Ch {phys + 1} RPM");
    }

    // ---------- FanControl-thread surface (no I/O) ----------

    /// <summary>Set the commanded duty for a channel. The worker pushes it to hardware.</summary>
    public void SetTarget(int channel, int duty) {
        lock (_lock) {
            _target[_physicalChannels[channel]] = duty;
        }
    }

    /// <summary>Release a channel so the keepalive stops asserting it (used by Reset).</summary>
    public void ReleaseChannel(int channel) {
        lock (_lock) {
            _target[_physicalChannels[channel]] = -1;
        }
    }

    /// <summary>Read the last measured RPM for a channel.</summary>
    public float GetRpm(int channel) {
        lock (_lock) {
            return _rpm[_physicalChannels[channel]];
        }
    }

    // ---------- worker-thread I/O (the only place HID is touched) ----------

    /// <summary>Push any changed-or-stale channel targets to the hardware.</summary>
    public void ApplyPending() {
        for (int ch = 0; ch < _physicalChannels.Length; ch++) {
            int phys = _physicalChannels[ch];
            int target;
            int lastWritten;
            DateTime lastWrite;
            lock (_lock) {
                target = _target[phys];
                lastWritten = _lastWritten[phys];
                lastWrite = _lastWriteUtc[phys];
            }

            if (!ChannelWriteDecision.ShouldWrite(
                    target, lastWritten, lastWrite, _clock.UtcNow, ChannelWriteDecision.RefreshInterval)) {
                continue;
            }

            bool changed = target != lastWritten;
            WriteSpeed(phys, target);

            DateTime writtenAt = _clock.UtcNow;
            lock (_lock) {
                _lastWritten[phys] = target;
                _lastWriteUtc[phys] = writtenAt;
            }

            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "Set C{0}:{1} = {2}% ({3})",
                _index,
                phys,
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

    private void WriteSpeed(int channel, int duty) {
        // Re-assert manual (software) mode BEFORE the speed write: a channel that
        // slipped back to PWM/RPM-sync mode IGNORES speed writes, so without this
        // the commanded speed never sticks.
        _transport.Write(_protocol.EncodeManualMode(channel));
        _transport.Write(_protocol.EncodeSetSpeed(channel, duty));
    }

    // Find which physical channels have fans, re-reading until the device returns a settled
    // (fully plausible) report so a post-wake idle buffer cannot fool the detection. Returns the
    // detected channel numbers in ascending order, or [0,1,2,3] as a fallback when the device
    // never settles within the retry budget, no fans are spinning, or the read fails.
    private int[] DetectPhysicalChannels() {
        try {
            for (int attempt = 1; attempt <= DetectionMaxAttempts; attempt++) {
                byte[] buffer = ReadRpmReport();

                // An implausible channel means the device is still in its idle/garbage state
                // (post-wake). Wait for it to settle and re-read rather than trust this report.
                if (!AllChannelsPlausible(buffer)) {
                    if (attempt < DetectionMaxAttempts) {
                        _clock.Sleep(DetectionRetryDelay);
                        continue;
                    }

                    // Never settled within the budget: keep the controller visible with all
                    // channels rather than guess from garbage.
                    _log.Write(string.Format(
                        CultureInfo.InvariantCulture,
                        "C{0} channel detection: device did not settle after {1} attempts, showing all channels",
                        _index,
                        DetectionMaxAttempts));
                    return new[] { 0, 1, 2, 3 };
                }

                var detected = new List<int>();
                for (int ch = 0; ch < Channels; ch++) {
                    if (_protocol.DecodeRpm(buffer, ch) > 0f) {
                        detected.Add(ch);
                    }
                }

                if (detected.Count > 0) {
                    // The count is populated CHANNELS, not fans: daisy-chained fans on one channel
                    // share a tachometer, so the controller cannot report how many are on a channel.
                    _log.Write(string.Format(
                        CultureInfo.InvariantCulture,
                        "C{0} channel detection: fans detected on {1} channel(s) [{2}] (attempt {3})",
                        _index,
                        detected.Count,
                        string.Join(", ", detected),
                        attempt));
                    return detected.ToArray();
                }

                // Settled but every channel reads zero: fans not yet spinning at boot, or all
                // unplugged. Show all four so the controller is not hidden entirely.
                _log.Write(string.Format(
                    CultureInfo.InvariantCulture,
                    "C{0} channel detection: no fans spinning, showing all channels",
                    _index));
                return new[] { 0, 1, 2, 3 };
            }
        }
#pragma warning disable CA1031 // resilience: detection failure is non-fatal; fall back to all channels
        catch (Exception ex) {
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "C{0} channel detection failed, showing all channels: {1}",
                _index,
                ex.Message));
        }
#pragma warning restore CA1031

        return new[] { 0, 1, 2, 3 };
    }

    // A settled report has every channel in the plausible RPM range. The post-wake idle buffer
    // fails this: its leading bytes have been observed as 0xE0 0x50, which decode at channel 0 to
    // 57424 rpm - far above MaxPlausibleRpm - so the range check is also an exact reject of that
    // signature, without hard-coding the byte pattern.
    // Prime the device (if its family is request-response) and then pull the RPM input report.
    // The SL-Infinity returns a stale idle buffer until the primer feature report is sent;
    // streaming families have no primer and read directly.
    private byte[] ReadRpmReport() {
        byte[]? primer = _protocol.EncodeRpmPrimer();
        if (primer != null) {
            _transport.SetFeature(primer);
        }

        return _transport.GetInputReport(RpmReportId, RpmReportLength);
    }

    private bool AllChannelsPlausible(byte[] buffer) {
        for (int ch = 0; ch < Channels; ch++) {
            if (!ChannelReadDecision.IsPlausible(_protocol.DecodeRpm(buffer, ch))) {
                return false;
            }
        }

        return true;
    }
}
