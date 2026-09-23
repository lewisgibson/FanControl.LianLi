namespace FanControl.LianLi.Transport;

/// <summary>
/// What <c>HidP_GetCaps</c> reports about a HID interface's top-level collection that the plugin uses:
/// its usage page, which picks the 0x0416 family's command interface (see
/// <see cref="CommandInterfaceFilter"/>), and its report lengths, which size every transfer. Each length
/// counts the report id byte, as the HID class driver requires of a transfer buffer.
/// </summary>
internal readonly struct HidCapabilities {
    public HidCapabilities(int usagePage, int inputReportLength, int outputReportLength, int featureReportLength) {
        UsagePage = usagePage;
        InputReportLength = inputReportLength;
        OutputReportLength = outputReportLength;
        FeatureReportLength = featureReportLength;
    }

    /// <summary>The top-level collection's usage page.</summary>
    public int UsagePage { get; }

    /// <summary>Length of the longest input report; a read buffer must hold at least this many bytes.</summary>
    public int InputReportLength { get; }

    /// <summary>Length of the longest output report; every write is exactly this many bytes.</summary>
    public int OutputReportLength { get; }

    /// <summary>Length of the longest feature report; <c>HidD_SetFeature</c> refuses a shorter buffer.</summary>
    public int FeatureReportLength { get; }
}
