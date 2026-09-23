using System;
using FanControl.LianLi.Devices;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

/// <summary>The channel the wireless master was last driven on, kept across FanControl's refreshes.</summary>
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

    [Fact]
    public void KeepsOneSaveSchedule_AndTheScreensSwitched_ForTheProcess() {
        var state = new WirelessProcessState();
        var start = new DateTime(2026, 9, 23, 19, 0, 0, DateTimeKind.Utc);

        WirelessSaveSchedule saves = state.SaveSchedule(start);
        Assert.Same(saves, state.SaveSchedule(start.AddHours(2)));

        Assert.False(state.IsScreenSwitched("a00000000001"));
        state.MarkScreenSwitched("a00000000001");
        Assert.True(state.IsScreenSwitched("a00000000001"));
        Assert.Throws<ArgumentNullException>(() => state.MarkScreenSwitched(null!));
    }
}
