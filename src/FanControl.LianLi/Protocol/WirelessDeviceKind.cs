namespace FanControl.LianLi.Protocol;

/// <summary>
/// What a wireless device is, decoded from record byte 18 exactly as L-Connect's
/// <c>RfDevice.InitAttr</c> maps it onto <c>DevTypes</c>. The kind decides what the plugin drives:
/// L-Connect's service gives every bound device that is not a Strimer, a water block or a Lancool
/// 217 an ordinary fan configuration (<c>LWirelessController.addSettingDevice</c>, default branch),
/// so a fan group, the V150 and a device type L-Connect has no name for are all driven the same way.
/// </summary>
internal enum WirelessDeviceKind {
    /// <summary>Type 0: a UNI FAN group of one to four fans.</summary>
    FanGroup,

    /// <summary>Types 1-9: a Strimer light strip. Lighting only.</summary>
    Strimer,

    /// <summary>Types 10 and 11: a HydroShift II water block, with fans, a pump and a coolant sensor.</summary>
    WaterBlock,

    /// <summary>Type 65: the Lancool 217 Infinity case fans, a front pair and a rear fan.</summary>
    CaseFans,

    /// <summary>Type 66: the V150, which L-Connect drives as one fan device.</summary>
    V150,

    /// <summary>Type 255: another master dongle's own record. Never driven.</summary>
    Master,

    /// <summary>Any other type. L-Connect leaves it as <c>DevTypes.ALL</c> and still drives it as a fan device.</summary>
    Unrecognised,
}
