using System;
using FanControl.LianLi.Protocol;
using Xunit;

namespace FanControl.LianLi.Tests.Protocol;

public class ProtocolEncodingTests {
    // ---- EncodeSetSpeed: exact 4-byte reports per family ----
    // Duty bytes: SL/AL=(800+11d)/19, SLI=(200+19d)/21, SLV2/ALV2=(250+17.5d)/20.

    [Theory]
    [InlineData(0, 0, new byte[] { 224, 32, 0, 42 })]   // 800/19 = 42
    [InlineData(0, 50, new byte[] { 224, 32, 0, 71 })]  // 1350/19 = 71
    [InlineData(0, 100, new byte[] { 224, 32, 0, 100 })] // 1900/19 = 100
    [InlineData(3, 100, new byte[] { 224, 35, 0, 100 })] // channel byte 32+3
    public void EncodeSetSpeed_Sl(int channel, int duty, byte[] expected)
        => Assert.Equal(expected, new SlProtocol().EncodeSetSpeed(channel, duty));

    [Theory]
    [InlineData(0, 0, new byte[] { 224, 32, 0, 42 })]
    [InlineData(1, 100, new byte[] { 224, 33, 0, 100 })]
    public void EncodeSetSpeed_Al(int channel, int duty, byte[] expected)
        => Assert.Equal(expected, new AlProtocol().EncodeSetSpeed(channel, duty));

    [Theory]
    [InlineData(0, 0, new byte[] { 224, 32, 0, 9 })]    // 200/21 = 9
    [InlineData(0, 50, new byte[] { 224, 32, 0, 54 })]  // 1150/21 = 54
    [InlineData(3, 100, new byte[] { 224, 35, 0, 100 })] // 2100/21 = 100
    public void EncodeSetSpeed_SlInfinity(int channel, int duty, byte[] expected)
        => Assert.Equal(expected, new SlInfinityProtocol().EncodeSetSpeed(channel, duty));

    [Theory]
    [InlineData(0, 0, new byte[] { 224, 32, 0, 12 })]   // 250/20 = 12 (12.5 truncated)
    [InlineData(0, 50, new byte[] { 224, 32, 0, 56 })]  // 1125/20 = 56 (56.25 truncated)
    [InlineData(0, 100, new byte[] { 224, 32, 0, 100 })] // 2000/20 = 100
    public void EncodeSetSpeed_SlV2(int channel, int duty, byte[] expected)
        => Assert.Equal(expected, new SlV2Protocol().EncodeSetSpeed(channel, duty));

    [Theory]
    [InlineData(0, 0, new byte[] { 224, 32, 0, 12 })]
    [InlineData(0, 100, new byte[] { 224, 32, 0, 100 })]
    public void EncodeSetSpeed_AlV2(int channel, int duty, byte[] expected)
        => Assert.Equal(expected, new AlV2Protocol().EncodeSetSpeed(channel, duty));

    [Fact]
    public void EncodeSetSpeed_ClampsDutyToZeroToHundred() {
        var protocol = new SlProtocol();
        Assert.Equal(protocol.EncodeSetSpeed(0, 0), protocol.EncodeSetSpeed(0, -10));
        Assert.Equal(protocol.EncodeSetSpeed(0, 100), protocol.EncodeSetSpeed(0, 150));
    }

    // ---- EncodeManualMode: channel byte 0x10<<ch (ch3 MUST be 0x80) and register per family ----

    [Theory]
    [InlineData(0, 0x10)]
    [InlineData(1, 0x20)]
    [InlineData(2, 0x40)]
    [InlineData(3, 0x80)] // regression lock: the upstream (2*ch)*16 bug gave 0x60 here
    public void EncodeManualMode_ChannelByteIsBitShift(int channel, int expectedChannelByte) {
        byte[] report = new SlProtocol().EncodeManualMode(channel);
        Assert.Equal((byte)expectedChannelByte, report[3]);
    }

    [Theory]
    [InlineData(typeof(SlProtocol), 49)]
    [InlineData(typeof(AlProtocol), 66)]
    [InlineData(typeof(SlInfinityProtocol), 98)]
    [InlineData(typeof(SlV2Protocol), 98)]
    [InlineData(typeof(AlV2Protocol), 98)]
    public void EncodeManualMode_RegisterPerFamily(Type protocolType, int expectedRegister) {
        var protocol = (IFanProtocol)Activator.CreateInstance(protocolType)!;
        byte[] report = protocol.EncodeManualMode(0);
        Assert.Equal(new byte[] { 224, 16, (byte)expectedRegister, 0x10 }, report);
    }

