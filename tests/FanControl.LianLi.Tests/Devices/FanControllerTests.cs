using System;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Tests.Fakes;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

public class FanControllerTests {
    // Fan control goes out as FEATURE reports (matching L-Connect): manual-mode is a 6-byte
    // SetFanMotherboardSync(ch, off); SL is a v1 family so duty 50 is sent raw as byte 50.
    private static readonly byte[] SlManualCh0 = { 224, 16, 49, 0x10, 0, 0 };
    private static readonly byte[] SlSpeedCh0Duty50 = { 224, 32, 0, 50 };

    private static readonly bool[] NoStartStop = { false, false, false, false };

    private static (FanController controller, FakeHidTransport transport, FakeClock clock) NewSlController() {
        var transport = new FakeHidTransport();
        var clock = new FakeClock();
        var controller = new FanController(0, transport, new SlProtocol(), NoStartStop, clock, new FakeLogger());
        return (controller, transport, clock);
    }

#if ENABLE_ARGB
    [Fact]
    public void AssertManualMode_EmitsArgbSyncThenManualModeOnEveryChannel()
    {
        var (controller, transport, _) = NewSlController();

        controller.AssertManualMode();

        Assert.Equal(5, transport.Features.Count); // ARGB sync + 4 manual mode, all feature reports
        Assert.Equal(new byte[] { 224, 16, 48, 1, 0, 0, 0 }, transport.Features[0]); // SL ARGB register, on
        Assert.Equal(new byte[] { 224, 16, 49, 0x10, 0, 0 }, transport.Features[1]); // ch0 manual
        Assert.Equal(new byte[] { 224, 16, 49, 0x80, 0, 0 }, transport.Features[4]); // ch3 manual -> 0x80
        Assert.Empty(transport.Writes); // the fan path uses no output reports
    }
#else
    [Fact]
    public void AssertManualMode_EmitsManualModeOnEveryChannel() {
        var (controller, transport, _) = NewSlController();

        controller.AssertManualMode();

        Assert.Equal(4, transport.Features.Count);
        Assert.Equal(new byte[] { 224, 16, 49, 0x10, 0, 0 }, transport.Features[0]); // ch0
        Assert.Equal(new byte[] { 224, 16, 49, 0x80, 0, 0 }, transport.Features[3]); // ch3 -> 0x80
        Assert.Empty(transport.Writes); // the fan path uses no output reports
    }
#endif

    [Fact]
    public void Constructor_DoesNoIo() {
        // Setup writes live in AssertManualMode, called - and guarded - by the composition root
        // after construction: a hub that rejects a setup write must degrade to a logged fault,
        // never lose the whole controller by aborting construction.
        var (_, transport, _) = NewSlController();
        Assert.Empty(transport.Transfers);
        Assert.Equal(0, transport.ReadCount);
    }

    // The control/RPM sensor identifiers are the contract with a user's saved FanControl config:
    // FanControl binds every fan curve to these exact strings. If a change alters an identifier, the
    // binding to it silently breaks - the curve now points at an id nothing exposes. (Auto-hide
    // deliberately stops exposing an *empty* channel, which is safe: FanControl greys that one
    // binding out and re-links it if the fan returns, leaving the rest of the config intact - so
    // hiding an empty slot is fine, but *re-keying* a populated channel is not.) These are pinned so
    // a change that re-keys a control fails a test loudly BEFORE it ships. Do not "fix" this test by
    // editing the expected strings without accepting that you are re-keying every existing user's controls.
    [Theory]
    [InlineData(0, 0, "LianLi/0/ch0/ctl", "Lian Li Uni #1 Ch 1", "LianLi/0/ch0/fan", "Lian Li Uni #1 Ch 1 RPM")]
    [InlineData(0, 3, "LianLi/0/ch3/ctl", "Lian Li Uni #1 Ch 4", "LianLi/0/ch3/fan", "Lian Li Uni #1 Ch 4 RPM")]
    [InlineData(1, 1, "LianLi/1/ch1/ctl", "Lian Li Uni #2 Ch 2", "LianLi/1/ch1/fan", "Lian Li Uni #2 Ch 2 RPM")]
    [InlineData(2, 0, "LianLi/2/ch0/ctl", "Lian Li Uni #3 Ch 1", "LianLi/2/ch0/fan", "Lian Li Uni #3 Ch 1 RPM")]
    [InlineData(2, 3, "LianLi/2/ch3/ctl", "Lian Li Uni #3 Ch 4", "LianLi/2/ch3/fan", "Lian Li Uni #3 Ch 4 RPM")]
    public void Describe_SensorIdentifiers_AreStable(
        int index, int channel, string controlId, string controlName, string rpmId, string rpmName) {
        var controller = new FanController(
            index, new FakeHidTransport(), new SlProtocol(), NoStartStop, new FakeClock(), new FakeLogger());

        ChannelDescriptor descriptor = controller.Describe(channel);

        Assert.Equal(controlId, descriptor.ControlId);
        Assert.Equal(controlName, descriptor.ControlName);
        Assert.Equal(rpmId, descriptor.RpmId);
        Assert.Equal(rpmName, descriptor.RpmName);
    }

