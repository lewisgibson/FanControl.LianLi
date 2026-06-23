using System;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Tests.Fakes;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

public class FanControllerTests {
    private static readonly byte[] SlManualCh0 = { 224, 16, 49, 0x10 };
    private static readonly byte[] SlSpeedCh0Duty50 = { 224, 32, 0, 71 };

    private static (FanController controller, FakeHidTransport transport, FakeClock clock) NewSlController() {
        var transport = new FakeHidTransport();
        var clock = new FakeClock();
        var controller = new FanController(0, transport, new SlProtocol(), clock, new FakeLogger());
        return (controller, transport, clock);
    }

#if ENABLE_ARGB
    [Fact]
    public void Constructor_EmitsArgbSyncThenManualModeOnEveryChannel()
    {
        var transport = new FakeHidTransport();
        _ = new FanController(0, transport, new SlProtocol(), new FakeClock(), new FakeLogger());

        Assert.Equal(5, transport.Writes.Count); // ARGB sync + 4 manual mode
        Assert.Equal(new byte[] { 224, 16, 48, 1, 0, 0, 0 }, transport.Writes[0]); // SL ARGB register, on
        Assert.Equal(new byte[] { 224, 16, 49, 0x10 }, transport.Writes[1]); // ch0 manual
        Assert.Equal(new byte[] { 224, 16, 49, 0x80 }, transport.Writes[4]); // ch3 manual -> 0x80
    }
#else
    [Fact]
    public void Constructor_AssertsManualModeOnEveryChannel() {
        var transport = new FakeHidTransport();
        _ = new FanController(0, transport, new SlProtocol(), new FakeClock(), new FakeLogger());

        Assert.Equal(4, transport.Writes.Count);
        Assert.Equal(new byte[] { 224, 16, 49, 0x10 }, transport.Writes[0]); // ch0
        Assert.Equal(new byte[] { 224, 16, 49, 0x80 }, transport.Writes[3]); // ch3 -> 0x80
    }
#endif

    [Fact]
    public void ApplyPending_WritesManualModeBeforeSpeed() {
        var (controller, transport, _) = NewSlController();
        transport.Clear();

        controller.SetTarget(0, 50);
        controller.ApplyPending();

        Assert.Equal(2, transport.Writes.Count);
        Assert.Equal(SlManualCh0, transport.Writes[0]);       // manual mode re-asserted FIRST
        Assert.Equal(SlSpeedCh0Duty50, transport.Writes[1]);  // then the speed write
    }

    [Fact]
    public void ApplyPending_UnchangedAndFresh_WritesNothing() {
        var (controller, transport, _) = NewSlController();
        controller.SetTarget(0, 50);
        controller.ApplyPending();
        transport.Clear();

        controller.ApplyPending(); // same target, clock not advanced

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public void ApplyPending_StaleAfterFifteenSeconds_ReassertsBothReports() {
        var (controller, transport, clock) = NewSlController();
        controller.SetTarget(0, 50);
        controller.ApplyPending();
        transport.Clear();

        clock.Advance(TimeSpan.FromSeconds(15));
        controller.ApplyPending();

        Assert.Equal(2, transport.Writes.Count);
        Assert.Equal(SlManualCh0, transport.Writes[0]);
        Assert.Equal(SlSpeedCh0Duty50, transport.Writes[1]);
    }

    [Fact]
    public void ReleaseChannel_StopsKeepaliveForThatChannel() {
        var (controller, transport, clock) = NewSlController();
        controller.SetTarget(0, 50);
        controller.ApplyPending();
        controller.ReleaseChannel(0);
        transport.Clear();

        clock.Advance(TimeSpan.FromSeconds(30));
        controller.ApplyPending();

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public void PollRpm_DecodesCachedRpm() {
        var (controller, transport, _) = NewSlController();
        var buffer = new byte[65];
        buffer[1] = 0x0A; // ch0 high
        buffer[2] = 0x28; // ch0 low -> 2600
        transport.InputReport = buffer;

        controller.PollRpm();

        Assert.Equal(2600f, controller.GetRpm(0));
    }

    [Fact]
    public void PollRpm_SendsRpmPrimerFeatureReportBeforeEachRead() {
        var (controller, transport, _) = NewSlController();
        transport.Clear();

        controller.PollRpm();

        // L-Connect primes the device before every RPM read; the primer (a feature report) must be
        // sent so the device refreshes its input report rather than returning the stale idle buffer.
        Assert.Single(transport.Features);                                // exactly one primer per poll
        Assert.Equal(7, transport.Features[0].Length);                    // feature report length
        Assert.Equal(new byte[] { 0xE0, 0x50, 0x00 }, transport.Features[0][..3]);
        Assert.Equal(1, transport.ReadCount);                            // and the read happened
    }

    [Fact]
    public void PollRpm_IgnoresImplausibleReading_AndKeepsLastGood() {
        var (controller, transport, _) = NewSlController();
        var good = new byte[65];
        good[1] = 0x05; // ch0 high
        good[2] = 0xDC; // ch0 low -> 1500
        transport.InputReport = good;
        controller.PollRpm();
        Assert.Equal(1500f, controller.GetRpm(0));

        // The post-hibernate idle buffer decodes to ~50000 rpm; it must be ignored, not cached.
        var garbage = new byte[65];
        garbage[1] = 0xC3; // ch0 high
        garbage[2] = 0x50; // ch0 low -> 50000
        transport.InputReport = garbage;
        controller.PollRpm();

        Assert.Equal(1500f, controller.GetRpm(0)); // unchanged: the last good value is kept
    }

    [Fact]
    public void PollRpm_ResumesUpdating_WhenReadingBecomesPlausibleAgain() {
        var (controller, transport, _) = NewSlController();
        var garbage = new byte[65];
        garbage[1] = 0xC3;
        garbage[2] = 0x50; // ch0 -> 50000
        transport.InputReport = garbage;
        controller.PollRpm();
        Assert.Equal(0f, controller.GetRpm(0)); // no good value yet: the initial 0 is kept

        var good = new byte[65];
        good[1] = 0x05;
        good[2] = 0xDC; // ch0 -> 1500
        transport.InputReport = good;
        controller.PollRpm();

        Assert.Equal(1500f, controller.GetRpm(0)); // recovers once a plausible reading returns
    }

    [Fact]
    public void PollRpm_LogsImplausibleOnsetOnce_ThenRecovery() {
        var transport = new FakeHidTransport();
        var logger = new FakeLogger();
        var controller = new FanController(0, transport, new SlProtocol(), new FakeClock(), logger);

        var garbage = new byte[65];
        garbage[1] = 0xC3;
        garbage[2] = 0x50; // ch0 -> 50000
        transport.InputReport = garbage;
        controller.PollRpm();
        controller.PollRpm(); // a second garbage poll must NOT log again: the onset is logged once

        Assert.Single(logger.Messages, m => m.Contains("implausible rpm") && m.Contains("C0:0"));

        var good = new byte[65];
        good[1] = 0x05;
        good[2] = 0xDC; // ch0 -> 1500
        transport.InputReport = good;
        controller.PollRpm();

        Assert.Contains(logger.Messages, m => m.Contains("rpm recovered") && m.Contains("C0:0"));
    }

    [Fact]
    public void Dispose_DisposesTransport() {
        var (controller, transport, _) = NewSlController();
        controller.Dispose();
        Assert.True(transport.IsDisposed);
    }
}
