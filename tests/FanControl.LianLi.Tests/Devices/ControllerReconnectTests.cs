using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Tests.Fakes;
using FanControl.LianLi.Tests.Protocol;
using FanControl.LianLi.Transport;
using FanControl.LianLi.Worker;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

/// <summary>
/// Every controller family on the real transport over the scripted native API, the fault latch,
/// the backoff and the reopen included, rather than on a fake transport that accepts every
/// transfer. The device drops off the bus, stays away while a number of reopens fail, and comes
/// back reset; whatever the timing - on which transfer the fault lands, and how many reopens fail
/// before one succeeds - its setup and the work registered to replay (in the plugin's own shape:
/// writes inside a catch-all that logs and reports failure) must reach the reopened handle, and
/// nothing may be recorded as replayed while the device is still gone.
/// </summary>
public sealed class ControllerReconnectTests {
    private const string UniPath = @"\\?\hid#vid_0cf2&pid_a102&mi_01#7&9c2f7a3&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
    private const string GalahadPath = @"\\?\hid#vid_0416&pid_7371&mi_00#fake";
    private const string TlPath = @"\\?\hid#vid_0416&pid_7372&mi_01#fake";
    private const string PumpPath = @"\\?\usb#vid_0416&pid_8051#7&2&0&1#{guid}";
    private const string TransmitterPath = @"\\?\usb#vid_0416&pid_8040#6&1&0&2#{guid}";
    private const string ReceiverPath = @"\\?\usb#vid_0416&pid_8041#6&1&0&3#{guid}";
    private const string FlexPath = @"\\?\usb#vid_43a8&pid_0101#8&3&0&2#{guid}";

    private const int ErrorDeviceNotConnected = 1167;
    private const int ErrorFileNotFound = 2;

    private readonly FakeLogger _log = new FakeLogger();
    private readonly FakeClock _clock = new FakeClock();

    private DeviceCallGate NewGate() => new DeviceCallGate(new FakeDeviceCallRunner(), new FakeDeviceCallClock(), _log);

    // The plugin's replay shape (LianLiPlugin.ApplyLighting): the transfers written in order inside
    // a catch-all that logs the failure, reports it and lets fan control continue.
    private bool Replay(IDeviceTransport transport, IReadOnlyList<KeyValuePair<bool, byte[]>> look) {
        try {
            foreach (KeyValuePair<bool, byte[]> transfer in look) {
                if (transfer.Key) {
                    transport.SetFeature(transfer.Value);
                } else {
                    transport.Write(transfer.Value);
                }
            }

            _log.Write("  lighting applied");
            return true;
        } catch (IOException ex) {
            _log.Write("  lighting apply failed, fan control continues: " + ex.Message);
            return false;
        }
    }

    // An SL-Infinity look (port 0 mode 46 with one colour, FanQuantity [4,4,4,4], MergeOrder
    // [0,1,2,3]): the four quantity reports, the colour output report, the effect, the frame, and
    // the merge order. In the Lighting build the literal bytes are checked against the encoder's.
    private static List<KeyValuePair<bool, byte[]>> SlInfinityLook() {
        var look = new List<KeyValuePair<bool, byte[]>>();
        for (int group = 1; group <= 4; group++) {
            look.Add(Feature(0xE0, 0x10, 0x60, (byte)group, 4, 0, 0));
        }

        var colours = new byte[353];
        colours[0] = 0xE0;
        colours[1] = 0x30;
        for (int fan = 0; fan < 4; fan++) {
            colours[2 + (fan * 12)] = 1;
            colours[3 + (fan * 12)] = 9;
            colours[4 + (fan * 12)] = 7;
        }

        look.Add(new KeyValuePair<bool, byte[]>(false, colours));
        look.Add(Feature(0xE0, 0x10, 0x26, 1, 0, 2, 0));
        look.Add(Feature(0xE0, 0x60, 0, 1, 0, 0, 0));
        look.Add(Feature(0xE0, 0x10, 0x63, 0, 1, 2, 3, 8));
#if ENABLE_LIGHTING
        IReadOnlyList<LightingTransfer> encoded = SlInfinityLightingEncoder.Encode(
            new[] { new LightingPortState(0, 46, 1, 0, 2, new[] { new RgbColor(1, 7, 9) }) }, new[] { 4, 4, 4, 4 }, false, new[] { 0, 1, 2, 3 });
        Assert.Equal(encoded.Count, look.Count);
        for (int i = 0; i < encoded.Count; i++) {
            Assert.Equal(encoded[i].IsFeature, look[i].Key);
            Assert.Equal(encoded[i].Report, look[i].Value);
        }
#endif
        return look;
    }

