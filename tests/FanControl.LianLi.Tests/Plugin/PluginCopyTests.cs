using System;
using System.IO;
using FanControl.LianLi.Plugin;
using Xunit;

namespace FanControl.LianLi.Tests.Plugin;

public class PluginCopyTests {
    [Fact]
    public void Constructor_RejectsNullArguments() {
        Assert.Throws<ArgumentNullException>(() => new PluginCopy(null!, new Version(1, 0), hasGuard: true));
        Assert.Throws<ArgumentNullException>(() => new PluginCopy("FanControl.LianLi.dll", null!, hasGuard: true));
    }

    [Fact]
    public void Constructor_KeepsWhatItIsGiven() {
        string path = Path.Combine(Path.GetTempPath(), "FanControl.LianLi.dll");
        var copy = new PluginCopy(path, new Version(1, 1, 40), hasGuard: false);

        Assert.Equal(path, copy.Location);
        Assert.Equal(new Version(1, 1, 40), copy.Version);
        Assert.False(copy.HasGuard);
    }

    [Fact]
    public void ToString_NamesTheFileAndTheVersion() {
        string path = Path.Combine(Path.GetTempPath(), "FanControl.LianLi.dll");
        Assert.Equal(path + " (version 1.1.40)", new PluginCopy(path, new Version(1, 1, 40), hasGuard: true).ToString());
        Assert.Equal("a copy loaded from memory (version 1.1.40)", new PluginCopy(string.Empty, new Version(1, 1, 40), hasGuard: true).ToString());
    }
}
