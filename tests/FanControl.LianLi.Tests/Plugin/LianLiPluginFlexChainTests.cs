using System;
using System.Linq;
using System.Threading;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Plugin;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Tests.Fakes;
using FanControl.LianLi.Tests.Protocol;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Plugin;

/// <summary>
/// One FLEX chain, two possible drivers - its USB receiver and the L-Wireless pair - under one set
/// of sensor ids, as FanControl drives the plugin: a new plugin object on every refresh, one
/// process, and the chain changing hands between scans, during a scan, while running, while its
/// receiver cannot be reached, while an instance is closing, and before Load. FanControl must
/// never be given the chain's ids twice, its control must reach whichever drives the chain, and a
/// change of hands must bring a refresh - and nothing else may.
/// </summary>
public sealed class LianLiPluginFlexChainTests {
    private static readonly byte[] ChainMac = { 0xA0, 0, 0, 0, 0, 1 };
    private const string ChainText = "a00000000001";
    private const string ControlId = "LianLi/wa00000000001/ctl";
    private const string FlexPath = "fake/flex";
    private const string TransmitterPath = "fake/tx";
    private const string ReceiverPath = "fake/rx";

    private static FakeWirelessRecord Record(byte[]? master = null, byte[]? mac = null)
        => new FakeWirelessRecord(mac ?? ChainMac, master ?? FakeWirelessRig.MasterMac) {
            FanCountByte = 2,
            FanTypes = new byte[] { 51, 51, 0, 0 },
            Rpm = new[] { 1200, 1150, 0, 0 },
        };

    private static byte[] UsbStatus(FakeWirelessRecord? record = null) => FlexReceiverProtocolTests.StatusReply(record ?? Record(new byte[6]));

    private static Func<byte[], byte[]> UsbReplies(byte[] status) => w => w[0] == 0x12 ? status : new byte[] { 0x13, 0 };

    private static LocatedDevice Flex() => new LocatedDevice(0x43A8, 0x0101, FlexPath, null);

    private static LocatedDevice Transmitter() => new LocatedDevice(0x0416, 0x8040, TransmitterPath, null);

    private static LocatedDevice Receiver() => new LocatedDevice(0x0416, 0x8041, ReceiverPath, null);

    private static LianLiPlugin NewPlugin(FakeEnumerator enumerator, PluginRuntime runtime, FakeLogger? logger = null, IClock? clock = null)
        => new LianLiPlugin(enumerator, new DeviceCatalog(), clock ?? new FakeClock(), new FakeDelay(), logger ?? new FakeLogger(), LConnectDirectory.Absent, runtime);

    private static FakeSensorsContainer Load(LianLiPlugin plugin) {
        plugin.Initialize();
        var container = new FakeSensorsContainer();
        plugin.Load(container);
        return container;
    }

    // A scan that finds the chain on USB alone, registered and remembered under its receiver. With
    // the pair remembered too, this scan stands in for it and starts rebuilding it in the
    // background; that rebuild must fail (the enumerator refuses the dongles) and be over before
    // the scan is closed, or it would still own the pair's build when the test's own scan runs,
    // which would then stand in for the pair instead of building it.
    private static void RememberOnUsb(PluginRuntime runtime) {
        var enumerator = new FakeEnumerator(Flex()) {
            ConfigureTransport = (_, transport) => transport.ReplyFor = UsbReplies(UsbStatus()),
            FailOpenWhen = info => info.DevicePath != FlexPath,
        };
        using LianLiPlugin plugin = NewPlugin(enumerator, runtime);
        FakeSensorsContainer sensors = Load(plugin);
        Assert.Equal(ControlId, Assert.Single(sensors.ControlSensors).Id);
        plugin.Close();
        Assert.True(SpinWait.SpinUntil(() => !runtime.IsBuilding(TransmitterPath), TimeSpan.FromSeconds(5)));
    }

    // A scan that finds the dongles alone, hearing what listReply says; as above, a stand-in's
    // rebuild of a remembered receiver is refused and over before the scan is closed.
    private static void RememberOnRadio(PluginRuntime runtime, byte[] listReply) {
        var radio = new FakeWirelessRig();
        var enumerator = new FakeEnumerator(Transmitter(), Receiver()) {
            ConfigureTransport = (info, transport) => transport.ReplyFor = info.ProductId == 0x8040
                ? _ => radio.MasterReply() ?? new byte[64]
                : _ => listReply,
            FailOpenWhen = info => info.DevicePath == FlexPath,
        };
        using LianLiPlugin plugin = NewPlugin(enumerator, runtime);
        _ = Load(plugin);
        plugin.Close();
        Assert.True(SpinWait.SpinUntil(() => !runtime.IsBuilding(FlexPath), TimeSpan.FromSeconds(5)));
    }

    // The transmitter's packets carrying a speed for the chain (the first chunk of SyncPwm's
    // payload: command 0x10 at byte 5, the address at 6-11, the four slot PWMs at 21-24).
    private static byte[][] SpeedsSentToChain(FakeDeviceTransport transmitter)
        => transmitter.SnapshotTransfers()
            .Select(t => t.Value)
            .Where(w => w[0] == 0x10 && w[1] == 0 && w[5] == 0x10 && w.Skip(6).Take(6).SequenceEqual(ChainMac))
            .ToArray();

    // The pair's first list reports the chain bound and its next reports
    // it unbound, so the pair keeps the chain's sensors without driving it; the receiver, built in
    // the same scan, drives it. The chain is registered once, on the receiver.
    [Fact]
    public void AChainUnboundDuringTheWirelessDiscovery_IsRegisteredOnce_OnItsReceiver() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        RememberOnRadio(runtime, WirelessProtocolTests.ListReply(1, Record().ToBytes()));

