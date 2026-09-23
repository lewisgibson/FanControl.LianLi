using System;
using System.Linq;
using System.Threading;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Plugin;
using FanControl.LianLi.Tests.Fakes;
using FanControl.LianLi.Transport;
using FanControl.Plugins;
using Xunit;

namespace FanControl.LianLi.Tests.Plugin;

/// <summary>
/// The plugin as FanControl actually drives it: a new plugin object on every refresh, sharing one
/// process; Close only for an instance that registered sensors; Update on the host's own thread.
/// </summary>
public sealed class LianLiPluginLifecycleTests {
    private static LocatedDevice Sli(string path) => new LocatedDevice(0x0CF2, 0xA102, path, null);

    private static LianLiPlugin NewPlugin(FakeEnumerator enumerator, PluginRuntime runtime, FakeLogger? logger = null, IClock? clock = null)
        => new LianLiPlugin(enumerator, new DeviceCatalog(), clock ?? new FakeClock(), new FakeDelay(), logger ?? new FakeLogger(), LConnectDirectory.Absent, runtime);

    private static FakeSensorsContainer Load(LianLiPlugin plugin) {
        plugin.Initialize();
        var container = new FakeSensorsContainer();
        plugin.Load(container);
        return container;
    }

    private static string[] ControlIds(FakeSensorsContainer container)
        => container.ControlSensors.Select(sensor => sensor.Id).ToArray();

    [Fact]
    public void ARefreshOnANewInstance_StandsInForTheControllersTheLastOneBuilt() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        var enumerator = new FakeEnumerator(Sli("a"));
        using LianLiPlugin first = NewPlugin(enumerator, runtime);
        string[] before = ControlIds(Load(first));
        first.Close();

        // FanControl refreshes with a fresh plugin object, and this scan cannot finish.
        enumerator.FailLocate = true;
        using LianLiPlugin second = NewPlugin(enumerator, runtime);
        string[] after = ControlIds(Load(second));

