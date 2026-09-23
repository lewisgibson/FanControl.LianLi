using System;
using System.Collections.Generic;
using FanControl.LianLi.Devices;

namespace FanControl.LianLi.Plugin;

/// <summary>
/// What a later scan needs to stand in for a controller it cannot rebuild: the plan it was built
/// from, its index (the wired sensor ids are keyed on it), and the sensors it registered - the
/// populated channels, the fan speed readings and any temperatures - so the stand-in registers
/// exactly the same ones. Each sensor carries when a build last reported it, so one that stops
/// being reported (a fan unplugged, a wireless group unbound) is kept - a stopped fan must not lose
/// its curve - but only for as long as a controller no scan builds is.
/// </summary>
internal sealed class RememberedController {
    /// <summary>
    /// Remember <paramref name="controller"/>, built from <paramref name="plan"/> at
    /// <paramref name="index"/>, with every sensor it reports seen at <paramref name="seenUtc"/>.
    /// </summary>
    public RememberedController(ControllerPlan plan, int index, IFanDevice controller, DateTime seenUtc) {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        if (controller is null) {
            throw new ArgumentNullException(nameof(controller));
        }

        Index = index;
        var channels = new List<ChannelDescriptor>();
        for (int ch = 0; ch < controller.ChannelCount; ch++) {
            if (controller.IsChannelPopulated(ch)) {
                channels.Add(controller.Describe(ch));
            }
        }

        Channels = channels;

        // A controller with fewer controls than fans reports each fan's speed on its own; every
        // other one reports one speed per channel, under the channel's RPM identity.
        var fanSpeeds = new List<FanSpeedDescriptor>();
        if (controller is IFanSpeedSource speeds) {
            for (int f = 0; f < speeds.FanSpeedCount; f++) {
                fanSpeeds.Add(speeds.DescribeFanSpeed(f));
            }
        } else {
            foreach (ChannelDescriptor channel in channels) {
                fanSpeeds.Add(new FanSpeedDescriptor(channel.RpmId, channel.RpmName));
            }
        }

        FanSpeeds = fanSpeeds;
        var temperatures = new List<TemperatureDescriptor>();
        if (controller is ITemperatureSource source) {
            for (int t = 0; t < source.TemperatureCount; t++) {
                temperatures.Add(source.DescribeTemperature(t));
            }
        }

        Temperatures = temperatures;
        var seen = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (string id in SensorIds()) {
            seen[id] = seenUtc;
        }

        _seenUtc = seen;
    }

    /// <summary>
    /// A controller remembered as it was saved: its plan, index and sensors, and when each sensor
    /// was last reported, keyed by its id (<see cref="LastSeenUtc"/>); every sensor must have one.
    /// </summary>
    public RememberedController(
        ControllerPlan plan,
        int index,
        IReadOnlyList<ChannelDescriptor> channels,
        IReadOnlyList<FanSpeedDescriptor> fanSpeeds,
        IReadOnlyList<TemperatureDescriptor> temperatures,
        IReadOnlyDictionary<string, DateTime> seenUtc) {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Index = index;
        Channels = channels ?? throw new ArgumentNullException(nameof(channels));
        FanSpeeds = fanSpeeds ?? throw new ArgumentNullException(nameof(fanSpeeds));
        Temperatures = temperatures ?? throw new ArgumentNullException(nameof(temperatures));
        if (seenUtc is null) {
            throw new ArgumentNullException(nameof(seenUtc));
        }

        var seen = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (string id in SensorIds()) {
            seen[id] = seenUtc.TryGetValue(id, out DateTime when)
                ? when
                : throw new ArgumentException("Sensor " + id + " has no last-seen time.", nameof(seenUtc));
        }

        _seenUtc = seen;
    }

    private readonly Dictionary<string, DateTime> _seenUtc;

    /// <summary>The plan the controller was built from.</summary>
    public ControllerPlan Plan { get; }

    /// <summary>The index the controller was built at, reserved for it for the rest of the process.</summary>
    public int Index { get; }

    /// <summary>The populated channels it registered, in order.</summary>
    public IReadOnlyList<ChannelDescriptor> Channels { get; }

    /// <summary>The fan speed readings it registered, in order.</summary>
    public IReadOnlyList<FanSpeedDescriptor> FanSpeeds { get; }

    /// <summary>The temperatures it registered, in order.</summary>
    public IReadOnlyList<TemperatureDescriptor> Temperatures { get; }

    /// <summary>
    /// The same controller, reached through <paramref name="plan"/> instead: it came back on another
    /// path (another USB port, or a hub or driver change), and keeps its index and sensors.
    /// </summary>
    public RememberedController MovedTo(ControllerPlan plan)
        => new RememberedController(plan, Index, Channels, FanSpeeds, Temperatures, _seenUtc);