    private static KeyValuePair<bool, byte[]> Feature(params byte[] report) => new KeyValuePair<bool, byte[]>(true, report);

    private static bool IsMergeOrder(byte[] report) => report.Length >= 3 && report[0] == 0xE0 && report[1] == 0x10 && report[2] == 0x63;

    private static bool IsColourReport(byte[] report) => report.Length == 353 && report[0] == 0xE0 && report[1] == 0x30;

    // ---------- SL-Infinity over the HID transport ----------

    // The fault lands on the RPM poll's primer (the curve did not move that second).
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void SlInfinity_FaultOnAPoll_TheLookAndTheSetupReachTheReopenedHandle(int failedReopens)
        => SlInfinity(failedReopens, faultOnSpeedWrite: false);

    // The fault lands on a speed write (the curve moved that second).
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void SlInfinity_FaultOnASpeedWrite_TheLookAndTheSetupReachTheReopenedHandle(int failedReopens)
        => SlInfinity(failedReopens, faultOnSpeedWrite: true);

    private void SlInfinity(int failedReopens, bool faultOnSpeedWrite) {
        var hid = new FakeHidApi();
        var device = new HidInterface(0x0CF2, 0xA102, UniPath, new HidCapabilities(0xFF72, 65, 353, 65));
        using HidTransport transport = HidTransport.Open(device, hid, _log, NewGate(), new FakeTransferDelay(), CancellationToken.None);
        List<KeyValuePair<bool, byte[]>> look = SlInfinityLook();
        var controller = new FanController(0, transport, new SlInfinityProtocol(), new bool[4], _clock, _log);
        controller.ReplayOnReconnect(() => Replay(transport, look));
        Assert.True(Replay(transport, look));
        controller.AssertManualMode();
        controller.SetTarget(0, 50);
        using var loop = new ControllerLoop(0, controller, _log, 1000);
        loop.Tick();

        // The device drops off the bus: the next control transfer fails with
        // ERROR_DEVICE_NOT_CONNECTED, and the next failedReopens stream opens with ERROR_FILE_NOT_FOUND.
        hid.TransferResults.Enqueue(ErrorDeviceNotConnected);
        for (int i = 0; i < failedReopens; i++) {
            hid.StreamOpenErrors.Enqueue(ErrorFileNotFound);
        }

        if (faultOnSpeedWrite) {
            controller.SetTarget(0, 60);
        }

        (int featuresAtReopen, int writesAtReopen) = RunUntilReopened(loop, hid);

        Assert.DoesNotContain(_log.Messages.Take(ReopenLineIndex()), m => m.Contains("C0 reconnected"));
        List<byte[]> featuresAfter = hid.Features.Skip(featuresAtReopen).ToList();
        List<byte[]> writesAfter = hid.Written.Skip(writesAtReopen).ToList();
        Assert.Single(writesAfter, IsColourReport);
        Assert.Single(featuresAfter, IsMergeOrder);
        Assert.Contains(featuresAfter, f => f[0] == 0xE0 && f[1] == 0x20 && f[3] == (faultOnSpeedWrite ? 60 : 50)); // the duty, re-sent
        Assert.Contains(_log.Messages, m => m == "C0 reconnected: setup replayed (transport generation 1)");
        Assert.Contains(_log.Messages, m => m.StartsWith("  reopened " + UniPath + " after ", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Messages, m => m.Contains("lighting apply failed")); // never tried while the device was gone

        // The look on the reopened handle is the whole look, in order, and it is not sent again.
        int first = hid.Features.FindIndex(featuresAtReopen, f => f[0] == 0xE0 && f[1] == 0x10 && f[2] == 0x60);
        for (int i = 0, feature = first, write = writesAtReopen; i < look.Count; i++) {
            if (look[i].Key) {
                Assert.Equal(look[i].Value, hid.Features[feature++].Take(look[i].Value.Length)); // padded to the feature length by the transport
            } else {
                Assert.Equal(look[i].Value, hid.Written[write++]);
            }
        }

        loop.Tick();
        loop.Tick();
        Assert.Single(hid.Written.Skip(writesAtReopen), IsColourReport);
    }

    // How many lines were written before the transport's own "reopened" line: what was said while
    // the device was still gone.
    private int ReopenLineIndex() {
        IReadOnlyList<string> lines = _log.Messages;
        for (int i = 0; i < lines.Count; i++) {
            if (lines[i].StartsWith("  reopened ", StringComparison.Ordinal)) {
                return i;
            }
        }

        return lines.Count;
    }

    // Tick until the stream open that succeeds, then a few ticks more for the replay that follows.
    private static (int Features, int Writes) RunUntilReopened(ControllerLoop loop, FakeHidApi hid) {
        int featuresAtReopen = -1;
        int writesAtReopen = -1;
        hid.OnCall = call => {
            if (call.StartsWith("OpenStreamHandle", StringComparison.Ordinal) && hid.StreamOpenErrors.Count == 0 && featuresAtReopen < 0) {
                featuresAtReopen = hid.Features.Count;
                writesAtReopen = hid.Written.Count;
            }
        };

        for (int tick = 0; tick < 2000 && featuresAtReopen < 0; tick++) {
            loop.Tick();
        }

        Assert.True(featuresAtReopen >= 0, "the device was never reopened");
        for (int tick = 0; tick < 3; tick++) {
            loop.Tick();
        }

        return (featuresAtReopen, writesAtReopen);
    }

    // ---------- Galahad II over the HID transport ----------

    // The cooler's only setup is its look; the fault lands on the handshake write. With 0 failed
    // reopens the reopen is the immediate attempt on the next transfer.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Galahad_TheLookReachesTheReopenedCooler(int failedReopens) {
        var hid = new FakeHidApi();
        var device = new HidInterface(0x0416, 0x7371, GalahadPath, new HidCapabilities(0xFF1B, 64, 64, 0));
        using HidTransport transport = HidTransport.Open(device, hid, _log, NewGate(), new FakeTransferDelay(), CancellationToken.None);
        var look = new List<KeyValuePair<bool, byte[]>> { new KeyValuePair<bool, byte[]>(false, new byte[] { 0x01, 0x77, 1, 2, 3 }) };
        var controller = new Galahad2Controller(0, transport, _clock, _log);
        controller.ReplayOnReconnect(() => Replay(transport, look));
        controller.SetTarget(0, 50);
        using var loop = new ControllerLoop(0, controller, _log, 1000);
        loop.Tick();

        hid.BeginFailure = new IOException("device gone");
        loop.Tick();
        hid.BeginFailure = null;
        Assert.Equal(1, transport.Generation);
        for (int i = 0; i < failedReopens; i++) {
            hid.StreamOpenErrors.Enqueue(ErrorFileNotFound);
        }

        (_, int writesAtReopen) = RunUntilReopened(loop, hid);

        Assert.Single(hid.Written.Skip(writesAtReopen), w => w[1] == 0x77);
        Assert.Contains(hid.Written.Skip(writesAtReopen), w => w[1] == 0x8B); // the fan, re-sent
        Assert.Contains(_log.Messages, m => m == "G0 reconnected: setup replayed (transport generation 1)");
        Assert.DoesNotContain(_log.Messages, m => m.Contains("lighting apply failed"));
    }

