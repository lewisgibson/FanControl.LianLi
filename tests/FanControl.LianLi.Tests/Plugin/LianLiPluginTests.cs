using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
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

public class LianLiPluginTests {
    // The ARGB build advertises a distinct plugin name; assert against the variant in play.
#if ENABLE_ARGB
    private const string ExpectedName = "Lian Li Uni (ARGB)";
#elif ENABLE_LIGHTING
    private const string ExpectedName = "Lian Li Uni (Lighting)";
#else
    private const string ExpectedName = "Lian Li Uni";
#endif

    private static LianLiPlugin NewPlugin(FakeEnumerator enumerator)
        => new LianLiPlugin(enumerator, new DeviceCatalog(), new FakeClock(), new FakeDelay(), new FakeLogger(), LConnectDirectory.Absent, new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()));

    private static LocatedDevice Sli(int index)
        => new LocatedDevice(0x0CF2, 0xA102, "fake/" + index, null);

    private static LocatedDevice SliAtPath(string devicePath)
        => new LocatedDevice(0x0CF2, 0xA102, devicePath, null);

    private static LocatedDevice SliInterface(string devicePath, string containerId, int maxOutput)
        => new LocatedDevice(0x0CF2, 0xA102, devicePath, null, containerId, maxOutput);

    private static LocatedDevice Galahad(int index)
        => new LocatedDevice(0x0416, 0x7371, "fake/galahad/" + index, null);

    // The device path that became controller 0. Each path reports its own RPM on every channel
    // (1000 rpm for "aaa", 2000 for "zzz"), so controller 0's first fan sensor says which it is.
    private static string PathOfControllerZero(FakeEnumerator enumerator) {
        enumerator.ConfigureTransport = (info, transport) => {
            int rpm = info.DevicePath == "aaa" ? 1000 : 2000;
            var report = new byte[65];
            for (int ch = 0; ch < 4; ch++) {
                report[1 + (ch * 2)] = (byte)(rpm >> 8);
                report[2 + (ch * 2)] = (byte)(rpm & 0xFF);
            }

            transport.InputReport = report;
        };
        using LianLiPlugin plugin = NewPlugin(enumerator);
        plugin.Initialize();
        var container = new FakeSensorsContainer();
        plugin.Load(container);
        IPluginSensor fan = container.FanSensors.Single(sensor => sensor.Id == "LianLi/0/ch0/fan");
        Assert.True(SpinWait.SpinUntil(() => { fan.Update(); return fan.Value > 0; }, TimeSpan.FromSeconds(5)));
        plugin.Close();
        return fan.Value == 1000f ? "aaa" : "zzz";
    }

    private static LocatedDevice WirelessTransmitter()
        => new LocatedDevice(0x0416, 0x8040, "fake/wireless/tx", null);

    private static LocatedDevice WirelessReceiver()
        => new LocatedDevice(0x0416, 0x8041, "fake/wireless/rx", null);

    // The transmitter's answer to the master query: its address and a clock that has started.
    private static byte[] MasterReply(byte[] masterMac) {
        var reply = new byte[64];
        reply[0] = 0x11;
        Array.Copy(masterMac, 0, reply, 1, 6);
        reply[10] = 0x40;
        return reply;
    }

    // Seed the dongle fakes: the transmitter answers the master query, the receiver lists one
    // bound group of two fans (and answers every poll the same way).
    private static void SeedWirelessDongles(LocatedDevice info, FakeDeviceTransport transport) {
        byte[] masterMac = { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
        if (info.ProductId == 0x8040) {
            transport.ReadReplies.Enqueue(MasterReply(masterMac));
            return;
        }

        byte[] group = WirelessProtocolTests.Record(
            new byte[] { 0xA0, 0, 0, 0, 0, 1 }, masterMac, 8, 1, 0, 2, new byte[] { 36, 36, 0, 0 },
            new[] { 1000, 1100, 0, 0 }, new byte[] { 100, 100, 0, 0 }, 1);
        for (int i = 0; i < 8; i++) {
            transport.ReadReplies.Enqueue(WirelessProtocolTests.ListReply(1, group));
        }
    }

    [Fact]
    public void InitializeThenLoad_RegistersAControlPerWirelessGroupAndASpeedPerFan() {
        var enumerator = new FakeEnumerator(WirelessTransmitter(), WirelessReceiver()) { ConfigureTransport = SeedWirelessDongles };
        using LianLiPlugin plugin = NewPlugin(enumerator);

        plugin.Initialize();
        var container = new FakeSensorsContainer();
        plugin.Load(container);

        IPluginControlSensor control = Assert.Single(container.ControlSensors);
        Assert.Equal("LianLi/wa00000000001/ctl", control.Id);
        Assert.Equal(2, container.FanSensors.Count);
        Assert.Equal("LianLi/wa00000000001/f1/fan", container.FanSensors[1].Id);
        container.FanSensors[1].Update();
        Assert.Equal(1100f, container.FanSensors[1].Value);

        plugin.Close();
        Assert.All(enumerator.Opened, t => Assert.True(t.IsDisposed));
    }

    [Fact]
    public void InitializeThenLoad_RegistersACoolantTemperatureForAWirelessWaterBlock() {
        byte[] masterMac = { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
        var enumerator = new FakeEnumerator(WirelessTransmitter(), WirelessReceiver()) {
            ConfigureTransport = (info, transport) => {
                if (info.ProductId == 0x8040) {
                    transport.ReadReplies.Enqueue(MasterReply(masterMac));
                    return;
                }

                byte[] block = WirelessProtocolTests.Record(
                    new byte[] { 0xD0, 0, 0, 0, 0, 1 }, masterMac, 8, 1, 10, 1, new byte[] { 0, 0, 0, 30 },
                    new[] { 800, 0, 0, 2000 }, new byte[] { 90, 0, 0, 0 }, 1);
                for (int i = 0; i < 8; i++) {
                    transport.ReadReplies.Enqueue(WirelessProtocolTests.ListReply(1, block));
                }
            },
        };
        using LianLiPlugin plugin = NewPlugin(enumerator);

        plugin.Initialize();
        var container = new FakeSensorsContainer();
        plugin.Load(container);

        Assert.Equal(2, container.ControlSensors.Count); // one fan, the pump
        Assert.Equal("LianLi/wd00000000001/pump/ctl", container.ControlSensors[1].Id);
        IPluginSensor coolant = Assert.Single(container.TempSensors);
        Assert.Equal("LianLi/wd00000000001/coolant/temp", coolant.Id);
        coolant.Update();
        Assert.Equal(30f, coolant.Value);
    }

    [Fact]
    public void AWirelessControllerThatThrowsWhileBeingMade_ClosesBothDongles() {
        var enumerator = new FakeEnumerator(WirelessTransmitter(), WirelessReceiver()) { ConfigureTransport = SeedWirelessDongles };
        var delay = new FakeDelay { OnWait = () => throw new InvalidOperationException("startup wait broke") };
        var logger = new FakeLogger();
        using var plugin = new LianLiPlugin(
            enumerator, new DeviceCatalog(), new FakeClock(), delay, logger, LConnectDirectory.Absent,
            new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()));

        plugin.Initialize();
        var container = new FakeSensorsContainer();
        plugin.Load(container);

        Assert.Empty(container.ControlSensors);
        Assert.True(enumerator.TransportFor("fake/wireless/tx").IsDisposed);
        Assert.True(enumerator.TransportFor("fake/wireless/rx").IsDisposed);
        Assert.Contains(logger.Messages, m => m.Contains("open failed for fake/wireless/tx: startup wait broke"));
    }

    [Fact]
    public void Initialize_SkipsAWirelessDongleWithoutItsPartner() {
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(WirelessTransmitter()) { ConfigureTransport = SeedWirelessDongles };
        using var plugin = new LianLiPlugin(enumerator, new DeviceCatalog(), new FakeClock(), new FakeDelay(), logger, LConnectDirectory.Absent, new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()));

        plugin.Initialize();
        var container = new FakeSensorsContainer();
        plugin.Load(container);

        Assert.Empty(container.ControlSensors);
        Assert.Empty(enumerator.Opened);
        Assert.Contains(logger.Messages, m => m.Contains("wireless transmitter without a receiver"));
    }

    [Fact]
    public void Name_MatchesBuildVariant()
        => Assert.Equal(ExpectedName, NewPlugin(new FakeEnumerator()).Name);

    [Fact]
    public void PublicConstructor_ComposesWithHostLogger() {
        // Exercises the production composition root (real enumerator/clock/loggers)
        // without enumerating hardware, since Initialize is not called.
        using var plugin = new LianLiPlugin(new FakePluginLogger());
        Assert.Equal(ExpectedName, plugin.Name);
    }

    [Fact]
    public void InitializeThenLoad_RegistersFourControlAndFourFanSensorsPerController() {
        var enumerator = new FakeEnumerator(Sli(0), Sli(1));
        using LianLiPlugin plugin = NewPlugin(enumerator);

        plugin.Initialize();
        var container = new FakeSensorsContainer();
        plugin.Load(container);

        Assert.Equal(8, container.ControlSensors.Count);
        Assert.Equal(8, container.FanSensors.Count);

        var ids = container.ControlSensors.Select(s => s.Id)
            .Concat(container.FanSensors.Select(s => s.Id))
            .ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count()); // every id is unique

        plugin.Close();
    }

    [Fact]
    public void InitializeThenLoad_SkipsChannelsWithNoFanAttached() {
        // The controller's startup probe reads a spinning fan on ch0 and ch2 only; the two empty
        // channels are not surfaced, so a controller with two fans registers two controls and two
        // rpm sensors rather than four. FanControl greys out (and later re-links) any saved binding
        // to a hidden channel rather than rejecting the whole config, so hiding an empty slot never
        // orphans the user's other curves.
        var rpm = new byte[65];
        rpm[1] = 0x05; rpm[2] = 0xDC; // ch0 -> 1500 rpm
        rpm[5] = 0x05; rpm[6] = 0xDC; // ch2 -> 1500 rpm
        var enumerator = new FakeEnumerator(Sli(0)) { ConfigureTransport = (_, t) => t.InputReport = rpm };
        using LianLiPlugin plugin = NewPlugin(enumerator);

        plugin.Initialize();
        var container = new FakeSensorsContainer();
        plugin.Load(container);

        Assert.Equal(2, container.ControlSensors.Count);
        Assert.Equal(2, container.FanSensors.Count);

        plugin.Close();
    }

    [Fact]
    public void InitializeThenLoad_RegistersFanAndPumpSensorsForAGalahad() {
        // A 0x0416 Galahad classifies to the command-packet builder and exposes two channels: a fan
        // and a pump, each with its own control and rpm sensor.
        var enumerator = new FakeEnumerator(Galahad(0));
        using LianLiPlugin plugin = NewPlugin(enumerator);

        plugin.Initialize();
        var container = new FakeSensorsContainer();
        plugin.Load(container);

        Assert.Equal(2, container.ControlSensors.Count);
        Assert.Equal(2, container.FanSensors.Count);
        Assert.Contains(container.ControlSensors, s => s.Name.Contains("Fan"));
        Assert.Contains(container.ControlSensors, s => s.Name.Contains("Pump"));

        plugin.Close();
    }

    [Fact]
    public void Lifecycle_IsRepeatableAndDisposesTransports() {
        var enumerator = new FakeEnumerator(Sli(0));
        var plugin = NewPlugin(enumerator);

        for (int cycle = 0; cycle < 2; cycle++) {
            plugin.Initialize();
            plugin.Load(new FakeSensorsContainer());
            plugin.Close();
        }

        Assert.NotEmpty(enumerator.Opened);
        Assert.All(enumerator.Opened, transport => Assert.True(transport.IsDisposed));

        plugin.Dispose();
    }

    [Fact]
    public void InitializeThenLoad_CollapsesDuplicateInterfacesOfOnePhysicalController() {
        // One controller surfacing two matching HID interfaces (same physical device, so the same
        // ContainerId) must register a single Ch1-4 set, not two.
        var enumerator = new FakeEnumerator(
            SliInterface("controller/mi_00", "CID-1", 0),
            SliInterface("controller/mi_01", "CID-1", 65));
        using LianLiPlugin plugin = NewPlugin(enumerator);

        plugin.Initialize();
        var container = new FakeSensorsContainer();
        plugin.Load(container);

        Assert.Equal(4, container.ControlSensors.Count);
        Assert.Equal(4, container.FanSensors.Count);
    }

    [Fact]
    public void InitializeThenLoad_KeepsPhysicallyDistinctControllersSeparate() {
        // Two distinct controllers (different ContainerIds) each register a full Ch1-4 set.
        var enumerator = new FakeEnumerator(
            SliInterface("controllerA", "CID-A", 65),
            SliInterface("controllerB", "CID-B", 65));
        using LianLiPlugin plugin = NewPlugin(enumerator);

        plugin.Initialize();
        var container = new FakeSensorsContainer();
        plugin.Load(container);

        Assert.Equal(8, container.ControlSensors.Count);
        Assert.Equal(8, container.FanSensors.Count);
    }

    [Fact]
    public void InitializeThenLoad_KeepsAllControllersThatReportTheSameFirmwareSerial() {
        // Regression for the real hardware: every Lian Li Uni controller reports the same fixed USB
        // serial, so three physically distinct controllers (distinct ContainerIds and device paths)
        // must register three full Ch1-4 sets - not collapse to one. The serial is never the key.
        var enumerator = new FakeEnumerator(
            SliInterface("hid/ctrlA", "CID-A", 353),
            SliInterface("hid/ctrlB", "CID-B", 353),
            SliInterface("hid/ctrlC", "CID-C", 353));
        using LianLiPlugin plugin = NewPlugin(enumerator);

        plugin.Initialize();
        var container = new FakeSensorsContainer();
        plugin.Load(container);

        Assert.Equal(12, container.ControlSensors.Count);
        Assert.Equal(12, container.FanSensors.Count);

        var ids = container.ControlSensors.Select(s => s.Id)
            .Concat(container.FanSensors.Select(s => s.Id))
            .ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count()); // every id is unique across the three
    }

    [Fact]
    public void Initialize_OrdersControllersByDevicePath_IndependentOfEnumerationOrder() {
        // The lexicographically-first device path always becomes controller index 0, so a saved
        // binding keeps pointing at the same physical channel regardless of OS enumeration order.
        Assert.Equal("aaa", PathOfControllerZero(new FakeEnumerator(SliAtPath("aaa"), SliAtPath("zzz"))));
        Assert.Equal("aaa", PathOfControllerZero(new FakeEnumerator(SliAtPath("zzz"), SliAtPath("aaa"))));
    }

    [Fact]
    public void Initialize_WhenEnumerationThrows_IsLoggedAndRegistersNothing() {
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(Sli(0)) { FailLocate = true };
        using var plugin = new LianLiPlugin(enumerator, new DeviceCatalog(), new FakeClock(), new FakeDelay(), logger, LConnectDirectory.Absent, new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()));

        plugin.Initialize(); // enumeration throws; it must be caught, logged, and degrade to no controllers
        var container = new FakeSensorsContainer();
        plugin.Load(container);

        Assert.Empty(container.ControlSensors);
        Assert.Empty(container.FanSensors);
        Assert.Contains(logger.Messages, m => m.Contains("scan failed"));
    }

    [Fact]
    public void Initialize_WhenAScanCannotComplete_KeepsThePreviousScansSensors() {
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(Sli(0));
        using var plugin = new LianLiPlugin(enumerator, new DeviceCatalog(), new FakeClock(), new FakeDelay(), logger, LConnectDirectory.Absent, new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()));

        // A first scan finds the controller and registers its four channels.
        plugin.Initialize();
        var first = new FakeSensorsContainer();
        plugin.Load(first);
        Assert.Equal(4, first.ControlSensors.Count);

        // FanControl refreshes a few seconds after a resume, and this time the scan cannot finish
        // because a device has stopped answering Windows. The sensors must not vanish with it.
        plugin.Close();
        enumerator.FailLocate = true;
        plugin.Initialize();
        var second = new FakeSensorsContainer();
        plugin.Load(second);

        Assert.Equal(4, second.ControlSensors.Count);
        for (int ch = 0; ch < 4; ch++) {
            Assert.Equal(first.ControlSensors[ch].Id, second.ControlSensors[ch].Id);
            Assert.Equal(first.FanSensors[ch].Id, second.FanSensors[ch].Id);
        }

        Assert.Contains(logger.Messages, m => m.Contains("standing in for the 1 controller(s) remembered"));
        Assert.Contains(logger.Messages, m => m.Contains("reconnecting in the background"));
    }

    [Fact]
    public void Initialize_WhenADeviceWillNotOpenAgain_KeepsItsSensorsAndRebuildsInTheBackground() {
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(Sli(0));
        using var plugin = new LianLiPlugin(enumerator, new DeviceCatalog(), new FakeClock(), new FakeDelay(), logger, LConnectDirectory.Absent, new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()));

        plugin.Initialize();
        var first = new FakeSensorsContainer();
        plugin.Load(first);

        // The device is still listed but will not open this time.
        plugin.Close();
        enumerator.FailOpen = true;
        plugin.Initialize();
        var second = new FakeSensorsContainer();
        plugin.Load(second);

        Assert.Equal(first.ControlSensors.Count, second.ControlSensors.Count);
        Assert.Equal(first.ControlSensors[0].Id, second.ControlSensors[0].Id);
        Assert.Contains(logger.Messages, m => m.Contains("open failed"));
        Assert.Contains(logger.Messages, m => m.Contains("reconnecting in the background"));

        // What the stand-in then does with that registration - rebuilding on the backoff, matching
        // the channels back up - is ReconnectingFanDeviceTests' business, and is asserted there
        // against a clock the test drives rather than the live keepalive loop.
    }

    [Fact]
    public void Initialize_WhenAScanFailsWithNothingRemembered_RegistersNothing() {
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(Sli(0)) { FailLocate = true };
        using var plugin = new LianLiPlugin(enumerator, new DeviceCatalog(), new FakeClock(), new FakeDelay(), logger, LConnectDirectory.Absent, new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()));

        plugin.Initialize();
        var container = new FakeSensorsContainer();
        plugin.Load(container);

        Assert.Empty(container.ControlSensors);
        Assert.Contains(logger.Messages, m => m.Contains("standing in for the 0 controller(s) remembered"));
    }

    [Fact]
    public void Initialize_SkipsDevicesThatFailToOpen() {
        var enumerator = new FakeEnumerator(Sli(0)) { FailOpen = true };
        using LianLiPlugin plugin = NewPlugin(enumerator);

        plugin.Initialize(); // the open throws; the device is logged and skipped, not fatal
        var container = new FakeSensorsContainer();
        plugin.Load(container);

        Assert.Empty(container.ControlSensors);
        Assert.Empty(container.FanSensors);
    }

    [Fact]
    public void Initialize_KeepsControllerWhenSetupWritesAreRejected() {
        // The transport opens but the device rejects every feature write, so the manual-mode assert
        // and the population probe both fail. The controller must stay registered with all four
        // channels shown - losing the whole controller over a rejected setup write is the failure
        // mode this guards - and the worker re-asserts manual mode before each speed write, so
        // control recovers if the device accepts writes later.
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(Sli(0)) { FailFeatures = true };
        using var plugin = new LianLiPlugin(enumerator, new DeviceCatalog(), new FakeClock(), new FakeDelay(), logger, LConnectDirectory.Absent, new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()));

        plugin.Initialize();
        var container = new FakeSensorsContainer();
        plugin.Load(container);

        Assert.Equal(4, container.ControlSensors.Count);
        Assert.Equal(4, container.FanSensors.Count);

        // Close BEFORE reading the log: the keepalive worker's background thread keeps logging its
        // (equally rejected) polls until the plugin stops, and FakeLogger's list is not synchronized.
        plugin.Close();
        Assert.Contains(logger.Messages, m => m.Contains("manual-mode assert failed"));
        Assert.All(enumerator.Opened, transport => Assert.True(transport.IsDisposed)); // no leaked handle
    }

    [Fact]
    public void Close_BeforeInitialize_DoesNotThrow() {
        using LianLiPlugin plugin = NewPlugin(new FakeEnumerator());
        plugin.Close(); // fixes the original unconditional-dispose NRE
    }

    [Fact]
    public void InitializeLoadClose_WithNoDevices_DoesNotThrow() {
        using LianLiPlugin plugin = NewPlugin(new FakeEnumerator());
        var container = new FakeSensorsContainer();

        plugin.Initialize();
        plugin.Load(container);
        plugin.Close();

        Assert.Empty(container.ControlSensors);
        Assert.Empty(container.FanSensors);
    }

    [Fact]
    public void Load_WithNullContainer_Throws() {
        using LianLiPlugin plugin = NewPlugin(new FakeEnumerator());
        plugin.Initialize();
        Assert.Throws<ArgumentNullException>(() => plugin.Load(null!));
    }

    private static LocatedDevice TlHub()
        => new LocatedDevice(0x0416, 0x7372, "fake/tl/0", null);

    // One TL fan on port 0, fan 0, at 1000 rpm: the discovery handshake the hub answers with.
    private static void SeedTlHandshake(LocatedDevice info, FakeDeviceTransport transport)
        => transport.ReadReplies.Enqueue(CommandPacket.Build(0xA1, 0x80, 0x03, 0xE8));

    [Fact]
    public void InitializeThenLoad_RegistersAFanPerDiscoveredTlFan() {
        var enumerator = new FakeEnumerator(TlHub()) { ConfigureTransport = SeedTlHandshake };
        using LianLiPlugin plugin = NewPlugin(enumerator);

        plugin.Initialize();
        var container = new FakeSensorsContainer();
        plugin.Load(container);

        Assert.Equal("LianLi/0/p0f0/ctl", Assert.Single(container.ControlSensors).Id);
        plugin.Close();
    }

    [Fact]
    public void Initialize_ATlHubWhoseHandshakeFails_IsReleasedAndSkipped() {
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(TlHub()) { ConfigureTransport = (_, t) => t.FailReads = true };
        using var plugin = new LianLiPlugin(enumerator, new DeviceCatalog(), new FakeClock(), new FakeDelay(), logger, LConnectDirectory.Absent, new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()));

        plugin.Initialize();
        var container = new FakeSensorsContainer();
        plugin.Load(container);

        Assert.Empty(container.ControlSensors);
        Assert.True(Assert.Single(enumerator.Opened).IsDisposed);
    }

    [Fact]
    public void Initialize_LogsAndSkipsADeviceNoFamilyClaims() {
        // The scan matches vendor and product separately, so the TL product id under the Uni vendor
        // is found - and belongs to nothing.
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(new LocatedDevice(0x0CF2, 0x7372, "fake/odd", null));
        using var plugin = new LianLiPlugin(enumerator, new DeviceCatalog(), new FakeClock(), new FakeDelay(), logger, LConnectDirectory.Absent, new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()));

        plugin.Initialize();

        Assert.Empty(enumerator.Opened);
        Assert.Contains(logger.Messages, m => m.Contains("skipped unrecognised device pid=0x7372 path=fake/odd"));
    }

    [Fact]
    public void Initialize_SkipsAWirelessReceiverWithoutATransmitter() {
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(WirelessReceiver()) { ConfigureTransport = SeedWirelessDongles };
        using var plugin = new LianLiPlugin(enumerator, new DeviceCatalog(), new FakeClock(), new FakeDelay(), logger, LConnectDirectory.Absent, new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()));

        plugin.Initialize();

        Assert.Empty(enumerator.Opened);
        Assert.Contains(logger.Messages, m => m.Contains("wireless receiver without a transmitter"));
    }

    [Fact]
    public void Initialize_AReceiverThatWillNotOpen_ReleasesTheTransmitter() {
        var enumerator = new FakeEnumerator(WirelessTransmitter(), WirelessReceiver()) {
            ConfigureTransport = SeedWirelessDongles,
            FailOpenWhen = info => info.ProductId == 0x8041,
        };
        using LianLiPlugin plugin = NewPlugin(enumerator);

        plugin.Initialize();

        FakeDeviceTransport transmitter = Assert.Single(enumerator.Opened);
        Assert.True(transmitter.IsDisposed);
    }

    [Fact]
    public void Initialize_UsesTheWirelessChannelLConnectSaved() {
        using var lConnect = new LConnectDirectory();
        Directory.CreateDirectory(lConnect.Locations.WirelessDirectory);
        File.WriteAllText(Path.Combine(lConnect.Locations.WirelessDirectory, "112233445566.config"), "21");
        var enumerator = new FakeEnumerator(WirelessTransmitter(), WirelessReceiver()) { ConfigureTransport = SeedWirelessDongles };
        var logger = new FakeLogger();
        using var plugin = new LianLiPlugin(enumerator, new DeviceCatalog(), new FakeClock(), new FakeDelay(), logger, lConnect.Locations, new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()));

        plugin.Initialize();

        // RFController.Init: the master is queried on the default channel first, then its own file is read.
        Assert.Equal(8, enumerator.TransportFor("fake/wireless/tx").Writes[0][1]);
        Assert.Contains(logger.Messages, m => m.Contains("master 112233445566, RF channel 21 (saved by L-Connect)"));
        Assert.Contains(logger.Messages, m => m.Contains("controller wireless master=112233445566 channel=21"));
        plugin.Close();
    }

    [Fact]
    public void Initialize_AnUnreadableWirelessChannel_FallsBackToTheDefault() {
        using var lConnect = new LConnectDirectory();
        Directory.CreateDirectory(lConnect.Locations.WirelessDirectory);
        string channelFile = Path.Combine(lConnect.Locations.WirelessDirectory, "112233445566.config");
        File.WriteAllText(channelFile, "21");
        var enumerator = new FakeEnumerator(WirelessTransmitter(), WirelessReceiver()) { ConfigureTransport = SeedWirelessDongles };
        var logger = new FakeLogger();
        using var plugin = new LianLiPlugin(enumerator, new DeviceCatalog(), new FakeClock(), new FakeDelay(), logger, lConnect.Locations, new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()));

        // Held open exclusively, as L-Connect would while writing it.
        using (new FileStream(channelFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            plugin.Initialize();
        }

        Assert.Contains(logger.Messages, m => m.Contains("wireless: saved channel for master 112233445566 unusable, ignored"));
        Assert.Contains(logger.Messages, m => m.Contains("controller wireless master=112233445566 channel=8"));
        plugin.Close();
    }

    [Fact]
    public void Initialize_ReadsTheStartStopSwitchFromTheControllersProfile() {
        using var lConnect = new LConnectDirectory();
        WriteStartStopProfile(lConnect, "fake/0", "{\"SubProfiles\":[{\"GroupIndex\":0,\"RPMSetting\":{\"Mode\":0,\"Profiles\":{\"Quiet\":{\"Mode\":0,\"IsStartStop\":true}}}}]}");
        var logger = new FakeLogger();
        using var plugin = new LianLiPlugin(new FakeEnumerator(Sli(0)), new DeviceCatalog(), new FakeClock(), new FakeDelay(), logger, lConnect.Locations, new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()));

        plugin.Initialize();

        Assert.Contains(logger.Messages, m => m.Contains("startStop=ch0"));
        plugin.Close();
    }

    [Fact]
    public void Initialize_ACorruptStartStopProfile_LeavesStartStopOff_AndStillRegistersTheController() {
        using var lConnect = new LConnectDirectory();
        Directory.CreateDirectory(lConnect.Locations.ProfileDirectory);
        File.WriteAllText(Path.Combine(lConnect.Locations.ProfileDirectory, StartStopConfigurationReader.ProfileFileName("fake/0")), "not gzip");
        var logger = new FakeLogger();
        using var plugin = new LianLiPlugin(new FakeEnumerator(Sli(0)), new DeviceCatalog(), new FakeClock(), new FakeDelay(), logger, lConnect.Locations, new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()));

        plugin.Initialize();
        var container = new FakeSensorsContainer();
        plugin.Load(container);

        Assert.Contains(logger.Messages, m => m.Contains("start/stop profile unreadable for fake/0"));
        Assert.Equal(4, container.ControlSensors.Count);
        plugin.Close();
    }

    private static void WriteStartStopProfile(LConnectDirectory lConnect, string devicePath, string json) {
        Directory.CreateDirectory(lConnect.Locations.ProfileDirectory);
        using FileStream file = File.Create(
            Path.Combine(lConnect.Locations.ProfileDirectory, StartStopConfigurationReader.ProfileFileName(devicePath)));
        using var gzip = new GZipStream(file, CompressionMode.Compress);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        gzip.Write(bytes, 0, bytes.Length);
    }

    [Fact]
    public void Constructor_RejectsMissingDependencies() {
        var enumerator = new FakeEnumerator();
        var catalog = new DeviceCatalog();
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        LConnectLocations lConnect = LConnectDirectory.Absent;

        var clock = new FakeClock();
        var delay = new FakeDelay();
        var logger = new FakeLogger();

        Assert.Throws<ArgumentNullException>(() => new LianLiPlugin(null!, catalog, clock, delay, logger, lConnect, runtime));
        Assert.Throws<ArgumentNullException>(() => new LianLiPlugin(enumerator, null!, clock, delay, logger, lConnect, runtime));
        Assert.Throws<ArgumentNullException>(() => new LianLiPlugin(enumerator, catalog, null!, delay, logger, lConnect, runtime));
        Assert.Throws<ArgumentNullException>(() => new LianLiPlugin(enumerator, catalog, clock, null!, logger, lConnect, runtime));
        Assert.Throws<ArgumentNullException>(() => new LianLiPlugin(enumerator, catalog, clock, delay, null!, lConnect, runtime));
        Assert.Throws<ArgumentNullException>(() => new LianLiPlugin(enumerator, catalog, clock, delay, logger, null!, runtime));
        Assert.Throws<ArgumentNullException>(() => new LianLiPlugin(enumerator, catalog, clock, delay, logger, lConnect, null!));
    }

    [Fact]
    public void Initialize_DrivesOneDonglePair_AndLogsAnySecondPair() {
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(
            WirelessTransmitter(),
            WirelessReceiver(),
            new LocatedDevice(0x0416, 0x8040, "fake/wireless/tx2", null),
            new LocatedDevice(0x0416, 0x8041, "fake/wireless/rx2", null)) { ConfigureTransport = SeedWirelessDongles };
        using var plugin = new LianLiPlugin(enumerator, new DeviceCatalog(), new FakeClock(), new FakeDelay(), logger, LConnectDirectory.Absent, new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()));

        plugin.Initialize();

        Assert.Equal(new[] { "fake/wireless/rx", "fake/wireless/tx" }, enumerator.OpenedPaths.OrderBy(path => path, StringComparer.Ordinal));
        Assert.Contains(logger.Messages, m => m.Contains("wireless transmitter not used (one pair is driven, as L-Connect does): fake/wireless/tx2"));
        Assert.Contains(logger.Messages, m => m.Contains("wireless receiver not used (one pair is driven, as L-Connect does): fake/wireless/rx2"));
        plugin.Close();
    }

    // Two kits: the first transmitter by path is paired with its own kit's receiver, not the first.
    [Fact]
    public void Initialize_PairsTheDonglesOfOneKit_ByTheirContainer() {
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(
            new LocatedDevice(0x0416, 0x8040, "fake/wireless/tx", null, "{kit-b}"),
            new LocatedDevice(0x0416, 0x8041, "fake/wireless/rx", null, "{kit-a}"),
            new LocatedDevice(0x0416, 0x8040, "fake/wireless/tx2", null, "{kit-a}"),
            new LocatedDevice(0x0416, 0x8041, "fake/wireless/rx2", null, "{kit-b}")) { ConfigureTransport = SeedWirelessDongles };
        using var plugin = new LianLiPlugin(enumerator, new DeviceCatalog(), new FakeClock(), new FakeDelay(), logger, LConnectDirectory.Absent, new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()));

        plugin.Initialize();

        Assert.Equal(new[] { "fake/wireless/rx2", "fake/wireless/tx" }, enumerator.OpenedPaths.OrderBy(path => path, StringComparer.Ordinal));
        Assert.Contains(logger.Messages, m => m.Contains("wireless receiver not used (one pair is driven, as L-Connect does): fake/wireless/rx"));
        plugin.Close();
    }

    [Fact]
    public void Initialize_ATransmitterThatNeverAnswers_KeepsTheControllerAndLogsIt() {
        // No replies queued at all: the master query and the list read both come back empty.
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(WirelessTransmitter(), WirelessReceiver());
        using var plugin = new LianLiPlugin(
            enumerator, new DeviceCatalog(), new FakeClock(), new FakeDelay(), logger, LConnectDirectory.Absent, new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()));

        plugin.Initialize();

        Assert.Contains(logger.Messages, m => m.Contains("controller wireless master=not answering yet"));
        Assert.All(enumerator.Opened, transport => Assert.False(transport.IsDisposed));
        plugin.Close();
    }
}
