namespace FanControl.LianLi.Devices;

/// <summary>
/// How a round of a <see cref="WirelessDeviceCommand"/> ended, as <c>MasterDevice.SyncControlInfo</c>
/// ends one: the device reported the sequence the round carried, or the sends ran out first.
/// </summary>
internal enum WirelessRoundEnd {
    /// <summary>The device's record reported the round's sequence.</summary>
    Acknowledged,

    /// <summary>The round's sends were all made and the record never reported its sequence.</summary>
    Exhausted,
}
