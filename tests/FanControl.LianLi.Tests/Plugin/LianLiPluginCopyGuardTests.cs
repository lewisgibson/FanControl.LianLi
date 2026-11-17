using System;
using System.IO;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Plugin;
using FanControl.LianLi.Tests.Fakes;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Plugin;

/// <summary>
/// The plugin when FanControl has loaded another copy of it: a copy that is not the one driving the
/// fans registers nothing, opens nothing and stops whatever it was running, and takes over again
/// once it is the only copy.
/// </summary>
public class LianLiPluginCopyGuardTests {
    private static readonly string OwnPath = Path.Combine(Path.GetTempPath(), "lianli-plugin-guard", "FanControl.LianLi.dll");
    private static readonly string NewerPath = Path.Combine(Path.GetTempPath(), "lianli-plugin-guard", "FanControl.LianLi-Argb.dll");

    private readonly FakeLogger _log = new FakeLogger();
    private readonly FakePluginCopyScanner _scanner = new FakePluginCopyScanner(new PluginCopy(OwnPath, new Version(1, 1, 40), hasGuard: true));
    private readonly FakePluginCopyClaim _claim = new FakePluginCopyClaim();
    private readonly FakePluginFileSystem _files = new FakePluginFileSystem();
    private readonly PluginCopyGuard _guard;
    private readonly PluginRuntime _runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());

    public LianLiPluginCopyGuardTests() {
        _guard = new PluginCopyGuard(_scanner, _files, _claim);
    }

    private static LocatedDevice Sli(int index) => new LocatedDevice(0x0CF2, 0xA102, "fake/" + index, null);

    private LianLiPlugin NewPlugin(FakeEnumerator enumerator)
        => new LianLiPlugin(enumerator, new DeviceCatalog(), new FakeClock(), new FakeDelay(), _log, LConnectDirectory.Absent, _runtime, _guard);

    // A newer copy that is driving the fans: one with the guard holding the claim, or one without
    // it whose file FanControl still loads.
    private void NewerCopyLoaded(bool hasGuard = true) {
        _scanner.Copies.Add(new PluginCopy(NewerPath, new Version(1, 1, 41), hasGuard));
        _files.Add(NewerPath, DateTime.UtcNow);
        _claim.HeldElsewhere = hasGuard;
    }

    [Fact]
    public void Initialize_WhileANewerCopyIsLoaded_RegistersNothingAndOpensNothing() {
        NewerCopyLoaded();
        var enumerator = new FakeEnumerator(Sli(0));
        using LianLiPlugin plugin = NewPlugin(enumerator);
        var refreshes = 0;
        plugin.RefreshRequested += () => refreshes++;

        plugin.Initialize();
        var container = new FakeSensorsContainer();
        plugin.Load(container);
        plugin.Update();
        plugin.Close();

        Assert.Empty(container.ControlSensors);
        Assert.Empty(container.FanSensors);
        Assert.Empty(container.TempSensors);
        Assert.Empty(enumerator.OpenedPaths);
        Assert.Equal(0, refreshes);
        Assert.False(_claim.Held);
        Assert.Contains(_log.Messages, message => message.Contains("stays idle: the copy at " + NewerPath, StringComparison.Ordinal));
    }

    [Fact]
    public void Initialize_WhenANewerCopyAppears_StopsWhatThisCopyWasRunning_AndTakesOverWhenItIsGone() {
        var enumerator = new FakeEnumerator(Sli(0));
        using LianLiPlugin first = NewPlugin(enumerator);
        first.Initialize();
        var registered = new FakeSensorsContainer();
        first.Load(registered);
        Assert.Equal(4, registered.ControlSensors.Count);
        Assert.True(_runtime.IsOwnedBy(first));

        // FanControl refreshes with a newer copy loaded beside this one, which never steps aside.
        NewerCopyLoaded(hasGuard: false);
        using LianLiPlugin second = NewPlugin(enumerator);
        second.Initialize();
        var idle = new FakeSensorsContainer();
        second.Load(idle);

        Assert.False(_runtime.IsOwnedBy(first));
        Assert.False(_claim.Held);
        Assert.Empty(idle.ControlSensors);

        // And refreshes again once it is the only copy.
        _scanner.Copies.Clear();
        _claim.HeldElsewhere = false;
        using LianLiPlugin third = NewPlugin(enumerator);
        third.Initialize();
        var back = new FakeSensorsContainer();
        third.Load(back);

        Assert.Equal(4, back.ControlSensors.Count);
        Assert.True(_claim.Held);
        Assert.Contains(_log.Messages, message => message.Contains("now drives the fans", StringComparison.Ordinal));
    }

    [Fact]
    public void Initialize_AfterClose_TakesTheClaimAgain() {
        var enumerator = new FakeEnumerator(Sli(0));
        using LianLiPlugin plugin = NewPlugin(enumerator);

        plugin.Initialize();
        Assert.True(_claim.Held);
        plugin.Close();
        Assert.False(_claim.Held);

        plugin.Initialize();
        var container = new FakeSensorsContainer();
        plugin.Load(container);

        Assert.True(_claim.Held);
        Assert.Equal(4, container.ControlSensors.Count);
    }

    [Fact]
    public void Initialize_WhenTheCheckForAnotherCopyFails_LogsItAndDrivesTheFans() {
        _scanner.Failure = new InvalidOperationException("scan failed");
        var enumerator = new FakeEnumerator(Sli(0));
        using LianLiPlugin plugin = NewPlugin(enumerator);

        plugin.Initialize();
        var container = new FakeSensorsContainer();
        plugin.Load(container);

        Assert.Equal(4, container.ControlSensors.Count);
        Assert.Contains(_log.Messages, message => message.Contains("could not check for another copy of the plugin, so this copy drives the fans", StringComparison.Ordinal)
            && message.Contains("scan failed", StringComparison.Ordinal));
    }

    [Fact]
    public void Update_WhenANewerCopyRenamesThisDrivingCopyAway_AsksFanControlForARefresh() {
        // The newer copy was loaded but had not started when this one took the fans.
        _files.Add(OwnPath, DateTime.UtcNow);
        _scanner.Copies.Add(new PluginCopy(NewerPath, new Version(1, 1, 41), hasGuard: true));
        var enumerator = new FakeEnumerator(Sli(0));
        using LianLiPlugin plugin = NewPlugin(enumerator);
        var refreshes = 0;
        plugin.RefreshRequested += () => refreshes++;
        plugin.Initialize();
        plugin.Load(new FakeSensorsContainer());

        plugin.Update();
        Assert.Equal(0, refreshes);

        _files.Move(OwnPath, OwnPath + PluginCopyGuard.RemovedSuffix);
        plugin.Update();
        plugin.Update();

        Assert.Equal(1, refreshes);
        Assert.Contains(_log.Messages, message => message.Contains("a newer copy of the plugin removed this copy's file", StringComparison.Ordinal));
    }
}

