using System;
using System.Linq;
using System.Threading;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Plugin;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Tests.Fakes;
using FanControl.LianLi.Tests.Protocol;
using FanControl.LianLi.Transport;
using FanControl.Plugins;
using Xunit;

namespace FanControl.LianLi.Tests.Plugin;

/// <summary>
/// The plugin as FanControl actually drives it: a new plugin object on every refresh, sharing one
/// process; Close only for an instance that registered sensors; Update on the host's own thread.
/// </summary>
public sealed class LianLiPluginLifecycleTests {
    // Stands in for the wireless controller that publishes the bound set.
    private static readonly object RadioOwner = new object();

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

            byte[] group = WirelessProtocolTests.Record(
                new byte[] { 0xA0, 0, 0, 0, 0, 1 }, master, 8, 1, 0, 2, new byte[] { 36, 36, 0, 0 },
                new[] { 1000, 1100, 0, 0 }, new byte[] { 100, 100, 0, 0 }, 1);
            for (int i = 0; i < 8; i++) {
                transport.ReadReplies.Enqueue(WirelessProtocolTests.ListReply(1, group));
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

            byte[] group = WirelessProtocolTests.Record(
                new byte[] { 0xA0, 0, 0, 0, 0, 1 }, master, 8, 1, 0, 2, new byte[] { 36, 36, 0, 0 },
                new[] { 1000, 1100, 0, 0 }, new byte[] { 100, 100, 0, 0 }, 1);
            for (int i = 0; i < emptyReads; i++) {
                transport.ReadReplies.Enqueue(WirelessProtocolTests.ListReply(0));
            }

            for (int i = 0; i < 50; i++) {
                transport.ReadReplies.Enqueue(WirelessProtocolTests.ListReply(1, group));
            }
        };
    }

    // The transmitter always answers the master query; the receiver lists nothing until heard says
    // so, then the group, so a test on the injected clock decides which poll hears it: the pair
    // reads its list once a second on the clock, so after Initialize only the poll the test lets
    // happen by moving the clock (or the worker's first, read at once) can hear it.
    private static Action<LocatedDevice, FakeDeviceTransport> GroupChecksInWhen(Func<bool> heard) {
        byte[] master = { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
        var masterReply = new byte[64];
        masterReply[0] = 0x11;
        Array.Copy(master, 0, masterReply, 1, 6);
        masterReply[10] = 0x40;
        byte[] group = WirelessProtocolTests.Record(
            new byte[] { 0xA0, 0, 0, 0, 0, 1 }, master, 8, 1, 0, 2, new byte[] { 36, 36, 0, 0 },
            new[] { 1000, 1100, 0, 0 }, new byte[] { 100, 100, 0, 0 }, 1);
        byte[] empty = WirelessProtocolTests.ListReply(0);
        byte[] listed = WirelessProtocolTests.ListReply(1, group);
        return (info, transport) => transport.ReplyFor = info.ProductId == 0x8040
            ? _ => masterReply
            : _ => heard() ? listed : empty;
    }

    private static LocatedDevice[] Dongles() => new[] {
        new LocatedDevice(0x0416, 0x8040, "fake/wireless/tx", null),
        new LocatedDevice(0x0416, 0x8041, "fake/wireless/rx", null),
    };

    private static readonly byte[] FlexChainMac = { 0xA0, 0, 0, 0, 0, 1 };
    private const string FlexControl = "LianLi/wa00000000001/ctl";

    private static LocatedDevice FlexReceiver() => new LocatedDevice(0x43A8, 0x0101, "fake/flex", null);

    // A receiver whose chain is the group the dongle fakes list (GroupChecksInLate), with the given
    // number of fans: it answers every status request and takes every speed.
    private static Action<LocatedDevice, FakeDeviceTransport> FlexChain(int fans = 2)
        => (info, transport) => {
            if (info.VendorId != 0x43A8) {
                return;
            }

            byte[] status = FlexReceiverProtocolTests.StatusReply(new FakeWirelessRecord(FlexChainMac, new byte[6]) {
                FanCountByte = (byte)fans,
                FanTypes = new byte[] { 51, 51, 0, 0 },
                Rpm = new[] { 1200, 1150, 0, 0 },
            });
            transport.ReplyFor = written => written[0] == 0x12 ? status : new byte[] { 0x13, 0 };
        };

    private static Action<LocatedDevice, FakeDeviceTransport> FlexChainAndDongles(int emptyReads)
        => (info, transport) => {
            if (info.VendorId == 0x0416) {
                GroupChecksInLate(emptyReads)(info, transport);
            }

            FlexChain()(info, transport);
        };

    // Its USB receiver first; then the dongles appear with the chain bound to their master; then the
    // dongles are gone again. The chain keeps its one control id throughout, registered by whichever
    // drives it, and the other never stands in with it beside.
    [Fact]
    public void AFlexChain_MovedBetweenItsUsbReceiverAndTheDongles_IsRegisteredOnceUnderTheSameId() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        using LianLiPlugin usbOnly = NewPlugin(new FakeEnumerator(FlexReceiver()) { ConfigureTransport = FlexChain() }, runtime);
        FakeSensorsContainer first = Load(usbOnly);
        Assert.Equal(FlexControl, Assert.Single(first.ControlSensors).Id);
        Assert.Contains("USB", Assert.Single(first.ControlSensors).Name, StringComparison.Ordinal);
        usbOnly.Close();

        var logger = new FakeLogger();
        var both = new FakeEnumerator(Dongles().Append(FlexReceiver()).ToArray()) { ConfigureTransport = FlexChainAndDongles(0) };
        using LianLiPlugin bound = NewPlugin(both, runtime, logger);
        FakeSensorsContainer second = Load(bound);
        Assert.Equal(FlexControl, Assert.Single(second.ControlSensors).Id);
        Assert.Contains("Wireless", Assert.Single(second.ControlSensors).Name, StringComparison.Ordinal);
        Assert.Equal(2, second.FanSensors.Count);
        Assert.Empty(runtime.Recall("fake/flex")!.Ids); // the pair took the chain from the receiver's memory
        Assert.Contains(logger.Messages, m => m.Contains("is bound to the L-Wireless controller's master, which drives it; left to the radio"));
        bound.Close();
        Assert.DoesNotContain(both.TransportFor("fake/flex").Writes, w => w[0] == 0x13);

        // The dongles are unplugged: the pair is stood in for, but without the chain, which its
        // receiver drives again.
        using LianLiPlugin usbAgain = NewPlugin(new FakeEnumerator(FlexReceiver()) { ConfigureTransport = FlexChain() }, runtime);
        FakeSensorsContainer third = Load(usbAgain);
        Assert.Equal(FlexControl, Assert.Single(third.ControlSensors).Id);
        Assert.Contains("USB", Assert.Single(third.ControlSensors).Name, StringComparison.Ordinal);
        Assert.Equal(2, third.FanSensors.Count);
        Assert.Empty(runtime.Recall("fake/wireless/tx")!.Ids);
        usbAgain.Close();
    }

    // The chain is driven over USB, registered on its receiver, when the radio takes it: the
    // receiver notices on its next poll, stops writing and asks for a refresh, since the host's
    // control is on the controller that no longer drives the chain. The radio letting it go again
    // while the host's control is still on the receiver (nothing claimed the ids meanwhile) puts
    // the chain back where the control is, and asks for nothing; with a wireless controller that
    // had claimed the ids it does (LianLiPluginFlexChainTests).
    [Fact]
    public void AFlexChainTakenByTheRadioWhileRunning_AsksForARefresh_AndLetGoAgainWithItsControlStillOnTheReceiver_AsksForNone() {
        var clock = new FakeClock();
        var runtime = new PluginRuntime(clock, new FakeRememberedControllerStore()) { WorkerTickIntervalMilliseconds = 20 };
        var logger = new FakeLogger();
        using LianLiPlugin plugin = NewPlugin(new FakeEnumerator(FlexReceiver()) { ConfigureTransport = FlexChain() }, runtime, logger);
        FakeSensorsContainer container = Load(plugin);
        Assert.Equal(FlexControl, Assert.Single(container.ControlSensors).Id);
        Assert.False(runtime.TryTakeRefresh(plugin, out _));

        // The worker logs the request after making it, so the line is what is waited for.
        runtime.WirelessState.RecordBoundDevices(RadioOwner, new[] { "a00000000001" });
        Assert.True(SpinWait.SpinUntil(() => logger.Messages.Contains("refresh wanted: a FLEX chain on a USB receiver is now the L-Wireless controller's"), TimeSpan.FromSeconds(5)));
        Assert.True(runtime.TryTakeRefresh(plugin, out _));
        Assert.Contains(logger.Messages, m => m.Contains("F0:a00000000001 is now bound to the L-Wireless controller's master"));

        clock.Advance(TimeSpan.FromMinutes(1));
        runtime.WirelessState.ForgetBoundDevices(RadioOwner);
        Assert.True(SpinWait.SpinUntil(() => logger.Messages.Any(m => m.Contains("F0:a00000000001 is no longer bound to the L-Wireless controller's master")), TimeSpan.FromSeconds(5)));
        Thread.Sleep(100);
        Assert.False(runtime.TryTakeRefresh(plugin, out _));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("refresh wanted: a FLEX chain on a USB receiver is driven over USB"));
        Assert.Equal(new[] { FlexControl, "LianLi/wa00000000001/f0/fan", "LianLi/wa00000000001/f1/fan" }, runtime.Recall("fake/flex")!.Ids);
        plugin.Close();
    }

    // A first-run receiver whose chain reports no fan yet keeps the placeholder, like a TL hub; once a
    // fan is reported the receiver asks for the refresh that registers it. The radio taking the
    // chain before that refresh has happened asks for nothing more: the host has none of the
    // chain's sensors on the receiver yet, so there is nothing to move.
    [Fact]
    public void APlaceholder_ForAFlexReceiverWithNoFanYet_ThenARefreshWhenOneIsReported() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()) { WorkerTickIntervalMilliseconds = 20 };
        var enumerator = new FakeEnumerator(FlexReceiver()) { ConfigureTransport = FlexChain(0) };
        var logger = new FakeLogger();
        using LianLiPlugin plugin = NewPlugin(enumerator, runtime, logger);
        FakeSensorsContainer container = Load(plugin);

        Assert.Empty(container.ControlSensors);
        Assert.Equal("LianLi/waiting", Assert.Single(container.TempSensors).Id);
        Assert.False(runtime.TryTakeRefresh(plugin, out _));

        FlexChain(2)(FlexReceiver(), enumerator.TransportFor("fake/flex"));
        Assert.True(SpinWait.SpinUntil(() => runtime.TryTakeRefresh(plugin, out _), TimeSpan.FromSeconds(5)));
        Assert.Equal(3, runtime.Recall("fake/flex")!.Ids.Count());

        runtime.WirelessState.RecordBoundDevices(RadioOwner, new[] { "a00000000001" });
        Assert.True(SpinWait.SpinUntil(() => logger.Messages.Any(m => m.Contains("F0:a00000000001 is now bound to the L-Wireless controller's master")), TimeSpan.FromSeconds(5)));
        Thread.Sleep(100);
        Assert.DoesNotContain(logger.Messages, m => m.Contains("refresh wanted: a FLEX chain on a USB receiver is now the L-Wireless controller's"));
        plugin.Close();
    }

    // What a receiver reports is remembered on the worker's thread; a fault there is logged, never
    // let out, and a receiver from a scan numbered before the saved controllers were read is not
    // remembered at all. An instance that no longer runs the worker does nothing with it: its
    // controllers are being closed for the refresh that replaces it.
    [Fact]
    public void AFlexReceiversChange_ThatCannotBeRemembered_OrReachesAClosingInstance_IsLogged() {
        var store = new FakeRememberedControllerStore { UnreadableLoads = 1 };
        var runtime = new PluginRuntime(new FakeClock(), store);
        var logger = new FakeLogger();
        var transport = new FakeDeviceTransport();
        FlexChain()(FlexReceiver(), transport);
        var controller = new FlexReceiverController(0, transport, FlexReceiverFamily.TlFlex, new FakeClock(), logger, runtime.WirelessState);
        controller.SettleOwnership();
        var plan = new ControllerPlan(DeviceKind.FlexReceiver, FlexReceiver());

        using LianLiPlugin unread = NewPlugin(new FakeEnumerator(Sli("a")), runtime, logger);
        _ = Load(unread);
        using LianLiPlugin read = NewPlugin(new FakeEnumerator(Sli("a")), runtime, logger);
        _ = Load(read);

        // Stopped by the next instance's scan, which read the file and moved the numbering on.
        unread.OnFlexReceiverChanged(plan, 7, controller, FlexReceiverChange.ReleasedByRadio);
        Assert.Contains("  fake/flex: the radio let its chain go while this plugin instance is closing; left to the next one", logger.Messages);
        unread.OnFlexReceiverChanged(plan, 7, controller, FlexReceiverChange.TakenByRadio);
        Assert.Contains("  fake/flex: the radio took its chain while this plugin instance is closing; left to the next one", logger.Messages);
        unread.OnFlexReceiverChanged(plan, 7, controller, FlexReceiverChange.FanReported);
        Assert.Contains("  fake/flex: a fan was reported while this plugin instance is closing; left to the next one", logger.Messages);
        unread.OnFlexReceiverChanged(plan, 7, controller, FlexReceiverChange.AnotherReceiverAnswered);
        Assert.Contains("  fake/flex: another receiver answered while this plugin instance is closing; left to the next one", logger.Messages);
        Assert.Null(runtime.Recall("fake/flex"));
        Assert.False(runtime.TryTakeRefresh(read, out _));

        // Running the worker again, with the numbering it had: nothing of what it reports is kept.
        runtime.Start(unread, Array.Empty<IFanDevice>(), logger);
        unread.OnFlexReceiverChanged(plan, 7, controller, FlexReceiverChange.ReleasedByRadio);
        Assert.Contains("  fake/flex: its sensors were numbered before the saved controllers were read; not remembered", logger.Messages);
        Assert.Null(runtime.Recall("fake/flex"));

        runtime.Start(read, Array.Empty<IFanDevice>(), logger);
        store.DuringSave = () => throw new InvalidOperationException("disk full");
        read.OnFlexReceiverChanged(plan, 7, controller, FlexReceiverChange.ReleasedByRadio);
        Assert.Contains("  fake/flex: the change it reported could not be remembered: disk full", logger.Messages);
        store.DuringSave = null;

        read.OnFlexReceiverChanged(plan, 7, controller, FlexReceiverChange.AnotherReceiverAnswered);
        Assert.True(runtime.TryTakeRefresh(read, out string reason));
        Assert.Equal("a USB receiver now answers as another chain", reason);
        read.Close();
        unread.Close();
    }

    // A receiver's change delivered after Initialize and before Load has published what it
    // registered: the chain let go is remembered under its receiver, the chain taken is left to
    // the pair's memory, and neither asks for a refresh, since Load registers the chain under
    // whoever the memory names when it runs. The FLEX chain tests reach this from the receiver's
    // own worker, which may or may not beat Load; this is the same callback, delivered by hand.
    [Fact]
    public void AFlexReceiversChange_BeforeLoadHasPublished_IsRememberedAndAsksForNoRefresh() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        var logger = new FakeLogger();
        var transport = new FakeDeviceTransport();
        FlexChain()(FlexReceiver(), transport);
        var controller = new FlexReceiverController(0, transport, FlexReceiverFamily.TlFlex, new FakeClock(), logger, runtime.WirelessState);
        controller.SettleOwnership();
        var plan = new ControllerPlan(DeviceKind.FlexReceiver, FlexReceiver());

        using LianLiPlugin plugin = NewPlugin(new FakeEnumerator(Sli("a")), runtime, logger);
        plugin.Initialize();

        plugin.OnFlexReceiverChanged(plan, 7, controller, FlexReceiverChange.ReleasedByRadio);
        Assert.Equal("fake/flex", runtime.OwnerOf(FlexControl));
        plugin.OnFlexReceiverChanged(plan, 7, controller, FlexReceiverChange.TakenByRadio);

        Assert.False(runtime.TryTakeRefresh(plugin, out _));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("refresh wanted", StringComparison.Ordinal));
        plugin.Close();
    }

    [Fact]
    public void AWirelessDeviceHeardAfterLoad_AsksFanControlForARefresh() {
        var clock = new FakeClock();
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()) { WorkerTickIntervalMilliseconds = 20 };
        int heard = 0;
        using LianLiPlugin plugin = NewPlugin(
            new FakeEnumerator(Dongles()) { ConfigureTransport = GroupChecksInWhen(() => Volatile.Read(ref heard) == 1) }, runtime, clock: clock);
        FakeSensorsContainer container = Load(plugin);
        Assert.Empty(container.ControlSensors);
        Assert.Equal("LianLi/waiting", Assert.Single(container.TempSensors).Id); // so FanControl hears the refresh
        int refreshes = 0;
        plugin.RefreshRequested += () => refreshes++;

        Volatile.Write(ref heard, 1);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(SpinWait.SpinUntil(() => { plugin.Update(); return refreshes == 1; }, TimeSpan.FromSeconds(10)));
        plugin.Close();
    }

    // A device the pair first hears while Load is registering a later controller - after the pair's
    // own registration, before Load published what it registered: nobody registers it, and the
    // worker's callback finds nothing published to compare against. Load's check of the memory
    // after publishing is what catches it (a claim remembered before the publication is in the
    // memory Load reads after it), or, had the claim come after, the callback's own check would.
    [Fact]
    public void AWirelessDeviceHeardWhileALaterControllerIsBeingRegistered_GetsARefresh() {
        var clock = new FakeClock();
        var runtime = new PluginRuntime(clock, new FakeRememberedControllerStore()) { WorkerTickIntervalMilliseconds = 20 };
        var log = new FakeLogger();
        var tx = new LocatedDevice(0x0416, 0x8040, "fake/tx", null);
        var rx = new LocatedDevice(0x0416, 0x8041, "fake/rx", null);
        var uni = Sli("fake/uni");
        var radio = new FakeWirelessRig();
        var first = new FakeWirelessRecord(new byte[] { 0xA0, 0, 0, 0, 0, 1 }, FakeWirelessRig.MasterMac) {
            FanCountByte = 2,
            FanTypes = new byte[] { 51, 51, 0, 0 },
            Rpm = new[] { 1200, 1150, 0, 0 },
        };
        var second = new FakeWirelessRecord(new byte[] { 0xA0, 0, 0, 0, 0, 2 }, FakeWirelessRig.MasterMac) {
            FanCountByte = 2,
            FanTypes = new byte[] { 51, 51, 0, 0 },
            Receiver = 2,
        };
        byte[] initial = WirelessProtocolTests.ListReply(1, first.ToBytes());
        byte[] added = WirelessProtocolTests.ListReply(2, first.ToBytes(), second.ToBytes());
        int heard = 0;
        Action<LocatedDevice, FakeDeviceTransport> configure = (info, transport) => transport.ReplyFor = info.ProductId == 0x8040
            ? _ => radio.MasterReply() ?? new byte[64]
            : _ => Volatile.Read(ref heard) == 0 ? initial : added;
        using (LianLiPlugin earlier = NewPlugin(new FakeEnumerator(tx, rx) { ConfigureTransport = configure }, runtime, log, clock)) {
            _ = Load(earlier);
            earlier.Close();
        }

        // The pair keeps index 0, so it registers before the new Uni controller.
        using LianLiPlugin plugin = NewPlugin(new FakeEnumerator(uni, tx, rx) { ConfigureTransport = configure }, runtime, log, clock);
        plugin.Initialize();
        var sensors = new FakeSensorsContainer();
        const string addedId = "LianLi/wa00000000002/ctl";
        plugin.Registering += key => {
            if (key != uni.DevicePath) {
                return;
            }

            Assert.Single(sensors.ControlSensors);
            Volatile.Write(ref heard, 1);
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.True(SpinWait.SpinUntil(() => runtime.OwnerOf(addedId) == tx.DevicePath, TimeSpan.FromSeconds(5)));
        };
        plugin.Load(sensors);

        Assert.DoesNotContain(sensors.ControlSensors, sensor => sensor.Id == addedId);
        Assert.Equal(tx.DevicePath, runtime.OwnerOf(addedId));
        Assert.True(SpinWait.SpinUntil(() => runtime.TryTakeRefresh(plugin, out _), TimeSpan.FromSeconds(5)));
        Assert.Contains("refresh wanted: a controller reported sensors while they were being registered", log.Messages);
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

    // A pair an earlier process remembered with nothing (so it is not new, and no placeholder is
    // owed to a device still to come) is stood in for when its dongles open slowly, and its late
    // build hears a group before Load runs: the stand-in exposes nothing, the memory has the
    // group's sensors, and Load asks for the refresh that registers them. Nothing else is
    // registered, so FanControl would never hear that refresh; the placeholder is registered for
    // the correction already pending, exactly as for a device still to come.
    [Fact]
    public void APlaceholder_WhenNothingRegisters_ButACorrectionIsAlreadyWanted() {
        var store = new FakeRememberedControllerStore();
        using (LianLiPlugin earlier = NewPlugin(new FakeEnumerator(Dongles()) { ConfigureTransport = GroupChecksInLate(100) }, new PluginRuntime(new FakeClock(), store))) {
            Assert.Single(Load(earlier).TempSensors);
            earlier.Close();
        }

        using var gate = new ManualResetEventSlim(false);
        var runtime = new PluginRuntime(new FakeClock(), store) { WorkerTickIntervalMilliseconds = 20 };
        var slow = new FakeEnumerator(Dongles()) {
            ConfigureTransport = (info, transport) => {
                GroupChecksInLate(0)(info, transport);
                if (info.ProductId == 0x8040) {
                    transport.BlockReadsUntil = gate;
                }
            },
        };
        var logger = new FakeLogger();
        using LianLiPlugin plugin = NewPlugin(slow, runtime, logger);
        plugin.BuildDeadlineMilliseconds = 50;
        plugin.Initialize();
        Assert.False(runtime.IsNew("fake/wireless/tx"));
        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => logger.Messages.Contains("  fake/wireless/tx finished opening after the scan; its stand-in took it"), TimeSpan.FromSeconds(10)));
        Assert.Equal(new[] { FlexControl }, runtime.Recall("fake/wireless/tx")!.Channels.Select(c => c.ControlId));
        Assert.False(runtime.TryTakeRefresh(plugin, out _));

        var container = new FakeSensorsContainer();
        plugin.Load(container);

        Assert.Empty(container.ControlSensors);
        Assert.Empty(container.FanSensors);
        Assert.Equal("LianLi/waiting", Assert.Single(container.TempSensors).Id);
        Assert.True(runtime.TryTakeRefresh(plugin, out string reason));
        Assert.Equal("a controller came back with sensors the scan did not have", reason);
        Assert.Contains("nothing to register yet but a refresh is already wanted; a placeholder sensor is registered so FanControl hears it", logger.Messages);
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
        var clock = new FakeClock();
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()) { WorkerTickIntervalMilliseconds = 20 };
        int heard = 0;
        var enumerator = new FakeEnumerator(Dongles()) { ConfigureTransport = GroupChecksInWhen(() => Volatile.Read(ref heard) == 1) };
        using LianLiPlugin plugin = NewPlugin(enumerator, runtime, clock: clock);
        plugin.Initialize();

        // The group checks in, and the worker's poll hears it - all before FanControl gets around to Load.
        Volatile.Write(ref heard, 1);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(SpinWait.SpinUntil(() => runtime.Recall("fake/wireless/tx")!.Channels.Count == 1, TimeSpan.FromSeconds(10)));
        var container = new FakeSensorsContainer();
        plugin.Load(container);

        Assert.Single(container.ControlSensors);
        FakeDeviceTransport receiver = enumerator.TransportFor("fake/wireless/rx");
        int reads = receiver.InterruptReadCount;
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(SpinWait.SpinUntil(() => receiver.InterruptReadCount > reads, TimeSpan.FromSeconds(10))); // a further poll
        Assert.False(runtime.TryTakeRefresh(plugin, out _));
        plugin.Close();
    }

    [Fact]
    public void AWirelessDeviceHeardBeforeLoad_IsRememberedAndSaved() {
        var clock = new FakeClock();
        var store = new FakeRememberedControllerStore();
        var runtime = new PluginRuntime(new FakeClock(), store) { WorkerTickIntervalMilliseconds = 20 };
        int heard = 0;
        var enumerator = new FakeEnumerator(Dongles()) { ConfigureTransport = GroupChecksInWhen(() => Volatile.Read(ref heard) == 1) };
        using LianLiPlugin plugin = NewPlugin(enumerator, runtime, clock: clock);
        plugin.Initialize();
        Assert.Empty(runtime.Recall("fake/wireless/tx")!.Channels);
        Volatile.Write(ref heard, 1);
        clock.Advance(TimeSpan.FromSeconds(2));
        // The worker remembers the group and then saves it, so wait for the save itself.
        Assert.True(SpinWait.SpinUntil(
            () => store.Snapshot().Any(stored => stored.Key == "fake/wireless/tx" && stored.Controller.Channels.Count == 1),
            TimeSpan.FromSeconds(10)));

        var container = new FakeSensorsContainer();
        plugin.Load(container);

        string control = Assert.Single(container.ControlSensors).Id;
        Assert.Equal(control, Assert.Single(runtime.Recall("fake/wireless/tx")!.Channels).ControlId);
        Assert.Equal(control, Assert.Single(Assert.Single(store.Snapshot()).Controller.Channels).ControlId);
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
            LianLiPlugin.HasUnregisteredSensor(ids, loaded ? new System.Collections.Generic.Dictionary<string, string> { ["a"] = "x", ["b"] = "x" } : null));

    // The transmitter always answers; the receiver lists nothing for emptyReads reads, then a group.
    private static void SeedPair(LocatedDevice info, FakeDeviceTransport transport, int emptyReads) {
        var rig = new FakeWirelessRig();
        if (info.ProductId == 0x8040) {
            for (int i = 0; i < 100; i++) {
                transport.ReadReplies.Enqueue(rig.MasterReply()!);
            }

            return;
        }

        byte[] group = WirelessProtocolTests.Record(
            new byte[] { 0xA0, 0, 0, 0, 0, 1 }, FakeWirelessRig.MasterMac, 8, 1, 0, 2,
            new byte[] { 36, 36, 0, 0 }, new[] { 1000, 1100, 0, 0 }, new byte[] { 100, 100, 0, 0 }, 1);
        for (int i = 0; i < 100; i++) {
            transport.ReadReplies.Enqueue(i < emptyReads
                ? WirelessProtocolTests.ListReply(0)
                : WirelessProtocolTests.ListReply(1, group));
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
        var clock = new FakeClock();
        var store = new FakeRememberedControllerStore();
        var runtime = new PluginRuntime(new FakeClock(), store) { WorkerTickIntervalMilliseconds = 20 };
        int heard = 0;
        using LianLiPlugin plugin = NewPlugin(
            new FakeEnumerator(Dongles()) { ConfigureTransport = GroupChecksInWhen(() => Volatile.Read(ref heard) == 1) }, runtime, clock: clock);
        Assert.Empty(Load(plugin).ControlSensors);

        Volatile.Write(ref heard, 1);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(SpinWait.SpinUntil(() => runtime.TryTakeRefresh(plugin, out _), TimeSpan.FromSeconds(10)));
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

    // A temperature reported after Load (a wireless water block's coolant, say) is a sensor the
    // host does not have, like any other.
    [Fact]
    public void ATemperatureReportedAfterLoad_AsksForARefresh() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        using LianLiPlugin plugin = NewPlugin(new FakeEnumerator(Sli("a")), runtime);
        _ = Load(plugin);

        var device = new FakeFanDevice(new[] { "LianLi/wa00000000001/empty/ctl" }, new[] { "LianLi/wa00000000001/coolant/temp" });
        device.UnpopulatedChannels.Add(0); // an empty channel is no sensor of the host's
        plugin.OnSensorsReported(
            new ControllerPlan(DeviceKind.WirelessTransmitter, new LocatedDevice(0x0416, 0x8040, "tx", null), new LocatedDevice(0x0416, 0x8041, "rx", null)),
            1,
            device,
            "a wireless device checked in after the scan");

        Assert.True(runtime.TryTakeRefresh(plugin, out string reason));
        Assert.Equal("a wireless device checked in after the scan", reason);
        plugin.Close();
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
    // instance's scan ran under, and only while that instance owns the worker: once it has closed
    // and the next instance has read the saved controllers, it opens nothing, so it can never claim
    // a device at a guessed index the file gives another controller, nor hold the device against
    // the next instance's scan for an open nobody would adopt.
    [Fact]
    public void ARebuildFromAnInstanceThatHasClosed_OrOfAnEarlierNumbering_OpensNothing() {
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

        Assert.Equal("a is still being opened by another build, or by the next scan, or this plugin instance has closed", refused.Message);
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
        var clock = new FakeClock();
        runtime.WorkerTickIntervalMilliseconds = 20;
        int heard = 0;
        using LianLiPlugin plugin = NewPlugin(
            new FakeEnumerator(Dongles()) { ConfigureTransport = GroupChecksInWhen(() => Volatile.Read(ref heard) == 1) }, runtime, clock: clock);
        plugin.Initialize();
        Volatile.Write(ref heard, 1);
        clock.Advance(TimeSpan.FromSeconds(2));
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
        byte[] foreign = WirelessProtocolTests.Record(
            new byte[] { 0xB0, 0, 0, 0, 0, 1 }, new byte[] { 0x99, 0x99, 0x99, 0x99, 0x99, 0x99 }, 8, 1, 0, 2,
            new byte[] { 36, 36, 0, 0 }, new[] { 1000, 1100, 0, 0 }, new byte[] { 100, 100, 0, 0 }, 1);
        var rig = new FakeWirelessRig();
        Action<LocatedDevice, FakeDeviceTransport> neighbourOnly = (info, transport) => {
            for (int i = 0; i < 100; i++) {
                transport.ReadReplies.Enqueue(info.ProductId == 0x8040
                    ? rig.MasterReply()!
                    : WirelessProtocolTests.ListReply(1, foreign));
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
        byte[] strimer = WirelessProtocolTests.Record(
            new byte[] { 0xA0, 0, 0, 0, 0, 1 }, FakeWirelessRig.MasterMac, 8, 1, 5, 0,
            new byte[4], new int[4], new byte[4], 1);
        var rig = new FakeWirelessRig();
        var enumerator = new FakeEnumerator(Dongles()) {
            ConfigureTransport = (info, transport) => {
                for (int i = 0; i < 100; i++) {
                    transport.ReadReplies.Enqueue(info.ProductId == 0x8040
                        ? rig.MasterReply()!
                        : WirelessProtocolTests.ListReply(1, strimer));
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

        public bool IsFaulted => false;

        public void Write(byte[] report) {
        }

        public void SetFeature(byte[] report) {
        }

        public byte[] GetInputReport(byte reportId, int length) => new byte[length];

        public byte[] Read(int length) {
            if (Interlocked.Increment(ref _reads) == 1) {
                // An undetected record (detected bit clear) when no fan has answered yet.
                return CommandPacket.Build(0xA1, _fansAtFirst == 0 ? (byte)0x00 : (byte)0x80, 0x03, 0xE8);
            }

            _entered.Set();
            Assert.True(_allow.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            return CommandPacket.Build(0xA1, 0x80, 0x03, 0xE8, 0x81, 0x04, 0x4C);
        }

        public void Dispose() {
        }
    }
}
