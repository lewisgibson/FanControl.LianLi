using System;
using System.Globalization;
using System.IO;

namespace FanControl.LianLi.Devices;

/// <summary>
/// Reads the RF channel L-Connect saved for one L-Wireless master, the way
/// <c>RFController.GetChannel</c> reads it: the file <c>&lt;master address&gt;.config</c> in
/// L-Connect's <c>slv3\config</c> directory, named for the master's address as twelve lowercase hex
/// digits, holding the channel as a decimal number. L-Connect learns the master's address from the
/// transmitter first (<c>RFController.Init</c> queries the master, then reads the file), so the file
/// is looked up by that address and a file left by another master is never used.
/// </summary>
internal static class WirelessChannelConfigurationReader {
    /// <summary>
    /// The saved channel (1-255) for the master at <paramref name="masterMacText"/>, or null when
    /// there is no file, it is empty, or it holds 0 - which <c>RFController.Init</c> treats as "not
    /// set". Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the
    /// file cannot be read, and <see cref="FormatException"/> or <see cref="OverflowException"/> when it
    /// does not hold a byte, as <c>Convert.ToByte</c> does in L-Connect.
    /// </summary>
    public static int? Read(string directory, string masterMacText) {
        if (directory is null) {
            throw new ArgumentNullException(nameof(directory));
        }

        if (masterMacText is null) {
            throw new ArgumentNullException(nameof(masterMacText));
        }

        string path = Path.Combine(directory, masterMacText + ".config");
        if (!File.Exists(path)) {
            return null;
        }

        string text = File.ReadAllText(path);
        if (text.Length == 0) {
            return null;
        }

        byte channel = byte.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
        return channel == 0 ? (int?)null : channel;
    }
}
