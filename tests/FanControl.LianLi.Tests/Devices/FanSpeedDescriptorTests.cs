using System;
using FanControl.LianLi.Devices;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

public class FanSpeedDescriptorTests {
    [Fact]
    public void Constructor_KeepsBothStrings() {
        var descriptor = new FanSpeedDescriptor("id", "name");

        Assert.Equal("id", descriptor.Id);
        Assert.Equal("name", descriptor.Name);
    }

    [Fact]
    public void Constructor_RejectsAMissingString() {
        Assert.Throws<ArgumentNullException>(() => new FanSpeedDescriptor(null!, "name"));
        Assert.Throws<ArgumentNullException>(() => new FanSpeedDescriptor("id", null!));
    }
}
