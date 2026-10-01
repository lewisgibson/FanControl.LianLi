using System;
using System.IO;
using System.Text;

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
    private const string WirelessFanScreenSettingKey = "WirelessLCD";

    /// <summary>The locations under <paramref name="dataDirectory"/>.</summary>
    public LConnectLocations(string dataDirectory) {
        DataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
    }

    /// <summary>L-Connect's own data directory on this machine.</summary>
    public static LConnectLocations Machine => new LConnectLocations(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Lian-Li", "L-Connect 3"));

    /// <summary>The root every other location sits under.</summary>
    public string DataDirectory { get; }

    /// <summary>The per-device settings: the wired controllers' saved looks and the wireless pump and fan screen settings.</summary>
    public string DeviceDirectory => Path.Combine(DataDirectory, "device");

    /// <summary>The per-controller fan profiles, which carry the start/stop switch.</summary>
    public string ProfileDirectory => Path.Combine(DataDirectory, "profile");

    /// <summary>The wireless master's saved RF channel and every wireless device's rendered effect.</summary>
    public string WirelessDirectory => Path.Combine(DataDirectory, "slv3", "config");

    /// <summary>The one document holding every wireless water block's screen presentation.</summary>
    public string WirelessPumpSettingPath
        => LConnectFile.DeviceSettingPath(DeviceDirectory, WirelessDeviceKey, WirelessPumpSettingKey);

    /// <summary>The one document holding every wireless LCD FLEX fan group's screen settings (L-Connect's <c>WirelessLCD</c> setting).</summary>
    public string WirelessFanScreenSettingPath
        => LConnectFile.DeviceSettingPath(DeviceDirectory, WirelessDeviceKey, WirelessFanScreenSettingKey);

    /// <summary>
    /// A setting L-Connect saves per wireless device under <paramref name="deviceDirectory"/>, keyed
    /// by the device's RF address as its <c>RfDevice.MacStr</c> writes it - lowercase hex pairs with
    /// colons between (<c>MasterDevice.RefreshList</c>) - which <c>DeviceSettingManager.GetFilePath</c>
    /// hashes like any other device key. <paramref name="macText"/> is the address as the plugin keys
    /// it, twelve hex digits with no separators.
    /// </summary>
    public static string WirelessDeviceSettingPath(string deviceDirectory, string macText, string settingKey) {
        if (macText is null) {
            throw new ArgumentNullException(nameof(macText));
        }

        if (macText.Length != 12) {
            throw new ArgumentException("An RF address is twelve hex digits.", nameof(macText));
        }

        var withColons = new StringBuilder(17);
        for (int i = 0; i < macText.Length; i += 2) {
            if (i > 0) {
                withColons.Append(':');
            }

            withColons.Append(macText, i, 2);
        }

        return LConnectFile.DeviceSettingPath(deviceDirectory, withColons.ToString(), settingKey);
    }
}
