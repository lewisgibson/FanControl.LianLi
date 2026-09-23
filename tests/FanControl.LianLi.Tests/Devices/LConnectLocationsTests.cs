using System;
using System.IO;
using FanControl.LianLi.Devices;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

/// <summary>
/// Where L-Connect keeps each document the plugin reads. The layout is L-Connect's own, so each
/// path is pinned exactly: a slip here reads nothing, silently.
/// </summary>
public class LConnectLocationsTests {
    private static readonly string Root = Path.Combine("data", "L-Connect 3");

    [Fact]
    public void EveryLocationSitsUnderTheDataDirectory() {
        var locations = new LConnectLocations(Root);

        Assert.Equal(Root, locations.DataDirectory);
        Assert.Equal(Path.Combine(Root, "device"), locations.DeviceDirectory);
        Assert.Equal(Path.Combine(Root, "profile"), locations.ProfileDirectory);
        Assert.Equal(Path.Combine(Root, "slv3", "config"), locations.WirelessDirectory);
    }

    [Fact]
    public void WirelessPumpSettingsAreNamedByLConnectsOwnHashes() {
        // md5("lwireless-controller") and md5("pump"), with L-Connect's fixed ".0" extension.
        Assert.Equal(
            Path.Combine(Root, "device", "137b3f244d3c5d1568121556b5b076e5", "cf82720db122ae41719df5b05503b749.0"),
            new LConnectLocations(Root).WirelessPumpSettingPath);
    }

    [Fact]
    public void MachineLocationIsLConnectsProgramDataFolder() {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Lian-Li", "L-Connect 3");

        Assert.Equal(expected, LConnectLocations.Machine.DataDirectory);
    }

    [Fact]
    public void NullDataDirectoryThrows()
        => Assert.Throws<ArgumentNullException>(() => new LConnectLocations(null!));
}
