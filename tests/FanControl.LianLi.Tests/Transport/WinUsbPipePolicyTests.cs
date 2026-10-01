using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

/// <summary>
/// The pipe timings L-Connect gives each WinUSB device: the dongles' (WinUsb.RfSend and ReadAll)
/// and the pump MCU's (lcd207's WinUsbHS2.SendAndReadLed and WinUsb.Read), and which device gets
/// which.
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

    [Theory]
    [InlineData(0x0416, 0x8040, "Dongle")]
    [InlineData(0x0416, 0x8041, "Dongle")]
    [InlineData(0x1A86, 0xE304, "Dongle")]
    [InlineData(0x0416, 0x8051, "PumpMcu")]
    public void For_PicksThePolicyOfTheDevice_AndTheDonglesForTheRest(int vendorId, int productId, string expected) {
        WinUsbPipePolicy policy = expected == "PumpMcu" ? WinUsbPipePolicy.PumpMcu : WinUsbPipePolicy.Dongle;
        Assert.Same(policy, WinUsbPipePolicy.For(vendorId, productId));
    }
}
