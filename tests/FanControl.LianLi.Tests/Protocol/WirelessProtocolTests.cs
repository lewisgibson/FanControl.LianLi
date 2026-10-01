using System;
using System.Linq;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Tests.Fakes;
using Xunit;

namespace FanControl.LianLi.Tests.Protocol;

/// <summary>
/// Byte-level tests for the L-Wireless dongle protocol. Every encoded packet and payload is
/// compared whole, zeros included, against an expected buffer written out from the L-Connect
/// method named in the test's comment - never against another call into the encoder.
/// </summary>
public sealed class WirelessProtocolTests {
    private static readonly byte[] MasterMac = { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
    private static readonly byte[] GroupMac = { 0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6 };

    /// <summary>One list record laid out exactly as the receiver reports it (docs/wireless.md, "The device list record").</summary>
    public static byte[] Record(
        byte[] mac, byte[] masterMac, byte channel, byte receiverType, byte deviceType, byte fanCount,
        byte[] fanTypes, int[] rpm, byte[] pwm, byte sequence, byte marker = 0x1C)
        => new FakeWirelessRecord(mac, masterMac) {
            Channel = channel,
            Receiver = receiverType,
            DeviceType = deviceType,
            FanCountByte = fanCount,
            FanTypes = fanTypes,
            Rpm = rpm,
            Pwm = pwm,
            Sequence = sequence,
            Marker = marker,
        }.ToBytes();

    /// <summary>A one-page list reply: the 4-byte header then the records, padded to 434 bytes.</summary>
    public static byte[] ListReply(int total, params byte[][] records) {
        var reply = new byte[434];
        reply[0] = 0x10;
        reply[1] = (byte)total;
        for (int i = 0; i < records.Length; i++) {
            Array.Copy(records[i], 0, reply, 4 + (i * 42), 42);
        }

        return reply;
    }

    [Fact]
    public void DecodeRecord_ReadsTheLayoutAlone_WithoutTheListReadsPwmRule() {
        // Ten more than the true count marks the right-attached end cap; an all-zero PWM with a
        // spinning first fan is read as is here, and as 100 by the list read.
        var record = new FakeWirelessRecord(GroupMac, MasterMac) {
            FanCountByte = 12,
            FanTypes = B(51, 52, 0, 0),
            Rpm = new[] { 1200, 1100, 0, 0 },
            RpmHighNibbles = B(0xA, 0x3, 0x2, 0x6),
            Sequence = 5,
        };
        byte[] reply = ListReply(1, record.ToBytes());

        WirelessDeviceRecord alone = WirelessProtocol.DecodeRecord(reply, 4);

        Assert.Equal(2, alone.FanCount);
        Assert.True(alone.RightAttached);
        Assert.Equal(new[] { 1200, 1100, 0, 0 }, alone.Rpm);
        Assert.Equal(new byte[4], alone.Pwm);
        Assert.Equal(new byte[] { 100, 100, 100, 100 }, Decode(record).Pwm);
        Assert.Equal((byte)5, alone.CommandSequence);
        Assert.Throws<ArgumentNullException>(() => WirelessProtocol.DecodeRecord(null!, 0));
        Assert.Throws<ArgumentException>(() => WirelessProtocol.DecodeRecord(reply, -1));
        Assert.Throws<ArgumentException>(() => WirelessProtocol.DecodeRecord(new byte[41], 0));
    }

    // A buffer of the given length that is zero except where a (offset, bytes) pair says otherwise.
    private static byte[] Expected(int length, params (int Offset, byte[] Bytes)[] parts) {
        var buffer = new byte[length];
        foreach ((int offset, byte[] bytes) in parts) {
            Array.Copy(bytes, 0, buffer, offset, bytes.Length);
        }

        return buffer;
    }

    private static byte[] B(params int[] values) => Array.ConvertAll(values, v => (byte)v);

    private static WirelessDeviceRecord Decode(FakeWirelessRecord record)
        => Assert.Single(WirelessProtocol.DecodeDeviceList(ListReply(1, record.ToBytes()), 1)!.Records);

    private static WirelessDeviceRecord Group(params int[] fanTypes)
        => Decode(new FakeWirelessRecord(GroupMac, MasterMac) { FanCountByte = 4, FanTypes = B(fanTypes) });

    private static WirelessDeviceRecord AttachedGroup(int fanCountByte, params int[] fanTypes)
        => Decode(new FakeWirelessRecord(GroupMac, MasterMac) { FanCountByte = (byte)fanCountByte, FanTypes = B(fanTypes) });

    // MasterDevice.QuerryMasterMac: [0] = 17, [1] = MasterChannel, the rest zero.
    [Fact]
    public void EncodeMasterQuery_IsCommand0x11WithTheChannelAndNothingElse()
        => Assert.Equal(Expected(64, (0, B(0x11, 11))), WirelessProtocol.EncodeMasterQuery(11));

    // QuerryMasterMac: address from bytes 1-6; time_tmos from 7-10 as an int; SysClock = (int)(ticks * 0.625).
    [Fact]
    public void DecodeMasterQuery_ReadsTheAddressAndTheClockInMilliseconds() {
        byte[] reply = Expected(64, (0, B(0x11)), (1, MasterMac), (7, B(0x00, 0x00, 0x06, 0x41)), (11, B(0x01, 0x02)));

        WirelessMasterReply decoded = WirelessProtocol.DecodeMasterQuery(reply)!;

        Assert.Equal(MasterMac, decoded.Mac);
        Assert.Equal(1000, decoded.ClockMilliseconds); // 1601 ticks = 1000.625 ms, truncated
        Assert.True(decoded.IsClockRunning);
        Assert.True(decoded.HasAddress);
    }

    // QuerryMasterMac sums the ticks as an int, so a clock past 2^31 ticks is negative.
    [Fact]
    public void DecodeMasterQuery_ATopBitClockIsNegativeAsLConnectReadsIt() {
        byte[] reply = Expected(64, (0, B(0x11)), (1, MasterMac), (7, B(0x80, 0x00, 0x00, 0x00)));

        Assert.Equal((int)(int.MinValue * 0.625), WirelessProtocol.DecodeMasterQuery(reply)!.ClockMilliseconds);
    }

    [Fact]
    public void DecodeMasterQuery_ZeroClockAndZeroAddressAreReportedNotHidden() {
        WirelessMasterReply decoded = WirelessProtocol.DecodeMasterQuery(Expected(64, (0, B(0x11))))!;

        Assert.False(decoded.IsClockRunning);
        Assert.False(decoded.HasAddress);
        Assert.Equal(new byte[6], decoded.Mac);
    }

    [Fact]
    public void DecodeMasterQuery_AnythingButAQueryEchoIsNoReply() {
        Assert.Null(WirelessProtocol.DecodeMasterQuery(Expected(64, (0, B(0x10)), (1, MasterMac))));
        Assert.Null(WirelessProtocol.DecodeMasterQuery(B(0x11, 1, 2, 3)));
        Assert.Throws<ArgumentNullException>(() => WirelessProtocol.DecodeMasterQuery(null!));
    }

    // MasterDevice.ResetRx / ResetTx: a 64-byte packet with [0] = 21.
    [Fact]
    public void EncodeDongleReset_IsCommand0x15AndNothingElse()
        => Assert.Equal(Expected(64, (0, B(0x15))), WirelessProtocol.EncodeDongleReset());

    // MasterDevice.GetDev(16, page): [0] = 16, [1] = pages; bytes 2-3 only while FgSync.
    [Fact]
    public void EncodeDeviceListRequest_IsCommand0x10WithThePageCount() {
        Assert.Equal(Expected(64, (0, B(0x10, 3))), WirelessProtocol.EncodeDeviceListRequest(3));
        Assert.Equal(1302, WirelessProtocol.DeviceListReplyLength(3)); // defalut_page_length 434 per page
    }

    // RefreshList: get_page_cnt = (byte)Math.Ceiling(num / 10.0).
    [Theory]
    [InlineData(1, 1)]
    [InlineData(10, 1)]
    [InlineData(11, 2)]
    [InlineData(25, 3)]
    [InlineData(255, 26)]
    public void PagesFor_IsExactlyEnoughTenRecordPages(int total, int pages)
        => Assert.Equal(pages, WirelessProtocol.PagesFor(total));

    // RefreshList: every offset of the 42-byte record.
    [Fact]
    public void DecodeDeviceList_DecodesEveryFieldOfARecord() {
        var record = new FakeWirelessRecord(GroupMac, MasterMac) {
            Channel = 9,
            Receiver = 3,
            ClockTicks = 3200,
            DeviceType = 0,
            FanCountByte = 12,
            EffectIndex = B(1, 2, 3, 4),
            FanTypes = B(36, 36, 0, 0),
            Rpm = new[] { 1234, 1500, 0, 0 },
            Pwm = B(127, 127, 0, 0),
            Sequence = 7,
        };

        WirelessDeviceList list = WirelessProtocol.DecodeDeviceList(ListReply(1, record.ToBytes()), 1)!;

        Assert.Equal(1, list.Total);
        WirelessDeviceRecord decoded = Assert.Single(list.Records);
        Assert.Equal(GroupMac, decoded.Mac);
        Assert.Equal("a1b2c3d4e5f6", decoded.MacText);
        Assert.Equal(MasterMac, decoded.MasterMac);
        Assert.Equal(9, decoded.Channel);
        Assert.Equal(3, decoded.ReceiverType);
        Assert.Equal(2000, decoded.ClockMilliseconds); // 3200 ticks of 0.625 ms
        Assert.Equal(0, decoded.DeviceType);
        Assert.Equal(WirelessDeviceKind.FanGroup, decoded.Kind);
        Assert.Equal(2, decoded.FanCount); // 12: two fans, end cap on the right
        Assert.Equal(B(1, 2, 3, 4), decoded.EffectIndex);
        Assert.Equal(B(36, 36, 0, 0), decoded.FanTypes);
        Assert.Equal(new[] { 1234, 1500, 0, 0 }, decoded.Rpm);
        Assert.Equal(B(127, 127, 0, 0), decoded.Pwm);
        Assert.Equal(7, decoded.CommandSequence);
        Assert.True(decoded.RightAttached);
        Assert.False(Decode(new FakeWirelessRecord(GroupMac, MasterMac) { FanCountByte = 2 }).RightAttached);
    }

    // RefreshList masks the high nibble of fans_speed[0], [2], [4] and [6] before the
    // RPM is read: slot 0's carries the sync, motherboard-light, PWM-line and tach flags, slot 1's
    // the P28 V2 gear or the wireless-theme bits, and slots 2 and 3's the RF firmware version.
    // Speeds[i] = (fans_speed[2i] << 8) + fans_speed[2i+1] on the masked bytes.
    [Fact]
    public void DecodeDeviceList_MasksTheFlagAndFirmwareNibblesOffEveryRpm() {
        var record = new FakeWirelessRecord(GroupMac, MasterMac) {
            FanCountByte = 4,
            FanTypes = B(51, 51, 51, 51),
            Rpm = new[] { 0x0FFF, 1500, 0x0123, 3200 },
            RpmHighNibbles = B(0xF, 0x7, 0x1, 0x8), // every flag; gear 7; TL FLEX version 0x18
        };

        WirelessDeviceRecord decoded = Decode(record);

        Assert.Equal(new[] { 0x0FFF, 1500, 0x0123, 3200 }, decoded.Rpm);
    }

    // Of the nibbles, RefreshList keeps two the plugin reads: IsSyncMbLight is bit 6 of
    // fans_speed[0], and WiredlessThemeSwitch is the high nibble of fans_speed[2] on every group
    // whose first fan is not a P28 V2 (63), whose nibble is its gear and sync line.
    [Fact]
    public void DecodeDeviceList_ReadsTheLightingSyncFlag_AndTheThemeSwitchBits() {
        var lit = new FakeWirelessRecord(GroupMac, MasterMac) { FanTypes = B(51, 51, 51, 0), RpmHighNibbles = B(0x4, 0xB, 0, 0) };
        var plain = new FakeWirelessRecord(GroupMac, MasterMac) { FanTypes = B(51, 51, 51, 0), RpmHighNibbles = B(0xB, 0x0, 0, 0) };
        var p28 = new FakeWirelessRecord(GroupMac, MasterMac) { FanTypes = B(63, 63, 63, 0), RpmHighNibbles = B(0x4, 0xB, 0, 0) };

        Assert.True(Decode(lit).LightingFollowsMotherboard);
        Assert.Equal(0xB, Decode(lit).ThemeSwitches);
        Assert.False(Decode(plain).LightingFollowsMotherboard);
        Assert.Equal(0, Decode(plain).ThemeSwitches);
        Assert.True(Decode(p28).LightingFollowsMotherboard);
        Assert.Equal(0, Decode(p28).ThemeSwitches);
    }

    // The no-PWM rule tests the masked byte: a flag nibble alone is not a spinning fan.
    [Theory]
    [InlineData(0, 0xF, 0)]
    [InlineData(256, 0xF, 100)]
    public void DecodeDeviceList_TheUnreportedPwmRuleIgnoresTheFlagNibble(int rpm, int nibble, int expected) {
        var record = new FakeWirelessRecord(GroupMac, MasterMac) { Rpm = new[] { rpm, 0, 0, 0 }, RpmHighNibbles = B(nibble, 0, 0, 0) };

        Assert.Equal(B(expected, expected, expected, expected), Decode(record).Pwm);
    }

    // RefreshList subtracts 10 from a count of 10 or more, and RfDevice.FanNum subtracts 10 again
    // from whatever is still 10 or more; a record has four slots, so anything past four is clamped.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 4)]
    [InlineData(5, 4)]
    [InlineData(9, 4)]
    [InlineData(10, 0)]
    [InlineData(13, 3)]
    [InlineData(15, 4)]
    [InlineData(22, 2)]
    [InlineData(255, 4)]
    public void DecodeDeviceList_FanCountIsLConnectsAndNeverMoreThanFour(int raw, int expected)
        => Assert.Equal(expected, Decode(new FakeWirelessRecord(GroupMac, MasterMac) { FanCountByte = (byte)raw }).FanCount);

    // RefreshList: all four PWMs zero while fans_speed[0] (slot 0's RPM high byte) is set -> 100 on every slot.
    [Theory]
    [InlineData(256, 100)]
    [InlineData(255, 0)]
    public void DecodeDeviceList_AnUnreportedPwmOnASpinningGroupReadsAs100(int rpm, int expected) {
        var record = new FakeWirelessRecord(GroupMac, MasterMac) { Rpm = new[] { rpm, 0, 0, 0 } };

        Assert.Equal(B(expected, expected, expected, expected), Decode(record).Pwm);
    }

    [Theory]
    [InlineData(9, 0, 0, 0)]
    [InlineData(0, 9, 0, 0)]
    [InlineData(0, 0, 9, 0)]
    [InlineData(0, 0, 0, 9)]
    public void DecodeDeviceList_APartlyReportedPwmIsKept(int a, int b, int c, int d) {
        var record = new FakeWirelessRecord(GroupMac, MasterMac) { Rpm = new[] { 1000, 0, 0, 0 }, Pwm = B(a, b, c, d) };

        Assert.Equal(B(a, b, c, d), Decode(record).Pwm);
    }

    // RefreshList: at most get_page_cnt * 10 records are read, a record whose byte 41 is not 28 is
    // skipped, and a master's own record (type 255) is decoded like any other.
    [Fact]
    public void DecodeDeviceList_ReadsOnlyTheRequestedPagesAndSkipsABadMarker() {
        byte[] good = Record(GroupMac, MasterMac, 8, 1, 0, 1, new byte[4], new int[4], new byte[4], 0);
        byte[] bad = Record(MasterMac, MasterMac, 8, 1, 0, 1, new byte[4], new int[4], new byte[4], 0, marker: 0x00);
        byte[] master = Record(MasterMac, new byte[6], 8, 0, 0xFF, 0, new byte[4], new int[4], new byte[4], 0);
        var records = new byte[11][];
        for (int i = 0; i < records.Length; i++) {
            records[i] = good;
        }

        records[1] = bad;
        records[2] = master;
        var reply = new byte[868];
        reply[0] = 0x10;
        reply[1] = 11;
        for (int i = 0; i < records.Length; i++) {
            Array.Copy(records[i], 0, reply, 4 + (i * 42), 42);
        }

        WirelessDeviceList onePage = WirelessProtocol.DecodeDeviceList(reply, 1)!;
        WirelessDeviceList twoPages = WirelessProtocol.DecodeDeviceList(reply, 2)!;

        Assert.Equal(11, onePage.Total);
        Assert.Equal(9, onePage.Records.Count); // ten read, one bad marker
        Assert.Equal(WirelessDeviceKind.Master, onePage.Records[1].Kind);
        Assert.Equal(10, twoPages.Records.Count);
    }

    [Fact]
    public void DecodeDeviceList_StopsAtTheEndOfAShortReply() {
        byte[] good = Record(GroupMac, MasterMac, 8, 1, 0, 1, new byte[4], new int[4], new byte[4], 0);
        var reply = new byte[4 + 42 + 10];
        reply[0] = 0x10;
        reply[1] = 2;
        Array.Copy(good, 0, reply, 4, 42);

        Assert.Single(WirelessProtocol.DecodeDeviceList(reply, 1)!.Records);
    }

    [Fact]
    public void DecodeDeviceList_AnythingButAListEchoIsNoList() {
        Assert.Null(WirelessProtocol.DecodeDeviceList(Expected(434, (0, B(0x11, 3))), 1));
        Assert.Null(WirelessProtocol.DecodeDeviceList(B(0x10, 1, 0), 1));
        Assert.Throws<ArgumentNullException>(() => WirelessProtocol.DecodeDeviceList(null!, 1));
    }

    // MasterDevice.SyncPwm: [0] 18, [1] 16, [2-7] device, [8-13] target master, [14] target_rx_type,
    // [15] target_channel, [16] bind index, [17-20] target_fans_pwm, the rest zero.
    [Fact]
    public void EncodeSpeedPayload_IsSyncPwmsBindAndSpeedCommand() {
        byte[] payload = WirelessProtocol.EncodeSpeedPayload(GroupMac, MasterMac, receiverType: 3, channel: 9, bindIndex: 2, pwm: B(127, 255, 35, 0));

        Assert.Equal(Expected(240, (0, B(0x12, 0x10)), (2, GroupMac), (8, MasterMac), (14, B(3, 9, 2, 127, 255, 35, 0))), payload);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(256)]
    public void EncodeSpeedPayload_RefusesAnIndexThatWouldUnbindOrWrap(int bindIndex)
        => Assert.Throws<ArgumentOutOfRangeException>(() => WirelessProtocol.EncodeSpeedPayload(GroupMac, MasterMac, 1, 8, bindIndex, new byte[4]));

    // MasterDevice.SendRfData: [0] 16, [1] chunk, [2] channel, [3] rx, [4-63] sixty payload bytes.
    [Fact]
    public void EncodeTransmitPackets_SplitsThePayloadIntoFourAddressedChunks() {
        var payload = new byte[240];
        for (int i = 0; i < payload.Length; i++) {
            payload[i] = (byte)(i + 1);
        }

        byte[][] packets = WirelessProtocol.EncodeTransmitPackets(channel: 8, receiverType: 3, payload);

        Assert.Equal(4, packets.Length);
        for (int chunk = 0; chunk < 4; chunk++) {
            Assert.Equal(Expected(64, (0, B(0x10, chunk, 8, 3)), (4, payload[(chunk * 60)..((chunk + 1) * 60)])), packets[chunk]);
        }
    }

    // MasterDevice.SendAioInfo: [0] 18, [1] 33, [2-7] device, [8-13] master, [14] target_rx_type,
    // [15] target_channel, [18-49] aio_param.
    [Fact]
    public void EncodeAioPayload_CarriesTheParameterBlockAtOffset18() {
        var parameters = new byte[32];
        for (int i = 0; i < parameters.Length; i++) {
            parameters[i] = (byte)(0x80 + i);
        }

        byte[] payload = WirelessProtocol.EncodeAioPayload(GroupMac, MasterMac, 4, 9, parameters);

        Assert.Equal(Expected(240, (0, B(0x12, 0x21)), (2, GroupMac), (8, MasterMac), (14, B(4, 9)), (18, parameters)), payload);
    }

    // RFController.SetAioParams with L-Connect's new-block defaults (LWirelessController.addSettingDevice):
    // interval 2, pump temperature shown, fan speed 2000 (0x07D0) hidden, white, brightness 100, theme 0.
    // The live CPU/GPU figures and their flags are zero: the plugin has none to show.
    [Fact]
    public void EncodeAioParameters_DefaultScreen() {
        byte[] parameters = WirelessProtocol.EncodeAioParameters(WirelessAioPresentation.Default, 0x0123);

        Assert.Equal(
            B(0, 0, 0, 0, 0x07, 0xD0, 2, 1, 0, 0, 0, 0, 0,
                0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
                100, 1, 0, 0x01, 0x23, 0, 0),
            parameters);
    }

    [Fact]
    public void EncodeAioParameters_CarriesEverySavedSettingThrough() {
        var presentation = new WirelessAioPresentation(
            refreshInterval: 5,
            pumpTemperatureShown: false,
            fanSpeed: 0x1234,
            fanSpeedShown: true,
            brightness: 60,
            themeIndex: 7,
            rotation: 3,
            title: new WirelessAioPresentation.Argb(0xFF, 0x10, 0x20, 0x30),
            value: new WirelessAioPresentation.Argb(0xEE, 0x40, 0x50, 0x60),
            unit: new WirelessAioPresentation.Argb(0xDD, 0x70, 0x80, 0x90),
            advanceMode: false);

        byte[] parameters = WirelessProtocol.EncodeAioParameters(presentation, 999);

        Assert.Equal(
            B(0, 0, 0, 0, 0x12, 0x34, 5, 0, 0, 0, 0, 0, 1,
                0xFF, 0x10, 0x20, 0x30, 0xEE, 0x40, 0x50, 0x60, 0xDD, 0x70, 0x80, 0x90,
                60, 1, 7, 0x03, 0xE7, 3, 0),
            parameters);
    }

    [Fact]
    public void EncodeWirelessThemePayload_AddressesTheDevice_WithTheCountAt16_AndTheSequenceAt17() {
        byte[] device = { 1, 2, 3, 4, 5, 6 };
        byte[] master = { 7, 8, 9, 10, 11, 12 };

        byte[] payload = WirelessProtocol.EncodeWirelessThemePayload(device, master, 3, 8, 5, 42);

        var expected = new byte[240];
        expected[0] = 0x12;
        expected[1] = 0x19;
        Array.Copy(device, 0, expected, 2, 6);
        Array.Copy(master, 0, expected, 8, 6);
        expected[14] = 3;
        expected[15] = 8;
        expected[16] = 5;
        expected[17] = 42;
        Assert.Equal(expected, payload);
    }

    // SyncControlInfo's isTlv3CloseLight branch: the theme switch's layout with the source at 20.
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void EncodeLightingSyncPayload_CarriesTheSourceAt20(bool follow, byte source) {
        byte[] device = { 1, 2, 3, 4, 5, 6 };
        byte[] master = { 7, 8, 9, 10, 11, 12 };

        byte[] payload = WirelessProtocol.EncodeLightingSyncPayload(device, master, 3, 8, 5, 42, follow);

        Assert.Equal(Expected(240, (0, B(0x12, 0x27)), (2, device), (8, master), (14, B(3, 8, 5, 42)), (20, B(source))), payload);
    }

    // SyncControlInfo's isPlayWirelessTheme branch: the switch mask (WiredlessThemeSwitchTarget) at 20.
    [Fact]
    public void EncodeThemeSwitchPayload_CarriesTheSwitchMaskAt20() {
        byte[] device = { 1, 2, 3, 4, 5, 6 };
        byte[] master = { 7, 8, 9, 10, 11, 12 };

        byte[] payload = WirelessProtocol.EncodeThemeSwitchPayload(device, master, 3, 8, 5, 42, 0b0111);

        Assert.Equal(Expected(240, (0, B(0x12, 0x29)), (2, device), (8, master), (14, B(3, 8, 5, 42)), (20, B(0b0111))), payload);
    }

    // SyncControlInfo's isSendColors branch: the 76-byte buffer at 20, the fan's slot (its table
    // index times 19) holding the six colours' red, green and blue and then the change marker,
    // the other slots zero (UpdateSensorColors fills a fresh buffer).
    [Theory]
    [InlineData(0, 20)]
    [InlineData(3, 77)]
    public void EncodeScreenColoursPayload_PutsTheColoursAndTheMarkerInTheFansSlotAt20(int fan, int offset) {
        byte[] device = { 1, 2, 3, 4, 5, 6 };
        byte[] master = { 7, 8, 9, 10, 11, 12 };
        byte[] colours = Enumerable.Range(1, 18).Select(i => (byte)i).ToArray();

        byte[] payload = WirelessProtocol.EncodeScreenColoursPayload(device, master, 3, 8, 5, 42, fan, colours);

        Assert.Equal(Expected(240, (0, B(0x12, 0x28)), (2, device), (8, master), (14, B(3, 8, 5, 42)), (offset, colours), (offset + 18, B(42))), payload);
        Assert.Throws<ArgumentNullException>(() => WirelessProtocol.EncodeScreenColoursPayload(device, master, 3, 8, 5, 42, 0, null!));
        Assert.Throws<ArgumentException>(() => WirelessProtocol.EncodeScreenColoursPayload(device, master, 3, 8, 5, 42, 0, new byte[17]));
        Assert.Throws<ArgumentOutOfRangeException>(() => WirelessProtocol.EncodeScreenColoursPayload(device, master, 3, 8, 5, 42, 4, colours));
        Assert.Throws<ArgumentOutOfRangeException>(() => WirelessProtocol.EncodeScreenColoursPayload(device, master, 3, 8, 5, 42, -1, colours));
    }

    // UpdateSensorColors draws the marker from random.Next(1, 254): the plugin's is 1 to 253 too,
    // from the round's sequence (1 to 254), so consecutive rounds carry different markers.
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(253, 253)]
    [InlineData(254, 1)]
    public void ScreenColoursMarker_IsOneToTwoHundredAndFiftyThree_FromTheSequence(int sequence, int marker)
        => Assert.Equal((byte)marker, WirelessProtocol.ScreenColoursMarker((byte)sequence));

