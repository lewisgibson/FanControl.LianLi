using System;
using FanControl.LianLi.Devices;
using FanControl.Plugins;

namespace FanControl.LianLi.Plugin;

/// <summary>
/// Writable control for one channel. <see cref="Set"/> only hands the target to
/// the controller's in-memory state and, when it changed, wakes the worker so the
/// USB write happens at once rather than at the next tick; <see cref="Reset"/>
/// releases the channel so the keepalive stops asserting it. Its id is distinct
/// from the matching fan sensor's to avoid a registry collision.
/// </summary>
internal sealed class ControlSensor : IPluginControlSensor {
    private readonly IFanDevice _controller;
    private readonly int _channel;
    private readonly Action _changed;
    private float? _commanded;

    public ControlSensor(IFanDevice controller, int channel, Action changed) {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _channel = channel;
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        ChannelDescriptor descriptor = controller.Describe(channel);
        Id = descriptor.ControlId;
        Name = descriptor.ControlName;
    }

    public string Id { get; }

    public string Name { get; }

    public float? Value { get; private set; }

    public void Update() => Value = _commanded;

    // FanControl calls Set every update with whatever its curve says, usually the same value, so
    // the worker is only woken for a value that differs from the last one.
    public void Set(float val) {
        _controller.SetTarget(_channel, (int)val);
        bool changed = _commanded != val;
        _commanded = val;
        if (changed) {
            _changed();
        }
    }

    public void Reset() {
        _controller.ReleaseChannel(_channel);
        _commanded = null;
    }
}
