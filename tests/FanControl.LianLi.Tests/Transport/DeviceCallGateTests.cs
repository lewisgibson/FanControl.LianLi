using System;
using System.IO;
using System.Threading;
using FanControl.LianLi.Tests.Fakes;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

/// <summary>
/// The gate's decisions, driven through the rule-based bound: a call given up on that has not
/// returned holds back every later call to its device and only its device, the refusal fails fast and
/// is logged once, a cleanup call is never held back, and the device is reachable again - with a line
/// saying so - once every call stuck on it has returned. One test runs the real bound, to show a real
/// abandoned thread reporting its return.
/// </summary>
public class DeviceCallGateTests {
    private const string Device = @"\\?\hid#vid_0cf2&pid_a102#uni";
    private const string OtherDevice = @"\\?\hid#vid_0cf2&pid_a102#other";

    private readonly FakeDeviceCallRunner _calls = new FakeDeviceCallRunner();
    private readonly FakeDeviceCallClock _clock = new FakeDeviceCallClock();
    private readonly FakeLogger _log = new FakeLogger();
    private readonly DeviceCallGate _gate;

    public DeviceCallGateTests() {
        _gate = new DeviceCallGate(_calls, _clock, _log);
    }

    private bool Run(string operation, string device = Device) => _gate.TryRun(device, operation, _ => { }, 500, () => { });

    // A call to the device the bound gives up on and holds, as a stuck thread would be.
    private void Strand(string operation, string device = Device) {
        _calls.TimesOut = candidate => candidate == operation;
        Assert.False(Run(operation, device));
        _calls.TimesOut = _ => false;
    }

    // FanControl makes a new plugin object, and so a new gate, on every refresh: gates over the same
    // stuck calls see each other's, so a refresh does not start another call on a stuck device.
    [Fact]
    public void GatesSharingTheirStuckCalls_RefuseForEachOther() {
        var shared = new DeviceCallGate.StuckCalls();
        var before = new DeviceCallGate(_calls, _clock, _log, shared);
        var after = new DeviceCallGate(new FakeDeviceCallRunner(), _clock, new FakeLogger(), shared);
        _calls.TimesOut = _ => true;
        Assert.False(before.TryRun(Device, "open " + Device, _ => { }, 500, () => { }));

        Assert.Throws<IOException>(() => after.TryRun(Device, "open " + Device, _ => { }, 500, () => { }));
        Assert.NotNull(DeviceCallGate.ForProcess(_calls, _clock, _log));
        Assert.Throws<ArgumentNullException>(() => new DeviceCallGate(_calls, _clock, _log, null!));
    }

    [Fact]
    public void TryRun_ACallThatCompletes_PassesStraightThrough_AndHoldsNothingBack() {
        bool ran = false;

        Assert.True(_gate.TryRun(Device, "WriteFile on " + Device, _ => ran = true, 1000, () => { }));

        Assert.True(ran);
        Assert.Equal(1000, _calls.TimeoutOf("WriteFile on " + Device));
        Assert.True(Run("ReadFile on " + Device));
        Assert.Empty(_log.Messages);
    }

    [Fact]
    public void TryRun_ACallThatThrowsInTime_Propagates_AndHoldsNothingBack() {
        Assert.Throws<IOException>(
            () => _gate.TryRun(Device, "WriteFile on " + Device, _ => throw new IOException("refused"), 1000, () => { }));

        Assert.True(Run("ReadFile on " + Device));
    }

    [Fact]
    public void TryRun_WhileAnEarlierCallHasNotReturned_FailsFast_WithoutStartingTheCall() {
        _clock.NowMilliseconds = 10_000;
        Strand("reopen on " + Device);
        _clock.NowMilliseconds = 12_500;
        bool ran = false;
        bool cancelled = false;

        IOException refused = Assert.Throws<IOException>(
            () => _gate.TryRun(Device, "reopen on " + Device, _ => ran = true, 2000, () => cancelled = true));

        Assert.Equal(
            "reopen on " + Device + " not started: the earlier reopen on " + Device + ", given up on 2500 ms ago, has not returned.",
            refused.Message);
        Assert.False(ran);
        Assert.False(cancelled);
        Assert.Single(_calls.Operations);
    }

