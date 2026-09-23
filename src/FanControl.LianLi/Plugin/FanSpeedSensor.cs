using System;
using FanControl.LianLi.Devices;
using FanControl.Plugins;

namespace FanControl.LianLi.Plugin;

/// <summary>
/// Read-only RPM sensor for one reading of an <see cref="IFanSpeedSource"/>.
/// <see cref="Update"/> publishes the cached value the worker last measured; it performs no I/O.
/// </summary>
internal sealed class FanSpeedSensor : IPluginSensor {
    private readonly IFanSpeedSource _source;
    private readonly int _index;

    public FanSpeedSensor(IFanSpeedSource source, int index) {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _index = index;
        FanSpeedDescriptor descriptor = source.DescribeFanSpeed(index);
        Id = descriptor.Id;
        Name = descriptor.Name;
    }

    public string Id { get; }

    public string Name { get; }

    public float? Value { get; private set; }

    public void Update() => Value = _source.GetFanSpeed(_index);
}
