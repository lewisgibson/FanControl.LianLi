using System;
using System.Linq;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Tests.Fakes;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

/// <summary>
/// The device table, read for read against MasterDevice.RefreshList: first-heard order, taking a
/// device as bound, the live countdown and which devices it drops, receiver slot conflicts and
/// ghosts, and the plugin's own lost mark.
/// </summary>
public sealed class WirelessDeviceTableTests {
    private static readonly byte[] Master = FakeWirelessRig.MasterMac;
    private static readonly byte[] Other = { 0x99, 0x99, 0x99, 0x99, 0x99, 0x99 };

    private static byte[] Mac(int last, byte first = 0xA0) => new byte[] { first, 0, 0, 0, 0, (byte)last };

    private static FakeWirelessRecord Rec(int last, byte[]? master = null, byte type = 0, byte receiver = 1, byte first = 0xA0)
        => new FakeWirelessRecord(Mac(last, first), master ?? Master) { DeviceType = type, Receiver = receiver, FanCountByte = 1, Pwm = new byte[] { 50, 50, 50, 50 } };

    private static (WirelessDeviceTable Table, FakeLogger Log) NewTable() {
        var log = new FakeLogger();
        return (new WirelessDeviceTable(3, log), log);
    }

    private static void Read(WirelessDeviceTable table, params FakeWirelessRecord[] records)
        => table.Apply(FakeWirelessRecords.List(records), Master, 5000);

    // RefreshList appends a device the first time it is heard, whatever the receiver's order later.
    [Fact]
    public void Apply_KeepsDevicesInFirstHeardOrder() {
        var (table, _) = NewTable();

        Read(table, Rec(2), Rec(1));
        Read(table, Rec(1), Rec(3), Rec(2));

        Assert.Equal(new[] { "a00000000002", "a00000000001", "a00000000003" }, table.Devices.Select(d => d.MacText));
    }

    // A master's record (type 255) goes to masterList, never rfList.
    [Fact]
    public void Apply_LeavesMasterDonglesOffTheTable() {
        var (table, _) = NewTable();

        Read(table, Rec(1, new byte[6], type: 0xFF), Rec(2));

        Assert.Equal("a00000000002", Assert.Single(table.Devices).MacText);
        WirelessMaster master = Assert.Single(table.Masters);
        Assert.Equal("a00000000001", master.MacText);
        Assert.Equal(8, master.Channel);
    }

    // masterList: a master heard again takes its new channel and restarts its countdown; one unheard
    // for 30 reads is dropped, but not on a read that already dropped a device.
    [Fact]
    public void Apply_KeepsAMasterListWithTheSameCountdownAsDevices() {
        var (table, _) = NewTable();
        FakeWirelessRecord other = Rec(9, new byte[6], type: 0xFF);
        Read(table, other, Rec(1, Other));
        other.Channel = 12;
        Read(table, other, Rec(1, Other));
        Assert.Equal(12, Assert.Single(table.Masters).Channel);

        // The unbound device and the master count down together; the device goes first.
        for (int read = 0; read < WirelessDevice.MaximumMissedReads; read++) {
            Read(table, Rec(2, Other));
        }

        Assert.DoesNotContain(table.Devices, d => d.MacText == "a00000000001");
        Assert.Single(table.Masters);

        Read(table, Rec(2, Other));
        Assert.Empty(table.Masters);
    }

    [Fact]
    public void WirelessMaster_RejectsANullAddress()
        => Assert.Throws<ArgumentNullException>(() => new WirelessMaster(null!, 8));

    // RefreshList, flag2 and our master: bind_to_master, target_rx_type = rx_type, target pwm = reported.
    [Fact]
    public void Apply_TakesANewDeviceNamingThisMasterAsBound() {
        var (table, log) = NewTable();

        Read(table, Rec(1, receiver: 4), Rec(2, Other));

        WirelessDevice ours = table.Devices[0];
        Assert.True(ours.IsBound);
        Assert.Equal(4, ours.TargetReceiverType);
        Assert.Equal(new byte[] { 50, 50, 50, 50 }, ours.TargetPwm);
        Assert.False(table.Devices[1].IsBound);
        Assert.Contains("W3:a00000000001 bound to this master: type 0, 1 fan(s), receiver slot 4, channel 8", log.Messages);
    }

