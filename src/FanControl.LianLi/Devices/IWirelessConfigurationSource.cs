using System.Collections.Generic;
using FanControl.LianLi.Protocol;

namespace FanControl.LianLi.Devices;

/// <summary>
/// What L-Connect saved about a wireless master and the devices bound to it, looked up by RF
/// address: the master's RF channel, the lighting effect to replay to a device, and how a water
/// block's screen is set up. The plugin reads L-Connect's files rather than deriving any of these -
/// it can neither render an effect nor invent a screen theme - and anything missing, or saved in a
/// file that cannot be read, is simply absent so the caller falls back to L-Connect's own default.
/// No member throws for a bad file.
/// </summary>
internal interface IWirelessConfigurationSource {
    /// <summary>
    /// The RF channel L-Connect saved for the master at <paramref name="masterMacText"/>
    /// (<c>RFController.GetChannel</c>), or null when it saved none and the master stays on the
    /// default channel.
    /// </summary>
    int? FindChannel(string masterMacText);

    /// <summary>
    /// The lighting effect saved for the device at <paramref name="macText"/>, or null when there is
    /// none. Always null in the builds that do not drive lighting.
    /// </summary>
    WirelessSavedEffect? FindEffect(string macText);

    /// <summary>
    /// The screen presentation saved for the water block at <paramref name="macText"/>, or null when
    /// there is none and <see cref="WirelessAioPresentation.Default"/> should stand in.
    /// </summary>
    WirelessAioPresentation? FindPumpPresentation(string macText);

    /// <summary>
    /// L-Connect's locked device list for the master at <paramref name="masterMacText"/>
    /// (<c>RFController.CheckLockAndInitData</c>), in its order, or null when the list is not locked
    /// - no list saved, an empty one, or one saved for another master.
    /// </summary>
    IReadOnlyList<WirelessLockedDevice>? FindLockedDevices(string masterMacText);
}
