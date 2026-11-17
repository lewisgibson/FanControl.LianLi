using System;
using System.IO;
using FanControl.LianLi.Plugin;

namespace FanControl.LianLi.Tests.Fakes;

/// <summary>A guard for a plugin that is the only copy loaded, as every test of a single plugin has it.</summary>
internal static class CopyGuardFixture {
    public static PluginCopyGuard Alone()
        => new PluginCopyGuard(
            new FakePluginCopyScanner(new PluginCopy(Path.Combine(Path.GetTempPath(), "FanControl.LianLi.dll"), new Version(1, 1, 40), hasGuard: true)),
            new FakePluginFileSystem(),
            new FakePluginCopyClaim());
}
