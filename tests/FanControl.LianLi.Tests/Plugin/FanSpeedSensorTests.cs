using System;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Plugin;
using Xunit;

namespace FanControl.LianLi.Tests.Plugin;

public class FanSpeedSensorTests {
    private sealed class OneFan : IFanSpeedSource {
        public float Speed { get; set; }

        public int FanSpeedCount => 1;

        public FanSpeedDescriptor DescribeFanSpeed(int index) => new FanSpeedDescriptor("LianLi/wa00000000001/f0/fan", "Fan 1 RPM");

        public float GetFanSpeed(int index) => Speed;
    }

    [Fact]
    public void Update_PublishesTheSourcesSpeed() {
        var source = new OneFan { Speed = 1234 };
        var sensor = new FanSpeedSensor(source, 0);

        Assert.Equal("LianLi/wa00000000001/f0/fan", sensor.Id);
        Assert.Equal("Fan 1 RPM", sensor.Name);
        Assert.Null(sensor.Value);
        sensor.Update();
        Assert.Equal(1234f, sensor.Value);
    }

    [Fact]
    public void Constructor_RejectsAMissingSource()
        => Assert.Throws<ArgumentNullException>(() => new FanSpeedSensor(null!, 0));
}
