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
    public void Dispose_DisposesTransport() {
        var (controller, transport, _) = NewSlController();
        controller.Dispose();
        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public void PollRpm_SlInfinity_SendsPrimerFeatureReportBeforeRead() {
        var transport = new FakeHidTransport();
        var controller = new FanController(0, transport, new SlInfinityProtocol(), new FakeClock(), new FakeLogger());
        transport.Clear();

        controller.PollRpm();

        // The primer must appear as a feature report before the (implicit) input read.
        Assert.NotEmpty(transport.Features);
        Assert.Equal(65, transport.Features[0].Length);
        Assert.Equal(0xE0, transport.Features[0][0]);
        Assert.Equal(0x50, transport.Features[0][1]);
    }

    [Fact]
    public void PollRpm_Sl_SendsNoPrimerFeatureReport() {
        var (controller, transport, _) = NewSlController();
        transport.Clear();

        controller.PollRpm();

        Assert.Empty(transport.Features);
    }
}