    // TryRestoreWirelessThemeSwitches asks for every fan whose bit is clear, fan i at bit
    // max(count, 3) - 1 - i (ToThemeSwitchPlayIndex), ored over what the device reports
    // (PlayWiredlessThemeSwitch's target starts from WiredlessThemeSwitch).
    [Theory]
    [InlineData(0b0000, 4, 0b1111)]
    [InlineData(0b0000, 3, 0b0111)]
    [InlineData(0b0000, 2, 0b0110)]
    [InlineData(0b0000, 1, 0b0100)]
    [InlineData(0b0000, 0, 0b0000)]
    [InlineData(0b0101, 3, 0b0111)]
    [InlineData(0b1000, 2, 0b1110)]
    [InlineData(0b0111, 3, 0b0111)]
    public void ThemeSwitchesForAllScreens_SetsEveryFansBitInTheFirmwaresOrder(int reported, int fans, int expected) {
        Assert.Equal((byte)expected, WirelessProtocol.ThemeSwitchesForAllScreens((byte)reported, fans));
        Assert.Throws<ArgumentOutOfRangeException>(() => WirelessProtocol.ThemeSwitchesForAllScreens(0, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => WirelessProtocol.ThemeSwitchesForAllScreens(0, -1));
    }

    // A screen in advance mode keeps every saved setting, with theme 0 (WirelessAioPresentation zeroes
    // it), as L-Connect's setting handlers write it.
    [Fact]
    public void EncodeAioParameters_InAdvanceMode_KeepsTheSavedSettings_WithThemeZero() {
        var presentation = new WirelessAioPresentation(
            5, true, 0x1234, true, 37, 7, 2,
            new WirelessAioPresentation.Argb(1, 2, 3, 4), new WirelessAioPresentation.Argb(5, 6, 7, 8), new WirelessAioPresentation.Argb(9, 10, 11, 12),
            advanceMode: true);

        Assert.Equal(
            B(0, 0, 0, 0, 0x12, 0x34, 5, 1, 0, 0, 0, 0, 1, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 37, 1, 0, 0x03, 0xE7, 2, 0),
            WirelessProtocol.EncodeAioParameters(presentation, 999));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(253, 254)]
    [InlineData(254, 1)]
    [InlineData(255, 2)]
    public void NextCommandSequence_RunsOneTo254_AndNeverRepeatsTheAcknowledgedOne(int acknowledged, int next)
        => Assert.Equal(next, WirelessProtocol.NextCommandSequence((byte)acknowledged));

    [Fact]
    public void EncodeAioParameters_NullPresentation_Throws()
        => Assert.Throws<ArgumentNullException>(() => WirelessProtocol.EncodeAioParameters(null!, 0));

    // MasterDevice.SyncMasterClock: [0] 18, [1] 20, [8-13] master, [14-233] cpuInfoParam:
    // the 50-byte fixed block (RFController.GetFixedData's figures, all zero from the plugin, with
    // the year big-endian at 32-33 then month, day, hour, minute, second at 34-38), the 168-byte
    // screen table at 50 (MasterDevice.SetFanLcdInfo), then zeros.
    [Fact]
    public void EncodeClockPayload_AddressesNobody_CarriesTheMaster_TheLocalTime_AndTheScreenTable() {
        var screens = new WirelessScreenTable();
        screens.SetFan(1, 0, 7, 2, 3, 60);
        screens.SetFan(13, 3, 255, 1, 0, 100);
        var time = new DateTime(2026, 9, 29, 23, 58, 59, DateTimeKind.Local);

        byte[] payload = WirelessProtocol.EncodeClockPayload(MasterMac, time, screens);

        Assert.Equal(
            Expected(240,
                (0, B(0x12, 0x14)),
                (8, MasterMac),
                (46, B(0x07, 0xEA, 9, 29, 23, 58, 59)),
                (64, B(7, 0, 0, 0, 0x43)), // slot 1: theme 7 on fan 0, direction 2 << 5 | source 3
                (72, B(60)),
                (64 + (12 * 12) + 3, B(255)), // slot 13: fan 3
                (64 + (12 * 12) + 7, B(0x20, 100))),
            payload);
    }

    [Fact]
    public void EncodeClockPayload_AnEmptyTableLeavesTheBlockZeroButTheTime() {
        byte[] payload = WirelessProtocol.EncodeClockPayload(MasterMac, new DateTime(2000, 1, 2, 3, 4, 5), new WirelessScreenTable());

        Assert.Equal(Expected(240, (0, B(0x12, 0x14)), (8, MasterMac), (46, B(0x07, 0xD0, 1, 2, 3, 4, 5))), payload);
        Assert.Throws<ArgumentNullException>(() => WirelessProtocol.EncodeClockPayload(MasterMac, DateTime.Now, null!));
    }

    // MasterDevice.SaveConfig: [0] 18, [1] 21, [2-7] FF, [8-13] master, [14] FF, [15] 0, [16] 0.
    [Fact]
    public void EncodeSaveConfigurationPayload_BroadcastsFromTheMaster()
        => Assert.Equal(
            Expected(240, (0, B(0x12, 0x15, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF)), (8, MasterMac), (14, B(0xFF))),
            WirelessProtocol.EncodeSaveConfigurationPayload(MasterMac));

    // MasterDevice.SyncRgbData: [0] 18, [1] 32, [2-7] device, [8-13] master, [14-17] effect index,
    // [18] index, [19] total_pk_num + 1; the descriptor at 20-39, the data 220 bytes at a time at 20.
    [Fact]
    public void EncodeEffectPayloads_DescribesThenSlicesTheEffect() {
        var data = new byte[450];
        for (int i = 0; i < data.Length; i++) {
            data[i] = (byte)(i + 1);
        }

        byte[] identity = B(0xAA, 0xBB, 0xCC, 0xDD);
        var effect = new WirelessSavedEffect(data, identity, 300, 7, 120, 33.75, 12.5);

        byte[][] payloads = WirelessProtocol.EncodeEffectPayloads(GroupMac, MasterMac, effect);

        Assert.Equal(4, payloads.Length); // the descriptor and ceil(450 / 220) = 3 chunks
        (int, byte[])[] Front(int index) => new[] { (0, B(0x12, 0x20)), (2, GroupMac), (8, MasterMac), (14, identity), (18, B(index, 4)) };
        Assert.Equal(
            Expected(240, (0, B(0x12, 0x20)), (2, GroupMac), (8, MasterMac), (14, identity), (18, B(0, 4)),
                (20, B(0, 0, 1, 194, 0, 1, 44, 120)), // 450 bytes, a zero, 300 frames, 120 LEDs
                (32, B(0, 33, 75, 0, 12, 0, 0, 7))), // 33.75 ms, 12 ms, isOuterMatchMax 0, 7 sub-frames
            payloads[0]);
        Assert.Equal(Expected(240, [.. Front(1), (20, data[0..220])]), payloads[1]);
        Assert.Equal(Expected(240, [.. Front(2), (20, data[220..440])]), payloads[2]);
        Assert.Equal(Expected(240, [.. Front(3), (20, data[440..450])]), payloads[3]);
    }

    // SyncRgbData: (byte)(interval * 100.0 % 100.0) truncates the hundredths.
    [Fact]
    public void EncodeEffectPayloads_TakesTheRetimedInterval() {
        var effect = new WirelessSavedEffect(B(1), B(1, 2, 3, 4), 1, 0, 0, 5.0, 0).WithInterval(300.129);

        byte[] header = WirelessProtocol.EncodeEffectPayloads(GroupMac, MasterMac, effect)[0];

        Assert.Equal(B(0x01, 0x2C, 12), header[32..35]);
    }

    // RFController.SetAioPumpSpeed and SetH2SAioPumpSpeed, including the first branch winning at a boundary.
    [Theory]
    [InlineData(10, 1000, 1500)]
    [InlineData(10, 1600, 1500)]
    [InlineData(10, 1650, 1417)]
    [InlineData(10, 1720, 1300)]
    [InlineData(10, 1800, 1140)]
    [InlineData(10, 1870, 1000)]
    [InlineData(10, 1935, 921)]
    [InlineData(10, 2000, 841)]
    [InlineData(10, 2150, 540)]
    [InlineData(10, 2310, 222)]
    [InlineData(10, 2400, 60)]
    [InlineData(10, 2450, 35)]
    [InlineData(10, 2500, 10)]
    [InlineData(10, 9000, 10)]
    [InlineData(11, 1000, 1590)]
    [InlineData(11, 1600, 1590)]
    [InlineData(11, 1700, 1495)]
    [InlineData(11, 1800, 1400)]
    [InlineData(11, 1900, 1300)]
    [InlineData(11, 2000, 1200)]
    [InlineData(11, 2100, 1100)]
    [InlineData(11, 2300, 900)]
    [InlineData(11, 2500, 700)]
    [InlineData(11, 2600, 600)]
    [InlineData(11, 2700, 469)]
    [InlineData(11, 2900, 210)]
    [InlineData(11, 3100, 45)]
    [InlineData(11, 3200, 0)]
    [InlineData(11, 9000, 0)]
    public void PumpTimerFromRpm_FollowsLConnectsTables(int deviceType, int rpm, int expected)
        => Assert.Equal(expected, WirelessProtocol.PumpTimerFromRpm(rpm, (byte)deviceType));

    [Theory]
    [InlineData(10, 0, 1600)]
    [InlineData(10, -5, 1600)]
    [InlineData(10, 100, 2450)] // the top of L-Connect's first-generation pump curves
    [InlineData(10, 50, 2025)]
    [InlineData(11, 100, 3200)]
    [InlineData(11, 25, 2000)]
    [InlineData(11, 150, 3200)]
    public void PumpRpmFromDuty_SpansTheBlocksRange(int deviceType, int duty, int expected)
        => Assert.Equal(expected, WirelessProtocol.PumpRpmFromDuty(duty, (byte)deviceType));

    // LWirelessController.getTemperatureDuty: a zero speed is 5% with no floor, anything else
    // max(floor, duty); startWriteFanSpeed maps it with NumberHelper.Map(0-100 -> 0-255) and truncates.
    [Theory]
    [InlineData(0, 14, 12)]
    [InlineData(-3, 14, 12)]
    [InlineData(1, 14, 35)]
    [InlineData(13, 10, 33)]
    [InlineData(50, 14, 127)]
    [InlineData(60, 10, 153)]
    [InlineData(100, 14, 255)]
    [InlineData(150, 14, 255)]
    public void FanPwm_IdlesAtFiveThenFloorsAndMaps(int duty, int floor, int expected)
        => Assert.Equal((byte)expected, WirelessProtocol.FanPwm(duty, floor));

    [Fact]
    public void FanPwm_IsTheTruncatedMapForEveryDuty() {
        for (int duty = 10; duty <= 100; duty++) {
            Assert.Equal((byte)(duty * 255 / 100), WirelessProtocol.FanPwm(duty, 10));
        }
    }

    // LWirelessController.calculateDuty (5 at zero) and startWriteCaseSpeed (floor 11).
    [Theory]
    [InlineData(0, 12)]
    [InlineData(3, 28)]
    [InlineData(50, 127)]
    [InlineData(100, 255)]
    public void CasePwm_IdlesAtFiveAndFloorsAtEleven(int duty, int expected)
        => Assert.Equal((byte)expected, WirelessProtocol.CasePwm(duty));

    // LWirelessController.convertFanType (slot 0's family and size, any slot's BindLcd from
    // RfDevice.InitAttr) and startWriteFanSpeed's floor per LWirelessFanType.
    [Theory]
    [InlineData(20, 0, 14)]  // SLV3Fan120LED
    [InlineData(24, 0, 14)]  // SLV3Fan120LCD
    [InlineData(28, 0, 11)]  // TLV2Fan120LED
    [InlineData(29, 0, 11)]  // TLV2FanReverse120LED
    [InlineData(30, 0, 11)]  // TLV2Fan140LED
    [InlineData(27, 0, 10)]  // 27 is TL V2 140 reverse with BindLcd: TLV2Fan140LCD
    [InlineData(32, 0, 10)]  // TLV2Fan120LCD
    [InlineData(35, 0, 10)]  // TLV2Fan140LCD
    [InlineData(28, 23, 10)] // a TL V2 group with an SL V3 LCD fan in it is an LCD group
    [InlineData(28, 26, 10)]
    [InlineData(28, 33, 10)]
    [InlineData(28, 22, 11)]
    [InlineData(28, 51, 10)] // a TL V2 group with a TL FLEX LCD fan in it is an LCD group too
    [InlineData(28, 48, 10)]
    [InlineData(28, 53, 11)] // a TL FLEX LED fan is not
    [InlineData(36, 0, 11)]  // SLINFWFan120LED
    [InlineData(39, 0, 11)]  // SLINFWFan140LED
    [InlineData(40, 0, 10)]  // RL120: None
    [InlineData(41, 0, 10)]  // CL: None
    [InlineData(43, 0, 10)]  // SLINFFlexFan120LCD
    [InlineData(45, 0, 10)]  // SLINFFlexFan120LED
    [InlineData(50, 0, 10)]  // SLINFFlexFan140LED
    [InlineData(51, 0, 11)]  // TLFlexFan120LED (InitAttr sizes every TL FLEX code as 120)
    [InlineData(58, 0, 11)]  // TLFlexFanReverse120LED
    [InlineData(59, 0, 14)]  // SLV4Fan120LED
    [InlineData(60, 0, 14)]  // SLV4FanReverse120LED
    [InlineData(61, 0, 10)]  // SL V4 140: convertFanType has no 140 branch, None
    [InlineData(62, 0, 10)]
    [InlineData(63, 0, 8)]   // P28V2Fan120
    [InlineData(126, 0, 10)] // CL V2: DevSize stays 0, None
    [InlineData(127, 0, 10)]
    [InlineData(0, 20, 10)]  // an empty first slot: ALL, None
    public void GroupDutyFloor_IsPickedFromTheFirstSlotLikeConvertFanType(int first, int second, int expected)
        => Assert.Equal(expected, WirelessProtocol.GroupDutyFloor(Group(first, second, 0, 0)));

    // getTemperatureDuty: a curve speed of 0 is 1 for P28V2Fan120 and 5 for everything else.
    [Theory]
    [InlineData(63, 1)]
    [InlineData(20, 5)]
    [InlineData(51, 5)]
    [InlineData(0, 5)]
    public void GroupIdleDuty_IsOneForP28V2AndFiveForTheRest(int first, int expected)
        => Assert.Equal(expected, WirelessProtocol.GroupIdleDuty(Group(first, 0, 0, 0)));

    [Fact]
    public void GroupIdleDuty_IsFiveForAnyOtherKindOfDevice()
        => Assert.Equal(5, WirelessProtocol.GroupIdleDuty(
            Decode(new FakeWirelessRecord(GroupMac, MasterMac) { DeviceType = 10, FanTypes = B(63, 0, 0, 0) })));

    // A P28 V2 group at 0 is sent 1%, which NumberHelper.Map truncates to PWM 2.
    [Theory]
    [InlineData(0, 1, 2)]
    [InlineData(0, 5, 12)]
    [InlineData(4, 1, 20)]
    public void FanPwm_TakesTheGroupsIdleDuty(int duty, int idle, int expected)
        => Assert.Equal((byte)expected, WirelessProtocol.FanPwm(duty, 8, idle));

    // A water block's fans are always CLFan120LED (addSettingDevice); V150 and unrecognised types are None.
    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(66)]
    [InlineData(50)]
    public void GroupDutyFloor_IsTenForEveryOtherDevice(int deviceType)
        => Assert.Equal(10, WirelessProtocol.GroupDutyFloor(
            Decode(new FakeWirelessRecord(GroupMac, MasterMac) { DeviceType = (byte)deviceType, FanTypes = B(20, 20, 0, 0) })));

