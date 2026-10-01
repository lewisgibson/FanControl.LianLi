using System;
using FanControl.LianLi.Devices;
using FanControl.Plugins;

namespace FanControl.LianLi.Plugin;

/// <summary>
/// Writable control for one channel. <see cref="Set"/> only passes the target to
/// the controller's in-memory state and, when it changed, wakes the worker so the
/// USB write happens at once instead of at the next tick; <see cref="Reset"/>
/// releases the channel so the keepalive stops asserting it. Its id is distinct
/// from the matching fan sensor's to avoid a registry collision. The control of a
/// fan group keyed on an RF address also keeps its target in the shared state
/// (<see cref="WirelessProcessState.ChainTarget"/>): the group may be a FLEX chain
/// with two possible drivers, and this control, the one FanControl has for it, may
/// be on a stand-in for a receiver that cannot be reached, which is when the other
/// driver has to be told this way.
/// </summary>
internal sealed class ControlSensor : IPluginControlSensor {
    private readonly IFanDevice _controller;
    private readonly int _channel;
    private readonly Action _changed;
    private readonly WirelessProcessState _chains;
    private readonly string? _chain;
    private float? _commanded;

    public ControlSensor(IFanDevice controller, int channel, Action changed, WirelessProcessState chains) {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _channel = channel;
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        _chains = chains ?? throw new ArgumentNullException(nameof(chains));
        ChannelDescriptor descriptor = controller.Describe(channel);
        Id = descriptor.ControlId;
        Name = descriptor.ControlName;
        _chain = WirelessSensorIds.TryChainAddress(Id);
    }

    public string Id { get; }

    public string Name { get; }

    public float? Value { get; private set; }

    public void Update() => Value = _commanded;

    // FanControl calls Set every update with whatever its curve says, usually the same value, so
    // the worker is only woken for a value that differs from the last one.
    public void Set(float val) {
        _controller.SetTarget(_channel, (int)val);
        if (_chain != null) {
            _chains.SetChainTarget(_chain, (int)val);
        }

        bool changed = _commanded != val;
        _commanded = val;
        if (changed) {
            _changed();
        }
    }

    public void Reset() {
        _controller.ReleaseChannel(_channel);
        if (_chain != null) {
            _chains.ReleaseChainTarget(_chain);
        }

        _commanded = null;
    }
}
