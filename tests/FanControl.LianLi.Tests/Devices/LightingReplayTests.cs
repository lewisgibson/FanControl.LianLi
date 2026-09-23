#if ENABLE_LIGHTING
using System.Collections.Generic;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Tests.Fakes;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

public sealed class LightingReplayTests
{
    [Fact]
    public void Apply_WritesEveryTransferInOrderRoutedByReportKind()
    {
        var transfers = new List<LightingTransfer>
        {
            new LightingTransfer(isFeature: true, new byte[] { 0xE0, 0x10, 0x60, 0x01, 0x04, 0x00, 0x00 }),  // SetQuantity
            new LightingTransfer(isFeature: false, new byte[] { 0xE0, 0x30, 0xFF, 0x00, 0x00 }),             // colour (output)
            new LightingTransfer(isFeature: true, new byte[] { 0xE0, 0x10, 0x01, 0x00, 0x00, 0x00, 0x00 }),  // effect
            new LightingTransfer(isFeature: true, new byte[] { 0xE0, 0x60, 0x00, 0x01, 0x00, 0x00, 0x00 }),  // SetFrame
        };
        var transport = new FakeDeviceTransport();

        LightingReplay.Apply(transport, transfers);

        // Exact order across both report kinds is preserved.
        Assert.Equal(4, transport.Transfers.Count);
        Assert.True(transport.Transfers[0].Key);
        Assert.False(transport.Transfers[1].Key);
        Assert.True(transport.Transfers[2].Key);
        Assert.True(transport.Transfers[3].Key);

        // Output reports go to Write, feature reports to SetFeature.
        Assert.Single(transport.Writes);
        Assert.Equal(new byte[] { 0xE0, 0x30, 0xFF, 0x00, 0x00 }, transport.Writes[0]);
        Assert.Equal(3, transport.Features.Count);
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x60, 0x01, 0x04, 0x00, 0x00 }, transport.Features[0]);
    }

    [Fact]
    public void Apply_NullArguments_Throw()
    {
        Assert.Throws<System.ArgumentNullException>(() => LightingReplay.Apply(null!, new List<LightingTransfer>()));
        Assert.Throws<System.ArgumentNullException>(() => LightingReplay.Apply(new FakeDeviceTransport(), null!));
    }
}
#endif
