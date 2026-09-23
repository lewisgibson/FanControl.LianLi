using FanControl.LianLi.Devices;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

public class ChannelPopulationDecisionTests {
    [Fact]
    public void Resolve_MajorityPlausible_IsPopulated() {
        // ch0 and ch2 read plausibly on every probe; ch1 and ch3 never did.
        bool[] populated = ChannelPopulationDecision.Resolve(new[] { 6, 0, 6, 0 }, totalReads: 6);

        Assert.Equal(new[] { true, false, true, false }, populated);
    }

    [Fact]
    public void Resolve_StrictMajority_ExactlyHalfIsNotPopulated() {
        // 3 of 6 is not a strict majority - a channel that read plausibly only half the time (e.g.
        // occasional in-range garbage on an empty channel) is treated as empty.
        bool[] populated = ChannelPopulationDecision.Resolve(new[] { 3, 4, 2, 0 }, totalReads: 6);

        Assert.Equal(new[] { false, true, false, false }, populated);
    }

    [Fact]
    public void Resolve_NoChannelPopulated_ShowsAll() {
        // Inconclusive (nothing spun up / all garbage): never hide the whole controller.
        bool[] populated = ChannelPopulationDecision.Resolve(new[] { 0, 0, 0, 0 }, totalReads: 6);

        Assert.Equal(new[] { true, true, true, true }, populated);
    }

    [Fact]
    public void Resolve_AllPopulated_KeepsAll() {
        bool[] populated = ChannelPopulationDecision.Resolve(new[] { 6, 6, 6, 6 }, totalReads: 6);

        Assert.Equal(new[] { true, true, true, true }, populated);
    }

    [Fact]
    public void Resolve_ZeroTotalReads_ShowsAll() {
        // No reads taken -> inconclusive -> show all, never hide.
        bool[] populated = ChannelPopulationDecision.Resolve(new[] { 0, 0 }, totalReads: 0);

        Assert.Equal(new[] { true, true }, populated);
    }

    [Fact]
    public void Resolve_NullCounts_Throws()
        => Assert.Throws<System.ArgumentNullException>(() => ChannelPopulationDecision.Resolve(null!, 1));
}
