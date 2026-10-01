#if ENABLE_LIGHTING
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.IO.Compression;
using System.Linq;
using System.Text;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Transport;
using FanControl.LianLi.Plugin;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Tests.Fakes;
using Xunit;

namespace FanControl.LianLi.Tests.Plugin;

// End-to-end coverage of the Lighting build's host wiring: reading L-Connect's config from a
// fixture directory, matching it to a located controller by instance token, the family gate,
// the encode+apply path, and the guarantee that a lighting fault never drops fan control.
public sealed class LianLiPluginLightingTests : IDisposable
{
    // A realistic SL-Infinity device path; the instance token (9c2f7a3) is what the saved
    // config is matched against.
    private const string DevicePath = @"\\?\hid#vid_0cf2&pid_a102&mi_01#7&9c2f7a3&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

    // A realistic Strimer Plus device path; its instance token (a7b1c92) is matched against the
    // saved Strimer config. Note the interface is mi_00, not the SL-Infinity mi_01.
    private const string StrimerPath = @"\\?\hid#vid_0cf2&pid_a200&mi_00#7&a7b1c92&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

    private readonly LConnectDirectory _lConnect = new LConnectDirectory();

    // The wired controllers' saved looks live under L-Connect's device directory.
    private readonly string _configDir;

    public LianLiPluginLightingTests()
    {
        _configDir = _lConnect.Locations.DeviceDirectory;
        Directory.CreateDirectory(_configDir);
    }

    public void Dispose() => _lConnect.Dispose();

