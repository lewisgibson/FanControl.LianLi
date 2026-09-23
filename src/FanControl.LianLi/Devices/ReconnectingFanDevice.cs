using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using FanControl.LianLi.Logging;
using FanControl.LianLi.Transport;

namespace FanControl.LianLi.Devices;

/// <summary>
/// Stands in for a controller the plugin built on an earlier scan but could not build on this
/// one - the scan timed out on a device that had stopped answering Windows, or the device itself
/// would not open. FanControl only registers sensors when the plugin initialises, so without this
/// the fans would be gone from FanControl until its next refresh. The stand-in carries the exact
/// sensor identities the controller had, so the same sensors are registered and the user's curve
/// bindings stay intact, and it builds the real controller in the background - on the shared
/// <see cref="DoublingBackoff"/> schedule, through the plugin's own builder, on a thread of its own
/// so a device that takes seconds to answer delays nothing but itself - then forwards everything to
/// it, matching channels by id so a fan that did not come back is simply left with no reading.
/// </summary>
internal sealed class ReconnectingFanDevice : IFanDevice, ITemperatureSource, IFanSpeedSource {
    private readonly int _index;
    private readonly ChannelDescriptor[] _channels;
    private readonly TemperatureDescriptor[] _temperatures;
    private readonly FanSpeedDescriptor[] _fanSpeeds;
    private readonly Func<IFanDevice> _build;
    private readonly ILog _log;
    // The rebuild schedule: immediate, then a gap doubling from 10 offers to 640. The worker
    // offers two a tick, so a device that is simply gone is retried every few minutes.
    private readonly DoublingBackoff _backoff = new DoublingBackoff(10, 640);

    private readonly object _lock = new object();
    private readonly int[] _target;          // commanded duty %, -1 = unassigned, forwarded on build
    private Connection? _connection;         // null until the controller has been rebuilt
    private readonly Action<ThreadStart> _runInBackground;
    private Action? _reconnectReplay;
    private int _attempts;
    private bool _building;
    private bool _disposed;

    public ReconnectingFanDevice(
        int index,
        IReadOnlyList<ChannelDescriptor> channels,
        IReadOnlyList<FanSpeedDescriptor> fanSpeeds,
        IReadOnlyList<TemperatureDescriptor> temperatures,
        Func<IFanDevice> build,
        ILog log)
        : this(index, channels, fanSpeeds, temperatures, build, log, StartThread) {
    }

    /// <summary>
    /// As the public constructor, with the way a rebuild is run in the background supplied; a test
    /// runs it inline to see each attempt deterministically.
    /// </summary>
    internal ReconnectingFanDevice(
        int index,
        IReadOnlyList<ChannelDescriptor> channels,
        IReadOnlyList<FanSpeedDescriptor> fanSpeeds,
        IReadOnlyList<TemperatureDescriptor> temperatures,
        Func<IFanDevice> build,
        ILog log,
        Action<ThreadStart> runInBackground) {
        _index = index;
        _runInBackground = runInBackground ?? throw new ArgumentNullException(nameof(runInBackground));
        if (channels is null) {
            throw new ArgumentNullException(nameof(channels));
        }

        if (fanSpeeds is null) {
            throw new ArgumentNullException(nameof(fanSpeeds));
        }

        if (temperatures is null) {
            throw new ArgumentNullException(nameof(temperatures));
        }

        _build = build ?? throw new ArgumentNullException(nameof(build));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _channels = new ChannelDescriptor[channels.Count];
        for (int i = 0; i < channels.Count; i++) {
            _channels[i] = channels[i];
        }

        _fanSpeeds = new FanSpeedDescriptor[fanSpeeds.Count];
        for (int i = 0; i < fanSpeeds.Count; i++) {
            _fanSpeeds[i] = fanSpeeds[i];
        }

        _temperatures = new TemperatureDescriptor[temperatures.Count];
        for (int i = 0; i < temperatures.Count; i++) {
            _temperatures[i] = temperatures[i];
        }

        _target = new int[_channels.Length];
        for (int ch = 0; ch < _target.Length; ch++) {
            _target[ch] = -1;
        }
    }