    /// <summary>When a build last reported the sensor <paramref name="id"/> (a control, fan speed or temperature id).</summary>
    public DateTime LastSeenUtc(string id) => _seenUtc[id];

    /// <summary>
    /// Whether this controller registered every sensor <paramref name="earlier"/> did: when not,
    /// the controller came back with something missing (a device not heard yet, a fan stopped while
    /// it was probed), and the sensors registered for it are the union of the two.
    /// </summary>
    public bool Covers(RememberedController earlier) {
        if (earlier is null) {
            throw new ArgumentNullException(nameof(earlier));
        }

        return Contains(Channels, earlier.Channels, c => c.ControlId)
            && Contains(FanSpeeds, earlier.FanSpeeds, f => f.Id)
            && Contains(Temperatures, earlier.Temperatures, t => t.Id);
    }

    /// <summary>
    /// Every sensor either registered: <paramref name="earlier"/>'s first, in their order, then any
    /// this one adds - except one of <paramref name="earlier"/>'s this one lacks that no build has
    /// reported for longer than <paramref name="forgetAfter"/> at <paramref name="nowUtc"/>. So a
    /// device that comes back incomplete does not shrink what the user's curves are bound to, and
    /// hardware that is gone for good stops showing as a fan reading nothing.
    /// </summary>
    public RememberedController MergedWith(RememberedController earlier, DateTime nowUtc, TimeSpan forgetAfter) {
        if (earlier is null) {
            throw new ArgumentNullException(nameof(earlier));
        }

        var seen = new Dictionary<string, DateTime>(_seenUtc, StringComparer.Ordinal);
        foreach (KeyValuePair<string, DateTime> sensor in earlier._seenUtc) {
            if (!seen.ContainsKey(sensor.Key) && !ClockSpan.IsOlderThan(nowUtc, sensor.Value, forgetAfter)) {
                seen[sensor.Key] = sensor.Value;
            }
        }

        return new RememberedController(
            Plan,
            Index,
            Union(Kept(earlier.Channels, c => c.ControlId, seen), Channels, c => c.ControlId),
            Union(Kept(earlier.FanSpeeds, f => f.Id, seen), FanSpeeds, f => f.Id),
            Union(Kept(earlier.Temperatures, t => t.Id, seen), Temperatures, t => t.Id),
            seen);
    }

    /// <summary>
    /// This controller without the sensors no build has reported for longer than
    /// <paramref name="forgetAfter"/> at <paramref name="nowUtc"/>.
    /// </summary>
    public RememberedController Pruned(DateTime nowUtc, TimeSpan forgetAfter) {
        var seen = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, DateTime> sensor in _seenUtc) {
            if (!ClockSpan.IsOlderThan(nowUtc, sensor.Value, forgetAfter)) {
                seen[sensor.Key] = sensor.Value;
            }
        }

        return new RememberedController(
            Plan,
            Index,
            Kept(Channels, c => c.ControlId, seen),
            Kept(FanSpeeds, f => f.Id, seen),
            Kept(Temperatures, t => t.Id, seen),
            seen);
    }

    // Every sensor id, as the unions key them.
    private IEnumerable<string> SensorIds() {
        foreach (ChannelDescriptor channel in Channels) {
            yield return channel.ControlId;
        }

        foreach (FanSpeedDescriptor speed in FanSpeeds) {
            yield return speed.Id;
        }

        foreach (TemperatureDescriptor temperature in Temperatures) {
            yield return temperature.Id;
        }
    }

    private static bool Contains<T>(IReadOnlyList<T> have, IReadOnlyList<T> wanted, Func<T, string> id) {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (T item in have) {
            _ = ids.Add(id(item));
        }

        foreach (T item in wanted) {
            if (!ids.Contains(id(item))) {
                return false;
            }
        }

        return true;
    }

    // first, then each of second whose id first lacks.
    private static List<T> Union<T>(IReadOnlyList<T> first, IReadOnlyList<T> second, Func<T, string> id) {
        var union = new List<T>(first);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (T item in first) {
            _ = ids.Add(id(item));
        }

        foreach (T item in second) {
            if (ids.Add(id(item))) {
                union.Add(item);
            }
        }

        return union;
    }

    // The items whose id is in kept, in order.
    private static List<T> Kept<T>(IReadOnlyList<T> items, Func<T, string> id, Dictionary<string, DateTime> kept) {
        var result = new List<T>();
        foreach (T item in items) {
            if (kept.ContainsKey(id(item))) {
                result.Add(item);
            }
        }

        return result;
    }
}
