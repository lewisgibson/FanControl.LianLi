using System;
using System.Collections.Generic;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Transport;

namespace FanControl.LianLi.Plugin;

/// <summary>
/// What a scan decided to build: the kind of controller and the located device(s) it runs over
/// (one for a wired controller, the transmitter then the receiver for a wireless pair). It says
/// what to build rather than holding a way to build it, because it outlives the plugin instance
/// that made it: FanControl creates a new plugin object on every refresh, and a remembered plan is
/// rebuilt by whichever instance is current.
/// </summary>
internal sealed class ControllerPlan {
    /// <summary>A plan for <paramref name="kind"/> over <paramref name="devices"/>, keyed by the first device's path.</summary>
    public ControllerPlan(DeviceKind kind, params LocatedDevice[] devices) {
        if (devices is null) {
            throw new ArgumentNullException(nameof(devices));
        }

        int expected = kind == DeviceKind.WirelessTransmitter ? 2 : 1;
        if (devices.Length != expected) {
            throw new ArgumentException(
                "A " + kind + " controller runs over " + expected + " device(s).", nameof(devices));
        }

        Kind = kind;
        Devices = (LocatedDevice[])devices.Clone();
    }

    /// <summary>
    /// The controller kind. A wireless pair is <see cref="DeviceKind.WirelessTransmitter"/>, the
    /// half that carries every command.
    /// </summary>
    public DeviceKind Kind { get; }

    /// <summary>The device(s) the controller opens, in the order the builder takes them.</summary>
    public IReadOnlyList<LocatedDevice> Devices { get; }

    /// <summary>
    /// The identity of the controller across scans: the first device's OS path, which stays the
    /// same for a device on the same port.
    /// </summary>
    public string Key => Devices[0].DevicePath;
}