    // MasterDevice.NeedSyncPwm steps only when RecType[0] == CLV1 or CLV2, which InitAttr sets for a fan group.
    [Fact]
    public void IsClGroup_IsAFanGroupWhoseFirstSlotIsClOrClV2() {
        Assert.True(WirelessProtocol.IsClGroup(Group(41, 0, 0, 0)));
        Assert.True(WirelessProtocol.IsClGroup(Group(42, 0, 0, 0)));
        Assert.True(WirelessProtocol.IsClGroup(Group(126, 0, 0, 0)));
        Assert.True(WirelessProtocol.IsClGroup(Group(127, 0, 0, 0)));
        Assert.False(WirelessProtocol.IsClGroup(Group(20, 41, 0, 0)));
        Assert.False(WirelessProtocol.IsClGroup(Group(0, 126, 0, 0)));
        Assert.False(WirelessProtocol.IsClGroup(
            Decode(new FakeWirelessRecord(GroupMac, MasterMac) { DeviceType = 10, FanTypes = B(41, 0, 0, 0) })));
    }

    // NeedSyncPwm: 153 and 154 -> 152, 155 -> 156.
    [Theory]
    [InlineData(152, 152)]
    [InlineData(153, 152)]
    [InlineData(154, 152)]
    [InlineData(155, 156)]
    [InlineData(156, 156)]
    public void StepClReserved_StepsAroundTheReservedValues(int pwm, int expected)
        => Assert.Equal((byte)expected, WirelessProtocol.StepClReserved((byte)pwm));

