using System;
using FanControl.LianLi.Protocol;
using Xunit;

namespace FanControl.LianLi.Tests.Protocol;

/// <summary>
/// Byte-level tests for the 0x0416 command-packet framing: the 64-byte layout (report id,
/// command, reserved, packet number, length, payload), zero padding, and the read accessors.
/// </summary>
public sealed class CommandPacketTests {
    [Fact]
    public void Build_FramesTheHeaderPayloadAndZeroPad() {
        byte[] packet = CommandPacket.Build(0xAA, 0x10, 0x32);

        Assert.Equal(64, packet.Length);
        Assert.Equal(0x01, packet[0]); // report id
        Assert.Equal(0xAA, packet[1]); // command
        Assert.Equal(0x00, packet[2]); // reserved
        Assert.Equal(0x00, packet[3]); // packet number high
        Assert.Equal(0x00, packet[4]); // packet number low
        Assert.Equal(0x02, packet[5]); // payload length
        Assert.Equal(0x10, packet[6]); // payload[0]
        Assert.Equal(0x32, packet[7]); // payload[1]
        for (int i = 8; i < 64; i++) {
            Assert.Equal(0x00, packet[i]);
        }
    }

    [Fact]
    public void Build_WithNoPayload_HasZeroLength() {
        byte[] packet = CommandPacket.Build(0xA1);

        Assert.Equal(0x01, packet[0]);
        Assert.Equal(0xA1, packet[1]);
        Assert.Equal(0x00, packet[5]);
    }

    [Fact]
    public void Build_PayloadTooLong_Throws() {
        Assert.Throws<ArgumentException>(() => CommandPacket.Build(0x01, new byte[59]));
    }

    [Fact]
    public void Accessors_ReadCommandLengthAndPayload() {
        byte[] packet = CommandPacket.Build(0xA1, 0x90, 0x05, 0xDC);

        Assert.Equal(0xA1, CommandPacket.CommandOf(packet));
        Assert.Equal(3, CommandPacket.PayloadLengthOf(packet));
        Assert.Equal(new byte[] { 0x90, 0x05, 0xDC }, CommandPacket.Payload(packet, 3));
    }

    [Fact]
    public void RejectsMissingOrTruncatedPackets() {
        Assert.Throws<ArgumentNullException>(() => CommandPacket.Build(0x01, null!));
        Assert.Throws<ArgumentNullException>(() => CommandPacket.Payload(null!, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CommandPacket.Payload(new byte[8], -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CommandPacket.Payload(new byte[8], 3));
        Assert.Throws<ArgumentNullException>(() => CommandPacket.PayloadLengthOf(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => CommandPacket.PayloadLengthOf(new byte[2]));
    }
}
