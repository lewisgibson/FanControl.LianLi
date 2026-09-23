using System;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

public class HidInterfaceTests {
    [Fact]
    public void Constructor_KeepsWhatItIsGiven() {
        var capabilities = new HidCapabilities(0xFF72, 65, 353, 64);

        var device = new HidInterface(0x0CF2, 0xA102, "path", capabilities);

        Assert.Equal((0x0CF2, 0xA102, "path"), (device.VendorId, device.ProductId, device.DevicePath));
        Assert.Equal(
            (0xFF72, 65, 353, 64),
            (device.Capabilities.UsagePage, device.Capabilities.InputReportLength, device.Capabilities.OutputReportLength, device.Capabilities.FeatureReportLength));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Constructor_WithoutAPath_Throws(string? path)
        => Assert.Throws<ArgumentException>(() => new HidInterface(0x0CF2, 0xA102, path!, default));
}