    // RfDevice.InitAttr: 20-26 SLV3 (num < 27), 27-35 TLV2 (num < 36), 36-39 SLINF, 40 RL120,
    // 41-42 CLV1, 43-50 SLINFV3, 51-58 TLV3, 59-62 SLV4, 63 P28V2, 126-127 CLV2.
    [Theory]
    [InlineData(20, "SlV3")]
    [InlineData(26, "SlV3")]
    [InlineData(27, "TlV2")]
    [InlineData(35, "TlV2")]
    [InlineData(36, "SlInfinity")]
    [InlineData(39, "SlInfinity")]
    [InlineData(41, "Cl")]
    [InlineData(42, "Cl")]
    [InlineData(43, "SlInfinityFlex")]
    [InlineData(50, "SlInfinityFlex")]
    [InlineData(51, "TlFlex")]
    [InlineData(58, "TlFlex")]
    [InlineData(59, "SlV4")]
    [InlineData(62, "SlV4")]
    [InlineData(63, "P28V2")]
    [InlineData(126, "ClV2")]
    [InlineData(127, "ClV2")]
    [InlineData(0, "Unknown")]
    [InlineData(40, "Unknown")]
    [InlineData(19, "Unknown")]
    [InlineData(64, "Unknown")]
    [InlineData(125, "Unknown")]
    [InlineData(128, "Unknown")]
    public void FamilyOf_MapsTheTypeCodeRanges(int fanType, string expected)
        => Assert.Equal(expected, WirelessProtocol.FamilyOf(fanType).ToString());

