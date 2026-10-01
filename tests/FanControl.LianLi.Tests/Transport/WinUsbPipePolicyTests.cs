using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

/// <summary>
/// The pipe timings L-Connect gives each WinUSB device: the dongles' (WinUsb.RfSend and ReadAll),
/// the pump MCU's (lcd207's WinUsbHS2.SendAndReadLed and WinUsb.Read) and a FLEX receiver's
/// (WinUsbLed.SendAndRead and Led_Read), and which device gets which.
/// </summary>
public class WinUsbPipePolicyTests {
    [Fact]
    public void TheDongles_Get100msWrites_50msPackets_AndAReplyReadToItsEnd() {
        WinUsbPipePolicy policy = WinUsbPipePolicy.Dongle;

        Assert.Equal(100u, policy.WriteTimeoutMilliseconds);
        Assert.Equal(50u, policy.ReadTimeoutMilliseconds);
        Assert.True(policy.ReadsWholeReply);
    }

    [Fact]
    public void ThePumpMcu_Gets200msWrites_200msForItsOnePacket_AndNoDrain() {
        WinUsbPipePolicy policy = WinUsbPipePolicy.PumpMcu;

        Assert.Equal(200u, policy.WriteTimeoutMilliseconds);
        Assert.Equal(200u, policy.ReadTimeoutMilliseconds);
        Assert.False(policy.ReadsWholeReply);
    }

    [Fact]
    public void AFlexReceiver_Gets2000msWrites_100msForItsOnePacket_AndNoDrain() {
        WinUsbPipePolicy policy = WinUsbPipePolicy.FlexReceiver;

        Assert.Equal(2000u, policy.WriteTimeoutMilliseconds);
        Assert.Equal(100u, policy.ReadTimeoutMilliseconds);
        Assert.False(policy.ReadsWholeReply);
    }

    [Theory]
    [InlineData(0x0416, 0x8040, "Dongle")]
    [InlineData(0x0416, 0x8041, "Dongle")]
    [InlineData(0x1A86, 0xE304, "Dongle")]
    [InlineData(0x0416, 0x8051, "PumpMcu")]
    [InlineData(0x43A8, 0x0101, "FlexReceiver")]
    [InlineData(0x43A8, 0x0102, "FlexReceiver")]
    [InlineData(0x43A8, 0x0104, "FlexReceiver")]
    [InlineData(0x43A8, 0x0105, "FlexReceiver")]
    [InlineData(0x43A8, 0x0103, "Dongle")]
    public void For_PicksThePolicyOfTheDevice_AndTheDonglesForTheRest(int vendorId, int productId, string expected) {
        WinUsbPipePolicy policy = expected == "PumpMcu" ? WinUsbPipePolicy.PumpMcu
            : expected == "FlexReceiver" ? WinUsbPipePolicy.FlexReceiver
            : WinUsbPipePolicy.Dongle;
        Assert.Same(policy, WinUsbPipePolicy.For(vendorId, productId));
    }
}
