using System;

namespace FanControl.LianLi.Devices;

/// <summary>
/// One master dongle the receiver hears - this one or another L-Wireless controller in radio range -
/// as L-Connect keeps it in <c>MasterDevice.masterList</c>: its address, the channel it reports, and
/// the same countdown every device has, so a master nobody hears any more is dropped.
/// </summary>
internal sealed class WirelessMaster {
    /// <summary>A master first heard on <paramref name="channel"/>.</summary>
    public WirelessMaster(string macText, int channel) {
        MacText = macText ?? throw new ArgumentNullException(nameof(macText));
        Channel = channel;
        Live = WirelessDevice.MaximumMissedReads;
    }

    /// <summary>The master's RF address, twelve lowercase hex digits.</summary>
    public string MacText { get; }

    /// <summary>The channel the master's record reports, updated on every read that carries it.</summary>
    public int Channel { get; set; }

    /// <summary>L-Connect's <c>live</c>: reads left before a master nobody hears is dropped.</summary>
    public int Live { get; set; }

    /// <summary>
    /// L-Connect's <c>master_channel_shouldbe</c>: the channel the conflict check last moved this
    /// master to, so it is moved once rather than on every read until the move shows.
    /// </summary>
    public int? ChannelShouldBe { get; set; }
}
