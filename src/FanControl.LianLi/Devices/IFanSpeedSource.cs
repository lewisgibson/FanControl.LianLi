namespace FanControl.LianLi.Devices;

/// <summary>
/// A device that reports more fan speeds than it has controls - today the wireless devices, where
/// one control drives a whole fan group (L-Connect repeats one duty across the group's four slots)
/// but every fan reports its own RPM. Optional beside <see cref="IFanDevice"/>, like
/// <see cref="ITemperatureSource"/>: for a device that implements it the plugin registers a fan
/// sensor per reading here instead of one per channel, and the other families do not implement it.
/// The count only grows during a run, so an index keeps naming the same fan.
/// </summary>
internal interface IFanSpeedSource {
    /// <summary>How many fan speed readings the device exposes.</summary>
    int FanSpeedCount { get; }

    /// <summary>The stable sensor identity for reading <paramref name="index"/>.</summary>
    FanSpeedDescriptor DescribeFanSpeed(int index);

    /// <summary>The last measured speed in RPM; 0 while the fan is not being heard.</summary>
    float GetFanSpeed(int index);
}
