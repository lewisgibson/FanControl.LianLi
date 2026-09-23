using System;
using FanControl.LianLi.Protocol;
using Xunit;

namespace FanControl.LianLi.Tests.Protocol;

/// <summary>
/// The decoded device record: it owns copies of its bytes, validates their lengths, maps its type
/// byte the way RfDevice.InitAttr does, and answers the questions the controller asks of it.
/// </summary>
public sealed class WirelessDeviceRecordTests {
    private static readonly byte[] Mac = { 0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6 };
    private static readonly byte[] Master = { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
    private static readonly byte[] Effect = { 1, 2, 3, 4 };

    private static WirelessDeviceRecord Record(
        byte[]? mac = null, byte[]? master = null, byte deviceType = 0, int fanCount = 3, byte[]? effect = null,
        byte[]? fanTypes = null, int[]? rpm = null, byte[]? pwm = null)
        => new WirelessDeviceRecord(
            mac ?? Mac,
            master ?? Master,
            channel: 8,
            receiverType: 1,
            clockMilliseconds: 1234,
            deviceType: deviceType,
            fanCount: fanCount,
            effectIndex: effect ?? Effect,
            fanTypes: fanTypes ?? new byte[] { 20, 20, 20, 0 },
            rpm: rpm ?? new[] { 900, 910, 920, 0 },
            pwm: pwm ?? new byte[] { 50, 50, 50, 0 },
            commandSequence: 7);

    [Fact]
    public void Constructor_CopiesEveryArray() {
        byte[] mac = (byte[])Mac.Clone();
        byte[] master = (byte[])Master.Clone();
        byte[] effect = (byte[])Effect.Clone();
        byte[] fanTypes = { 20, 20, 20, 0 };
        int[] rpm = { 1, 2, 3, 4 };
        byte[] pwm = { 5, 6, 7, 8 };

        WirelessDeviceRecord record = Record(mac, master, effect: effect, fanTypes: fanTypes, rpm: rpm, pwm: pwm);
        mac[0] = master[0] = effect[0] = fanTypes[0] = pwm[0] = 0;
        rpm[0] = 0;

        Assert.Equal(Mac, record.Mac);
        Assert.Equal(Master, record.MasterMac);
        Assert.Equal(Effect, record.EffectIndex);
        Assert.Equal(new byte[] { 20, 20, 20, 0 }, record.FanTypes);
        Assert.Equal(new[] { 1, 2, 3, 4 }, record.Rpm);
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, record.Pwm);
        Assert.Equal("a1b2c3d4e5f6", record.MacText);
        Assert.Equal(8, record.Channel);
        Assert.Equal(1, record.ReceiverType);
        Assert.Equal(1234, record.ClockMilliseconds);
        Assert.Equal(7, record.CommandSequence);
    }

    [Fact]
    public void Constructor_RejectsMissingOrMisSizedValues() {
        Assert.Throws<ArgumentNullException>(
            () => new WirelessDeviceRecord(null!, Master, 0, 0, 0, 0, 0, Effect, new byte[4], new int[4], new byte[4], 0));
        Assert.Throws<ArgumentException>(() => Record(mac: new byte[5]));
        Assert.Throws<ArgumentException>(() => Record(master: new byte[7]));
        Assert.Throws<ArgumentException>(() => Record(effect: new byte[3]));
        Assert.Throws<ArgumentException>(() => Record(fanTypes: new byte[3]));
        Assert.Throws<ArgumentException>(() => Record(pwm: new byte[5]));
        Assert.Throws<ArgumentException>(() => Record(rpm: new int[3]));
        Assert.Throws<ArgumentNullException>(
            () => new WirelessDeviceRecord(Mac, Master, 0, 0, 0, 0, 0, Effect, new byte[4], null!, new byte[4], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Record(fanCount: 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Record(fanCount: -1));
    }

    // RfDevice.InitAttr: 0 fans, 1-9 Strimer, 10 WaterBlock, 11 WaterBlock2, 65 LC217, 66 V150;
    // 255 goes to masterList; anything else stays DevTypes.ALL.
    [Theory]
    [InlineData(0, "FanGroup")]
    [InlineData(1, "Strimer")]
    [InlineData(9, "Strimer")]
    [InlineData(10, "WaterBlock")]
    [InlineData(11, "WaterBlock")]
    [InlineData(12, "Unrecognised")]
    [InlineData(64, "Unrecognised")]
    [InlineData(65, "CaseFans")]
    [InlineData(66, "V150")]
    [InlineData(67, "Unrecognised")]
    [InlineData(255, "Master")]
    public void Kind_IsInitAttrsMapping(int deviceType, string kind) {
        WirelessDeviceRecord record = Record(deviceType: (byte)deviceType);

        Assert.Equal(kind, record.Kind.ToString());
        Assert.Equal(deviceType, record.DeviceType);
    }

    [Fact]
    public void IsBoundTo_MatchesOnlyTheSameMaster() {
        WirelessDeviceRecord record = Record();

        Assert.True(record.IsBoundTo(Master));
        Assert.False(record.IsBoundTo(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x67 }));
        Assert.False(record.IsBoundTo(new byte[] { 0x11, 0x22 }));
        Assert.Throws<ArgumentNullException>(() => record.IsBoundTo(null!));
    }

    [Fact]
    public void IsUnbound_IsTheAllZeroMaster() {
        Assert.False(Record().IsUnbound);
        Assert.False(Record(master: new byte[] { 0, 0, 0, 0, 0, 1 }).IsUnbound);
        Assert.True(Record(master: new byte[6]).IsUnbound);
    }

    [Fact]
    public void IsRunningEffect_MatchesOnlyTheSameIdentity() {
        WirelessDeviceRecord record = Record();

        Assert.True(record.IsRunningEffect(Effect));
        Assert.False(record.IsRunningEffect(new byte[] { 1, 2, 3, 5 }));
        Assert.False(record.IsRunningEffect(new byte[] { 1, 2, 3 }));
        Assert.Throws<ArgumentNullException>(() => record.IsRunningEffect(null!));
    }

    [Fact]
    public void FormatMac_WritesLowercaseHexWithoutSeparators() {
        Assert.Equal("0a0b0c0d0e0f", WirelessDeviceRecord.FormatMac(new byte[] { 10, 11, 12, 13, 14, 15 }));
        Assert.Throws<ArgumentNullException>(() => WirelessDeviceRecord.FormatMac(null!));
    }

    // RefreshList under lock_list keeps dev_type, and fans_type except on types 10, 11, 65 and 66.
    [Fact]
    public void WithLockedIdentity_KeepsTheSavedTypeAndFanTypes_AndTakesEverythingElse() {
        WirelessDeviceRecord saved = Record(deviceType: 0, fanTypes: new byte[] { 36, 36, 0, 0 });
        WirelessDeviceRecord heard = Record(deviceType: 3, fanTypes: new byte[] { 20, 20, 20, 0 }, rpm: new[] { 1, 2, 3, 4 });

        WirelessDeviceRecord locked = heard.WithLockedIdentity(saved);

        Assert.Equal(0, locked.DeviceType);
        Assert.Equal(new byte[] { 36, 36, 0, 0 }, locked.FanTypes);
        Assert.Equal(new[] { 1, 2, 3, 4 }, locked.Rpm);
        Assert.Equal(7, locked.CommandSequence);
        Assert.Throws<ArgumentNullException>(() => heard.WithLockedIdentity(null!));
    }
}
