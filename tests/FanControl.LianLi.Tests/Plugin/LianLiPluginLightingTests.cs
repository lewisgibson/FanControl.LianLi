#if ENABLE_LIGHTING
using System;
using System.Collections.Generic;
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

    // Write one controller's saved look (a StaticColor port plus a fan quantity) as L-Connect
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
