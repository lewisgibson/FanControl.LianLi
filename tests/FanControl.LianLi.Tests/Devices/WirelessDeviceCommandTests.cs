using FanControl.LianLi.Devices;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

/// <summary>The state a command sent under a sequence keeps between the device's passes and its caller's.</summary>
public sealed class WirelessDeviceCommandTests {
    // The end of a round waits on the command until its caller takes it, and is taken once: a
    // caller that stopped calling mid-round (a pump control released) finds it whenever it is next
    // called, and nothing acts on it twice.
    [Fact]
    public void TakeEnd_HandsTheRoundsEndBackOnce() {
        var command = new WirelessDeviceCommand();
        Assert.False(command.IsUnderWay);
        Assert.Null(command.TakeEnd());

        command.Ended = WirelessRoundEnd.Exhausted;
        Assert.True(command.IsUnderWay);

        Assert.Equal(WirelessRoundEnd.Exhausted, command.TakeEnd());
        Assert.Null(command.TakeEnd());
        Assert.False(command.IsUnderWay);
    }

    [Fact]
    public void IsUnderWay_WhileARoundIsBeingSent() {
        var command = new WirelessDeviceCommand { Sequence = 3 };

        Assert.True(command.IsUnderWay);
    }

    [Fact]
    public void Reset_StartsOver_EndOwedIncluded() {
        var command = new WirelessDeviceCommand {
            Sequence = 3,
            Sends = 4,
            Rounds = 2,
            ReadbackReads = 1,
            Done = true,
            TransmitterLifetime = 2,
            Ended = WirelessRoundEnd.Acknowledged,
        };

        command.Reset();

        Assert.Null(command.Sequence);
        Assert.Equal(0, command.Sends);
        Assert.Equal(0, command.Rounds);
        Assert.Equal(0, command.ReadbackReads);
        Assert.False(command.Done);
        Assert.Equal(0, command.TransmitterLifetime);
        Assert.Null(command.Ended);
        Assert.False(command.IsUnderWay);
    }
}
