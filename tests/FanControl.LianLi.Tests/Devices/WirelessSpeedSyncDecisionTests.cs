using System;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Protocol;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

/// <summary>MasterDevice.NeedSyncPwm, restricted to the slots a control drives.</summary>
public sealed class WirelessSpeedSyncDecisionTests {
    private static readonly bool[] All = { true, true, true, true };

    private static byte[] B(params int[] values) => Array.ConvertAll(values, v => (byte)v);

    private static WirelessDeviceKind Kind(string name) => Enum.Parse<WirelessDeviceKind>(name);

    // NeedSyncPwm: resend when |fans_pwm - target_fans_pwm| > 5.
    [Theory]
    [InlineData(100, 105, false)]
    [InlineData(100, 106, true)]
    [InlineData(106, 100, true)]
    [InlineData(95, 100, false)]
    public void NeedsSync_WhenAnySlotIsMoreThanFiveOff(int reported, int target, bool expected)
        => Assert.Equal(expected, WirelessSpeedSyncDecision.NeedsSync(
            WirelessDeviceKind.FanGroup, 2, B(100, 100, reported, 100), B(100, 100, target, 100), All));

    [Fact]
    public void NeedsSync_IgnoresASlotNoControlDrives()
        => Assert.False(WirelessSpeedSyncDecision.NeedsSync(
            WirelessDeviceKind.FanGroup, 2, B(0, 0, 0, 0), B(255, 255, 255, 255), new[] { false, false, false, false }));

    // NeedSyncPwm: FanNum == 0 is never synced, except LC217 and V150.
    [Theory]
    [InlineData("FanGroup", false)]
    [InlineData("WaterBlock", false)]
    [InlineData("Unrecognised", false)]
    [InlineData("CaseFans", true)]
    [InlineData("V150", true)]
    public void NeedsSync_ADeviceWithoutFansOnlyForTheCaseAndTheV150(string kind, bool expected)
        => Assert.Equal(expected, WirelessSpeedSyncDecision.NeedsSync(Kind(kind), 0, B(0, 0, 0, 0), B(200, 200, 200, 200), All));

    // NeedSyncPwm: "target_fans_pwm <= 10 && RecType != LC217 && RecType == V150" always resends.
    [Theory]
    [InlineData("V150", 10, true)]
    [InlineData("V150", 11, false)]
    [InlineData("CaseFans", 10, false)]
    [InlineData("FanGroup", 0, false)]
    public void NeedsSync_AV150AtOrBelowTenIsAlwaysResent(string kind, int pwm, bool expected)
        => Assert.Equal(expected, WirelessSpeedSyncDecision.NeedsSync(Kind(kind), 1, B(pwm, pwm, pwm, pwm), B(pwm, pwm, pwm, pwm), All));

    [Fact]
    public void NeedsSync_RejectsMissingArrays() {
        Assert.Throws<ArgumentNullException>(() => WirelessSpeedSyncDecision.NeedsSync(WirelessDeviceKind.FanGroup, 1, null!, new byte[4], All));
        Assert.Throws<ArgumentNullException>(() => WirelessSpeedSyncDecision.NeedsSync(WirelessDeviceKind.FanGroup, 1, new byte[4], null!, All));
        Assert.Throws<ArgumentNullException>(() => WirelessSpeedSyncDecision.NeedsSync(WirelessDeviceKind.FanGroup, 1, new byte[4], new byte[4], null!));
    }
}
