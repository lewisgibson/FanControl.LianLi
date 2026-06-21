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

    private static HidDeviceInfo SliInterface(string devicePath, string serial, int maxOutput)
        => new HidDeviceInfo(0x0CF2, 0xA102, devicePath, null, serial, maxOutput);

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
        // One controller surfacing two matching HID interfaces (shared serial) must register a
        // single Ch1-4 set, not two.
        var enumerator = new FakeEnumerator(
            SliInterface("controller/mi_00", "SERIAL-1", 0),
            SliInterface("controller/mi_01", "SERIAL-1", 65));
        using LianLiPlugin plugin = NewPlugin(enumerator);

        plugin.Initialize();
        var container = new FakeSensorsContainer();
        plugin.Load(container);

        Assert.Equal(4, container.ControlSensors.Count);
        Assert.Equal(4, container.FanSensors.Count);
    }

    [Fact]
    public void InitializeThenLoad_KeepsPhysicallyDistinctControllersSeparate() {
        // Two distinct controllers (different serials) each register a full Ch1-4 set.
        var enumerator = new FakeEnumerator(
            SliInterface("controllerA", "SERIAL-A", 65),
            SliInterface("controllerB", "SERIAL-B", 65));
        using LianLiPlugin plugin = NewPlugin(enumerator);

        plugin.Initialize();
        var container = new FakeSensorsContainer();
        plugin.Load(container);

        Assert.Equal(8, container.ControlSensors.Count);
        Assert.Equal(8, container.FanSensors.Count);
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
    public void Initialize_DisposesTransportWhenControllerSetupThrows() {
        // The transport opens, but the FanController constructor's setup writes throw, so
        // the controller is never created. The opened HID handle must not leak.
        var enumerator = new FakeEnumerator(Sli(0)) { FailWrites = true };
        using LianLiPlugin plugin = NewPlugin(enumerator);

        plugin.Initialize();

        Assert.NotEmpty(enumerator.Opened);
        Assert.All(enumerator.Opened, transport => Assert.True(transport.IsDisposed));

        var container = new FakeSensorsContainer();
        plugin.Load(container);
        Assert.Empty(container.ControlSensors); // the faulted device registered nothing
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
