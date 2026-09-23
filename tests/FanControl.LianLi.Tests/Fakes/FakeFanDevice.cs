using System;
using System.Collections.Generic;
using FanControl.LianLi.Devices;

namespace FanControl.LianLi.Tests.Fakes;

/// <summary>
/// A controller with no device behind it: the channels and temperatures it exposes are whatever the
/// test names, and every call is recorded. Used to drive the parts of the plugin that only care
/// about the <see cref="IFanDevice"/> contract.
/// </summary>
internal sealed class FakeFanDevice : IFanDevice, ITemperatureSource {
    private readonly ChannelDescriptor[] _channels;
    private readonly TemperatureDescriptor[] _temperatures;
    private readonly float[] _rpm;
    private readonly float?[] _temperature;

    public FakeFanDevice(params string[] channelIds) {
        _channels = new ChannelDescriptor[channelIds.Length];
        for (int ch = 0; ch < channelIds.Length; ch++) {
            _channels[ch] = new ChannelDescriptor(channelIds[ch], channelIds[ch], channelIds[ch] + "/fan", channelIds[ch]);
        }

        _temperatures = Array.Empty<TemperatureDescriptor>();
        _rpm = new float[channelIds.Length];
        _temperature = Array.Empty<float?>();
    }

    public FakeFanDevice(string[] channelIds, string[] temperatureIds)
        : this(channelIds) {
        _temperatures = new TemperatureDescriptor[temperatureIds.Length];
        for (int t = 0; t < temperatureIds.Length; t++) {
            _temperatures[t] = new TemperatureDescriptor(temperatureIds[t], temperatureIds[t]);
        }

        _temperature = new float?[temperatureIds.Length];
    }

    /// <summary>Targets set on this device, in order, as (channel, duty).</summary>
    public List<KeyValuePair<int, int>> Targets { get; } = new List<KeyValuePair<int, int>>();

    /// <summary>Channels released on this device, in order.</summary>
    public List<int> Released { get; } = new List<int>();

    public int ApplyCount { get; private set; }

    public int PollCount { get; private set; }

    public bool IsDisposed { get; private set; }

    /// <summary>When set, <see cref="Dispose"/> throws this after marking the device disposed.</summary>
    public Exception? DisposeFault { get; set; }

    /// <summary>The replay the owner registered, if any.</summary>
    public Action? Replay { get; private set; }

    /// <summary>When set, a channel whose index is in here reports as unpopulated.</summary>
    public HashSet<int> UnpopulatedChannels { get; } = new HashSet<int>();

    public int ChannelCount => _channels.Length;

    public int TemperatureCount => _temperatures.Length;

    /// <summary>When set, <see cref="IsChannelPopulated"/> throws this.</summary>
    public Exception? PopulationFault { get; set; }

    public bool IsChannelPopulated(int channel) => PopulationFault is null ? !UnpopulatedChannels.Contains(channel) : throw PopulationFault;

    public ChannelDescriptor Describe(int channel) => _channels[channel];

    public TemperatureDescriptor DescribeTemperature(int index) => _temperatures[index];

    public float? GetTemperature(int index) => _temperature[index];

    /// <summary>Set the value <see cref="GetTemperature"/> will report.</summary>
    public void SetTemperature(int index, float? value) => _temperature[index] = value;

    /// <summary>Set the value <see cref="GetRpm"/> will report.</summary>
    public void SetRpm(int channel, float value) => _rpm[channel] = value;

    public void SetTarget(int channel, int duty) => Targets.Add(new KeyValuePair<int, int>(channel, duty));

    public void ReleaseChannel(int channel) => Released.Add(channel);

    public float GetRpm(int channel) => _rpm[channel];

    public void ApplyPending() => ApplyCount++;

    public void PollRpm() => PollCount++;

    public void ReplayOnReconnect(Action replay) => Replay = replay;

    public void Dispose() {
        IsDisposed = true;
        if (DisposeFault != null) {
            throw DisposeFault;
        }
    }
}
