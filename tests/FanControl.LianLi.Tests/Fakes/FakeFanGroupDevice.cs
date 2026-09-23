using System;
using System.Collections.Generic;
using FanControl.LianLi.Devices;

namespace FanControl.LianLi.Tests.Fakes;

// A device with fewer controls than fans, as a wireless group is: one control, a speed per fan.
internal sealed class FakeFanGroupDevice : IFanDevice, IFanSpeedSource {
    private readonly string _controlId;
    private readonly string[] _fanIds;
    private readonly float[] _speeds;

    public FakeFanGroupDevice(string controlId, params string[] fanIds) {
        _controlId = controlId;
        _fanIds = fanIds;
        _speeds = new float[fanIds.Length];
    }

    public int ChannelCount => 1;

    public int FanSpeedCount => _fanIds.Length;

    public List<KeyValuePair<int, int>> Targets { get; } = new List<KeyValuePair<int, int>>();

    public void SetSpeed(int fan, float rpm) => _speeds[fan] = rpm;

    public bool IsChannelPopulated(int channel) => true;

    public ChannelDescriptor Describe(int channel) => new ChannelDescriptor(_controlId, _controlId, _controlId + "/fan", _controlId);

    public FanSpeedDescriptor DescribeFanSpeed(int index) => new FanSpeedDescriptor(_fanIds[index], _fanIds[index] + " RPM");

    public float GetFanSpeed(int index) => _speeds[index];

    public void SetTarget(int channel, int duty) => Targets.Add(new KeyValuePair<int, int>(channel, duty));

    public void ReleaseChannel(int channel) {
    }

    public float GetRpm(int channel) => 0f;

    public void ApplyPending() {
    }

    public void PollRpm() {
    }

    public void ReplayOnReconnect(Action replay) {
    }

    public void Dispose() {
    }
}