        var radio = new FakeWirelessRig();
        byte[] bound = WirelessProtocolTests.ListReply(1, Record().ToBytes());
        byte[] unbound = WirelessProtocolTests.ListReply(1, Record(new byte[6]).ToBytes());
        int reads = 0;
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(Flex(), Transmitter(), Receiver()) {
            ConfigureTransport = (info, transport) => transport.ReplyFor = info.ProductId switch {
                0x8040 => _ => radio.MasterReply() ?? new byte[64],
                0x8041 => _ => Interlocked.Increment(ref reads) == 1 ? bound : unbound,
                _ => UsbReplies(UsbStatus()),
            },
        };
        using LianLiPlugin plugin = NewPlugin(enumerator, runtime, logger);
        FakeSensorsContainer sensors = Load(plugin);

        Assert.False(runtime.WirelessState.IsBoundToMaster(ChainText));
        Assert.Equal(ControlId, Assert.Single(sensors.ControlSensors).Id);
        Assert.Contains("USB", Assert.Single(sensors.ControlSensors).Name, StringComparison.Ordinal);
        Assert.Equal(2, sensors.FanSensors.Count);
        Assert.Equal(FlexPath, runtime.OwnerOf(ControlId));
        Assert.Empty(runtime.Recall(TransmitterPath)!.Ids);
        plugin.Close();
    }

    // The pair alone, with nothing remembered: its first list reports the chain bound and its next
    // reports it unbound, so the pair keeps the chain's sensors without driving them, and no
    // memory holds them. Load registers them under the pair, the first controller to report them,
    // as ids nothing remembers go to their first reporter; nothing is asked for.
    [Fact]
    public void AChainThePairOnlyRetains_WithNothingRememberedAndNoReceiver_IsRegisteredUnderThePairAsItsFirstReporter() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        var radio = new FakeWirelessRig();
        byte[] bound = WirelessProtocolTests.ListReply(1, Record().ToBytes());
        byte[] unbound = WirelessProtocolTests.ListReply(1, Record(new byte[6]).ToBytes());
        int reads = 0;
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(Transmitter(), Receiver()) {
            ConfigureTransport = (info, transport) => transport.ReplyFor = info.ProductId == 0x8040
                ? _ => radio.MasterReply() ?? new byte[64]
                : _ => Interlocked.Increment(ref reads) == 1 ? bound : unbound,
        };
        using LianLiPlugin plugin = NewPlugin(enumerator, runtime, logger);
        FakeSensorsContainer sensors = Load(plugin);

        Assert.Equal(ControlId, Assert.Single(sensors.ControlSensors).Id);
        Assert.Contains("Wireless", Assert.Single(sensors.ControlSensors).Name, StringComparison.Ordinal);
        Assert.Equal(2, sensors.FanSensors.Count);
        Assert.Null(runtime.OwnerOf(ControlId));
        Assert.False(runtime.TryTakeRefresh(plugin, out _));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("is registered under"));
        plugin.Close();
    }

    // The chain was registered on its receiver; on the next scan the receiver's USB is gone (stood
    // in for) and the pair opens before the chain checks in. When it does, bound to our master, the
    // pair drives it: a refresh is asked for from the pair's side, since the receiver cannot see
    // anything, and the target FanControl set on the stand-in's control reaches the chain over the
    // radio meanwhile.
    [Fact]
    public void UsbGoneThenTheRadioChecksIn_AsksForARefresh_AndTheCurveReachesTheChainOverTheRadio() {
        var clock = new FakeClock();
        var runtime = new PluginRuntime(clock, new FakeRememberedControllerStore()) { WorkerTickIntervalMilliseconds = 20 };
        RememberOnUsb(runtime);

        var radio = new FakeWirelessRig();
        byte[] bound = WirelessProtocolTests.ListReply(1, Record().ToBytes());
        byte[] empty = WirelessProtocolTests.ListReply(0);
        int heard = 0;
        var enumerator = new FakeEnumerator(Flex(), Transmitter(), Receiver()) {
            FailOpenWhen = info => info.ProductId == 0x0101,
            ConfigureTransport = (info, transport) => transport.ReplyFor = info.ProductId == 0x8040
                ? _ => radio.MasterReply() ?? new byte[64]
                : _ => Volatile.Read(ref heard) == 0 ? empty : bound,
        };
        var logger = new FakeLogger();
        using LianLiPlugin plugin = NewPlugin(enumerator, runtime, logger, clock);
        FakeSensorsContainer sensors = Load(plugin);
        Assert.Equal(ControlId, Assert.Single(sensors.ControlSensors).Id);
        Assert.Contains("USB", Assert.Single(sensors.ControlSensors).Name, StringComparison.Ordinal);
        Assert.False(runtime.TryTakeRefresh(plugin, out _));
        Assert.Single(sensors.ControlSensors).Set(60);
        Assert.Equal(60, runtime.WirelessState.ChainTarget(ChainText));

        Volatile.Write(ref heard, 1);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(SpinWait.SpinUntil(() => runtime.Recall(TransmitterPath)?.Ids.Contains(ControlId) == true, TimeSpan.FromSeconds(5)));
        Assert.True(runtime.WirelessState.IsBoundToMaster(ChainText));
        Assert.True(SpinWait.SpinUntil(() => runtime.TryTakeRefresh(plugin, out _), TimeSpan.FromSeconds(5)));
        Assert.Empty(runtime.Recall(FlexPath)!.Ids);

        // The pair sends the chain the 60% the stand-in's control was given.
        FakeDeviceTransport transmitter = enumerator.TransportFor(TransmitterPath);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(SpinWait.SpinUntil(() => SpeedsSentToChain(transmitter).Length > 0, TimeSpan.FromSeconds(5)));
        WirelessDeviceRecord record = WirelessProtocol.DecodeRecord(Record().ToBytes(), 0);
        byte pwm = WirelessProtocol.FanPwm(60, WirelessProtocol.GroupDutyFloor(record), WirelessProtocol.GroupIdleDuty(record));
        Assert.Equal(new[] { pwm, pwm, pwm, pwm }, SpeedsSentToChain(transmitter)[0].Skip(21).Take(4));
        plugin.Close();
        Assert.Contains("refresh wanted: a FLEX chain on a USB receiver is now the L-Wireless controller's", logger.Messages);
    }

    // A refresh closes the instance; the wireless controller closing forgets what it drove, which a
    // receiver mid-poll would read as the radio letting the chain go. The closing instance does
    // nothing with it: no memory change, no refresh.
    [Fact]
    public void TheWirelessControllerClosingForARefresh_IsNotTakenForTheRadioLettingTheChainGo() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()) { WorkerTickIntervalMilliseconds = 20 };
        var radio = new FakeWirelessRig();
        byte[] bound = WirelessProtocolTests.ListReply(1, Record().ToBytes());
        var enumerator = new FakeEnumerator(Flex(), Transmitter(), Receiver()) {
            ConfigureTransport = (info, transport) => transport.ReplyFor = info.ProductId switch {
                0x8040 => _ => radio.MasterReply() ?? new byte[64],
                0x8041 => _ => bound,
                _ => UsbReplies(UsbStatus()),
            },
        };
        var logger = new FakeLogger();
        using LianLiPlugin plugin = NewPlugin(enumerator, runtime, logger);
        FakeSensorsContainer sensors = Load(plugin);
        Assert.Contains("Wireless", Assert.Single(sensors.ControlSensors).Name, StringComparison.Ordinal);
        Assert.Empty(runtime.Recall(FlexPath)!.Ids);

        // Hold the receiver inside a status read, close the plugin, and let the read finish only
        // once the wireless controller has been closed.
        using var held = new ManualResetEventSlim(false);
        FakeDeviceTransport flex = enumerator.TransportFor(FlexPath);
        FakeDeviceTransport transmitter = enumerator.TransportFor(TransmitterPath);
        int reads = flex.InterruptReadCount;
        flex.BlockReadsUntil = held;
        Assert.True(SpinWait.SpinUntil(() => flex.InterruptReadCount > reads, TimeSpan.FromSeconds(5)));
        // The wireless controller lets its dongles go and only then forgets what it drove, so the
        // forgetting is what is waited for; the dongles are closed by then.
        bool wirelessClosedFirst = false;
        bool forgottenFirst = false;
        var release = new Thread(() => {
            forgottenFirst = SpinWait.SpinUntil(() => !runtime.WirelessState.IsBoundToMaster(ChainText), TimeSpan.FromSeconds(5));
            wirelessClosedFirst = transmitter.IsDisposed;
            held.Set();
        });
        release.Start();
        plugin.Close();
        release.Join();

        Assert.True(wirelessClosedFirst);
        Assert.True(forgottenFirst);

        Assert.True(flex.IsDisposed);
        Assert.Contains(logger.Messages, m => m.Contains("F0:a00000000001 is no longer bound to the L-Wireless controller's master"));
        Assert.Contains("  fake/flex: the radio let its chain go while this plugin instance is closing; left to the next one", logger.Messages);
        Assert.DoesNotContain(logger.Messages, m => m.StartsWith("refresh wanted:", StringComparison.Ordinal));
        Assert.Empty(runtime.Recall(FlexPath)!.Ids);
        Assert.Equal(TransmitterPath, runtime.OwnerOf(ControlId));
    }

    // A chain remembered with three fans comes back reporting two, so its receiver is stood in for
    // with all three ids; the third fan answering later adds a sensor the host already has, and
    // asks for nothing.
    [Fact]
    public void AFanReportedLater_ForAChainWhoseStandInAlreadyRegistersIt_AsksForNoRefresh() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()) { WorkerTickIntervalMilliseconds = 20 };
        var three = Record(new byte[6]);
        three.FanCountByte = 3;
        three.FanTypes = new byte[] { 51, 51, 51, 0 };
        var first = new FakeEnumerator(Flex()) { ConfigureTransport = (_, transport) => transport.ReplyFor = UsbReplies(UsbStatus(three)) };
        using (LianLiPlugin earlier = NewPlugin(first, runtime)) {
            Assert.Equal(3, Load(earlier).FanSensors.Count);
            earlier.Close();
        }

        var enumerator = new FakeEnumerator(Flex()) { ConfigureTransport = (_, transport) => transport.ReplyFor = UsbReplies(UsbStatus()) };
        using LianLiPlugin plugin = NewPlugin(enumerator, runtime);
        FakeSensorsContainer sensors = Load(plugin);
        Assert.Equal(3, sensors.FanSensors.Count);
        Assert.False(runtime.TryTakeRefresh(plugin, out _));

        FakeDeviceTransport flex = enumerator.TransportFor(FlexPath);
        flex.ReplyFor = UsbReplies(UsbStatus(three));
        int reads = flex.InterruptReadCount;
        Assert.True(SpinWait.SpinUntil(() => flex.InterruptReadCount >= reads + 3, TimeSpan.FromSeconds(5)));
        Assert.Equal(3, runtime.Recall(FlexPath)!.FanSpeeds.Count);
        Assert.False(runtime.TryTakeRefresh(plugin, out _));
        plugin.Close();
    }

    // A pair remembered from an earlier scan runs past the deadline; its receiver, remembered with
    // the chain, waits with it, so both are stood in for, the receiver with the chain and the pair
    // without. The late pair then hears the chain bound and takes it, which asks for the refresh,
    // and the late receiver is taken by its stand-in with the chain left to the radio.
    [Fact]
    public void ALatePairBuild_ThatTakesTheChainFromItsRegisteredReceiver_AsksForARefresh() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()) { WorkerTickIntervalMilliseconds = 20 };
        RememberOnRadio(runtime, WirelessProtocolTests.ListReply(0));
        RememberOnUsb(runtime);

        using var slowMaster = new ManualResetEventSlim(false);
        var radio = new FakeWirelessRig();
        byte[] bound = WirelessProtocolTests.ListReply(1, Record().ToBytes());
        var enumerator = new FakeEnumerator(Flex(), Transmitter(), Receiver()) {
            ConfigureTransport = (info, transport) => {
                transport.ReplyFor = info.ProductId switch {
                    0x8040 => _ => radio.MasterReply() ?? new byte[64],
                    0x8041 => _ => bound,
                    _ => UsbReplies(UsbStatus()),
                };
                if (info.ProductId == 0x8040) {
                    transport.BlockReadsUntil = slowMaster;
                }
            },
        };
        var logger = new FakeLogger();
        using LianLiPlugin plugin = NewPlugin(enumerator, runtime, logger);
        plugin.BuildDeadlineMilliseconds = 50;
        FakeSensorsContainer sensors = Load(plugin);

        Assert.Equal(ControlId, Assert.Single(sensors.ControlSensors).Id);
        Assert.Contains("USB", Assert.Single(sensors.ControlSensors).Name, StringComparison.Ordinal);
        Assert.Contains(logger.Messages, m => m.Contains("fake/flex still opening after 50 ms"));
        Assert.False(runtime.TryTakeRefresh(plugin, out _));

        // A request is logged after it is made, so the line is what is waited for; the request
        // itself is then there to take.
        slowMaster.Set();
        Assert.True(SpinWait.SpinUntil(() => logger.Messages.Contains("refresh wanted: a FLEX chain on a USB receiver is now the L-Wireless controller's"), TimeSpan.FromSeconds(10)));
        Assert.True(runtime.TryTakeRefresh(plugin, out _));
        Assert.Equal(TransmitterPath, runtime.OwnerOf(ControlId));
        Assert.True(SpinWait.SpinUntil(() => logger.Messages.Contains("  fake/flex finished opening after the scan; its stand-in took it"), TimeSpan.FromSeconds(10)));
        Assert.Contains(logger.Messages, m => m.Contains("F1:a00000000001 is bound to the L-Wireless controller's master, which drives it; left to the radio"));
        plugin.Close();
        Assert.DoesNotContain(enumerator.TransportFor(FlexPath).Writes, w => w[0] == 0x13);
    }

    // The same late pair, finishing only after FanControl has closed the instance (a refresh
    // came first): its stand-in is gone, so the controller is closed, but the chain is the pair's
    // now and the memory says so, and the refresh is still asked for, since the receiver's control
    // registered before the close is the one FanControl would bring back. The receiver, late with
    // it, decided whose its chain is against a pair the closed instance disposes as soon as it is
    // built - which may come before or after the receiver looks - so nothing it decided is kept.
    [Fact]
    public void ALatePairBuild_ThatTakesTheChainAfterTheInstanceClosed_StillAsksForTheRefresh() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()) { WorkerTickIntervalMilliseconds = 20 };
        RememberOnRadio(runtime, WirelessProtocolTests.ListReply(0));
        RememberOnUsb(runtime);

        using var slowMaster = new ManualResetEventSlim(false);
        var radio = new FakeWirelessRig();
        byte[] bound = WirelessProtocolTests.ListReply(1, Record().ToBytes());
        var enumerator = new FakeEnumerator(Flex(), Transmitter(), Receiver()) {
            ConfigureTransport = (info, transport) => {
                transport.ReplyFor = info.ProductId switch {
                    0x8040 => _ => radio.MasterReply() ?? new byte[64],
                    0x8041 => _ => bound,
                    _ => UsbReplies(UsbStatus()),
                };
                if (info.ProductId == 0x8040) {
                    transport.BlockReadsUntil = slowMaster;
                }
            },
        };
        var logger = new FakeLogger();
        using LianLiPlugin plugin = NewPlugin(enumerator, runtime, logger);
        plugin.BuildDeadlineMilliseconds = 50;
        FakeSensorsContainer sensors = Load(plugin);
        Assert.Contains("USB", Assert.Single(sensors.ControlSensors).Name, StringComparison.Ordinal);
        plugin.Close();

        // Each late result is waited for by the line it ends on, on its own thread: the pair's
        // closes its controller before it asks for the refresh, so its transports being disposed
        // says nothing about the request yet.
        slowMaster.Set();
        Assert.True(SpinWait.SpinUntil(() => logger.Messages.Contains("refresh wanted: a FLEX chain on a USB receiver is now the L-Wireless controller's"), TimeSpan.FromSeconds(10)));
        Assert.True(SpinWait.SpinUntil(() => logger.Messages.Contains("  fake/flex finished opening after this plugin instance closed; whose its chain is, is left to the next one"), TimeSpan.FromSeconds(10)));
        Assert.True(SpinWait.SpinUntil(() => enumerator.TransportFor(FlexPath).IsDisposed, TimeSpan.FromSeconds(10)));
        Assert.True(enumerator.TransportFor(TransmitterPath).IsDisposed);
        Assert.Equal(TransmitterPath, runtime.OwnerOf(ControlId));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("its stand-in took it"));
    }

    // The chain checks in bound between Initialize and Load: the receiver, built driving it,
    // reports its control still, but the memory gives the chain to the pair, and Load registers
    // it once, under the pair.
    [Fact]
    public void AChainTheRadioTakesBeforeLoad_IsRegisteredOnce_UnderThePair() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()) { WorkerTickIntervalMilliseconds = 20 };
        var radio = new FakeWirelessRig();
        byte[] bound = WirelessProtocolTests.ListReply(1, Record().ToBytes());
        byte[] empty = WirelessProtocolTests.ListReply(0);
        int heard = 0;
        var enumerator = new FakeEnumerator(Flex(), Transmitter(), Receiver()) {
            ConfigureTransport = (info, transport) => transport.ReplyFor = info.ProductId switch {
                0x8040 => _ => radio.MasterReply() ?? new byte[64],
                0x8041 => _ => Volatile.Read(ref heard) == 0 ? empty : bound,
                _ => UsbReplies(UsbStatus()),
            },
        };
        var logger = new FakeLogger();
        var clock = new FakeClock();
        using LianLiPlugin plugin = NewPlugin(enumerator, runtime, logger, clock);
        plugin.Initialize();
        Assert.Equal(FlexPath, runtime.OwnerOf(ControlId));

        // The pair reads its list at most once a second on the clock: the poll the clock lets happen
        // now hears the chain bound, or the worker's first did, read at once.
        Volatile.Write(ref heard, 1);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(SpinWait.SpinUntil(() => runtime.OwnerOf(ControlId) == TransmitterPath, TimeSpan.FromSeconds(5)));
        var sensors = new FakeSensorsContainer();
        plugin.Load(sensors);

        Assert.Equal(ControlId, Assert.Single(sensors.ControlSensors).Id);
        Assert.Contains("Wireless", Assert.Single(sensors.ControlSensors).Name, StringComparison.Ordinal);
        Assert.Equal(2, sensors.FanSensors.Count);
        Assert.Contains("  fake/flex: LianLi/wa00000000001/ctl is registered under fake/tx, which drives it", logger.Messages);
        plugin.Close();
    }

    // A receiver stood in for (its USB gone at the scan) is rebuilt in the background once it is
    // back: the rebuild identifies it and settles ownership without waiting for any pair, the
    // stand-in takes it, and the duty the stand-in's control was given goes out over USB.
    [Fact]
    public void AReceiverStoodInFor_IsRebuiltInTheBackground_AndDrivesTheChainToTheStandInsDuty() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()) { WorkerTickIntervalMilliseconds = 20 };
        RememberOnUsb(runtime);

        int usbGone = 1;
        var enumerator = new FakeEnumerator(Flex()) {
            FailOpenWhen = _ => Volatile.Read(ref usbGone) == 1,
            ConfigureTransport = (_, transport) => transport.ReplyFor = UsbReplies(UsbStatus()),
        };
        var logger = new FakeLogger();
        using LianLiPlugin plugin = NewPlugin(enumerator, runtime, logger);
        FakeSensorsContainer sensors = Load(plugin);
        Assert.Equal(ControlId, Assert.Single(sensors.ControlSensors).Id);
        Assert.Contains(logger.Messages, m => m.Contains("open failed for fake/flex"));
        Assert.Single(sensors.ControlSensors).Set(60);

        // The rebuild's own thread logs the adoption after handing the controller to the worker,
        // which writes the duty from its next tick; Close waits for the worker, not for that
        // thread, so its line is waited for as well.
        Volatile.Write(ref usbGone, 0);
        Assert.True(SpinWait.SpinUntil(() => logger.Messages.Any(m => m.Contains("rebuilt after") && m.Contains("1 of 1 remembered channel(s) matched")), TimeSpan.FromSeconds(10)));
        Assert.True(SpinWait.SpinUntil(() => enumerator.OpenedPaths.Contains(FlexPath) && enumerator.TransportFor(FlexPath).SnapshotTransfers().Any(t => t.Value[0] == 0x13), TimeSpan.FromSeconds(10)));
        plugin.Close();

        Assert.Equal(new byte[] { 0x13, 153, 153, 0 }, enumerator.TransportFor(FlexPath).Writes.First(w => w[0] == 0x13).Take(4));
        Assert.False(runtime.TryTakeRefresh(plugin, out _));
    }

    // After a reopen the receiver's path answers as another chain. Nothing is sent on it again, one
    // refresh is asked for to plan the path again, and the log does not fill with a failed poll a second.
    [Fact]
    public void AReceiverThatAnswersAsAnotherChainAfterAReopen_AsksForOneRefresh_AndIsQuiet() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()) { WorkerTickIntervalMilliseconds = 20 };
        var enumerator = new FakeEnumerator(Flex()) { ConfigureTransport = (_, transport) => transport.ReplyFor = UsbReplies(UsbStatus()) };
        var logger = new FakeLogger();
        using LianLiPlugin plugin = NewPlugin(enumerator, runtime, logger);
        FakeSensorsContainer sensors = Load(plugin);
        Assert.Single(sensors.ControlSensors).Set(40);
        FakeDeviceTransport flex = enumerator.TransportFor(FlexPath);
        Assert.True(SpinWait.SpinUntil(() => flex.SnapshotTransfers().Any(t => t.Value[0] == 0x13), TimeSpan.FromSeconds(5)));

        flex.ReplyFor = UsbReplies(UsbStatus(Record(new byte[6], new byte[] { 0xA0, 0, 0, 0, 0, 2 })));
        flex.Generation = 1;
        Assert.True(SpinWait.SpinUntil(() => runtime.TryTakeRefresh(plugin, out _), TimeSpan.FromSeconds(5)));
        int writes = flex.SnapshotTransfers().Length;
        Thread.Sleep(200);
        plugin.Close();

        Assert.Equal(writes, flex.Transfers.Count);
        Assert.Contains("refresh wanted: a USB receiver now answers as another chain", logger.Messages);
        Assert.Single(logger.Messages, m => m.Contains("now answers as a00000000002"));
        Assert.DoesNotContain(logger.Messages, m => m.StartsWith("poll err", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, m => m.StartsWith("apply err", StringComparison.Ordinal));
    }

    // Both drivers present and the chain bound: the receiver registers nothing, and a target set
    // on the pair's control is kept for the chain too, so a later hand-over to USB drives it to
    // the same duty at once.
    [Fact]
    public void AChainHandedFromTheRadioToUsbWhileRunning_IsDrivenToTheSameDutyOverUsbAtOnce() {
        var clock = new FakeClock();
        var runtime = new PluginRuntime(clock, new FakeRememberedControllerStore()) { WorkerTickIntervalMilliseconds = 20 };
        var radio = new FakeWirelessRig();
        byte[] bound = WirelessProtocolTests.ListReply(1, Record().ToBytes());
        byte[] unbound = WirelessProtocolTests.ListReply(1, Record(new byte[6]).ToBytes());
        int released = 0;
        var enumerator = new FakeEnumerator(Flex(), Transmitter(), Receiver()) {
            ConfigureTransport = (info, transport) => transport.ReplyFor = info.ProductId switch {
                0x8040 => _ => radio.MasterReply() ?? new byte[64],
                0x8041 => _ => Volatile.Read(ref released) == 0 ? bound : unbound,
                _ => UsbReplies(UsbStatus()),
            },
        };
        var logger = new FakeLogger();
        using LianLiPlugin plugin = NewPlugin(enumerator, runtime, logger, clock);
        FakeSensorsContainer sensors = Load(plugin);
        Assert.Contains("Wireless", Assert.Single(sensors.ControlSensors).Name, StringComparison.Ordinal);
        Assert.Single(sensors.ControlSensors).Set(60);
        Assert.Equal(60, runtime.WirelessState.ChainTarget(ChainText));
        FakeDeviceTransport flex = enumerator.TransportFor(FlexPath);

        Volatile.Write(ref released, 1);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(SpinWait.SpinUntil(() => flex.SnapshotTransfers().Any(t => t.Value[0] == 0x13), TimeSpan.FromSeconds(5)));
        Assert.True(SpinWait.SpinUntil(() => runtime.TryTakeRefresh(plugin, out _), TimeSpan.FromSeconds(5)));
        plugin.Close();

        Assert.Equal(new byte[] { 0x13, 153, 153, 0 }, flex.Writes.First(w => w[0] == 0x13).Take(4));
        Assert.Contains("refresh wanted: a FLEX chain on a USB receiver is driven over USB", logger.Messages);
        Assert.Equal(FlexPath, runtime.OwnerOf(ControlId));
    }

    // A chain remembered on the radio: the pair hears it bound once and then unbound during its
    // discovery, so it retains the chain's sensors without driving them, and the receiver, built in
    // the same scan, takes the chain and its memory. Before Load the chain checks in bound again,
    // with a third fan: the pair drives it, claims its ids, and Load registers every sensor under
    // the pair - the pair reports them all - while the receiver's copies are skipped. Nothing is
    // left for a refresh to put right.
    [Fact]
    public void AChainRetainedByThePair_ThenTakenBackBeforeLoadWithAThirdFan_IsRegisteredWholeUnderThePair() {
        var clock = new FakeClock();
        var runtime = new PluginRuntime(clock, new FakeRememberedControllerStore()) { WorkerTickIntervalMilliseconds = 20 };
        RememberOnRadio(runtime, WirelessProtocolTests.ListReply(1, Record().ToBytes()));

        var radio = new FakeWirelessRig();
        FakeWirelessRecord three = Record();
        three.FanCountByte = 3;
        three.FanTypes = new byte[] { 51, 51, 51, 0 };
        byte[] bound = WirelessProtocolTests.ListReply(1, Record().ToBytes());
        byte[] expanded = WirelessProtocolTests.ListReply(1, three.ToBytes());
        byte[] unbound = WirelessProtocolTests.ListReply(1, Record(new byte[6]).ToBytes());
        int reads = 0;
        int returned = 0;
        var enumerator = new FakeEnumerator(Flex(), Transmitter(), Receiver()) {
            ConfigureTransport = (info, transport) => transport.ReplyFor = info.ProductId switch {
                0x8040 => _ => radio.MasterReply() ?? new byte[64],
                0x8041 => _ => Volatile.Read(ref returned) != 0 ? expanded : Interlocked.Increment(ref reads) == 1 ? bound : unbound,
                _ => UsbReplies(UsbStatus()),
            },
        };
        var logger = new FakeLogger();
        using LianLiPlugin plugin = NewPlugin(enumerator, runtime, logger, clock);
        plugin.Initialize();
        Assert.Equal(FlexPath, runtime.OwnerOf(ControlId));

        Volatile.Write(ref returned, 1);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(SpinWait.SpinUntil(() => runtime.OwnerOf(ControlId) == TransmitterPath, TimeSpan.FromSeconds(5)));
        var sensors = new FakeSensorsContainer();
        plugin.Load(sensors);

        Assert.Equal(ControlId, Assert.Single(sensors.ControlSensors).Id);
        Assert.Contains("Wireless", Assert.Single(sensors.ControlSensors).Name, StringComparison.Ordinal);
        Assert.Equal(new[] { "LianLi/wa00000000001/f0/fan", "LianLi/wa00000000001/f1/fan", "LianLi/wa00000000001/f2/fan" }, sensors.FanSensors.Select(f => f.Id));
        Assert.Empty(sensors.TempSensors);
        Assert.False(runtime.TryTakeRefresh(plugin, out _));
        Assert.Contains("  fake/flex: LianLi/wa00000000001/ctl is registered under fake/tx, which drives it", logger.Messages);
        plugin.Close();
    }

    // The radio takes the chain while Load is registering: after the receiver's control went in
    // and before the pair's turn. Whose each id is was read once for the pass, so the pair's copy
    // is skipped and the control is registered once, under the receiver; the memory now names the
    // pair, which Load notices once the pass is done, and asks for the refresh that moves the
    // control, since the claim itself found nothing registered yet.
    [Fact]
    public void AChainTheRadioTakesWhileLoadRuns_IsRegisteredOnce_AndTheHandOverIsAskedForAfterwards() {
        var clock = new FakeClock();
        var runtime = new PluginRuntime(clock, new FakeRememberedControllerStore()) { WorkerTickIntervalMilliseconds = 20 };
        RememberOnUsb(runtime);

        var radio = new FakeWirelessRig();
        byte[] bound = WirelessProtocolTests.ListReply(1, Record().ToBytes());
        byte[] empty = WirelessProtocolTests.ListReply(0);
        int heard = 0;
        var enumerator = new FakeEnumerator(Flex(), Transmitter(), Receiver()) {
            ConfigureTransport = (info, transport) => transport.ReplyFor = info.ProductId switch {
                0x8040 => _ => radio.MasterReply() ?? new byte[64],
                0x8041 => _ => Volatile.Read(ref heard) == 0 ? empty : bound,
                _ => UsbReplies(UsbStatus()),
            },
        };
        var logger = new FakeLogger();
        using LianLiPlugin plugin = NewPlugin(enumerator, runtime, logger, clock);
        plugin.Initialize();
        Assert.Equal(FlexPath, runtime.OwnerOf(ControlId));
        plugin.Registering += key => {
            if (key == TransmitterPath) {
                Volatile.Write(ref heard, 1);
                clock.Advance(TimeSpan.FromSeconds(2));
                Assert.True(SpinWait.SpinUntil(() => runtime.OwnerOf(ControlId) == TransmitterPath, TimeSpan.FromSeconds(5)));
            }
        };

        var sensors = new FakeSensorsContainer();
        plugin.Load(sensors);

        Assert.Equal(ControlId, Assert.Single(sensors.ControlSensors).Id);
        Assert.Contains("USB", Assert.Single(sensors.ControlSensors).Name, StringComparison.Ordinal);
        Assert.Equal(2, sensors.FanSensors.Count);
        Assert.True(runtime.TryTakeRefresh(plugin, out string reason));
        Assert.Equal("a FLEX chain changed hands while the sensors were being registered", reason);
        plugin.Close();
    }

    // A claim that starts before Load and finishes after it: the pair's callback reads the chain
    // (which a test holds up mid-read), Load runs to its end - registering the chain under the
    // receiver, publishing, and comparing the memory, in which the pair has claimed nothing yet -
    // and only then does the callback remember the chain as the pair's. The registration it is
    // handed as it remembers is the one Load published, under the same lock, so it sees the
    // receiver's control on the chain it now drives and asks for the refresh; nothing is lost on
    // either side of the boundary.
    [Fact]
    public void AClaimThatStartsBeforeLoadAndFinishesAfterIt_StillAsksForTheRefresh() {
        var clock = new FakeClock();
        var runtime = new PluginRuntime(clock, new FakeRememberedControllerStore());
        var log = new FakeLogger();
        var enumerator = new FakeEnumerator(Flex()) { ConfigureTransport = (_, transport) => transport.ReplyFor = UsbReplies(UsbStatus()) };
        using LianLiPlugin plugin = NewPlugin(enumerator, runtime, log, clock);
        plugin.Initialize();
        using var radio = new FakeFanGroupDevice(ControlId, "LianLi/wa00000000001/f0/fan", "LianLi/wa00000000001/f1/fan");
        var radioPlan = new ControllerPlan(DeviceKind.WirelessTransmitter, Transmitter(), Receiver());
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var reporting = new HeldReport(radio, entered, resume);
        var callback = new Thread(() => plugin.OnSensorsReported(radioPlan, 1, reporting, "a wireless device checked in"));
        callback.Start();
        try {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            var sensors = new FakeSensorsContainer();
            plugin.Load(sensors);
            Assert.Equal(ControlId, Assert.Single(sensors.ControlSensors).Id);
            Assert.Contains("USB", Assert.Single(sensors.ControlSensors).Name, StringComparison.Ordinal);
            Assert.False(runtime.TryTakeRefresh(plugin, out _)); // nothing had changed hands when Load looked
        } finally {
            resume.Set();
            Assert.True(callback.Join(TimeSpan.FromSeconds(5)));
        }

        Assert.Equal(TransmitterPath, runtime.OwnerOf(ControlId));
        Assert.True(runtime.TryTakeRefresh(plugin, out string reason));
        Assert.Equal("a FLEX chain on a USB receiver is now the L-Wireless controller's", reason);
        plugin.Close();
    }

    // A wireless group whose sensors are read for the memory only once a test lets them be, so a
    // callback can be held between reading the controller and remembering it.
    private sealed class HeldReport : IFanDevice, IFanSpeedSource {
        private readonly FakeFanGroupDevice _inner;
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _resume;

        public HeldReport(FakeFanGroupDevice inner, ManualResetEventSlim entered, ManualResetEventSlim resume) {
            _inner = inner;
            _entered = entered;
            _resume = resume;
        }

        public int ChannelCount {
            get {
                _entered.Set();
                Assert.True(_resume.Wait(TimeSpan.FromSeconds(5)));
                return _inner.ChannelCount;
            }
        }

        public int FanSpeedCount => _inner.FanSpeedCount;

        public bool IsChannelPopulated(int channel) => _inner.IsChannelPopulated(channel);

        public ChannelDescriptor Describe(int channel) => _inner.Describe(channel);

        public FanSpeedDescriptor DescribeFanSpeed(int index) => _inner.DescribeFanSpeed(index);

        public float GetFanSpeed(int index) => _inner.GetFanSpeed(index);

        public void SetTarget(int channel, int duty) => _inner.SetTarget(channel, duty);

        public void ReleaseChannel(int channel) => _inner.ReleaseChannel(channel);

        public float GetRpm(int channel) => _inner.GetRpm(channel);

        public void ApplyPending() => _inner.ApplyPending();

        public void PollRpm() => _inner.PollRpm();

        public void ReplayOnReconnect(Func<bool> replay) => _inner.ReplayOnReconnect(replay);

        public void Dispose() => _inner.Dispose();
    }

    // The receiver is stood in for (its USB gone) with the chain, and the pair, which heard the
    // chain bound and then unbound during its discovery, retains the chain's sensors: nothing is
    // added when the chain binds again, but it is driven over the radio again, and the pair says
    // so; the plugin then claims the ids from the receiver's memory and asks for the refresh that
    // moves the host's control off the unreachable stand-in.
    [Fact]
    public void AChainThePairRetains_BoundAgainWhileItsReceiverIsStoodInFor_AsksForTheRefresh() {
        var clock = new FakeClock();
        var runtime = new PluginRuntime(clock, new FakeRememberedControllerStore()) { WorkerTickIntervalMilliseconds = 20 };
        RememberOnUsb(runtime);

        var radio = new FakeWirelessRig();
        byte[] bound = WirelessProtocolTests.ListReply(1, Record().ToBytes());
        byte[] unbound = WirelessProtocolTests.ListReply(1, Record(new byte[6]).ToBytes());
        int reads = 0;
        int returned = 0;
        var enumerator = new FakeEnumerator(Flex(), Transmitter(), Receiver()) {
            FailOpenWhen = info => info.ProductId == 0x0101,
            ConfigureTransport = (info, transport) => transport.ReplyFor = info.ProductId == 0x8040
                ? _ => radio.MasterReply() ?? new byte[64]
                : _ => Interlocked.Increment(ref reads) == 1 || Volatile.Read(ref returned) != 0 ? bound : unbound,
        };
        var logger = new FakeLogger();
        using LianLiPlugin plugin = NewPlugin(enumerator, runtime, logger, clock);
        FakeSensorsContainer sensors = Load(plugin);
        Assert.Contains("USB", Assert.Single(sensors.ControlSensors).Name, StringComparison.Ordinal);
        Assert.Equal(FlexPath, runtime.OwnerOf(ControlId));
        Assert.False(runtime.TryTakeRefresh(plugin, out _));

        Volatile.Write(ref returned, 1);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(SpinWait.SpinUntil(() => runtime.WirelessState.IsBoundToMaster(ChainText), TimeSpan.FromSeconds(5)));
        Assert.True(SpinWait.SpinUntil(() => runtime.TryTakeRefresh(plugin, out _), TimeSpan.FromSeconds(5)));
        plugin.Close();

        Assert.Equal(TransmitterPath, runtime.OwnerOf(ControlId));
        Assert.Contains("refresh wanted: a FLEX chain on a USB receiver is now the L-Wireless controller's", logger.Messages);
    }

    // The chain is registered under the pair. While the receiver's USB is gone (stood in for with
    // nothing), the radio lets the chain go: the pair only retains it and says nothing. The
    // receiver's USB comes back, its stand-in rebuilds it, it takes the chain back and drives it
    // to the duty the pair's control was given. A refresh is asked for from the receiver's side,
    // the only one that can see it, and the reason logged says the chain is driven over USB.
    [Fact]
    public void RadioLetsGoWhileTheReceiverIsStoodInFor_TheRebuiltReceiverTakesTheChain_AndTheReasonSaysUsb() {
        var clock = new FakeClock();
        var runtime = new PluginRuntime(clock, new FakeRememberedControllerStore()) { WorkerTickIntervalMilliseconds = 20 };
        RememberOnUsb(runtime);

        var radio = new FakeWirelessRig();
        byte[] bound = WirelessProtocolTests.ListReply(1, Record().ToBytes());
        byte[] unbound = WirelessProtocolTests.ListReply(1, Record(new byte[6]).ToBytes());
        int released = 0;
        int usbGone = 1;
        var enumerator = new FakeEnumerator(Flex(), Transmitter(), Receiver()) {
            FailOpenWhen = info => info.ProductId == 0x0101 && Volatile.Read(ref usbGone) == 1,
            ConfigureTransport = (info, transport) => transport.ReplyFor = info.ProductId switch {
                0x8040 => _ => radio.MasterReply() ?? new byte[64],
                0x8041 => _ => Volatile.Read(ref released) == 0 ? bound : unbound,
                _ => UsbReplies(UsbStatus()),
            },
        };
        var logger = new FakeLogger();
        using LianLiPlugin plugin = NewPlugin(enumerator, runtime, logger, clock);
        FakeSensorsContainer sensors = Load(plugin);
        Assert.Contains("Wireless", Assert.Single(sensors.ControlSensors).Name, StringComparison.Ordinal);
        Assert.Equal(TransmitterPath, runtime.OwnerOf(ControlId));
        Assert.Single(sensors.ControlSensors).Set(60);
        Assert.False(runtime.TryTakeRefresh(plugin, out _));

        // The radio lets go; no controller drives the chain, nothing is asked for.
        Volatile.Write(ref released, 1);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(SpinWait.SpinUntil(() => !runtime.WirelessState.IsBoundToMaster(ChainText), TimeSpan.FromSeconds(5)));
        Thread.Sleep(200);
        Assert.False(runtime.TryTakeRefresh(plugin, out _));
        Assert.Equal(TransmitterPath, runtime.OwnerOf(ControlId));

        // The receiver's USB is back: its stand-in rebuilds it and it takes the chain.
        Volatile.Write(ref usbGone, 0);
        Assert.True(SpinWait.SpinUntil(() => runtime.OwnerOf(ControlId) == FlexPath, TimeSpan.FromSeconds(10)));
        Assert.True(SpinWait.SpinUntil(() => logger.Messages.Contains("refresh wanted: a FLEX chain on a USB receiver is driven over USB"), TimeSpan.FromSeconds(5)));
        Assert.True(runtime.TryTakeRefresh(plugin, out _));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(SpinWait.SpinUntil(() => enumerator.OpenedPaths.Contains(FlexPath) && enumerator.TransportFor(FlexPath).SnapshotTransfers().Any(t => t.Value[0] == 0x13), TimeSpan.FromSeconds(10)));
        plugin.Close();

        Assert.Equal(new byte[] { 0x13, 153, 153, 0 }, enumerator.TransportFor(FlexPath).Writes.First(w => w[0] == 0x13).Take(4));
        Assert.Equal("refresh wanted: a FLEX chain on a USB receiver is driven over USB", Assert.Single(logger.Messages, m => m.StartsWith("refresh wanted:", StringComparison.Ordinal)));
    }
}
