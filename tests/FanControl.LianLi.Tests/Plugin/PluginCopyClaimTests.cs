using System;
using FanControl.LianLi.Plugin;
using FanControl.LianLi.Tests.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Plugin;

/// <summary>
/// The claim as the real named semaphore, which only Windows has. Each test claims a name of its
/// own, so the tests never contend with each other or with a FanControl on the machine.
/// </summary>
public class PluginCopyClaimTests {
    private static string UniqueName() => "FanControl.LianLi.Test." + Guid.NewGuid().ToString("N");

    [Fact]
    public void Constructor_RejectsAMissingName() {
        Assert.Throws<ArgumentException>(() => new PluginCopyClaim(null!));
        Assert.Throws<ArgumentException>(() => new PluginCopyClaim(string.Empty));
    }

    [Fact]
    public void NameFor_IsPerProcess() {
        Assert.Equal("FanControl.LianLi.Driving.42", PluginCopyClaim.NameFor(42));
        Assert.NotEqual(PluginCopyClaim.NameFor(42), PluginCopyClaim.NameFor(43));
    }

    [WindowsFact]
    public void OneClaimHoldsTheName_UntilItIsReleased() {
        string name = UniqueName();
        using var first = new PluginCopyClaim(name);
        using var second = new PluginCopyClaim(name);

        Assert.True(first.TryTake());
        Assert.True(first.TryTake());
        Assert.False(second.TryTake());

        first.Release();
        Assert.True(second.TryTake());
        Assert.False(first.TryTake());
    }

    [WindowsFact]
    public void ReleaseAndDispose_AreSafeWithoutTheClaim_AndDisposeGivesItUp() {
        string name = UniqueName();
        var first = new PluginCopyClaim(name);
        using var second = new PluginCopyClaim(name);
        first.Release();

        Assert.True(first.TryTake());
        first.Dispose();
        first.Dispose();

        Assert.True(second.TryTake());
    }
}
