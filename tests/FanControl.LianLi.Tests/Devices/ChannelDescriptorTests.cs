using System;
using FanControl.LianLi.Devices;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

public sealed class ChannelDescriptorTests {
    [Fact]
    public void CarriesTheFourIdentities() {
        var descriptor = new ChannelDescriptor("ctl", "Control", "fan", "Fan RPM");

        Assert.Equal("ctl", descriptor.ControlId);
        Assert.Equal("Control", descriptor.ControlName);
        Assert.Equal("fan", descriptor.RpmId);
        Assert.Equal("Fan RPM", descriptor.RpmName);
    }

    [Fact]
    public void RejectsAMissingIdentity() {
        Assert.Throws<ArgumentNullException>(() => new ChannelDescriptor(null!, "b", "c", "d"));
        Assert.Throws<ArgumentNullException>(() => new ChannelDescriptor("a", null!, "c", "d"));
        Assert.Throws<ArgumentNullException>(() => new ChannelDescriptor("a", "b", null!, "d"));
        Assert.Throws<ArgumentNullException>(() => new ChannelDescriptor("a", "b", "c", null!));
    }
}