    /// <summary>
    /// A stand-in around a controller that has just been built but no longer has every sensor it
    /// registered before - a wireless group not heard yet after a wake, a fan stopped while the
    /// channels were probed. It presents the remembered sensors, forwards those the controller has,
    /// and reads nothing for the rest, so no curve binding is dropped while they are away.
    /// </summary>
    public static ReconnectingFanDevice Around(
        IFanDevice built,
        int index,
        IReadOnlyList<ChannelDescriptor> channels,
        IReadOnlyList<FanSpeedDescriptor> fanSpeeds,
        IReadOnlyList<TemperatureDescriptor> temperatures,
        Func<IFanDevice> rebuild,
        ILog log) {
        if (built is null) {
            throw new ArgumentNullException(nameof(built));
        }

        var device = new ReconnectingFanDevice(index, channels, fanSpeeds, temperatures, rebuild, log);
        _ = device.Adopt(built);
        return device;
    }

    /// <summary>
    /// Take <paramref name="built"/> - the controller this stand-in is for, built elsewhere, by a scan
    /// build that finished after the scan had moved on - so the device is not opened a second time.
    /// False, leaving <paramref name="built"/> to the caller, when this stand-in is already connected
    /// or disposed.
    /// </summary>
    public bool Offer(IFanDevice built) {
        if (built is null) {
            throw new ArgumentNullException(nameof(built));
        }

        return Adopt(built);
    }

    /// <summary>
    /// Raised, on whichever thread adopted it, with the controller once this stand-in has taken it -
    /// from its own rebuild or from <see cref="Offer"/>. The controller may have sensors the stand-in
    /// was not made with (a wireless device heard while it was built); the stand-in forwards only the
    /// remembered ones, so whoever registered them needs to know.
    /// </summary>
    public event EventHandler<IFanDevice>? Adopted;

    /// <summary>Whether the real controller has been built yet.</summary>
    public bool IsConnected {
        get {
            lock (_lock) {
                return _connection != null;
            }
        }
    }

    /// <summary>The channels the controller had when it was remembered; all are registered again.</summary>
    public int ChannelCount => _channels.Length;

    /// <summary>Every remembered channel is registered, so the user's bindings survive the outage.</summary>
    public bool IsChannelPopulated(int channel) => true;

    /// <summary>The identity the channel had when it was remembered.</summary>
    public ChannelDescriptor Describe(int channel) => _channels[channel];

    /// <inheritdoc />
    public int FanSpeedCount => _fanSpeeds.Length;

    /// <inheritdoc />
    public FanSpeedDescriptor DescribeFanSpeed(int index) => _fanSpeeds[index];

    /// <summary>The controller's reading for the fan, or 0 until it exists (or if it lacks the fan).</summary>
    public float GetFanSpeed(int index) {
        lock (_lock) {
            return _connection?.FanSpeed(index) ?? 0f;
        }
    }

    /// <inheritdoc />
    public int TemperatureCount => _temperatures.Length;

    /// <inheritdoc />
    public TemperatureDescriptor DescribeTemperature(int index) => _temperatures[index];

    /// <inheritdoc />
    public float? GetTemperature(int index) {
        lock (_lock) {
            return _connection?.Temperature(index);
        }
    }

    /// <inheritdoc />
    public void ReplayOnReconnect(Action replay) {
        if (replay is null) {
            throw new ArgumentNullException(nameof(replay));
        }

        lock (_lock) {
            _reconnectReplay = replay;
            _connection?.Inner.ReplayOnReconnect(replay);
        }
    }

    // ---------- FanControl-thread surface (no I/O) ----------

    /// <summary>Set the commanded duty; kept for the controller and forwarded once (or as soon as) it exists.</summary>
    public void SetTarget(int channel, int duty) {
        lock (_lock) {
            _target[channel] = duty;
            _connection?.SetTarget(channel, duty);
        }
    }

    /// <summary>Release a channel; forwarded to the controller when it exists.</summary>
    public void ReleaseChannel(int channel) {
        lock (_lock) {
            _target[channel] = -1;
            _connection?.ReleaseChannel(channel);
        }
    }

    /// <summary>The controller's reading for the channel, or 0 until it exists (or if it lacks the channel).</summary>
    public float GetRpm(int channel) {
        lock (_lock) {
            return _connection?.GetRpm(channel) ?? 0f;
        }
    }

    // ---------- worker-thread I/O ----------

    /// <summary>Build the controller if it is due, then let it push its targets.</summary>
    public void ApplyPending() => Reconnect()?.Inner.ApplyPending();

