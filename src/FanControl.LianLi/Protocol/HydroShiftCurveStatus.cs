namespace FanControl.LianLi.Protocol;

/// <summary>
/// What the HydroShift II OLED Curve's pump MCU reports in its status reply: the liquid
/// temperature and whether the pump is following the motherboard's PWM header. Produced by
/// <see cref="HydroShiftCurveProtocol.DecodeStatus"/>.
/// </summary>
internal readonly struct HydroShiftCurveStatus {
    /// <summary>Create a status with the given temperature and sync state.</summary>
    public HydroShiftCurveStatus(int liquidTemperature, bool followsMotherboard) {
        LiquidTemperature = liquidTemperature;
        FollowsMotherboard = followsMotherboard;
    }

    /// <summary>The liquid temperature in whole degrees Celsius.</summary>
    public int LiquidTemperature { get; }

    /// <summary>Whether the pump follows the motherboard's PWM header rather than the speed it was sent.</summary>
    public bool FollowsMotherboard { get; }
}
