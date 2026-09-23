namespace FanControl.LianLi.Devices;

/// <summary>
/// A device that also measures temperatures - today the wireless water blocks, which report their
/// coolant temperature in the receiver's list. Optional beside <see cref="IFanDevice"/>: the plugin
/// registers a temperature sensor per reading for any controller that implements it, and the other
/// families do not.
/// </summary>
internal interface ITemperatureSource {
    /// <summary>How many temperature readings the device exposes.</summary>
    int TemperatureCount { get; }

    /// <summary>The stable sensor identity for reading <paramref name="index"/>.</summary>
    TemperatureDescriptor DescribeTemperature(int index);

    /// <summary>The last measured temperature in degrees Celsius, or null when none has been read yet.</summary>
    float? GetTemperature(int index);
}
