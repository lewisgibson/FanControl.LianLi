using System;

namespace FanControl.LianLi.Devices;

/// <summary>
/// The stable identity of one fan speed reading from an <see cref="IFanSpeedSource"/>. Like
/// <see cref="ChannelDescriptor"/>, the device owns the naming so the id stays byte-stable across
/// restarts.
/// </summary>
internal readonly struct FanSpeedDescriptor {
    /// <summary>Create the identity for one reading. Both strings are required.</summary>
    public FanSpeedDescriptor(string id, string name) {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    /// <summary>The sensor's id.</summary>
    public string Id { get; }

    /// <summary>The sensor's display name.</summary>
    public string Name { get; }
}
