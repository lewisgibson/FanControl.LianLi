using System;
using FanControl.LianLi.Devices;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

/// <summary>What the wireless controller keeps for the process: the channel, the save schedule, the switched screens and the devices it drives.</summary>
public sealed class WirelessProcessStateTests {
    [Fact]
    public void RemembersTheLastChannel_ForItsMasterOnly() {
        var memory = new WirelessProcessState();
        Assert.Null(memory.Last);
        Assert.Null(memory.RecallFor("112233445566"));

        memory.Remember("112233445566", 12);

        Assert.Equal(12, memory.Last);
        Assert.Equal(12, memory.RecallFor("112233445566"));
        Assert.Null(memory.RecallFor("aabbccddeeff"));
        Assert.Throws<ArgumentNullException>(() => memory.Remember(null!, 8));
    }

    // Each mark is the device's own, and once made it stands for the process: L-Connect sends the
    // switch and the colours when its service starts and relies on the device keeping them.
    [Fact]
    public void KeepsOneSaveSchedule_TheScreensSwitched_AndTheScreensColoured_ForTheProcess() {
        var state = new WirelessProcessState();
        var start = new DateTime(2026, 9, 23, 19, 0, 0, DateTimeKind.Utc);

        WirelessSaveSchedule saves = state.SaveSchedule(start);
        Assert.Same(saves, state.SaveSchedule(start.AddHours(2)));

        Assert.Equal(0, state.TransmitterLifetime);
        Assert.False(state.IsScreenSwitched("a00000000001"));
        Assert.Null(state.ScreenSwitchedUnder("a00000000001"));
        state.MarkScreenSwitched("a00000000001", 0);
        state.MarkScreenSwitched("a00000000001", 0); // marking twice is nothing
        Assert.True(state.IsScreenSwitched("a00000000001"));
        Assert.Equal(0, state.ScreenSwitchedUnder("a00000000001")); // the lifetime the mark stands under, read with it
        Assert.False(state.IsScreenSwitched("a00000000002"));
        Assert.False(state.AreScreensColoured("a00000000001"));
        Assert.Null(state.ScreensColouredUnder("a00000000001"));
        Assert.Throws<ArgumentNullException>(() => state.MarkScreenSwitched(null!, 0));

        state.MarkScreensColoured("a00000000001", 0);
        Assert.True(state.AreScreensColoured("a00000000001"));
        Assert.Equal(0, state.ScreensColouredUnder("a00000000001"));
        Assert.False(state.AreScreensColoured("a00000000002"));
        Assert.Throws<ArgumentNullException>(() => state.MarkScreensColoured(null!, 0));
    }

    // The marks belong to a transmitter lifetime: L-Connect's service builds a new controller when
    // the transmitter dongle is plugged in again, and that one sends every device both looks
    // again. So the loss of the transmitter drops every mark and moves the lifetime on; a look
    // begun under the lifetime before, ending after the loss, marks nothing for the new one; and a
    // look begun under the new one marks as before.
    [Fact]
    public void TheTransmittersLoss_DropsEveryMark_AndALookBegunBeforeItMarksNothingAfterIt() {
        var state = new WirelessProcessState();
        state.MarkScreenSwitched("a00000000001", 0);
        state.MarkScreensColoured("a00000000001", 0);
        state.MarkScreensColoured("a00000000002", 0);

        state.TransmitterLost();

        Assert.Equal(1, state.TransmitterLifetime);
        Assert.False(state.IsScreenSwitched("a00000000001"));
        Assert.False(state.AreScreensColoured("a00000000001"));
        Assert.False(state.AreScreensColoured("a00000000002"));

        state.MarkScreenSwitched("a00000000001", 0); // begun under the lifetime before
        state.MarkScreensColoured("a00000000001", 0);
        Assert.False(state.IsScreenSwitched("a00000000001"));
        Assert.False(state.AreScreensColoured("a00000000001"));

        state.MarkScreenSwitched("a00000000001", 1);
        state.MarkScreensColoured("a00000000001", 1);
        Assert.True(state.IsScreenSwitched("a00000000001"));
        Assert.True(state.AreScreensColoured("a00000000001"));
        Assert.Equal(1, state.ScreenSwitchedUnder("a00000000001"));
        Assert.Equal(1, state.ScreensColouredUnder("a00000000001"));

        state.TransmitterLost();
        Assert.Equal(2, state.TransmitterLifetime);
        Assert.False(state.IsScreenSwitched("a00000000001"));
        Assert.False(state.AreScreensColoured("a00000000001"));
        Assert.Null(state.ScreenSwitchedUnder("a00000000001"));
        Assert.Null(state.ScreensColouredUnder("a00000000001"));
    }

