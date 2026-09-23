using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Tests.Fakes;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

/// <summary>
/// The wireless controller played out against a fake transmitter and receiver: discovery, the
/// sensors each device gets, the one-second cycle of MasterDevice.Run (speed resend, save, master
/// query, water block parameters, clock), effect replay, and every way a device or dongle can go
/// wrong. Time only moves through the fake clock and the fake delay. Every payload is compared whole.
/// </summary>
public sealed class WirelessControllerTests : IDisposable {
    private static readonly byte[] Master = FakeWirelessRig.MasterMac;
    private static readonly byte[] Other = { 0x99, 0x99, 0x99, 0x99, 0x99, 0x99 };
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private readonly FakeWirelessRig _rig = new FakeWirelessRig();
    private readonly FakeClock _clock = new FakeClock();
    private readonly FakeLogger _log = new FakeLogger();
    private readonly FakeWirelessConfiguration _configuration = new FakeWirelessConfiguration();
    private readonly WirelessProcessState _processState = new WirelessProcessState();
    private readonly FakeDelay _delay;
    private WirelessController? _controller;

    public WirelessControllerTests() => _delay = new FakeDelay(_clock);

    public void Dispose() => _controller?.Dispose();

    private WirelessController Controller => _controller ??= Build();

    private static byte[] Mac(int last) => new byte[] { 0xA0, 0, 0, 0, 0, (byte)last };

    private static string MacText(int last) => "a000000000" + last.ToString("x2");

    private static byte[] B(params int[] values) => Array.ConvertAll(values, v => (byte)v);

    private static byte[] Expected(int length, params (int Offset, byte[] Bytes)[] parts) {
        var buffer = new byte[length];
        foreach ((int offset, byte[] bytes) in parts) {
            Array.Copy(bytes, 0, buffer, offset, bytes.Length);
        }

        return buffer;
    }

    // MasterDevice.SyncPwm's payload for a device.
    private static byte[] SpeedPayload(int last, int receiver, int channel, int bindIndex, params int[] pwm)
        => Expected(240, (0, B(0x12, 0x10)), (2, Mac(last)), (8, Master), (14, B(receiver, channel, bindIndex)), (17, B(pwm)));

    private static FakeWirelessRecord Group(int last, int fans = 2, int fanType = 36, int receiver = 1, int pwm = 100, byte[]? master = null)
        => new FakeWirelessRecord(Mac(last), master ?? Master) {
            Receiver = (byte)receiver,
            FanCountByte = (byte)fans,
            FanTypes = Enumerable.Range(0, 4).Select(slot => slot < fans ? (byte)fanType : (byte)0).ToArray(),
            Rpm = Enumerable.Range(0, 4).Select(slot => slot < fans ? 1000 + slot : 0).ToArray(),
            Pwm = B(pwm, pwm, pwm, pwm),
        };

    private static FakeWirelessRecord Device(int last, int type, int fans = 0, int receiver = 1)
        => new FakeWirelessRecord(Mac(last), Master) { DeviceType = (byte)type, FanCountByte = (byte)fans, Receiver = (byte)receiver };

    private WirelessController Build() {
        _controller = new WirelessController(4, _rig.Transmitter, _rig.Receiver, _configuration, _clock, _delay, _log, _processState);
        return _controller;
    }

    // One worker tick a second later: ApplyPending then PollRpm, as KeepAliveWorker runs them.
    private void Tick() {
        _clock.Advance(Second);
        Controller.ApplyPending();
        Controller.PollRpm();
    }

    private List<(byte[] Header, byte[] Payload)> Payloads(int command) => _rig.Payloads().Where(p => p.Payload[1] == command).ToList();

    private List<(byte[] Header, byte[] Payload)> SpeedPayloads() => Payloads(0x10);

    private void ClearWrites() {
        _rig.Transmitter.Writes.Clear();
        _rig.Receiver.Writes.Clear();
    }

    private int ControlIndex(string id) {
        for (int channel = 0; channel < Controller.ChannelCount; channel++) {
            if (Controller.Describe(channel).ControlId == id) {
                return channel;
            }
        }

        throw new InvalidOperationException(id);
    }

    private List<string> FanSpeedIds()
        => Enumerable.Range(0, Controller.FanSpeedCount).Select(i => Controller.DescribeFanSpeed(i).Id).ToList();

    // ---------- construction ----------

