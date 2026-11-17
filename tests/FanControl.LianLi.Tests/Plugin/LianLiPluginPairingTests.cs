using System;
using System.Collections.Generic;
using System.Linq;
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
/// Which fan sensor each control names as its own (FanControl's <c>PairedFanSensorId</c>): the
/// reading its device says stands for it, as FanControl identifies that sensor - the plugin's name,
/// a slash and the sensor's id - and only a reading the same <c>Load</c> registered. Every test
/// asserts the whole set of pairings by id.
/// </summary>
public sealed class LianLiPluginPairingTests {
    // Each build advertises its own plugin name, which every pairing starts with.
#if ENABLE_ARGB
    private const string ExpectedName = "Lian Li Uni (ARGB)";
#elif ENABLE_LIGHTING
    private const string ExpectedName = "Lian Li Uni (Lighting)";
#else
    private const string ExpectedName = "Lian Li Uni";
#endif

    private const string Wireless = "LianLi/wa00000000001";

    private static LocatedDevice Sli(string path) => new LocatedDevice(0x0CF2, 0xA102, path, null);

    private static LocatedDevice Transmitter() => new LocatedDevice(0x0416, 0x8040, "fake/tx", null);

    private static LocatedDevice Receiver() => new LocatedDevice(0x0416, 0x8041, "fake/rx", null);

