using System;
using System.IO;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Tests.Fakes;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

/// <summary>
/// The work a controller owes its device after a reconnect: owed from the moment the device is
/// back, done once the replay says so, kept owed while it says otherwise and tried again on the
/// keepalive cadence, or at once when the device comes back again.
/// </summary>
public sealed class ReconnectReplayTests {
    private readonly FakeClock _clock = new FakeClock();

    [Fact]
    public void NothingIsOwed_UntilTheDeviceIsBack_AndNothingWithoutAReplay() {
        var replay = new ReconnectReplay(_clock);
        int runs = 0;

        replay.Apply();
        replay.Owe();
        Assert.False(replay.IsOwed);
        replay.Register(() => {
            runs++;
            return true;
        });
        replay.Apply();

        Assert.Equal(0, runs);
        Assert.False(replay.IsOwed);
    }

    [Fact]
    public void AReplayThatReportsItselfDone_IsOwedNoMore() {
        var replay = new ReconnectReplay(_clock);
        int runs = 0;
        replay.Register(() => {
            runs++;
            return true;
        });

        replay.Owe();
        Assert.True(replay.IsOwed);
        replay.Apply();
        replay.Apply();

        Assert.Equal(1, runs);
        Assert.False(replay.IsOwed);
    }

    // A look the device refused is tried again with the next keepalive re-send, not every tick,
    // so a device that refuses it for good costs a log line every fifteen seconds.
    [Fact]
    public void AReplayThatReportsFailure_StaysOwed_AndIsTriedAgainOnTheKeepaliveCadence() {
        var replay = new ReconnectReplay(_clock);
        int runs = 0;
        replay.Register(() => runs++ >= 2);
        replay.Owe();

        replay.Apply();
        _clock.Advance(TimeSpan.FromSeconds(14));
        replay.Apply();
        Assert.Equal(1, runs);
        Assert.True(replay.IsOwed);

        _clock.Advance(TimeSpan.FromSeconds(1));
        replay.Apply();
        Assert.Equal(2, runs);
        Assert.True(replay.IsOwed);

        _clock.Advance(ChannelWriteDecision.RefreshInterval);
        replay.Apply();
        Assert.Equal(3, runs);
        Assert.False(replay.IsOwed);
    }

    // The device came back again before the refused look was due: it is owed afresh, at once.
    [Fact]
    public void ADeviceBackAgain_HasTheReplayTriedAtOnce_HoweverRecentlyItFailed() {
        var replay = new ReconnectReplay(_clock);
        int runs = 0;
        replay.Register(() => {
            runs++;
            return false;
        });
        replay.Owe();
        replay.Apply();

        replay.Owe();
        replay.Apply();

        Assert.Equal(2, runs);
    }

    [Fact]
    public void AReplayThatThrows_Propagates_AndStaysOwed() {
        var replay = new ReconnectReplay(_clock);
        replay.Register(() => throw new IOException("gone again"));
        replay.Owe();

        Assert.Throws<IOException>(replay.Apply);

        Assert.True(replay.IsOwed);
    }

    [Fact]
    public void RejectsMissingParts() {
        Assert.Throws<ArgumentNullException>(() => new ReconnectReplay(null!));
        Assert.Throws<ArgumentNullException>(() => new ReconnectReplay(_clock).Register(null!));
    }
}