    // ---------- Uni Fan TL over the HID transport ----------

    // Software control is retaken and then the look replayed, both on the reopened handle.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void TlHub_SoftwareControlAndTheLookReachTheReopenedHub(int failedReopens) {
        var hid = new FakeHidApi();
        var device = new HidInterface(0x0416, 0x7372, TlPath, new HidCapabilities(0xFF1B, 64, 64, 0));
        hid.Transfers.Enqueue(new FakeHidTransfer());
        hid.Transfers.Enqueue(new FakeHidTransfer { Reply = CommandPacket.Build(0xA1, 0x80, 0x03, 0xE8) });
        using HidTransport transport = HidTransport.Open(device, hid, _log, NewGate(), new FakeTransferDelay(), CancellationToken.None);
        var look = new List<KeyValuePair<bool, byte[]>> { new KeyValuePair<bool, byte[]>(false, new byte[] { 0x01, 0xB5, 9 }) };
        var controller = new TlFanController(0, transport, _clock, _log);
        controller.ReplayOnReconnect(() => Replay(transport, look));
        controller.SetTarget(0, 50);
        using var loop = new ControllerLoop(0, controller, _log, 1000);
        loop.Tick();

        hid.BeginFailure = new IOException("device gone");
        loop.Tick();
        hid.BeginFailure = null;
        for (int i = 0; i < failedReopens; i++) {
            hid.StreamOpenErrors.Enqueue(ErrorFileNotFound);
        }