        Assert.Equal(before, after);
        second.Close();
    }

    [Fact]
    public void AnInstanceFanControlNeverClosed_IsStoppedByTheNextOne() {
        // FanControl only closes an instance that registered sensors, so one that found nothing
        // useful is simply dropped - with its worker still running.
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        var enumerator = new FakeEnumerator(Sli("a"));
        using LianLiPlugin abandoned = NewPlugin(enumerator, runtime);
        abandoned.Initialize();
        FakeDeviceTransport leaked = Assert.Single(enumerator.Opened);

        using LianLiPlugin next = NewPlugin(new FakeEnumerator(), runtime);
        next.Initialize();

        Assert.True(leaked.IsDisposed);
        next.Close();
    }

    [Fact]
    public void ClosingAnOlderInstance_LeavesTheRunningOneAlone() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        using LianLiPlugin older = NewPlugin(new FakeEnumerator(Sli("a")), runtime);
        older.Initialize();
        var enumerator = new FakeEnumerator(Sli("a"));
        using LianLiPlugin current = NewPlugin(enumerator, runtime);
        current.Initialize();

        older.Close();

        Assert.False(Assert.Single(enumerator.Opened).IsDisposed);
        Assert.True(runtime.IsOwnedBy(current));
        current.Close();
        Assert.True(enumerator.Opened[0].IsDisposed);
    }

    [Fact]
    public void ADeviceMissingFromASuccessfulScan_IsStoodIn_AndTheOthersKeepTheirIds() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        using LianLiPlugin first = NewPlugin(new FakeEnumerator(Sli("a"), Sli("b")), runtime);
        string[] before = ControlIds(Load(first));
        first.Close();

        // "a" has not re-appeared yet after a wake; the scan itself succeeds.
        var logger = new FakeLogger();
        using LianLiPlugin second = NewPlugin(new FakeEnumerator(Sli("b")), runtime, logger);
        string[] after = ControlIds(Load(second));

        Assert.Equal(before, after);
        Assert.Contains(logger.Messages, m => m.Contains("a not found by this scan"));
        second.Close();
    }

    [Fact]
    public void IndicesNeverCollide_WhenOneIsMissingAndAnotherFails() {
        // Built as a=0, b=1, c=2. Next scan: a is gone, b will not open, c opens. Before the runtime
        // kept indices, c took the next list position and collided with b's stand-in.
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        using LianLiPlugin first = NewPlugin(new FakeEnumerator(Sli("a"), Sli("b"), Sli("c")), runtime);
        string[] before = ControlIds(Load(first));
        first.Close();

        var enumerator = new FakeEnumerator(Sli("b"), Sli("c")) { FailOpenWhen = info => info.DevicePath == "b" };
        using LianLiPlugin second = NewPlugin(enumerator, runtime);
        string[] after = ControlIds(Load(second));

        Assert.Equal(12, after.Distinct().Count());
        Assert.Equal(before, after);
        second.Close();
    }

    [Fact]
    public void ABuildStillRunningAtTheDeadline_IsStoodIn_AndTheStandInTakesItWithNoRefresh() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        using LianLiPlugin first = NewPlugin(new FakeEnumerator(Sli("a")), runtime);
        string[] before = ControlIds(Load(first));
        first.Close();

        // The population probe blocks this time, as a device wedged after a wake would, and the
        // stand-in's own reopens fail, so the stand-in can only connect through the late build.
        using var gate = new ManualResetEventSlim(false);
        var logger = new FakeLogger();
        int opens = 0;
        var enumerator = new FakeEnumerator(Sli("a")) {
            ConfigureTransport = (_, transport) => transport.BlockReadsUntil = gate,
            FailOpenWhen = _ => Interlocked.Increment(ref opens) > 1,
        };
        using LianLiPlugin second = NewPlugin(enumerator, runtime, logger);
        second.BuildDeadlineMilliseconds = 50;
        string[] during = ControlIds(Load(second));

        Assert.Equal(before, during);
        Assert.Contains(logger.Messages, m => m.Contains("a still opening after 50 ms"));

        gate.Set();

        Assert.True(SpinWait.SpinUntil(
            () => logger.Messages.Any(m => m == "  a finished opening after the scan; its stand-in took it"), TimeSpan.FromSeconds(10)));
        Assert.False(Assert.Single(enumerator.Opened).IsDisposed);
        Assert.False(runtime.TryTakeRefresh(second, out _));
        second.Close();
    }

    [Fact]
    public void ALateBuildOfAKnownController_WhoseStandInHasGone_IsClosed_WithNoRefresh() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        using LianLiPlugin first = NewPlugin(new FakeEnumerator(Sli("a")), runtime);
        _ = Load(first);
        first.Close();

        using var gate = new ManualResetEventSlim(false);
        var logger = new FakeLogger();
        int opens = 0;
        var enumerator = new FakeEnumerator(Sli("a")) {
            ConfigureTransport = (_, transport) => transport.BlockReadsUntil = gate,
            FailOpenWhen = _ => Interlocked.Increment(ref opens) > 1,
        };
        using LianLiPlugin second = NewPlugin(enumerator, runtime, logger);
        second.BuildDeadlineMilliseconds = 50;
        _ = Load(second);
        second.Close(); // FanControl refreshed again before the build finished

        gate.Set();

        Assert.True(SpinWait.SpinUntil(() => Assert.Single(enumerator.Opened).IsDisposed, TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("refresh wanted"));
    }

    [Fact]
    public void ANewControllerStillOpeningAtTheDeadline_AsksForOneRefresh_AndIsStoodInAfterThat() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        using var gate = new ManualResetEventSlim(false);
        var enumerator = new FakeEnumerator(Sli("a")) { ConfigureTransport = (_, transport) => transport.BlockReadsUntil = gate };
        using LianLiPlugin first = NewPlugin(enumerator, runtime);
        first.BuildDeadlineMilliseconds = 50;
        Assert.Empty(Load(first).ControlSensors);

        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => runtime.Recall("a") != null, TimeSpan.FromSeconds(5)));
        Assert.True(SpinWait.SpinUntil(() => runtime.TryTakeRefresh(first, out _), TimeSpan.FromSeconds(5)));
        Assert.True(SpinWait.SpinUntil(() => enumerator.Opened[0].IsDisposed, TimeSpan.FromSeconds(5)));
        first.Close();

        // The refresh's scan finds it slow again: it is stood in for, and no second refresh is asked.
        using var secondGate = new ManualResetEventSlim(false);
        var again = new FakeEnumerator(Sli("a")) { ConfigureTransport = (_, transport) => transport.BlockReadsUntil = secondGate };
        using LianLiPlugin second = NewPlugin(again, runtime);
        second.BuildDeadlineMilliseconds = 50;
        Assert.Equal(4, Load(second).ControlSensors.Count);
        secondGate.Set();
        Assert.True(SpinWait.SpinUntil(() => again.Opened.Count(t => !t.IsDisposed) == 1, TimeSpan.FromSeconds(5)));
        Assert.False(runtime.TryTakeRefresh(second, out _));
        second.Close();
    }

    [Fact]
    public void ABuildThatFailsAfterTheDeadline_IsLogged() {
        using var gate = new ManualResetEventSlim(false);
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(Sli("a")) {
            ConfigureTransport = (_, _) => {
                gate.Wait();
                throw new InvalidOperationException("wedged open");
            },
        };
        using LianLiPlugin plugin = NewPlugin(enumerator, new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()), logger);
        plugin.BuildDeadlineMilliseconds = 20;

        Assert.Empty(Load(plugin).ControlSensors);
        gate.Set();

        Assert.True(SpinWait.SpinUntil(
            () => logger.Messages.Any(m => m.Contains("a: the build that ran past the deadline failed: wedged open")),
            TimeSpan.FromSeconds(5)));
        plugin.Close();
    }

    [Fact]
    public void Update_DoesNoDeviceIo_AndRaisesARefreshOnlyWhenOneIsWanted() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(Sli("a"));
        using LianLiPlugin plugin = NewPlugin(enumerator, runtime, logger);
        _ = Load(plugin);
        int refreshes = 0;
        plugin.RefreshRequested += () => refreshes++;

        plugin.Update();
        Assert.Equal(0, refreshes);

        runtime.RequestRefresh("a wireless device checked in", logger);
        plugin.Update();
        Assert.Equal(1, refreshes);
        Assert.Contains(logger.Messages, m => m.Contains("requesting a FanControl refresh: a wireless device checked in"));
        plugin.Close();
    }

    [Fact]
    public void Update_WithNoSubscriber_StillTakesTheRequest() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        using LianLiPlugin plugin = NewPlugin(new FakeEnumerator(Sli("a")), runtime);
        _ = Load(plugin);
        runtime.RequestRefresh("x", new FakeLogger());

        plugin.Update();

        Assert.False(runtime.TryTakeRefresh(plugin, out _));
        plugin.Close();
    }

    [Fact]
    public void SettingAControl_WakesTheWorkerSoTheWriteHappensAtOnce() {
        // The loops tick once a minute here, so a write within seconds can only be the wake's.
        var enumerator = new FakeEnumerator(Sli("a"));
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()) { WorkerTickIntervalMilliseconds = 60_000 };
        using LianLiPlugin plugin = NewPlugin(enumerator, runtime);
        IPluginControlSensor control = Load(plugin).ControlSensors[0];
        FakeDeviceTransport transport = enumerator.Opened[0];
        Assert.True(SpinWait.SpinUntil(() => transport.ReadCount > 0, TimeSpan.FromSeconds(5))); // first tick ran
        int featuresBefore = transport.SnapshotTransfers().Length;

        control.Set(70);

        Assert.True(
            SpinWait.SpinUntil(() => transport.SnapshotTransfers().Length > featuresBefore, TimeSpan.FromSeconds(10)),
            "the new target was not written until the worker's next tick");
        plugin.Close();
    }

    // Each channel's RPM: 1500 where a fan spins, 0 where the probe finds none.
    private static Action<LocatedDevice, FakeDeviceTransport> Spinning(params int[] channels)
        => (_, transport) => {
            var report = new byte[65];
            foreach (int ch in channels) {
                report[1 + (ch * 2)] = 0x05;
                report[2 + (ch * 2)] = 0xDC;
            }

            transport.InputReport = report;
        };

    [Fact]
    public void AControllerThatComesBackWithFewerFans_KeepsTheSensorsItHadBefore() {
        // The second probe finds channels 2 and 3 stopped - a curve that had them at 0, a fan
        // unplugged - which used to take their sensors, and the curves bound to them, away.
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        using LianLiPlugin first = NewPlugin(new FakeEnumerator(Sli("a")) { ConfigureTransport = Spinning(0, 1, 2, 3) }, runtime);
        string[] before = ControlIds(Load(first));
        first.Close();

        var logger = new FakeLogger();
        using LianLiPlugin second = NewPlugin(new FakeEnumerator(Sli("a")) { ConfigureTransport = Spinning(0, 1) }, runtime, logger);
        FakeSensorsContainer container = Load(second);

        Assert.Equal(before, ControlIds(container));
        Assert.Equal(4, container.FanSensors.Count);
        Assert.Contains(logger.Messages, m => m.Contains("C0 kept all 4 remembered channel(s); 4 of them are there now"));
        second.Close();
    }

    [Fact]
    public void ASensorNoBuildReportsFor30Days_IsLetGo() {
        var clock = new FakeClock();
        var runtime = new PluginRuntime(clock, new FakeRememberedControllerStore());
        using LianLiPlugin first = NewPlugin(new FakeEnumerator(Sli("a")) { ConfigureTransport = Spinning(0, 1, 2, 3) }, runtime);
        _ = Load(first);
        first.Close();

        // Channels 2 and 3 stop being found; they are kept for the window, then let go.
        clock.Advance(TimeSpan.FromDays(29));
        using LianLiPlugin second = NewPlugin(new FakeEnumerator(Sli("a")) { ConfigureTransport = Spinning(0, 1) }, runtime);
        Assert.Equal(4, Load(second).ControlSensors.Count);
        second.Close();

        clock.Advance(TimeSpan.FromDays(2));
        using LianLiPlugin third = NewPlugin(new FakeEnumerator(Sli("a")) { ConfigureTransport = Spinning(0, 1) }, runtime);
        Assert.Equal(new[] { "LianLi/0/ch0/ctl", "LianLi/0/ch1/ctl" }, ControlIds(Load(third)));
        third.Close();
    }

    [Fact]
    public void AWirelessRebuildThatHearsNothingYet_KeepsTheWirelessSensors() {
        byte[] master = { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
        var tx = new LocatedDevice(0x0416, 0x8040, "fake/wireless/tx", null);
        var rx = new LocatedDevice(0x0416, 0x8041, "fake/wireless/rx", null);
        Action<LocatedDevice, FakeDeviceTransport> heard = (info, transport) => {
            if (info.ProductId == 0x8040) {
                var reply = new byte[64];
                reply[0] = 0x11;
                Array.Copy(master, 0, reply, 1, 6);
                reply[10] = 0x40;
                transport.ReadReplies.Enqueue(reply);
                return;
            }

            byte[] group = FanControl.LianLi.Tests.Protocol.WirelessProtocolTests.Record(
                new byte[] { 0xA0, 0, 0, 0, 0, 1 }, master, 8, 1, 0, 2, new byte[] { 36, 36, 0, 0 },
                new[] { 1000, 1100, 0, 0 }, new byte[] { 100, 100, 0, 0 }, 1);
            for (int i = 0; i < 8; i++) {
                transport.ReadReplies.Enqueue(FanControl.LianLi.Tests.Protocol.WirelessProtocolTests.ListReply(1, group));
            }
        };
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        using LianLiPlugin first = NewPlugin(new FakeEnumerator(tx, rx) { ConfigureTransport = heard }, runtime);
        FakeSensorsContainer before = Load(first);
        first.Close();

        // After a wake the dongles open before any fan has checked in over the radio.
        using LianLiPlugin second = NewPlugin(new FakeEnumerator(tx, rx), runtime);
        FakeSensorsContainer after = Load(second);

        Assert.Equal(ControlIds(before), ControlIds(after));
        Assert.Equal(before.FanSensors.Select(s => s.Id), after.FanSensors.Select(s => s.Id));
        second.Close();
    }

    [Fact]
    public void AfterAReboot_TheControllersFromTheLastRun_AreRegisteredBeforeTheyAnswer() {
        // The first run builds a controller and saves it; the next process starts with a fresh
        // runtime over the same store, and this time the scan finds nothing.
        var store = new FakeRememberedControllerStore();
        using LianLiPlugin first = NewPlugin(new FakeEnumerator(Sli("a")), new PluginRuntime(new FakeClock(), store));
        string[] before = ControlIds(Load(first));
        first.Close();
        Assert.Equal("a", Assert.Single(store.Stored).Key);

        var enumerator = new FakeEnumerator();
        var logger = new FakeLogger();
        using LianLiPlugin afterReboot = NewPlugin(enumerator, new PluginRuntime(new FakeClock(), store), logger);
        string[] after = ControlIds(Load(afterReboot));

        Assert.Equal(before, after);
        Assert.Contains(logger.Messages, m => m.Contains("remembered 1 controller(s) from earlier runs"));
        Assert.Contains(logger.Messages, m => m.Contains("a not found by this scan"));
        afterReboot.Close();
    }

    // The transmitter answers the master query; the receiver lists nothing for its first few reads
    // (the plugin's startup wait), then a group checks in and stays.
    private static Action<LocatedDevice, FakeDeviceTransport> GroupChecksInLate(int emptyReads) {
        byte[] master = { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
        return (info, transport) => {
            if (info.ProductId == 0x8040) {
                for (int i = 0; i < 50; i++) {
                    var reply = new byte[64];
                    reply[0] = 0x11;
                    Array.Copy(master, 0, reply, 1, 6);
                    reply[10] = 0x40;
                    transport.ReadReplies.Enqueue(reply);
                }

                return;
            }

            byte[] group = FanControl.LianLi.Tests.Protocol.WirelessProtocolTests.Record(
                new byte[] { 0xA0, 0, 0, 0, 0, 1 }, master, 8, 1, 0, 2, new byte[] { 36, 36, 0, 0 },
                new[] { 1000, 1100, 0, 0 }, new byte[] { 100, 100, 0, 0 }, 1);
            for (int i = 0; i < emptyReads; i++) {
                transport.ReadReplies.Enqueue(FanControl.LianLi.Tests.Protocol.WirelessProtocolTests.ListReply(0));
            }

            for (int i = 0; i < 50; i++) {
                transport.ReadReplies.Enqueue(FanControl.LianLi.Tests.Protocol.WirelessProtocolTests.ListReply(1, group));
            }
        };
    }

    private static LocatedDevice[] Dongles() => new[] {
        new LocatedDevice(0x0416, 0x8040, "fake/wireless/tx", null),
        new LocatedDevice(0x0416, 0x8041, "fake/wireless/rx", null),
    };

    [Fact]
    public void AWirelessDeviceHeardAfterLoad_AsksFanControlForARefresh() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        using LianLiPlugin plugin = NewPlugin(new FakeEnumerator(Dongles()) { ConfigureTransport = GroupChecksInLate(8) }, runtime, clock: new SystemClock());
        FakeSensorsContainer container = Load(plugin);
        Assert.Empty(container.ControlSensors);
        Assert.Equal("LianLi/waiting", Assert.Single(container.TempSensors).Id); // so FanControl hears the refresh
        int refreshes = 0;
        plugin.RefreshRequested += () => refreshes++;

        Assert.True(SpinWait.SpinUntil(() => { plugin.Update(); return refreshes == 1; }, TimeSpan.FromSeconds(10)));
        plugin.Close();
    }

    [Fact]
    public void NoPlaceholder_WhenTheWirelessPairWillNotOpen_OrOtherSensorsAreRegistered() {
        var failing = new FakeEnumerator(Dongles()) { FailOpen = true };
        using LianLiPlugin unopened = NewPlugin(failing, new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()));
        Assert.Empty(Load(unopened).TempSensors);
        unopened.Close();

        var withWired = new FakeEnumerator(Dongles().Append(Sli("a")).ToArray()) { ConfigureTransport = GroupChecksInLate(8) };
        using LianLiPlugin mixed = NewPlugin(withWired, new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()));
        FakeSensorsContainer container = Load(mixed);
        Assert.Equal(4, container.ControlSensors.Count);
        Assert.Empty(container.TempSensors);
        mixed.Close();

        using LianLiPlugin nothing = NewPlugin(new FakeEnumerator(), new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()));
        Assert.Empty(Load(nothing).TempSensors);
        nothing.Close();
    }

    [Fact]
    public void APlaceholder_WhenTheWirelessPairIsStillOpening_OrStoodInWithNothingHeardYet() {
        using var gate = new ManualResetEventSlim(false);
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        var slow = new FakeEnumerator(Dongles()) { ConfigureTransport = (_, transport) => transport.BlockReadsUntil = gate };
        using LianLiPlugin opening = NewPlugin(slow, runtime);
        opening.BuildDeadlineMilliseconds = 50;
        Assert.Single(Load(opening).TempSensors);
        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => runtime.Recall("fake/wireless/tx") != null, TimeSpan.FromSeconds(5)));
        opening.Close();

        // Remembered with nothing heard, and now not opening: stood in for, still waiting.
        using LianLiPlugin standIn = NewPlugin(new FakeEnumerator(Dongles()) { FailOpen = true }, runtime);
        Assert.Single(Load(standIn).TempSensors);
        standIn.Close();
    }

    // A first run whose only controller is a wired one still opening at the deadline: the placeholder
    // lets FanControl hear the refresh its late build asks for.
    [Fact]
    public void APlaceholder_WhenAWiredControllerIsStillOpeningOnAFirstRun() {
        using var gate = new ManualResetEventSlim(false);
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        var slow = new FakeEnumerator(Sli("a")) { ConfigureTransport = (_, transport) => transport.BlockReadsUntil = gate };
        using LianLiPlugin plugin = NewPlugin(slow, runtime);
        plugin.BuildDeadlineMilliseconds = 20;

        FakeSensorsContainer container = Load(plugin);

        Assert.Empty(container.ControlSensors);
        Assert.Equal("LianLi/waiting", Assert.Single(container.TempSensors).Id);
        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => runtime.TryTakeRefresh(plugin, out _), TimeSpan.FromSeconds(5)));
        plugin.Close();
    }

    [Fact]
    public void WaitingSensor_HasNoReading() {
        var sensor = new WaitingSensor();
        sensor.Update();

        Assert.Null(sensor.Value);
        Assert.Equal("Lian Li: waiting for devices", sensor.Name);
    }

    [Fact]
    public void AWirelessDeviceHeardBeforeLoad_IsSimplyRegistered_WithNoRefresh() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        var enumerator = new FakeEnumerator(Dongles()) { ConfigureTransport = GroupChecksInLate(5) };
        using LianLiPlugin plugin = NewPlugin(enumerator, runtime, clock: new SystemClock());
        plugin.Initialize();

        // The startup wait reads the list four times, all empty; the worker's first poll is the fifth,
        // also empty, and its second hears the group - all before FanControl gets round to Load.
        FakeDeviceTransport receiver = enumerator.TransportFor("fake/wireless/rx");
        Assert.True(SpinWait.SpinUntil(() => receiver.InterruptReadCount >= 7, TimeSpan.FromSeconds(10)));
        var container = new FakeSensorsContainer();
        plugin.Load(container);

        Assert.Single(container.ControlSensors);
        int reads = receiver.InterruptReadCount;
        Assert.True(SpinWait.SpinUntil(() => receiver.InterruptReadCount > reads, TimeSpan.FromSeconds(10))); // a further tick
        Assert.False(runtime.TryTakeRefresh(plugin, out _));
        plugin.Close();
    }

    [Fact]
    public void AWirelessDeviceHeardBeforeLoad_IsRememberedAndSaved() {
        var store = new FakeRememberedControllerStore();
        var runtime = new PluginRuntime(new FakeClock(), store);
        var enumerator = new FakeEnumerator(Dongles()) { ConfigureTransport = GroupChecksInLate(5) };
        using LianLiPlugin plugin = NewPlugin(enumerator, runtime, clock: new SystemClock());
        plugin.Initialize();
        Assert.Empty(runtime.Recall("fake/wireless/tx")!.Channels);
        FakeDeviceTransport receiver = enumerator.TransportFor("fake/wireless/rx");
        Assert.True(SpinWait.SpinUntil(() => receiver.InterruptReadCount >= 7, TimeSpan.FromSeconds(10)));

        var container = new FakeSensorsContainer();
        plugin.Load(container);

        string control = Assert.Single(container.ControlSensors).Id;
        Assert.Equal(control, Assert.Single(runtime.Recall("fake/wireless/tx")!.Channels).ControlId);
        Assert.Equal(control, Assert.Single(Assert.Single(store.Stored).Controller.Channels).ControlId);
        plugin.Close();
    }

    // A wireless pair an earlier run built, with one group control, at index.
    private static void RememberPair(PluginRuntime runtime, string name, int index)
        => _ = runtime.Remember("old/" + name + "/tx", RememberedFixture.Of(
            new ControllerPlan(
                DeviceKind.WirelessTransmitter,
                new LocatedDevice(0x0416, 0x8040, "old/" + name + "/tx", null),
                new LocatedDevice(0x0416, 0x8041, "old/" + name + "/rx", null)),
            index,
            new[] { new ChannelDescriptor(name + "/control", name, name + "/rpm", name) },
            Array.Empty<FanSpeedDescriptor>(),
            Array.Empty<TemperatureDescriptor>(),
            new FakeClock().UtcNow));

    [Fact]
    public void APairOnANewPath_TakesOverTheRememberedPair() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        RememberPair(runtime, "earlier", 0);
        var logger = new FakeLogger();
        using LianLiPlugin plugin = NewPlugin(new FakeEnumerator(Dongles()) { ConfigureTransport = GroupChecksInLate(0) }, runtime, logger);

        string[] controls = ControlIds(Load(plugin));

        Assert.Equal(1, controls.Count(id => id == "earlier/control"));
        Assert.Contains("  fake/wireless/tx is taken as old/earlier/tx on a new path; it keeps that controller's sensors", logger.Messages);
        Assert.Null(runtime.Recall("old/earlier/tx"));
        plugin.Close();
    }

    // With two remembered pairs missing, nothing says which one the new path is: neither is taken
    // over, and neither is stood in for beside the pair that is driven.
    [Fact]
    public void APairOnANewPath_WithTwoRememberedPairs_TakesOverNeither_AndStandsInForNeither() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        RememberPair(runtime, "earlier", 0);
        RememberPair(runtime, "other", 1);
        var logger = new FakeLogger();
        using LianLiPlugin plugin = NewPlugin(new FakeEnumerator(Dongles()) { ConfigureTransport = GroupChecksInLate(0) }, runtime, logger);

        string[] controls = ControlIds(Load(plugin));

        Assert.DoesNotContain("earlier/control", controls);
        Assert.DoesNotContain("other/control", controls);
        Assert.Contains("  wireless pair old/other/tx from an earlier scan not used: another pair is driven", logger.Messages);
        Assert.Contains("  wireless pair old/earlier/tx from an earlier scan not used: another pair is driven", logger.Messages);
        plugin.Close();
    }

    [Fact]
    public void AWiredControllerOnAnotherPort_KeepsItsIdsAndIsNotRegisteredTwice() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        using LianLiPlugin first = NewPlugin(new FakeEnumerator(Sli("port1")), runtime);
        string[] before = ControlIds(Load(first));
        first.Close();

        using LianLiPlugin second = NewPlugin(new FakeEnumerator(Sli("port2")), runtime);
        string[] after = ControlIds(Load(second));

        Assert.Equal(before, after);
        Assert.Null(runtime.Recall("port1"));
        Assert.Equal(0, runtime.Recall("port2")!.Index);
        second.Close();
    }

    [Fact]
    public void ANewPath_TakesOverOnlyAControllerOfTheSameKindAndProduct_ThatThisScanDidNotFind() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        using LianLiPlugin first = NewPlugin(
            new FakeEnumerator(Sli("a"), new LocatedDevice(0x0CF2, 0xA101, "al", null), new LocatedDevice(0x0CF2, 0xA102, "b", null)), runtime);
        _ = Load(first);
        first.Close();

        // a is still found; al is another product; only b, missing, is the same device moved.
        var logger = new FakeLogger();
        using LianLiPlugin second = NewPlugin(new FakeEnumerator(Sli("a"), Sli("c")), runtime, logger);
        _ = Load(second);

        Assert.Contains("  c is taken as b on a new path; it keeps that controller's sensors", logger.Messages);
        Assert.NotNull(runtime.Recall("a"));
        Assert.NotNull(runtime.Recall("al"));
        Assert.Equal(2, runtime.Recall("c")!.Index);
        second.Close();
    }

    [Fact]
    public void TwoRememberedPairs_AndNoneFound_OnlyTheFirstIsStoodInFor() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        RememberPair(runtime, "first", 0);
        RememberPair(runtime, "second", 1);
        using LianLiPlugin plugin = NewPlugin(new FakeEnumerator(), runtime);

        Assert.Equal(new[] { "first/control" }, ControlIds(Load(plugin)));
        plugin.Close();
    }

    [Theory]
    [InlineData(new[] { "a", "b" }, true, false)]  // everything already registered by Load
    [InlineData(new[] { "a", "c" }, true, true)]   // a sensor Load did not register
    [InlineData(new[] { "c" }, false, false)]      // Load has not run: it will register c itself
    public void ARefreshIsNeededOnlyForASensorLoadDidNotRegister(string[] ids, bool loaded, bool expected)
        => Assert.Equal(
            expected,
            LianLiPlugin.HasUnregisteredSensor(ids, loaded ? new System.Collections.Generic.HashSet<string> { "a", "b" } : null));

    // The transmitter always answers; the receiver lists nothing for emptyReads reads, then a group.
    private static void SeedPair(LocatedDevice info, FakeDeviceTransport transport, int emptyReads) {
        var rig = new FakeWirelessRig();
        if (info.ProductId == 0x8040) {
            for (int i = 0; i < 100; i++) {
                transport.ReadReplies.Enqueue(rig.MasterReply()!);
            }

            return;
        }

        byte[] group = FanControl.LianLi.Tests.Protocol.WirelessProtocolTests.Record(
            new byte[] { 0xA0, 0, 0, 0, 0, 1 }, FakeWirelessRig.MasterMac, 8, 1, 0, 2,
            new byte[] { 36, 36, 0, 0 }, new[] { 1000, 1100, 0, 0 }, new byte[] { 100, 100, 0, 0 }, 1);
        for (int i = 0; i < 100; i++) {
            transport.ReadReplies.Enqueue(i < emptyReads
                ? FanControl.LianLi.Tests.Protocol.WirelessProtocolTests.ListReply(0)
                : FanControl.LianLi.Tests.Protocol.WirelessProtocolTests.ListReply(1, group));
        }
    }

    // A pair remembered with nothing heard builds late and hears a group; its stand-in takes it, and
    // since the group's control was never registered, a refresh is asked for.
    [Fact]
    public void AStandInThatTakesAControllerWithNewSensors_AsksForARefresh() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        var logger = new FakeLogger();
        using LianLiPlugin first = NewPlugin(new FakeEnumerator(Dongles()) {
            ConfigureTransport = (info, transport) => SeedPair(info, transport, 100),
        }, runtime, logger);
        Assert.Single(Load(first).TempSensors);
        first.Close();

        using var gate = new ManualResetEventSlim(false);
        int transmitterOpens = 0;
        var enumerator = new FakeEnumerator(Dongles()) {
            FailOpenWhen = info => info.ProductId == 0x8040 && Interlocked.Increment(ref transmitterOpens) > 1,
            ConfigureTransport = (info, transport) => {
                if (info.ProductId == 0x8040) {
                    gate.Wait();
                }

                SeedPair(info, transport, 0);
            },
        };
        using LianLiPlugin second = NewPlugin(enumerator, runtime, logger);
        second.BuildDeadlineMilliseconds = 20;
        FakeSensorsContainer container = Load(second);
        Assert.Empty(container.ControlSensors);
        gate.Set();

        Assert.True(SpinWait.SpinUntil(
            () => logger.Messages.Any(m => m.Contains("finished opening after the scan; its stand-in took it")), TimeSpan.FromSeconds(5)));
        Assert.Single(runtime.Recall("fake/wireless/tx")!.Channels);
        Assert.True(runtime.TryTakeRefresh(second, out _));
        second.Close();
    }

    // A group heard after Load is remembered and saved before the refresh is asked for, so the
    // refreshed instance stands in with its control even when it cannot open the pair.
    [Fact]
    public void AGroupHeardAfterLoad_IsRememberedBeforeTheRefresh_SoTheNextInstanceStandsInWithIt() {
        var store = new FakeRememberedControllerStore();
        var runtime = new PluginRuntime(new FakeClock(), store);
        using LianLiPlugin plugin = NewPlugin(
            new FakeEnumerator(Dongles()) { ConfigureTransport = (info, transport) => SeedPair(info, transport, 8) },
            runtime,
            clock: new SystemClock());
        Assert.Empty(Load(plugin).ControlSensors);
        Assert.True(SpinWait.SpinUntil(() => runtime.TryTakeRefresh(plugin, out _), TimeSpan.FromSeconds(15)));
        plugin.Close();

        Assert.Single(Assert.Single(store.Stored).Controller.Channels);
        using LianLiPlugin refreshed = NewPlugin(new FakeEnumerator(Dongles()) { FailOpen = true }, runtime);
        Assert.Single(Load(refreshed).ControlSensors);
        refreshed.Close();
    }

    // FanControl's service can run for months: a controller absent for 30 days is let go on the next
    // scan, not only at the next process start.
    [Fact]
    public void AControllerAbsentFor30Days_IsLetGoOnTheNextScan_InTheSameProcess() {
        var clock = new FakeClock();
        var runtime = new PluginRuntime(clock, new FakeRememberedControllerStore());
        var logger = new FakeLogger();
        using LianLiPlugin first = NewPlugin(new FakeEnumerator(Sli("a")), runtime);
        Assert.Equal(4, Load(first).ControlSensors.Count);
        first.Close();

        clock.Advance(TimeSpan.FromDays(29));
        using LianLiPlugin second = NewPlugin(new FakeEnumerator { FailOpen = true }, runtime);
        Assert.Equal(4, Load(second).ControlSensors.Count);
        second.Close();

        // The earlier stand-in's background rebuild may still be running, and a controller whose
        // build is in flight is deliberately kept; the first scan with none running lets it go.
        clock.Advance(TimeSpan.FromDays(2));
        // (Kept, it is still pruned: its sensors go before the controller itself does.)
        Assert.True(SpinWait.SpinUntil(() => {
            using LianLiPlugin third = NewPlugin(new FakeEnumerator { FailOpen = true }, runtime, logger);
            _ = Load(third);
            third.Close();
            return runtime.Recall("a") is null;
        }, TimeSpan.FromSeconds(5)));
        Assert.Contains("  a not built for 30 days; no longer stood in for", logger.Messages);
        using LianLiPlugin after = NewPlugin(new FakeEnumerator { FailOpen = true }, runtime);
        Assert.Empty(Load(after).ControlSensors);
        after.Close();
    }

    // A build that outlives its scan keeps its index: a later scan does not hand it to another
    // controller, so the late result and that controller never collide.
    [Fact]
    public void ABuildStillRunning_KeepsItsIndex_FromTheNextScansControllers() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        using var gate = new ManualResetEventSlim(false);
        var slow = new FakeEnumerator(new LocatedDevice(0x0CF2, 0xA102, "old", null)) {
            ConfigureTransport = (_, transport) => transport.BlockReadsUntil = gate,
        };
        using LianLiPlugin first = NewPlugin(slow, runtime);
        first.BuildDeadlineMilliseconds = 20;
        Assert.Empty(Load(first).ControlSensors);

        var present = new FakeEnumerator(new LocatedDevice(0x0CF2, 0xA101, "current", null));
        using LianLiPlugin second = NewPlugin(present, runtime);
        Assert.NotEmpty(Load(second).ControlSensors);
        Assert.Equal(1, runtime.Recall("current")!.Index);
        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => runtime.Recall("old") != null, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, runtime.Recall("old")!.Index);
        second.Close();

        using LianLiPlugin third = NewPlugin(present, runtime);
        Assert.Equal(8, Load(third).ControlSensors.Count); // current, and old stood in for
        third.Close();
    }

    // A stand-in that took its controller before Load ran, when nothing was registered to compare
    // with: Load itself sees the controller brought sensors it did not have, and asks for the refresh.
    [Fact]
    public void AStandInThatTookItsControllerBeforeLoad_HasLoadAskForTheRefresh() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        var logger = new FakeLogger();
        using LianLiPlugin first = NewPlugin(new FakeEnumerator(Dongles()) {
            ConfigureTransport = (info, transport) => SeedPair(info, transport, 100),
        }, runtime, logger);
        _ = Load(first);
        first.Close();

        using var gate = new ManualResetEventSlim(false);
        int transmitterOpens = 0;
        var enumerator = new FakeEnumerator(Dongles()) {
            FailOpenWhen = info => info.ProductId == 0x8040 && Interlocked.Increment(ref transmitterOpens) > 1,
            ConfigureTransport = (info, transport) => {
                if (info.ProductId == 0x8040) {
                    gate.Wait();
                }

                SeedPair(info, transport, 0);
            },
        };
        using LianLiPlugin second = NewPlugin(enumerator, runtime, logger);
        second.BuildDeadlineMilliseconds = 20;
        second.Initialize();
        gate.Set();
        Assert.True(SpinWait.SpinUntil(
            () => logger.Messages.Any(m => m.Contains("finished opening after the scan; its stand-in took it")), TimeSpan.FromSeconds(5)));
        Assert.False(runtime.TryTakeRefresh(second, out _));

        second.Load(new FakeSensorsContainer());

        Assert.True(runtime.TryTakeRefresh(second, out _));
        second.Close();
    }

    // What a controller reports is remembered on a thread of the plugin's own; a fault there is
    // logged, never let out.
    [Fact]
    public void SensorsThatCannotBeRemembered_AreLogged() {
        var logger = new FakeLogger();
        using LianLiPlugin plugin = NewPlugin(new FakeEnumerator(), new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()), logger);
        var device = new FakeFanDevice("c0") { PopulationFault = new InvalidOperationException("device gone") };

        plugin.OnSensorsReported(new ControllerPlan(DeviceKind.UniFan, Sli("a")), 0, device, "test");

        Assert.Contains("  a: the sensors it reported could not be remembered: device gone", logger.Messages);
    }

    // An instance whose scan ran before the saved controllers were read does not remember what its
    // controllers report afterwards: their indices were numbered without the file.
    [Fact]
    public void SensorsReportedUnderAnEarlierNumbering_AreNotRemembered() {
        var store = new FakeRememberedControllerStore { UnreadableLoads = 1 };
        var runtime = new PluginRuntime(new FakeClock(), store);
        var logger = new FakeLogger();
        using LianLiPlugin unread = NewPlugin(new FakeEnumerator(Sli("a")), runtime, logger);
        _ = Load(unread);
        unread.Close();
        using LianLiPlugin read = NewPlugin(new FakeEnumerator(Sli("a")), runtime);
        _ = Load(read);
        read.Close();

        unread.OnSensorsReported(new ControllerPlan(DeviceKind.UniFan, Sli("b")), 7, new FakeFanDevice("c0"), "test");

        Assert.Contains("  b: its sensors were numbered before the saved controllers were read; not remembered", logger.Messages);
        Assert.Null(runtime.Recall("b"));
    }

    // A stand-in's rebuild runs through the instance that registered it, under the numbering that
    // instance's scan ran under: once the saved controllers have been read since, it opens nothing,
    // so it can never claim a device at a guessed index the file gives another controller.
    [Fact]
    public void ARebuildFromAnInstanceOfAnEarlierNumbering_OpensNothing() {
        var store = new FakeRememberedControllerStore { UnreadableLoads = 1 };
        var runtime = new PluginRuntime(new FakeClock(), store);
        var unreadEnumerator = new FakeEnumerator(Sli("a"));
        using LianLiPlugin unread = NewPlugin(unreadEnumerator, runtime);
        _ = Load(unread);
        unread.Close();
        using LianLiPlugin read = NewPlugin(new FakeEnumerator(), runtime);
        _ = Load(read);
        read.Close();
        int opened = unreadEnumerator.Opened.Count;

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
            () => unread.Rebuild(new ControllerPlan(DeviceKind.UniFan, Sli("a")), 0));

        Assert.Equal("a is still being opened by another build, or by the next scan", refused.Message);
        Assert.Equal(opened, unreadEnumerator.Opened.Count);
    }

    // While the scan's own build of a remembered controller is still running, the stand-in
    // registered for it does not open the device a second time: one owner at a time.
    [Fact]
    public void AStandIn_NeverOpensADeviceItsScansLateBuildStillHas() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        using LianLiPlugin first = NewPlugin(new FakeEnumerator(Sli("a")), runtime);
        _ = Load(first);
        first.Close();

        using var gate = new ManualResetEventSlim(false);
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(Sli("a")) { ConfigureTransport = (_, transport) => transport.BlockReadsUntil = gate };
        using LianLiPlugin second = NewPlugin(enumerator, runtime, logger);
        second.BuildDeadlineMilliseconds = 20;
        Assert.Equal(4, Load(second).ControlSensors.Count);

        Assert.True(SpinWait.SpinUntil(
            () => logger.Messages.Any(m => m.Contains("a is still being opened by another build")), TimeSpan.FromSeconds(5)));
        Assert.Single(enumerator.Opened);
        gate.Set();
        Assert.True(SpinWait.SpinUntil(
            () => logger.Messages.Any(m => m == "  a finished opening after the scan; its stand-in took it"), TimeSpan.FromSeconds(5)));
        Assert.False(Assert.Single(enumerator.Opened).IsDisposed);
        second.Close();
    }

    // A scan after a refresh does not reopen a device an earlier scan's build still has open.
    [Fact]
    public void ANewScan_DoesNotReopenADeviceAnEarlierScansBuildStillHas() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        using var gate = new ManualResetEventSlim(false);
        var slow = new FakeEnumerator(Sli("a")) { ConfigureTransport = (_, transport) => transport.BlockReadsUntil = gate };
        using LianLiPlugin first = NewPlugin(slow, runtime);
        first.BuildDeadlineMilliseconds = 20;
        _ = Load(first);
        first.Close();

        var logger = new FakeLogger();
        var again = new FakeEnumerator(Sli("a"));
        using LianLiPlugin second = NewPlugin(again, runtime, logger);
        _ = Load(second);

        Assert.Empty(again.Opened);
        Assert.Contains("  a is still being opened by an earlier build; not opened again", logger.Messages);
        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => runtime.Recall("a") != null, TimeSpan.FromSeconds(5)));
        second.Close();
    }

    // A pair remembered with group A comes back with nothing heard, so it is wrapped to keep A; then
    // group B checks in before Load. Load registers only what the wrapper carries, so it asks for
    // the refresh that brings B in.
    [Fact]
    public void AWrappedControllerThatHearsANewGroupBeforeLoad_HasLoadAskForTheRefresh() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        var plan = new ControllerPlan(DeviceKind.WirelessTransmitter, Dongles());
        _ = runtime.Remember(plan.Key, RememberedFixture.Of(
            plan, 0, new[] { new ChannelDescriptor("group-a/ctl", "A", "group-a/fan", "A") },
            Array.Empty<FanSpeedDescriptor>(), Array.Empty<TemperatureDescriptor>(), new FakeClock().UtcNow));
        using LianLiPlugin plugin = NewPlugin(
            new FakeEnumerator(Dongles()) { ConfigureTransport = GroupChecksInLate(5) }, runtime, clock: new SystemClock());
        plugin.Initialize();
        Assert.True(SpinWait.SpinUntil(() => runtime.Recall(plan.Key)!.Channels.Count == 2, TimeSpan.FromSeconds(10)));

        var container = new FakeSensorsContainer();
        plugin.Load(container);

        Assert.Equal(new[] { "group-a/ctl" }, ControlIds(container));
        Assert.True(runtime.TryTakeRefresh(plugin, out _));
        plugin.Close();
    }

    // A pair that only ever hears devices it does not drive - a neighbour's kit in range - waits
    // while the process that first saw it runs, a refresh included, and not in any process after: the
    // placeholder must not stay for good.
    [Fact]
    public void APlaceholder_ForAPairHearingOnlyAnotherMastersDevices_OnlyInTheProcessThatFirstSawIt() {
        byte[] foreign = FanControl.LianLi.Tests.Protocol.WirelessProtocolTests.Record(
            new byte[] { 0xB0, 0, 0, 0, 0, 1 }, new byte[] { 0x99, 0x99, 0x99, 0x99, 0x99, 0x99 }, 8, 1, 0, 2,
            new byte[] { 36, 36, 0, 0 }, new[] { 1000, 1100, 0, 0 }, new byte[] { 100, 100, 0, 0 }, 1);
        var rig = new FakeWirelessRig();
        Action<LocatedDevice, FakeDeviceTransport> neighbourOnly = (info, transport) => {
            for (int i = 0; i < 100; i++) {
                transport.ReadReplies.Enqueue(info.ProductId == 0x8040
                    ? rig.MasterReply()!
                    : FanControl.LianLi.Tests.Protocol.WirelessProtocolTests.ListReply(1, foreign));
            }
        };
        var store = new FakeRememberedControllerStore();
        var runtime = new PluginRuntime(new FakeClock(), store);

        using LianLiPlugin first = NewPlugin(new FakeEnumerator(Dongles()) { ConfigureTransport = neighbourOnly }, runtime);
        Assert.Contains(Load(first).TempSensors, sensor => sensor.Id == "LianLi/waiting");
        first.Close();

        using LianLiPlugin refreshed = NewPlugin(new FakeEnumerator(Dongles()) { ConfigureTransport = neighbourOnly }, runtime);
        Assert.Contains(Load(refreshed).TempSensors, sensor => sensor.Id == "LianLi/waiting");
        refreshed.Close();

        // A later process reads the pair back from the file: built, failing to open, or not found.
        foreach (FakeEnumerator enumerator in new[] {
            new FakeEnumerator(Dongles()) { ConfigureTransport = neighbourOnly },
            new FakeEnumerator(Dongles()) { FailOpen = true },
            new FakeEnumerator(),
        }) {
            using LianLiPlugin later = NewPlugin(enumerator, new PluginRuntime(new FakeClock(), store));
            FakeSensorsContainer container = Load(later);

            Assert.Empty(container.ControlSensors);
            Assert.Empty(container.TempSensors);
            later.Close();
        }
    }

    // A pair whose receiver already reports its devices has nothing more to wait for, even when none
    // of them has a sensor - a Strimer only, here - so no placeholder is registered.
    [Fact]
    public void NoPlaceholder_ForAPairWhoseDevicesHaveAnsweredWithoutSensors() {
        byte[] strimer = FanControl.LianLi.Tests.Protocol.WirelessProtocolTests.Record(
            new byte[] { 0xA0, 0, 0, 0, 0, 1 }, FakeWirelessRig.MasterMac, 8, 1, 5, 0,
            new byte[4], new int[4], new byte[4], 1);
        var rig = new FakeWirelessRig();
        var enumerator = new FakeEnumerator(Dongles()) {
            ConfigureTransport = (info, transport) => {
                for (int i = 0; i < 100; i++) {
                    transport.ReadReplies.Enqueue(info.ProductId == 0x8040
                        ? rig.MasterReply()!
                        : FanControl.LianLi.Tests.Protocol.WirelessProtocolTests.ListReply(1, strimer));
                }
            },
        };
        using LianLiPlugin plugin = NewPlugin(enumerator, new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()));

        FakeSensorsContainer container = Load(plugin);

        Assert.Empty(container.ControlSensors);
        Assert.Empty(container.TempSensors);
        plugin.Close();
    }

    // A first run with L-Connect's locked list on disk and nothing on the air yet: the saved entries
    // are not replies, so the pair is still waiting and the placeholder keeps FanControl listening.
    [Fact]
    public void APlaceholder_WhenOnlyLConnectsLockedListIsKnown() {
        using var files = new LConnectDirectory();
        System.IO.Directory.CreateDirectory(files.Locations.WirelessDirectory);
        System.IO.File.WriteAllText(System.IO.Path.Combine(files.Locations.WirelessDirectory, "savedDevices.config"),
            "[{\"MasterMacStr\":\"11:22:33:44:55:66\",\"_target_rx_type\":1,\"_rx_type\":1,"
            + "\"mac_addr\":\"oAAAAAAB\",\"master_mac_addr\":\"ESIzRFVm\",\"channel\":8,"
            + "\"dev_type\":0,\"fan_num\":2,\"effect_index\":\"AAAAAA==\",\"fans_type\":\"JCQAAA==\","
            + "\"fans_pwm\":\"MjIyMg==\",\"target_fans_pwm\":\"MjIyMg==\"}]");
        var rig = new FakeWirelessRig();
        var clock = new FakeClock();
        var runtime = new PluginRuntime(clock, new FakeRememberedControllerStore());
        var log = new FakeLogger();
        using var plugin = new LianLiPlugin(new RigEnumerator(rig), new DeviceCatalog(), clock, new FakeDelay(), log, files.Locations, runtime);
        plugin.Initialize();
        var container = new FakeSensorsContainer();
        plugin.Load(container);

        Assert.Empty(container.ControlSensors);
        Assert.Contains(log.Messages, m => m.Contains("device list is locked", StringComparison.Ordinal));
        Assert.Contains(container.TempSensors, sensor => sensor.Id == "LianLi/waiting");
        plugin.Close();
    }

    // A TL fan that first answers a poll after Load is remembered, and FanControl is asked to refresh
    // so it can be given a curve.
    [Fact]
    public void ATlFanThatAnswersAfterLoad_IsRemembered_AndRefreshedFor() {
        using var allowLateReply = new ManualResetEventSlim(false);
        using var pollEntered = new ManualResetEventSlim(false);
        var transport = new LateTlTransport(allowLateReply, pollEntered);
        var clock = new FakeClock();
        var runtime = new PluginRuntime(clock, new FakeRememberedControllerStore()) { WorkerTickIntervalMilliseconds = 20 };
        var log = new FakeLogger();
        using var plugin = new LianLiPlugin(new SingleEnumerator(transport), new DeviceCatalog(), clock, new FakeDelay(), log, LConnectDirectory.Absent, runtime);
        try {
            plugin.Initialize();
            Assert.True(pollEntered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            var container = new FakeSensorsContainer();
            plugin.Load(container);
            Assert.Single(container.ControlSensors);

            allowLateReply.Set();

            Assert.True(SpinWait.SpinUntil(() => runtime.TryTakeRefresh(plugin, out _), TimeSpan.FromSeconds(5)));
            Assert.Equal(2, runtime.Recall("fake/tl")!.Channels.Count);
        } finally {
            allowLateReply.Set();
            plugin.Close();
        }
    }

    // A TL hub whose construction handshake reports no fan yet, on a first run with nothing else:
    // the placeholder keeps FanControl listening for the refresh its first fan asks for.
    [Fact]
    public void APlaceholder_ForATlHubWhoseFansHaveNotAnsweredYet() {
        using var allowLateReply = new ManualResetEventSlim(false);
        using var pollEntered = new ManualResetEventSlim(false);
        var transport = new LateTlTransport(allowLateReply, pollEntered, fansAtFirst: 0);
        var clock = new FakeClock();
        var runtime = new PluginRuntime(clock, new FakeRememberedControllerStore()) { WorkerTickIntervalMilliseconds = 20 };
        using var plugin = new LianLiPlugin(new SingleEnumerator(transport), new DeviceCatalog(), clock, new FakeDelay(), new FakeLogger(), LConnectDirectory.Absent, runtime);
        try {
            plugin.Initialize();
            Assert.True(pollEntered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            var container = new FakeSensorsContainer();
            plugin.Load(container);

            Assert.Empty(container.ControlSensors);
            Assert.Contains(container.TempSensors, sensor => sensor.Id == "LianLi/waiting");

            allowLateReply.Set();
            Assert.True(SpinWait.SpinUntil(() => runtime.TryTakeRefresh(plugin, out _), TimeSpan.FromSeconds(5)));
        } finally {
            allowLateReply.Set();
            plugin.Close();
        }
    }

    // A first-run TL hub with no fan yet keeps the placeholder through a refresh in the same process,
    // whether the refresh builds it, fails to open it or does not find it.
    [Fact]
    public void APlaceholder_ForAFirstRunTlHubWithNoFanYet_ThroughARefresh_BuiltFailingOrAbsent() {
        var hub = new LocatedDevice(0x0416, 0x7372, "fake/tl", null);
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        foreach (FakeEnumerator enumerator in new[] {
            new FakeEnumerator(hub),
            new FakeEnumerator(hub),
            new FakeEnumerator(hub) { FailOpen = true },
            new FakeEnumerator(),
        }) {
            using LianLiPlugin plugin = NewPlugin(enumerator, runtime);
            FakeSensorsContainer container = Load(plugin);

            Assert.Empty(container.ControlSensors);
            Assert.Contains(container.TempSensors, sensor => sensor.Id == "LianLi/waiting");
            plugin.Close();
        }
    }

    // A build started while the saved controllers could not be read was numbered without them. When
    // it finishes after a later scan has read them, it is not remembered at its guessed index - that
    // could be another controller's for good - and the next scan builds it under the saved numbering.
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ABuildNumberedBeforeTheSavedControllersWereRead_IsNotRemembered_WhenItFinishesLate(bool saved, bool laterOpensFail) {
        var store = new FakeRememberedControllerStore();
        using (LianLiPlugin earlierProcess = NewPlugin(
            saved ? new FakeEnumerator(Sli("a"), Sli("b")) : new FakeEnumerator(Sli("a")), new PluginRuntime(new FakeClock(), store))) {
            _ = Load(earlierProcess);
            earlierProcess.Close();
        }

        store.UnreadableLoads = 1;
        var runtime = new PluginRuntime(new FakeClock(), store);
        var logger = new FakeLogger();
        using var release = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        var slow = new FakeEnumerator(Sli("b")) {
            ConfigureTransport = (_, transport) => {
                entered.Set();
                transport.BlockReadsUntil = release;
            },
        };
        using LianLiPlugin unread = NewPlugin(slow, runtime, logger);
        unread.BuildDeadlineMilliseconds = 50;
        using LianLiPlugin read = NewPlugin(new FakeEnumerator(Sli("a"), Sli("b")) { FailOpen = true }, runtime, logger);
        try {
            _ = Load(unread);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            unread.Close();
            _ = Load(read);
            read.Close();

            release.Set();
            Assert.True(SpinWait.SpinUntil(() => slow.Opened.Count == 1 && slow.Opened[0].IsDisposed, TimeSpan.FromSeconds(5)));
            Assert.Contains("  b finished opening after the scan, numbered before the saved controllers were read; built again by the next scan", logger.Messages);

            using LianLiPlugin next = NewPlugin(new FakeEnumerator(Sli("a"), Sli("b")) { FailOpen = laterOpensFail }, runtime);
            FakeSensorsContainer container = Load(next);

            string[] ids = ControlIds(container);
            Assert.Equal(ids.Length, ids.Distinct().Count());
            int[] expected = saved || !laterOpensFail ? new[] { 0, 1 } : new[] { 0 };
            Assert.Equal(expected, runtime.Remembered().Select(entry => entry.Value.Index).OrderBy(i => i));
            Assert.Equal(expected, store.Stored.Select(entry => entry.Controller.Index).OrderBy(i => i));
            next.Close();
        } finally {
            release.Set();
            unread.Close();
            read.Close();
        }
    }

    // A build still running from before the saved controllers were read does not keep the index it
    // was guessed: the next scan numbers by the file, so the saved controller keeps its own.
    [Fact]
    public void AScanAfterALateRead_NumbersByTheFile_NotByABuildStillRunningFromBefore() {
        var store = new FakeRememberedControllerStore();
        using (LianLiPlugin earlierProcess = NewPlugin(new FakeEnumerator(Sli("b")), new PluginRuntime(new FakeClock(), store))) {
            _ = Load(earlierProcess);
            earlierProcess.Close();
        }

        store.UnreadableLoads = 1;
        var runtime = new PluginRuntime(new FakeClock(), store);
        using var gate = new ManualResetEventSlim(false);
        var slow = new FakeEnumerator(Sli("a")) { ConfigureTransport = (_, transport) => transport.BlockReadsUntil = gate };
        using LianLiPlugin unread = NewPlugin(slow, runtime);
        unread.BuildDeadlineMilliseconds = 50;
        try {
            _ = Load(unread);
            unread.Close();

            using LianLiPlugin read = NewPlugin(new FakeEnumerator(Sli("a"), Sli("b")), runtime);
            FakeSensorsContainer container = Load(read);

            Assert.Equal(0, runtime.Recall("b")!.Index);
            Assert.Equal(new[] { "LianLi/0/ch0/ctl", "LianLi/0/ch1/ctl", "LianLi/0/ch2/ctl", "LianLi/0/ch3/ctl" }, ControlIds(container));
            read.Close();
        } finally {
            gate.Set();
            unread.Close();
        }
    }

    private sealed class RigEnumerator : IDeviceEnumerator {
        private readonly FakeWirelessRig _rig;

        public RigEnumerator(FakeWirelessRig rig) => _rig = rig;

        public System.Collections.Generic.IReadOnlyList<LocatedDevice> Locate(
            System.Collections.Generic.IReadOnlyList<int> vendorIds, System.Collections.Generic.IReadOnlyList<int> productIds) => new[] {
            new LocatedDevice(0x0416, 0x8040, "fake/wireless/tx", null),
            new LocatedDevice(0x0416, 0x8041, "fake/wireless/rx", null),
        };

        public IDeviceTransport Open(LocatedDevice info) => info.ProductId == 0x8040 ? _rig.Transmitter : _rig.Receiver;
    }

    private sealed class SingleEnumerator : IDeviceEnumerator {
        private readonly IDeviceTransport _transport;

        public SingleEnumerator(IDeviceTransport transport) => _transport = transport;

        public System.Collections.Generic.IReadOnlyList<LocatedDevice> Locate(
            System.Collections.Generic.IReadOnlyList<int> vendorIds, System.Collections.Generic.IReadOnlyList<int> productIds)
            => new[] { new LocatedDevice(0x0416, 0x7372, "fake/tl", null) };

        public IDeviceTransport Open(LocatedDevice info) => _transport;
    }

    // A TL hub that reports one fan (or none) to the construction handshake, then holds the first
    // poll's reply until the test lets it go, reporting a second fan.
    private sealed class LateTlTransport : IDeviceTransport {
        private readonly ManualResetEventSlim _allow;
        private readonly ManualResetEventSlim _entered;
        private readonly int _fansAtFirst;
        private int _reads;

        public LateTlTransport(ManualResetEventSlim allow, ManualResetEventSlim entered, int fansAtFirst = 1) {
            _allow = allow;
            _entered = entered;
            _fansAtFirst = fansAtFirst;
        }

        public bool CanWrite => true;

        public int Generation => 0;

        public void Write(byte[] report) {
        }

        public void SetFeature(byte[] report) {
        }

        public byte[] GetInputReport(byte reportId, int length) => new byte[length];

        public byte[] Read(int length) {
            if (Interlocked.Increment(ref _reads) == 1) {
                // An undetected record (detected bit clear) when no fan has answered yet.
                return FanControl.LianLi.Protocol.CommandPacket.Build(0xA1, _fansAtFirst == 0 ? (byte)0x00 : (byte)0x80, 0x03, 0xE8);
            }

            _entered.Set();
            Assert.True(_allow.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            return FanControl.LianLi.Protocol.CommandPacket.Build(0xA1, 0x80, 0x03, 0xE8, 0x81, 0x04, 0x4C);
        }

        public void Dispose() {
        }
    }
}