    [Fact]
    public void ChannelCount_IsAlwaysFour_AndModelsEveryPhysicalChannel() {
        // A Uni controller always MODELS four physical channels. Auto-hide skips *registering* an
        // empty channel at the plugin layer (see LianLiPlugin.Load / IsChannelPopulated) - it never
        // renumbers or drops a physical channel here, because that is what keeps a populated
        // channel's sensor id stable so a saved binding survives. Pinned so a change to the physical
        // channel set fails a test before it ships.
        var controller = new FanController(
            0, new FakeHidTransport(), new SlProtocol(), NoStartStop, new FakeClock(), new FakeLogger());

        Assert.Equal(4, controller.ChannelCount);
    }

    [Fact]
    public void IsChannelPopulated_DefaultsToAllShown_BeforeDetection() {
        // Until DetectPopulation runs, every channel is shown - the controller is never hidden by
        // default, so a controller whose probe never runs (or faults) still surfaces all channels.
        var (controller, _, _) = NewSlController();

        for (int channel = 0; channel < 4; channel++) {
            Assert.True(controller.IsChannelPopulated(channel));
        }
    }

    [Fact]
    public void DetectPopulation_HidesChannelsThatReadNoRpm() {
        // ch0 and ch2 spin (a plausible non-zero RPM on every probe); ch1 and ch3 read 0 - an empty
        // slot. Detection must mark only the spinning channels populated.
        var transport = new FakeHidTransport();
        var buffer = new byte[65];
        buffer[1] = 0x05; buffer[2] = 0xDC; // ch0 (SL rpm offset 1) -> 1500
        buffer[5] = 0x05; buffer[6] = 0xDC; // ch2 (offset 5)        -> 1500
        transport.InputReport = buffer;
        var controller = new FanController(0, transport, new SlProtocol(), NoStartStop, new FakeClock(), new FakeLogger());

        controller.DetectPopulation();

        Assert.True(controller.IsChannelPopulated(0));
        Assert.False(controller.IsChannelPopulated(1));
        Assert.True(controller.IsChannelPopulated(2));
        Assert.False(controller.IsChannelPopulated(3));
    }

    [Fact]
    public void DetectPopulation_WhenNothingReadsRpm_ShowsAllChannels() {
        // An all-idle probe (fans not spun up yet, or every read is 0/garbage) is inconclusive:
        // never hide the whole controller. The default all-zero input report reads 0 on every channel.
        var (controller, _, _) = NewSlController();

        controller.DetectPopulation();

        for (int channel = 0; channel < 4; channel++) {
            Assert.True(controller.IsChannelPopulated(channel));
        }
    }

    [Fact]
    public void ApplyPending_WritesManualModeBeforeSpeed() {
        var (controller, transport, _) = NewSlController();
        transport.Clear();

        controller.SetTarget(0, 50);
        controller.ApplyPending();

        Assert.Equal(2, transport.Features.Count);
        Assert.Equal(SlManualCh0, transport.Features[0]);       // manual mode re-asserted FIRST
        Assert.Equal(SlSpeedCh0Duty50, transport.Features[1]);  // then the speed write
    }

    [Fact]
    public void ApplyPending_UnchangedAndFresh_WritesNothing() {
        var (controller, transport, _) = NewSlController();
        controller.SetTarget(0, 50);
        controller.ApplyPending();
        transport.Clear();

        controller.ApplyPending(); // same target, clock not advanced

        Assert.Empty(transport.Features);
    }

    [Fact]
    public void ApplyPending_StaleAfterFifteenSeconds_ReassertsBothReports() {
        var (controller, transport, clock) = NewSlController();
        controller.SetTarget(0, 50);
        controller.ApplyPending();
        transport.Clear();

        clock.Advance(TimeSpan.FromSeconds(15));
        controller.ApplyPending();

        Assert.Equal(2, transport.Features.Count);
        Assert.Equal(SlManualCh0, transport.Features[0]);
        Assert.Equal(SlSpeedCh0Duty50, transport.Features[1]);
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

        Assert.Empty(transport.Features);
    }

    [Fact]
    public void ApplyPending_HonorsPerChannelStartStopAtZeroDuty() {
        // SL-Infinity is a floored family: a 0% request is the stop value 1 on a channel with
        // start/stop enabled, and the 10 spin floor on a channel with it disabled.
        var transport = new FakeHidTransport();
        var startStop = new[] { true, false, false, false };
        var controller = new FanController(0, transport, new SlV2Protocol(), startStop, new FakeClock(), new FakeLogger());
        transport.Clear();

        controller.SetTarget(0, 0);
        controller.SetTarget(1, 0);
        controller.ApplyPending();

        // Each written channel emits (manual mode, speed); assert the speed reports.
        Assert.Equal(new byte[] { 224, 32, 0, 1 }, transport.Features[1]);  // ch0 start/stop on -> stop value
        Assert.Equal(new byte[] { 224, 33, 0, 10 }, transport.Features[3]); // ch1 start/stop off -> floor 10
    }

    [Fact]
    public void Constructor_RejectsWrongLengthStartStopArray() {
        var transport = new FakeHidTransport();
        Assert.Throws<ArgumentException>(() =>
            new FanController(0, transport, new SlProtocol(), new[] { true, false }, new FakeClock(), new FakeLogger()));
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
        Assert.Equal(new byte[] { 0xE0, 0x50, 0x00 }, transport.Features[0]); // the transport pads to the feature length
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
        var controller = new FanController(0, transport, new SlProtocol(), NoStartStop, new FakeClock(), logger);

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
