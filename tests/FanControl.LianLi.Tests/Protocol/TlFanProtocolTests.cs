using System.Collections.Generic;
using FanControl.LianLi.Protocol;
using Xunit;

namespace FanControl.LianLi.Tests.Protocol;

/// <summary>
/// Byte-level tests for the Uni Fan TL protocol: the set-speed packet and its 12-100 duty clamp
/// (0% to the idle value 1), the handshake request, the motherboard-sync address byte, and the
/// big-endian handshake RPM decode that skips undetected fans.
/// </summary>
public sealed class TlFanProtocolTests {
    [Theory]
    [InlineData(0, 0, 0, 0x00, 1)]    // duty 0 -> idle value 1
    [InlineData(0, 0, 5, 0x00, 12)]   // below the 12 floor -> 12
    [InlineData(0, 0, 50, 0x00, 50)]  // in range -> as-is
    [InlineData(0, 0, 150, 0x00, 100)] // above 100 -> 100
    [InlineData(2, 3, 75, 0x23, 75)]  // port 2 / fan 3 -> address 0x23
    public void EncodeSetFanSpeed_FramesAddressAndClampedDuty(
        int port, int fanIndex, int duty, int expectedAddress, int expectedPwm) {
        byte[] packet = TlFanProtocol.EncodeSetFanSpeed(port, fanIndex, duty);

        Assert.Equal(0x01, packet[0]);
        Assert.Equal(0xAA, packet[1]);
        Assert.Equal(0x02, packet[5]);
        Assert.Equal((byte)expectedAddress, packet[6]);
        Assert.Equal((byte)expectedPwm, packet[7]);
    }

    [Fact]
    public void EncodeHandshakeRequest_IsTheBareCommand() {
        byte[] packet = TlFanProtocol.EncodeHandshakeRequest();

        Assert.Equal(0x01, packet[0]);
        Assert.Equal(0xA1, packet[1]);
        Assert.Equal(0x00, packet[5]); // no payload
    }

    [Theory]
    [InlineData(true, 0x90)]  // sync on -> high bit set over address 0x10
    [InlineData(false, 0x10)] // sync off -> address only
    public void EncodeMotherboardSync_SetsTheHighBitForSync(bool sync, int expectedAddress) {
        byte[] packet = TlFanProtocol.EncodeMotherboardSync(port: 1, fanIndex: 0, sync: sync);

        Assert.Equal(0xB1, packet[1]);
        Assert.Equal(0x01, packet[5]);
        Assert.Equal((byte)expectedAddress, packet[6]);
    }

    [Fact]
    public void DecodeHandshake_ReadsBigEndianRpmAndSkipsUndetectedFans() {
        // Two 3-byte records: port 1/fan 0 detected at 1500 rpm, then an undetected slot.
        byte[] reply = CommandPacket.Build(0xA1, 0x90, 0x05, 0xDC, 0x00, 0x00, 0x00);

        IReadOnlyList<TlFanReading> readings = TlFanProtocol.DecodeHandshake(reply);

        TlFanReading reading = Assert.Single(readings);
        Assert.Equal(1, reading.Port);
        Assert.Equal(0, reading.FanIndex);
        Assert.Equal(1500, reading.Rpm);
    }

    // TLFanDevice.GetHandshakeInfo trusts a reply only when it echoes the handshake command.
    [Fact]
    public void DecodeHandshake_APacketAnsweringAnotherCommand_HoldsNoFans() {
        byte[] speedAnswer = CommandPacket.Build(0xAA, 0x90, 0x05, 0xDC);

        Assert.False(TlFanProtocol.IsHandshakeReply(speedAnswer));
        Assert.True(TlFanProtocol.IsHandshakeReply(CommandPacket.Build(0xA1)));
        Assert.Empty(TlFanProtocol.DecodeHandshake(speedAnswer));
    }

    // TLFanHandshakeInfo.Parse stores each record at its address, so the later of two wins.
    [Fact]
    public void DecodeHandshake_ASecondRecordForTheSameFan_ReplacesTheFirst() {
        byte[] reply = CommandPacket.Build(0xA1, 0x90, 0x05, 0xDC, 0x90, 0x07, 0xD0);

        TlFanReading reading = Assert.Single(TlFanProtocol.DecodeHandshake(reply));
        Assert.Equal(2000, reading.Rpm);
    }

    // LEDPacket.Data reads at most 58 payload bytes, whatever the length byte claims.
    [Fact]
    public void DecodeHandshake_ALengthPastTheFrame_IsReadAsTheWholeFrame() {
        byte[] reply = CommandPacket.Build(0xA1, 0x90, 0x05, 0xDC);
        reply[5] = 0xFF;

        TlFanReading reading = Assert.Single(TlFanProtocol.DecodeHandshake(reply));
        Assert.Equal(1500, reading.Rpm);
    }

    [Fact]
    public void DecodeHandshake_NullReply_Throws()
        => Assert.Throws<System.ArgumentNullException>(() => TlFanProtocol.DecodeHandshake(null!));
}
