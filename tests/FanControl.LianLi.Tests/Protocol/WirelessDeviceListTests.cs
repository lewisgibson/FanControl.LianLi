using System;
using FanControl.LianLi.Protocol;
using Xunit;

namespace FanControl.LianLi.Tests.Protocol;

public sealed class WirelessDeviceListTests {
    [Fact]
    public void Constructor_KeepsTheTotalAndTheRecords() {
        var records = Array.Empty<WirelessDeviceRecord>();

        var list = new WirelessDeviceList(12, records);

        Assert.Equal(12, list.Total);
        Assert.Same(records, list.Records);
    }

    [Fact]
    public void Constructor_RejectsMissingRecords()
        => Assert.Throws<ArgumentNullException>(() => new WirelessDeviceList(0, null!));
}