        (_, int writesAtReopen) = RunUntilReopened(loop, hid);

        List<byte[]> after = hid.Written.Skip(writesAtReopen).ToList();
        int control = after.FindIndex(w => w[1] == 0xB1);
        int lookAt = after.FindIndex(w => w[1] == 0xB5);
        Assert.True(control >= 0 && lookAt > control, "software control, then the look");
        Assert.Contains(_log.Messages, m => m == "T0 reconnected: setup replayed (transport generation 1)");
        Assert.DoesNotContain(_log.Messages, m => m.Contains("lighting apply failed"));
    }

    // ---------- HydroShift II OLED Curve over the WinUSB transport ----------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void HydroShiftCurve_TheReplayAndSoftwareControlReachTheReopenedMcu(int failedReopens) {
        var api = new FakeWinUsbApi {
            ReplyFor = packet => packet[0] switch {
                0x60 => new byte[] { 0x60, 30, 1 },
                0x62 => new byte[] { 0x62, 0x07, 0x3A },
                _ => new[] { packet[0] },
            },
        };
        using WinUsbTransport transport = WinUsbTransport.Open(PumpPath, WinUsbPipePolicy.PumpMcu, _log, api, NewGate(), new FakeTransferDelay(), CancellationToken.None);
        var work = new List<KeyValuePair<bool, byte[]>> { new KeyValuePair<bool, byte[]>(false, new byte[] { 0x70, 1, 0, 0, 0, 0, 0, 0 }) };
        var controller = new HydroShiftCurveController(0, transport, _clock, _log);
        controller.ReplayOnReconnect(() => Replay(transport, work));
        controller.AssertSoftwareControl();
        controller.SetTarget(0, 50);
        using var loop = new ControllerLoop(0, controller, _log, 1000);
        loop.Tick();

        api.WriteResults.Enqueue((false, 0, 31));
        for (int i = 0; i < failedReopens; i++) {
            api.OpenDeviceErrors.Enqueue(ErrorFileNotFound);
        }

        int writesAtReopen = RunUntilReopened(api, loop.Tick);

        List<byte[]> after = api.Written.Skip(writesAtReopen).ToList();
        int replayAt = after.FindIndex(w => w[0] == 0x70);
        int controlAt = after.FindIndex(w => w[0] == 0x64);
        int pumpAt = after.FindIndex(w => w[0] == 0x61);
        Assert.True(replayAt >= 0 && controlAt > replayAt && pumpAt > controlAt, "the replay, then software control, then the pump");
        Assert.Contains(_log.Messages, m => m == "H0 reconnected: setup replayed (transport generation 1)");
        Assert.DoesNotContain(_log.Messages, m => m.Contains("lighting apply failed"));
    }

    // Tick until the device open that succeeds, then a few ticks more for the replay that follows.
    // Returns how many packets had been written to the device - or whatever countWritten counts -
    // at that moment.
    private static int RunUntilReopened(FakeWinUsbApi api, Action tick, Func<int>? countWritten = null) {
        int writesAtReopen = -1;
        api.OnCall = call => {
            if (call.StartsWith("OpenDevice", StringComparison.Ordinal) && api.OpenDeviceErrors.Count == 0 && writesAtReopen < 0) {
                writesAtReopen = countWritten is null ? api.Written.Count : countWritten();
            }
        };

        for (int i = 0; i < 2000 && writesAtReopen < 0; i++) {
            tick();
        }

        Assert.True(writesAtReopen >= 0, "the device was never reopened");
        for (int i = 0; i < 3; i++) {
            tick();
        }

        return writesAtReopen;
    }

    // ---------- the L-Wireless dongles over the WinUSB transport ----------

    // A water block's screen switch was acknowledged and done; the transmitter drops off the bus.
    // Nothing is replayed, and nothing said, while it is gone; once it is back the cycle resumes
    // on the reopened dongle - the pump's parameters go out again - and so does the switch, since
    // L-Connect's service builds a new controller when the transmitter is plugged in again and
    // that one switches every block again (MainService.usbDeviceWatcher_Inserted, ApplyAll).
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Wireless_TransmitterGone_TheCycleResumesOnceItIsBack_WithTheScreenSwitchAgain(int failedReopens) {
        var rig = new FakeWirelessRig();
        var state = new WirelessProcessState();
        (WirelessController controller, FakeWinUsbApi transmitter, _) = NewWireless(rig, state);
        using (controller) {
            SwitchTheBlocksScreen(rig, controller);
            transmitter.WriteResults.Enqueue((false, 0, 31));
            for (int i = 0; i < failedReopens; i++) {
                transmitter.OpenDeviceErrors.Enqueue(ErrorFileNotFound);
            }

            int writesAtReopen = RunUntilReopened(transmitter, () => Tick(controller));

            AssertReappliedAfterTheReopen(controller, transmitter, writesAtReopen, screenSwitchAgain: true);
            Assert.Contains(_log.Messages, m => m.StartsWith("W0: the transmitter was lost (generation 1)", StringComparison.Ordinal));
            Assert.Contains("W0 reconnected (transmitter generation 1, receiver generation 0)", _log.Messages);
            Assert.Equal(1, state.TransmitterLifetime);
        }
    }

    // The same with the receiver gone: its list read fails and it is reopened on the backoff; the
    // cycle resumes only once it is back, and the switch is not sent again, as L-Connect's service
    // has no controller for the receiver and its RF layer reopens it by itself.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Wireless_ReceiverGone_TheCycleResumesOnceItIsBack_WithoutTheScreenSwitch(int failedReopens) {
        var rig = new FakeWirelessRig();
        var state = new WirelessProcessState();
        (WirelessController controller, FakeWinUsbApi transmitter, FakeWinUsbApi receiver) = NewWireless(rig, state);
        using (controller) {
            SwitchTheBlocksScreen(rig, controller);
            receiver.Replies.Enqueue((null, 31));
            for (int i = 0; i < failedReopens; i++) {
                receiver.OpenDeviceErrors.Enqueue(ErrorFileNotFound);
            }

            int writesAtReopen = RunUntilReopened(receiver, () => Tick(controller), () => transmitter.Written.Count);

            AssertReappliedAfterTheReopen(controller, transmitter, writesAtReopen, screenSwitchAgain: false);
            Assert.DoesNotContain(_log.Messages, m => m.Contains("the transmitter was lost"));
            Assert.Contains("W0 reconnected (transmitter generation 0, receiver generation 1)", _log.Messages);
            Assert.Equal(0, state.TransmitterLifetime);
        }
    }

    // The transmitter drops off the bus and FanControl refreshes before the backoff has reopened
    // it: the closing controller counted the loss when its transfer faulted, so the controller
    // built over fresh transports, with the transmitter back, switches the block's screen again.
    [Fact]
    public void Wireless_TransmitterGone_ThenARefreshBeforeItIsBack_TheNextControllerSwitchesTheScreenAgain() {
        var rig = new FakeWirelessRig();
        var state = new WirelessProcessState();
        (WirelessController controller, FakeWinUsbApi transmitter, _) = NewWireless(rig, state);
        using (controller) {
            SwitchTheBlocksScreen(rig, controller);
            transmitter.WriteResults.Enqueue((false, 0, 31));
            transmitter.OpenDeviceErrors.Enqueue(ErrorFileNotFound);
            transmitter.OpenDeviceErrors.Enqueue(ErrorFileNotFound);
            Tick(controller); // the fault lands in this tick's master query, after the tick counted nothing

            Assert.Contains(_log.Messages, m => m.StartsWith("W0 master failed: WinUsb_WritePipe failed", StringComparison.Ordinal));
            Assert.DoesNotContain(_log.Messages, m => m.Contains("the transmitter was lost"));
            Assert.DoesNotContain(_log.Messages, m => m.Contains("W0 reconnected"));
        }

        Assert.Contains(_log.Messages, m => m.StartsWith("W0: the transmitter was lost (generation 1)", StringComparison.Ordinal)); // counted as the controller closed
        Assert.False(state.IsScreenSwitched("a00000000001"));
        (WirelessController next, FakeWinUsbApi nextTransmitter, _) = NewWireless(rig, state);
        using (next) {
            next.SetTarget(0, 0);
            Tick(next);
            Tick(next);

            Assert.Contains(nextTransmitter.Written, w => w[0] == 0x10 && w[1] == 0 && w[5] == 0x19);
        }
    }

    // The transmitter drops off the bus during the next controller's discovery, before it has
    // heard a single device: the loss is counted as the discovery ends, and once the transmitter
    // is reopened the block's screen is switched again by that controller.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Wireless_TransmitterGoneDuringDiscovery_TheScreenIsSwitchedAgainOnceItIsBack(int failedReopens) {
        var rig = new FakeWirelessRig();
        var state = new WirelessProcessState();
        (WirelessController controller, _, _) = NewWireless(rig, state);
        using (controller) {
            SwitchTheBlocksScreen(rig, controller);
        }

        Assert.True(state.IsScreenSwitched("a00000000001"));
        (WinUsbTransport transmitterTransport, WinUsbTransport receiverTransport, FakeWinUsbApi transmitter, _) = OpenDongles(rig);
        transmitter.WriteResults.Enqueue((false, 0, 31));
        for (int i = 0; i < failedReopens; i++) {
            transmitter.OpenDeviceErrors.Enqueue(ErrorFileNotFound);
        }

        using (var next = new WirelessController(
            0, transmitterTransport, receiverTransport, new FakeWirelessConfiguration(), _clock, new FakeDelay(_clock), _log, state)) {
            Assert.Contains("W0: the transmitter has not reported its master yet; asking again every second", _log.Messages);
            Assert.Contains(_log.Messages, m => m.StartsWith("W0: the transmitter was lost (generation 1)", StringComparison.Ordinal));
            Assert.False(state.IsScreenSwitched("a00000000001"));

            int writesAtReopen = RunUntilReopened(transmitter, () => Tick(next));
            next.SetTarget(0, 0);
            Tick(next);
            Tick(next);

            Assert.DoesNotContain(_log.Messages, m => m.Contains("W0 reconnected")); // the loss came before the controller set anything up
            Assert.Contains(transmitter.Written.Skip(writesAtReopen), w => w[0] == 0x10 && w[1] == 0 && w[5] == 0x19);
        }
    }

    // An LCD FLEX group's first table has gone out and its colours are not yet due when the
    // transmitter drops off the bus in the next cycle's master query, and the immediate reopen
    // fails. A later cycle enters with the transmitter still faulted, so the tick's reconnect
    // replay waits, and the cycle's own master query reopens the handle on the backoff: the clock
    // that then completes is the first on the reopened handle, and the colours wait 1.2 s from it
    // rather than running from the table sent before the loss. The tick after replays the
    // reconnect, and the wait runs from the clock that sends.
    [Fact]
    public void Wireless_TransmitterReopenedInsideACycle_TheColoursWaitForTheClockSentOnTheReopenedHandle() {
        var rig = new FakeWirelessRig();
        var group = new FakeWirelessRecord(new byte[] { 0xA0, 0, 0, 0, 0, 1 }, FakeWirelessRig.MasterMac) {
            FanCountByte = 1,
            FanTypes = new byte[] { 51, 0, 0, 0 },
        };
        rig.Records.Add(group);
        var configuration = new FakeWirelessConfiguration();
        configuration.FanScreens["a00000000001"] = new WirelessFanScreenPresentation(new byte[] { 1, 2, 3, 4 }, new byte[4], 60, 1, false);
        (WinUsbTransport transmitterTransport, WinUsbTransport receiverTransport, FakeWinUsbApi transmitter, _) = OpenDongles(rig);
        using var controller = new WirelessController(
            0, transmitterTransport, receiverTransport, configuration, _clock, new FakeDelay(_clock), _log, new WirelessProcessState());
        Tick(controller); // the first table; the colours are due 1.2 s on
        transmitter.WriteResults.Enqueue((false, 0, 31));
        transmitter.OpenDeviceErrors.Enqueue(ErrorFileNotFound);
        Tick(controller); // the master query faults; the clock's first packet tries to reopen and fails
        Assert.True(transmitterTransport.IsFaulted);
        Assert.Contains(_log.Messages, m => m.StartsWith("W0 master failed: WinUsb_WritePipe failed", StringComparison.Ordinal));

        DateTime? reopenedAt = null;
        DateTime? clockCompleted = null;
        DateTime? firstColours = null;
        bool inClock = false;
        transmitter.OnCall = call => {
            if (call.StartsWith("OpenDevice", StringComparison.Ordinal) && transmitter.OpenDeviceErrors.Count == 0 && reopenedAt is null) {
                reopenedAt = _clock.UtcNow;
            }
        };
        transmitter.ReplyFor = packet => {
            if (reopenedAt != null && packet[0] == 0x10) {
                if (packet[1] == 0) {
                    inClock = packet[5] == 0x14;
                    if (packet[5] == 0x28 && firstColours is null) {
                        firstColours = _clock.UtcNow;
                    }
                }

                if (inClock && packet[1] == 3 && clockCompleted is null) {
                    clockCompleted = _clock.UtcNow;
                }
            }

            return packet[0] == 0x11 ? rig.MasterReply() : null;
        };
        for (int tick = 0; tick < 30 && transmitterTransport.IsFaulted; tick++) {
            Tick(controller); // the reopen comes inside the cycle's master query, after the tick's replay check
        }

        Assert.False(transmitterTransport.IsFaulted);
        Assert.Contains(_log.Messages, m => m.StartsWith("W0: the transmitter was lost (generation 1)", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Messages, m => m.Contains("W0 reconnected"));
        Assert.NotNull(clockCompleted);
        Assert.Null(firstColours); // more than 1.2 s since the table before the loss, and none sent: that table went out on the handle since lost
        Tick(controller); // the reconnect replay, and the clock it sends
        Assert.Contains("W0 reconnected (transmitter generation 1, receiver generation 0)", _log.Messages);
        Assert.Null(firstColours);
        Tick(controller);
        Tick(controller);

        Assert.NotNull(firstColours);
        Assert.True(firstColours.Value - clockCompleted.Value >= TimeSpan.FromMilliseconds(1200));
    }

    // Both dongles opened on the real transport over fresh scripted APIs, before anything is scripted to fail.
    private (WinUsbTransport Transmitter, WinUsbTransport Receiver, FakeWinUsbApi TransmitterApi, FakeWinUsbApi ReceiverApi) OpenDongles(FakeWirelessRig rig) {
        var transmitter = new FakeWinUsbApi { ReplyFor = packet => packet[0] == 0x11 ? rig.MasterReply() : null };
        var receiver = new FakeWinUsbApi { ReplyFor = packet => packet[0] == 0x10 ? rig.ListReply(packet[1]) : null };
        DeviceCallGate gate = NewGate();
        WinUsbTransport transmitterTransport = WinUsbTransport.Open(TransmitterPath, WinUsbPipePolicy.Dongle, _log, transmitter, gate, new FakeTransferDelay(), CancellationToken.None);
        WinUsbTransport receiverTransport = WinUsbTransport.Open(ReceiverPath, WinUsbPipePolicy.Dongle, _log, receiver, gate, new FakeTransferDelay(), CancellationToken.None);
        return (transmitterTransport, receiverTransport, transmitter, receiver);
    }

    private (WirelessController Controller, FakeWinUsbApi Transmitter, FakeWinUsbApi Receiver) NewWireless(FakeWirelessRig rig, WirelessProcessState state) {
        (WinUsbTransport transmitterTransport, WinUsbTransport receiverTransport, FakeWinUsbApi transmitter, FakeWinUsbApi receiver) = OpenDongles(rig);
        var controller = new WirelessController(
            0, transmitterTransport, receiverTransport, new FakeWirelessConfiguration(), _clock, new FakeDelay(_clock), _log, state);
        return (controller, transmitter, receiver);
    }

    private void Tick(WirelessController controller) {
        _clock.Advance(TimeSpan.FromSeconds(1));
        controller.ApplyPending();
        controller.PollRpm();
    }

    // A water block whose pump FanControl drives, its screen switch sent and acknowledged, so the
    // switch is done with for the process.
    private void SwitchTheBlocksScreen(FakeWirelessRig rig, WirelessController controller) {
        var block = new FakeWirelessRecord(new byte[] { 0xA0, 0, 0, 0, 0, 1 }, FakeWirelessRig.MasterMac) { DeviceType = 10 };
        rig.Records.Add(block);
        Tick(controller);
        controller.SetTarget(0, 0);
        Tick(controller);
        block.Sequence = 1;
        Tick(controller);
        Tick(controller);
        Assert.Contains("W0:a00000000001 switched its screen to its wireless theme", _log.Messages);
    }

    // Nothing was said about a reconnect while the dongle was gone, and once it is back the pump's
    // parameter block goes out again with the cycle, and the screen switch only after the
    // transmitter's return (the first chunk of a payload: the command at packet byte 5).
    private void AssertReappliedAfterTheReopen(WirelessController controller, FakeWinUsbApi transmitter, int writesAtReopen, bool screenSwitchAgain) {
        Assert.DoesNotContain(_log.Messages.Take(ReopenLineIndex()), m => m.Contains("W0 reconnected"));
        Tick(controller);
        List<byte[]> after = transmitter.Written.Skip(writesAtReopen).ToList();
        Assert.Contains(after, w => w[0] == 0x10 && w[1] == 0 && w[5] == 0x21);
        Assert.Equal(screenSwitchAgain, after.Any(w => w[0] == 0x10 && w[1] == 0 && w[5] == 0x19));
    }

    // ---------- a FLEX receiver over the WinUSB transport ----------

    // The receiver identifies the device on the reopened path first, then the registered work
    // and the duty follow, after as many failed reopens as it takes.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void FlexReceiver_IdentifiesThenReplaysOnTheReopenedPath(int failedReopens) {
        byte[] status = FlexReceiverProtocolTests.StatusReply(new FakeWirelessRecord(new byte[] { 0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6 }, new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 }) {
            FanCountByte = 2,
            FanTypes = new byte[] { 51, 51, 51, 51 },
            Rpm = new[] { 1000, 1000, 0, 0 },
        });
        var api = new FakeWinUsbApi { ReplyFor = packet => packet[0] == 0x12 ? status : new byte[] { 0x13, 0 } };
        using WinUsbTransport transport = WinUsbTransport.Open(FlexPath, WinUsbPipePolicy.FlexReceiver, _log, api, NewGate(), new FakeTransferDelay(), CancellationToken.None);
        var work = new List<KeyValuePair<bool, byte[]>> { new KeyValuePair<bool, byte[]>(false, new byte[] { 0x70, 1 }) };
        using var controller = new FlexReceiverController(0, transport, FlexReceiverFamily.TlFlex, _clock, _log, new WirelessProcessState());
        controller.SettleOwnership();
        controller.ReplayOnReconnect(() => Replay(transport, work));
        controller.SetTarget(0, 40);
        using var loop = new ControllerLoop(0, controller, _log, 1000);
        loop.Tick();

        api.WriteResults.Enqueue((false, 0, 31));
        for (int i = 0; i < failedReopens; i++) {
            api.OpenDeviceErrors.Enqueue(ErrorFileNotFound);
        }

        int writesAtReopen = RunUntilReopened(api, loop.Tick);

        List<byte[]> after = api.Written.Skip(writesAtReopen).ToList();
        Assert.Equal(new byte[] { 0x12, 0x70, 0x13 }, after.Take(3).Select(w => w[0]));
        Assert.Contains("F0:a1b2c3d4e5f6 reconnected and answers as the same receiver (transport generation 1)", _log.Messages);
        Assert.DoesNotContain(_log.Messages, m => m.Contains("lighting apply failed"));
    }
}
