using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

/// <summary>
/// The interface-selection rule for the 0x0416 command-packet family: keep only its command-page
/// interface, never filter the Uni family, and keep an interface whose page could not be probed.
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
    [InlineData(0x0CF2, 0x000C, true)]  // Uni interface is always kept, any page
    [InlineData(0x0CF2, null, true)]
    [InlineData(0x0416, 0xFF1B, true)]  // the command page is kept
    [InlineData(0x0416, 0x000C, false)] // a non-command 0x0416 interface is dropped
    [InlineData(0x0416, 0x0001, false)]
    [InlineData(0x0416, null, true)]    // unknown page -> kept, falls back to output-report dedup
    public void Keep_FiltersOnlyKnownNonCommandPagesOfTheCommandVendor(int vendorId, int? usagePage, bool expected) {
        Assert.Equal(expected, CommandInterfaceFilter.Keep(vendorId, usagePage));
    }
}