    // RefreshList's "reunbind": L-Connect unbinds a device that names this master after first being
    // heard unbound; the plugin never unbinds, so it takes it as bound.
    [Fact]
    public void Apply_TakesADeviceThatLaterNamesThisMasterAsBound() {
        var (table, log) = NewTable();
        Read(table, Rec(1, new byte[6]));

        Read(table, Rec(1, receiver: 6));

        Assert.True(table.Devices[0].IsBound);
        Assert.Equal(6, table.Devices[0].TargetReceiverType);
        Assert.Contains(log.Messages, m => m.Contains("(first heard unbound; L-Connect would unbind it, the plugin drives it)"));
    }

    // bind_to_master is only cleared by an unbind, so a device that moves to another master stays
    // bound in L-Connect's sense and keeps its place in the numbering.
    [Fact]
    public void Apply_ADeviceThatMovesToAnotherMasterStaysBound() {
        var (table, _) = NewTable();
        Read(table, Rec(1));

        Read(table, Rec(1, Other));

        Assert.True(table.Devices[0].IsBound);
        Assert.True(table.Devices[0].Record.IsBoundTo(Other));
    }

    // RefreshList: rfList.Where(bind_to_master).Count() >= 12 unbinds the new device.
    [Fact]
    public void Apply_RefusesTheTwelfthDeviceNamingThisMaster() {
        var (table, log) = NewTable();
        FakeWirelessRecord[] records = Enumerable.Range(1, 12).Select(i => Rec(i, receiver: (byte)i)).ToArray();

        Read(table, records);
        Read(table, records);

        Assert.Equal(11, table.Devices.Count(d => d.IsBound));
        WirelessDevice twelfth = table.Devices[11];
        Assert.True(twelfth.IsRefused);
        Assert.False(twelfth.IsBound);
        Assert.Single(log.Messages, m => m.Contains("W3:a0000000000c names this master but 11 devices already do"));
    }

    // A bound V150 counts down even while bound, so it can be dropped; the refused device is then
    // taken, on the next read that carries it.
    [Fact]
    public void Apply_TakesARefusedDevice_OnceABoundV150IsDroppedAndThereIsRoom() {
        var (table, log) = NewTable();
        FakeWirelessRecord v150 = Rec(1, type: 66, receiver: 1);
        FakeWirelessRecord[] others = Enumerable.Range(2, 11).Select(i => Rec(i, receiver: (byte)i)).ToArray();
        Read(table, others.Prepend(v150).ToArray());
        Assert.True(table.Devices[11].IsRefused);

        for (int read = 0; read <= WirelessDevice.MaximumMissedReads; read++) {
            Read(table, others);
        }

        WirelessDevice twelfth = table.Devices.Single(d => d.MacText == "a0000000000c");
        Assert.True(twelfth.IsBound);
        Assert.False(twelfth.IsRefused);
        Assert.Single(log.Messages, m => m.Contains("W3:a0000000000c names this master but 11 devices already do"));
        Assert.Contains(log.Messages, m => m.Contains("W3:a0000000000c bound to this master (refused earlier, now there is room)"));
    }

    // RefreshList: live-- for (!bind_to_master && not LC217) || V150, before the read; FindDev resets
    // it; the first device at zero is removed.
    [Fact]
    public void Apply_DropsAnUnboundDeviceAfterThirtyReadsUnheard() {
        var (table, _) = NewTable();
        Read(table, Rec(1, Other), Rec(2));

        for (int read = 0; read < 29; read++) {
            Read(table, Rec(2));
        }

        Assert.Equal(2, table.Devices.Count);
        Read(table, Rec(2));
        Assert.Equal("a00000000002", Assert.Single(table.Devices).MacText);
    }

    [Fact]
    public void Apply_NeverDropsABoundFanGroupOrAnUnboundLancool217() {
        var (table, _) = NewTable();
        Read(table, Rec(1), Rec(2, Other, type: 65), Rec(9));

        for (int read = 0; read < 40; read++) {
            Read(table, Rec(9));
        }

        Assert.Equal(3, table.Devices.Count);
    }

    [Fact]
    public void Apply_DropsEvenABoundV150AfterThirtyReadsUnheard() {
        var (table, log) = NewTable();
        Read(table, Rec(1, type: 66), Rec(9));

        for (int read = 0; read < 30; read++) {
            Read(table, Rec(9));
        }

        Assert.Equal("a00000000009", Assert.Single(table.Devices).MacText);
        Assert.Contains("W3:a00000000001 dropped from the device list after going unheard", log.Messages);
    }

