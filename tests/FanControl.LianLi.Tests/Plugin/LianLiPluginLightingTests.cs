#if ENABLE_LIGHTING
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.IO.Compression;
using System.Text;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Hid;
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

    private readonly string _configDir;

    public LianLiPluginLightingTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "lianli-plugin-lighting-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_configDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a locked file must not fail the test run.
        }
    }

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

        FakeHidTransport transport = Assert.Single(enumerator.Opened);
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
        FakeHidTransport transport = Assert.Single(enumerator.Opened);
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

    private static int IndexOfSequence(FakeHidTransport transport, IReadOnlyList<LightingTransfer> expected)
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
        FakeHidTransport transport = Assert.Single(enumerator.Opened);
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

        FakeHidTransport transport = Assert.Single(enumerator.Opened);
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

        FakeHidTransport transport = Assert.Single(enumerator.Opened);
        IReadOnlyList<LightingTransfer> expected = StrimerPlusLightingEncoder.Encode(
            new[] { new LightingPortState(0, 1, 0, 0, 0, new[] { new RgbColor(255, 0, 0) }) });

        Assert.Equal(expected.Count, transport.Transfers.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].IsFeature, transport.Transfers[i].Key);
            Assert.Equal(expected[i].Report, transport.Transfers[i].Value);
        }

        // A lighting-only device is opened, applied, then disposed - nothing keeps it alive.
        Assert.True(transport.IsDisposed);

        // It registers no fan sensors (it has no fan protocol).
        var container = new FakeSensorsContainer();
        plugin.Load(container);
        Assert.Empty(container.ControlSensors);
        Assert.Empty(container.FanSensors);
    }

    private LianLiPlugin NewPlugin(FakeEnumerator enumerator)
        => new LianLiPlugin(enumerator, new DeviceCatalog(), new FakeClock(), new FakeLogger(), _configDir);

    private static HidDeviceInfo Device(int productId, string devicePath)
        => new HidDeviceInfo(0x0CF2, productId, devicePath, null);

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