    [Fact]
    public void Initialize_AppliesSavedLook_ToMatchingSlInfinityController()
    {
        WriteSavedLook();
        var enumerator = new FakeEnumerator(Device(0xA102, DevicePath));
        using LianLiPlugin plugin = NewPlugin(enumerator);

        plugin.Initialize();
        // Stop the keepalive worker so its RPM-primer poll cannot race the transfer-log assertions.
        plugin.Close();

        IReadOnlyList<LightingTransfer> expected = SlInfinityLightingEncoder.Encode(
            new[] { new LightingPortState(0, 26, 0, 0, 0, new[] { new RgbColor(255, 0, 0) }) },
            new[] { 4, 4, 4, 4 });

        FakeDeviceTransport transport = Assert.Single(enumerator.Opened);
        // Lighting is applied before fan setup, so the look is the prefix of the transfer log.
        Assert.True(transport.Transfers.Count >= expected.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].IsFeature, transport.Transfers[i].Key);
            Assert.Equal(expected[i].Report, transport.Transfers[i].Value);
        }
    }

    [Fact]
    public void Reconnect_ReappliesSavedLook_OnTheNextTick()
    {
        WriteSavedLook();
        var enumerator = new FakeEnumerator(Device(0xA102, DevicePath));
        using LianLiPlugin plugin = NewPlugin(enumerator);
        plugin.Initialize();
        FakeDeviceTransport transport = Assert.Single(enumerator.Opened);
        transport.Clear();

        // The transport reports it reopened the device (a wake). Either the host tick or the
        // background keepalive tick picks it up; poll the host tick with a bounded wait rather
        // than a fixed sleep.
        transport.Generation = 1;
        IReadOnlyList<LightingTransfer> expected = SlInfinityLightingEncoder.Encode(
            new[] { new LightingPortState(0, 26, 0, 0, 0, new[] { new RgbColor(255, 0, 0) }) },
            new[] { 4, 4, 4, 4 });
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && IndexOfSequence(transport, expected) < 0)
        {
            plugin.Update();
            Thread.Sleep(20);
        }

        plugin.Close();

        // The look appears again, contiguous, somewhere after the reconnect (an RPM primer from a
        // tick already in flight may precede it).
        Assert.True(IndexOfSequence(transport, expected) >= 0, "saved look was not replayed after the reconnect");
    }

    // The same through the real HID transport, with the worker running: the controller drops off
    // the bus on a poll and refuses one reopen, so the replay is not tried while it is gone - it
    // would be refused, and the look then lost - and the saved look, the manual-mode assert and
    // the duty reach the reopened handle once it is back.
    [Fact]
    public void Reconnect_OverTheRealTransport_ReappliesSavedLook_OnceTheDeviceIsBack()
    {
        WriteSavedLook();
        var hid = new FakeHidApi();
        var logger = new FakeLogger();
        var gate = new DeviceCallGate(new FakeDeviceCallRunner(), new FakeDeviceCallClock(), logger);
        HidTransport transport = HidTransport.Open(
            new HidInterface(0x0CF2, 0xA102, DevicePath, new HidCapabilities(0xFF72, 65, 353, 65)),
            hid, logger, gate, new FakeTransferDelay(), CancellationToken.None);
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()) { WorkerTickIntervalMilliseconds = 20 };
        using var plugin = new LianLiPlugin(
            new OpenedEnumerator(Device(0xA102, DevicePath), transport), new DeviceCatalog(), new FakeClock(), new FakeDelay(), logger, _lConnect.Locations, runtime);

        // The population probe reads the input report six times at Initialize; the ninth read is
        // the worker's third poll, which loses the device. Everything runs on the worker's thread
        // from there, the scripting included.
        int reads = 0;
        int featuresAtReopen = -1;
        int writesAtReopen = -1;
        hid.OnCall = call =>
        {
            if (call.StartsWith("HidD_GetInputReport", StringComparison.Ordinal) && ++reads == 9)
            {
                hid.TransferResults.Enqueue(1167);
                hid.StreamOpenErrors.Enqueue(2);
            }
            else if (call.StartsWith("OpenStreamHandle", StringComparison.Ordinal) && reads >= 9 && hid.StreamOpenErrors.Count == 0 && featuresAtReopen < 0)
            {
                featuresAtReopen = hid.Features.Count;
                writesAtReopen = hid.Written.Count;
            }
        };
        plugin.Initialize();
        var sensors = new FakeSensorsContainer();
        plugin.Load(sensors);
        sensors.ControlSensors[0].Set(40f);

        Assert.True(SpinWait.SpinUntil(() => logger.Messages.Contains("C0 reconnected: setup replayed (transport generation 1)"), TimeSpan.FromSeconds(10)),
            string.Join("\n", logger.Messages));
        plugin.Close();

        Assert.True(featuresAtReopen >= 0);
        Assert.Contains(logger.Messages, m => m.StartsWith("  reopened " + DevicePath + " after ", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("lighting apply failed"));
        IReadOnlyList<LightingTransfer> expected = SlInfinityLightingEncoder.Encode(
            new[] { new LightingPortState(0, 26, 0, 0, 0, new[] { new RgbColor(255, 0, 0) }) },
            new[] { 4, 4, 4, 4 });
        int feature = hid.Features.FindIndex(featuresAtReopen, f => f[0] == 0xE0 && f[1] == 0x10 && f[2] == 0x60);
        int write = writesAtReopen;
        Assert.True(feature >= 0, "no look after the reopen");
        foreach (LightingTransfer transfer in expected)
        {
            if (transfer.IsFeature)
            {
                Assert.Equal(transfer.Report, hid.Features[feature++].Take(transfer.Report.Length));
            }
            else
            {
                Assert.Equal(transfer.Report, hid.Written[write++]);
            }
        }

        Assert.Contains(hid.Features.Skip(feature), f => f[0] == 0xE0 && f[1] == 0x20 && f[3] == 40); // the duty, re-sent after the look and the manual-mode assert
    }

    // An enumerator handing out a transport opened by the test, so the plugin drives a controller
    // over the real HID transport.
    private sealed class OpenedEnumerator : IDeviceEnumerator
    {
        private readonly LocatedDevice _device;
        private readonly IDeviceTransport _transport;

        public OpenedEnumerator(LocatedDevice device, IDeviceTransport transport)
        {
            _device = device;
            _transport = transport;
        }

        public IReadOnlyList<LocatedDevice> Locate(IReadOnlyList<int> vendorIds, IReadOnlyList<int> productIds) => new[] { _device };

        public IDeviceTransport Open(LocatedDevice info) => _transport;
    }

    private static int IndexOfSequence(FakeDeviceTransport transport, IReadOnlyList<LightingTransfer> expected)
    {
        KeyValuePair<bool, byte[]>[] transfers = transport.SnapshotTransfers();
        for (int start = 0; start + expected.Count <= transfers.Length; start++)
        {
            bool match = true;
            for (int i = 0; i < expected.Count && match; i++)
            {
                match = transfers[start + i].Key == expected[i].IsFeature
                    && expected[i].Report.AsSpan().SequenceEqual(transfers[start + i].Value);
            }

            if (match)
            {
                return start;
            }
        }

        return -1;
    }

    [Fact]
    public void Initialize_SkipsLighting_ForUnsupportedFamily()
    {
        WriteSavedLook();
        // A legacy Uni Hub (0x7750) matches the saved look by token but has no verified lighting
        // protocol, so it hits the "family not supported" branch and is left untouched.
        var enumerator = new FakeEnumerator(Device(0x7750, DevicePath));
        using LianLiPlugin plugin = NewPlugin(enumerator);

        plugin.Initialize();

        // The controller is still registered: fan control is unaffected by the lighting skip.
        var container = new FakeSensorsContainer();
        plugin.Load(container);
        Assert.Equal(4, container.ControlSensors.Count);

        plugin.Close(); // stop the worker before inspecting the transport
        FakeDeviceTransport transport = Assert.Single(enumerator.Opened);
        // Lighting colours are the only output reports (fan control is all feature reports), so an
        // empty output log means no lighting was applied for this unsupported family.
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public void Initialize_DrivesNoLighting_WhenNoSavedLookMatches()
    {
        // Config directory is empty: nothing to apply.
        var enumerator = new FakeEnumerator(Device(0xA102, DevicePath));
        using LianLiPlugin plugin = NewPlugin(enumerator);

        plugin.Initialize();
        plugin.Close(); // stop the worker before inspecting the transport

        FakeDeviceTransport transport = Assert.Single(enumerator.Opened);
        // No saved look matched, so no lighting output report (the colour data) was sent.
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public void Initialize_LightingWriteFault_DisablesLightingButKeepsFanControl()
    {
        WriteSavedLook();
        // The device rejects the lighting output write (the colour report), but feature reports - the
        // fan-control path and the lighting effect - still work, so fan control must be unaffected.
        var enumerator = new FakeEnumerator(Device(0xA102, DevicePath)) { FailWrites = true };
        using LianLiPlugin plugin = NewPlugin(enumerator);

        plugin.Initialize();

        // The lighting fault is isolated: the controller is still registered with its sensors.
        var container = new FakeSensorsContainer();
        plugin.Load(container);
        Assert.Equal(4, container.ControlSensors.Count);
        Assert.Equal(4, container.FanSensors.Count);
    }

    [Fact]
    public void Initialize_AppliesStrimerLook_ToMatchingStrimerPlus()
    {
        WriteStrimerLook();
        var enumerator = new FakeEnumerator(Device(0xA200, StrimerPath));
        using LianLiPlugin plugin = NewPlugin(enumerator);

        plugin.Initialize();

        // Driven on a thread of its own, opened, applied and disposed - nothing keeps it alive.
        Assert.True(SpinWait.SpinUntil(() => enumerator.Opened.Count == 1 && enumerator.Opened[0].IsDisposed, TimeSpan.FromSeconds(5)));
        FakeDeviceTransport transport = Assert.Single(enumerator.Opened);
        IReadOnlyList<LightingTransfer> expected = StrimerPlusLightingEncoder.Encode(
            new[] { new LightingPortState(0, 1, 0, 0, 0, new[] { new RgbColor(255, 0, 0) }) });

        Assert.Equal(expected.Count, transport.Transfers.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].IsFeature, transport.Transfers[i].Key);
            Assert.Equal(expected[i].Report, transport.Transfers[i].Value);
        }

        // It registers no fan sensors (it has no fan protocol).
        var container = new FakeSensorsContainer();
        plugin.Load(container);
        Assert.Empty(container.ControlSensors);
        Assert.Empty(container.FanSensors);
    }

    // A Uni Fan TL hub and a Galahad II at the same instance token as the SL-Infinity above, so
    // the saved looks written below match them.
    private const string TlPath = @"\\?\hid#vid_0416&pid_7372&mi_01#7&9c2f7a3&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
    private const string GalahadPath = @"\\?\hid#vid_0416&pid_7371&mi_01#7&9c2f7a3&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
    private const string VisionPath = @"\\?\hid#vid_0416&pid_7391&mi_01#7&9c2f7a3&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
    private const string HydroShiftPath = @"\\?\hid#vid_0416&pid_7398&mi_01#7&9c2f7a3&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

    // The Vision's saved fan light (Color1..Color4) and a static screen ring, as L-Connect writes them.
    private const string VisionFanJson =
        "{\"Mode\":5,\"Brightness\":3,\"Speed\":2,\"Color1\":{\"R\":1,\"G\":2,\"B\":3},\"Color2\":{\"R\":0,\"G\":0,\"B\":0},\"Color3\":{\"R\":0,\"G\":0,\"B\":0},\"Color4\":{\"R\":0,\"G\":0,\"B\":0},\"Direction\":1,\"SyncToPump\":false,\"NumberOfLED\":24}";
    private const string VisionStaticScreenJson =
        "{\"Mode\":12,\"IsDynamicMode\":false,\"Static\":{\"Colors\":[{\"R\":255,\"G\":0,\"B\":0}],\"Speed\":50,\"Brightness\":100,\"Direction\":1}}";
    private const string VisionDynamicScreenJson =
        "{\"Mode\":3,\"IsDynamicMode\":true,\"SensorType\":2,\"Static\":{\"Colors\":[],\"Speed\":50,\"Brightness\":100,\"Direction\":0}}";

    [Theory]
    [InlineData(0xA100, "Sl")]
    [InlineData(0xA106, "Sl")]
    [InlineData(0xA101, "Al")]
    [InlineData(0xA103, "SlV2")]
    [InlineData(0xA105, "SlV2")]
    [InlineData(0xA104, "AlV2")]
    public void Initialize_AppliesSavedLook_WithTheControllersOwnFamilyEncoder(int productId, string family)
    {
        WriteSavedLook();
        var enumerator = new FakeEnumerator(Device(productId, DevicePath));
        using LianLiPlugin plugin = NewPlugin(enumerator);

        plugin.Initialize();
        plugin.Close();

        UniFanLightingProfile profile = family switch
        {
            "Sl" => UniFanLightingProfiles.Sl,
            "Al" => UniFanLightingProfiles.Al,
            "SlV2" => UniFanLightingProfiles.SlV2,
            _ => UniFanLightingProfiles.AlV2,
        };
        IReadOnlyList<LightingTransfer> expected = UniFanLightingEncoder.Encode(
            profile,
            new[] { new LightingPortState(0, 26, 0, 0, 0, new[] { new RgbColor(255, 0, 0) }) },
            new[] { 4, 4, 4, 4 });
        Assert.Equal(0, IndexOfSequence(Assert.Single(enumerator.Opened), expected));
    }

    [Fact]
    public void Initialize_ReplaysAPerFanTlLook()
    {
        string folder = Path.Combine(_configDir, "tl");
        Directory.CreateDirectory(folder);
        WriteGzip(folder, "lighting", SettingFor(TlPath, "Lighting",
            "{\"LightingConfigs\":[{\"1\":[{\"IsGrouping\":false,\"Configs\":[{\"Mode\":3,\"Speed\":2,\"Direction\":0,\"Brightness\":2,\"Colors\":[{\"R\":255,\"G\":0,\"B\":0}]}]}]}]}"));
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(new LocatedDevice(0x0416, 0x7372, TlPath, null)) { ConfigureTransport = SeedTlHandshake };
        using LianLiPlugin plugin = NewPlugin(enumerator, logger);

        plugin.Initialize();
        plugin.Close();

        Assert.Contains(logger.Messages, m => m.Contains("lighting applied for 9c2f7a3"));
    }

    [Fact]
    public void Initialize_SkipsATlLookWithNoPerFanEntries()
    {
        WriteSavedLook(); // ports only: nothing a TL hub can address per fan
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(new LocatedDevice(0x0416, 0x7372, TlPath, null)) { ConfigureTransport = SeedTlHandshake };
        using LianLiPlugin plugin = NewPlugin(enumerator, logger);

        plugin.Initialize();
        plugin.Close();

        Assert.Contains(logger.Messages, m => m.Contains("lighting skipped for 9c2f7a3: no per-fan TL look saved"));
    }

    [Theory]
    [InlineData(true, true, "lighting applied for 9c2f7a3")]
    [InlineData(true, false, "lighting skipped for 9c2f7a3: incomplete Galahad look saved")]
    [InlineData(false, true, "lighting skipped for 9c2f7a3: incomplete Galahad look saved")]
    public void Initialize_ReplaysAGalahadLookOnlyWhenBothHalvesAreSaved(bool withFan, bool withPump, string expected)
    {
        string folder = Path.Combine(_configDir, "galahad");
        Directory.CreateDirectory(folder);
        if (withFan)
        {
            WriteGzip(folder, "fan", SettingFor(GalahadPath, "FanLEDLighting",
                "{\"Mode\":3,\"Brightness\":2,\"Speed\":4,\"Colors\":[{\"R\":255,\"G\":0,\"B\":0}],\"Direction\":1}"));
        }

        if (withPump)
        {
            WriteGzip(folder, "pump", SettingFor(GalahadPath, "PumpLEDLighting",
                "[{\"Scope\":2,\"Mode\":2001,\"Brightness\":2,\"Speed\":3,\"Colors\":[{\"R\":0,\"G\":0,\"B\":255}],\"Direction\":5}]"));
        }

        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(new LocatedDevice(0x0416, 0x7371, GalahadPath, null));
        using LianLiPlugin plugin = NewPlugin(enumerator, logger);

        plugin.Initialize();
        plugin.Close();

        Assert.Contains(logger.Messages, m => m.Contains(expected));
    }

    [Theory]
    [InlineData(0x7391)]
    [InlineData(0x7395)]
    public void Initialize_ReplaysAVisionFanLightAndStaticScreenRing(int productId)
    {
        string folder = Path.Combine(_configDir, "vision");
        Directory.CreateDirectory(folder);
        WriteGzip(folder, "fan", SettingFor(VisionPath, "FanLEDLighting", VisionFanJson));
        WriteGzip(folder, "screen", SettingFor(VisionPath, "ScreenLEDLighting", VisionStaticScreenJson));
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(new LocatedDevice(0x0416, productId, VisionPath, null));
        using LianLiPlugin plugin = NewPlugin(enumerator, logger);

        plugin.Initialize();
        plugin.Close();

        FakeDeviceTransport transport = Assert.Single(enumerator.Opened);
        IReadOnlyList<LightingTransfer> expected = Galahad2LightingEncoder.EncodeVision(
            new Galahad2FanLightingState(5, 2, 1, 3, 24, false, new[] { new RgbColor(1, 2, 3), default, default, default }),
            new Galahad2ScreenLightingState(12, false, 50, 100, 1, new[] { new RgbColor(255, 0, 0) }));
        Assert.Equal(0, IndexOfSequence(transport, expected));
        Assert.Single(transport.Writes, w => w[1] == 0x85);
        Assert.Single(transport.Writes, w => w[1] == 0x83);
        Assert.Contains(logger.Messages, m => m.Contains("lighting applied for 9c2f7a3 (2 writes)"));
    }

    [Fact]
    public void Initialize_LeavesAVisionScreenRingThatFollowsASensor_AndStillWritesItsFanLight()
    {
        string folder = Path.Combine(_configDir, "vision");
        Directory.CreateDirectory(folder);
        WriteGzip(folder, "fan", SettingFor(VisionPath, "FanLEDLighting", VisionFanJson));
        WriteGzip(folder, "screen", SettingFor(VisionPath, "ScreenLEDLighting", VisionDynamicScreenJson));
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(new LocatedDevice(0x0416, 0x7391, VisionPath, null));
        using LianLiPlugin plugin = NewPlugin(enumerator, logger);

        plugin.Initialize();
        plugin.Close();

        FakeDeviceTransport transport = Assert.Single(enumerator.Opened);
        Assert.Single(transport.Writes, w => w[1] == 0x85);
        Assert.DoesNotContain(transport.Writes, w => w[1] == 0x83);
        Assert.Contains(logger.Messages, m => m.Contains("screen ring left as found for 9c2f7a3: its saved look follows a live sensor in L-Connect"));
        Assert.Contains(logger.Messages, m => m.Contains("lighting applied for 9c2f7a3 (1 writes)"));
    }

    [Fact]
    public void Initialize_ReplaysAVisionFanLightAlone_WhenNoScreenRingWasSaved()
    {
        string folder = Path.Combine(_configDir, "vision");
        Directory.CreateDirectory(folder);
        WriteGzip(folder, "fan", SettingFor(VisionPath, "FanLEDLighting", VisionFanJson));
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(new LocatedDevice(0x0416, 0x7391, VisionPath, null));
        using LianLiPlugin plugin = NewPlugin(enumerator, logger);

        plugin.Initialize();
        plugin.Close();

        FakeDeviceTransport transport = Assert.Single(enumerator.Opened);
        Assert.Single(transport.Writes, w => w[1] == 0x85);
        Assert.DoesNotContain(transport.Writes, w => w[1] == 0x83);
        Assert.Contains(logger.Messages, m => m.Contains("lighting applied for 9c2f7a3 (1 writes)"));
    }

    [Fact]
    public void Initialize_ReplaysAVisionScreenRingAlone_WhenNoFanLightWasSaved()
    {
        string folder = Path.Combine(_configDir, "vision");
        Directory.CreateDirectory(folder);
        WriteGzip(folder, "screen", SettingFor(VisionPath, "ScreenLEDLighting", VisionStaticScreenJson));
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(new LocatedDevice(0x0416, 0x7391, VisionPath, null));
        using LianLiPlugin plugin = NewPlugin(enumerator, logger);

        plugin.Initialize();
        plugin.Close();

        FakeDeviceTransport transport = Assert.Single(enumerator.Opened);
        Assert.DoesNotContain(transport.Writes, w => w[1] == 0x85);
        Assert.Single(transport.Writes, w => w[1] == 0x83);
        Assert.Contains(logger.Messages, m => m.Contains("lighting applied for 9c2f7a3 (1 writes)"));
    }

    [Fact]
    public void Initialize_SkipsAVisionWhoseOnlySavedLookIsADynamicScreenRing()
    {
        string folder = Path.Combine(_configDir, "vision");
        Directory.CreateDirectory(folder);
        WriteGzip(folder, "screen", SettingFor(VisionPath, "ScreenLEDLighting", VisionDynamicScreenJson));
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(new LocatedDevice(0x0416, 0x7391, VisionPath, null));
        using LianLiPlugin plugin = NewPlugin(enumerator, logger);

        plugin.Initialize();
        plugin.Close();

        Assert.DoesNotContain(Assert.Single(enumerator.Opened).Writes, w => w[1] == 0x85 || w[1] == 0x83);
        Assert.Contains(logger.Messages, m => m.Contains("lighting skipped for 9c2f7a3: no Vision look saved"));
    }

    [Theory]
    [InlineData(0x7398)]
    [InlineData(0x7399)]
    [InlineData(0x739A)]
    public void Initialize_ReplaysAHydroShiftLcdFanLight(int productId)
    {
        string folder = Path.Combine(_configDir, "hydroshift");
        Directory.CreateDirectory(folder);
        WriteGzip(folder, "fan", SettingFor(HydroShiftPath, "FanLEDLighting",
            "{\"Mode\":16,\"Brightness\":4,\"Speed\":1,\"Colors\":[{\"R\":9,\"G\":8,\"B\":7}],\"Direction\":2,\"SyncToPump\":false,\"NumberOfLED\":36}"));
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(new LocatedDevice(0x0416, productId, HydroShiftPath, null));
        using LianLiPlugin plugin = NewPlugin(enumerator, logger);

        plugin.Initialize();
        plugin.Close();

        FakeDeviceTransport transport = Assert.Single(enumerator.Opened);
        IReadOnlyList<LightingTransfer> expected = Galahad2LightingEncoder.EncodeHydroShiftLcd(
            new Galahad2FanLightingState(16, 1, 2, 4, 36, false, new[] { new RgbColor(9, 8, 7) }));
        Assert.Equal(0, IndexOfSequence(transport, expected));
        Assert.DoesNotContain(transport.Writes, w => w[1] == 0x83);
        Assert.Contains(logger.Messages, m => m.Contains("lighting applied for 9c2f7a3 (1 writes)"));
    }

    [Fact]
    public void Initialize_SkipsAHydroShiftLcdWithNoFanLookSaved()
    {
        string folder = Path.Combine(_configDir, "hydroshift");
        Directory.CreateDirectory(folder);
        WriteGzip(folder, "screen", SettingFor(HydroShiftPath, "ScreenLEDLighting", VisionStaticScreenJson)); // not a HydroShift setting, but a look
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(new LocatedDevice(0x0416, 0x7398, HydroShiftPath, null));
        using LianLiPlugin plugin = NewPlugin(enumerator, logger);

        plugin.Initialize();
        plugin.Close();

        Assert.DoesNotContain(Assert.Single(enumerator.Opened).Writes, w => w[1] == 0x85 || w[1] == 0x83);
        Assert.Contains(logger.Messages, m => m.Contains("lighting skipped for 9c2f7a3: no HydroShift LCD fan look saved"));
    }

    [Fact]
    public void Initialize_HandsTheSavedMergeOrderToTheEncoder()
    {
        WriteSavedLook();
        string folder = Path.Combine(_configDir, "controller");
        WriteGzip(folder, "order", Setting("MergeOrder", "[3,2,1,0]"));
        var enumerator = new FakeEnumerator(Device(0xA102, DevicePath));
        using LianLiPlugin plugin = NewPlugin(enumerator);

        plugin.Initialize();
        plugin.Close();

        FakeDeviceTransport transport = Assert.Single(enumerator.Opened);
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x63, 3, 2, 1, 0, 8 }, transport.Transfers[7].Value); // after the look and its frame, before the fan setup
    }

    // SLInfinityController.ApplyAll applies a saved MergeOrder through setMergeOrder before the
    // merge sequence, so a Runway merge with a saved order gets that order, not the default.
    [Fact]
    public void Initialize_RestoresTheSavedGroupOrder_InMergeMode()
    {
        string folder = Path.Combine(_configDir, "controller");
        Directory.CreateDirectory(folder);
        WriteGzip(folder, "port", Setting("LightingPort0", "{\"Port\":0,\"Mode\":107}"));
        WriteGzip(folder, "order", Setting("MergeOrder", "[3,2,1,0]"));
        var enumerator = new FakeEnumerator(Device(0xA102, DevicePath));
        using LianLiPlugin plugin = NewPlugin(enumerator);

        plugin.Initialize();
        plugin.Close();

        FakeDeviceTransport transport = Assert.Single(enumerator.Opened);
        Assert.Equal(new byte[] { 0xE0, 0x60, 0, 1, 0, 0, 0 }, transport.Transfers[20].Value); // the default look's frame
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x63, 3, 2, 1, 0, 8 }, transport.Transfers[21].Value); // then the order, before the blanks
        Assert.Contains(transport.Transfers, t => t.Key && t.Value[1] == 0x10 && t.Value[2] == 70); // Runway_Merge on port 0
    }

    [Fact]
    public void Initialize_MotherboardArgbSync_HandsAUniControllerToTheMotherboardInsteadOfItsLook()
    {
        WriteSavedLook();
        WriteMotherboardArgbSync("controller", DevicePath);
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(Device(0xA102, DevicePath));
        using LianLiPlugin plugin = NewPlugin(enumerator, logger);

        plugin.Initialize();
        plugin.Close();

        // The fan quantity then the ARGB-sync register, before fan setup, and no colour report at all.
        IReadOnlyList<LightingTransfer> expected = SlInfinityLightingEncoder.Encode(
            new[] { new LightingPortState(0, 26, 0, 0, 0, new[] { new RgbColor(255, 0, 0) }) },
            new[] { 4, 4, 4, 4 },
            motherboardArgbSync: true);
        FakeDeviceTransport transport = Assert.Single(enumerator.Opened);
        Assert.Equal(0, IndexOfSequence(transport, expected));
        Assert.Empty(transport.Writes);
        Assert.Contains(logger.Messages, m => m.Contains("lighting left to the motherboard for 9c2f7a3 (6 writes)"));
    }

    [Fact]
    public void Initialize_MotherboardArgbSync_WritesATlHubNoLighting()
    {
        string folder = Path.Combine(_configDir, "tl");
        Directory.CreateDirectory(folder);
        WriteGzip(folder, "lighting", SettingFor(TlPath, "Lighting",
            "{\"LightingConfigs\":[{\"1\":[{\"IsGrouping\":false,\"Configs\":[{\"Mode\":3,\"Speed\":2,\"Direction\":0,\"Brightness\":2,\"Colors\":[{\"R\":255,\"G\":0,\"B\":0}]}]}]}]}"));
        WriteMotherboardArgbSync("tl", TlPath);
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(new LocatedDevice(0x0416, 0x7372, TlPath, null)) { ConfigureTransport = SeedTlHandshake };
        using LianLiPlugin plugin = NewPlugin(enumerator, logger);

        plugin.Initialize();
        plugin.Close();

        // No 0xA3 fan-light packet went out; fan control's own packets still did.
        FakeDeviceTransport transport = Assert.Single(enumerator.Opened);
        Assert.DoesNotContain(transport.Writes, w => w[1] == 0xA3);
        Assert.NotEmpty(transport.Writes);
        Assert.Contains(logger.Messages, m => m.Contains("lighting left to the motherboard for 9c2f7a3: L-Connect writes a TL hub nothing"));
    }

    [Fact]
    public void Initialize_MotherboardArgbSync_WritesAGalahadLookWithTheMotherboardAsItsSource()
    {
        string folder = Path.Combine(_configDir, "galahad");
        Directory.CreateDirectory(folder);
        WriteGzip(folder, "fan", SettingFor(GalahadPath, "FanLEDLighting",
            "{\"Mode\":3,\"Brightness\":2,\"Speed\":4,\"Colors\":[{\"R\":255,\"G\":0,\"B\":0}],\"Direction\":1}"));
        WriteGzip(folder, "pump", SettingFor(GalahadPath, "PumpLEDLighting",
            "[{\"Scope\":2,\"Mode\":2001,\"Brightness\":2,\"Speed\":3,\"Colors\":[{\"R\":0,\"G\":0,\"B\":255}],\"Direction\":5}]"));
        WriteMotherboardArgbSync("galahad", GalahadPath);
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(new LocatedDevice(0x0416, 0x7371, GalahadPath, null));
        using LianLiPlugin plugin = NewPlugin(enumerator, logger);

        plugin.Initialize();
        plugin.Close();

        FakeDeviceTransport transport = Assert.Single(enumerator.Opened);
        byte[] fanLight = Assert.Single(transport.Writes, w => w[1] == 0x85);
        byte[] pumpLight = Assert.Single(transport.Writes, w => w[1] == 0x83);
        Assert.Equal(1, fanLight[23]);  // fan payload[17]: source = motherboard
        Assert.Equal(1, pumpLight[24]); // pump payload[18]: source = motherboard
        Assert.Contains(logger.Messages, m => m.Contains("lighting left to the motherboard for 9c2f7a3 (2 writes)"));
    }

    // Galahad2TrinityController.ApplyAll calls setFanLEDLighting and setPumpLEDLighting whatever
    // was saved, with its default FanLightingSetting / PumpLightingSetting for a half that never
    // was: the switch alone hands both lights over.
    [Theory]
    [InlineData(false, false, "fan and pump")]
    [InlineData(true, false, "pump")]
    [InlineData(false, true, "fan")]
    public void Initialize_MotherboardArgbSync_HandsAGalahadOverWithLConnectsDefaultForAnUnsavedHalf(bool withFan, bool withPump, string defaulted)
    {
        string folder = Path.Combine(_configDir, "galahad");
        Directory.CreateDirectory(folder);
        if (withFan)
        {
            WriteGzip(folder, "fan", SettingFor(GalahadPath, "FanLEDLighting", "{\"Mode\":3,\"Brightness\":2,\"Speed\":4,\"Colors\":[{\"R\":255,\"G\":0,\"B\":0}],\"Direction\":1}"));
        }

        if (withPump)
        {
            WriteGzip(folder, "pump", SettingFor(GalahadPath, "PumpLEDLighting", "[{\"Scope\":2,\"Mode\":2005,\"Brightness\":2,\"Speed\":3,\"Colors\":[],\"Direction\":5}]"));
        }

        WriteMotherboardArgbSync("galahad", GalahadPath);
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(new LocatedDevice(0x0416, 0x7371, GalahadPath, null));
        using LianLiPlugin plugin = NewPlugin(enumerator, logger);

        plugin.Initialize();
        plugin.Close();

        FakeDeviceTransport transport = Assert.Single(enumerator.Opened);
        byte[] fanLight = Assert.Single(transport.Writes, w => w[1] == 0x85);
        byte[] pumpLight = Assert.Single(transport.Writes, w => w[1] == 0x83);
        Assert.Equal(withFan ? 3 : 1, fanLight[6]);   // the saved mode, or L-Connect's default Rainbow
        Assert.Equal(withPump ? 5 : 1, pumpLight[7]); // 2005 % 1000, or Rainbow
        Assert.Equal(1, fanLight[23]);
        Assert.Equal(1, pumpLight[24]);
        Assert.Contains(logger.Messages, m => m.Contains("lighting handed to the motherboard for 9c2f7a3 with L-Connect's default " + defaulted + " light, since none was saved"));
        Assert.Contains(logger.Messages, m => m.Contains("lighting left to the motherboard for 9c2f7a3 (2 writes)"));
    }

    // setPumpLEDLighting iterates the whole saved array: an individual-mode look's Inner and Outer
    // scopes both get the source change, and both are replayed without the switch too.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Initialize_ReplaysEverySavedGalahadPumpScope(bool motherboardArgbSync)
    {
        string folder = Path.Combine(_configDir, "galahad");
        Directory.CreateDirectory(folder);
        WriteGzip(folder, "fan", SettingFor(GalahadPath, "FanLEDLighting", "{}"));
        WriteGzip(folder, "pump", SettingFor(GalahadPath, "PumpLEDLighting", "[{\"Scope\":0,\"Mode\":1},{\"Scope\":1,\"Mode\":1}]"));
        if (motherboardArgbSync)
        {
            WriteMotherboardArgbSync("galahad", GalahadPath);
        }

        var enumerator = new FakeEnumerator(new LocatedDevice(0x0416, 0x7371, GalahadPath, null));
        using LianLiPlugin plugin = NewPlugin(enumerator);

        plugin.Initialize();
        plugin.Close();

        FakeDeviceTransport transport = Assert.Single(enumerator.Opened);
        byte source = motherboardArgbSync ? (byte)1 : (byte)0;
        Assert.Contains(transport.Writes, w => w[1] == 0x83 && w[6] == 0 && w[24] == source);
        Assert.Contains(transport.Writes, w => w[1] == 0x83 && w[6] == 1 && w[24] == source);
        Assert.Equal(3, transport.Writes.Count(w => w[1] == 0x85 || w[1] == 0x83));
    }

    [Fact]
    public void Initialize_MotherboardArgbSync_HandsAStrimerToTheMotherboardInsteadOfItsLook()
    {
        WriteStrimerLook();
        WriteMotherboardArgbSync("strimer", StrimerPath);
        var enumerator = new FakeEnumerator(Device(0xA200, StrimerPath));
        using LianLiPlugin plugin = NewPlugin(enumerator);

        plugin.Initialize();

        Assert.True(SpinWait.SpinUntil(() => enumerator.Opened.Count == 1 && enumerator.Opened[0].IsDisposed, TimeSpan.FromSeconds(5)));
        FakeDeviceTransport transport = Assert.Single(enumerator.Opened);
        IReadOnlyList<LightingTransfer> expected = StrimerPlusLightingEncoder.Encode(
            new[] { new LightingPortState(0, 1, 0, 0, 0, new[] { new RgbColor(255, 0, 0) }) },
            motherboardArgbSync: true);
        Assert.Equal(expected.Count, transport.Transfers.Count);
        Assert.Equal(0, IndexOfSequence(transport, expected));
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public void Initialize_ACorruptSavedLook_DisablesLightingButKeepsFanControl()
    {
        string folder = Path.Combine(_configDir, "controller");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "port0.0"), "not gzip");
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(Device(0xA102, DevicePath));
        using LianLiPlugin plugin = NewPlugin(enumerator, logger);

        plugin.Initialize();
        var container = new FakeSensorsContainer();
        plugin.Load(container);

        Assert.Contains(logger.Messages, m => m.Contains("Lighting: config read failed, lighting disabled"));
        Assert.Equal(4, container.ControlSensors.Count);
    }

    [Fact]
    public void Initialize_AStrimerThatThrowsOnClose_IsLogged()
    {
        WriteStrimerLook();
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(Device(0xA200, StrimerPath))
        {
            ConfigureTransport = (info, transport) => transport.DisposeFault = new IOException("handle gone"),
        };
        using LianLiPlugin plugin = NewPlugin(enumerator, logger);

        plugin.Initialize();

        Assert.True(SpinWait.SpinUntil(
            () => logger.Messages.Contains("  lighting-only close failed pid=0xa200: handle gone"), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Initialize_AStrimerThatWillNotOpen_IsLoggedAndSkipped()
    {
        WriteStrimerLook();
        var logger = new FakeLogger();
        var enumerator = new FakeEnumerator(Device(0xA200, StrimerPath)) { FailOpen = true };
        using LianLiPlugin plugin = NewPlugin(enumerator, logger);

        plugin.Initialize();

        Assert.True(SpinWait.SpinUntil(
            () => logger.Messages.Any(m => m.Contains("lighting-only open failed pid=0xa200")), TimeSpan.FromSeconds(5)));
    }

    private static void SeedTlHandshake(LocatedDevice info, FakeDeviceTransport transport)
        => transport.ReadReplies.Enqueue(CommandPacket.Build(0xA1, 0x80, 0x03, 0xE8));

    private LianLiPlugin NewPlugin(FakeEnumerator enumerator)
        => NewPlugin(enumerator, new FakeLogger());

    private LianLiPlugin NewPlugin(FakeEnumerator enumerator, FakeLogger logger)
        => new LianLiPlugin(enumerator, new DeviceCatalog(), new FakeClock(), new FakeDelay(), logger, _lConnect.Locations, new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()));

    private static LocatedDevice Device(int productId, string devicePath)
        => new LocatedDevice(0x0CF2, productId, devicePath, null);

    // Write one controller's saved look (a StaticColor port and a fan quantity) as L-Connect
    // stores it: gzipped JSON setting files under a per-device folder.
    private void WriteSavedLook()
    {
        string folder = Path.Combine(_configDir, "controller");
        Directory.CreateDirectory(folder);
        WriteGzip(
            folder,
            "port0",
            Setting("LightingPort0", "{\"Port\":0,\"Mode\":26,\"Speed\":0,\"Direction\":0,\"Brightness\":0,\"Colors\":[{\"R\":255,\"G\":0,\"B\":0}]}"));
        WriteGzip(folder, "quantity", Setting("FanQuantity", "[4,4,4,4]"));
    }

    // Write a Strimer Plus saved look the way L-Connect stores it: a "Port0" setting holding a
    // StaticColor_Individual (mode 1) red look under a per-device folder, keyed to the Strimer path.
    private void WriteStrimerLook()
    {
        string folder = Path.Combine(_configDir, "strimer");
        Directory.CreateDirectory(folder);
        WriteGzip(
            folder,
            "port0",
            SettingFor(
                StrimerPath,
                "Port0",
                "{\"Port\":0,\"Mode\":1,\"Speed\":0,\"Direction\":0,\"Brightness\":0,\"Colors\":[{\"R\":255,\"G\":0,\"B\":0}]}"));
    }

    // Three wired SL-Infinity controllers, each with its own saved look, keep all twelve control
    // ids and twelve RPM ids in the same order across a scan that lists them in reverse, one in
    // which the middle controller will not open, and a new runtime restored from the saved store,
    // and every reachable one gets its own look.
    [Fact]
    public void ThreeSlInfinityControllers_KeepTheirIdsAndOwnSavedLooksAcrossRefreshAndRestart()
    {
        var store = new FakeRememberedControllerStore();
        var clock = new FakeClock();
        var runtime = new PluginRuntime(clock, store);
        LocatedDevice[] devices = Enumerable.Range(0, 3).Select(i => new LocatedDevice(
            0x0CF2, 0xA102,
            @"\\?\hid#vid_0cf2&pid_a102&mi_01#7&9c2f7a" + i.ToString(CultureInfo.InvariantCulture) + @"&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
            null)).ToArray();
        for (int i = 0; i < devices.Length; i++)
        {
            string folder = Path.Combine(_configDir, "controller" + i.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            string color = "{\"R\":" + (i + 1).ToString(CultureInfo.InvariantCulture) + ",\"G\":0,\"B\":0}";
            WriteGzip(folder, "look", SettingFor(devices[i].DevicePath, "LightingPort0", "{\"Port\":0,\"Mode\":26,\"Speed\":0,\"Direction\":0,\"Brightness\":0,\"Colors\":[" + color + "]}"));
            WriteGzip(folder, "quantity", SettingFor(devices[i].DevicePath, "FanQuantity", "[4,4,4,4]"));
        }

        string[]? original = null;
        for (int scan = 0; scan < 3; scan++)
        {
            if (scan == 2)
            {
                runtime = new PluginRuntime(clock, store);
            }

            var enumerator = new FakeEnumerator(devices.Reverse().ToArray())
            {
                FailOpenWhen = device => scan == 1 && device.DevicePath == devices[1].DevicePath,
            };
            using var plugin = new LianLiPlugin(enumerator, new DeviceCatalog(), clock, new FakeDelay(), new FakeLogger(), _lConnect.Locations, runtime);
            plugin.Initialize();
            var sensors = new FakeSensorsContainer();
            plugin.Load(sensors);
            string[] controls = sensors.ControlSensors.Select(s => s.Id).ToArray();
            string[] fans = sensors.FanSensors.Select(s => s.Id).ToArray();
            Assert.Equal(12, controls.Length);
            Assert.Equal(12, controls.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(12, fans.Length);
            for (int i = 0; i < 3; i++)
            {
                for (int channel = 0; channel < 4; channel++)
                {
                    Assert.Contains($"LianLi/{i}/ch{channel}/ctl", controls);
                    Assert.Contains($"LianLi/{i}/ch{channel}/fan", fans);
                }
            }

            if (original != null)
            {
                Assert.Equal(original, controls);
            }

            original = controls;
            plugin.Close();
            for (int i = 0; i < 3; i++)
            {
                if (scan == 1 && i == 1)
                {
                    continue;
                }

                FakeDeviceTransport transport = enumerator.TransportFor(devices[i].DevicePath);
                var expected = SlInfinityLightingEncoder.Encode(
                    new[] { new LightingPortState(0, 26, 0, 0, 0, new[] { new RgbColor((byte)(i + 1), 0, 0) }) },
                    new[] { 4, 4, 4, 4 });
                for (int write = 0; write < expected.Count; write++)
                {
                    Assert.Equal(expected[write].IsFeature, transport.Transfers[write].Key);
                    Assert.Equal(expected[write].Report, transport.Transfers[write].Value);
                }

                Assert.True(transport.IsDisposed);
            }
        }
    }

    // The bytes three SL-Infinity controllers with port 0 saved as Lottery_Inner (mode 46, wire
    // 0x26) at speed 1, direction 0 and brightness 2, FanQuantity [4,4,4,4] and MergeOrder
    // [0,1,2,3] receive, spelled out, not derived from the encoder: the four quantity
    // reports, the 353-byte colour report (R, B, G per LED, the 16-slot palette repeated per fan),
    // the effect report, the frame, and the merge-order report last, after the look and its frame
    // as L-Connect's Init and ResumeSuspend send it (setFanQuantity, then setMergeOrder).
    [Fact]
    public void ThreeSlInfinityControllers_WithLotteryInnerSaved_ReceiveTheseBytesInThisOrder()
    {
        LocatedDevice[] devices = Enumerable.Range(0, 3).Select(i => new LocatedDevice(
            0x0CF2, 0xA102,
            @"\\?\hid#vid_0cf2&pid_a102&mi_01#7&9c2f7b" + i.ToString(CultureInfo.InvariantCulture) + @"&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
            null)).ToArray();
        for (int i = 0; i < 3; i++)
        {
            string folder = Path.Combine(_configDir, "lottery" + i.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            WriteGzip(folder, "look", SettingFor(devices[i].DevicePath, "LightingPort0",
                "{\"Port\":0,\"Mode\":46,\"Speed\":1,\"Direction\":0,\"Brightness\":2,\"Colors\":[{\"R\":" + (i + 1).ToString(CultureInfo.InvariantCulture) + ",\"G\":7,\"B\":9}]}"));
            WriteGzip(folder, "quantity", SettingFor(devices[i].DevicePath, "FanQuantity", "[4,4,4,4]"));
            WriteGzip(folder, "order", SettingFor(devices[i].DevicePath, "MergeOrder", "[0,1,2,3]"));
        }

        var enumerator = new FakeEnumerator(devices);
        using var plugin = new LianLiPlugin(enumerator, new DeviceCatalog(), new FakeClock(), new FakeDelay(), new FakeLogger(), _lConnect.Locations, new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()));
        plugin.Initialize();
        var sensors = new FakeSensorsContainer();
        plugin.Load(sensors);
        plugin.Close();

        Assert.Equal(12, sensors.ControlSensors.Count);
        Assert.Equal(12, sensors.FanSensors.Count);
        for (int i = 0; i < 3; i++)
        {
            List<KeyValuePair<bool, byte[]>> transfers = enumerator.TransportFor(devices[i].DevicePath).Transfers;
            Assert.Single(transfers, t => t.Value.Length == 8 && t.Value[2] == 0x63); // the lighting comes first; the fan setup follows it
            for (int group = 0; group < 4; group++)
            {
                Assert.True(transfers[group].Key);
                Assert.Equal(new byte[] { 0xE0, 0x10, 0x60, (byte)(group + 1), 4, 0, 0 }, transfers[group].Value);
            }

            var colors = new byte[353];
            colors[0] = 0xE0;
            colors[1] = 0x30;
            for (int fan = 0; fan < 4; fan++)
            {
                colors[2 + (fan * 12)] = (byte)(i + 1);
                colors[3 + (fan * 12)] = 9;
                colors[4 + (fan * 12)] = 7;
            }

            Assert.False(transfers[4].Key);
            Assert.Equal(colors, transfers[4].Value);
            Assert.True(transfers[5].Key);
            Assert.Equal(new byte[] { 0xE0, 0x10, 0x26, 1, 0, 2, 0 }, transfers[5].Value);
            Assert.True(transfers[6].Key);
            Assert.Equal(new byte[] { 0xE0, 0x60, 0, 1, 0, 0, 0 }, transfers[6].Value);
            Assert.True(transfers[7].Key);
            Assert.Equal(new byte[] { 0xE0, 0x10, 0x63, 0, 1, 2, 3, 8 }, transfers[7].Value);
        }
    }

    // Write L-Connect's per-controller "sync to motherboard" switch, on, beside a look.
    private void WriteMotherboardArgbSync(string folderName, string devicePath)
    {
        string folder = Path.Combine(_configDir, folderName);
        Directory.CreateDirectory(folder);
        WriteGzip(folder, "sync", SettingFor(devicePath, "MotherboardARGBSync", "true"));
    }

    private static string Setting(string type, string dataJson) => SettingFor(DevicePath, type, dataJson);

    private static string SettingFor(string devicePath, string type, string dataJson)
        => "{\"DeviceID\":\"" + devicePath.Replace("\\", "\\\\") + "\",\"Type\":\"" + type + "\",\"Data\":" + dataJson + "}";

    private static void WriteGzip(string folder, string name, string json)
    {
        using FileStream file = File.Create(Path.Combine(folder, name + ".0"));
        using var gzip = new GZipStream(file, CompressionMode.Compress);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        gzip.Write(bytes, 0, bytes.Length);
    }
}
#endif