    // RefreshList returns after removing one device, so only the first expired goes per read.
    [Fact]
    public void Apply_DropsOneExpiredDevicePerRead() {
        var (table, _) = NewTable();
        Read(table, Rec(1, Other), Rec(2, Other), Rec(9));
        for (int read = 0; read < 29; read++) {
            Read(table, Rec(9));
        }

        Read(table, Rec(9));
        Assert.Equal(new[] { "a00000000002", "a00000000009" }, table.Devices.Select(d => d.MacText));
        Read(table, Rec(9));
        Assert.Equal("a00000000009", Assert.Single(table.Devices).MacText);
    }

    // The countdown runs even on a read that fails (it is taken before the request), but only a
    // list that carries devices can drop one.
    [Fact]
    public void Apply_AFailedOrEmptyReadCountsDownButDropsNothing() {
        var (table, _) = NewTable();
        Read(table, Rec(1, Other), Rec(9));
        for (int read = 0; read < 35; read++) {
            table.Apply(null, Master, 5000);
        }

        table.Apply(FakeWirelessRecords.List(0), Master, 5000);
        Assert.Equal(2, table.Devices.Count);
        Assert.True(table.Devices[0].Live <= 0);

        Read(table, Rec(9));
        Assert.Equal("a00000000009", Assert.Single(table.Devices).MacText);
    }

    // The plugin's lost mark: thirty reads in a row that did not carry the device.
    [Fact]
    public void Apply_MarksABoundDeviceLostAfterThirtyMissesAndFoundWhenHeard() {
        var (table, log) = NewTable();
        Read(table, Rec(1), Rec(2, Other));

        for (int read = 0; read < 29; read++) {
            table.Apply(null, Master, 5000);
        }

        Assert.False(table.Devices[0].IsLost);
        Assert.Equal(29, table.Devices[0].MissedReads);
        table.Apply(null, Master, 5000);
        Assert.True(table.Devices[0].IsLost);
        Assert.True(table.Devices[1].IsLost);
        Assert.Contains("W3:a00000000001 not heard for 30 list reads; reading 0 rpm until it is", log.Messages);
        Assert.DoesNotContain(log.Messages, m => m.StartsWith("W3:a00000000002", StringComparison.Ordinal));

        Read(table, Rec(1));
        Assert.False(table.Devices[0].IsLost);
        Assert.Equal(0, table.Devices[0].MissedReads);
        Assert.Contains("W3:a00000000001 heard again", log.Messages);

        table.Apply(null, Master, 5000);
        Assert.Single(log.Messages, m => m.Contains("not heard for 30"));
    }

    // RefreshList: ConflictCnt++ for each other device of this master on the same rx_type, acting
    // once it is past 4; a pair differing only in a first byte of 1 is a ghost, added to ErrMacLst
    // and removed; any other conflict would be unbound (the plugin only logs it) and resets the count.
    // A device is checked against the table as it stands when its record is reached, so on the first
    // read the first device has no one to conflict with yet and its count runs one read behind.
    [Fact]
    public void Apply_LogsAReceiverSlotConflictOncePastFourReads() {
        var (table, log) = NewTable();

        for (int read = 0; read < 5; read++) {
            Assert.DoesNotContain(log.Messages, m => m.Contains("shares receiver slot"));
            Read(table, Rec(1, receiver: 5), Rec(2, receiver: 5));
        }

        Assert.Contains("W3:a00000000002 shares receiver slot 5 with a00000000001; L-Connect would unbind it, the plugin leaves the binding alone", log.Messages);
        Assert.Equal(4, table.Devices[0].ConflictCount);
        Assert.Equal(0, table.Devices[1].ConflictCount);
        Read(table, Rec(1, receiver: 5), Rec(2, receiver: 5));
        Assert.Contains("W3:a00000000001 shares receiver slot 5 with a00000000002; L-Connect would unbind it, the plugin leaves the binding alone", log.Messages);
        Assert.Equal(0, table.Devices[0].ConflictCount);

        for (int read = 0; read < 10; read++) {
            Read(table, Rec(1, receiver: 5), Rec(2, receiver: 5));
        }

        Assert.Equal(2, log.Messages.Count(m => m.Contains("shares receiver slot")));
        Assert.Equal(2, table.Devices.Count);
    }

    [Fact]
    public void Apply_ALoggedConflictIsLoggedAgainAfterItClears() {
        var (table, log) = NewTable();
        for (int read = 0; read < 6; read++) {
            Read(table, Rec(1, receiver: 5), Rec(2, receiver: 5));
        }

        Read(table, Rec(1, receiver: 6), Rec(2, receiver: 5));
        for (int read = 0; read < 6; read++) {
            Read(table, Rec(1, receiver: 5), Rec(2, receiver: 5));
        }

        Assert.Equal(4, log.Messages.Count(m => m.Contains("shares receiver slot")));
    }

