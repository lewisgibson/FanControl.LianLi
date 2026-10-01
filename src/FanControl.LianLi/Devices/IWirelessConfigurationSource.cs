using System.Collections.Generic;
using FanControl.LianLi.Protocol;

namespace FanControl.LianLi.Devices;

/// <summary>
/// What L-Connect saved about a wireless master and the devices bound to it, looked up by RF
/// address: the master's RF channel, the lighting effect to replay to a device, whether a device's
/// lighting was handed to the motherboard, how a water block's screen is set up, and how an LCD
/// FLEX group's screens are. The plugin reads L-Connect's files instead of deriving any of these
/// - it can neither render an effect nor invent a screen theme - and anything missing, or saved in
/// a file that cannot be read, is absent so the caller falls back to L-Connect's own
/// default. No member throws for a bad file.
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
    /// Whether L-Connect's per-device "sync to motherboard" switch is saved on for the device at
    /// <paramref name="macText"/> (its <c>MotherboardARGBSync</c> setting): the device's lighting
    /// then follows the motherboard's ARGB header, while its saved effect is still streamed to it
    /// as L-Connect streams it. Always false in the builds that do not drive lighting, and when
    /// nothing is saved.
    /// </summary>
    bool FindMotherboardArgbSync(string macText);

    /// <summary>
    /// The screen presentation saved for the water block at <paramref name="macText"/>, or null when
    /// there is none and <see cref="WirelessAioPresentation.Default"/> should stand in.
    /// </summary>
    WirelessAioPresentation? FindPumpPresentation(string macText);

    /// <summary>
    /// The screen settings saved for the LCD FLEX fan group at <paramref name="macText"/>
    /// (<c>LWirelessLCDConfig</c>: each screen's theme, data source and the colours of its theme,
    /// the brightness, the direction and the mode), or null when there are none and
    /// <see cref="WirelessFanScreenPresentation.Default"/> should stand in.
    /// </summary>
    WirelessFanScreenPresentation? FindFanScreenPresentation(string macText);

    /// <summary>
    /// L-Connect's locked device list for the master at <paramref name="masterMacText"/>
    /// (<c>RFController.CheckLockAndInitData</c>), in its order, or null when the list is not locked
    /// - no list saved, an empty one, or one saved for another master.
    /// </summary>
    IReadOnlyList<WirelessLockedDevice>? FindLockedDevices(string masterMacText);
}