    [Fact]
    public void TryRun_Refusals_AreLoggedOnce_NotOnEveryAttempt() {
        Strand("open of " + Device);
        _clock.NowMilliseconds = 700;

        for (int i = 0; i < 5; i++) {
            Assert.Throws<IOException>(() => Run("open of " + Device));
        }

        Assert.Equal(
            "  open of " + Device + " refused: the earlier open of " + Device + ", given up on 700 ms ago, has not returned; every call to " + Device + " fails fast until it does (logged once until then)",
            Assert.Single(_log.Messages));
    }

    [Fact]
    public void TryRun_AStuckCall_HoldsBackOnlyItsOwnDevice() {
        Strand("reopen on " + Device);

        Assert.True(Run("open of " + OtherDevice, OtherDevice));

        Assert.Throws<IOException>(() => Run("open of " + Device));
    }

    [Fact]
    public void TryRun_TheDeviceKey_IgnoresCase_AsWindowsPathsDo() {
        Strand("reopen on " + Device);

        Assert.Throws<IOException>(() => Run("open of " + Device, Device.ToUpperInvariant()));
    }

    [Fact]
    public void TryRun_OnceTheStuckCallReturns_ResumesNormally_AndSaysSoIfAnythingWasRefused() {
        _clock.NowMilliseconds = 1000;
        Strand("reopen on " + Device);
        _clock.NowMilliseconds = 3000;
        Assert.Throws<IOException>(() => Run("reopen on " + Device));
        Assert.Throws<IOException>(() => Run("reopen on " + Device));
        _clock.NowMilliseconds = 61_000;

        Assert.Single(_calls.Abandoned)(CancellationToken.None);

        Assert.Equal(
            "  reopen on " + Device + " returned 60000 ms after it was given up on; calls to " + Device + " resume (2 refused meanwhile)",
            _log.Messages[^1]);
        Assert.True(Run("reopen on " + Device));
        Assert.Equal(2, _log.Messages.Count);
    }

    [Fact]
    public void TryRun_AStuckCallThatReturnsBeforeAnythingWasRefused_ResumesSilently() {
        Strand("WriteFile on " + Device);

        Assert.Single(_calls.Abandoned)(CancellationToken.None);

        Assert.True(Run("reopen on " + Device));
        Assert.Empty(_log.Messages);
    }

    [Fact]
    public void TryRun_AStuckCallThatReturnsByThrowing_StillReleasesTheDevice() {
        _calls.TimesOut = _ => true;
        Assert.False(_gate.TryRun(Device, "reopen on " + Device, _ => throw new IOException("late"), 2000, () => { }));
        _calls.TimesOut = _ => false;

        Assert.Throws<IOException>(() => Assert.Single(_calls.Abandoned)(CancellationToken.None));

        Assert.True(Run("reopen on " + Device));
    }

    [Fact]
    public void TryRun_ACallThatFinishesAtTheDeadline_IsNotLeftOut() {
        _calls.FinishesAtDeadline = _ => true;
        Assert.False(Run("reopen on " + Device));
        _calls.FinishesAtDeadline = _ => false;

        Assert.True(Run("reopen on " + Device));
    }

    [Fact]
    public void TryRun_ACallGivenUpOnWhileRunning_ThatUnwindsBeforeTheBoundHandsBack_IsNotLeftOut() {
        Assert.False(_gate.TryRun(Device, "reopen on " + Device, _ => _calls.AbandonRunning(), 2000, () => { }));

        Assert.True(Run("reopen on " + Device));
    }