    // The devices the radio drives, for a controller reaching the same device another way: each
    // controller's set replaced whole after each of its list reads, kept apart from another
    // controller's (the one closing after FanControl's refresh does not take the new one's with
    // it), and forgotten when that controller closes. IsBoundToMaster is true while any live
    // controller drives the device.
    [Fact]
    public void KeepsTheDevicesEachControllerDrives_UntilItReplacesOrForgetsThem() {
        var state = new WirelessProcessState();
        var first = new object();
        var second = new object();
        Assert.False(state.IsBoundToMaster("a00000000001"));
        Assert.False(state.IsBoundToMaster("a00000000002"));
        Assert.False(state.IsBoundToMaster("a00000000003"));

        state.RecordBoundDevices(first, new[] { "a00000000001", "a00000000002" });
        Assert.True(state.IsBoundToMaster("a00000000001"));
        Assert.True(state.IsBoundToMaster("a00000000002"));
        Assert.False(state.IsBoundToMaster("a00000000003"));

        state.RecordBoundDevices(second, new[] { "a00000000002", "a00000000003" });
        state.RecordBoundDevices(first, new[] { "a00000000002" });
        Assert.False(state.IsBoundToMaster("a00000000001"));
        Assert.True(state.IsBoundToMaster("a00000000002"));
        Assert.True(state.IsBoundToMaster("a00000000003"));

        state.ForgetBoundDevices(first);
        Assert.False(state.IsBoundToMaster("a00000000001"));
        Assert.True(state.IsBoundToMaster("a00000000002")); // the second still drives it
        Assert.True(state.IsBoundToMaster("a00000000003"));

        state.RecordBoundDevices(second, Array.Empty<string>());
        Assert.False(state.IsBoundToMaster("a00000000001"));
        Assert.False(state.IsBoundToMaster("a00000000002"));
        Assert.False(state.IsBoundToMaster("a00000000003"));
        state.ForgetBoundDevices(second);
        state.ForgetBoundDevices(second); // forgetting twice is nothing
        Assert.False(state.IsBoundToMaster("a00000000001"));
        Assert.False(state.IsBoundToMaster("a00000000002"));
        Assert.False(state.IsBoundToMaster("a00000000003"));
        Assert.Throws<ArgumentNullException>(() => state.IsBoundToMaster(null!));
        Assert.Throws<ArgumentNullException>(() => state.RecordBoundDevices(null!, Array.Empty<string>()));
        Assert.Throws<ArgumentNullException>(() => state.RecordBoundDevices(first, null!));
        Assert.Throws<ArgumentNullException>(() => state.ForgetBoundDevices(null!));
    }

    // The duty FanControl last commanded for a fan group, for whichever of the group's two
    // possible drivers has it now; -1 while none is commanded.
    [Fact]
    public void KeepsEachFanGroupsCommandedDuty_UntilItIsReleased() {
        var state = new WirelessProcessState();
        Assert.Equal(-1, state.ChainTarget("a00000000001"));

        state.SetChainTarget("a00000000001", 40);
        state.SetChainTarget("a00000000002", 70);
        Assert.Equal(40, state.ChainTarget("a00000000001"));
        Assert.Equal(70, state.ChainTarget("a00000000002"));

        state.SetChainTarget("a00000000001", 55);
        Assert.Equal(55, state.ChainTarget("a00000000001"));

        state.ReleaseChainTarget("a00000000001");
        state.ReleaseChainTarget("a00000000001"); // releasing twice is nothing
        Assert.Equal(-1, state.ChainTarget("a00000000001"));
        Assert.Equal(70, state.ChainTarget("a00000000002"));
        Assert.Throws<ArgumentNullException>(() => state.SetChainTarget(null!, 1));
        Assert.Throws<ArgumentNullException>(() => state.ReleaseChainTarget(null!));
        Assert.Throws<ArgumentNullException>(() => state.ChainTarget(null!));
    }
}