    /// <summary>Build the controller if it is due, then let it poll.</summary>
    public void PollRpm() {
        Connection? connection = Reconnect();
        if (connection != null) {
            connection.Inner.PollRpm();
            Remap(connection);
        }
    }

    /// <summary>Dispose the controller, and any that a rebuild still running produces later.</summary>
    public void Dispose() {
        IFanDevice? inner;
        lock (_lock) {
            _disposed = true;
            inner = _connection?.Inner;
        }

        inner?.Dispose();
    }

    // Start a rebuild when the backoff schedule allows one (immediately the first time, then at
    // growing gaps, counted in calls: two per tick, like the transport's) and none is running. The
    // tick never waits for it; the controller is used from the tick after it is adopted.
    private Connection? Reconnect() {
        lock (_lock) {
            if (_connection != null || _building || _disposed || !_backoff.ShouldAttempt()) {
                return _connection;
            }

            _building = true;
            _attempts++;
        }

        _runInBackground(Rebuild);
        return null;
    }

    private void Rebuild() {
        IFanDevice? built = null;
        try {
            built = _build();
        }
#pragma warning disable CA1031 // resilience: a failed rebuild is logged and retried on the backoff; an exception escaping this thread would end FanControl's process
        catch (Exception ex) {
            _log.Write(string.Format(
                CultureInfo.InvariantCulture, "C{0} reconnect attempt {1} failed: {2}", _index, _attempts, ex.Message));
        }
#pragma warning restore CA1031

        if (built is null) {
            lock (_lock) {
                _building = false;
            }

            return;
        }

        if (!Adopt(built)) {
            DisposeUnadopted(built);
        }
    }

    // A rebuild that finished after the stand-in was disposed is closed here, on the rebuild's own
    // thread.
    private void DisposeUnadopted(IFanDevice built) {
        try {
            built.Dispose();
        }
#pragma warning disable CA1031 // resilience: an exception escaping this thread would end FanControl's process
        catch (Exception ex) {
            _log.Write(string.Format(
                CultureInfo.InvariantCulture, "C{0} closing a controller rebuilt after shutdown failed: {1}", _index, ex.Message));
        }
#pragma warning restore CA1031
    }

    private static void StartThread(ThreadStart work)
        => new Thread(work) { IsBackground = true, Name = "LianLiReconnect" }.Start();

    // A controller that adds sensors as it goes - a wireless one hearing a device check in - may by
    // now have one the connection could not match when it was made; match again, and hand the new
    // matches their targets. Only when its sensor count moved, so an unchanged controller costs a
    // comparison a poll.
    // Only the worker thread ever replaces the connection (the rebuild adopts it, this remaps it),
    // so what is read here is still current when it is replaced.
    private void Remap(Connection current) {
        if (!current.IsBehind) {
            return;
        }

        var next = new Connection(current.Inner, _channels, _fanSpeeds, _temperatures);
        lock (_lock) {
            _connection = next;
            for (int ch = 0; ch < _channels.Length; ch++) {
                if (_target[ch] >= 0) {
                    next.SetTarget(ch, _target[ch]);
                }
            }
        }

        _log.Write(string.Format(
            CultureInfo.InvariantCulture,
            "C{0}: {1} of {2} remembered channel(s) are there now",
            _index,
            next.MatchedChannels,
            _channels.Length));
    }

    // Match the built controller's channels to the remembered ones by control id and hand it
    // everything the host set in the meantime. False when the stand-in was disposed while the
    // rebuild ran: nobody will drive the controller, so the caller disposes it.
    private bool Adopt(IFanDevice built) {
        var connection = new Connection(built, _channels, _fanSpeeds, _temperatures);
        lock (_lock) {
            _building = false;

            // Connected already: a controller offered by a late scan build and one from this
            // stand-in's own rebuild can both arrive, and only the first is kept.
            if (_disposed || _connection != null) {
                return false;
            }

            _connection = connection;
            if (_reconnectReplay != null) {
                built.ReplayOnReconnect(_reconnectReplay);
            }

            for (int ch = 0; ch < _channels.Length; ch++) {
                if (_target[ch] >= 0) {
                    connection.SetTarget(ch, _target[ch]);
                }
            }
        }

        _log.Write(_attempts == 0
            ? string.Format(
                CultureInfo.InvariantCulture,
                "C{0} kept all {1} remembered channel(s); {2} of them are there now",
                _index,
                _channels.Length,
                connection.MatchedChannels)
            : string.Format(
                CultureInfo.InvariantCulture,
                "C{0} rebuilt after {1} attempt(s): {2} of {3} remembered channel(s) matched",
                _index,
                _attempts,
                connection.MatchedChannels,
                _channels.Length));
        Adopted?.Invoke(this, built);
        return true;
    }

