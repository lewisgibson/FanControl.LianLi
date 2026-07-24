using System;
using System.Linq;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Hid;
using FanControl.LianLi.Plugin;
using FanControl.LianLi.Tests.Fakes;
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
        => new LianLiPlugin(enumerator, new DeviceCatalog(), new FakeClock(), new FakeLogger());

    private static HidDeviceInfo Sli(int index)
        => new HidDeviceInfo(0x0CF2, 0xA102, "fake/" + index, null);

    private static HidDeviceInfo SliAtPath(string devicePath)
        => new HidDeviceInfo(0x0CF2, 0xA102, devicePath, null);

    private static HidDeviceInfo SliInterface(string devicePath, string containerId, int maxOutput)
        => new HidDeviceInfo(0x0CF2, 0xA102, devicePath, null, containerId, maxOutput);

    private static HidDeviceInfo Galahad(int index)
        => new HidDeviceInfo(0x0416, 0x7371, "fake/galahad/" + index, null, usagePage: 0xFF1B);

    private static string FirstOpenedPath(FakeEnumerator enumerator) {
        using LianLiPlugin plugin = NewPlugin(enumerator);
        plugin.Initialize();
        return enumerator.OpenedPaths[0];
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
        Assert.Equal("aaa", FirstOpenedPath(new FakeEnumerator(SliAtPath("aaa"), SliAtPath("zzz"))));
        Assert.Equal("aaa", FirstOpenedPath(new FakeEnumerator(SliAtPath("zzz"), SliAtPath("aaa"))));
    }

    [Fact]
    public void Initialize_WhenEnumerationThrows_IsLoggedAndRegistersNothing() {
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(Sli(0)) { FailLocate = true };
        using var plugin = new LianLiPlugin(enumerator, new DeviceCatalog(), new FakeClock(), logger);

        plugin.Initialize(); // enumeration throws; it must be caught, logged, and degrade to no controllers
        var container = new FakeSensorsContainer();
        plugin.Load(container);

        Assert.Empty(container.ControlSensors);
        Assert.Empty(container.FanSensors);
        Assert.Contains(logger.Messages, m => m.Contains("enumeration failed"));
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
        using var plugin = new LianLiPlugin(enumerator, new DeviceCatalog(), new FakeClock(), logger);

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
}
