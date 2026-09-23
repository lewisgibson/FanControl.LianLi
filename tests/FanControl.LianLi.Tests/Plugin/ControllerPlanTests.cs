using System;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Plugin;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Plugin;

public sealed class ControllerPlanTests {
    private static LocatedDevice Device(string path) => new LocatedDevice(0x0416, 0x8040, path, null);

    [Fact]
    public void AWiredPlanRunsOverOneDevice_KeyedByItsPath() {
        var plan = new ControllerPlan(DeviceKind.UniFan, Device("hid/a"));

        Assert.Equal(DeviceKind.UniFan, plan.Kind);
        Assert.Equal("hid/a", plan.Key);
        Assert.Single(plan.Devices);
    }

    [Fact]
    public void AWirelessPlanRunsOverTheTransmitterThenTheReceiver_KeyedByTheTransmitter() {
        var plan = new ControllerPlan(DeviceKind.WirelessTransmitter, Device("usb/tx"), Device("usb/rx"));

        Assert.Equal("usb/tx", plan.Key);
        Assert.Equal("usb/rx", plan.Devices[1].DevicePath);
    }

    [Fact]
    public void ThePlanKeepsItsOwnCopyOfTheDevices() {
        LocatedDevice[] devices = { Device("hid/a") };
        var plan = new ControllerPlan(DeviceKind.TlFan, devices);

        devices[0] = Device("hid/b");

        Assert.Equal("hid/a", plan.Key);
    }

    [Fact]
    public void RejectsTheWrongNumberOfDevices() {
        Assert.Throws<ArgumentNullException>(() => new ControllerPlan(DeviceKind.UniFan, null!));
        Assert.Throws<ArgumentException>(() => new ControllerPlan(DeviceKind.UniFan, Device("a"), Device("b")));
        Assert.Throws<ArgumentException>(() => new ControllerPlan(DeviceKind.WirelessTransmitter, Device("a")));
    }
}
