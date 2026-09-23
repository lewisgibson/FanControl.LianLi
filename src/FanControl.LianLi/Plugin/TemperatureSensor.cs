using System;
using FanControl.LianLi.Devices;
using FanControl.Plugins;

namespace FanControl.LianLi.Plugin;

/// <summary>
/// Read-only temperature sensor for one reading of an <see cref="ITemperatureSource"/>.
/// <see cref="Update"/> publishes the cached value the worker last measured; it performs no I/O.
/// </summary>
internal sealed class TemperatureSensor : IPluginSensor {
    private readonly ITemperatureSource _source;
    private readonly int _index;

    public TemperatureSensor(ITemperatureSource source, int index) {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _index = index;
        TemperatureDescriptor descriptor = source.DescribeTemperature(index);
        Id = descriptor.Id;
        Name = descriptor.Name;
    }

    public string Id { get; }

    public string Name { get; }

    public float? Value { get; private set; }

    public void Update() => Value = _source.GetTemperature(_index);
}