    // RFController.Init: the master query (on the default channel 8) first, then GetChannel for
    // that master's file; MasterDevice.RefreshList reads the list, one page first.
    [Fact]
    public void Constructor_QueriesTheMasterThenReadsTheListUntilItIsStable() {
        _rig.Records.Add(Group(1));
        _configuration.Channels["112233445566"] = 21;

        WirelessController controller = Build();

        Assert.Equal(Expected(64, (0, B(0x11, 8))), Assert.Single(_rig.Transmitter.Writes));
        Assert.Equal(2, _rig.Receiver.Writes.Count);
        Assert.All(_rig.Receiver.Writes, w => Assert.Equal(Expected(64, (0, B(0x10, 1))), w));
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(500) }, _delay.Waits);
        Assert.Equal("112233445566", controller.MasterMacText);
        Assert.Equal(21, controller.Channel);
        Assert.Equal(1, controller.DeviceCount);
        Assert.Equal(1, controller.ChannelCount);
        Assert.Equal(2, controller.FanSpeedCount);
        Assert.Contains("W4: master 112233445566, RF channel 21 (saved by L-Connect)", _log.Messages);
    }

    [Fact]
    public void Constructor_WithoutASavedChannelStaysOnTheDefault() {
        WirelessController controller = Build();

        Assert.Equal(8, controller.Channel);
        Assert.Contains("W4: master 112233445566, RF channel 8 (default)", _log.Messages);
    }

    // The saved channel is used from the next query on (MasterDevice.Run re-sends it every second).
    [Fact]
    public void TheSavedChannel_GoesOutOnEveryLaterQuery() {
        _configuration.Channels["112233445566"] = 21;
        Build();
        ClearWrites();

        Tick();

        Assert.Equal(Expected(64, (0, B(0x11, 21))), Assert.Single(_rig.Transmitter.Writes, w => w[0] == 0x11));
    }

    [Fact]
    public void Constructor_AsksForTheMasterUpToThreeTimes() {
        int queries = 0;
        _rig.Transmitter.Responder = packet => packet[0] == 0x11 && ++queries == 3 ? _rig.MasterReply() : null;

        WirelessController controller = Build();

        Assert.Equal("112233445566", controller.MasterMacText);
        Assert.Equal(3, _rig.Transmitter.Writes.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(500), _delay.Waits[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(500), _delay.Waits[1]);
        Assert.Contains("W4 master failed: the transmitter did not answer the master query", _log.Messages);
        Assert.Contains("W4 master recovered", _log.Messages);
    }

    // A master that never answers leaves a live controller that keeps asking (MasterInite ignores
    // QuerryMasterMac's result and Run asks every second); RefreshList reads nothing without one.
    [Fact]
    public void AMasterThatNeverAnswers_LeavesALiveControllerThatKeepsAsking() {
        _rig.MasterAnswers = false;
        _rig.Records.Add(Group(1));
        int topologyChanges = 0;

        WirelessController controller = Build();
        controller.TopologyChanged += (_, _) => topologyChanges++;

        Assert.Null(controller.MasterMacText);
        Assert.Equal(0, controller.ChannelCount);
        Assert.Equal(3, _rig.Transmitter.Writes.Count);
        Assert.Empty(_rig.Receiver.Writes);
        Assert.Contains("W4: the transmitter has not reported its master yet; asking again every second", _log.Messages);

        Tick();
        Assert.Equal(4, _rig.Transmitter.Writes.Count); // the query, and nothing else without a master
        Assert.Empty(_rig.Receiver.Writes);

        _rig.MasterAnswers = true;
        Tick();
        Assert.Equal("112233445566", controller.MasterMacText);
        Assert.Equal(1, controller.ChannelCount);
        Assert.Equal(1, topologyChanges);
    }

    // QuerryMasterMac returns false for SysClock == 0 before it takes the address.
    [Fact]
    public void AMasterWhoseClockHasNotStarted_IsNotTaken() {
        _rig.MasterClockTicks = 0;

        WirelessController controller = Build();

        Assert.Null(controller.MasterMacText);
        Assert.Contains("W4 master failed: the transmitter has not started its clock", _log.Messages);
    }

    // With no master known nothing refreshes the readings, so after the usual 30 reads they stop
    // looking live: a fan reads 0 rather than its last speed for ever.
    [Fact]
    public void AMasterWithNoAddress_LetsTheReadingsGoStale() {
        FakeWirelessRecord group = Group(1);
        group.Rpm = new[] { 1000, 1000, 1000, 0 };
        _rig.Records.Add(group);
        Build();
        Tick();
        Assert.Equal(1000f, Controller.GetFanSpeed(0));

        _rig.Master = new byte[6];
        for (int read = 0; read < 40; read++) {
            Tick();
        }

        Assert.Equal(0f, Controller.GetFanSpeed(0));
    }

    // QuerryMasterMac copies an all-zero address over the one it had: the master is unknown until
    // the next good reply, and RefreshList reads nothing meanwhile.
    [Fact]
    public void AMasterReportingNoAddress_IsUnknownUntilItReportsOneAgain() {
        _rig.Records.Add(Group(1));
        Build();
        _rig.Master = new byte[6];
        Tick();
        Assert.Contains("W4 master failed: the transmitter reports no address", _log.Messages);
        ClearWrites();

        Tick();
        Assert.Empty(_rig.Receiver.Writes);
        Assert.Single(_rig.Transmitter.Writes); // only the query

        _rig.Master = (byte[])Master.Clone();
        Tick();
        Assert.Single(_rig.Receiver.Writes);
        Assert.Single(_log.Messages, m => m.StartsWith("W4: master 112233445566", StringComparison.Ordinal));
    }

    [Fact]
    public void AMasterThatChangesAddress_IsLearntAgainWithItsOwnChannel() {
        Build();
        _configuration.Channels["aabbccddeeff"] = 17;
        _rig.Master = B(0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF);

        Tick();

        Assert.Equal("aabbccddeeff", Controller.MasterMacText);
        Assert.Equal(17, Controller.Channel);
    }

    [Fact]
    public void Constructor_NeverThrowsForAFailingDongle() {
        _rig.Transmitter.FailWrite = _ => true;
        _rig.Receiver.ReadFailure = new IOException("gone");

        WirelessController controller = Build();

        Assert.Null(controller.MasterMacText);
        Assert.Contains("W4 master failed: simulated write failure", _log.Messages);
        Assert.Single(_log.Messages, m => m.Contains("master failed"));
    }

    // The startup wait reads the list until two successful reads agree, at most four reads.
    [Fact]
    public void Constructor_KeepsReadingWhileTheListIsStillChanging() {
        int reads = 0;
        _rig.Receiver.Responder = packet => {
            reads++;
            if (_rig.Records.Count < 3) {
                _rig.Records.Add(Group(_rig.Records.Count + 1, receiver: _rig.Records.Count + 1));
            }

            return _rig.ListReply(packet[1]);
        };

        WirelessController controller = Build();

        Assert.Equal(4, reads);
        Assert.Equal(3, controller.DeviceCount);
        Assert.Equal(3, _delay.Waits.Count);
    }

    [Fact]
    public void Constructor_GivesUpWaitingAfterFourReads() {
        _rig.Receiver.Responder = packet => {
            _rig.Records.Add(Group(_rig.Records.Count + 1, receiver: _rig.Records.Count + 1));
            return _rig.ListReply(packet[1]);
        };

        WirelessController controller = Build();

        Assert.Equal(4, _rig.Receiver.Writes.Count);
        Assert.Equal(4, controller.DeviceCount);
    }

    // A failed read never counts as the list agreeing with itself.
    [Fact]
    public void Constructor_AFailedReadIsNotAStableList() {
        int reads = 0;
        _rig.Records.Add(Group(1));
        _rig.Receiver.Responder = packet => ++reads <= 2 ? null : _rig.ListReply(packet[1]);

        WirelessController controller = Build();

        Assert.Equal(4, reads);
        Assert.Equal(1, controller.DeviceCount);
    }

    // The startup wait re-sends the master query each second, as Run does.
    [Fact]
    public void Constructor_ReassertsTheChannelEverySecondWhileItWaits() {
        _configuration.Channels["112233445566"] = 21;
        _rig.Receiver.Responder = packet => {
            _rig.Records.Add(Group(_rig.Records.Count + 1, receiver: _rig.Records.Count + 1));
            return _rig.ListReply(packet[1]);
        };

        Build();

        Assert.Equal(new[] { 8, 21 }, _rig.Transmitter.Writes.Select(w => (int)w[1]));
    }

    [Fact]
    public void Constructor_RejectsMissingDependencies() {
        IDeviceTransport tx = _rig.Transmitter;
        IDeviceTransport rx = _rig.Receiver;
        Assert.Throws<ArgumentNullException>(() => new WirelessController(0, tx, rx, null!, _clock, _delay, _log, _processState));
        Assert.Throws<ArgumentNullException>(() => new WirelessController(0, tx, rx, _configuration, null!, _delay, _log, _processState));
        Assert.Throws<ArgumentNullException>(() => new WirelessController(0, tx, rx, _configuration, _clock, null!, _log, _processState));
        Assert.Throws<ArgumentNullException>(() => new WirelessController(0, tx, rx, _configuration, _clock, _delay, null!, _processState));
        Assert.Throws<ArgumentNullException>(() => new WirelessController(0, tx, rx, _configuration, _clock, _delay, _log, null!));
        Assert.Throws<ArgumentNullException>(() => new WirelessController(0, null!, rx, _configuration, _clock, _delay, _log, _processState));
        Assert.Throws<ArgumentNullException>(() => Build().ReplayOnReconnect(null!));
    }

    [Fact]
    public void Dispose_ReleasesBothDongles() {
        Build().Dispose();

        Assert.Equal(1, _rig.Transmitter.DisposeCount);
        Assert.Equal(1, _rig.Receiver.DisposeCount);
    }

    // ---------- sensors ----------

    // LWirelessDevice.SetFanSpeed repeats one duty across the four slots: one control per group,
    // one RPM reading per fan, all keyed on the RF address.
    [Fact]
    public void AFanGroup_GetsOneControlAndAReadingPerFan() {
        _rig.Records.Add(Group(1, fans: 3, fanType: 20));

        ChannelDescriptor control = Controller.Describe(0);

        Assert.Equal("LianLi/wa00000000001/ctl", control.ControlId);
        Assert.Equal("Lian Li UNI FAN SL V3 Wireless 000001", control.ControlName);
        Assert.Equal("LianLi/wa00000000001/f0/fan", control.RpmId);
        Assert.Equal("Lian Li UNI FAN SL V3 Wireless 000001 Fan 1 RPM", control.RpmName);
        Assert.True(Controller.IsChannelPopulated(0));
        Assert.Equal(new[] { "LianLi/wa00000000001/f0/fan", "LianLi/wa00000000001/f1/fan", "LianLi/wa00000000001/f2/fan" }, FanSpeedIds());
        Assert.Equal("Lian Li UNI FAN SL V3 Wireless 000001 Fan 3 RPM", Controller.DescribeFanSpeed(2).Name);
        Assert.Equal(1002f, Controller.GetFanSpeed(2));
        Assert.Equal(1000f, Controller.GetRpm(0));
        Assert.Equal(0, Controller.TemperatureCount);
    }

    [Theory]
    [InlineData(20, "UNI FAN SL V3")]
    [InlineData(28, "UNI FAN TL V2")]
    [InlineData(36, "UNI FAN SL-Infinity")]
    [InlineData(41, "UNI FAN CL")]
    [InlineData(40, "UNI FAN")]
    public void AFanGroup_IsNamedForItsFirstFansFamily(int fanType, string product) {
        _rig.Records.Add(Group(1, fanType: fanType));

        Assert.Equal("Lian Li " + product + " Wireless 000001", Controller.Describe(0).ControlName);
    }

    // NeedSyncPwm never writes a device without fans, so such a group has no control until it has one.
    [Fact]
    public void AGroupWithoutFans_GetsItsControlWhenItReportsSome() {
        _rig.Records.Add(Group(1, fans: 0));
        int topologyChanges = 0;
        Controller.TopologyChanged += (_, _) => topologyChanges++;
        Assert.Equal(0, Controller.ChannelCount);

        _rig.Records[0] = Group(1, fans: 2);
        Tick();

        Assert.Equal(1, Controller.ChannelCount);
        Assert.Equal(2, Controller.FanSpeedCount);
        Assert.Equal(1, topologyChanges);
        Tick();
        Assert.Equal(1, topologyChanges);
    }

    [Fact]
    public void AGroupThatReportsMoreFans_GetsTheirReadingsAndAsksForARefresh() {
        _rig.Records.Add(Group(1, fans: 2));
        int topologyChanges = 0;
        Controller.TopologyChanged += (_, _) => topologyChanges++;

        _rig.Records[0] = Group(1, fans: 3);
        Tick();

        Assert.Equal(1, Controller.ChannelCount);
        Assert.Equal(3, Controller.FanSpeedCount);
        Assert.Equal(1, topologyChanges);

        // Fewer fans later takes nothing away: the index keeps naming the same fan.
        _rig.Records[0] = Group(1, fans: 1);
        Tick();
        Assert.Equal(3, Controller.FanSpeedCount);
        Assert.Equal(0f, Controller.GetFanSpeed(2));
    }

    // A late device with nobody subscribed is adopted all the same.
    [Fact]
    public void ALateDevice_IsAdoptedWithoutASubscriber() {
        Build();
        _rig.Records.Add(Group(1));

        Tick();

        Assert.Equal(1, Controller.ChannelCount);
    }

    // A fan count past the record's four slots is clamped rather than trusted.
    [Fact]
    public void AGroupReportingMoreThanFourFans_HasFourReadings() {
        FakeWirelessRecord record = Group(1, fans: 4);
        record.FanCountByte = 7;
        _rig.Records.Add(record);

        Assert.Equal(4, Controller.FanSpeedCount);
    }

    // A water block: its fans (GetAioTemp reads fans_type[3], so at most three), its pump (RPM in
    // slot 3, Speeds.LastOrDefault) and its coolant temperature.
    [Fact]
    public void AWaterBlock_GetsItsFansItsPumpAndItsCoolant() {
        FakeWirelessRecord block = Device(1, 10, fans: 4);
        block.FanTypes = B(41, 41, 41, 31);
        block.Rpm = new[] { 900, 910, 920, 2400 };
        _rig.Records.Add(block);

        Assert.Equal(2, Controller.ChannelCount);
        Assert.Equal("LianLi/wa00000000001/ctl", Controller.Describe(0).ControlId);
        Assert.Equal("Lian Li HydroShift II Wireless 000001", Controller.Describe(0).ControlName);
        Assert.Equal("LianLi/wa00000000001/pump/ctl", Controller.Describe(1).ControlId);
        Assert.Equal("Lian Li HydroShift II Wireless 000001 Pump", Controller.Describe(1).ControlName);
        Assert.Equal("LianLi/wa00000000001/pump/fan", Controller.Describe(1).RpmId);
        Assert.Equal("Lian Li HydroShift II Wireless 000001 Pump RPM", Controller.Describe(1).RpmName);
        Assert.Equal(
            new[] { "LianLi/wa00000000001/f0/fan", "LianLi/wa00000000001/f1/fan", "LianLi/wa00000000001/f2/fan", "LianLi/wa00000000001/pump/fan" },
            FanSpeedIds());
        Assert.Equal(2400f, Controller.GetRpm(1));
        Assert.Equal(2400f, Controller.GetFanSpeed(3));
        TemperatureDescriptor coolant = Controller.DescribeTemperature(0);
        Assert.Equal("LianLi/wa00000000001/coolant/temp", coolant.Id);
        Assert.Equal("Lian Li HydroShift II Wireless 000001 Coolant", coolant.Name);
        Assert.Equal(31f, Controller.GetTemperature(0));
    }

    [Fact]
    public void AWaterBlockWithoutFans_GetsOnlyItsPump() {
        _rig.Records.Add(Device(1, 11));

        Assert.Equal("LianLi/wa00000000001/pump/ctl", Assert.Single(Enumerable.Range(0, Controller.ChannelCount).Select(c => Controller.Describe(c).ControlId)));
        Assert.Equal(new[] { "LianLi/wa00000000001/pump/fan" }, FanSpeedIds());
    }

    // With only the second front fan fitted, the front control reads that fan: the reading it names
    // is one that is registered.
    [Fact]
    public void TheLancool217FrontControl_ReadsTheFirstFittedFrontFan() {
        FakeWirelessRecord lancool = Device(1, 65);
        lancool.FanTypes = B(0, 1, 0, 0);
        lancool.Rpm = new[] { 0, 720, 0, 0 };
        _rig.Records.Add(lancool);

        Assert.Equal("LianLi/wa00000000001/f1/fan", Controller.Describe(0).RpmId);
        Assert.Equal(new[] { "LianLi/wa00000000001/f1/fan" }, FanSpeedIds());
        Assert.Equal(720f, Controller.GetRpm(0));
    }

    // L-Connect offers the front curve only while a front fan is fitted (HasFrontFan: fans_type[0]
    // or [1] == 1); a case with only its rear fan fitted gets the rear control alone.
    [Theory]
    [InlineData(new byte[] { 0, 0, 1, 0 }, new[] { "LianLi/wa00000000001/rear/ctl" })]
    [InlineData(new byte[] { 0, 1, 1, 0 }, new[] { "LianLi/wa00000000001/front/ctl", "LianLi/wa00000000001/rear/ctl" })]
    [InlineData(new byte[] { 0, 0, 0, 0 }, new string[0])]
    public void TheLancool217_GetsAFrontControlOnlyWithAFrontFanFitted(byte[] fanTypes, string[] controls) {
        FakeWirelessRecord lancool = Device(1, 65);
        lancool.FanTypes = fanTypes;
        _rig.Records.Add(lancool);

        Assert.Equal(controls, Enumerable.Range(0, Controller.ChannelCount).Select(c => Controller.Describe(c).ControlId));
    }

    // L-Connect drives the Lancool 217 as two curves, front (slots 0-1) and rear (slot 2), and a
    // fitted fan reports type 1 in its slot. The rear curve only exists once a rear fan is fitted
    // (LWirelessController creates the rear sub-profile when fans_type[2] == 1).
    [Fact]
    public void TheLancool217_GetsAFrontControl_AndARearOneOnlyOnceARearFanIsFitted() {
        FakeWirelessRecord lancool = Device(1, 65);
        lancool.FanTypes = B(1, 1, 0, 0);
        lancool.Rpm = new[] { 700, 710, 0, 0 };
        _rig.Records.Add(lancool);

        Assert.Equal(1, Controller.ChannelCount);
        Assert.Equal("LianLi/wa00000000001/front/ctl", Controller.Describe(0).ControlId);
        Assert.Equal("Lian Li Lancool 217 Wireless 000001 Front Fans", Controller.Describe(0).ControlName);
        Assert.Equal(new[] { "LianLi/wa00000000001/f0/fan", "LianLi/wa00000000001/f1/fan" }, FanSpeedIds());
        Assert.Equal("Lian Li Lancool 217 Wireless 000001 Front Fan 2 RPM", Controller.DescribeFanSpeed(1).Name);
        Assert.Equal(700f, Controller.GetRpm(0));

        lancool.FanTypes = B(1, 1, 1, 0);
        lancool.Rpm = new[] { 700, 710, 1200, 0 };
        Tick();

        Assert.Equal(2, Controller.ChannelCount);
        Assert.Equal("LianLi/wa00000000001/rear/ctl", Controller.Describe(1).ControlId);
        Assert.Equal("Lian Li Lancool 217 Wireless 000001 Rear Fan", Controller.Describe(1).ControlName);
        Assert.Equal("LianLi/wa00000000001/f2/fan", Controller.Describe(1).RpmId);
        Assert.Equal("Lian Li Lancool 217 Wireless 000001 Rear Fan RPM", Controller.Describe(1).RpmName);
        Assert.Equal(3, Controller.FanSpeedCount);
        Assert.Equal(1200f, Controller.GetRpm(1));
    }

    // NeedSyncPwm writes a V150 whatever its fan count.
    [Fact]
    public void TheV150_GetsOneControlEvenWithoutFans() {
        _rig.Records.Add(Device(1, 66));

        Assert.Equal(1, Controller.ChannelCount);
        Assert.Equal("Lian Li V150 Wireless 000001", Controller.Describe(0).ControlName);
        Assert.Equal("LianLi/wa00000000001/f0/fan", Controller.Describe(0).RpmId);
        Assert.Equal(0, Controller.FanSpeedCount);
        Assert.Equal(0f, Controller.GetRpm(0));
    }

    // addSettingDevice's default branch gives a device type L-Connect has no name for a fan config.
    [Fact]
    public void AnUnrecognisedDevice_IsDrivenAsAFanDevice() {
        _rig.Records.Add(Device(1, 50, fans: 1));

        Assert.Equal("Lian Li Type 50 Wireless 000001", Controller.Describe(0).ControlName);
        Assert.Single(FanSpeedIds());
    }

    [Fact]
    public void AStrimer_IsCountedButHasNoSensors() {
        _rig.Records.Add(Device(1, 3));
        _configuration.Effects[MacText(1)] = new WirelessSavedEffect(B(1), B(9, 9, 9, 9), 1, 0, 1, 10, 0);

        Assert.Equal(1, Controller.DeviceCount);
        Assert.Equal(1, Controller.LitDeviceCount);
        Assert.Equal(0, Controller.ChannelCount);
        Assert.Equal(0, Controller.FanSpeedCount);
    }

    // Only a device whose record names this master is driven (BindStatus BindLink).
    [Fact]
    public void ADeviceOfAnotherMaster_GetsNothing() {
        _rig.Records.Add(Group(1, master: Other));
        _rig.Records.Add(Group(2, master: new byte[6]));
        _rig.Records.Add(new FakeWirelessRecord(Other, new byte[6]) { DeviceType = 0xFF });

        Assert.Equal(0, Controller.DeviceCount);
        Assert.Equal(0, Controller.ChannelCount);
    }

    [Fact]
    public void AnImplausibleReading_KeepsTheLastGoodValueAndIsLoggedOnce() {
        _rig.Records.Add(Group(1));
        Assert.Equal(1000f, Controller.GetFanSpeed(0));

        _rig.Records[0].Rpm = new[] { 50000, 1001, 0, 0 };
        Tick();
        Tick();
        Assert.Equal(1000f, Controller.GetFanSpeed(0));
        Assert.Single(_log.Messages, m => m == "W4:a00000000001 f0 implausible 50000 rpm ignored, keeping 1000");

        _rig.Records[0].Rpm = new[] { 1100, 1001, 0, 0 };
        Tick();
        Assert.Equal(1100f, Controller.GetFanSpeed(0));
        Assert.Contains("W4:a00000000001 f0 recovered (1100 rpm)", _log.Messages);
    }

    // ---------- the speed resend (MasterDevice.SyncPwm, NeedSyncPwm) ----------

    // SyncPwm: header on the device's channel and rx_type, payload [14] target_rx_type (the slot
    // it was bound under), [15] MasterChannel, [16] the bind index, [17-20] the four slots.
    [Fact]
    public void ADuty_IsSentAsTheBindAndSpeedCommandOnAllFourSlots() {
        _configuration.Channels["112233445566"] = 21;
        _rig.Records.Add(Group(1, receiver: 3));
        Controller.SetTarget(0, 50);
        ClearWrites();

        Tick();

        (byte[] header, byte[] payload) = Assert.Single(SpeedPayloads());
        Assert.Equal(B(0x10, 0, 8, 3), header);
        Assert.Equal(SpeedPayload(1, receiver: 3, channel: 21, bindIndex: 1, 127, 127, 127, 127), payload);
        Assert.Contains("Set W4:a00000000001 fans = 50%", _log.Messages);
    }

    // Four packets of 60 payload bytes each, chunk index in byte 1 (MasterDevice.SendRfData).
    [Fact]
    public void ASpeed_GoesOutAsFourTransmitPackets() {
        _rig.Records.Add(Group(1, receiver: 3));
        Controller.SetTarget(0, 50);
        ClearWrites();

        Tick();

        byte[] payload = SpeedPayload(1, 3, 8, 1, 127, 127, 127, 127);
        List<byte[]> packets = _rig.Transmitter.Writes.Take(4).ToList();
        for (int chunk = 0; chunk < 4; chunk++) {
            Assert.Equal(Expected(64, (0, B(0x10, chunk, 8, 3)), (4, payload[(chunk * 60)..((chunk + 1) * 60)])), packets[chunk]);
        }
    }

    // NeedSyncPwm: once the device reports within 5 of its target nothing more is sent; when it
    // falls more than 5 away the target is sent again every second.
    [Fact]
    public void TheSpeed_IsResentEverySecondUntilTheDeviceReportsIt() {
        _rig.Records.Add(Group(1));
        Controller.SetTarget(0, 50);
        Tick();
        ClearWrites();

        Tick();
        Assert.Single(SpeedPayloads()); // still reporting 100
        Assert.Single(_log.Messages, m => m == "W4:a00000000001 reports pwm [100,100,100,100] against [127,127,127,127]; resending each second until it matches");

        _rig.Records[0].Pwm = B(123, 127, 127, 132);
        Tick();
        ClearWrites();
        Tick();
        Assert.Empty(SpeedPayloads());

        // What a tick sends is decided on the list read at the end of the tick before.
        _rig.Records[0].Pwm = B(127, 127, 127, 120);
        Tick();
        Assert.Empty(SpeedPayloads());
        Tick();
        Assert.Single(SpeedPayloads());
        Assert.Equal(2, _log.Messages.Count(m => m.Contains("resending each second")));
    }

    // Nothing is sent to a group FanControl has not set, however far off it reports.
    [Fact]
    public void AGroupNobodyHasSet_IsSentNothing() {
        _rig.Records.Add(Group(1, pwm: 0));
        ClearWrites();

        Tick();
        Tick();

        Assert.Empty(SpeedPayloads());
    }

    // getTemperatureDuty: 0 -> 5% with no floor; otherwise the group's floor from convertFanType.
    [Theory]
    [InlineData(20, 0, 12)]
    [InlineData(20, 5, 35)]
    [InlineData(28, 5, 28)]
    [InlineData(27, 5, 25)]
    [InlineData(36, 100, 255)]
    public void TheDuty_FollowsTheServicesIdleAndFloorRules(int fanType, int duty, int pwm) {
        _rig.Records.Add(Group(1, fanType: fanType, pwm: 200));
        Controller.SetTarget(0, duty);

        Tick();

        Assert.Equal(B(pwm, pwm, pwm, pwm), SpeedPayloads().Last().Payload[17..21]);
    }

    // NeedSyncPwm steps a CL group's 153-154 down to 152 and 155 up to 156.
    [Theory]
    [InlineData(60, 152)]
    [InlineData(61, 156)]
    [InlineData(59, 150)]
    public void ACLGroup_IsSteppedOffItsReservedValues(int duty, int pwm) {
        _rig.Records.Add(Group(1, fanType: 41, pwm: 0));
        Controller.SetTarget(0, duty);

        Tick();

        Assert.Equal(B(pwm, pwm, pwm, pwm), SpeedPayloads().Last().Payload[17..21]);
    }

    // SyncPwm numbers the bound devices in rfList order, counting one on another master (bound in
    // L-Connect's sense) but skipping one that is unbound or changing effect.
    [Fact]
    public void TheBindIndex_IsThePositionAmongTheBoundDevicesInFirstHeardOrder() {
        _rig.Records.Add(Group(3, receiver: 3));
        _rig.Records.Add(Group(9, master: Other, receiver: 9));
        _rig.Records.Add(Group(1, receiver: 1));
        _rig.Records.Add(Group(2, receiver: 2));
        Build();
        _rig.Records[3].Master = Other; // group 2 moved away after being bound
        _rig.Records.Add(Group(4, receiver: 4));
        Tick();
        for (int channel = 0; channel < Controller.ChannelCount; channel++) {
            Controller.SetTarget(channel, 50);
        }

        Tick();

        List<byte[]> sent = SpeedPayloads().Select(p => p.Payload).ToList();
        Assert.Equal(3, sent.Count);
        Assert.Equal(SpeedPayload(3, 3, 8, 1, 127, 127, 127, 127), sent[0]);
        Assert.Equal(SpeedPayload(1, 1, 8, 2, 127, 127, 127, 127), sent[1]);
        Assert.Equal(SpeedPayload(4, 4, 8, 4, 127, 127, 127, 127), sent[2]); // group 2 still holds 3
    }

    // SyncPwm sleeps 5 ms after each device it numbers, sent or not.
    [Fact]
    public void TheResend_PausesFiveMillisecondsAfterEachBoundDevice() {
        _rig.Records.Add(Group(1, receiver: 1));
        _rig.Records.Add(Group(2, receiver: 2));
        _rig.Records.Add(Group(3, master: Other, receiver: 3));
        Build();
        _delay.Waits.Clear();

        Controller.ApplyPending();

        Assert.Equal(2, _delay.Waits.Count(w => w == TimeSpan.FromMilliseconds(5)));
    }

    // target_rx_type is what the device reported when it was taken as bound; the header carries
    // the rx_type it reports now.
    [Fact]
    public void TheReceiverSlot_InThePayloadIsTheOneTheDeviceWasBoundUnder() {
        _rig.Records.Add(Group(1, receiver: 3));
        Build();
        _rig.Records[0].Receiver = 5;
        _rig.Records[0].Channel = 9;
        Tick();
        Controller.SetTarget(0, 50);

        Tick();

        (byte[] header, byte[] payload) = SpeedPayloads().Last();
        Assert.Equal(B(0x10, 0, 9, 5), header);
        Assert.Equal(3, payload[14]);
        Assert.Equal(8, payload[15]);
    }

    // SyncControlInfo: a bound device heard off the master's channel is sent back to it every
    // cycle, even with no speed to change and no control set.
    [Fact]
    public void ADeviceHeardOnAnotherChannel_IsMovedBackWithoutASpeedChange() {
        FakeWirelessRecord group = Group(1, receiver: 3);
        group.Pwm = B(100, 100, 100, 100);
        _rig.Records.Add(group);
        Build();
        group.Channel = 9;
        Tick();
        ClearWrites();

        Tick();

        (byte[] header, byte[] payload) = SpeedPayloads().Single();
        Assert.Equal(B(0x10, 0, 9, 3), header);             // sent where it is now
        Assert.Equal(8, payload[15]);                          // told to come back to channel 8
        Assert.Equal(B(100, 100, 100, 100), payload[17..21]);  // at the speed it already runs

        group.Channel = 8;
        Tick();
        ClearWrites();
        Tick();
        Assert.Empty(SpeedPayloads()); // back where it belongs, and nothing to change
    }

    [Fact]
    public void ReleasingAControl_StopsTheResend() {
        _rig.Records.Add(Group(1));
        Controller.SetTarget(0, 50);
        Tick();

        Controller.ReleaseChannel(0);
        ClearWrites();
        Tick();

        Assert.Empty(SpeedPayloads());
        Assert.Contains("W4:a00000000001 fans released", _log.Messages);
    }

    // SetCaseSpeed: front on slots 0-1, rear on slot 2, slot 3 zero; startWriteCaseSpeed floors at 11,
    // calculateDuty sends 5 at zero.
    [Fact]
    public void TheLancool217_IsSentFrontAndRearWithSlotThreeZero() {
        FakeWirelessRecord lancool = Device(1, 65, receiver: 2);
        lancool.FanTypes = B(1, 1, 1, 0);
        lancool.Pwm = B(40, 40, 40, 40);
        _rig.Records.Add(lancool);
        Controller.SetTarget(ControlIndex("LianLi/wa00000000001/front/ctl"), 3);
        Tick();
        Assert.Equal(SpeedPayload(1, 2, 8, 1, 28, 28, 40, 0), SpeedPayloads().Last().Payload);

        Controller.SetTarget(ControlIndex("LianLi/wa00000000001/rear/ctl"), 0);
        Tick();
        Assert.Equal(SpeedPayload(1, 2, 8, 1, 28, 28, 12, 0), SpeedPayloads().Last().Payload);
    }

    // The RFList getter leaves out a bound Lancool 217 or V150 more than 2 s off the master's
    // clock, so its targets are not updated; SyncPwm keeps resending the old ones.
    [Fact]
    public void ALancool217WithADriftedClock_KeepsItsOldTarget() {
        FakeWirelessRecord lancool = Device(1, 65);
        lancool.FanTypes = B(1, 1, 0, 0);
        lancool.ClockTicks = 1600; // the master's clock
        _rig.Records.Add(lancool);
        int front = ControlIndex("LianLi/wa00000000001/front/ctl");
        Controller.SetTarget(front, 50);
        Tick();
        Assert.Equal(127, SpeedPayloads().Last().Payload[17]);

        lancool.ClockTicks = 1600 + 4000; // 2500 ms ahead
        Tick();
        Controller.SetTarget(front, 100);
        Tick();

        Assert.Equal(127, SpeedPayloads().Last().Payload[17]);
    }

    // The front pair and the rear fan share one packet, so a speed sent for the front must carry
    // the rear as the device reports it once FanControl has let the rear go - never its old target.
    [Fact]
    public void ChangingTheFront_DoesNotRestoreAReleasedRear() {
        FakeWirelessRecord lancool = Device(1, 65, receiver: 2);
        lancool.FanTypes = B(1, 1, 1, 0);
        _rig.Records.Add(lancool);
        int front = ControlIndex("LianLi/wa00000000001/front/ctl");
        int rear = ControlIndex("LianLi/wa00000000001/rear/ctl");
        Controller.SetTarget(front, 50);
        Controller.SetTarget(rear, 50);
        Tick();

        Controller.ReleaseChannel(rear);
        lancool.Pwm = B(127, 127, 200, 0); // something else now runs the rear at 200
        Tick();                             // and the receiver reports it
        Controller.SetTarget(front, 60);
        Tick();

        Assert.Equal(200, SpeedPayloads().Last().Payload[19]);
    }

    // A drifted clock freezes the targets, not FanControl's release of a control.
    [Fact]
    public void AReleasedControl_StopsBeingResent_EvenWhileTheClockIsDrifted() {
        FakeWirelessRecord lancool = Device(1, 65);
        lancool.FanTypes = B(1, 1, 0, 0);
        lancool.ClockTicks = 1600;
        _rig.Records.Add(lancool);
        int front = ControlIndex("LianLi/wa00000000001/front/ctl");
        Controller.SetTarget(front, 50);
        Tick();

        lancool.ClockTicks = 1600 + 4000;
        Tick();
        Controller.ReleaseChannel(front);
        ClearWrites();
        Tick();

        Assert.Empty(SpeedPayloads());
    }

    // addSettingDevice gives the V150 a fan config: SetFanSpeed repeats the duty, floor 10.
    [Fact]
    public void TheV150_IsSentOneDutyOnAllFourSlots() {
        _rig.Records.Add(Device(1, 66, receiver: 6));
        Controller.SetTarget(0, 3);

        Tick();

        Assert.Equal(SpeedPayload(1, 6, 8, 1, 25, 25, 25, 25), SpeedPayloads().Last().Payload);
    }

    // A clock set back does not stop the cycle until it catches up again.
    [Fact]
    public void TheCycle_KeepsRunning_WhenTheClockGoesBack() {
        _rig.Records.Add(Group(1));
        Controller.SetTarget(0, 50);
        Tick();

        _clock.Advance(TimeSpan.FromHours(-1));
        ClearWrites();
        Controller.ApplyPending();

        Assert.NotEmpty(Payloads(0x14)); // the clock broadcast went out: the cycle ran
    }

    // The one-second block runs once a second however often the worker calls.
    [Fact]
    public void TheCycle_RunsOnceASecond() {
        _rig.Records.Add(Group(1));
        Controller.SetTarget(0, 50);
        Controller.ApplyPending();
        ClearWrites();

        Controller.ApplyPending();
        _clock.Advance(TimeSpan.FromMilliseconds(900));
        Controller.ApplyPending();
        Assert.Empty(_rig.Transmitter.Writes);

        _clock.Advance(TimeSpan.FromMilliseconds(100));
        Controller.ApplyPending();
        Assert.NotEmpty(_rig.Transmitter.Writes);
    }

    // A device that moves to another master is never sent anything - its speed command would bind
    // it back - and stops counting as driven; its fans still read.
    [Fact]
    public void ADeviceThatMovesToAnotherMaster_IsNoLongerDriven() {
        _rig.Records.Add(Group(1));
        Controller.SetTarget(0, 50);
        Tick();
        _rig.Records[0].Master = Other;
        _rig.Records[0].Rpm = new[] { 1500, 1500, 0, 0 };
        Tick();
        ClearWrites();

        Tick();

        Assert.Empty(SpeedPayloads());
        Assert.Equal(0, Controller.DeviceCount);
        Assert.Equal(1500f, Controller.GetFanSpeed(0));
    }

    // RfDevice.max_live_time: thirty list reads unheard. The plugin reads its fans as 0 and its
    // coolant as nothing until it is heard, and still drives it, as L-Connect does: a receiver that
    // stops answering says nothing about whether the device hears the transmitter.
    [Fact]
    public void ADeviceUnheardForThirtyReads_ReadsZero_ButIsStillDriven() {
        FakeWirelessRecord block = Device(1, 10, fans: 1);
        block.FanTypes = B(41, 0, 0, 30);
        block.Rpm = new[] { 900, 0, 0, 2400 };
        _rig.Records.Add(block);
        Controller.SetTarget(0, 50);
        Controller.SetTarget(1, 50);
        Tick();
        _rig.Records.Clear();

        for (int read = 0; read < 29; read++) {
            Tick();
        }

        Assert.Equal(900f, Controller.GetFanSpeed(0));
        Tick();
        Assert.Equal(0f, Controller.GetFanSpeed(0));
        Assert.Equal(0f, Controller.GetRpm(1));
        Assert.Null(Controller.GetTemperature(0));
        Assert.Equal(1, Controller.DeviceCount);
        ClearWrites();
        Controller.SetTarget(0, 100);
        Controller.SetTarget(1, 100);
        Tick();
        Assert.NotEmpty(SpeedPayloads());
        Assert.NotEmpty(Payloads(0x21));

        _rig.Records.Add(block);
        Tick();
        Assert.Equal(900f, Controller.GetFanSpeed(0));
        Assert.Equal(30f, Controller.GetTemperature(0));
    }

    // A V150 is dropped from L-Connect's list after thirty unheard reads and added again as new.
    [Fact]
    public void AV150DroppedFromTheList_ReadsZeroAndComesBackWhenHeard() {
        FakeWirelessRecord v150 = Device(1, 66, fans: 1);
        v150.Rpm = new[] { 800, 0, 0, 0 };
        _rig.Records.Add(v150);
        _rig.Records.Add(Group(2, receiver: 2));
        Build();
        _rig.Records.RemoveAt(0);
        for (int read = 0; read < 30; read++) {
            Tick();
        }

        Assert.Equal(0f, Controller.GetFanSpeed(0));

        _rig.Records.Add(v150);
        Tick();
        Assert.Equal(800f, Controller.GetFanSpeed(0));
    }

    // One device's failure costs only that device that second (the worker's per-controller catch,
    // applied per RF device), logged when it starts and when it ends.
    [Fact]
    public void AWriteFailureMidTick_CostsOnlyThatDevice() {
        _rig.Records.Add(Group(1, receiver: 1));
        _rig.Records.Add(Group(2, receiver: 2));
        Controller.SetTarget(0, 50);
        Controller.SetTarget(1, 50);
        bool fail = true;
        _rig.Transmitter.FailWrite = packet => fail && packet[0] == 0x10 && packet[1] == 0 && packet[4 + 2] == 0xA0 && packet[4 + 7] == 1;

        Tick();
        Tick();

        Assert.All(SpeedPayloads(), p => Assert.Equal(Mac(2), p.Payload[2..8]));
        Assert.Single(_log.Messages, m => m == "W4 a00000000001/speed failed: simulated write failure");
        Assert.Contains(Payloads(0x14), p => true); // the clock still went out

        fail = false;
        Tick();
        Assert.Contains("W4 a00000000001/speed recovered", _log.Messages);
    }

    [Fact]
    public void AFailedListRead_IsLoggedOnceAndCountsAsAMiss() {
        _rig.Records.Add(Group(1));
        Build();
        _rig.Receiver.ReadFailure = new IOException("receiver gone");

        Tick();
        Tick();
        Assert.Single(_log.Messages, m => m == "W4 list failed: receiver gone");

        _rig.Receiver.ReadFailure = null;
        _rig.ReceiverAnswers = false;
        Tick();
        Assert.Contains("W4 list failed: the receiver did not answer the list request", _log.Messages);

        _rig.ReceiverAnswers = true;
        Tick();
        Assert.Contains("W4 list recovered", _log.Messages);
    }

    // RefreshList: get_page_cnt follows the receiver's count, growing and shrinking.
    [Fact]
    public void ThePageCount_FollowsTheReceiversCount() {
        for (int i = 1; i <= 11; i++) {
            _rig.Records.Add(Group(i, master: Other, receiver: i));
        }

        Build();
        Assert.Equal(2, _rig.Receiver.Writes.Last()[1]);

        _rig.Records.RemoveRange(5, 6);
        Tick();
        Tick();
        Assert.Equal(1, _rig.Receiver.Writes.Last()[1]);

        _rig.Records.Clear();
        _rig.Total = 0;
        Tick();
        Assert.Equal(1, _rig.Receiver.Writes.Last()[1]);
    }

    // Every control change wakes the worker; the list is still read once a second, so a device is
    // not counted lost by FanControl's pace.
    [Fact]
    public void WakesWithinASecond_ReadTheListOnce_AndCountNobodyLost() {
        _rig.Records.Add(Group(1));
        Tick();
        _rig.Records.Clear();
        int reads = _rig.Receiver.Writes.Count;

        for (int wake = 0; wake < WirelessDevice.MaximumMissedReads + 10; wake++) {
            Controller.ApplyPending();
            Controller.PollRpm();
        }

        Assert.Equal(reads, _rig.Receiver.Writes.Count); // this second's read was the tick's
        Assert.DoesNotContain(_log.Messages, m => m.Contains("reading 0 rpm until it is"));
    }

    // ---------- L-Connect's locked device list (RFController.CheckLockAndInitData) ----------

    private static WirelessLockedDevice Locked(FakeWirelessRecord record)
        => new WirelessLockedDevice(FakeWirelessRecords.Decode(record), record.Receiver, record.Pwm);

    [Fact]
    public void ALockedList_IsDrivenInItsOrder_AndADevicePairedSinceIsLeftAlone() {
        _configuration.LockedDevices["112233445566"] = new[] { Locked(Group(2, receiver: 2)), Locked(Group(1)) };
        _rig.Records.Add(Group(1));
        _rig.Records.Add(Group(2, receiver: 2));
        _rig.Records.Add(Group(3, receiver: 3));
        Controller.SetTarget(ControlIndex("LianLi/w" + MacText(1) + "/ctl"), 60);
        Controller.SetTarget(ControlIndex("LianLi/w" + MacText(2) + "/ctl"), 60);

        Tick();

        Assert.DoesNotContain(Enumerable.Range(0, Controller.ChannelCount), c => Controller.Describe(c).ControlId.Contains(MacText(3)));
        List<(byte[] Header, byte[] Payload)> speeds = Payloads(0x10);
        Assert.Equal(1, speeds.Single(p => p.Payload[7] == 2).Payload[16]); // bind index follows the locked order
        Assert.Equal(2, speeds.Single(p => p.Payload[7] == 1).Payload[16]);
        Assert.Contains(_log.Messages, m => m.Contains("L-Connect's device list is locked; its 2 device(s) are used in its order"));
    }

    [Fact]
    public void ALockedListWhoseDevicesAllNameAnotherMaster_IsLetGo() {
        _configuration.LockedDevices["112233445566"] = new[] { Locked(Group(1)) };
        _rig.Records.Add(Group(1, master: Other));
        Build();

        Tick();
        _rig.Records.Add(Group(3, receiver: 3));
        Tick();

        Assert.Single(_log.Messages, m => m == "W4: no device on L-Connect's locked list names this master any more; the list is no longer kept");
        Assert.Contains(Enumerable.Range(0, Controller.ChannelCount), c => Controller.Describe(c).ControlId.Contains(MacText(3)));
    }

    // ---------- the channel across FanControl's refreshes ----------

    [Fact]
    public void ANewControllerAfterARefresh_QueriesOnTheChannelTheLastOneDroveTheMasterOn() {
        _processState.Remember("112233445566", 12);

        Build();

        Assert.Equal(12, _rig.Transmitter.Writes.First(w => w[0] == 0x11)[1]);
        Assert.Equal(12, Controller.Channel);
        Assert.Contains(_log.Messages, m => m.Contains("RF channel 12 (kept from before FanControl's refresh)"));
    }

    [Fact]
    public void LConnectsSavedChannel_WinsOverTheRememberedOne_AndIsRemembered() {
        _processState.Remember("112233445566", 12);
        _configuration.Channels["112233445566"] = 21;

        Build();

        Assert.Equal(21, Controller.Channel);
        Assert.Equal(21, _processState.RecallFor("112233445566"));
    }

    [Fact]
    public void AChannelRememberedForAnotherMaster_IsNotUsedOnceThisMasterIsKnown() {
        _processState.Remember("aabbccddeeff", 16);

        Build();

        Assert.Equal(16, _rig.Transmitter.Writes.First(w => w[0] == 0x11)[1]); // before the master answers
        Assert.Equal(8, Controller.Channel);
    }

    // A device known only from L-Connect's saved locked list is sent nothing - no speed, which
    // restates a binding - until the receiver reports it.
    [Fact]
    public void ALockedDeviceNotHeardYet_IsSentNothing_UntilItIs() {
        // Saved with targets that differ from its speeds, so a driven device would be sent them.
        _configuration.LockedDevices["112233445566"] = new[] {
            new WirelessLockedDevice(FakeWirelessRecords.Decode(Group(1, receiver: 2)), 2, B(200, 200, 200, 200)),
        };
        _rig.Records.Add(Group(2, receiver: 3)); // the receiver hears something, but not device 1
        for (int second = 0; second < 5; second++) {
            Tick();
        }

        Assert.DoesNotContain(Payloads(0x10), p => p.Payload[7] == 1);

        _rig.Records.Add(Group(1, receiver: 2));
        Tick();
        Controller.SetTarget(ControlIndex("LianLi/w" + MacText(1) + "/ctl"), 80);
        Tick();
        Assert.Contains(Payloads(0x10), p => p.Payload[7] == 1);
    }

    // Only a device this pair drives, heard on the air, ends the wait for devices to check in: an
    // entry from L-Connect's locked list is not heard yet, and another master's group never registers.
    [Fact]
    public void HasHeardDevices_OnlyOnceADeviceThisPairDrivesIsHeard() {
        _configuration.LockedDevices["112233445566"] = new[] { Locked(Group(1)) };
        _rig.Records.Add(Group(2, master: new byte[] { 0x99, 0x99, 0x99, 0x99, 0x99, 0x99 }));
        Tick();
        Tick();

        Assert.Equal(0, Controller.ChannelCount);
        Assert.False(Controller.HasHeardDevices);

        _rig.Records.Add(Group(1));
        Tick();

        Assert.True(Controller.HasHeardDevices);
    }

    // ---------- other masters in range (MasterDevice.CheckChannelConflict) ----------

    // A master record: type 255, its own address as the device address.
    private static FakeWirelessRecord MasterRecord(byte[] mac, int channel)
        => new FakeWirelessRecord(mac, new byte[6]) { DeviceType = 0xFF, Channel = (byte)channel };

    [Fact]
    public void AMasterSortingSecond_MovesToChannel12_AndItsDevicesFollow() {
        byte[] lower = { 0x00, 0x00, 0x00, 0x00, 0x00, 0x01 };
        _rig.Records.Add(MasterRecord(lower, 8));
        _rig.Records.Add(MasterRecord(Master, 8));
        _rig.Records.Add(Group(1));
        Tick();
        _rig.Transmitter.Writes.Clear();
        ClearWrites();

        Tick();

        Assert.Equal(12, Controller.Channel);
        Assert.Contains(_rig.Transmitter.Writes, w => w[0] == 0x11 && w[1] == 12);
        Assert.Contains(Payloads(0x10), p => p.Payload[15] == 12);
        Assert.Single(_log.Messages, m => m == "W4: 2 master(s) in range; this one is moved from channel 8 to 12 for this run, as L-Connect moves it");
        Assert.Equal(12, _processState.RecallFor("112233445566"));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, 8)] // sorts after it: 8 is its own place
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x01 }, 21)] // a channel the user chose: odd, never moved
    public void AMasterAlreadyOnItsPlace_OrOnAChosenChannel_IsNotMoved(byte[] otherMaster, int reported) {
        _configuration.Channels["112233445566"] = reported;
        _rig.Records.Add(MasterRecord(otherMaster, 8));
        _rig.Records.Add(MasterRecord(Master, reported));
        Tick();
        Tick();

        Assert.Equal(reported, Controller.Channel);
        Assert.DoesNotContain(_log.Messages, m => m.Contains("moved from channel"));
    }

    [Fact]
    public void AMove_IsMadeOnce_EvenWhileTheMastersRecordStillShowsTheOldChannel() {
        _rig.Records.Add(MasterRecord(new byte[] { 0, 0, 0, 0, 0, 1 }, 8));
        _rig.Records.Add(MasterRecord(Master, 8));
        for (int second = 0; second < 5; second++) {
            Tick();
        }

        Assert.Single(_log.Messages, m => m.Contains("moved from channel 8 to 12"));
    }

    // ---------- the water block (MasterDevice.SendAioInfo) ----------

    // SendAioInfo sends the parameter block every second; the plugin sends it to a block whose pump
    // FanControl drives, with the saved screen, and logs only a change.
    [Fact]
    public void ThePump_IsSentItsParameterBlockEverySecond() {
        _rig.Records.Add(Device(1, 11, receiver: 2));
        var presentation = new WirelessAioPresentation(
            3, false, 1800, true, 60, 4, 1,
            new WirelessAioPresentation.Argb(1, 2, 3, 4), new WirelessAioPresentation.Argb(5, 6, 7, 8), new WirelessAioPresentation.Argb(9, 10, 11, 12), false);
        _configuration.Presentations[MacText(1)] = presentation;
        Controller.SetTarget(0, 25); // 2000 rpm on the second generation: timer 1200 = 0x04B0

        Tick();
        Tick();

        List<(byte[] Header, byte[] Payload)> sent = Payloads(0x21);
        Assert.Equal(2, sent.Count);
        Assert.Equal(B(0x10, 0, 8, 2), sent[0].Header);
        byte[] parameters = B(0, 0, 0, 0, 0x07, 0x08, 3, 0, 0, 0, 0, 0, 1, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 60, 1, 4, 0x04, 0xB0, 1, 0);
        Assert.Equal(Expected(240, (0, B(0x12, 0x21)), (2, Mac(1)), (8, Master), (14, B(2, 8)), (18, parameters)), sent[0].Payload);
        Assert.Single(_log.Messages, m => m == "Set W4:a00000000001 pump = 25%");
    }

    [Fact]
    public void ThePump_UsesLConnectsDefaultScreenWithoutSavedSettings() {
        _rig.Records.Add(Device(1, 10));
        Controller.SetTarget(0, 0); // 1600 rpm on the first generation: timer 1500 = 0x05DC

        Tick();

        byte[] parameters = B(0, 0, 0, 0, 0x07, 0xD0, 2, 1, 0, 0, 0, 0, 0,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 100, 1, 0, 0x05, 0xDC, 0, 0);
        Assert.Equal(parameters, Payloads(0x21).Single().Payload[18..50]);
    }

    // LWirelessController.applyWirelessMode: a screen saved out of advance mode is switched to its
    // wireless theme ahead of its parameters, under a command sequence the device has not acknowledged.
    [Fact]
    public void AScreenOutOfAdvanceMode_IsSwitchedToItsTheme_BeforeItsParameters() {
        _rig.Records.Add(Device(1, 10, receiver: 2));
        _rig.Records[0].Sequence = 7;
        Controller.SetTarget(0, 0);

        Tick();

        List<(byte[] Header, byte[] Payload)> sent = _rig.Payloads().Where(p => p.Payload[1] == 0x19 || p.Payload[1] == 0x21).ToList();
        Assert.Equal(new[] { 0x19, 0x21 }, sent.Select(p => (int)p.Payload[1]));
        Assert.Equal(Expected(240, (0, B(0x12, 0x19)), (2, Mac(1)), (8, Master), (14, B(2, 8, 0, 8))), sent[0].Payload);
    }

    // SyncControlInfo's b: the bound devices before it that the pass counted.
    [Fact]
    public void TheScreenSwitch_CarriesHowManyBoundDevicesCameBeforeIt() {
        _rig.Records.Add(Group(1));
        _rig.Records.Add(Device(2, 5, receiver: 2)); // a Strimer, bound
        _rig.Records.Add(Group(3, master: Other, receiver: 3)); // not ours: not counted
        _rig.Records.Add(Device(4, 10, receiver: 4));
        Controller.SetTarget(ControlIndex("LianLi/w" + MacText(4) + "/pump/ctl"), 0);

        Tick();

        Assert.Equal(2, Payloads(0x19).Single().Payload[16]);
    }

    // A device mid-effect or mid-switch of its own is not counted, as SyncControlInfo counts neither.
    [Fact]
    public void TheScreenSwitch_DoesNotCountADeviceChangingEffect_OrOneSwitchingItself() {
        _configuration.Effects[MacText(1)] = Effect(B(1, 2, 3, 4));
        _rig.Records.Add(Group(1));
        _rig.Records.Add(Device(2, 10, receiver: 2));
        _rig.Records.Add(Device(3, 10, receiver: 3));
        Controller.SetTarget(ControlIndex("LianLi/w" + MacText(2) + "/pump/ctl"), 0);
        Controller.SetTarget(ControlIndex("LianLi/w" + MacText(3) + "/pump/ctl"), 0);
        Tick(); // the group is now streamed its effect, and both blocks are switching
        ClearWrites();

        Tick();

        Assert.Equal(0, Payloads(0x19).Single(p => p.Payload[7] == 3).Payload[16]);
    }

    // The same for a fan group: its targets are left as the list saved them until it has controls.
    [Fact]
    public void AGroupFromALockedListLearnedLate_IsNotAFailedSpeedSync() {
        Func<byte[], byte[]?>? answers = _rig.Transmitter.Responder;
        _rig.Transmitter.Responder = packet => null;
        _rig.Records.Add(Group(1));
        Build();
        _configuration.LockedDevices["112233445566"] = new[] { Locked(Group(1)) };
        _rig.Transmitter.Responder = answers;

        // Two one-second cycles with no list read between them: the second syncs speeds for a device
        // the lock put on the table and no read has given controls yet.
        _clock.Advance(Second);
        Controller.ApplyPending();
        _clock.Advance(Second);
        Controller.ApplyPending();

        Assert.DoesNotContain(_log.Messages, m => m.Contains("/speed failed"));
    }

    // A locked list learned after construction puts a water block on the table before any list
    // read has given it sensors; it is sent nothing that second rather than failing.
    [Fact]
    public void AWaterBlockFromALockedListLearnedLate_IsSentNothingUntilItHasSensors() {
        Func<byte[], byte[]?>? answers = _rig.Transmitter.Responder;
        _rig.Transmitter.Responder = packet => null; // the master does not answer during construction
        _rig.Records.Add(Device(1, 10));
        Build();
        _configuration.LockedDevices["112233445566"] = new[] { Locked(Device(1, 10)) };
        _rig.Transmitter.Responder = answers;

        Tick();

        Assert.DoesNotContain(_log.Messages, m => m.Contains("/pump failed"));
    }

    [Fact]
    public void TheScreenSwitch_StopsOnceTheDeviceReportsItsSequence() {
        _rig.Records.Add(Device(1, 10));
        Controller.SetTarget(0, 0);
        Tick();
        _rig.Records[0].Sequence = 1;
        Tick(); // the list read at the end of this tick hears the acknowledgement
        ClearWrites();

        Tick();
        Tick();

        Assert.Empty(Payloads(0x19));
        Assert.NotEmpty(Payloads(0x21));
        Assert.Single(_log.Messages, m => m == "W4:a00000000001 switched its screen to its wireless theme");
    }

    [Fact]
    public void TheScreenSwitch_GoesOutTenTimesAtMost_WhenNeverAcknowledged() {
        _rig.Records.Add(Device(1, 10));
        Controller.SetTarget(0, 0);

        for (int second = 0; second < 15; second++) {
            Tick();
        }

        Assert.Equal(10, Payloads(0x19).Count);
        Assert.All(Payloads(0x19), p => Assert.Equal(1, p.Payload[17]));
        Assert.Single(_log.Messages, m => m == "W4:a00000000001 did not acknowledge the switch to its wireless theme in 10 sends");
    }

    // L-Connect switches a screen when its service starts, not on every refresh: a controller built
    // after a refresh does not switch it again, unless the block came back since.
    [Fact]
    public void AScreenSwitchedBeforeARefresh_IsNotSwitchedAgain_ByTheNextController() {
        _rig.Records.Add(Device(1, 10));
        Controller.SetTarget(0, 0);
        Tick();
        _rig.Records[0].Sequence = 1;
        Tick();
        Tick();
        Controller.Dispose();
        ClearWrites();

        _controller = null;
        Controller.SetTarget(0, 0);
        Tick();
        Tick();

        Assert.Empty(Payloads(0x19));
        Assert.NotEmpty(Payloads(0x21));
    }

    [Fact]
    public void AScreenInAdvanceMode_IsNotSwitched() {
        _rig.Records.Add(Device(1, 10));
        _configuration.Presentations[MacText(1)] = new WirelessAioPresentation(
            2, true, 2000, false, 100, 3, 0,
            WirelessAioPresentation.Argb.White, WirelessAioPresentation.Argb.White, WirelessAioPresentation.Argb.White, true);
        Controller.SetTarget(0, 0);

        Tick();

        Assert.Empty(Payloads(0x19));
        Assert.NotEmpty(Payloads(0x21));
    }

    [Fact]
    public void AWaterBlockHeardAgain_IsSwitchedAgain() {
        _rig.Records.Add(Device(1, 10));
        Controller.SetTarget(0, 0);
        Tick();
        _rig.Records[0].Sequence = 1;
        Tick();
        FakeWirelessRecord block = _rig.Records[0];
        _rig.Records.Clear();
        for (int second = 0; second <= WirelessDevice.MaximumMissedReads; second++) {
            Tick();
        }

        _rig.Records.Add(block);
        ClearWrites();
        Tick();
        Tick();

        Assert.Equal(2, Payloads(0x19).Single().Payload[17]);
    }

    [Fact]
    public void APumpNobodyHasSet_IsSentNothing() {
        _rig.Records.Add(Device(1, 10));

        Tick();
        Controller.SetTarget(0, 50);
        Controller.ReleaseChannel(0);
        Tick();

        Assert.Empty(Payloads(0x21));
    }

    // ---------- broadcasts ----------

    // SyncMasterClock: every second, on the master channel to every receiver slot.
    [Fact]
    public void TheClock_IsBroadcastEverySecond() {
        _configuration.Channels["112233445566"] = 21;
        Build();
        ClearWrites();

        Tick();
        Tick();

        List<(byte[] Header, byte[] Payload)> clocks = Payloads(0x14);
        Assert.Equal(2, clocks.Count);
        Assert.Equal(B(0x10, 0, 21, 0xFF), clocks[0].Header);
        Assert.Equal(Expected(240, (0, B(0x12, 0x14)), (8, Master)), clocks[0].Payload);
    }

    // CheckSaveConfig: an hour after start, one save (SaveConfig(1): one send, then 200 ms).
    [Fact]
    public void TheConfiguration_IsSavedAnHourAfterStart() {
        Build();
        _clock.Advance(TimeSpan.FromMinutes(60));
        Tick();

        (byte[] header, byte[] payload) = Assert.Single(Payloads(0x15));
        Assert.Equal(B(0x10, 0, 8, 0xFF), header);
        Assert.Equal(Expected(240, (0, B(0x12, 0x15, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF)), (8, Master), (14, B(0xFF))), payload);
        Assert.Contains(TimeSpan.FromMilliseconds(200), _delay.Waits);
        Assert.Contains("W4: asked every device to save its configuration", _log.Messages);

        Tick();
        Assert.Single(Payloads(0x15));
    }

    [Fact]
    public void AFailedBroadcast_IsIsolatedAndLogged() {
        Build();
        _rig.Transmitter.FailWrite = packet => packet[0] == 0x10 && packet[3] == 0xFF;

        Tick();

        Assert.Contains("W4 clock failed: simulated write failure", _log.Messages);
        _clock.Advance(TimeSpan.FromHours(1));
        Tick();
        Assert.Contains("W4 save failed: simulated write failure", _log.Messages);
    }

    // ---------- lighting (MasterDevice.SyncRgbData) ----------

    private static WirelessSavedEffect Effect(byte[] identity, int bytes = 300, int frames = 10, double interval = 30)
        => new WirelessSavedEffect(Enumerable.Range(1, bytes).Select(i => (byte)i).ToArray(), identity, frames, 0, 24, interval, 0);

    // SyncRgbData: the descriptor, three more times 20 ms apart, the data, then 10 ms and a
    // debounced save; the device is changing effect - left out of the speed resend and its
    // numbering - until it reports the effect.
    [Fact]
    public void ASavedEffect_IsStreamedUntilTheDeviceRunsIt() {
        byte[] identity = B(1, 2, 3, 4);
        _configuration.Effects[MacText(1)] = Effect(identity);
        _rig.Records.Add(Group(1, receiver: 1));
        _rig.Records.Add(Group(2, receiver: 2));
        Controller.SetTarget(0, 50);
        Controller.SetTarget(1, 50);
        Assert.Equal(1, Controller.LitDeviceCount);
        _delay.Waits.Clear();

        Tick();

        List<(byte[] Header, byte[] Payload)> effect = Payloads(0x20);
        Assert.Equal(6, effect.Count); // the descriptor four times, then two data chunks
        Assert.All(effect.Take(4), p => Assert.Equal(0, p.Payload[18]));
        Assert.Equal(1, effect[4].Payload[18]);
        Assert.Equal(2, effect[5].Payload[18]);
        Assert.Equal(B(0x10, 0, 8, 1), effect[0].Header);
        Assert.Equal(
            new[] { TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(10) },
            _delay.Waits.Where(w => w != TimeSpan.FromMilliseconds(5)).ToArray());
        Assert.Contains("W4:a00000000001 streaming its saved lighting effect 01020304 (2 chunks) until it reports running it", _log.Messages);

        // Group 1 is changing effect: group 2 is numbered 1 and group 1 is sent nothing.
        ClearWrites();
        Tick();
        Assert.Equal(SpeedPayload(2, 2, 8, 1, 127, 127, 127, 127), Assert.Single(SpeedPayloads()).Payload);
        Assert.Equal(6, Payloads(0x20).Count); // streamed again: it still does not report it
        Assert.Single(_log.Messages, m => m.Contains("streaming its saved lighting effect"));

        _rig.Records[0].EffectIndex = identity;
        Tick();
        Tick();
        Assert.Contains("W4:a00000000001 took its saved lighting effect", _log.Messages);
        ClearWrites();
        Tick();
        Assert.Empty(Payloads(0x20));
        Assert.Equal(2, SpeedPayloads().Count);
    }

    // The plugin's one departure from SyncRgbData: a device that keeps refusing its saved effect is
    // not kept from its fan speed for ever. After ten streams in a row its lighting is given up and
    // it is driven like any other device.
    [Fact]
    public void ADeviceThatNeverTakesItsEffect_IsDrivenAfterTenStreams() {
        _configuration.Effects[MacText(1)] = Effect(B(1, 2, 3, 4));
        _rig.Records.Add(Group(1, receiver: 1));
        Controller.SetTarget(0, 50);

        for (int stream = 0; stream < 10; stream++) {
            Tick();
        }

        // The eleventh call gives the lighting up; the next cycle drives the group.
        Tick();
        ClearWrites();
        Tick();

        Assert.Empty(Payloads(0x20));
        Assert.Single(SpeedPayloads());
        Assert.Contains(
            "W4:a00000000001 did not take its saved lighting effect in 10 stream(s) tried over 10 s; lighting replay stopped for it, its fans are driven as usual",
            _log.Messages);

        ClearWrites();
        Tick();
        Assert.Empty(Payloads(0x20)); // and it stays given up
    }

    // RFController.SaveConfig: ten seconds after the streams go quiet.
    [Fact]
    public void AStream_IsFollowedByOneDebouncedSave() {
        byte[] identity = B(1, 2, 3, 4);
        _configuration.Effects[MacText(1)] = Effect(identity);
        _rig.Records.Add(Group(1));
        Build();
        Tick();
        _rig.Records[0].EffectIndex = identity;

        for (int second = 0; second <= WirelessDevice.MaximumMissedReads; second++) {
            Tick();
        }

        Assert.Single(Payloads(0x15));
    }

    // SyncStrimmer_22: a type 2 or 4 Strimer's interval becomes the first type 1 or 3 Strimer's
    // interval times its frame count over its own, and stays when that Strimer goes.
    [Fact]
    public void AType2Strimer_IsRetimedToTheFirstType1Strimer() {
        _configuration.Effects[MacText(1)] = Effect(B(1, 1, 1, 1), frames: 40, interval: 25);
        _configuration.Effects[MacText(2)] = Effect(B(2, 2, 2, 2), frames: 100, interval: 11);
        _configuration.Effects[MacText(3)] = Effect(B(3, 3, 3, 3), frames: 100, interval: 11);
        _rig.Records.Add(Device(1, 1, receiver: 1));
        _rig.Records.Add(Device(2, 2, receiver: 2));
        _rig.Records.Add(Device(3, 5, receiver: 3));

        Tick();

        List<byte[]> descriptors = Payloads(0x20).Where(p => p.Payload[18] == 0).Select(p => p.Payload).ToList();
        byte[] type2 = descriptors.First(p => p[7] == 2);
        byte[] type5 = descriptors.First(p => p[7] == 3);
        Assert.Equal(B(0, 10, 0), type2[32..35]); // 25 * 40 / 100 = 10 ms
        Assert.Equal(B(0, 11, 0), type5[32..35]);

        // The leading Strimer goes; the type 2 one plays its effect while the leader ages out of the
        // table, then loses it, and is streamed it again at the interval it was re-timed to.
        _rig.Records.RemoveAt(0);
        _configuration.Effects.Remove(MacText(1));
        _rig.Records[0].EffectIndex = B(2, 2, 2, 2);
        for (int read = 0; read < 31; read++) {
            Tick();
        }

        _rig.Records[0].EffectIndex = B(9, 9, 9, 9);
        Tick();
        ClearWrites();
        Tick();
        Assert.Equal(B(0, 10, 0), Payloads(0x20).First(p => p.Payload[18] == 0 && p.Payload[7] == 2).Payload[32..35]);
    }

    // SyncStrimmer_22 takes the first type 1 or 3 device on the table, whatever comes before it.
    [Fact]
    public void AType4Strimer_FollowsTheFirstType3StrimerPastOtherDevices() {
        _configuration.Effects[MacText(2)] = Effect(B(2, 2, 2, 2), frames: 20, interval: 12);
        _configuration.Effects[MacText(3)] = Effect(B(3, 3, 3, 3), frames: 100, interval: 11);
        _rig.Records.Add(Group(1, receiver: 1));
        _rig.Records.Add(Device(2, 3, receiver: 2));
        _rig.Records.Add(Device(3, 4, receiver: 3));

        Tick();

        Assert.Equal(B(0, 2, 40), Payloads(0x20).First(p => p.Payload[18] == 0 && p.Payload[7] == 3).Payload[32..35]); // 12 * 20 / 100
    }

    [Fact]
    public void AType2StrimerWithNoType1Or3OnTheTable_KeepsItsOwnInterval() {
        _configuration.Effects[MacText(2)] = Effect(B(2, 2, 2, 2), frames: 100, interval: 11);
        _rig.Records.Add(Device(2, 2, receiver: 2));

        Tick();

        Assert.Equal(B(0, 11, 0), Payloads(0x20).First().Payload[32..35]);
    }

    [Fact]
    public void AType4StrimerWithoutALeadingEffect_KeepsItsOwnInterval() {
        _configuration.Effects[MacText(2)] = Effect(B(2, 2, 2, 2), frames: 100, interval: 11);
        _rig.Records.Add(Device(1, 3, receiver: 1));
        _rig.Records.Add(Device(2, 4, receiver: 2));

        Tick();

        Assert.Equal(B(0, 11, 0), Payloads(0x20).First().Payload[32..35]);
    }

    [Fact]
    public void AFailedStream_CostsOnlyTheLook() {
        _configuration.Effects[MacText(1)] = Effect(B(1, 2, 3, 4));
        _rig.Records.Add(Group(1));
        Build();
        _rig.Transmitter.FailWrite = packet => packet[0] == 0x10 && packet[1] == 0 && packet[5] == 0x20;

        Tick();

        Assert.Contains("W4 a00000000001/lighting failed: simulated write failure", _log.Messages);
        Assert.NotEmpty(Payloads(0x14));
    }

    [Fact]
    public void AStreamThatKeepsFailing_NeverStopsTheFansBeingDriven() {
        _configuration.Effects[MacText(1)] = Effect(B(1, 2, 3, 4));
        _rig.Records.Add(Group(1));
        Build();
        _rig.Transmitter.FailWrite = packet => packet[0] == 0x10 && packet[1] == 0 && packet[5] == 0x20;
        Tick();
        ClearWrites();

        Tick();

        Assert.NotEmpty(Payloads(0x14));
    }

    // Streams brought forward by control changes do not make the plugin give up any sooner.
    [Fact]
    public void ManyStreamsInAMoment_DoNotGiveTheLightingUp() {
        _configuration.Effects[MacText(1)] = Effect(B(1, 2, 3, 4));
        _rig.Records.Add(Group(1));
        Build();
        for (int wake = 0; wake < 30; wake++) {
            Controller.ApplyPending(); // the clock does not move
        }

        ClearWrites();
        Controller.ApplyPending();

        Assert.NotEmpty(Payloads(0x20));
        Assert.DoesNotContain(_log.Messages, m => m.Contains("did not take its saved lighting effect"));
    }

    // Only a stream the device received counts towards giving its lighting up: a dongle that could
    // not send it has not asked the device anything.
    [Fact]
    public void StreamsThatFailToSend_CountTowardsGivingTheLightingUp_AndTheFansStayDriven() {
        _configuration.Effects[MacText(1)] = Effect(B(1, 2, 3, 4));
        _rig.Records.Add(Group(1));
        Build();
        _rig.Transmitter.FailWrite = packet => packet[0] == 0x10 && packet[1] == 0 && packet[5] == 0x20;
        for (int attempt = 0; attempt < 15; attempt++) {
            Tick();
        }

        _rig.Transmitter.FailWrite = null;
        Controller.SetTarget(0, 60);
        ClearWrites();
        Tick();

        Assert.Empty(Payloads(0x20));
        Assert.NotEmpty(Payloads(0x10));
        Assert.Contains(_log.Messages, m => m.Contains("did not take its saved lighting effect in"));
    }

    private void GiveTheLightingUp() {
        _configuration.Effects[MacText(1)] = Effect(B(1, 2, 3, 4));
        _rig.Records.Add(Group(1));
        for (int stream = 0; stream < 11; stream++) {
            Tick();
        }

        ClearWrites();
        Tick();
        Assert.Empty(Payloads(0x20));
    }

    [Fact]
    public void AReconnect_TriesAGivenUpLightingAgain() {
        GiveTheLightingUp();

        _rig.Transmitter.Generation = 1;
        Tick();

        Assert.NotEmpty(Payloads(0x20));
    }

    [Fact]
    public void ADeviceLostAndHeardAgain_HasItsGivenUpLightingTriedAgain() {
        GiveTheLightingUp();
        FakeWirelessRecord group = _rig.Records[0];
        _rig.Records.Clear();
        for (int read = 0; read < 31; read++) {
            Tick();
        }

        _rig.Records.Add(group);
        Tick();
        ClearWrites();
        Tick();

        Assert.NotEmpty(Payloads(0x20));
    }

    // A look streamed moments before FanControl closes the plugin is still saved to the devices.
    [Fact]
    public void Closing_WithAStreamedLookNotYetSaved_SavesItFirst() {
        _configuration.Effects[MacText(1)] = Effect(B(1, 2, 3, 4));
        _rig.Records.Add(Group(1));
        Build();
        Tick(); // streams; the debounced save is ten seconds away
        ClearWrites();

        Controller.Dispose();
        Controller.Dispose();

        Assert.Equal(0x15, Assert.Single(Payloads(0x15)).Payload[1]); // once, however often it is closed
    }

    // The save schedule outlives a controller FanControl's refresh closes: the save made on the way
    // out is not made again by the controller built in its place.
    [Fact]
    public void ALookSavedOnClosing_IsNotSavedAgainByTheControllerBuiltAfterTheRefresh() {
        _configuration.Effects[MacText(1)] = Effect(B(1, 2, 3, 4));
        _rig.Records.Add(Group(1));
        Build();
        Tick();
        Controller.Dispose();
        _rig.Records.Clear();
        Build();
        ClearWrites();

        for (int second = 0; second < 15; second++) {
            Tick();
        }

        Assert.Empty(Payloads(0x15));
    }

    [Fact]
    public void Closing_WithNothingUnsaved_WritesNothing() {
        _rig.Records.Add(Group(1));
        Build();
        Tick();
        ClearWrites();

        Controller.Dispose();

        Assert.Empty(_rig.Transmitter.Writes);
    }

    // ---------- reconnect ----------

    // A reopened dongle: the registered replay runs, and the cycle - master query included, which
    // re-asserts the channel - runs at once rather than at the next second.
    [Fact]
    public void AReopenedDongle_RunsTheReplayAndTheCycleAtOnce() {
        Build();
        int replays = 0;
        Controller.ReplayOnReconnect(() => replays++);
        Tick();
        ClearWrites();

        _rig.Receiver.Generation = 1;
        Controller.ApplyPending();

        Assert.Equal(1, replays);
        Assert.Contains(_rig.Transmitter.Writes, w => w[0] == 0x11);
        Assert.Contains("W4 reconnected (transmitter generation 0, receiver generation 1)", _log.Messages);

        Controller.ApplyPending();
        Assert.Equal(1, replays);
    }

    [Fact]
    public void AReplayThatThrows_IsTriedAgainNextTick() {
        Build();
        int attempts = 0;
        Controller.ReplayOnReconnect(() => {
            if (++attempts == 1) {
                throw new IOException("replay failed");
            }
        });
        _rig.Transmitter.Generation = 2;

        Assert.Throws<IOException>(() => Controller.ApplyPending());
        Controller.ApplyPending();

        Assert.Equal(2, attempts);
    }

    [Fact]
    public void AReopenWithoutAReplay_StillRunsTheCycle() {
        Build();
        Tick();
        ClearWrites();
        _rig.Transmitter.Generation = 1;

        Controller.ApplyPending();

        Assert.Contains(_rig.Transmitter.Writes, w => w[0] == 0x11);
    }

    // ---------- the fault log ----------

    [Fact]
    public void AFailureThatChangesMessage_IsLoggedAgain() {
        Build();
        _rig.MasterAnswers = false;
        Tick();
        _rig.MasterClockTicks = 0;
        _rig.MasterAnswers = true;
        Tick();

        Assert.Contains("W4 master failed: the transmitter did not answer the master query", _log.Messages);
        Assert.Contains("W4 master failed: the transmitter has not started its clock", _log.Messages);
    }
}
