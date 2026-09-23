using System;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Tests.Fakes;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

/// <summary>The per-device state L-Connect's RfDevice keeps, as the table uses it.</summary>
public sealed class WirelessDeviceTests {
    private static readonly byte[] Master = FakeWirelessRig.MasterMac;
    private static readonly byte[] Mac = { 0xA0, 0, 0, 0, 0, 1 };

    private static WirelessDeviceRecord Record(byte receiver = 3, params byte[] pwm)
        => FakeWirelessRecords.Decode(new FakeWirelessRecord(Mac, Master) { Receiver = receiver, DeviceType = 65, Pwm = pwm });

    // RfDevice(): live = max_live_time (30).
    [Fact]
    public void Constructor_StartsTheCountdownAtThirty() {
        var device = new WirelessDevice(Record(3, 1, 2, 3, 4));

        Assert.Equal(30, device.Live);
        Assert.Equal(WirelessDevice.MaximumMissedReads, device.Live);
        Assert.Equal(Mac, device.Mac);
        Assert.Equal("a00000000001", device.MacText);
        Assert.Equal(WirelessDeviceKind.CaseFans, device.Kind);
        Assert.False(device.IsBound);
        Assert.Equal(new byte[4], device.TargetPwm);
        Assert.Equal(new bool[4], device.DrivenSlots);
    }

    // MasterDevice.FindDev: live = max_live_time on every record.
    [Fact]
    public void Update_TakesTheRecordAndRestartsTheCountdown() {
        var device = new WirelessDevice(Record(3, 1, 2, 3, 4));
        device.Live = 2;
        WirelessDeviceRecord next = Record(5, 9, 9, 9, 9);

        device.Update(next);

        Assert.Same(next, device.Record);
        Assert.Equal(30, device.Live);
    }

    // RefreshList, a new device naming this master: bind_to_master, target_rx_type = rx_type,
    // target_fans_pwm = fans_pwm.
    [Fact]
    public void TakeAsBound_RemembersTheSlotAndStartsTheTargetsFromTheReport() {
        var device = new WirelessDevice(Record(7, 10, 20, 30, 40));

        device.TakeAsBound();

        Assert.True(device.IsBound);
        Assert.Equal(7, device.TargetReceiverType);
        Assert.Equal(new byte[] { 10, 20, 30, 40 }, device.TargetPwm);
    }

    [Fact]
    public void TakeAsBound_FromALockedList_UsesTheSavedSlotAndTargets() {
        var device = new WirelessDevice(Record(7, 10, 20, 30, 40));

        device.TakeAsBound(2, new byte[] { 90, 91, 92, 93 });

        Assert.True(device.IsBound);
        Assert.Equal(2, device.TargetReceiverType);
        Assert.Equal(new byte[] { 90, 91, 92, 93 }, device.TargetPwm);
        Assert.Throws<ArgumentNullException>(() => device.TakeAsBound(2, null!));
    }

    [Fact]
    public void WirelessLockedDevice_RejectsMissingOrMisshapenParts() {
        WirelessDeviceRecord record = Record(1, 0, 0, 0, 0);

        Assert.Throws<ArgumentNullException>(() => new WirelessLockedDevice(null!, 1, new byte[4]));
        Assert.Throws<ArgumentNullException>(() => new WirelessLockedDevice(record, 1, null!));
        Assert.Throws<ArgumentException>(() => new WirelessLockedDevice(record, 1, new byte[3]));
    }

    [Fact]
    public void Constructor_AndUpdate_RejectAMissingRecord() {
        Assert.Throws<ArgumentNullException>(() => new WirelessDevice(null!));
        Assert.Throws<ArgumentNullException>(() => new WirelessDevice(Record(1, 0, 0, 0, 0)).Update(null!));
    }
}
