using System;

namespace FanControl.LianLi.Devices;

/// <summary>
/// The stable identity of one temperature reading as FanControl sees it. Like
/// <see cref="ChannelDescriptor"/>, the device owns the naming so the id stays byte-stable across
/// restarts - a user's curve may reference it as a source.
/// </summary>
internal readonly struct TemperatureDescriptor {
    /// <summary>Create the identity for one reading. Both strings are required.</summary>
    public TemperatureDescriptor(string id, string name) {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    /// <summary>The sensor's id.</summary>
    public string Id { get; }

    /// <summary>The sensor's display name.</summary>
    public string Name { get; }
}