    // ---- EncodeArgbSync: {224,16,reg,on,0,0,0} per family ----

    [Theory]
    [InlineData(typeof(SlProtocol), 48)]
    [InlineData(typeof(AlProtocol), 65)]
    [InlineData(typeof(SlInfinityProtocol), 97)]
    [InlineData(typeof(SlV2Protocol), 97)]
    [InlineData(typeof(AlV2Protocol), 97)]
    public void EncodeArgbSync_PerFamily(Type protocolType, int expectedRegister) {
        var protocol = (IFanProtocol)Activator.CreateInstance(protocolType)!;
        Assert.Equal(new byte[] { 224, 16, (byte)expectedRegister, 1, 0, 0, 0 }, protocol.EncodeArgbSync(true));
        Assert.Equal(new byte[] { 224, 16, (byte)expectedRegister, 0, 0, 0, 0 }, protocol.EncodeArgbSync(false));
    }

    // ---- DecodeRpm: big-endian pair at the family offset ----

    [Fact]
    public void DecodeRpm_Offset1_BigEndian() {
        var buffer = new byte[65];
        buffer[1] = 0x0A; // ch0 high
        buffer[2] = 0x28; // ch0 low  -> (10<<8)|40 = 2600
        buffer[7] = 0x01; // ch3 high (offset 1 + 3*2 = 7)
        buffer[8] = 0x2C; // ch3 low  -> (1<<8)|44 = 300

        var protocol = new SlProtocol();
        Assert.Equal(2600f, protocol.DecodeRpm(buffer, 0));
        Assert.Equal(300f, protocol.DecodeRpm(buffer, 3));
    }

    [Fact]
    public void DecodeRpm_Offset2_ForV2() {
        var buffer = new byte[65];
        buffer[2] = 0x0A; // ch0 high (offset 2)
        buffer[3] = 0x28; // ch0 low -> 2600

        Assert.Equal(2600f, new SlV2Protocol().DecodeRpm(buffer, 0));
        Assert.Equal(2600f, new AlV2Protocol().DecodeRpm(buffer, 0));
    }

    [Theory]
    [InlineData(typeof(SlProtocol), 1)]
    [InlineData(typeof(AlProtocol), 1)]
    [InlineData(typeof(SlInfinityProtocol), 1)]
    [InlineData(typeof(SlV2Protocol), 2)]
    [InlineData(typeof(AlV2Protocol), 2)]
    public void RpmReportOffset_PerFamily(Type protocolType, int expectedOffset) {
        var protocol = (IFanProtocol)Activator.CreateInstance(protocolType)!;
        Assert.Equal(expectedOffset, protocol.RpmReportOffset);
    }

    [Fact]
    public void ChannelCount_IsFour()
        => Assert.Equal(4, new SlProtocol().ChannelCount);

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void EncodeSetSpeed_ThrowsForChannelOutOfRange(int channel)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new SlProtocol().EncodeSetSpeed(channel, 50));

    // ---- EncodeRpmRequest: SlInfinity requires a 65-byte primer; others return null ----

    [Fact]
    public void EncodeRpmRequest_SlInfinity_Returns65ByteBufferWithPrimer() {
        byte[]? request = new SlInfinityProtocol().EncodeRpmRequest();
        Assert.NotNull(request);
        Assert.Equal(65, request!.Length);
        Assert.Equal(0xE0, request[0]);
        Assert.Equal(0x50, request[1]);
        Assert.Equal(0x00, request[2]);
        for (int i = 3; i < 65; i++) {
            Assert.Equal(0, request[i]);
        }
    }

    [Theory]
    [InlineData(typeof(SlProtocol))]
    [InlineData(typeof(AlProtocol))]
    [InlineData(typeof(SlV2Protocol))]
    [InlineData(typeof(AlV2Protocol))]
    public void EncodeRpmRequest_OtherFamilies_ReturnsNull(Type protocolType) {
        var protocol = (IFanProtocol)Activator.CreateInstance(protocolType)!;
        Assert.Null(protocol.EncodeRpmRequest());
    }
}
