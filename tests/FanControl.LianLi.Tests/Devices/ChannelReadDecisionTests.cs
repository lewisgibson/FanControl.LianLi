using FanControl.LianLi.Devices;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

public class ChannelReadDecisionTests {
    [Fact]
    public void MaxPlausibleRpm_IsSixThousand()
        => Assert.Equal(6000f, ChannelReadDecision.MaxPlausibleRpm);

    [Theory]
    [InlineData(0f)]      // a stopped or unpopulated channel
    [InlineData(250f)]
    [InlineData(1500f)]
    [InlineData(2100f)]   // the SL-Infinity top speed
    [InlineData(6000f)]   // the bound is inclusive
    public void IsPlausible_TrueForRealReadings(float rpm)
        => Assert.True(ChannelReadDecision.IsPlausible(rpm));

    [Theory]
    [InlineData(6001f)]
    [InlineData(50000f)]  // the documented post-hibernate idle buffer (~0xC350)
    [InlineData(65535f)]  // the largest a 16-bit big-endian decode can produce
    [InlineData(-1f)]
    public void IsPlausible_FalseForGarbage(float rpm)
        => Assert.False(ChannelReadDecision.IsPlausible(rpm));

    [Fact]
    public void Describe_NamesTheReading_OrNoReading() {
        Assert.Equal("1500", ChannelReadDecision.Describe(1500f));
        Assert.Equal("0", ChannelReadDecision.Describe(0f));
        Assert.Equal("no reading", ChannelReadDecision.Describe(null));
    }
}
