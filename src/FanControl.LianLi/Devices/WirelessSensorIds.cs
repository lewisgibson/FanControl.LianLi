using System;
using System.Globalization;

namespace FanControl.LianLi.Devices;

/// <summary>
/// The sensor ids of a device keyed on its RF address, which the wireless controller and a FLEX
/// receiver give the same chain: <c>LianLi/w{address}/ctl</c> for a fan group's one control and
/// <c>LianLi/w{address}/f{slot}/fan</c> for each fan's reading, the address as twelve lowercase hex
/// digits. Kept in one place so the two controllers cannot drift apart, and so the registered
/// control can tell which chain it drives (<see cref="TryChainAddress"/>).
/// </summary>
internal static class WirelessSensorIds {
    private const string Prefix = "LianLi/w";
    private const string GroupControlSuffix = "/ctl";
    private const int AddressLength = 12;

    /// <summary>The id of the one control of the fan group at <paramref name="macText"/>.</summary>
    public static string GroupControlId(string macText) => Prefix + macText + GroupControlSuffix;

    /// <summary>The id of the reading of fan <paramref name="slot"/> of the group at <paramref name="macText"/>.</summary>
    public static string FanId(string macText, int slot)
        => Prefix + macText + "/f" + slot.ToString(CultureInfo.InvariantCulture) + "/fan";

    /// <summary>
    /// The address of the fan group whose one control <paramref name="controlId"/> is, or null for
    /// any other id: a wired channel's, a pump's, or a case fan control named for its part.
    /// </summary>
    public static string? TryChainAddress(string controlId) {
        if (controlId is null) {
            throw new ArgumentNullException(nameof(controlId));
        }

        if (controlId.Length != Prefix.Length + AddressLength + GroupControlSuffix.Length
            || !controlId.StartsWith(Prefix, StringComparison.Ordinal)
            || !controlId.EndsWith(GroupControlSuffix, StringComparison.Ordinal)) {
            return null;
        }

        string address = controlId.Substring(Prefix.Length, AddressLength);
        foreach (char digit in address) {
            if (!((digit >= '0' && digit <= '9') || (digit >= 'a' && digit <= 'f'))) {
                return null;
            }
        }

        return address;
    }
}
