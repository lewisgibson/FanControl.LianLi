using System;
using System.Collections.Generic;

namespace FanControl.LianLi.Protocol;

/// <summary>
/// One decoded answer to the receiver's device-list request (see
/// <see cref="WirelessProtocol.DecodeDeviceList"/>): how many devices the receiver says it can hear,
/// and the records the requested pages carried.
/// </summary>
internal sealed class WirelessDeviceList {
    /// <summary>Create a list.</summary>
    public WirelessDeviceList(int total, IReadOnlyList<WirelessDeviceRecord> records) {
        Total = total;
        Records = records ?? throw new ArgumentNullException(nameof(records));
    }

    /// <summary>
    /// The count in byte 1: every device the receiver hears, which can exceed what the requested
    /// pages carried. L-Connect sizes its next request from it.
    /// </summary>
    public int Total { get; }

    /// <summary>The records with a valid end marker, in the receiver's order.</summary>
    public IReadOnlyList<WirelessDeviceRecord> Records { get; }
}