    // RFController.UpdateSensorDataByWiredLess writes an entry for a bound group with RecType[0]
    // TLV3 or SLINFV3 and BindLcd[0] (InitAttr: 43, 44, 47, 48, 51, 52, 55, 56).
    [Theory]
    [InlineData(43, true)]
    [InlineData(44, true)]
    [InlineData(45, false)]
    [InlineData(46, false)]
    [InlineData(47, true)]
    [InlineData(48, true)]
    [InlineData(49, false)]
    [InlineData(50, false)]
    [InlineData(51, true)]
    [InlineData(52, true)]
    [InlineData(53, false)]
    [InlineData(54, false)]
    [InlineData(55, true)]
    [InlineData(56, true)]
    [InlineData(57, false)]
    [InlineData(58, false)]
    [InlineData(24, false)] // an SL V3 LCD fan takes its screen over USB, not from the table
    [InlineData(0, false)]
    public void HasScreens_IsAFanGroupWhoseFirstSlotIsAnLcdFlexFan(int first, bool expected) {
        Assert.Equal(expected, WirelessProtocol.HasScreens(Group(first, 0, 0, 0)));
        Assert.False(WirelessProtocol.HasScreens(Group(0, first, 0, 0)));
        Assert.False(WirelessProtocol.HasScreens(
            Decode(new FakeWirelessRecord(GroupMac, MasterMac) { DeviceType = 10, FanTypes = B(first, 0, 0, 0) })));
    }

