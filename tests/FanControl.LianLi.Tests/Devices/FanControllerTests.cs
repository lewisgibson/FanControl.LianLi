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

    // Primer: the SL-Infinity is request-response and must be primed before every RPM read; the
    // streaming SL/AL families must not be primed.

    [Fact]
    public void PollRpm_SlInfinity_SendsPrimerFeatureReportBeforeRead() {
        var transport = new FakeHidTransport();
        var settled = new byte[65];
        settled[1] = 0x05; settled[2] = 0xDC; // ch0: 1500, so detection settles in the constructor
        transport.InputReport = settled;
        var controller = new FanController(0, transport, new SlInfinityProtocol(), new FakeClock(), new FakeLogger());
        transport.Clear();

        controller.PollRpm();

        Assert.Single(transport.Features);                 // exactly one primer per read
        Assert.Equal(65, transport.Features[0].Length);    // 65 bytes or HidD_SetFeature fails
        Assert.Equal(new byte[] { 0xE0, 0x50, 0x00 }, transport.Features[0][..3]);
        Assert.Equal(1, transport.ReadCount);              // the read followed the primer
    }

    [Fact]
    public void PollRpm_StreamingFamily_SendsNoPrimer() {
        var (controller, transport, _) = NewSlController();
        transport.Clear();

        controller.PollRpm();

        Assert.Empty(transport.Features); // SL streams its input report; no primer
    }

    // Channel detection tests.
    // RPM is big-endian at buffer[1..2] for ch0, buffer[3..4] for ch1, etc. (SL protocol).

    [Fact]
    public void ChannelCount_DefaultAllZero_ReturnsFour() {
        // Default InputReport is all-zeros: no fans detected → fallback to all four channels.
        var (controller, _, _) = NewSlController();

        Assert.Equal(4, controller.ChannelCount);
    }

    [Fact]
    public void ChannelCount_TwoFansDetected_ReturnsTwo() {
        var transport = new FakeHidTransport();
        // ch0 = 1500 rpm, ch1 = 0 (unpopulated), ch2 = 1200 rpm, ch3 = 0
        var buffer = new byte[65];
        buffer[1] = 0x05; buffer[2] = 0xDC; // ch0: 1500
        buffer[5] = 0x04; buffer[6] = 0xB0; // ch2: 1200
        transport.InputReport = buffer;

        var controller = new FanController(0, transport, new SlProtocol(), new FakeClock(), new FakeLogger());

        Assert.Equal(2, controller.ChannelCount);
    }

    [Fact]
    public void Describe_MapsToPhysicalChannel_WhenFilteredToNonContiguousChannels() {
        var transport = new FakeHidTransport();
        // ch0 = 1500 rpm, ch2 = 1200 rpm → _physicalChannels = [0, 2]
        var buffer = new byte[65];
        buffer[1] = 0x05; buffer[2] = 0xDC; // ch0: 1500
        buffer[5] = 0x04; buffer[6] = 0xB0; // ch2: 1200
        transport.InputReport = buffer;

        var controller = new FanController(0, transport, new SlProtocol(), new FakeClock(), new FakeLogger());

        ChannelDescriptor ch0 = controller.Describe(0);
        ChannelDescriptor ch1 = controller.Describe(1);

        // External channel 0 → physical 0, external channel 1 → physical 2.
        Assert.Contains("ch0", ch0.ControlId);
        Assert.Contains("ch2", ch1.ControlId);
        Assert.Equal("Lian Li Uni #1 Ch 1", ch0.ControlName);
        Assert.Equal("Lian Li Uni #1 Ch 3", ch1.ControlName);
    }

    [Fact]
    public void ChannelCount_DetectionReadFails_ReturnsFour() {
        var transport = new FakeHidTransport { FailReads = true };
        // Construction must not throw; detection failure falls back to all four channels.
        var controller = new FanController(0, transport, new SlProtocol(), new FakeClock(), new FakeLogger());

        Assert.Equal(4, controller.ChannelCount);
    }

    // The SL-Infinity idle-state buffer (returned for a short window after wake) has a fixed
    // pattern: some channels decode above MaxPlausibleRpm (ch0 = 57424 = 0xE050) while others
    // fall below it (ch3 = 3328 = 0x0D00). Any implausible channel marks the whole report as
    // unsettled, so detection must NOT register the sub-threshold garbage as a real fan.
    private static byte[] IdleStateBuffer() {
        var buffer = new byte[65];
        buffer[1] = 0xE0; buffer[2] = 0x50; // ch0: 57424 (implausible)
        buffer[3] = 0x80; buffer[4] = 0xC4; // ch1: 32964 (implausible)
        buffer[5] = 0x00; buffer[6] = 0x00; // ch2: 0
        buffer[7] = 0x0D; buffer[8] = 0x00; // ch3: 3328 (plausible but garbage)
        return buffer;
    }

    [Fact]
    public void ChannelCount_DeviceNeverSettles_FallsBackToFour() {
        // Every read returns the idle buffer: detection retries the full budget, then falls back
        // to all four channels so the controller is never hidden.
        var transport = new FakeHidTransport { InputReport = IdleStateBuffer() };
        var clock = new FakeClock();

        var controller = new FanController(0, transport, new SlProtocol(), clock, new FakeLogger());

        Assert.Equal(4, controller.ChannelCount);
        Assert.True(transport.ReadCount > 1, "detection should retry, not give up after one read");
        Assert.True(clock.TotalSlept > TimeSpan.Zero, "detection should wait between retries");
    }

    [Fact]
    public void ChannelCount_IdleBufferThenSettles_DetectsRealChannels() {
        // First read lands in the post-wake idle window (garbage); the second read is settled
        // with fans on ch0 and ch1. Detection must wait, re-read, and register only the real
        // channels - not the garbage from the first read.
        var transport = new FakeHidTransport();
        transport.InputReportSequence.Enqueue(IdleStateBuffer());
        var settled = new byte[65];
        settled[1] = 0x05; settled[2] = 0xDC; // ch0: 1500
        settled[3] = 0x04; settled[4] = 0xB0; // ch1: 1200
        transport.InputReportSequence.Enqueue(settled);

        var controller = new FanController(0, transport, new SlProtocol(), new FakeClock(), new FakeLogger());

        Assert.Equal(2, controller.ChannelCount);
        Assert.Equal("Lian Li Uni #1 Ch 1", controller.Describe(0).ControlName);
        Assert.Equal("Lian Li Uni #1 Ch 2", controller.Describe(1).ControlName);
    }

    [Fact]
    public void GetRpm_ChannelMapsToPhysicalChannel() {
        var transport = new FakeHidTransport();
        // Only ch2 has a fan.
        var detected = new byte[65];
        detected[5] = 0x04; detected[6] = 0xB0; // ch2: 1200
        transport.InputReport = detected;

        var controller = new FanController(0, transport, new SlProtocol(), new FakeClock(), new FakeLogger());

        // After PollRpm, GetRpm(0) should return the ch2 physical reading.
        var poll = new byte[65];
        poll[5] = 0x05; poll[6] = 0xDC; // ch2: 1500
        transport.InputReport = poll;
        controller.PollRpm();

        Assert.Equal(1500f, controller.GetRpm(0));
    }
}