    [Fact]
    public void TryRun_SeveralStuckCalls_NameTheOldest_AndHoldTheDeviceUntilEveryOneHasReturned() {
        _clock.NowMilliseconds = 100;
        Strand("WinUsb_WritePipe on " + Device);
        _clock.NowMilliseconds = 400;
        _calls.TimesOut = candidate => candidate.StartsWith("WinUsb_AbortPipe", StringComparison.Ordinal);
        Assert.False(_gate.TryRunCleanup(Device, "WinUsb_AbortPipe on " + Device, _ => { }, 500, () => { }));
        _calls.TimesOut = _ => false;
        _clock.NowMilliseconds = 1100;

        IOException refused = Assert.Throws<IOException>(() => Run("reopen on " + Device));
        Assert.Contains("the earlier WinUsb_WritePipe on " + Device + ", given up on 1000 ms ago", refused.Message);

        _calls.Abandoned[0](CancellationToken.None);
        Assert.Throws<IOException>(() => Run("reopen on " + Device));
        Assert.Single(_log.Messages);

        _calls.Abandoned[1](CancellationToken.None);
        Assert.True(Run("reopen on " + Device));
        Assert.Equal(
            "  WinUsb_AbortPipe on " + Device + " returned 700 ms after it was given up on; calls to " + Device + " resume (2 refused meanwhile)",
            _log.Messages[^1]);
    }

    [Fact]
    public void TryRunCleanup_RunsEvenWhileACallIsStuck_AndACleanupThatCompletes_LeavesTheStuckOneInPlace() {
        Strand("reopen on " + Device);
        bool closed = false;

        Assert.True(_gate.TryRunCleanup(Device, "close on " + Device, _ => closed = true, 2000, () => { }));

        Assert.True(closed);
        Assert.Throws<IOException>(() => Run("open of " + Device));
    }

    [Fact]
    public void TryRunCleanup_ThatIsItselfStuck_HoldsBackLaterCalls() {
        _calls.TimesOut = _ => true;
        Assert.False(_gate.TryRunCleanup(Device, "close on " + Device, _ => { }, 2000, () => { }));
        _calls.TimesOut = _ => false;

        IOException refused = Assert.Throws<IOException>(() => Run("open of " + Device));

        Assert.Contains("the earlier close on " + Device, refused.Message);
    }

    [Fact]
    public void TryRun_OverTheRealBound_AnAbandonedThreadThatFinallyReturns_ReleasesTheDevice() {
        using var release = new ManualResetEventSlim(false);
        var gate = new DeviceCallGate(new BoundedDeviceCallRunner(_log), _clock, _log);

        Assert.False(gate.TryRun(Device, "reopen on " + Device, _ => release.Wait(CancellationToken.None), 50, () => { }));
        Assert.Throws<IOException>(() => gate.TryRun(Device, "reopen on " + Device, _ => { }, 50, () => { }));

        release.Set();
        Assert.True(SpinWait.SpinUntil(() => _log.Messages.Count == 2, TimeSpan.FromSeconds(5)));
        Assert.EndsWith("calls to " + Device + " resume (1 refused meanwhile)", _log.Messages[1]);
        Assert.True(gate.TryRun(Device, "reopen on " + Device, _ => { }, 1000, () => { }));
    }

    [Fact]
    public void Constructor_ValidatesItsDependencies() {
        Assert.Throws<ArgumentNullException>(() => new DeviceCallGate(null!, _clock, _log));
        Assert.Throws<ArgumentNullException>(() => new DeviceCallGate(_calls, null!, _log));
        Assert.Throws<ArgumentNullException>(() => new DeviceCallGate(_calls, _clock, null!));
    }

    [Fact]
    public void TryRun_ValidatesItsArguments() {
        Assert.Throws<ArgumentNullException>(() => _gate.TryRun(null!, "op", _ => { }, 1, () => { }));
        Assert.Throws<ArgumentNullException>(() => _gate.TryRun(Device, null!, _ => { }, 1, () => { }));
        Assert.Throws<ArgumentNullException>(() => _gate.TryRun(Device, "op", null!, 1, () => { }));
        Assert.Throws<ArgumentNullException>(() => _gate.TryRun(Device, "op", _ => { }, 1, null!));
        Assert.Throws<ArgumentNullException>(() => _gate.TryRunCleanup(null!, "op", _ => { }, 1, () => { }));
        Assert.Empty(_calls.Operations);
    }
}
