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
/// driver has to be told this way. It names the fan sensor whose reading stands for it,
/// which FanControl reads once, when the control is registered.
/// </summary>
internal sealed class ControlSensor : IPluginControlSensor2 {
    private readonly IFanDevice _controller;
    private readonly int _channel;
    private readonly Action _changed;
    private readonly WirelessProcessState _chains;
    private readonly string? _chain;
    private float? _commanded;

    public ControlSensor(IFanDevice controller, int channel, Action changed, WirelessProcessState chains, string? pairedFanSensorId) {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _channel = channel;
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        _chains = chains ?? throw new ArgumentNullException(nameof(chains));
        ChannelDescriptor descriptor = controller.Describe(channel);
        Id = descriptor.ControlId;
        Name = descriptor.ControlName;
        _chain = WirelessSensorIds.TryChainAddress(Id);
        PairedFanSensorId = pairedFanSensorId;
    }

    public string Id { get; }

    public string Name { get; }

    public float? Value { get; private set; }

    // FanControl's own identifier of the fan sensor whose reading stands for this control: the
    // plugin's name, a slash and that sensor's id, which is how FanControl identifies a plugin's
    // sensor. FanControl reads it once, when the control is registered, and pairs the two unless the
    // user has paired the control themselves. Null when no such reading is registered, which
    // FanControl takes as no pairing.
    public string? PairedFanSensorId { get; }

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
