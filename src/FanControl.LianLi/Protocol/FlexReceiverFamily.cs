namespace FanControl.LianLi.Protocol;

/// <summary>
/// Which product a USB receiver (vendor 0x43A8) is, decoded from its product id exactly as
/// L-Connect's <c>WinUsbLed.FanTypes</c> names it. The family fixes the duty floor and idle duty
/// L-Connect's service applies before a speed write and the name the plugin shows for the chain.
/// Only the families L-Connect gives a wired fan controller are listed: the plain SL-INF FLEX
/// (0x0103), SL FLEX (0x0106) and CL FLEX (0x0107) receivers speak the same commands but no
/// L-Connect controller ever sends them a speed over USB, so the plugin does not either.
/// </summary>
internal enum FlexReceiverFamily {
    /// <summary>UNI FAN TL FLEX, pid 0x0101 (<c>TLV3LEDRec</c>).</summary>
    TlFlex,

    /// <summary>The receiver half of UNI FAN TL FLEX LCD, pid 0x0102 (<c>TLV3LCDRec</c>).</summary>
    TlFlexLcd,

    /// <summary>The receiver half of UNI FAN SL-INF FLEX LCD, pid 0x0104 (<c>INFV3LCDRec</c>).</summary>
    SlInfinityFlexLcd,

    /// <summary>UNI FAN P28 V2, pid 0x0105 (<c>P28V2LEDRec</c>).</summary>
    P28V2,
}