    // The rebuilt controller and how the remembered sensors map onto it, fixed when it is adopted.
    // A remembered channel or temperature the controller no longer has maps to nothing, and reads
    // as no reading.
    private sealed class Connection {
        private readonly int[] _channelMap; // remembered channel -> the controller's, -1 when it has none
        private readonly Func<float>[] _fanSpeeds;
        private readonly Func<float?>[] _temperatures;
        private readonly int _sensorCount;

        public Connection(
            IFanDevice inner, ChannelDescriptor[] channels, FanSpeedDescriptor[] fanSpeeds, TemperatureDescriptor[] temperatures) {
            Inner = inner;
            _channelMap = new int[channels.Length];
            for (int ch = 0; ch < channels.Length; ch++) {
                _channelMap[ch] = FindChannel(inner, channels[ch].ControlId);
                if (_channelMap[ch] >= 0) {
                    MatchedChannels++;
                }
            }

            _fanSpeeds = new Func<float>[fanSpeeds.Length];
            for (int f = 0; f < fanSpeeds.Length; f++) {
                _fanSpeeds[f] = FanSpeedReader(inner, fanSpeeds[f].Id);
            }

            _temperatures = new Func<float?>[temperatures.Length];
            for (int t = 0; t < temperatures.Length; t++) {
                _temperatures[t] = TemperatureReader(inner as ITemperatureSource, temperatures[t].Id);
            }

            _sensorCount = SensorCount(inner);
        }

        // Whether the controller has gained sensors since this was matched.
        public bool IsBehind => SensorCount(Inner) != _sensorCount;

        public IFanDevice Inner { get; }

        public int MatchedChannels { get; }

        public void SetTarget(int channel, int duty) {
            if (_channelMap[channel] >= 0) {
                Inner.SetTarget(_channelMap[channel], duty);
            }
        }

        public void ReleaseChannel(int channel) {
            if (_channelMap[channel] >= 0) {
                Inner.ReleaseChannel(_channelMap[channel]);
            }
        }

        public float GetRpm(int channel) => _channelMap[channel] >= 0 ? Inner.GetRpm(_channelMap[channel]) : 0f;

        public float FanSpeed(int index) => _fanSpeeds[index]();

        public float? Temperature(int index) => _temperatures[index]();

        private static int SensorCount(IFanDevice inner)
            => inner.ChannelCount
                + ((inner as IFanSpeedSource)?.FanSpeedCount ?? 0)
                + ((inner as ITemperatureSource)?.TemperatureCount ?? 0);

        private static int FindChannel(IFanDevice inner, string controlId) {
            for (int ch = 0; ch < inner.ChannelCount; ch++) {
                if (inner.Describe(ch).ControlId == controlId) {
                    return ch;
                }
            }

            return -1;
        }

        // A fan speed is found by its id among the controller's own speed readings, or, for a
        // controller that reports one per channel, among its channels' RPM identities.
        private static Func<float> FanSpeedReader(IFanDevice inner, string id) {
            if (inner is IFanSpeedSource speeds) {
                for (int f = 0; f < speeds.FanSpeedCount; f++) {
                    if (speeds.DescribeFanSpeed(f).Id == id) {
                        int found = f;
                        return () => speeds.GetFanSpeed(found);
                    }
                }
            } else {
                for (int ch = 0; ch < inner.ChannelCount; ch++) {
                    if (inner.Describe(ch).RpmId == id) {
                        int found = ch;
                        return () => inner.GetRpm(found);
                    }
                }
            }

            return () => 0f;
        }

        private static Func<float?> TemperatureReader(ITemperatureSource? source, string id) {
            if (source != null) {
                for (int t = 0; t < source.TemperatureCount; t++) {
                    if (source.DescribeTemperature(t).Id == id) {
                        int found = t;
                        return () => source.GetTemperature(found);
                    }
                }
            }

            return () => null;
        }
    }
}