    // UpdateSensorSettingByWiredLess: FanIndex = FanNum - FanIndex - 1 when isINFRightAttach, or
    // when the group is neither SLINFV3 nor SLINF; a left-attached SL-INF group keeps its order.
    [Theory]
    [InlineData(43, 2, true)]
    [InlineData(43, 12, false)]
    [InlineData(51, 2, false)]
    [InlineData(51, 12, false)]
    [InlineData(36, 2, true)]
    [InlineData(36, 12, false)]
    public void ScreensNumberedInReverse_ExceptOnALeftAttachedSlInfinityGroup(int first, int fanCountByte, bool inOrder)
        => Assert.Equal(!inOrder, WirelessProtocol.ScreensNumberedInReverse(AttachedGroup(fanCountByte, first, first, 0, 0)));

    // WinUsb.GetRFSender / GetRFReciver: 0416:8040 / 8041 and 1A86:E304 / E305.
    [Theory]
    [InlineData(0x0416, 0x8040, true, true, false)]
    [InlineData(0x0416, 0x8041, true, false, true)]
    [InlineData(0x1A86, 0xE304, true, true, false)]
    [InlineData(0x1A86, 0xE305, true, false, true)]
    [InlineData(0x0416, 0x7372, false, false, false)]
    [InlineData(0x1A86, 0x8040, false, false, false)]
    public void IsDongle_RecognisesBothVendorPairs(int vendor, int product, bool dongle, bool transmitter, bool receiver) {
        Assert.Equal(dongle, WirelessProtocol.IsDongle(vendor, product));
        Assert.Equal(transmitter, WirelessProtocol.IsTransmitter(vendor, product));
        Assert.Equal(receiver, WirelessProtocol.IsReceiver(vendor, product));
    }

