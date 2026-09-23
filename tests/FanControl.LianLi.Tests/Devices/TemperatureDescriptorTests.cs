using System;
using FanControl.LianLi.Devices;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

public sealed class TemperatureDescriptorTests {
    [Fact]
    public void CarriesItsIdentity() {
        var descriptor = new TemperatureDescriptor("temp", "Coolant");

        Assert.Equal("temp", descriptor.Id);
        Assert.Equal("Coolant", descriptor.Name);
    }

    [Fact]
    public void RejectsAMissingIdentity() {
        Assert.Throws<ArgumentNullException>(() => new TemperatureDescriptor(null!, "b"));
        Assert.Throws<ArgumentNullException>(() => new TemperatureDescriptor("a", null!));
    }
}
