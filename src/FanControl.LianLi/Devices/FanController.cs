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

    // How many RPM probes the startup population detection takes. A few reads get past the stale
    // idle buffer the device returns before it wakes; a majority-plausible rule (see
    // ChannelPopulationDecision) then separates a spinning fan from an empty channel's garbage.
    private const int PopulationProbeReads = 6;

    private readonly int _index;
    private readonly IHidTransport _transport;
    private readonly IFanProtocol _protocol;
    private readonly bool[] _startStopEnabled;   // per-channel L-Connect start/stop toggle
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

    // Which channels have a fan attached. Defaults to all-shown; DetectPopulation() narrows it once
    // during setup. Set-once before Load reads it via IsChannelPopulated, so no synchronization is
    // needed - the host thread only reads it after the composition root has run detection.
    private bool[] _populated = { true, true, true, true };

    public FanController(
        int index,
        IHidTransport transport,
        IFanProtocol protocol,
        bool[] startStopEnabled,
        IClock clock,
        ILog log) {
        _index = index;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
        if (startStopEnabled is null) {
            throw new ArgumentNullException(nameof(startStopEnabled));
        }

        if (startStopEnabled.Length != Channels) {
            throw new ArgumentException(
                $"Expected {Channels} start/stop flags, got {startStopEnabled.Length}.", nameof(startStopEnabled));
        }

        // Copy so a later mutation of the caller's array cannot change this controller's behavior.
        _startStopEnabled = (bool[])startStopEnabled.Clone();
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>The controller family, for logging/diagnostics.</summary>
    public DeviceFamily Family => _protocol.Family;

    /// <summary>The Uni controllers expose four fan channels.</summary>
    public int ChannelCount => Channels;

    /// <inheritdoc />
    public bool IsChannelPopulated(int channel) => _populated[channel];

    /// <summary>
    /// One-time setup writes: assert manual (software) mode on every channel so the host owns the
    /// speed (ARGB build: preceded by the LED ARGB-header sync enable). L-Connect sends every
    /// fan-control and config command as a feature report, so these go through SetFeature. Called
    /// once by the plugin composition root after construction - never in the constructor, so a hub
    /// that rejects a setup write cannot abort construction and cost the user the whole controller.
    /// The caller guards this call; on a fault the controller stays registered and regains ownership
    /// on the worker path, which re-asserts manual mode before every speed write.
    /// </summary>
    public void AssertManualMode() {
#if ENABLE_ARGB
        // ARGB build only: enable LED ARGB-header sync so the fans' lighting follows
        // the motherboard's ARGB header. On controllers that do not persist config to
        // hardware (e.g. SL-Infinity 120 V1) this resets lighting to factory on every
        // startup - the documented trade-off of the ARGB variant.
        _transport.SetFeature(_protocol.EncodeArgbSync(true));
#endif

        for (int ch = 0; ch < Channels; ch++) {
            _transport.SetFeature(_protocol.EncodeManualMode(ch));
        }
    }

    /// <summary>
    /// Detect which channels have a fan attached and narrow the surfaced set. The Uni controllers
    /// report no presence bit (the input report carries only RPM), so population is inferred from a
    /// burst of RPM probes: a channel with a spinning fan reads a plausible non-zero RPM on a
    /// majority of probes, while an empty channel reads 0 or occasional out-of-range garbage. The
    /// majority rule (and the all-empty fallback in <see cref="ChannelPopulationDecision"/>) keeps a
    /// real fan from being hidden and an empty channel from being shown. Called once by the plugin
    /// composition root after construction and before Load registers sensors - off the periodic
    /// Update path. The probe reads can throw on a genuine device fault; the caller guards this call
    /// so a fault leaves the controller shown (all channels), never lost.
    ///
    /// Limitation: with no presence bit, a fan that is present but physically stopped at probe time
    /// (a 0rpm-capable fan the user has stopped) reads identically to an empty channel and stays
    /// hidden until it next spins. Detection runs before the plugin drives anything, when a present
    /// fan sits at its non-zero firmware default, so this is rare in practice.
    /// </summary>
    public void DetectPopulation() {
        var plausibleCounts = new int[Channels];
        for (int probe = 0; probe < PopulationProbeReads; probe++) {
            byte[] buffer = ReadRpmReport();
            for (int ch = 0; ch < Channels; ch++) {
                float rpm = _protocol.DecodeRpm(buffer, ch);
                if (ChannelReadDecision.IsPlausible(rpm) && rpm > 0f) {
                    plausibleCounts[ch]++;
                }
            }
        }

        _populated = ChannelPopulationDecision.Resolve(plausibleCounts, PopulationProbeReads);
        _log.Write(string.Format(
            CultureInfo.InvariantCulture,
            "Controller {0} channel population: plausible reads [{1}] of {2} -> populated [{3}]",
            _index,
            string.Join(",", plausibleCounts),
            PopulationProbeReads,
            string.Join(",", _populated)));
    }

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
        // Re-assert manual (software) mode BEFORE the speed write: a channel that slipped back to
        // PWM/RPM-sync mode IGNORES speed writes, so without this the commanded speed never sticks.
        // Both are feature reports, matching L-Connect (the fan control path uses SET_REPORT(Feature),
        // not output reports).
        _transport.SetFeature(_protocol.EncodeManualMode(channel));
        _transport.SetFeature(_protocol.EncodeSetSpeed(channel, duty, _startStopEnabled[channel]));
    }
}