    [Fact]
    public void EveryEncoder_RejectsMissingOrMisSizedInput() {
        byte[] pwm = new byte[WirelessProtocol.SlotsPerGroup];
        byte[] parameters = new byte[WirelessProtocol.AioParameterLength];
        byte[] shortMac = new byte[WirelessProtocol.MacLength - 1];
        var effect = new WirelessSavedEffect(B(1), B(1, 2, 3, 4), 1, 1, 1, 1, 1);

        Assert.Throws<ArgumentNullException>(() => WirelessProtocol.EncodeSpeedPayload(null!, MasterMac, 1, 8, 1, pwm));
        Assert.Throws<ArgumentNullException>(() => WirelessProtocol.EncodeSpeedPayload(GroupMac, null!, 1, 8, 1, pwm));
        Assert.Throws<ArgumentNullException>(() => WirelessProtocol.EncodeSpeedPayload(GroupMac, MasterMac, 1, 8, 1, null!));
        Assert.Throws<ArgumentException>(() => WirelessProtocol.EncodeSpeedPayload(shortMac, MasterMac, 1, 8, 1, pwm));
        Assert.Throws<ArgumentException>(() => WirelessProtocol.EncodeSpeedPayload(GroupMac, shortMac, 1, 8, 1, pwm));
        Assert.Throws<ArgumentException>(() => WirelessProtocol.EncodeSpeedPayload(GroupMac, MasterMac, 1, 8, 1, new byte[3]));
        Assert.Throws<ArgumentNullException>(() => WirelessProtocol.EncodeTransmitPackets(8, 1, null!));
        Assert.Throws<ArgumentNullException>(() => WirelessProtocol.EncodeAioPayload(GroupMac, MasterMac, 1, 8, null!));
        Assert.Throws<ArgumentException>(() => WirelessProtocol.EncodeAioPayload(GroupMac, MasterMac, 1, 8, new byte[31]));
        Assert.Throws<ArgumentNullException>(() => WirelessProtocol.EncodeAioPayload(null!, MasterMac, 1, 8, parameters));
        Assert.Throws<ArgumentNullException>(() => WirelessProtocol.EncodeAioPayload(GroupMac, null!, 1, 8, parameters));
        Assert.Throws<ArgumentException>(() => WirelessProtocol.EncodeAioPayload(shortMac, MasterMac, 1, 8, parameters));
        Assert.Throws<ArgumentNullException>(() => WirelessProtocol.EncodeEffectPayloads(GroupMac, MasterMac, null!));
        Assert.Throws<ArgumentNullException>(() => WirelessProtocol.EncodeEffectPayloads(null!, MasterMac, effect));
        Assert.Throws<ArgumentNullException>(() => WirelessProtocol.EncodeClockPayload(null!, DateTime.Now, new WirelessScreenTable()));
        Assert.Throws<ArgumentException>(() => WirelessProtocol.EncodeSaveConfigurationPayload(shortMac));
        Assert.Throws<ArgumentNullException>(() => WirelessProtocol.GroupDutyFloor(null!));
        Assert.Throws<ArgumentNullException>(() => WirelessProtocol.GroupIdleDuty(null!));
        Assert.Throws<ArgumentNullException>(() => WirelessProtocol.IsClGroup(null!));
        Assert.Throws<ArgumentNullException>(() => WirelessProtocol.HasScreens(null!));
        Assert.Throws<ArgumentNullException>(() => WirelessProtocol.ScreensNumberedInReverse(null!));
    }
}
