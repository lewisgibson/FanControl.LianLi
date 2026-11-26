using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

/// <summary>
/// The interface-selection rule for the 0x0416 command-packet family: keep only its command-page
/// interface (or the Galahad II Vision LCD's single collection), never filter the Uni family, and keep
/// an interface whose page could not be probed.
/// </summary>
public class CommandInterfaceFilterTests {
    [Theory]
    [InlineData(0x0416, true)]  // command-packet family needs filtering
    [InlineData(0x0CF2, false)] // Uni family does not
    [InlineData(0x1234, false)] // anything else does not
    public void RequiresUsageFilter_OnlyForTheCommandPacketVendor(int vendorId, bool expected) {
        Assert.Equal(expected, CommandInterfaceFilter.RequiresUsageFilter(vendorId));
    }

    [Theory]
    [InlineData(0x0CF2, 0xA102, 0x000C, true)]  // Uni interface is always kept, any page
    [InlineData(0x0CF2, 0xA102, null, true)]
    [InlineData(0x0CF2, 0xA102, 0xFF1A, true)]
    [InlineData(0x0416, 0x7372, 0xFF1B, true)]  // the command page is kept
    [InlineData(0x0416, 0x7372, 0x000C, false)] // a non-command 0x0416 interface is dropped
    [InlineData(0x0416, 0x7372, 0x0001, false)]
    [InlineData(0x0416, 0x7372, null, true)]    // unknown page -> kept, falls back to output-report dedup
    [InlineData(0x0416, 0x7391, 0xFF1A, true)]  // the Galahad II Vision LCD's single collection is kept
    [InlineData(0x0416, 0x7395, 0xFF1A, true)]
    [InlineData(0x0416, 0x7395, 0xFF1B, true)]  // and its command page would be too
    [InlineData(0x0416, 0x7395, 0x0001, false)] // any other page of it is still dropped
    [InlineData(0x0416, 0x7373, 0xFF1A, false)] // 0xFF1A is not a command page for any other device
    [InlineData(0x0416, 0x7372, 0xFF1A, false)]
    [InlineData(0x0416, 0x7398, 0xFF1A, false)]
    public void Keep_FiltersOnlyKnownNonCommandPagesOfTheCommandVendor(int vendorId, int productId, int? usagePage, bool expected) {
        Assert.Equal(expected, CommandInterfaceFilter.Keep(vendorId, productId, usagePage));
    }
}
