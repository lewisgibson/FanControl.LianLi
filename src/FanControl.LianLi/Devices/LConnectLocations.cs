using System;
using System.IO;

namespace FanControl.LianLi.Devices;

/// <summary>
/// Where L-Connect keeps each document the plugin reads, all under one data directory
/// (<c>%ProgramData%\Lian-Li\L-Connect 3</c> on a real machine). The layout is L-Connect's, not the
/// plugin's, so it is written down once here; the plugin is handed one of these so a test can point
/// every reader at a fixture directory instead of the machine's own.
/// </summary>
internal sealed class LConnectLocations {
    // How L-Connect keys the wireless settings document under its device directory.
    private const string WirelessDeviceKey = "LWireless-Controller";
    private const string WirelessPumpSettingKey = "Pump";

    /// <summary>The locations under <paramref name="dataDirectory"/>.</summary>
    public LConnectLocations(string dataDirectory) {
        DataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
    }

    /// <summary>L-Connect's own data directory on this machine.</summary>
    public static LConnectLocations Machine => new LConnectLocations(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Lian-Li", "L-Connect 3"));

    /// <summary>The root every other location sits under.</summary>
    public string DataDirectory { get; }

    /// <summary>The per-device settings: the wired controllers' saved looks and the wireless pump settings.</summary>
    public string DeviceDirectory => Path.Combine(DataDirectory, "device");

    /// <summary>The per-controller fan profiles, which carry the start/stop switch.</summary>
    public string ProfileDirectory => Path.Combine(DataDirectory, "profile");

    /// <summary>The wireless master's saved RF channel and every wireless device's rendered effect.</summary>
    public string WirelessDirectory => Path.Combine(DataDirectory, "slv3", "config");

    /// <summary>The one document holding every wireless water block's screen presentation.</summary>
    public string WirelessPumpSettingPath
        => LConnectFile.DeviceSettingPath(DeviceDirectory, WirelessDeviceKey, WirelessPumpSettingKey);
}
