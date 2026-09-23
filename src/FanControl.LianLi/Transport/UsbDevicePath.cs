using System;
using System.Collections.Generic;
using System.Globalization;

namespace FanControl.LianLi.Transport;

/// <summary>
/// The string handling around Windows device identifiers, kept pure so it is tested without a
/// device: reading the vendor and product id out of a device instance id or an interface path, and
/// splitting the null-terminated lists the configuration manager and the registry return.
/// </summary>
internal static class UsbDevicePath {
    // A device instance id reads "USB\VID_0416&PID_8040\6&1b2c3d4e&0&2" and an interface path
    // "\\?\usb#vid_0416&pid_8040#6&1b2c3d4e&0&2#{guid}". Either way the ids are the four hex digits
    // after these markers, in either case.
    private const string VendorMarker = "vid_";
    private const string ProductMarker = "pid_";
    private const int IdDigits = 4;

    /// <summary>
    /// Read the vendor and product id out of a device instance id or device interface path.
    /// Returns false when either is absent or not four hex digits.
    /// </summary>
    public static bool TryParseIds(string path, out int vendorId, out int productId) {
        if (path is null) {
            throw new ArgumentNullException(nameof(path));
        }

        productId = 0;
        return TryParseHexAfter(path, VendorMarker, out vendorId)
            && TryParseHexAfter(path, ProductMarker, out productId);
    }

    /// <summary>
    /// Split a buffer of null-terminated strings - the shape of both a configuration-manager list
    /// and a <c>REG_MULTI_SZ</c> value - into its entries. An empty entry ends the list, and a
    /// buffer with no terminator at all still yields what it holds.
    /// </summary>
    public static IReadOnlyList<string> SplitNullTerminatedList(char[] buffer, int length) {
        if (buffer is null) {
            throw new ArgumentNullException(nameof(buffer));
        }

        var entries = new List<string>();
        int end = Math.Min(length, buffer.Length);
        int start = 0;
        for (int i = 0; i < end; i++) {
            if (buffer[i] != '\0') {
                continue;
            }

            if (i == start) {
                return entries;
            }

            entries.Add(new string(buffer, start, i - start));
            start = i + 1;
        }

        if (start < end) {
            entries.Add(new string(buffer, start, end - start));
        }

        return entries;
    }

    private static bool TryParseHexAfter(string path, string marker, out int value) {
        value = 0;
        int at = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0 || at + marker.Length + IdDigits > path.Length) {
            return false;
        }

        return int.TryParse(
            path.Substring(at + marker.Length, IdDigits),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out value);
    }
}