    private static LianLiPlugin NewPlugin(FakeEnumerator enumerator, PluginRuntime? runtime = null, FakeLogger? logger = null)
        => new LianLiPlugin(
            enumerator,
            new DeviceCatalog(),
            new FakeClock(),
            new FakeDelay(),
            logger ?? new FakeLogger(),
            LConnectDirectory.Absent,
            runtime ?? new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore()),
            CopyGuardFixture.Alone());

    private static FakeSensorsContainer Load(LianLiPlugin plugin) {
        plugin.Initialize();
        var container = new FakeSensorsContainer();
        plugin.Load(container);
        return container;
    }

    // Every pairing names exactly one fan sensor of the same load, by FanControl's own identifier.
    private static Dictionary<string, string?> Pairings(LianLiPlugin plugin, FakeSensorsContainer container) {
        var pairings = new Dictionary<string, string?>();
        foreach (IPluginControlSensor control in container.ControlSensors) {
            string? paired = Assert.IsAssignableFrom<IPluginControlSensor2>(control).PairedFanSensorId;
            if (paired != null) {
                Assert.Single(container.FanSensors, fan => plugin.Name + "/" + fan.Id == paired);
            }

            pairings[control.Id] = paired;
        }

        return pairings;
    }

    private static Dictionary<string, string?> Pairings(LianLiPlugin plugin) => Pairings(plugin, Load(plugin));

    // The wireless pair, its receiver listing the given devices on every read.
    private static FakeEnumerator Radio(params FakeWirelessRecord[] records) {
        var radio = new FakeWirelessRig();
        byte[] list = WirelessProtocolTests.ListReply(records.Length, records.Select(record => record.ToBytes()).ToArray());
        return new FakeEnumerator(Transmitter(), Receiver()) {
            ConfigureTransport = (info, transport) => transport.ReplyFor = info.ProductId == 0x8040
                ? _ => radio.MasterReply() ?? new byte[64]
                : _ => list,
        };
    }

    private static FakeWirelessRecord Device(int type, byte[] fanTypes, int[] rpm, int fans)
        => new FakeWirelessRecord(new byte[] { 0xA0, 0, 0, 0, 0, 1 }, FakeWirelessRig.MasterMac) {
            DeviceType = (byte)type,
            FanCountByte = (byte)fans,
            FanTypes = fanTypes,
            Rpm = rpm,
        };

    [Fact]
    public void EveryHubChannel_IsPairedWithItsOwnRpm() {
        using LianLiPlugin plugin = NewPlugin(new FakeEnumerator(Sli("a"), Sli("b")));

        var expected = new Dictionary<string, string?>();
        for (int hub = 0; hub < 2; hub++) {
            for (int ch = 0; ch < 4; ch++) {
                string channel = "LianLi/" + hub + "/ch" + ch;
                expected[channel + "/ctl"] = ExpectedName + "/" + channel + "/fan";
            }
        }

        Assert.Equal(expected, Pairings(plugin));
        plugin.Close();
    }

    [Fact]
    public void AHiddenChannel_LeavesTheShownOnesPairedWithTheirOwnChannel() {
        var rpm = new byte[65];
        rpm[1] = 0x05; rpm[2] = 0xDC; // ch0 -> 1500 rpm
        rpm[5] = 0x05; rpm[6] = 0xDC; // ch2 -> 1500 rpm
        using LianLiPlugin plugin = NewPlugin(new FakeEnumerator(Sli("a")) { ConfigureTransport = (_, t) => t.InputReport = rpm });

        Assert.Equal(
            new Dictionary<string, string?> {
                ["LianLi/0/ch0/ctl"] = ExpectedName + "/LianLi/0/ch0/fan",
                ["LianLi/0/ch2/ctl"] = ExpectedName + "/LianLi/0/ch2/fan",
            },
            Pairings(plugin));
        plugin.Close();
    }

    [Fact]
    public void AGalahad_PairsItsFanAndItsPumpEachWithItsOwnRpm() {
        using LianLiPlugin plugin = NewPlugin(new FakeEnumerator(new LocatedDevice(0x0416, 0x7371, "fake/galahad", null)) {
            ConfigureTransport = (_, transport) => transport.ReplyFor = _ => CommandPacket.Build(0x81, 0x04, 0xB0, 0x0A, 0xF0),
        });

        Assert.Equal(
            new Dictionary<string, string?> {
                ["LianLi/0/ch0/ctl"] = ExpectedName + "/LianLi/0/ch0/fan",
                ["LianLi/0/ch1/ctl"] = ExpectedName + "/LianLi/0/ch1/fan",
            },
            Pairings(plugin));
        plugin.Close();
    }

    [Fact]
    public void AHydroShiftCurve_PairsThePumpWithThePumpRpm() {
        using LianLiPlugin plugin = NewPlugin(new FakeEnumerator(new LocatedDevice(0x0416, 0x8051, "fake/hydroshift-curve", null)));

        Assert.Equal(
            new Dictionary<string, string?> { ["LianLi/0/ch0/ctl"] = ExpectedName + "/LianLi/0/ch0/fan" },
            Pairings(plugin));
        plugin.Close();
    }

    [Fact]
    public void ATlHub_PairsEveryFanWithItsOwnRpm() {
        // One TL fan on port 0, fan 0, at 1000 rpm, on every handshake.
        using LianLiPlugin plugin = NewPlugin(new FakeEnumerator(new LocatedDevice(0x0416, 0x7372, "fake/tl", null)) {
            ConfigureTransport = (_, transport) => transport.ReplyFor = _ => CommandPacket.Build(0xA1, 0x80, 0x03, 0xE8),
        });

        Assert.Equal(
            new Dictionary<string, string?> { ["LianLi/0/p0f0/ctl"] = ExpectedName + "/LianLi/0/p0f0/fan" },
            Pairings(plugin));
        plugin.Close();
    }

    [Fact]
    public void AFlexChainOnItsUsbReceiver_IsPairedWithItsFirstFan() {
        byte[] status = FlexReceiverProtocolTests.StatusReply(new FakeWirelessRecord(new byte[] { 0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6 }, new byte[6]) {
            FanCountByte = 2,
            FanTypes = new byte[] { 51, 51, 0, 0 },
            Rpm = new[] { 1200, 1150, 0, 0 },
        });
        using LianLiPlugin plugin = NewPlugin(new FakeEnumerator(new LocatedDevice(0x43A8, 0x0101, "fake/flex", null)) {
            ConfigureTransport = (_, transport) => transport.ReplyFor = written => written[0] == 0x12 ? status : new byte[] { 0x13, 0 },
        });
        FakeSensorsContainer container = Load(plugin);

        Assert.Equal(
            new Dictionary<string, string?> { ["LianLi/wa1b2c3d4e5f6/ctl"] = ExpectedName + "/LianLi/wa1b2c3d4e5f6/f0/fan" },
            Pairings(plugin, container));
        Assert.Equal(2, container.FanSensors.Count); // the second fan keeps a reading of its own
        plugin.Close();
    }

    [Fact]
    public void AWirelessGroup_IsPairedWithItsFirstFan() {
        using LianLiPlugin plugin = NewPlugin(Radio(Device(0, new byte[] { 36, 36, 0, 0 }, new[] { 1000, 1100, 0, 0 }, 2)));

        Assert.Equal(
            new Dictionary<string, string?> { [Wireless + "/ctl"] = ExpectedName + "/" + Wireless + "/f0/fan" },
            Pairings(plugin));
        plugin.Close();
    }

    [Fact]
    public void AWirelessWaterBlock_PairsItsFansWithTheFirstFan_AndItsPumpWithThePumpRpm() {
        using LianLiPlugin plugin = NewPlugin(Radio(Device(10, new byte[] { 41, 41, 41, 31 }, new[] { 900, 910, 920, 2400 }, 4)));

        Assert.Equal(
            new Dictionary<string, string?> {
                [Wireless + "/ctl"] = ExpectedName + "/" + Wireless + "/f0/fan",
                [Wireless + "/pump/ctl"] = ExpectedName + "/" + Wireless + "/pump/fan",
            },
            Pairings(plugin));
        plugin.Close();
    }

    // The front follows L-Connect's fans_type[0] == 1 ? Speeds[0] : Speeds[1]: the first front fan
    // when it is fitted, whether or not the second is, and the second otherwise.
    [Theory]
    [InlineData(new byte[] { 1, 1, 1, 0 }, "f0")]
    [InlineData(new byte[] { 1, 0, 1, 0 }, "f0")]
    [InlineData(new byte[] { 0, 1, 1, 0 }, "f1")]
    public void ALancool217_PairsTheFrontWithTheFirstFittedFan_AndTheRearWithItsOwn(byte[] fanTypes, string front) {
        using LianLiPlugin plugin = NewPlugin(Radio(Device(65, fanTypes, new[] { 710, 720, 730, 0 }, 3)));

        Assert.Equal(
            new Dictionary<string, string?> {
                [Wireless + "/front/ctl"] = ExpectedName + "/" + Wireless + "/" + front + "/fan",
                [Wireless + "/rear/ctl"] = ExpectedName + "/" + Wireless + "/f2/fan",
            },
            Pairings(plugin));
        plugin.Close();
    }

    [Theory]
    [InlineData(new byte[] { 1, 1, 1, 0 }, "f0")]
    [InlineData(new byte[] { 1, 0, 1, 0 }, "f0")]
    [InlineData(new byte[] { 0, 1, 1, 0 }, "f1")]
    public void AV150_PairsTheFrontWithTheFittedFrontFan_AndTheRearWithItsOwn(byte[] fanTypes, string front) {
        using LianLiPlugin plugin = NewPlugin(Radio(Device(66, fanTypes, new[] { 710, 720, 730, 0 }, 3)));

        Assert.Equal(
            new Dictionary<string, string?> {
                [Wireless + "/ctl"] = ExpectedName + "/" + Wireless + "/" + front + "/fan",
                [Wireless + "/rear/ctl"] = ExpectedName + "/" + Wireless + "/f2/fan",
            },
            Pairings(plugin));
        plugin.Close();
    }

    // The V150's front control exists before any front fan does, and FanControl must not be given
    // a reading that is not registered.
    [Fact]
    public void AV150WithNoFans_HasAFrontControlWithNoPairing() {
        var logger = new FakeLogger();
        using LianLiPlugin plugin = NewPlugin(Radio(Device(66, new byte[4], new int[4], 0)), logger: logger);
        FakeSensorsContainer container = Load(plugin);

        Assert.Equal(new Dictionary<string, string?> { [Wireless + "/ctl"] = null }, Pairings(plugin, container));
        Assert.Empty(container.FanSensors);
        Assert.Contains("  " + Wireless + "/ctl has no fan speed registered, so FanControl is given no pairing for it", logger.Messages);
        plugin.Close();
    }

    [Fact]
    public void AStandIn_PairsItsControlsExactlyAsTheControllerDid() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        var enumerator = new FakeEnumerator(Sli("a"));
        using LianLiPlugin first = NewPlugin(enumerator, runtime);
        Dictionary<string, string?> before = Pairings(first);
        first.Close();

        // FanControl refreshes with a fresh plugin object, and this scan cannot finish.
        enumerator.FailLocate = true;
        using LianLiPlugin second = NewPlugin(enumerator, runtime);
        Dictionary<string, string?> after = Pairings(second);

        Assert.Equal(4, before.Count);
        Assert.Equal(before, after);
        second.Close();
    }

    // The wireless pair and a FLEX receiver are stood in for like a hub, and pair the same way.
    [Fact]
    public void AStoodInWirelessPair_PairsItsControlsExactlyAsTheControllerDid() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        FakeEnumerator enumerator = Radio(Device(10, new byte[] { 41, 41, 41, 31 }, new[] { 900, 910, 920, 2400 }, 4));
        using LianLiPlugin first = NewPlugin(enumerator, runtime);
        Dictionary<string, string?> before = Pairings(first);
        first.Close();

        enumerator.FailLocate = true;
        using LianLiPlugin second = NewPlugin(enumerator, runtime);
        Dictionary<string, string?> after = Pairings(second);

        Assert.Equal(
            new Dictionary<string, string?> {
                [Wireless + "/ctl"] = ExpectedName + "/" + Wireless + "/f0/fan",
                [Wireless + "/pump/ctl"] = ExpectedName + "/" + Wireless + "/pump/fan",
            },
            after);
        Assert.Equal(before, after);
        second.Close();
    }

    [Fact]
    public void AStoodInFlexReceiver_PairsItsChainWithItsFirstFan() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        byte[] status = FlexReceiverProtocolTests.StatusReply(new FakeWirelessRecord(new byte[] { 0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6 }, new byte[6]) {
            FanCountByte = 2,
            FanTypes = new byte[] { 51, 51, 0, 0 },
            Rpm = new[] { 1200, 1150, 0, 0 },
        });
        var enumerator = new FakeEnumerator(new LocatedDevice(0x43A8, 0x0101, "fake/flex", null)) {
            ConfigureTransport = (_, transport) => transport.ReplyFor = written => written[0] == 0x12 ? status : new byte[] { 0x13, 0 },
        };
        using LianLiPlugin first = NewPlugin(enumerator, runtime);
        Dictionary<string, string?> before = Pairings(first);
        first.Close();

        enumerator.FailLocate = true;
        using LianLiPlugin second = NewPlugin(enumerator, runtime);
        Dictionary<string, string?> after = Pairings(second);

        Assert.Equal(
            new Dictionary<string, string?> { ["LianLi/wa1b2c3d4e5f6/ctl"] = ExpectedName + "/LianLi/wa1b2c3d4e5f6/f0/fan" },
            after);
        Assert.Equal(before, after);
        second.Close();
    }

    // A FLEX chain's control remembered on its USB receiver (index 0) and its first fan's reading on
    // the wireless pair (index 1): the reading is registered after the control, and the control is
    // still paired with it, because the pairings are decided once the whole pass is registered.
    [Fact]
    public void AControlWhoseReadingALaterControllerRegisters_IsStillPaired() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        _ = runtime.Remember("old/flex", RememberedFixture.Of(
            new ControllerPlan(DeviceKind.FlexReceiver, new LocatedDevice(0x43A8, 0x0101, "old/flex", null)),
            0,
            new[] { new ChannelDescriptor(Wireless + "/ctl", "Chain", Wireless + "/f0/fan", "Chain Fan 1 RPM") },
            Array.Empty<FanSpeedDescriptor>(),
            Array.Empty<TemperatureDescriptor>(),
            new FakeClock().UtcNow));
        _ = runtime.Remember("old/tx", RememberedFixture.Of(
            new ControllerPlan(
                DeviceKind.WirelessTransmitter,
                new LocatedDevice(0x0416, 0x8040, "old/tx", null),
                new LocatedDevice(0x0416, 0x8041, "old/rx", null)),
            1,
            Array.Empty<ChannelDescriptor>(),
            new[] { new FanSpeedDescriptor(Wireless + "/f0/fan", "Chain Fan 1 RPM") },
            Array.Empty<TemperatureDescriptor>(),
            new FakeClock().UtcNow));
        using LianLiPlugin plugin = NewPlugin(new FakeEnumerator(), runtime);
        FakeSensorsContainer container = Load(plugin);

        Assert.Equal(
            new Dictionary<string, string?> { [Wireless + "/ctl"] = ExpectedName + "/" + Wireless + "/f0/fan" },
            Pairings(plugin, container));
        Assert.Equal(Wireless + "/f0/fan", Assert.Single(container.FanSensors).Id);
        plugin.Close();
    }

    // FanControl may load the same plugin object again; the pairings come out the same.
    [Fact]
    public void ALoadRepeatedOnOneInstance_PairsTheSameWay() {
        using LianLiPlugin plugin = NewPlugin(new FakeEnumerator(Sli("a")));
        plugin.Initialize();
        var first = new FakeSensorsContainer();
        plugin.Load(first);
        var second = new FakeSensorsContainer();
        plugin.Load(second);

        Assert.Equal(4, first.ControlSensors.Count);
        Assert.Equal(Pairings(plugin, first), Pairings(plugin, second));
        plugin.Close();
    }
}
