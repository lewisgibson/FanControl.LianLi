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

    // RFController raises targe_cmd_seq for every command it queues, one sequence per device, and
    // the record's byte 40 is what acknowledges it: a round takes the next sequence after the last
    // round's (after what the record reports, for the first), and never the one the record reports
    // now, or the round would count as acknowledged before it went out.
    [Fact]
    public void BeginCommand_TakesTheDevicesNextSequence_AndSkipsTheOneTheRecordReports() {
        var device = new WirelessDevice(FakeWirelessRecords.Decode(new FakeWirelessRecord(Mac, Master) { Sequence = 5 }));
        var first = new WirelessDeviceCommand();
        var second = new WirelessDeviceCommand();

        Assert.Equal(new byte[] { 6 }, device.BeginCommand(first, Send));
        Assert.Equal((byte?)6, first.Sequence);
        Assert.Equal(6, device.TargetSequence);
        Assert.Equal(1, first.Sends);
        Assert.Equal(1, first.Rounds);
        Assert.Same(first, device.SendingCommand);

        // The device reports 6: the round is acknowledged and the sequence free.
        device.Update(FakeWirelessRecords.Decode(new FakeWirelessRecord(Mac, Master) { Sequence = 6 }));
        Assert.Null(device.NextSend());
        Assert.Null(first.Sequence);
        Assert.Null(device.SendingCommand);
        Assert.Equal(WirelessRoundEnd.Acknowledged, first.Ended);

        // The device reports 7 meanwhile, which is what the next round would have taken.
        device.Update(FakeWirelessRecords.Decode(new FakeWirelessRecord(Mac, Master) { Sequence = 7 }));
        Assert.Equal(new byte[] { 8 }, device.BeginCommand(second, Send));
        Assert.Same(second, device.SendingCommand);
    }

    // The sequence wraps as L-Connect wraps it, 254 to 1, and 0 (a device that never acknowledged
    // anything) starts at 1.
    [Fact]
    public void BeginCommand_WrapsTheSequenceAsLConnectDoes() {
        var device = new WirelessDevice(FakeWirelessRecords.Decode(new FakeWirelessRecord(Mac, Master) { Sequence = 254 }));

        Assert.Equal(new byte[] { 1 }, device.BeginCommand(new WirelessDeviceCommand(), Send));
    }

    // SyncControlInfo's ten sends: the round's payload goes out under its sequence until the tenth,
    // and the pass after that ends the round with its sends out. The device does this itself, from
    // the payload it was handed, so nothing else has to keep calling for the round to end.
    [Fact]
    public void NextSend_SendsARoundTenTimes_ThenEndsItExhausted() {
        var device = new WirelessDevice(Record(1, 0, 0, 0, 0));
        WirelessDeviceCommand command = device.ThemeSwitch;
        Assert.Null(device.NextSend()); // no round under way

        Assert.Equal(new byte[] { 1 }, device.BeginCommand(command, Send));
        for (int send = 2; send <= WirelessDeviceCommand.SendsPerRound; send++) {
            Assert.Equal(new byte[] { 1 }, device.NextSend());
            Assert.Equal(send, command.Sends);
        }

        Assert.Null(device.NextSend());
        Assert.Equal(WirelessRoundEnd.Exhausted, command.Ended);
        Assert.Null(command.Sequence);
        Assert.Null(device.SendingCommand);
        Assert.True(command.IsUnderWay); // the end is owed to the pass that services the command
        Assert.Null(device.NextSend());
        Assert.Equal(10, command.Sends);
    }

    // A round begun after an unserviced end starts clean.
    [Fact]
    public void BeginCommand_StartsARoundWithNoEndOwed() {
        var device = new WirelessDevice(Record(1, 0, 0, 0, 0));
        WirelessDeviceCommand command = device.ScreenMode;
        _ = device.BeginCommand(command, Send);
        device.Update(FakeWirelessRecords.Decode(new FakeWirelessRecord(Mac, Master) { Sequence = 1 }));
        Assert.Null(device.NextSend());
        Assert.NotNull(command.Ended);

        Assert.Equal(new byte[] { 2 }, device.BeginCommand(command, Send));

        Assert.Null(command.Ended);
        Assert.Equal(2, command.Rounds);
        Assert.Equal(1, command.Sends);
    }

    // The clock goes out every second carrying the same entry: the first broadcast's time stands
    // for as long as the entry, its slot and the transmitter lifetime and handle it went out under
    // are the same, and a change of any of them is published afresh, from that broadcast. A device
    // or dongle back after a loss starts the group's commands over, and the publication with them.
    [Fact]
    public void PublishScreenEntry_KeepsTheFirstTimeOfTheSameEntry_AndStartsOverForAnother() {
        var device = new WirelessDevice(Record(1, 0, 0, 0, 0));
        var entry = new byte[12];
        entry[0] = 5;
        var first = new DateTime(2026, 9, 30, 20, 0, 0, DateTimeKind.Utc);
        Assert.Null(device.ScreenEntryPublication);

        device.PublishScreenEntry(1, entry, 0, 0, first);
        device.PublishScreenEntry(1, entry, 0, 0, first.AddSeconds(1));
        Assert.Equal(first, PublishedAt(device));
        device.PublishScreenEntry(2, entry, 0, 0, first.AddSeconds(2)); // another slot
        Assert.Equal(first.AddSeconds(2), PublishedAt(device));
        device.PublishScreenEntry(2, entry, 1, 0, first.AddSeconds(3)); // a transmitter lifetime counted since
        Assert.Equal(first.AddSeconds(3), PublishedAt(device));
        device.PublishScreenEntry(2, entry, 1, 1, first.AddSeconds(4)); // the transmitter's handle lost since
        Assert.Equal(first.AddSeconds(4), PublishedAt(device));
        entry[1] = 6;
        device.PublishScreenEntry(2, entry, 1, 1, first.AddSeconds(5)); // the entry rewritten
        Assert.Equal(first.AddSeconds(5), PublishedAt(device));
        device.PublishScreenEntry(2, entry, 1, 1, first.AddSeconds(6));
        Assert.Equal(first.AddSeconds(5), PublishedAt(device));

        device.ReapplySavedLook();

        Assert.Null(device.ScreenEntryPublication);
    }

    // A clock broadcast that completes without the group's entry withdraws the publication: the
    // next broadcast that carries the entry publishes it afresh, from then, the same entry or not.
    [Fact]
    public void WithdrawScreenEntry_LeavesNoPublication_AndTheNextIsFresh() {
        var device = new WirelessDevice(Record(1, 0, 0, 0, 0));
        var entry = new byte[12];
        var first = new DateTime(2026, 9, 30, 20, 0, 0, DateTimeKind.Utc);
        device.PublishScreenEntry(1, entry, 0, 0, first);

        device.WithdrawScreenEntry();

        Assert.Null(device.ScreenEntryPublication);
        device.PublishScreenEntry(1, entry, 0, 0, first.AddSeconds(2));
        Assert.Equal(first.AddSeconds(2), PublishedAt(device));
    }

    private static DateTime PublishedAt(WirelessDevice device) {
        WirelessScreenEntryPublication? published = device.ScreenEntryPublication;
        Assert.NotNull(published);
        return published.CompletedUtc;
    }

    [Fact]
    public void ReapplySavedLook_FreesTheDevicesSequence_ForTheNextRound() {
        var device = new WirelessDevice(Record(1, 0, 0, 0, 0));
        WirelessDeviceCommand command = device.LightingSync;
        _ = device.BeginCommand(command, Send);

        device.ScreenColours.Done = true;
        device.ColouredScreens = 2;
        device.ReapplySavedLook();

        Assert.Null(device.SendingCommand);
        Assert.Null(device.NextSend());
        Assert.Null(command.Sequence);
        Assert.False(device.ScreenColours.Done);
        Assert.Equal(0, device.ColouredScreens);
        Assert.Equal(1, device.TargetSequence); // the next round still takes a fresh one
        Assert.Equal(new byte[] { 2 }, device.BeginCommand(command, Send));
    }

    [Fact]
    public void BeginCommand_RejectsAMissingCommandOrPayload() {
        var device = new WirelessDevice(Record(1, 0, 0, 0, 0));

        Assert.Throws<ArgumentNullException>(() => device.BeginCommand(null!, Send));
        Assert.Throws<ArgumentNullException>(() => device.BeginCommand(new WirelessDeviceCommand(), null!));
    }

    // A round's send, as a test sees it: the sequence it carries.
    private static byte[] Send(byte sequence) => new[] { sequence };

    [Fact]
    public void Constructor_AndUpdate_RejectAMissingRecord() {
        Assert.Throws<ArgumentNullException>(() => new WirelessDevice(null!));
        Assert.Throws<ArgumentNullException>(() => new WirelessDevice(Record(1, 0, 0, 0, 0)).Update(null!));
    }
}
