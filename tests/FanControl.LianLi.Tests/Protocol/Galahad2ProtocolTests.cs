using FanControl.LianLi.Protocol;
using Xunit;

namespace FanControl.LianLi.Tests.Protocol;

/// <summary>
/// Byte-level tests for the Galahad II AIO protocol: the set-fan and set-pump packets, the pump
/// safety floor that prevents a stopped pump, the motherboard-sync flag, and the big-endian
/// fan/pump RPM handshake decode.
/// </summary>
public sealed class Galahad2ProtocolTests {
    [Theory]
    [InlineData(false, 50, 0x00, 50)]
    [InlineData(true, 50, 0x01, 50)]   // motherboard sync flag
    [InlineData(false, 150, 0x00, 100)] // clamped to 100
    [InlineData(false, 0, 0x00, 0)]    // a fan may idle at 0
    public void EncodeSetFan_FramesSyncFlagAndClampedDuty(bool sync, int duty, int expectedFlag, int expectedDuty) {
        byte[] packet = Galahad2Protocol.EncodeSetFan(sync, duty);

        Assert.Equal(0x01, packet[0]);
        Assert.Equal(0x8B, packet[1]);
        Assert.Equal(0x02, packet[5]);
        Assert.Equal((byte)expectedFlag, packet[6]);
        Assert.Equal((byte)expectedDuty, packet[7]);
    }

    [Theory]
    [InlineData(0, 50)]    // 0% floored to the pump-duty floor (never stop the pump)
    [InlineData(30, 50)]   // below the floor -> floor
    [InlineData(80, 80)]   // in range -> as-is
    [InlineData(150, 100)] // above 100 -> 100
    public void EncodeSetPump_NeverDrivesBelowTheSafetyFloor(int duty, int expectedDuty) {
        byte[] packet = Galahad2Protocol.EncodeSetPump(motherboardSync: false, dutyPercent: duty);

        Assert.Equal(0x8A, packet[1]);
        Assert.Equal((byte)expectedDuty, packet[7]);
        Assert.True(packet[7] >= Galahad2Protocol.PumpDutyFloor);
    }

    [Fact]
    public void EncodeHandshakeRequest_IsTheBareCommand() {
        byte[] packet = Galahad2Protocol.EncodeHandshakeRequest();

        Assert.Equal(0x81, packet[1]);
        Assert.Equal(0x00, packet[5]);
    }

    [Fact]
    public void DecodeHandshake_ReadsBigEndianFanThenPumpRpm() {
        // payload: fan 1200 (0x04B0), pump 2800 (0x0AF0).
        byte[] reply = CommandPacket.Build(0x81, 0x04, 0xB0, 0x0A, 0xF0);

        Galahad2Reading reading = Galahad2Protocol.DecodeHandshake(reply);

        Assert.Equal(1200, reading.FanRpm);
        Assert.Equal(2800, reading.PumpRpm);
    }

    [Fact]
    public void DecodeHandshake_NullReply_Throws()
        => Assert.Throws<System.ArgumentNullException>(() => Galahad2Protocol.DecodeHandshake(null!));
}