    // RefreshList: without a conflict anywhere in the read, every ConflictCnt eases by one. The first
    // device moves slot, since it is compared against the second's record from the read before.
    [Fact]
    public void Apply_ConflictCountsEaseOnAReadWithoutConflict() {
        var (table, _) = NewTable();
        for (int read = 0; read < 3; read++) {
            Read(table, Rec(1, receiver: 5), Rec(2, receiver: 5));
        }

        Assert.Equal(2, table.Devices[0].ConflictCount);
        Assert.Equal(3, table.Devices[1].ConflictCount);
        Read(table, Rec(1, receiver: 6), Rec(2, receiver: 5));
        Assert.Equal(1, table.Devices[0].ConflictCount);
        Assert.Equal(2, table.Devices[1].ConflictCount);
        Read(table, Rec(1, receiver: 6), Rec(2, receiver: 5));
        Read(table, Rec(1, receiver: 6), Rec(2, receiver: 5));
        Assert.Equal(0, table.Devices[0].ConflictCount);
        Assert.Equal(0, table.Devices[1].ConflictCount);
    }

    // A device of another master on the same slot is no conflict.
    [Fact]
    public void Apply_AnotherMastersDeviceOnTheSameSlotIsNoConflict() {
        var (table, _) = NewTable();

        Read(table, Rec(1, receiver: 5), Rec(2, Other, receiver: 5));

        Assert.Equal(0, table.Devices[0].ConflictCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Apply_RemovesAGhostAddressForGood(bool ghostHeardFirst) {
        var (table, log) = NewTable();
        FakeWirelessRecord real = Rec(7, receiver: 5, first: 0x30);
        FakeWirelessRecord ghost = Rec(7, receiver: 5, first: 0x01);
        FakeWirelessRecord[] records = ghostHeardFirst ? new[] { ghost, real } : new[] { real, ghost };

        for (int read = 0; read < 5; read++) {
            Read(table, records);
        }

        Assert.Equal("300000000007", Assert.Single(table.Devices).MacText);
        Assert.Contains("W3:010000000007 is a ghost of another device's address sharing its receiver slot; dropped for good, as L-Connect does", log.Messages);

        Read(table, records);
        Assert.Single(table.Devices);
    }

    [Fact]
    public void Apply_AddressesThatDifferBeyondTheFirstByteAreNoGhosts() {
        var (table, _) = NewTable();
        FakeWirelessRecord a = Rec(7, receiver: 5, first: 0x01);
        FakeWirelessRecord b = Rec(8, receiver: 5, first: 0x30);

        for (int read = 0; read < 6; read++) {
            Read(table, a, b);
        }

        Assert.Equal(2, table.Devices.Count);
    }

    // RefreshList: sys_offset = SysClock - sysTime for LC217 and V150, and the RFList getter leaves
    // out a bound one more than 2000 ms off.
    [Fact]
    public void Apply_TakesTheClockOffsetOfCaseFansAndV150() {
        var (table, _) = NewTable();
        FakeWirelessRecord lancool = Rec(1, type: 65);
        lancool.ClockTicks = 4800; // 3000 ms
        FakeWirelessRecord v150 = Rec(2, type: 66);
        v150.ClockTicks = 11200; // 7000 ms
        FakeWirelessRecord group = Rec(3);
        group.ClockTicks = 1600;

        table.Apply(FakeWirelessRecords.List(lancool, v150, group), Master, 5000);

        Assert.Equal(2000, table.Devices[0].ClockOffset);
        Assert.Equal(-2000, table.Devices[1].ClockOffset);
        Assert.Equal(0, table.Devices[2].ClockOffset);
        Assert.False(WirelessDeviceTable.IsClockDrifted(table.Devices[0]));
        table.Apply(FakeWirelessRecords.List(lancool, v150, group), Master, 5001);
        Assert.True(WirelessDeviceTable.IsClockDrifted(table.Devices[0]));
        Assert.False(WirelessDeviceTable.IsClockDrifted(table.Devices[1]));
        table.Devices[2].ClockOffset = 9000;
        Assert.False(WirelessDeviceTable.IsClockDrifted(table.Devices[2]));
    }

    // RefreshList returns straight after a removal, before the clock offsets.
    [Fact]
    public void Apply_ARemovalSkipsTheClockOffsetsThatRead() {
        var (table, _) = NewTable();
        table.Apply(FakeWirelessRecords.List(Rec(1, Other), Rec(2, type: 65)), Master, 1000);
        FakeWirelessRecord lancool = Rec(2, type: 65);
        lancool.ClockTicks = 1600;
        for (int read = 0; read < 29; read++) {
            table.Apply(FakeWirelessRecords.List(Rec(2, type: 65)), Master, 1000);
        }

        table.Apply(FakeWirelessRecords.List(lancool), Master, 9000);

        Assert.Single(table.Devices);
        Assert.Equal(1000, table.Devices[0].ClockOffset);
    }

    [Fact]
    public void Arguments_AreValidated() {
        var (table, _) = NewTable();

        Assert.Throws<ArgumentNullException>(() => new WirelessDeviceTable(0, null!));
        Assert.Throws<ArgumentNullException>(() => table.Find(null!));
        Assert.Throws<ArgumentNullException>(() => table.Apply(null, null!, 0));
        Assert.Throws<ArgumentNullException>(() => WirelessDeviceTable.IsClockDrifted(null!));
        Assert.Null(table.Find("a00000000001"));
    }

    // ---------- L-Connect's locked device list (lock_list) ----------

    private static WirelessLockedDevice Locked(FakeWirelessRecord record, byte targetReceiver = 1, byte pwm = 80)
        => new WirelessLockedDevice(FakeWirelessRecords.Decode(record), targetReceiver, new[] { pwm, pwm, pwm, pwm });

    // CheckLockAndInitData: rfList becomes the saved list, in its order, every device bound.
    [Fact]
    public void Lock_RestoresTheSavedList_InItsOrder_Bound_WithTheSavedTargets() {
        var (table, _) = NewTable();
        Read(table, Rec(7));

        table.Lock(new[] { Locked(Rec(3), targetReceiver: 5, pwm: 90), Locked(Rec(1)) });

        Assert.True(table.IsLocked);
        Assert.Equal(new[] { "a00000000003", "a00000000001" }, table.Devices.Select(d => d.MacText));
        Assert.All(table.Devices, d => Assert.True(d.IsBound));
        Assert.Equal(5, table.Devices[0].TargetReceiverType);
        Assert.Equal(new byte[] { 90, 90, 90, 90 }, table.Devices[0].TargetPwm);
        Assert.Throws<ArgumentNullException>(() => table.Lock(null!));
    }

    // RefreshList under lock_list: no new device, the saved product type and (for a fan group) fan
    // types kept, no receiver slot conflict settled, nothing dropped.
    [Fact]
    public void WhileLocked_NoNewDevice_TheSavedIdentityKept_AndNothingDropped() {
        var (table, log) = NewTable();
        FakeWirelessRecord group = Rec(1);
        group.FanTypes = new byte[] { 36, 36, 0, 0 };
        // 3 shares 1's receiver slot, and the V150 (5) counts down unheard: unlocked, the one would
        // be settled as a conflict and the other dropped.
        table.Lock(new[] { Locked(group), Locked(Rec(3)), Locked(Rec(5, type: 66)) });

        FakeWirelessRecord heard = Rec(1, type: 3);
        heard.FanTypes = new byte[] { 20, 20, 20, 0 };
        for (int read = 0; read < WirelessDevice.MaximumMissedReads + 5; read++) {
            Read(table, heard, Rec(3), Rec(9));
        }

        Assert.Equal(new[] { "a00000000001", "a00000000003", "a00000000005" }, table.Devices.Select(d => d.MacText));
        Assert.Equal(0, table.Devices[0].Record.DeviceType);
        Assert.Equal(new byte[] { 36, 36, 0, 0 }, table.Devices[0].Record.FanTypes);
        Assert.DoesNotContain(log.Messages, m => m.Contains("receiver slot"));

        table.Unlock();
        Read(table, heard, Rec(9));
        Assert.False(table.IsLocked);
        Assert.Equal(3, table.Devices[0].Record.DeviceType);
        Assert.Contains(table.Devices, d => d.MacText == "a00000000009");
    }

    // A water block's and a case's slots carry live readings, so they are read even while locked.
    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(65)]
    [InlineData(66)]
    public void WhileLocked_AWaterBlockOrCase_StillReadsItsSlots(byte type) {
        var (table, _) = NewTable();
        table.Lock(new[] { Locked(Rec(1, type: type)) });
        FakeWirelessRecord heard = Rec(1, type: type);
        heard.FanTypes = new byte[] { 1, 1, 1, 30 };

        Read(table, heard);

        Assert.Equal(new byte[] { 1, 1, 1, 30 }, Assert.Single(table.Devices).Record.FanTypes);
    }
}
