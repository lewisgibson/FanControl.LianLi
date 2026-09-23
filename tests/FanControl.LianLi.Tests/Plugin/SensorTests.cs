using System;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Plugin;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Tests.Fakes;
using Xunit;

namespace FanControl.LianLi.Tests.Plugin;

public class SensorTests {
    private static (FanController controller, FakeDeviceTransport transport) NewController() {
        var transport = new FakeDeviceTransport();
        var controller = new FanController(0, transport, new SlProtocol(), new bool[4], new FakeClock(), new FakeLogger());
        return (controller, transport);
    }

    [Fact]
    public void ControlSensor_Set_DrivesTargetAndPublishesValue() {
        var (controller, transport) = NewController();
        var control = new ControlSensor(controller, 0, () => { });

        control.Set(50);
        transport.Clear();
        controller.ApplyPending();

        // SL is a v1 family: duty 50 is sent raw as byte 50, as a feature report.
        Assert.Equal(new byte[] { 224, 32, 0, 50 }, transport.Features[1]);

        control.Update();
        Assert.Equal(50f, control.Value);
    }

    [Fact]
    public void ControlSensor_Reset_ReleasesChannelAndClearsValue() {
        var (controller, transport) = NewController();
        var control = new ControlSensor(controller, 0, () => { });
        control.Set(50);
        controller.ApplyPending();

        control.Reset();
        control.Update();
        Assert.Null(control.Value);

        transport.Clear();
        controller.ApplyPending();
        Assert.Empty(transport.Features); // released channel is no longer asserted
    }

    [Fact]
    public void FanSensor_Update_PublishesDecodedRpm() {
        var (controller, transport) = NewController();
        var buffer = new byte[65];
        buffer[1] = 0x0A; // ch0 high
        buffer[2] = 0x28; // ch0 low -> 2600
        transport.InputReport = buffer;
        controller.PollRpm();

        var fan = new FanSensor(controller, 0);
        fan.Update();

        Assert.Equal(2600f, fan.Value);
    }

    [Fact]
    public void ControlAndFanSensor_HaveDistinctIds() {
        var (controller, _) = NewController();
        var control = new ControlSensor(controller, 0, () => { });
        var fan = new FanSensor(controller, 0);

        Assert.NotEqual(control.Id, fan.Id);
    }

    [Fact]
    public void TemperatureSensor_PublishesTheCachedReading() {
        var device = new FakeFanDevice(new[] { "ctl/0" }, new[] { "LianLi/w0/coolant/temp" });
        var sensor = new TemperatureSensor(device, 0);

        Assert.Equal("LianLi/w0/coolant/temp", sensor.Id);
        Assert.Equal("LianLi/w0/coolant/temp", sensor.Name);
        Assert.Null(sensor.Value); // nothing read yet

        device.SetTemperature(0, 31.5f);
        sensor.Update();

        Assert.Equal(31.5f, sensor.Value);
    }

    [Fact]
    public void TemperatureSensor_NullSource_Throws()
        => Assert.Throws<ArgumentNullException>(() => new TemperatureSensor(null!, 0));

    [Fact]
    public void ControlAndFanSensor_CarryTheControllersNames() {
        var (controller, _) = NewController();
        ChannelDescriptor descriptor = controller.Describe(0);

        Assert.Equal(descriptor.ControlName, new ControlSensor(controller, 0, () => { }).Name);
        Assert.Equal(descriptor.RpmName, new FanSensor(controller, 0).Name);
    }

    [Fact]
    public void ControlSensor_WakesTheWorkerOnlyWhenTheTargetChanges() {
        var (controller, _) = NewController();
        int wakes = 0;
        var control = new ControlSensor(controller, 0, () => wakes++);

        control.Set(40);
        control.Set(40); // FanControl re-sends the same value every update
        control.Set(55);

        Assert.Equal(2, wakes);
    }

    [Fact]
    public void ControlSensor_NullWake_Throws() {
        var (controller, _) = NewController();

        Assert.Throws<ArgumentNullException>(() => new ControlSensor(controller, 0, null!));
    }

    [Fact]
    public void FanSensor_NullController_Throws()
        => Assert.Throws<ArgumentNullException>(() => new FanSensor(null!, 0));

    [Fact]
    public void ControlSensor_NullController_Throws()
        => Assert.Throws<ArgumentNullException>(() => new ControlSensor(null!, 0, () => { }));
}
