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

    // The list reads a command's result is read back for after each round
    // (SaveThemeSwitchAfterReadback's four seconds).
    private const int ConfirmationReadbackReads = 4;

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

    private List<string> ControlIds()
        => Enumerable.Range(0, Controller.ChannelCount).Select(c => Controller.Describe(c).ControlId).ToList();

    // MasterDevice.SyncMasterClock's date and time: DateTime.Now, so the fake clock's instant in local time.
    private static byte[] TimeBytes(DateTime utc) {
        DateTime local = utc.ToLocalTime();
        return B(local.Year >> 8, local.Year & 0xFF, local.Month, local.Day, local.Hour, local.Minute, local.Second);
    }

    // The clock payload with nothing on any screen: header, master, the time, and an empty table.
    private static byte[] ClockPayload(DateTime utc, params (int Offset, byte[] Bytes)[] screens)
        => Expected(240, new[] { (0, B(0x12, 0x14)), (8, Master), (46, TimeBytes(utc)) }.Concat(screens).ToArray());

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
    // looking live: a fan reads 0, not its last speed for ever.
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
    [InlineData(43, "UNI FAN SL-INF FLEX")]
    [InlineData(51, "UNI FAN TL FLEX")]
    [InlineData(59, "UNI FAN SL FLEX")]
    [InlineData(63, "UNI FAN P28 V2")]
    [InlineData(126, "UNI FAN CL FLEX")]
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

    // A late device with no subscriber is adopted all the same.
    [Fact]
    public void ALateDevice_IsAdoptedWithoutASubscriber() {
        Build();
        _rig.Records.Add(Group(1));

        Tick();

        Assert.Equal(1, Controller.ChannelCount);
    }

    // A fan count past the record's four slots is clamped, not trusted.
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

    // The V150 is a case device (LWirlessCaseConfig): the app always creates its front profile
    // (V150SubProfile.CreateFrom) and the rear one only once fans_type[2] == 1. The front control
    // takes the device's plain control id, so an existing curve bound to the V150 stays bound.
    [Fact]
    public void TheV150_GetsAFrontControlEvenWithoutFans_UnderTheDevicesPlainControlId() {
        _rig.Records.Add(Device(1, 66));

        Assert.Equal(1, Controller.ChannelCount);
        Assert.Equal("LianLi/wa00000000001/ctl", Controller.Describe(0).ControlId);
        Assert.Equal("Lian Li V150 Wireless 000001 Front Fans", Controller.Describe(0).ControlName);
        Assert.Equal("LianLi/wa00000000001/f0/fan", Controller.Describe(0).RpmId);
        Assert.Equal(0, Controller.FanSpeedCount);
        Assert.Equal(0f, Controller.GetRpm(0));
    }

    [Fact]
    public void TheV150_GetsARearControl_OnlyOnceARearFanIsFitted() {
        FakeWirelessRecord v150 = Device(1, 66, fans: 2);
        v150.FanTypes = B(1, 1, 0, 0);
        _rig.Records.Add(v150);
        Assert.Equal(new[] { "LianLi/wa00000000001/ctl" }, ControlIds());
        Assert.Equal(new[] { "LianLi/wa00000000001/f0/fan", "LianLi/wa00000000001/f1/fan" }, FanSpeedIds());

        v150.FanTypes = B(1, 1, 1, 0);
        v150.FanCountByte = 3;
        Tick();

        Assert.Equal(new[] { "LianLi/wa00000000001/ctl", "LianLi/wa00000000001/rear/ctl" }, ControlIds());
        Assert.Equal("Lian Li V150 Wireless 000001 Rear Fan", Controller.Describe(1).ControlName);
        Assert.Equal("LianLi/wa00000000001/f2/fan", Controller.Describe(1).RpmId);
        Assert.Equal(3, Controller.FanSpeedCount);
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
    public void TheFlagAndFirmwareNibbles_AreMaskedOffTheReadings() {
        FakeWirelessRecord group = Group(1, fans: 4, fanType: 51);
        group.Rpm = new[] { 1000, 1001, 1002, 1003 };
        group.RpmHighNibbles = B(0xF, 0x8, 0x1, 0x8); // every flag, and TL FLEX firmware version 0x18
        _rig.Records.Add(group);

        Assert.Equal(new[] { 1000f, 1001f, 1002f, 1003f }, Enumerable.Range(0, 4).Select(Controller.GetFanSpeed));
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
    [InlineData(51, 5, 28)]  // TL FLEX floors at 11
    [InlineData(59, 5, 35)]  // SL FLEX 120 at 14
    [InlineData(63, 0, 2)]   // P28 V2 idles at 1%
    [InlineData(63, 5, 20)]  // and floors at 8
    public void TheDuty_FollowsTheServicesIdleAndFloorRules(int fanType, int duty, int pwm) {
        _rig.Records.Add(Group(1, fanType: fanType, pwm: 200));
        Controller.SetTarget(0, duty);

        Tick();

        Assert.Equal(B(pwm, pwm, pwm, pwm), SpeedPayloads().Last().Payload[17..21]);
    }

    // NeedSyncPwm steps a CL or CL FLEX group's 153-154 down to 152 and 155 up to 156.
    [Theory]
    [InlineData(41, 60, 152)]
    [InlineData(41, 61, 156)]
    [InlineData(41, 59, 150)]
    [InlineData(126, 60, 152)]
    [InlineData(127, 61, 156)]
    [InlineData(20, 60, 153)]
    public void ACLGroup_IsSteppedOffItsReservedValues(int fanType, int duty, int pwm) {
        _rig.Records.Add(Group(1, fanType: fanType, pwm: 0));
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

    // startWriteCaseSpeed: the V150's front on slots 0-1 and rear on slot 2, floored at 11, through
    // SetCaseSpeed with slot 3 zero. NeedSyncPwm's V150 rule (any target at or below 10) then fires
    // every second on that zero, so the speed goes out each second, as L-Connect sends it.
    [Fact]
    public void TheV150_IsSentFrontAndRearWithSlotThreeZero_EverySecond() {
        FakeWirelessRecord v150 = Device(1, 66, fans: 3, receiver: 6);
        v150.FanTypes = B(1, 1, 1, 0);
        v150.Pwm = B(40, 40, 40, 40);
        _rig.Records.Add(v150);
        Controller.SetTarget(ControlIndex("LianLi/w" + MacText(1) + "/ctl"), 3);
        Tick();
        Assert.Equal(SpeedPayload(1, 6, 8, 1, 28, 28, 40, 0), Assert.Single(SpeedPayloads()).Payload);
        Assert.Contains("Set W4:a00000000001 front = 3%", _log.Messages);

        Controller.SetTarget(ControlIndex("LianLi/w" + MacText(1) + "/rear/ctl"), 50);
        v150.Pwm = B(28, 28, 40, 0);
        ClearWrites();
        Tick();
        Assert.Equal(SpeedPayload(1, 6, 8, 1, 28, 28, 127, 0), Assert.Single(SpeedPayloads()).Payload);

        v150.Pwm = B(28, 28, 127, 0);
        ClearWrites();
        Tick();
        Tick();
        Assert.Equal(2, SpeedPayloads().Count);
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
    public void ADeviceUnheardForThirtyReads_IsDroppedAsLConnectDropsIt_ReadsZero_AndIsDrivenAgainWhenHeard() {
        FakeWirelessRecord block = Device(1, 10, fans: 1);
        block.FanTypes = B(41, 0, 0, 30);
        block.Rpm = new[] { 900, 0, 0, 2400 };
        _rig.Records.Add(block);
        _rig.Records.Add(Group(2, receiver: 2));
        Controller.SetTarget(0, 50);
        Controller.SetTarget(1, 50);
        Tick();
        _rig.Records.RemoveAt(0);

        for (int read = 0; read < 29; read++) {
            Tick();
        }

        Assert.Equal(900f, Controller.GetFanSpeed(0));
        Assert.Equal(2, Controller.DeviceCount);
        Tick();
        Assert.Equal(0f, Controller.GetFanSpeed(0));
        Assert.Equal(0f, Controller.GetRpm(1));
        Assert.Null(Controller.GetTemperature(0));
        Assert.Equal(1, Controller.DeviceCount);
        Assert.Contains(_log.Messages, m => m.StartsWith("W4:a00000000001 not heard for 30 list reads; dropped from the device list", StringComparison.Ordinal));

        // Off the table nothing is sent to it, and the group after it takes its bind index.
        ClearWrites();
        Controller.SetTarget(0, 100);
        Controller.SetTarget(1, 100);
        Controller.SetTarget(ControlIndex("LianLi/w" + MacText(2) + "/ctl"), 50);
        Tick();
        Assert.Empty(Payloads(0x21));
        Assert.Equal(1, Assert.Single(SpeedPayloads()).Payload[16]);

        // Heard again it is appended after the group, numbered second, sent the duty set meanwhile,
        // and its sensors read again under the ids they had.
        _rig.Records.Add(block);
        Tick();
        ClearWrites();
        Tick();
        Assert.Equal(900f, Controller.GetFanSpeed(0));
        Assert.Equal(30f, Controller.GetTemperature(0));
        Assert.Equal(2, Controller.DeviceCount);
        Assert.Equal(2, SpeedPayloads().Single(p => p.Payload[7] == 1).Payload[16]);
        Assert.Equal(255, SpeedPayloads().Single(p => p.Payload[7] == 1).Payload[17]);
        Assert.NotEmpty(Payloads(0x21));
        Assert.Contains("W4:a00000000001 heard again", _log.Messages);
        Assert.Equal(new[] { "LianLi/wa00000000001/f0/fan", "LianLi/wa00000000001/pump/fan", "LianLi/wa00000000002/f0/fan", "LianLi/wa00000000002/f1/fan" }, FanSpeedIds());
    }

    // The set of devices the radio drives, kept for the process so a controller reaching a FLEX
    // group over its USB receiver can leave it alone: replaced after every list read, forgotten on close.
    [Fact]
    public void TheDevicesDriven_AreKeptForTheProcess_UntilTheControllerCloses() {
        _rig.Records.Add(Group(1));
        _rig.Records.Add(Group(2, receiver: 2, master: Other));
        Build();
        Assert.True(_processState.IsBoundToMaster(MacText(1)));
        Assert.False(_processState.IsBoundToMaster(MacText(2)));

        _rig.Records.Clear();
        for (int read = 0; read < 30; read++) {
            Tick();
        }

        Assert.False(_processState.IsBoundToMaster(MacText(1)));
        Assert.False(_processState.IsBoundToMaster(MacText(2)));
        _rig.Records.Add(Group(1));
        Tick();
        Assert.True(_processState.IsBoundToMaster(MacText(1)));
        Assert.False(_processState.IsBoundToMaster(MacText(2)));

        Controller.Dispose();
        Assert.False(_processState.IsBoundToMaster(MacText(1)));
        Assert.False(_processState.IsBoundToMaster(MacText(2)));
    }

    // WinUsb.RfRead: L-Connect's iReadErr is one static count across both dongles, reset by any
    // read that carries something. A receiver that answers nothing while the transmitter answers
    // the master query each second is never reset by it; once both go quiet, the fifth empty read
    // in a row resets the dongle it was read from through its partner.
    [Fact]
    public void ASilentReceiver_IsNotResetWhileTheTransmitterAnswers_AndTheFifthEmptyReadAcrossBothResetsItsDongle() {
        _rig.Records.Add(Group(1));
        Build();
        _rig.ReceiverSilent = true;
        ClearWrites();

        for (int second = 0; second < 10; second++) {
            Tick();
        }

        Assert.DoesNotContain(_rig.Transmitter.Writes, w => w[0] == 0x15);
        Assert.DoesNotContain(_rig.Receiver.Writes, w => w[0] == 0x15);

        // Both quiet, the count at one from the last list read: the master query's read (2), the
        // list's (3), the query's (4), then the list's again is the fifth, so the receiver is reset.
        _rig.MasterAnswers = false;
        Tick();
        Assert.DoesNotContain(_rig.Transmitter.Writes, w => w[0] == 0x15);
        Tick();

        Assert.Single(_rig.Transmitter.Writes, w => w[0] == 0x15);
        Assert.DoesNotContain(_rig.Receiver.Writes, w => w[0] == 0x15);
        Assert.Contains("W4: receiver read nothing for the 5th time in a row across both dongles; reset it through the transmitter", _log.Messages);

        // The count starts again: two more ticks bring it to four, and the query's read is the fifth.
        Tick();
        Tick();
        Assert.DoesNotContain(_rig.Receiver.Writes, w => w[0] == 0x15);
        Tick();
        Assert.Single(_rig.Receiver.Writes, w => w[0] == 0x15);
        Assert.Contains("W4: transmitter read nothing for the 5th time in a row across both dongles; reset it through the receiver", _log.Messages);
    }

    // A master query answered with a running clock but no address leaves the controller without
    // a master (QuerryMasterMac): RF writes stop, and nothing is published as driven over the
    // radio, so a controller reaching a device another way may take it.
    [Fact]
    public void NoMaster_StopsAdvertisingDevicesAsDriven() {
        _rig.Records.Add(Group(1));
        Build();
        Assert.True(_processState.IsBoundToMaster(MacText(1)));

        _rig.Master = new byte[6];
        Tick();

        Assert.False(_processState.IsBoundToMaster(MacText(1)));
        Assert.Equal(1, Controller.ChannelCount); // the sensors stay

        _rig.Master = (byte[])Master.Clone();
        Tick();
        Assert.True(_processState.IsBoundToMaster(MacText(1)));
    }

    // Two controllers in one process (the one closing after FanControl's refresh and the one
    // built in its place) each publish their own set; the old one's close leaves the new one's.
    [Fact]
    public void TheDevicesDriven_ArePublishedPerController() {
        _rig.Records.Add(Group(1));
        Build();
        WirelessController earlier = Controller;
        _controller = null;
        Build();

        earlier.Dispose();

        Assert.True(_processState.IsBoundToMaster(MacText(1)));
    }

    // What the controller drives against what it only retains: a group's sensors stay after it is
    // unbound (its record names no master) or the master is lost, but only a driven group's are
    // the controller's to claim from a receiver driving the same chain over USB.
    [Fact]
    public void TheSensorsDriven_AreThoseOfTheDevicesDrivenNow_TheRestRetained() {
        _rig.Records.Add(Group(1));
        Build();
        Assert.Equal(
            new[] { "LianLi/wa00000000001/ctl", "LianLi/wa00000000001/f0/fan", "LianLi/wa00000000001/f1/fan" },
            Controller.DrivenSensorIds);

        _rig.Records.Clear();
        _rig.Records.Add(Group(1, master: new byte[6]));
        Tick();
        Assert.Empty(Controller.DrivenSensorIds);
        Assert.Equal(1, Controller.ChannelCount);
        Assert.Equal(2, Controller.FanSpeedCount);

        _rig.Records.Clear();
        _rig.Records.Add(Group(1));
        Tick();
        Assert.Equal(3, Controller.DrivenSensorIds.Count());

        _rig.Master = new byte[6];
        Tick();
        Assert.Empty(Controller.DrivenSensorIds);
    }

    // A group's duty is kept in the shared state as well, since its chain may be a FLEX chain
    // whose FanControl control is on its USB receiver: a duty the receiver put there is sent
    // over the radio when this controller drives the chain and its own control has none. A control
    // named for its part (the Lancool 217's front pair) has no chain to share with.
    [Fact]
    public void AGroupsDuty_IsKeptInTheSharedState_AndOneKeptThereForTheChainIsSentWhenTheControlHasNone() {
        _rig.Records.Add(Group(1));
        Controller.SetTarget(0, 50);
        Assert.Equal(50, _processState.ChainTarget(MacText(1)));
        Controller.ReleaseChannel(0);
        Assert.Equal(-1, _processState.ChainTarget(MacText(1)));
        ClearWrites();

        _processState.SetChainTarget(MacText(1), 50);
        Tick();

        (_, byte[] payload) = Assert.Single(SpeedPayloads());
        Assert.Equal(SpeedPayload(1, 1, 8, 1, 127, 127, 127, 127), payload);
        Assert.Contains("Set W4:a00000000001 fans = 50%", _log.Messages);

        _processState.ReleaseChainTarget(MacText(1));
        ClearWrites();
        Tick();
        Assert.Empty(SpeedPayloads());
    }

    [Fact]
    public void ACaseFanControlsDuty_IsNotKeptInTheSharedState() {
        FakeWirelessRecord lancool = Device(1, 65, receiver: 2);
        lancool.FanTypes = B(1, 1, 1, 0);
        _rig.Records.Add(lancool);
        Controller.SetTarget(ControlIndex("LianLi/wa00000000001/front/ctl"), 30);
        Controller.ReleaseChannel(ControlIndex("LianLi/wa00000000001/front/ctl"));

        Assert.Equal(-1, _processState.ChainTarget(MacText(1)));
    }

    // V150SubProfile.UpdateFrom: the front speed is Speeds[0] when fans_type[0] == 1 and Speeds[1]
    // otherwise, the rear's Speeds[2]; the app reads the fitted slots, not fan_num, so a reading is
    // registered for every fitted slot 0-2 as well as the first fan_num slots.
    [Fact]
    public void TheV150_ReadsItsRearFansSpeedFromSlotTwo_WhateverTheFanCount() {
        FakeWirelessRecord v150 = Device(1, 66, fans: 2);
        v150.FanTypes = B(0, 1, 1, 0);
        v150.Rpm = new[] { 0, 1100, 1200, 0 };
        _rig.Records.Add(v150);

        int rear = ControlIndex("LianLi/w" + MacText(1) + "/rear/ctl");
        Assert.Equal(1200f, Controller.GetRpm(rear));
        Assert.Equal("LianLi/w" + MacText(1) + "/f2/fan", Controller.Describe(rear).RpmId);
        Assert.Equal(new[] { "LianLi/wa00000000001/f0/fan", "LianLi/wa00000000001/f1/fan", "LianLi/wa00000000001/f2/fan" }, FanSpeedIds());
    }

    [Fact]
    public void TheV150_ReadsTheFittedFrontFansSpeed_AndFollowsItFromRecordToRecord() {
        FakeWirelessRecord v150 = Device(1, 66, fans: 2);
        v150.FanTypes = B(0, 1, 1, 0);
        v150.Rpm = new[] { 0, 1100, 1200, 0 };
        _rig.Records.Add(v150);

        Assert.Equal(1100f, Controller.GetRpm(0));
        Assert.Equal("LianLi/wa00000000001/f1/fan", Controller.Describe(0).RpmId);

        v150.FanTypes = B(1, 1, 1, 0);
        v150.Rpm = new[] { 1000, 1100, 1200, 0 };
        Tick();

        Assert.Equal(1000f, Controller.GetRpm(0));
        Assert.Equal("LianLi/wa00000000001/f0/fan", Controller.Describe(0).RpmId);
    }

    // ---------- the lighting source (0x27) ----------

    // LWirelessController.ResumeSuspend: a bound device whose MotherboardARGBSync setting is saved on
    // is sent SetMotherboardARGBSync(mac, true) - RFController.SyncMBLightSwitch(mac, isClose: false),
    // ten sends of 0x27 with 1 at byte 20 under a fresh command sequence. Its saved effect is still
    // streamed to it: MasterDevice.SyncRgbData streams every bound device whose effect_index is not
    // the saved one and never reads IsSyncMbLight, so the flash holds the effect for the switch
    // being turned off; the switch only decides what the LEDs show.
    [Fact]
    public void ADeviceWhoseSyncSwitchIsSavedOn_IsHandedToTheMotherboard_AndStillStreamedItsEffect() {
        _configuration.MotherboardArgbSync.Add(MacText(1));
        _configuration.Effects[MacText(1)] = Effect(B(1, 2, 3, 4));
        _rig.Records.Add(Group(1, receiver: 2));
        _rig.Records[0].Sequence = 7;
        Build();
        Assert.Equal(1, Controller.LitDeviceCount);
        Assert.Contains("W4:a00000000001 lighting is left to the motherboard's ARGB header (L-Connect's sync switch for it is on); its saved effect is still streamed to its flash, as L-Connect streams it", _log.Messages);
        ClearWrites();

        Tick();

        Assert.NotEmpty(Payloads(0x20));
        Assert.Equal(
            Expected(240, (0, B(0x12, 0x27)), (2, Mac(1)), (8, Master), (14, B(2, 8, 0, 8)), (20, B(1))),
            Assert.Single(Payloads(0x27)).Payload);
        Assert.Contains(MacText(1), _configuration.EffectLookups);
        Assert.Contains("W4:a00000000001 streaming its saved lighting effect 01020304 (2 chunks) until it reports running it", _log.Messages);
    }

    // The handover is confirmed by the record's source flag (IsSyncMbLight), read back after the
    // round the acknowledgement ends, as the theme switch is confirmed by its bits: the sequence
    // alone acknowledges whichever command carried it.
    [Fact]
    public void TheLightingHandover_IsConfirmedByTheSourceFlag_OnceTheDeviceReportsItsSequence() {
        _configuration.MotherboardArgbSync.Add(MacText(1));
        FakeWirelessRecord group = Group(1);
        group.RpmHighNibbles = B(0x4, 0, 0, 0); // IsSyncMbLight
        _rig.Records.Add(group);
        Build();
        Tick(); // the first round goes out whatever the flag says, as ResumeSuspend sends it regardless
        group.Sequence = 1;
        Tick(); // the list read at the end of this tick hears the acknowledgement
        ClearWrites();

        Tick(); // acknowledged: the round ends
        Tick(); // the readback finds the flag

        Assert.Empty(Payloads(0x27));
        Assert.Single(_log.Messages, m => m == "W4:a00000000001 handed its lighting to the motherboard's ARGB header, as L-Connect's sync switch for it says; it reports following the header");
        Tick();
        Assert.Empty(Payloads(0x27));
    }

    // An acknowledgement without the flag is not the handover taking effect (in L-Connect's model
    // the sequence is the device's, so it could be another command's): the record is read back for
    // four reads, and one more round goes out under the next sequence; the flag showing then
    // confirms it.
    [Fact]
    public void TheLightingHandover_AcknowledgedWithoutTheSourceFlag_GetsAnotherRound_ThenIsConfirmedByTheFlag() {
        _configuration.MotherboardArgbSync.Add(MacText(1));
        FakeWirelessRecord group = Group(1);
        _rig.Records.Add(group);
        Build();
        Tick();
        group.Sequence = 1;
        Tick();
        Tick(); // acknowledged, flag clear
        for (int read = 0; read < ConfirmationReadbackReads; read++) {
            Tick();
        }

        Assert.Equal(2, Payloads(0x27).Count);
        Assert.All(Payloads(0x27), p => Assert.Equal(1, p.Payload[17]));
        Assert.DoesNotContain(_log.Messages, m => m.Contains("handed its lighting"));

        Tick(); // the second round, under the next sequence
        Assert.Equal(2, Payloads(0x27).Last().Payload[17]);
        group.Sequence = 2;
        group.RpmHighNibbles = B(0x4, 0, 0, 0);
        Tick();
        Tick(); // acknowledged
        Tick(); // confirmed

        Assert.Equal(4, Payloads(0x27).Count);
        Assert.Single(_log.Messages, m => m == "W4:a00000000001 handed its lighting to the motherboard's ARGB header, as L-Connect's sync switch for it says; it reports following the header");
    }

    // Never acknowledged, or acknowledged but never reporting the flag: two rounds of ten sends
    // with a readback after each, then the device is left as it is, its fans driven throughout.
    [Fact]
    public void TheLightingHandover_GetsTwoRoundsOfTenSends_WithAReadbackAfterEach_ThenIsLeft() {
        _configuration.MotherboardArgbSync.Add(MacText(1));
        _rig.Records.Add(Group(1));
        Build();

        for (int second = 0; second < 30; second++) {
            Tick();
        }

        Assert.Equal(20, Payloads(0x27).Count); // ticks 1-10, then 16-25
        Assert.Equal(new byte[] { 1, 2 }, Payloads(0x27).Select(p => p.Payload[17]).Distinct()); // each round under the device's next sequence
        Assert.DoesNotContain(_log.Messages, m => m.Contains("left as it is"));
        Tick();
        Assert.Equal(20, Payloads(0x27).Count);
        Assert.Single(_log.Messages, m => m == "W4:a00000000001 still reports its lighting not following the motherboard's ARGB header after 2 rounds of the handover; left as it is");
        for (int second = 0; second < 5; second++) {
            Tick();
        }

        Assert.Equal(20, Payloads(0x27).Count);
    }

    // A device has one command sequence (RfDevice.targe_cmd_seq), and reporting it back
    // acknowledges every command L-Connect has pending for the device, so the plugin sends the
    // commands one at a time: the theme switch waits while the lighting handover has the device's
    // sequence and takes the next one once that round has ended. A 0x27 lost on the air while a
    // 0x29 got through can therefore never be taken as acknowledged by the 0x29's sequence.
    [Fact]
    public void TheThemeSwitch_WaitsWhileTheLightingHandoverHasTheDevicesSequence_ThenTakesTheNext() {
        _configuration.MotherboardArgbSync.Add(MacText(1));
        _configuration.FanScreens[MacText(1)] = new WirelessFanScreenPresentation(B(1, 2, 3, 4), B(0, 0, 0, 0), 60, 1, false);
        _processState.MarkScreensColoured(MacText(1), 0); // coloured earlier in this process, so the switch is the next command due
        FakeWirelessRecord group = Group(1, fans: 2, fanType: 51);
        _rig.Records.Add(group);
        Build();

        Tick();
        Assert.Single(Payloads(0x27));
        Assert.Empty(Payloads(0x29));
        Assert.DoesNotContain(_log.Messages, m => m.Contains("switching its screens"));

        group.Sequence = 1;
        Tick(); // the handover's second send; the list read hears the acknowledgement
        Assert.Empty(Payloads(0x29));
        ClearWrites();

        Tick(); // the handover's round ends; the theme switch takes the device's next sequence in the same cycle

        Assert.Empty(Payloads(0x27));
        Assert.Equal(2, Assert.Single(Payloads(0x29)).Payload[17]);
        Assert.Contains("W4:a00000000001 switching its screens onto their wireless themes (switches 0x0 against 0x6)", _log.Messages);
    }

    // A dongle that came back frees the device's sequence with everything else it resets, so the
    // handover is sent again at once, under the next sequence, instead of waiting for a round
    // that was cut short.
    [Fact]
    public void TheLightingHandover_IsSentAgainUnderTheNextSequence_WhenTheDongleComesBackMidRound() {
        _configuration.MotherboardArgbSync.Add(MacText(1));
        _rig.Records.Add(Group(1));
        Build();
        Tick();
        Tick();

        _rig.Transmitter.Generation = 1;
        Tick();

        Assert.Equal(new byte[] { 1, 1, 2 }, Payloads(0x27).Select(p => p.Payload[17]));
    }

    // ResumeSuspend sends it again on every resume; the plugin sends it again whenever the device
    // or its dongle comes back.
    [Fact]
    public void TheLightingHandover_IsSentAgain_WhenTheDeviceIsHeardAgain() {
        _configuration.MotherboardArgbSync.Add(MacText(1));
        _rig.Records.Add(Group(1));
        Build();
        Tick();
        _rig.Records[0].Sequence = 1;
        Tick();
        FakeWirelessRecord group = _rig.Records[0];
        _rig.Records.Clear();
        for (int second = 0; second <= WirelessDevice.MaximumMissedReads; second++) {
            Tick();
        }

        _rig.Records.Add(group);
        ClearWrites();
        Tick();
        Tick();

        Assert.Equal(2, Payloads(0x27).Single().Payload[17]);
    }

    [Fact]
    public void ADeviceWithoutTheSwitch_IsSentNoLightingHandover() {
        _rig.Records.Add(Group(1));
        Build();

        Tick();

        Assert.Empty(Payloads(0x27));
    }

    // ---------- the LCD FLEX screens' theme switches (0x29) ----------

    // applyWirelessLCDMode, for a bound LCD FLEX group with a saved configuration not in advance
    // mode: after the table entries, FlexLCDSdkHelper.TryRestoreWirelessThemeSwitches queues
    // PlayWiredlessThemeSwitch for every fan whose switch bit is clear - ten sends of 0x29 with the
    // target mask at byte 20 under a fresh sequence - so a screen left on streamed content shows its
    // theme; the record's bits are then read back, and the switch saved to flash once they match.
    [Fact]
    public void AFlexGroupSavedOutOfAdvanceMode_HasItsScreensSwitchedOntoTheirThemes_AndTheSwitchSaved() {
        var problems = new List<string>();
        _configuration.FanScreens[MacText(1)] = LConnectWirelessConfiguration.ParseFanScreenSettings(
            "{\"Data\":{\"a00000000001\":{\"IsAdvanceMode\":false,\"TemplateParams\":{\"Theme1\":5,\"Theme2\":6,\"Brightness\":80}}}}",
            problems)[MacText(1)];
        Assert.Empty(problems);
        _processState.MarkScreensColoured(MacText(1), 0); // coloured earlier in this process; the colours are their own test
        FakeWirelessRecord group = Group(1, fans: 2, fanType: 51, receiver: 3);
        _rig.Records.Add(group);
        Build();
        ClearWrites();

        Tick();

        Assert.NotEmpty(Payloads(0x14));
        Assert.Equal(
            Expected(240, (0, B(0x12, 0x29)), (2, Mac(1)), (8, Master), (14, B(3, 8, 0, 1)), (20, B(0b0110))), // fans 0-1 are bits 2-1 (max(2, 3) - 1 - i)
            Assert.Single(Payloads(0x29)).Payload);
        Assert.Contains("W4:a00000000001 switching its screens onto their wireless themes (switches 0x0 against 0x6)", _log.Messages);

        group.Sequence = 1;
        group.RpmHighNibbles = B(0, 0x6, 0, 0); // the screens report their switches
        Tick(); // one more send, then the list read hears both
        Tick(); // acknowledged
        ClearWrites();
        Tick(); // the readback matches: saved

        Assert.Empty(Payloads(0x29));
        Assert.Single(Payloads(0x15));
        Assert.Contains("W4:a00000000001 screens all show their wireless themes again (switches 0x6); asked every device to save", _log.Messages);
        Tick();
        Assert.Single(Payloads(0x15));
    }

    // SaveThemeSwitchAfterReadback: four seconds of readback, the switches queued once more, four
    // seconds more, then it gives up without saving.
    [Fact]
    public void TheThemeSwitch_GetsTwoRoundsOfTenSends_WithAReadbackAfterEach_ThenIsLeft() {
        _configuration.FanScreens[MacText(1)] = new WirelessFanScreenPresentation(B(1, 2, 3, 4), B(0, 0, 0, 0), 60, 1, false);
        _processState.MarkScreensColoured(MacText(1), 0);
        _rig.Records.Add(Group(1, fans: 3, fanType: 43));
        Build();

        for (int second = 0; second < 30; second++) {
            Tick();
        }

        Assert.Equal(20, Payloads(0x29).Count); // ticks 1-10, then 16-25
        Assert.DoesNotContain(_log.Messages, m => m.Contains("left as they are"));
        Tick();
        Assert.Equal(20, Payloads(0x29).Count);
        Assert.Contains("W4:a00000000001 screens still report switches 0x0 against 0x7 after 2 rounds of the switch; left as they are", _log.Messages);
        Assert.Equal(new byte[] { 1, 2 }, Payloads(0x29).Select(p => p.Payload[17]).Distinct()); // each round under the device's next sequence
        Assert.Empty(Payloads(0x15));
        for (int second = 0; second < 5; second++) {
            Tick();
        }

        Assert.Equal(20, Payloads(0x29).Count);
    }

    // TryRestoreWirelessThemeSwitches queues nothing for a group whose screens all show their
    // themes; and applyConfiguredWirelessLCDModes skips a group in advance mode, or one with
    // nothing saved, as does a group without screens.
    [Fact]
    public void TheThemeSwitch_IsNotSent_WhenEveryScreenShowsItsTheme_InAdvanceMode_WithNothingSaved_OrWithoutScreens() {
        _configuration.FanScreens[MacText(1)] = new WirelessFanScreenPresentation(B(1, 2, 3, 4), B(0, 0, 0, 0), 60, 1, false);
        _configuration.FanScreens[MacText(2)] = new WirelessFanScreenPresentation(B(1, 2, 3, 4), B(0, 0, 0, 0), 60, 1, true);
        _configuration.FanScreens[MacText(4)] = new WirelessFanScreenPresentation(B(1, 2, 3, 4), B(0, 0, 0, 0), 60, 1, false);
        FakeWirelessRecord shown = Group(1, fans: 4, fanType: 51, receiver: 1);
        shown.RpmHighNibbles = B(0, 0xF, 0, 0);
        _rig.Records.Add(shown);
        _processState.MarkScreensColoured(MacText(1), 0); // its colours sent earlier in the process, so the switch is looked at from the first cycle
        _rig.Records.Add(Group(2, fans: 2, fanType: 51, receiver: 2)); // advance mode
        _rig.Records.Add(Group(3, fans: 2, fanType: 51, receiver: 3)); // nothing saved
        _rig.Records.Add(Group(4, fans: 2, fanType: 53, receiver: 4)); // TL FLEX LED: no screens
        Build();

        Tick();
        Tick();

        Assert.Empty(Payloads(0x29));
        Assert.DoesNotContain(_log.Messages, m => m.Contains("wireless themes"));
    }

    // The switch's fan bits are the firmware's, which reverses the service's order over at least
    // three: a lone screen is bit 2, and the mask keeps whatever bits the group already reports.
    [Fact]
    public void TheThemeSwitch_KeepsTheBitsReported_AndSetsTheGroupsOwnInTheFirmwaresOrder() {
        _configuration.FanScreens[MacText(1)] = new WirelessFanScreenPresentation(B(1, 2, 3, 4), B(0, 0, 0, 0), 60, 1, false);
        _processState.MarkScreensColoured(MacText(1), 0);
        FakeWirelessRecord group = Group(1, fans: 1, fanType: 51);
        group.RpmHighNibbles = B(0, 0x8, 0, 0);
        _rig.Records.Add(group);
        Build();

        Tick();

        Assert.Equal(0b1100, Assert.Single(Payloads(0x29)).Payload[20]);
    }

    // A group heard again after being dropped has its screens switched onto their themes again,
    // since its record says they are off them, and only that: its colours, sent once for the
    // process as L-Connect sends them once at its start, are not sent again, so the switch takes
    // the group's next sequence at once.
    [Fact]
    public void TheThemeSwitch_IsSentAgain_WithoutTheColours_WhenTheGroupIsHeardAgain() {
        _configuration.FanScreens[MacText(1)] = new WirelessFanScreenPresentation(B(1, 2, 3, 4), B(0, 0, 0, 0), 60, 1, false);
        _processState.MarkScreensColoured(MacText(1), 0);
        FakeWirelessRecord group = Group(1, fans: 2, fanType: 51);
        _rig.Records.Add(group);
        Build();
        Tick();
        group.Sequence = 1;
        group.RpmHighNibbles = B(0, 0x6, 0, 0);
        Tick();
        Tick();
        Tick();
        Assert.Single(Payloads(0x15));
        _rig.Records.Clear();
        for (int second = 0; second <= WirelessDevice.MaximumMissedReads; second++) {
            Tick();
        }

        group.RpmHighNibbles = B(0, 0, 0, 0); // back with its screens off their themes
        _rig.Records.Add(group);
        ClearWrites();
        Tick();
        Tick();

        Assert.Empty(Payloads(0x28));
        Assert.Equal(2, Payloads(0x29).Single().Payload[17]); // under the group's next sequence
    }

    // TryRestoreWirelessThemeSwitches returns, restoring nothing, for a group whose FanNum is 0: a
    // group that for a while reports no fan has no screen whose bit says anything, so its switch is
    // left pending , not taken as every screen showing its theme, and is decided against the
    // bits its screens report once a fan is back. Here the count drops while the colours wait for
    // their interval, which held the switch until then.
    [Fact]
    public void TheThemeSwitch_IsLeftPending_WhileTheGroupReportsNoFan_AndSentOnceItReportsOneAgain() {
        FakeWirelessRecord group = LookDevice(pump: false);
        _rig.Records.Add(group);
        int topologyChanges = 0;
        Controller.TopologyChanged += (_, _) => topologyChanges++;
        Tick(); // the table goes out; the colours wait for their interval, and the switch behind them
        group.FanCountByte = 0; // the count goes; the fan type stays
        Tick(); // the read hears no fan
        Tick(); // nothing to colour, and no screen whose switch to decide
        Assert.Equal(B(0, 0, 0, 0), Payloads(0x14).Last().Payload[64..68]);
        Assert.Empty(Payloads(0x28));
        Assert.Empty(Payloads(0x29));

        group.FanCountByte = 1;
        Tick(); // the read hears the fan again
        ClearWrites();
        Tick(); // the clock carries the entry again
        Assert.Equal(B(1, 0, 0, 0), Payloads(0x14).Single().Payload[64..68]);
        WaitColoursInterval(pump: false);
        Assert.Equal(1, Assert.Single(Payloads(0x28)).Payload[17]);
        Assert.Empty(Payloads(0x29));
        group.Sequence = 1;
        Tick();
        Tick(); // the colours are done; the switch follows, for the bit the screen reports clear

        Assert.True(_processState.AreScreensColoured(MacText(1)));
        byte[] theme = Assert.Single(Payloads(0x29)).Payload;
        Assert.Equal(2, theme[17]);
        Assert.Equal(0b100, theme[20]);
        Assert.Equal(0, topologyChanges);
    }

    // A round of the switch already under way when the group's count drops to none is the
    // device's to finish (ServiceRounds): its sends go on and its end is taken, after which the
    // switch is left pending, neither sent again nor done with, until a fan is back, when the bits
    // the screens report then decide it - here confirmed, and saved.
    [Fact]
    public void AThemeSwitchRoundUnderWay_WhenTheGroupReportsNoFan_IsSentToItsEnd_AndTheSwitchIsDecidedOnceAFanIsBack() {
        _configuration.FanScreens[MacText(1)] = new WirelessFanScreenPresentation(B(1, 2, 3, 4), B(0, 0, 0, 0), 60, 1, false);
        _processState.MarkScreensColoured(MacText(1), 0);
        FakeWirelessRecord group = Group(1, fans: 1, fanType: 51);
        _rig.Records.Add(group);
        Build();
        Tick(); // the first send
        Assert.Equal(0b100, Assert.Single(Payloads(0x29)).Payload[20]);
        group.FanCountByte = 0;
        Tick(); // the second send; the read hears no fan
        Tick(); // the third, by the device's own bookkeeping
        group.Sequence = 1;
        Tick(); // the fourth; the read hears the acknowledgement
        Tick(); // the device ends the round, and its end is taken; nothing to decide
        Tick();
        Assert.Equal(4, Payloads(0x29).Count);
        Assert.DoesNotContain(_log.Messages, m => m.Contains("wireless themes again") || m.Contains("left as they are"));
        Assert.Empty(Payloads(0x15));

        group.FanCountByte = 1;
        group.RpmHighNibbles = B(0, 0x4, 0, 0); // back, and the screen reports its switch
        Tick(); // the read hears it
        Tick(); // confirmed against what the screen reports

        Assert.Equal(4, Payloads(0x29).Count);
        Assert.Contains("W4:a00000000001 screens all show their wireless themes again (switches 0x4); asked every device to save", _log.Messages);
        Assert.Single(Payloads(0x15));
    }

    // ---------- the LCD FLEX screens' theme colours (0x28) ----------

    // applyWirelessLCDMode, for a bound LCD FLEX group saved out of advance mode: for each fan in
    // turn, after the table entry and the clock, UpdateSensorSettingByWiredLess sends the colours of
    // the fan's theme (the saved fan setting's, or L-Connect's own for a fan with none saved) as ten
    // sends of 0x28 under a fresh sequence, in the fan's slot of the buffer (the table's fan order,
    // so the service's fan 0 of a two-fan TL FLEX LCD group is slot 1) with a change marker after
    // them; once every fan has had its round the screens are switched. The 1200 ms sleep between
    // the clock and the colours puts the first round on the third tick, at one tick a second.
    [Fact]
    public void AFlexGroupSavedOutOfAdvanceMode_IsSentEachScreensThemeColours_ThenItsThemeSwitch() {
        var problems = new List<string>();
        _configuration.FanScreens[MacText(1)] = LConnectWirelessConfiguration.ParseFanScreenSettings(
            "{\"Data\":{\"a00000000001\":{\"IsAdvanceMode\":false,\"TemplateParams\":{\"Theme1\":5,\"Theme2\":6,\"Brightness\":80},"
            + "\"TemplateFanSettings\":{\"0:5\":{\"FanIndex\":0,\"FanThemeIndex\":5,\"FanDataSourceIndex\":11,"
            + "\"GraphColor1\":4294901760,\"GraphColor2\":65280,\"FontTitleColor\":255,\"FontDataColor\":4278255360,\"FontUnitColor\":8421504}}}}}",
            problems)[MacText(1)];
        Assert.Empty(problems);
        FakeWirelessRecord group = Group(1, fans: 2, fanType: 51, receiver: 3);
        _rig.Records.Add(group);
        Build();
        ClearWrites();

        Tick();
        DateTime tableFirstCarried = _clock.UtcNow;
        Assert.NotEmpty(Payloads(0x14));
        Assert.Empty(Payloads(0x28));
        Assert.Empty(Payloads(0x29));
        Tick(); // about 1000 ms after the clock that carried the table: short of the 1200
        Assert.True(_clock.UtcNow - tableFirstCarried < TimeSpan.FromMilliseconds(1200));
        Assert.Empty(Payloads(0x28));
        Assert.Empty(Payloads(0x29));
        Tick();

        Assert.True(_clock.UtcNow - tableFirstCarried >= TimeSpan.FromMilliseconds(1200));
        Assert.Empty(Payloads(0x29));
        byte[] saved = B(0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF, 0, 0xFF, 0, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 1); // theme 5: graph 1, graph 2, title, data, unit, unit; marker 1
        Assert.Equal(
            Expected(240, (0, B(0x12, 0x28)), (2, Mac(1)), (8, Master), (14, B(3, 8, 0, 1)), (20 + 19, saved)),
            Assert.Single(Payloads(0x28)).Payload);

        group.Sequence = 1;
        Tick(); // one more send, then the list read hears the acknowledgement
        ClearWrites();
        Tick(); // the round ends; the second screen's colours take the next sequence in the same cycle

        byte[] defaults = B(0x99, 0x2E, 0x63, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 2); // theme 6's own: graph 1, title, data, unit, and no shadows; marker 2
        Assert.Equal(
            Expected(240, (0, B(0x12, 0x28)), (2, Mac(1)), (8, Master), (14, B(3, 8, 0, 2)), (20, defaults)),
            Assert.Single(Payloads(0x28)).Payload);
        Assert.Empty(Payloads(0x29));

        group.Sequence = 2;
        Tick();
        ClearWrites();
        Tick(); // every screen has had its round: the switch follows in the same cycle

        Assert.Empty(Payloads(0x28));
        Assert.Equal(3, Assert.Single(Payloads(0x29)).Payload[17]);
        Assert.Contains("W4:a00000000001 sent its 2 screen(s) the colours of their wireless themes", _log.Messages);
        Assert.True(_processState.AreScreensColoured(MacText(1)));
    }

    // SyncMasterClock reads DateTime.Now for the clock's date and time before SendRfData, and
    // UpdateSensorSettingByWiredLess sleeps its 1200 ms after SyncMasterClock has returned: the
    // four packets' writes, up to 100 ms each on the pipe, are no part of the interval. Here each
    // packet takes 90 ms, and a control change wakes the worker one second after the previous
    // cycle began, 640 ms after its table completed, so a wait measured from the send would be
    // short by the broadcast's length.
    [Fact]
    public void TheClocksTimeIsReadBeforeItsSend_AndTheColoursWaitRunsFromItsCompletion() {
        FakeWirelessRecord group = LookDevice(pump: false);
        _rig.Records.Add(group);
        Build();
        ClearWrites();
        DateTime? sent = null;
        DateTime? completed = null;
        bool inClock = false;
        _rig.Transmitter.FailWrite = packet => {
            if (packet[0] != 0x10) {
                return false;
            }

            if (packet[1] == 0) {
                inClock = packet[5] == 0x14;
                if (inClock && sent is null) {
                    sent = _clock.UtcNow;
                }
            }

            if (inClock) {
                _clock.Advance(TimeSpan.FromMilliseconds(90));
                if (packet[1] == 3 && completed is null) {
                    completed = _clock.UtcNow;
                }
            }

            return false;
        };

        Tick();

        Assert.NotNull(sent);
        Assert.NotNull(completed);
        Assert.Equal(sent.Value.AddMilliseconds(360), completed.Value);
        Assert.Equal(TimeBytes(sent.Value), Payloads(0x14).Single().Payload[46..53]); // the moment before the first packet
        _clock.Advance(TimeSpan.FromMilliseconds(640));
        Controller.ApplyPending(); // the next cycle is due, one second after the previous began
        Assert.Equal(2, Payloads(0x14).Count);
        Assert.Empty(Payloads(0x28)); // 1000 ms after the first table completed, though 1360 after it was sent
        Tick();
        Assert.Single(Payloads(0x28));
        Assert.True(_clock.UtcNow - completed.Value >= TimeSpan.FromMilliseconds(1200));
    }

    // A TL FLEX LCD group reports one fan, so its first screen's theme goes out in table slot 0.
    // Before its colours are due it reports two, and the table numbers the screens the other way
    // round, so the first screen's theme goes out in slot 1 for the first time: that is a new
    // entry, and the colours wait 1.2 s from the clock that first carried it, as
    // UpdateSensorSettingByWiredLess waits after the clock that carries the entry the colours are
    // for. The first round then goes to slot 1.
    [Fact]
    public void AGroupWhoseCountChangesBeforeItsColours_WaitsForTheClockThatCarriesItsNewEntry() {
        FakeWirelessRecord group = LookDevice(pump: false, fans: 1);
        _rig.Records.Add(group);
        Build();
        Tick();
        Assert.Equal(B(1, 0, 0, 0), Payloads(0x14).Single().Payload[64..68]); // slot 0: the first screen's theme
        group.FanCountByte = 2;
        group.FanTypes[1] = 51;
        Tick(); // the read learns the second screen
        ClearWrites();
        Tick(); // 1.2 s past the first clock, but the entry it carried is not the one the group needs now

        Assert.Equal(B(2, 1, 0, 0), Payloads(0x14).Single().Payload[64..68]); // slot 1 now the first screen's, slot 0 the second's
        Assert.Empty(Payloads(0x28));
        Assert.Empty(Payloads(0x29));
        Tick();
        Assert.Empty(Payloads(0x28));
        Tick();

        byte[] first = Assert.Single(Payloads(0x28)).Payload;
        Assert.Equal(1, first[17]);
        Assert.Equal(new byte[19], first[20..39]); // slot 0 empty
        Assert.Equal(_configuration.FanScreens[MacText(1)].ColoursOf(0).ToBytes(), first[39..57]); // the first screen's colours in slot 1
        Assert.Equal(1, first[57]);
    }

    // A group reports a third fan between its first screen's round and its second's: every screen
    // is renumbered, so the entry the second screen needs first goes out in this cycle's clock, and
    // its round, which would otherwise begin in the pass the first's ends, waits 1.2 s from that
    // clock, with the theme switch behind it. The round then takes the slot the table numbers the
    // second screen with now.
    [Fact]
    public void AGroupWhoseCountChangesBetweenTwoScreens_HasTheNextScreenWaitForTheClockThatCarriesItsNewEntry() {
        FakeWirelessRecord group = LookDevice(pump: false, fans: 2);
        _rig.Records.Add(group);
        Build();
        WaitColoursInterval(pump: false);
        Tick(); // the first screen's round, in slot 1
        Assert.Equal(1, Assert.Single(Payloads(0x28)).Payload[17]);
        group.FanCountByte = 3;
        group.FanTypes[2] = 51;
        group.Sequence = 1;
        Tick(); // the second send; the read hears the acknowledgement and the third screen
        ClearWrites();
        Tick(); // the round ends; the clock first carries the three-screen entry, and the second screen's round waits

        Assert.NotEmpty(Payloads(0x14));
        Assert.Empty(Payloads(0x28));
        Assert.Empty(Payloads(0x29));
        Tick();
        Assert.Empty(Payloads(0x28));
        Tick();

        byte[] second = Assert.Single(Payloads(0x28)).Payload;
        Assert.Equal(2, second[17]);
        Assert.Equal(_configuration.FanScreens[MacText(1)].ColoursOf(1).ToBytes(), second[39..57]); // the second screen, numbered 1 of three
        Assert.Empty(Payloads(0x29));
    }

    // A group whose colours are done reports a fan more: its new entry goes out in the clock, and
    // nothing else, since the colours go to a group once per process and the record says nothing
    // about them, as L-Connect applies nothing for a group it already knows when its count changes.
    [Fact]
    public void AGroupWhoseCountChangesAfterItsColours_IsNotColouredAgain() {
        FakeWirelessRecord group = LookDevice(pump: false, fans: 1);
        _rig.Records.Add(group);
        Build();
        WaitColoursInterval(pump: false);
        Tick();
        group.Sequence = 1;
        Tick();
        Tick();
        Assert.True(_processState.AreScreensColoured(MacText(1)));

        group.FanCountByte = 2;
        group.FanTypes[1] = 51;
        ClearWrites();
        for (int second = 0; second < 4; second++) {
            Tick();
        }

        Assert.Equal(B(2, 1, 0, 0), Payloads(0x14).Last().Payload[64..68]);
        Assert.Empty(Payloads(0x28));
        Assert.True(_processState.AreScreensColoured(MacText(1)));
    }

    // An entry the clock stops carrying and later carries again: the group names no master for a
    // while, or reports a receiver slot the table has no entry for, no LCD fan in its first slot,
    // or no fan at all. The clock that completes without the entry withdraws its publication
    // (RFController's fresh table carries nothing for it either), so the colours wait their 1.2 s
    // from the clock that restores the entry, not from the one that first carried it before the
    // gap. The group never left the table, so its commands are not started over.
    [Theory]
    [InlineData(0)] // names no master
    [InlineData(1)] // a slot the table has no entry for
    [InlineData(2)] // no LCD fan in its first slot
    [InlineData(3)] // no fan at all
    public void AGroupsEntryCarriedAgain_AfterAClockWithoutIt_HasItsColoursWaitFromTheClockThatRestoresIt(int absence) {
        FakeWirelessRecord group = LookDevice(pump: false);
        _rig.Records.Add(group);
        Build();
        Tick(); // the entry goes out; the colours would be due two ticks on
        switch (absence) {
            case 0:
                group.Master = new byte[6];
                break;
            case 1:
                group.Receiver = 14;
                break;
            case 2:
                group.FanCountByte = 0;
                group.FanTypes[0] = 0;
                break;
            default:
                group.FanCountByte = 0;
                break;
        }

        Tick(); // the read hears the change
        Tick(); // the clock goes out without the entry, on the tick the colours were due
        Assert.Equal(new byte[12], Payloads(0x14).Last().Payload[64..76]);
        Assert.Empty(Payloads(0x28));
        if (absence == 1) {
            // Off the table's slots the switch is not held behind colours that are not owed, as
            // TryRestoreWirelessThemeSwitches runs though UpdateSensorSettingByWiredLess refused
            // the slot: acknowledged and confirmed here, so the colours' turn comes as they are due.
            Assert.Single(Payloads(0x29));
            group.Sequence = 1;
            group.RpmHighNibbles = B(0, 0x4, 0, 0);
        } else {
            Assert.Empty(Payloads(0x29));
        }

        group.Master = Master;
        group.Receiver = 1;
        group.FanCountByte = 1;
        group.FanTypes[0] = 51;
        Tick(); // the read hears the group as before
        ClearWrites();
        Tick(); // the clock carries the entry again
        Assert.Equal(B(1, 0, 0, 0), Payloads(0x14).Single().Payload[64..68]);
        Assert.Empty(Payloads(0x28));
        Tick(); // about 1000 ms after it
        Assert.Empty(Payloads(0x28));
        Tick();

        Assert.Equal(absence == 1 ? 2 : 1, Assert.Single(Payloads(0x28)).Payload[17]);
        Assert.DoesNotContain(_log.Messages, m => m.Contains("started over"));
    }

    // Two groups reporting one receiver slot, which the plugin leaves alone: UpdateSensorDataByWiredLess
    // writes the later group's entry over the earlier's, so the slot's bytes that go out are the
    // later group's. L-Connect colours both groups all the same; the plugin colours only the group
    // whose own entry went out, and holds nothing for the other meanwhile, so its theme switch goes
    // out as for a group with no colours owed. Once the slot is its own again (the other group
    // re-paired away here) its entry goes out, and its colours follow the clock that first carries
    // it by 1.2 s.
    [Fact]
    public void TwoGroupsOnOneReceiverSlot_OnlyTheOneWhoseEntryWentOut_IsColoured_AndTheOthersThemeSwitchIsNotHeld() {
        _configuration.FanScreens[MacText(1)] = new WirelessFanScreenPresentation(B(1, 1, 1, 1), B(0, 0, 0, 0), 60, 0, false);
        _configuration.FanScreens[MacText(2)] = new WirelessFanScreenPresentation(B(6, 6, 6, 6), B(0, 0, 0, 0), 60, 0, false);
        FakeWirelessRecord earlier = Group(1, fans: 1, fanType: 51, receiver: 1);
        FakeWirelessRecord later = Group(2, fans: 1, fanType: 51, receiver: 1);
        _rig.Records.Add(earlier);
        _rig.Records.Add(later);
        Build();

        Tick();
        Assert.Equal(B(6, 0, 0, 0), Payloads(0x14).Single().Payload[64..68]); // the later group's theme on the shared slot
        Assert.Equal(Mac(1), Assert.Single(Payloads(0x29)).Payload[2..8]); // the earlier group's switch, not held behind colours it is not owed
        earlier.Sequence = 1;
        earlier.RpmHighNibbles = B(0, 0x4, 0, 0);
        Tick(); // the switch's second send; the read hears its acknowledgement and the screen on its theme
        Tick(); // the switch's round ends; the later group's colours are due
        Assert.Equal(Mac(2), Assert.Single(Payloads(0x28)).Payload[2..8]);
        later.Sequence = 1;
        Tick(); // the earlier group's switch is confirmed; the read hears the later group's acknowledgement
        Tick(); // the later group's colours are done

        Assert.True(_processState.AreScreensColoured(MacText(2)));
        Assert.False(_processState.AreScreensColoured(MacText(1)));
        Assert.Contains("W4:a00000000001 screens all show their wireless themes again (switches 0x4); asked every device to save", _log.Messages);
        Assert.All(Payloads(0x14), p => Assert.Equal(6, p.Payload[64]));
        Assert.All(Payloads(0x28), p => Assert.Equal(2, p.Payload[7]));

        later.Master = new byte[6]; // re-paired away: the slot is the earlier group's own
        Tick(); // the read hears it
        ClearWrites();
        Tick(); // the clock carries the earlier group's entry for the first time
        Assert.Equal(B(1, 0, 0, 0), Payloads(0x14).Single().Payload[64..68]);
        Assert.Empty(Payloads(0x28));
        Tick();
        Assert.Empty(Payloads(0x28));
        Tick();

        byte[] first = Assert.Single(Payloads(0x28)).Payload;
        Assert.Equal(Mac(1), first[2..8]);
        Assert.Equal(2, first[17]); // under the earlier group's next sequence, after its switch's round
        Assert.Equal(_configuration.FanScreens[MacText(1)].ColoursOf(0).ToBytes(), first[20..38]);
    }

    // Two groups on one slot that want the same entry: the bytes that go out are each one's own,
    // so the same clock publishes both and both are coloured, each under its own sequence.
    [Fact]
    public void TwoGroupsOnOneReceiverSlot_WithTheSameEntry_AreBothColoured() {
        _configuration.FanScreens[MacText(1)] = new WirelessFanScreenPresentation(B(1, 2, 3, 4), B(0, 0, 0, 0), 60, 0, false);
        _configuration.FanScreens[MacText(2)] = new WirelessFanScreenPresentation(B(1, 2, 3, 4), B(0, 0, 0, 0), 60, 0, false);
        _rig.Records.Add(Group(1, fans: 1, fanType: 51, receiver: 1));
        _rig.Records.Add(Group(2, fans: 1, fanType: 51, receiver: 1));
        Build();
        WaitColoursInterval(pump: false);

        Tick();

        Assert.Equal(new[] { 1, 2 }, Payloads(0x28).Select(p => (int)p.Payload[7]));
        Assert.All(Payloads(0x28), p => Assert.Equal(1, p.Payload[17]));
        Assert.Empty(Payloads(0x29));
    }

    // UpdateSensorSettingByWiredLess keeps the service's fan index for an SL-Infinity or SL-INF
    // FLEX group whose end cap is on the left (isINFRightAttach false), so the first screen's
    // colours go in slot 0 of the buffer , not the last slot.
    [Fact]
    public void TheScreenColours_KeepTheServicesOrder_ForALeftAttachedSlInfFlexGroup() {
        _configuration.FanScreens[MacText(1)] = new WirelessFanScreenPresentation(B(6, 6, 6, 6), B(0, 0, 0, 0), 60, 1, false);
        _rig.Records.Add(Group(1, fans: 2, fanType: 43, receiver: 3));
        Build();
        WaitColoursInterval(pump: false);

        Tick();

        byte[] defaults = B(0x99, 0x2E, 0x63, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 1); // theme 6's own colours; marker 1
        Assert.Equal(
            Expected(240, (0, B(0x12, 0x28)), (2, Mac(1)), (8, Master), (14, B(3, 8, 0, 1)), (20, defaults)),
            Assert.Single(Payloads(0x28)).Payload);
    }

    // A screen that never acknowledges its colours gets ten sends, is logged, and the next
    // screen's round follows.
    [Fact]
    public void TheScreenColours_AnUnacknowledgedScreen_GetsTenSends_ThenTheNextScreens() {
        _configuration.FanScreens[MacText(1)] = new WirelessFanScreenPresentation(B(1, 2, 3, 4), B(0, 0, 0, 0), 60, 1, false);
        _rig.Records.Add(Group(1, fans: 2, fanType: 51));
        Build();
        WaitColoursInterval(pump: false);

        for (int second = 0; second < 12; second++) {
            Tick();
        }

        Assert.Equal(new byte[] { 1, 2 }, Payloads(0x28).Select(p => p.Payload[17]).Distinct());
        Assert.Equal(10, Payloads(0x28).Count(p => p.Payload[17] == 1));
        Assert.Contains("W4:a00000000001 did not acknowledge the colours of its screen 1's wireless theme in 10 sends; the next screen's follow", _log.Messages);
    }

    // A round's later sends carry what the round began with (the screen's slot and colours, the
    // switch mask, the sequence), as L-Connect's sendColors and WiredlessThemeSwitchTarget are fixed
    // when the command is queued, and take the route and the bound count as they are at each send,
    // as SendRfData and SyncControlInfo's b are recomputed every pass; repeated calls within the
    // second add no send.
    [Theory]
    [InlineData(0x19)]
    [InlineData(0x27)]
    [InlineData(0x28)]
    [InlineData(0x29)]
    public void ACommandsLaterSends_KeepTheRoundsBytes_AndTakeTheLiveRouteAndCount(int command) {
        _configuration.Effects[MacText(1)] = Effect(B(1, 2, 3, 4));
        FakeWirelessRecord first = Group(1);
        if (command == 0x28) {
            first.EffectIndex = B(1, 2, 3, 4); // running its effect while the colours' interval passes, so it is still counted at the first send
        }

        _rig.Records.Add(first);
        FakeWirelessRecord target = command == 0x19 ? Device(2, 10, receiver: 3) : Group(2, fans: 2, fanType: 51, receiver: 3);
        _rig.Records.Add(target);
        if (command == 0x27) {
            _configuration.MotherboardArgbSync.Add(MacText(2));
        }

        if (command == 0x28 || command == 0x29) {
            _configuration.FanScreens[MacText(2)] = new WirelessFanScreenPresentation(B(6, 6, 6, 6), B(0, 0, 0, 0), 60, 1, false);
        }

        if (command == 0x29) {
            _processState.MarkScreensColoured(MacText(2), 0); // coloured earlier in this process: the switch goes first
        }

        Build();
        if (command == 0x19) {
            Controller.SetTarget(ControlIndex("LianLi/w" + MacText(2) + "/pump/ctl"), 50);
        }

        if (command == 0x28) {
            WaitColoursInterval(pump: false);
            first.EffectIndex = new byte[4]; // lost its effect: streamed, and changing effect, from the read after the first send
        }

        Tick();
        byte[] expected = Expected(240, (0, B(0x12, command)), (2, Mac(2)), (8, Master), (14, B(3, 8, 1, 1)));
        if (command == 0x27) {
            expected[20] = 1;
        } else if (command == 0x29) {
            expected[20] = 6;
        } else if (command == 0x28) {
            byte[] colours = B(0x99, 0x2E, 0x63, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 1);
            Array.Copy(colours, 0, expected, 39, colours.Length);
        }

        Assert.Equal(expected, Assert.Single(Payloads(command)).Payload);
        target.Channel = 5;
        target.Receiver = 7;
        target.FanCountByte = 1;
        target.FanTypes[1] = 0;
        Tick(); // the changed route and count are read after this send
        ClearWrites();
        Tick();

        expected[16] = 0; // the group before it is changing effect now, so is not counted
        (byte[] header, byte[] payload) = Assert.Single(Payloads(command));
        Assert.Equal(B(0x10, 0, 5, 7), header);
        Assert.Equal(expected, payload);
        for (int wake = 0; wake < 10; wake++) {
            Controller.ApplyPending();
        }

        Assert.Single(Payloads(command));
    }

    // L-Connect colours the screens when its service starts, not on every refresh: a controller
    // built after a refresh does not colour them again.
    [Fact]
    public void TheScreenColours_AreSentOncePerProcess_NotAgainByTheNextController() {
        _configuration.FanScreens[MacText(1)] = new WirelessFanScreenPresentation(B(1, 2, 3, 4), B(0, 0, 0, 0), 60, 1, false);
        FakeWirelessRecord group = Group(1, fans: 1, fanType: 51);
        _rig.Records.Add(group);
        Build();
        WaitColoursInterval(pump: false);
        Tick();
        group.Sequence = 1;
        Tick();
        Tick();
        Assert.True(_processState.AreScreensColoured(MacText(1)));
        Controller.Dispose();
        ClearWrites();

        Build();
        Tick();
        Tick();

        Assert.NotEmpty(Payloads(0x14));
        Assert.Empty(Payloads(0x28));
    }

    // applyConfiguredWirelessLCDModes skips a group in advance mode, or one with nothing saved;
    // applyWirelessLCDMode returns for a group without fans, and UpdateSensorSettingByWiredLess
    // for a receiver slot the table has no entry for; a group without screens has no colours.
    [Fact]
    public void TheScreenColours_AreNotSent_InAdvanceMode_WithNothingSaved_WithoutFans_OffTheTable_OrWithoutScreens() {
        _configuration.FanScreens[MacText(1)] = new WirelessFanScreenPresentation(B(1, 2, 3, 4), B(0, 0, 0, 0), 60, 1, true);
        _configuration.FanScreens[MacText(3)] = new WirelessFanScreenPresentation(B(1, 2, 3, 4), B(0, 0, 0, 0), 60, 1, false);
        _configuration.FanScreens[MacText(4)] = new WirelessFanScreenPresentation(B(1, 2, 3, 4), B(0, 0, 0, 0), 60, 1, false);
        _configuration.FanScreens[MacText(5)] = new WirelessFanScreenPresentation(B(1, 2, 3, 4), B(0, 0, 0, 0), 60, 1, false);
        _rig.Records.Add(Group(1, fans: 2, fanType: 51, receiver: 1)); // advance mode
        _rig.Records.Add(Group(2, fans: 2, fanType: 51, receiver: 2)); // nothing saved
        _rig.Records.Add(Group(3, fans: 0, fanType: 51, receiver: 3)); // no fans yet
        _rig.Records.Add(Group(4, fans: 2, fanType: 51, receiver: 14)); // off the table
        _rig.Records.Add(Group(5, fans: 2, fanType: 53, receiver: 5)); // TL FLEX LED: no screens
        Build();

        Tick();
        Tick();

        Assert.Empty(Payloads(0x28));
    }

    // The colours share the device's one sequence with the lighting handover, and wait their turn.
    [Fact]
    public void TheScreenColours_WaitWhileTheLightingHandoverHasTheDevicesSequence() {
        _configuration.MotherboardArgbSync.Add(MacText(1));
        _configuration.FanScreens[MacText(1)] = new WirelessFanScreenPresentation(B(1, 2, 3, 4), B(0, 0, 0, 0), 60, 1, false);
        FakeWirelessRecord group = Group(1, fans: 1, fanType: 51);
        _rig.Records.Add(group);
        Build();

        Tick();
        Assert.Single(Payloads(0x27));
        Assert.Empty(Payloads(0x28));
        group.Sequence = 1;
        Tick();
        ClearWrites();

        Tick();

        Assert.Equal(2, Assert.Single(Payloads(0x28)).Payload[17]);
    }

    // A receiver that came back starts every device's commands over, but the colours, marked for
    // the process, are found sent and not begun again: L-Connect's ResumeSuspend restores the
    // lighting handover and the effects, never the colours, and its service rebuilds nothing for a
    // receiver. The theme switch, which the record's bits still call for, is begun again under the
    // device's next sequence.
    [Fact]
    public void AReceiverReconnect_LeavesTheScreensColoured_AndBeginsTheirThemeSwitchAgain() {
        _configuration.FanScreens[MacText(1)] = new WirelessFanScreenPresentation(B(1, 2, 3, 4), B(0, 0, 0, 0), 60, 1, false);
        FakeWirelessRecord group = Group(1, fans: 1, fanType: 51);
        _rig.Records.Add(group);
        Build();
        WaitColoursInterval(pump: false);
        Tick();
        group.Sequence = 1;
        Tick();
        Tick();
        Assert.True(_processState.AreScreensColoured(MacText(1)));
        Assert.Equal(2, Assert.Single(Payloads(0x29)).Payload[17]); // the theme switch begun after the colours took the next sequence
        ClearWrites();

        _rig.Receiver.Generation = 1;
        Tick();

        Assert.Contains("W4 reconnected (transmitter generation 0, receiver generation 1)", _log.Messages);
        Assert.True(_processState.AreScreensColoured(MacText(1)));
        Assert.Empty(Payloads(0x28));
        Assert.Equal(3, Assert.Single(Payloads(0x29)).Payload[17]); // a fresh round of the switch, under the next sequence again
    }

    // A transmitter that came back starts every device's commands over, the colours included,
    // since its loss dropped their mark: they are sent again first, 1.2 s after the clock the
    // reconnect sent carried the table again, under the next sequence, and the theme switch waits
    // its turn behind them as at a start.
    [Fact]
    public void ATransmitterReconnect_ColoursTheScreensAgain_ThenBeginsTheirThemeSwitch() {
        _configuration.FanScreens[MacText(1)] = new WirelessFanScreenPresentation(B(1, 2, 3, 4), B(0, 0, 0, 0), 60, 1, false);
        FakeWirelessRecord group = Group(1, fans: 1, fanType: 51);
        _rig.Records.Add(group);
        Build();
        WaitColoursInterval(pump: false);
        Tick();
        group.Sequence = 1;
        Tick();
        Tick();
        Assert.True(_processState.AreScreensColoured(MacText(1)));
        ClearWrites();

        _rig.Transmitter.Generation = 1;
        Tick();

        Assert.Contains("W4 reconnected (transmitter generation 1, receiver generation 0)", _log.Messages);
        Assert.False(_processState.AreScreensColoured(MacText(1)));
        Assert.NotEmpty(Payloads(0x14));
        Assert.Empty(Payloads(0x28)); // the interval runs again from this clock
        Assert.Empty(Payloads(0x29)); // and the switch waits behind the colours
        Tick();
        Assert.Empty(Payloads(0x28));
        Tick();
        Assert.Equal(3, Assert.Single(Payloads(0x28)).Payload[17]);
        Assert.Empty(Payloads(0x29));
        group.Sequence = 3;
        Tick();
        Tick();

        Assert.True(_processState.AreScreensColoured(MacText(1)));
        Assert.Equal(4, Assert.Single(Payloads(0x29)).Payload[17]);
    }

    // A round, once begun, is the device's to finish, whatever its command's caller decides next: a
    // group that reports one fan fewer while its second screen's colours are being sent has that
    // round sent to its end (the count now equals the screens coloured, so the colouring is done
    // here without ever looking at the round again) and the sequence freed for the theme switch
    // behind it, instead of held indefinitely by a round this pass would never see.
    [Fact]
    public void TheScreenColours_AFanRemovedDuringItsScreensRound_DoesNotHoldTheThemeSwitchForever() {
        _configuration.FanScreens[MacText(1)] = new WirelessFanScreenPresentation(B(1, 2, 3, 4), B(0, 0, 0, 0), 60, 1, false);
        FakeWirelessRecord group = Group(1, fans: 2, fanType: 51);
        _rig.Records.Add(group);
        Build();
        WaitColoursInterval(pump: false);
        Tick();
        group.Sequence = 1;
        Tick();
        Tick();
        Assert.Contains(Payloads(0x28), p => p.Payload[17] == 2);
        Assert.Empty(Payloads(0x29));

        group.FanCountByte = 1;
        group.FanTypes[1] = 0;
        group.Sequence = 2;
        Tick(); // the round's second send goes out; then the read hears one fan, and the acknowledgement
        ClearWrites();
        Tick(); // the device ends the round; the colouring is done for the one screen; the switch takes the next sequence

        Assert.Empty(Payloads(0x28));
        Assert.Equal(3, Assert.Single(Payloads(0x29)).Payload[17]);
        Assert.Contains("W4:a00000000001 sent its 1 screen(s) the colours of their wireless themes", _log.Messages);
    }

    // Down to no fan at all: the colouring is not even looked at while the group reports none, but
    // the round in flight is still sent to its end by the device, and once a fan is back its end
    // is taken - the screen had its round - and the sequence is free for what follows.
    [Fact]
    public void TheScreenColours_AGroupReportingNoFanMidRound_HasTheRoundSentToItsEnd_AndIsDoneWhenTheFanIsBack() {
        _configuration.FanScreens[MacText(1)] = new WirelessFanScreenPresentation(B(1, 2, 3, 4), B(0, 0, 0, 0), 60, 1, false);
        FakeWirelessRecord group = Group(1, fans: 1, fanType: 51);
        _rig.Records.Add(group);
        Build();
        WaitColoursInterval(pump: false);
        Tick();
        Assert.Equal(1, Assert.Single(Payloads(0x28)).Payload[17]);

        group.FanCountByte = 0;
        group.FanTypes[0] = 0;
        Tick(); // the second send, then the read hears no fan
        ClearWrites();
        Tick();
        Assert.Equal(1, Assert.Single(Payloads(0x28)).Payload[17]); // the third send, by the device's own bookkeeping

        group.Sequence = 1;
        Tick(); // the fourth send; the read hears the acknowledgement
        ClearWrites();
        Tick(); // the device ends the round
        Assert.Empty(Payloads(0x28));

        group.FanCountByte = 1;
        group.FanTypes[0] = 51;
        Tick(); // the read hears the fan again
        Tick(); // the colouring takes the round's end: every screen has had its round, and the switch follows

        Assert.Empty(Payloads(0x28));
        Assert.Contains("W4:a00000000001 sent its 1 screen(s) the colours of their wireless themes", _log.Messages);
        Assert.Equal(2, Assert.Single(Payloads(0x29)).Payload[17]);
    }

    // A send of a round under way is isolated like the rest of a device's work, under the device's
    // command key, logged when it starts failing and when it recovers.
    [Fact]
    public void ARoundsSendThatFails_IsLoggedUnderTheDevicesCommandKey_AndCostsOnlyThatDevice() {
        _configuration.FanScreens[MacText(1)] = new WirelessFanScreenPresentation(B(1, 2, 3, 4), B(0, 0, 0, 0), 60, 1, false);
        _processState.MarkScreensColoured(MacText(1), 0);
        _rig.Records.Add(Group(1, fans: 1, fanType: 51));
        _rig.Records.Add(Group(2, fans: 1, receiver: 2));
        Build();
        Tick(); // the switch's first send, from its own pass
        Assert.Single(Payloads(0x29));

        _rig.Transmitter.FailWrite = packet => packet[0] == 0x10 && packet[5] == 0x29;
        Tick();
        Assert.Contains("W4 a00000000001/command failed: simulated write failure", _log.Messages);
        Assert.NotEmpty(Payloads(0x14)); // the rest of the second went on

        _rig.Transmitter.FailWrite = null;
        Tick();

        Assert.Contains("W4 a00000000001/command recovered", _log.Messages);
        Assert.Equal(2, Payloads(0x29).Count); // the failed send is counted against the round's ten, as L-Connect counts it, but never written
    }

    // A chain that went unbound (to its USB receiver, say) keeps its sensors here, retained, and
    // when it is bound to the master again nothing is added; but it is driven here again, so the
    // plugin is told, as it is for a new sensor: the plugin instance that registered those sensors elsewhere has to
    // move them.
    [Fact]
    public void AChainDrivenAgainWithItsSensorsRetained_RaisesTopologyChanged() {
        _rig.Records.Add(Group(1));
        Build();
        int reports = 0;
        Controller.TopologyChanged += (_, _) => reports++;
        _rig.Records.Clear();
        _rig.Records.Add(Group(1, master: new byte[6]));
        Tick();
        Assert.False(_processState.IsBoundToMaster(MacText(1)));
        Assert.Equal(0, reports);
        Assert.Empty(Controller.DrivenSensorIds);

        _rig.Records.Clear();
        _rig.Records.Add(Group(1));
        Tick();

        Assert.True(_processState.IsBoundToMaster(MacText(1)));
        Assert.Equal(1, reports);
        Assert.Equal(3, Controller.DrivenSensorIds.Count());
        Tick();
        Assert.Equal(1, reports); // driven still, nothing more to tell
    }

    // Any device but a Lancool 217 is dropped from L-Connect's list after thirty unheard reads and
    // added again as new; its sensors, held by address, keep their ids and read again.
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

    // Run calls CheckChannelConflict only while the list is not locked.
    [Fact]
    public void AMasterOnAnothersChannel_IsNotMovedWhileTheListIsLocked() {
        _configuration.LockedDevices["112233445566"] = new[] { Locked(Group(1)) };
        _rig.Records.Add(MasterRecord(new byte[] { 0, 0, 0, 0, 0, 1 }, 8));
        _rig.Records.Add(MasterRecord(Master, 8));
        _rig.Records.Add(Group(1));
        Tick();
        Tick();

        Assert.Equal(8, Controller.Channel);
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

    // A device mid-effect is not counted, but one mid-switch of its own is: SyncControlInfo counts
    // every bound device that is not changing effect, whatever command it has pending.
    [Fact]
    public void TheScreenSwitch_DoesNotCountADeviceChangingEffect_ButCountsOneSwitchingItself() {
        _configuration.Effects[MacText(1)] = Effect(B(1, 2, 3, 4));
        _rig.Records.Add(Group(1));
        _rig.Records.Add(Device(2, 10, receiver: 2));
        _rig.Records.Add(Device(3, 10, receiver: 3));
        Controller.SetTarget(ControlIndex("LianLi/w" + MacText(2) + "/pump/ctl"), 0);
        Controller.SetTarget(ControlIndex("LianLi/w" + MacText(3) + "/pump/ctl"), 0);
        Tick(); // the group is now streamed its effect, and both blocks are switching
        ClearWrites();

        Tick();

        Assert.Equal(1, Payloads(0x19).Single(p => p.Payload[7] == 3).Payload[16]);
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
    // read has given it sensors; it is sent nothing that second instead of failing.
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

    // SyncControlInfo services a pending command whether or not SendAioInfo sends the pump
    // anything: a screen switch mid-round when FanControl releases the pump control is carried to
    // its end (its acknowledgement here), so the device's one sequence is not held indefinitely and the
    // lighting handover's second round, waiting behind it, goes out.
    [Fact]
    public void TheScreenSwitch_ReleasedMidRound_IsCarriedToItsEnd_SoTheHandoverBehindItIsNotHeldForever() {
        _configuration.MotherboardArgbSync.Add(MacText(1));
        FakeWirelessRecord block = Device(1, 10);
        _rig.Records.Add(block);
        Controller.SetTarget(0, 50);
        Tick(); // the handover's first round, sequence 1
        block.Sequence = 1; // acknowledged, but the source flag stays clear: a second round is owed
        Tick();
        Tick(); // the handover's round ends; the screen switch takes sequence 2
        Assert.Equal(2, Assert.Single(Payloads(0x19)).Payload[17]);
        Controller.ReleaseChannel(0); // FanControl resets the pump control
        ClearWrites();

        Tick();
        Assert.Equal(2, Assert.Single(Payloads(0x19)).Payload[17]); // still sent, though the pump gets nothing
        Assert.Empty(Payloads(0x21));
        block.Sequence = 2;
        Tick();
        for (int second = 0; second < ConfirmationReadbackReads + 1; second++) {
            Tick();
        }

        Assert.Contains(Payloads(0x27), p => p.Payload[17] == 3); // the handover's second round, under the next sequence
        Assert.Contains("W4:a00000000001 switched its screen to its wireless theme", _log.Messages);
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
    // after a refresh does not switch it again.
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

    // ---------- the saved look across losses, faults and refreshes ----------

    // The two looks whose completion is marked for the whole process, since the record reports
    // nothing about either: a water block's screen switch (0x19, begun when its pump is set) and
    // an LCD FLEX group's screen colours (0x28, begun as the group appears). L-Connect sends each
    // when its service starts, not again for a device that drops off its table and returns or on
    // a resume, and only once more, when the transmitter is plugged in again, since its service
    // builds a new controller for it whose ApplyAll sends every device both looks.
    private FakeWirelessRecord LookDevice(bool pump, int fans = 1) {
        if (pump) {
            return Device(1, 10);
        }

        _configuration.FanScreens[MacText(1)] = new WirelessFanScreenPresentation(B(1, 2, 3, 4), B(0, 0, 0, 0), 60, 1, false);
        return Group(1, fans: fans, fanType: 51);
    }

    private static int LookCommand(bool pump) => pump ? 0x19 : 0x28;

    // What the device is sent again when it is back, in the look's place: the pump's parameter
    // block, or the group's theme switch, which its record's bits still call for.
    private static int ResentCommand(bool pump) => pump ? 0x21 : 0x29;

    private bool LookMarked(bool pump) => pump ? _processState.IsScreenSwitched(MacText(1)) : _processState.AreScreensColoured(MacText(1));

    // What it takes for the look to begin on the next tick: the pump's control is the water
    // block's first sensor, and setting it is what begins its switch; a group's colours begin on
    // their own once their interval has passed.
    private void DriveLook(bool pump) {
        if (pump) {
            Controller.SetTarget(0, 50);
        } else {
            WaitColoursInterval(pump);
        }
    }

    // A group's colours begin 1.2 s after the first clock that carried its table, so two ticks
    // on from it the next tick is the one that sends them; the pump's switch needs no wait.
    private void WaitColoursInterval(bool pump) {
        if (!pump) {
            Tick();
            Tick();
        }
    }

    // The transmitter's handle is lost, as the transport reports it: its generation moves at the
    // fault, its latch stays set and every write is refused until it is reopened.
    private static void Lose(FakeWirelessDongle transmitter) {
        transmitter.Generation++;
        transmitter.IsFaulted = true;
        transmitter.FailWrite = _ => true;
    }

    private static void Reopen(FakeWirelessDongle transmitter) {
        transmitter.IsFaulted = false;
        transmitter.FailWrite = null;
    }

    private static string TransmitterLostLine(int generation, int index = 4)
        => "W" + index + ": the transmitter was lost (generation " + generation + "); every device will be sent its screen switch or screen colours again once it is back, as L-Connect's service sends them again when the transmitter is plugged in again";

    private static string LookStartedOverLine(bool pump, int lifetime)
        => "W4:a00000000001 " + (pump ? "screen switch" : "screen colours") + " begun before the transmitter was lost (now lifetime " + lifetime + "); started over, to be sent again once the transmitter carries it";

    // A second controller of the process on dongles of its own: the one FanControl is still closing
    // after a refresh that put the pair on new USB paths, while the controller under test drives
    // the devices there. Its transmitter lost is counted for the process, and the controller under
    // test, whose dongles never fault, owes every look again for it.
    private WirelessController OtherController(FakeWirelessRig rig)
        => new WirelessController(5, rig.Transmitter, rig.Receiver, _configuration, _clock, _delay, _log, _processState);

    // Acknowledged, then the device dropped from the table (thirty reads unheard) and back after a
    // reset, with or without a refresh between: the controller that hears it again - this one, or
    // the one FanControl built meanwhile - finds the look marked and sends it no more, while the
    // rest of the device's traffic resumes.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ALookAcknowledged_IsNotSentAgain_WhenTheDeviceIsDroppedAndReturns(bool pump, bool refresh) {
        FakeWirelessRecord record = LookDevice(pump);
        _rig.Records.Add(record);
        Build();
        DriveLook(pump);
        Tick();
        record.Sequence = 1;
        Tick();
        Tick();
        Assert.True(LookMarked(pump));

        _rig.Records.Clear();
        for (int second = 0; second <= WirelessDevice.MaximumMissedReads; second++) {
            Tick();
        }

        Assert.Equal(0, Controller.DeviceCount);
        Assert.True(LookMarked(pump));
        record.Sequence = 0; // back after a reset
        _rig.Records.Add(record);
        if (refresh) {
            Controller.Dispose();
            Build();
            DriveLook(pump);
        }

        ClearWrites();
        Tick();
        Tick();

        Assert.Equal(1, Controller.DeviceCount);
        Assert.NotEmpty(Payloads(ResentCommand(pump)));
        Assert.Empty(Payloads(LookCommand(pump)));
    }

    // Lost while it stays on the table (L-Connect's locked list), the look's sends run on to their
    // end, as SyncControlInfo sends every pending command to every device on its table, and the
    // look is marked when they run out; the device heard again, by this controller or the one
    // built after a refresh, is not sent it again.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ALookWhoseSendsRunOutWhileTheDeviceIsLost_IsMarkedSent_AndNotSentAgainOnItsReturn(bool pump, bool refresh) {
        FakeWirelessRecord record = LookDevice(pump, fans: 4);
        _configuration.LockedDevices["112233445566"] = new[] { Locked(record) };
        _rig.Records.Add(record);
        Build();
        Tick();
        _rig.Records.Clear();
        if (pump) {
            // The switch is begun five reads short of the loss, so its ten sends end after it.
            for (int read = 0; read < WirelessDevice.MaximumMissedReads - 6; read++) {
                Tick();
            }

            DriveLook(pump);
        }

        // Four screens take forty sends, ten of them after the loss at the thirtieth read.
        for (int second = 0; second < 45; second++) {
            Tick();
        }

        Assert.Contains(_log.Messages, m => m.Contains("not heard for 30 list reads"));
        Assert.Equal(pump ? 10 : 40, Payloads(LookCommand(pump)).Count);
        Assert.Contains(_log.Messages, m => m.Contains("did not acknowledge"));
        Assert.True(LookMarked(pump));

        _rig.Records.Add(record);
        if (refresh) {
            Controller.Dispose();
            Build();
            DriveLook(pump);
        }

        ClearWrites();
        Tick();
        Tick();

        Assert.Equal(1, Controller.DeviceCount);
        Assert.Equal(!refresh, _log.Messages.Contains("W4:a00000000001 heard again")); // the controller built after the refresh never lost it
        Assert.NotEmpty(Payloads(ResentCommand(pump)));
        Assert.Empty(Payloads(LookCommand(pump)));
    }

    // Acknowledged and marked, then the transmitter's handle is lost and reopened by the same
    // controller: the loss drops the mark the moment it is seen, nothing is begun while the
    // transmitter cannot carry it, and the reopen sends the look again with the rest, as
    // L-Connect's new controller does when the transmitter is plugged in again. Marked again
    // under the new lifetime, the look is not repeated by the controller built after an ordinary
    // refresh.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALookAcknowledged_IsSentAgain_WhenTheTransmitterIsLostAndBack(bool pump) {
        FakeWirelessRecord record = LookDevice(pump);
        _rig.Records.Add(record);
        Build();
        DriveLook(pump);
        Tick();
        record.Sequence = 1;
        Tick();
        Tick();
        Assert.True(LookMarked(pump));
        ClearWrites();

        Lose(_rig.Transmitter);
        for (int second = 0; second < 12; second++) {
            Tick();
        }

        Assert.Contains(TransmitterLostLine(1), _log.Messages);
        Assert.False(LookMarked(pump));
        Assert.DoesNotContain(_log.Messages, m => m.Contains("did not acknowledge")); // not begun under the lost transmitter
        Assert.Empty(Payloads(LookCommand(pump)));

        Reopen(_rig.Transmitter);
        Tick();
        Assert.Contains("W4 reconnected (transmitter generation 1, receiver generation 0)", _log.Messages);
        WaitColoursInterval(pump); // measured again from the clock the reconnect sent
        byte sequence = Assert.Single(Payloads(LookCommand(pump))).Payload[17];
        Assert.False(LookMarked(pump));

        record.Sequence = sequence;
        Tick();
        Tick();
        Assert.True(LookMarked(pump));
        Assert.NotEmpty(Payloads(ResentCommand(pump))); // the pump block with the cycle; the theme switch once the colours have freed the sequence
        Assert.Equal(1, _processState.TransmitterLifetime);

        Controller.Dispose();
        Build();
        DriveLook(pump);
        ClearWrites();
        Tick();
        Tick();

        Assert.NotEmpty(Payloads(ResentCommand(pump)));
        Assert.Empty(Payloads(LookCommand(pump)));
    }

    // A look begun before the transmitter is lost has its sends run on under the loss and fail, as
    // any pending command's do, and ends exhausted; but it was begun under the lifetime the loss
    // ended, so its end is discarded and the look started over, marking nothing, and no second
    // screen's round begins under the loss, since the group's entry has not gone out under the new
    // lifetime. The reopen begins it again from its first screen with the device's other commands.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALookBegunBeforeTheTransmitterIsLost_EndsUnderTheLossUnmarked_AndIsBegunAgainAtTheReopen(bool pump) {
        FakeWirelessRecord record = LookDevice(pump, fans: 2);
        _rig.Records.Add(record);
        Build();
        DriveLook(pump);
        Tick();
        Assert.Single(Payloads(LookCommand(pump)));

        Lose(_rig.Transmitter);
        for (int second = 0; second < 22; second++) {
            Tick();
        }

        Assert.Contains(TransmitterLostLine(1), _log.Messages);
        Assert.Contains(_log.Messages, m => m.Contains("/command failed"));
        Assert.Contains(LookStartedOverLine(pump, 1), _log.Messages);
        Assert.DoesNotContain(_log.Messages, m => m.Contains("did not acknowledge"));
        Assert.False(LookMarked(pump));
        Assert.Single(Payloads(LookCommand(pump))); // one send reached the dongle before the loss; the rest were refused

        Reopen(_rig.Transmitter);
        ClearWrites();
        Tick();

        Assert.Contains("W4 reconnected (transmitter generation 1, receiver generation 0)", _log.Messages);
        WaitColoursInterval(pump);
        Assert.Single(Payloads(LookCommand(pump)));
        for (int round = 0; round < 2; round++) {
            // The round's sequence is acknowledged in the next list read, after its second send.
            record.Sequence = Payloads(LookCommand(pump)).Last().Payload[17];
            Tick();
            Tick();
        }

        Assert.Equal(pump ? 2 : 4, Payloads(LookCommand(pump)).Count); // one round of the switch, or a round per screen from the first screen again
        Assert.True(LookMarked(pump));
        Assert.NotEmpty(Payloads(ResentCommand(pump)));
    }

    // The old controller sees the transmitter lost and is closed before its backoff reopens it;
    // the controller built over fresh dongles finds every mark dropped, and sends the look again.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALookMarkedBeforeTheTransmitterIsLost_IsSentAgain_ByTheControllerBuiltAfterARefresh(bool pump) {
        FakeWirelessRecord record = LookDevice(pump);
        _rig.Records.Add(record);
        Build();
        DriveLook(pump);
        Tick();
        record.Sequence = 1;
        Tick();
        Tick();
        Assert.True(LookMarked(pump));

        Lose(_rig.Transmitter);
        Tick();
        Assert.Contains(_log.Messages, m => m.Contains("master failed"));
        Assert.Contains(TransmitterLostLine(1), _log.Messages);
        Assert.DoesNotContain(_log.Messages, m => m.Contains("reconnected"));
        Assert.False(LookMarked(pump));
        Controller.Dispose();

        var returned = new FakeWirelessRig();
        record.Sequence = 0; // back after a reset
        returned.Records.Add(record);
        _controller = new WirelessController(4, returned.Transmitter, returned.Receiver, _configuration, _clock, _delay, _log, _processState);
        DriveLook(pump);
        Tick();
        Tick();

        Assert.Equal(1, Controller.DeviceCount);
        Assert.Contains(returned.Payloads(), p => p.Payload[1] == LookCommand(pump));
        record.Sequence = returned.Payloads().Last(p => p.Payload[1] == LookCommand(pump)).Payload[17];
        Tick();
        Tick();
        Assert.True(LookMarked(pump));
        Assert.Contains(returned.Payloads(), p => p.Payload[1] == ResentCommand(pump));
    }

    // The transmitter is lost on the last tick before the refresh, in a write of the list read's
    // tick that no ApplyPending follows: the closing controller counts the loss as it closes, so
    // the next one still finds the marks dropped.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATransmitterLostOnTheTickBeforeARefresh_IsCountedByTheClosingController(bool pump) {
        FakeWirelessRecord record = LookDevice(pump);
        _rig.Records.Add(record);
        Build();
        DriveLook(pump);
        Tick();
        record.Sequence = 1;
        Tick();
        Tick();
        Assert.True(LookMarked(pump));

        // The transport moves its generation inside the failing write, as the real one does; here
        // that write is the one after the cycle, so the tick ends with the loss uncounted.
        _clock.Advance(Second);
        Controller.ApplyPending();
        Lose(_rig.Transmitter);
        Controller.PollRpm();
        Assert.DoesNotContain(TransmitterLostLine(1), _log.Messages);
        Assert.True(LookMarked(pump));

        Controller.Dispose();

        Assert.Contains(TransmitterLostLine(1), _log.Messages);
        Assert.False(LookMarked(pump));
    }

    // The transmitter is lost inside the closing save itself, the last write the controller makes:
    // counted before the dongles are let go, so the next controller sends the look again.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATransmitterLostOnTheClosingSave_LeavesTheLookOwed_ToTheNextController(bool pump) {
        _configuration.Effects[MacText(1)] = Effect(B(1, 2, 3, 4));
        FakeWirelessRecord record = LookDevice(pump);
        _rig.Records.Add(record);
        Build();
        DriveLook(pump);
        Tick(); // streams the effect; the debounced save is ten seconds away
        record.Sequence = 1;
        Tick();
        Tick();
        Assert.True(LookMarked(pump));

        _rig.Transmitter.FailWrite = packet => {
            Lose(_rig.Transmitter);
            return true;
        };
        Controller.Dispose();

        Assert.Contains(_log.Messages, m => m.StartsWith("W4 save failed", StringComparison.Ordinal));
        Assert.Contains(TransmitterLostLine(1), _log.Messages);
        Assert.False(LookMarked(pump));

        var returned = new FakeWirelessRig();
        record.Sequence = 0; // back after a reset
        returned.Records.Add(record);
        _controller = new WirelessController(4, returned.Transmitter, returned.Receiver, _configuration, _clock, _delay, _log, _processState);
        DriveLook(pump);
        Tick();
        Tick();

        Assert.Contains(returned.Payloads(), p => p.Payload[1] == LookCommand(pump));
    }

    // The transmitter is lost during the next controller's discovery, before it knows a single
    // device: counted at the end of the discovery, so the look is owed to every device the
    // controller goes on to drive, and begun once the transmitter is back.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATransmitterLostDuringDiscovery_LeavesEveryLookOwed(bool pump) {
        FakeWirelessRecord record = LookDevice(pump);
        _rig.Records.Add(record);
        Build();
        DriveLook(pump);
        Tick();
        record.Sequence = 1;
        Tick();
        Tick();
        Assert.True(LookMarked(pump));
        Controller.Dispose();

        var returned = new FakeWirelessRig();
        record.Sequence = 0; // back after a reset
        returned.Records.Add(record);
        returned.Transmitter.FailWrite = packet => {
            Lose(returned.Transmitter);
            return true;
        };
        _controller = new WirelessController(4, returned.Transmitter, returned.Receiver, _configuration, _clock, _delay, _log, _processState);

        Assert.Contains("W4: the transmitter has not reported its master yet; asking again every second", _log.Messages);
        Assert.Contains(TransmitterLostLine(1), _log.Messages);
        Assert.False(LookMarked(pump));

        Reopen(returned.Transmitter);
        Tick();
        Tick();
        DriveLook(pump);
        Tick();
        Tick();

        Assert.DoesNotContain(_log.Messages, m => m.Contains("reconnected")); // the loss came before the controller set anything up
        Assert.Equal(1, Controller.DeviceCount);
        Assert.Contains(returned.Payloads(), p => p.Payload[1] == LookCommand(pump));
    }

    // A look not yet begun when the transmitter is lost - a pump set, or a group heard, while the
    // transmitter's handle is faulted - is not begun until the transmitter is back: nothing written
    // then reaches the air, ten refused sends would mark it sent for the new lifetime, and
    // L-Connect has no controller at all while its transmitter is off the bus. The device's other
    // commands are not held back, and the look begins at the reopen.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALookNotYetBegun_WaitsWhileTheTransmitterIsLost_AndBeginsOnceItIsBack(bool pump) {
        FakeWirelessRecord record = LookDevice(pump);
        if (pump) {
            _rig.Records.Add(record);
        }

        Build();
        Lose(_rig.Transmitter);
        Tick();
        Assert.Contains(TransmitterLostLine(1), _log.Messages);
        if (!pump) {
            _rig.Records.Add(record); // a group heard for the first time under the loss; the block was there already, its pump not yet set
        }

        Tick();
        DriveLook(pump);
        for (int second = 0; second < 12; second++) {
            Tick();
        }

        Assert.Equal(1, Controller.DeviceCount);

        // The pump block, or the clock, is tried as any command; a group's theme switch waits
        // behind its colours.
        Assert.Contains(_log.Messages, m => m.Contains(pump ? "/pump failed" : "clock failed"));
        Assert.DoesNotContain(_log.Messages, m => m.Contains("did not acknowledge the " + (pump ? "switch" : "colours")));
        Assert.Empty(Payloads(LookCommand(pump)));
        if (!pump) {
            Assert.Empty(Payloads(0x29)); // the theme switch waits behind the colours, as it follows them in applyWirelessLCDMode
        }

        Assert.False(LookMarked(pump));

        Reopen(_rig.Transmitter);
        ClearWrites();
        Tick();

        Assert.Contains("W4 reconnected (transmitter generation 1, receiver generation 0)", _log.Messages);
        WaitColoursInterval(pump);
        Assert.Single(Payloads(LookCommand(pump)));
    }

    // A group whose table went out in the clock and whose colours' interval then runs out while the
    // transmitter is lost: the colours are due, and still not begun until the transmitter is back,
    // from the clock the reconnect sends.
    [Fact]
    public void TheColoursDueUnderALostTransmitter_WaitForItToBeBack() {
        FakeWirelessRecord record = LookDevice(pump: false);
        _rig.Records.Add(record);
        Build();
        Tick(); // the table goes out; the colours are due 1.2 s on
        Assert.NotEmpty(Payloads(0x14));

        Lose(_rig.Transmitter);
        for (int second = 0; second < 12; second++) {
            Tick();
        }

        Assert.Contains(TransmitterLostLine(1), _log.Messages);
        Assert.Empty(Payloads(0x28));
        Assert.Empty(Payloads(0x29));
        Assert.False(_processState.AreScreensColoured(MacText(1)));

        Reopen(_rig.Transmitter);
        ClearWrites();
        Tick();
        Assert.Contains("W4 reconnected (transmitter generation 1, receiver generation 0)", _log.Messages);
        Assert.Empty(Payloads(0x28));
        WaitColoursInterval(pump: false);

        Assert.Single(Payloads(0x28));
    }

    // FanControl's refresh can put the dongles on new USB paths: the controller built over them
    // drives the devices while the old one, still closing, sees its transmitter's handle lost and
    // counts it for the process. The new controller's dongles never faulted, but its look, done
    // under the lifetime before, is owed again all the same. The switch goes out at once; the
    // colours wait for the next clock to carry the group's entry under the new lifetime, and 1.2 s
    // more, with the theme switch behind them. Marked again under the new lifetime, the look is
    // not repeated.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALookDone_IsOwedAgain_WhenAnotherControllerCountsTheTransmitterLost(bool pump) {
        var otherRig = new FakeWirelessRig();
        using WirelessController other = OtherController(otherRig);
        FakeWirelessRecord record = LookDevice(pump);
        record.RpmHighNibbles = B(0, 0x4, 0, 0); // a group's one screen shows its theme: no switch to take the sequence after the colours
        _rig.Records.Add(record);
        Build();
        DriveLook(pump);
        Tick();
        record.Sequence = 1;
        Tick();
        Tick();
        Assert.True(LookMarked(pump));
        ClearWrites();

        Lose(otherRig.Transmitter);
        other.Dispose();
        Assert.Contains(TransmitterLostLine(1, index: 5), _log.Messages);
        Assert.Equal(1, _processState.TransmitterLifetime);
        Assert.False(LookMarked(pump));

        Tick();
        Assert.Contains(LookStartedOverLine(pump, 1), _log.Messages);
        Assert.DoesNotContain(_log.Messages, m => m.Contains("reconnected"));
        if (!pump) {
            Assert.NotEmpty(Payloads(0x14)); // the group's entry goes out again, under the new lifetime
            Assert.Empty(Payloads(0x28));
            Tick(); // about 1000 ms after it
            Assert.Empty(Payloads(0x28));
            Assert.Empty(Payloads(0x29));
            Tick();
        }

        Assert.Equal(2, Assert.Single(Payloads(LookCommand(pump))).Payload[17]); // under the device's next sequence
        record.Sequence = 2;
        Tick();
        Tick();
        Assert.True(LookMarked(pump));
        Assert.Equal(1, _processState.TransmitterLifetime);
        if (pump) {
            Assert.NotEmpty(Payloads(0x21));
        } else {
            Assert.Empty(Payloads(0x29));
        }

        ClearWrites();
        Tick();
        Tick();
        Assert.Empty(Payloads(LookCommand(pump)));
    }

    // The same loss counted while this controller's look is in flight: the round is the device's
    // to end, and its sends go on until the device acknowledges it; but it was begun under the
    // lifetime the loss ended, so its end marks nothing, and the look is started over from its
    // first screen under the new lifetime and the device's next sequence.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALookInFlight_IsStartedOver_WhenAnotherControllerCountsTheTransmitterLost(bool pump) {
        var otherRig = new FakeWirelessRig();
        using WirelessController other = OtherController(otherRig);
        FakeWirelessRecord record = LookDevice(pump, fans: 2);
        _rig.Records.Add(record);
        Build();
        DriveLook(pump);
        Tick();
        Assert.Single(Payloads(LookCommand(pump)));

        Lose(otherRig.Transmitter);
        other.Dispose();
        Tick(); // the round's second send, under the loss counted elsewhere
        Assert.Equal(2, Payloads(LookCommand(pump)).Count);
        Assert.DoesNotContain(LookStartedOverLine(pump, 1), _log.Messages); // the round is the device's to end first
        record.Sequence = 1;
        Tick(); // the third send; the read hears the acknowledgement
        Tick(); // the round ends acknowledged; its end is discarded and the look begun again

        Assert.Contains(LookStartedOverLine(pump, 1), _log.Messages);
        Assert.DoesNotContain(_log.Messages, m => m.Contains(pump ? "switched its screen" : "did not acknowledge"));
        Assert.False(LookMarked(pump));
        List<(byte[] Header, byte[] Payload)> sent = Payloads(LookCommand(pump));
        Assert.Equal(new byte[] { 1, 1, 1, 2 }, sent.Select(p => p.Payload[17]));
        if (!pump) {
            Assert.Equal(_configuration.FanScreens[MacText(1)].ColoursOf(0).ToBytes(), sent[3].Payload[39..57]); // the first screen again, in slot 1
        }

        for (int round = 0; round < 2; round++) {
            record.Sequence = Payloads(LookCommand(pump)).Last().Payload[17];
            Tick();
            Tick();
        }

        Assert.True(LookMarked(pump));
        Assert.Equal(1, _processState.TransmitterLifetime);
        Assert.NotEmpty(Payloads(ResentCommand(pump)));
    }

    // The transmitter lifetime moves while a group's colours wait for their interval: the entry
    // went out under the lifetime before, so the wait runs again from the next clock, which
    // carries it under the new one, and the theme switch waits behind the colours throughout.
    [Fact]
    public void TheColoursWait_RunsAgainFromTheNextClock_WhenTheTransmitterLifetimeMovesMidWait() {
        var otherRig = new FakeWirelessRig();
        using WirelessController other = OtherController(otherRig);
        FakeWirelessRecord group = LookDevice(pump: false);
        _rig.Records.Add(group);
        Build();
        Tick(); // the entry goes out; the colours would be due two ticks on

        Lose(otherRig.Transmitter);
        other.Dispose();
        Tick(); // the entry goes out again, under the new lifetime
        Tick(); // the tick the colours were due on

        Assert.Empty(Payloads(0x28));
        Assert.Empty(Payloads(0x29));
        Tick();
        Assert.Single(Payloads(0x28));
        Assert.Empty(Payloads(0x29));
    }

    // Another controller of the process counts its transmitter lost at the very moment this one
    // has judged a group's colours due, between the due check and the round's first send: the
    // round is stamped with the lifetime its readiness was judged under, so its acknowledgement
    // marks nothing and the look is started over under the new lifetime. The loss is counted
    // inside the clock read of the due check.
    [Fact]
    public void ATransmitterLossCountedAsTheColoursAreJudgedDue_LeavesTheRoundUnderTheOldLifetime_AndTheColoursOwedAgain() {
        var otherRig = new FakeWirelessRig();
        using WirelessController other = OtherController(otherRig);
        FakeWirelessRecord group = LookDevice(pump: false);
        _rig.Records.Add(group);
        var clock = new ClockWithHook(_clock);
        _controller = new WirelessController(4, _rig.Transmitter, _rig.Receiver, _configuration, clock, _delay, _log, _processState);
        Tick();
        Tick();
        bool inClock = false;
        _rig.Transmitter.FailWrite = packet => {
            if (packet[0] == 0x10 && packet[1] == 0) {
                inClock = packet[5] == 0x14;
            }

            if (inClock && packet[1] == 3) {
                // The first read after the broadcast's last packet completes it; the second is the due check's.
                clock.RunOnRead(2, () => {
                    Lose(otherRig.Transmitter);
                    other.Dispose();
                });
            }

            return false;
        };
        Tick(); // the colours' first round, judged due under lifetime 0 and stamped with it
        _rig.Transmitter.FailWrite = null;

        Assert.Contains(TransmitterLostLine(1, index: 5), _log.Messages);
        Assert.Equal(1, _processState.TransmitterLifetime);
        Assert.Equal(1, Assert.Single(Payloads(0x28)).Payload[17]);
        group.Sequence = 1;
        Tick(); // the second send; the clock carries the entry under lifetime 1; the read hears the acknowledgement
        Tick(); // the round ends acknowledged under a lifetime that has passed: its end is discarded

        Assert.False(_processState.AreScreensColoured(MacText(1)));
        Assert.Contains(LookStartedOverLine(pump: false, 1), _log.Messages);
        Assert.Equal(2, Payloads(0x28).Count);
        Assert.All(Payloads(0x28), p => Assert.Equal(1, p.Payload[17]));
        Tick(); // 1.2 s after the first clock under the new lifetime

        Assert.Equal(2, Payloads(0x28).Last().Payload[17]);
        group.Sequence = 2;
        Tick();
        Tick();
        Assert.True(_processState.AreScreensColoured(MacText(1)));
    }

    // Ninety-six seeded mixtures of what can disturb an LCD FLEX group's colours and a water
    // block's switch - the group's count, slot and master changing, a transmitter lifetime counted
    // elsewhere, the transmitter faulted and back, the receiver reopened, the pump control
    // released - for fifty ticks each, after which everything holds still: both looks are done
    // within two minutes, marked, and nothing under a sequence is sent again, while the clock goes
    // on. No command is reset at the end, so a round the disturbances stranded, or a look they
    // completed falsely, would show as one never done or never sent.
    [Fact]
    public void EveryMixtureOfDisturbances_LeavesBothLooksDone_AndNoSequenceHeld() {
        for (int scenario = 0; scenario < 96; scenario++) {
            var random = new Random(scenario);
            var rig = new FakeWirelessRig();
            var clock = new FakeClock();
            var configuration = new FakeWirelessConfiguration();
            var processState = new WirelessProcessState();
            configuration.FanScreens[MacText(1)] = new WirelessFanScreenPresentation(B(1, 2, 3, 4), B(0, 0, 0, 0), 60, 1, false);
            _ = configuration.MotherboardArgbSync.Add(MacText(1));
            FakeWirelessRecord group = Group(1, fans: 1, fanType: 51);
            rig.Records.Add(group);
            rig.Records.Add(Device(3, 10, receiver: 3));
            using var controller = new WirelessController(4, rig.Transmitter, rig.Receiver, configuration, clock, new FakeDelay(clock), new FakeLogger(), processState);
            int pump = Enumerable.Range(0, controller.ChannelCount).Single(channel => controller.Describe(channel).ControlId == "LianLi/w" + MacText(3) + "/pump/ctl");
            controller.SetTarget(pump, 50);
            for (int tick = 0; tick < 50; tick++) {
                switch (random.Next(8)) {
                    case 0:
                        group.FanCountByte = (byte)random.Next(5);
                        break;
                    case 1:
                        group.Receiver = B(0, 1, 13, 14)[random.Next(4)];
                        break;
                    case 2:
                        group.Master = random.Next(2) == 0 ? new byte[6] : Master;
                        break;
                    case 3:
                        processState.TransmitterLost();
                        break;
                    case 4:
                        Lose(rig.Transmitter);
                        break;
                    case 5:
                        Reopen(rig.Transmitter);
                        break;
                    case 6:
                        rig.Receiver.Generation++;
                        break;
                    default:
                        controller.ReleaseChannel(pump);
                        break;
                }

                clock.Advance(Second);
                controller.ApplyPending();
                controller.PollRpm();
            }

            Reopen(rig.Transmitter);
            group.Master = Master;
            group.Receiver = 1;
            group.FanCountByte = 4;
            group.FanTypes = B(51, 51, 51, 51);
            controller.SetTarget(pump, 50);
            for (int tick = 0; tick < 120; tick++) {
                clock.Advance(Second);
                controller.ApplyPending();
                controller.PollRpm();
            }

            Assert.True(processState.AreScreensColoured(MacText(1)), "the colours in scenario " + scenario);
            Assert.True(processState.IsScreenSwitched(MacText(3)), "the switch in scenario " + scenario);
            rig.Transmitter.Writes.Clear();
            for (int tick = 0; tick < 15; tick++) {
                clock.Advance(Second);
                controller.ApplyPending();
                controller.PollRpm();
            }

            Assert.DoesNotContain(rig.Payloads(), p => p.Payload[1] is 0x19 or 0x27 or 0x28 or 0x29);
            Assert.Equal(15, rig.Payloads().Count(p => p.Payload[1] == 0x14));
        }
    }

    // The transmitter's handle is lost inside a cycle and reopened by the cycle's next transfer,
    // before any tick has counted the loss: the clock that then completes goes out on a new handle
    // (the transport's generation moved at the fault), so the group's entry counts as published
    // from that clock, not from the one before the loss, though that was 2 s ago. The next tick
    // counts the loss and replays the reconnect, and the colours wait 1.2 s from the clock the
    // reconnect sends.
    [Fact]
    public void ATransmitterLostAndReopenedInsideACycle_HasTheColoursWaitForTheClockSentOnTheNewHandle() {
        FakeWirelessRecord group = LookDevice(pump: false);
        _rig.Records.Add(group);
        Build();
        Tick();
        Tick();
        _rig.Transmitter.FailWrite = packet => {
            if (packet[0] == 0x11 && _rig.Transmitter.Generation == 0) {
                _rig.Transmitter.Generation = 1; // the handle is lost in the master query: the generation moves at the fault
                _rig.Transmitter.IsFaulted = true;
                return true;
            }

            _rig.Transmitter.IsFaulted = false; // the next transfer reopens it
            return false;
        };
        ClearWrites();

        Tick(); // the query fails; the clock goes out on the reopened handle

        Assert.Contains(_log.Messages, m => m.Contains("master failed"));
        Assert.DoesNotContain(TransmitterLostLine(1), _log.Messages);
        Assert.NotEmpty(Payloads(0x14));
        Assert.Empty(Payloads(0x28));
        ClearWrites();
        Tick(); // the loss is counted and the reconnect replayed, with the clock it sends
        Assert.Contains(TransmitterLostLine(1), _log.Messages);
        Assert.Contains("W4 reconnected (transmitter generation 1, receiver generation 0)", _log.Messages);
        Assert.NotEmpty(Payloads(0x14));
        Assert.Empty(Payloads(0x28));
        Tick();
        Assert.Empty(Payloads(0x28));
        Tick();
        Assert.Single(Payloads(0x28));
    }

    // The receiver's handle lost and reopened leaves every mark standing: L-Connect's service has
    // no controller for the receiver (its RF layer reopens it by itself and applies nothing), so
    // nothing is sent again but what the record calls for.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALookMarked_IsNotSentAgain_WhenTheReceiverIsLostAndBack(bool pump) {
        FakeWirelessRecord record = LookDevice(pump);
        _rig.Records.Add(record);
        Build();
        DriveLook(pump);
        Tick();
        record.Sequence = 1;
        Tick();
        Tick();
        Assert.True(LookMarked(pump));

        _rig.Receiver.Generation = 1;
        _rig.Receiver.IsFaulted = true;
        _rig.Receiver.ReadFailure = new IOException("simulated read failure");
        for (int second = 0; second < 3; second++) {
            Tick();
        }

        Assert.Contains(_log.Messages, m => m.Contains("list failed"));
        Assert.True(LookMarked(pump));

        _rig.Receiver.IsFaulted = false;
        _rig.Receiver.ReadFailure = null;
        ClearWrites();
        Tick();
        Tick();

        Assert.Contains("W4 reconnected (transmitter generation 0, receiver generation 1)", _log.Messages);
        Assert.DoesNotContain(_log.Messages, m => m.Contains("the transmitter was lost"));
        Assert.Equal(0, _processState.TransmitterLifetime);
        Assert.NotEmpty(Payloads(ResentCommand(pump)));
        Assert.Empty(Payloads(LookCommand(pump)));
    }

    // A look not yet marked is begun for a lost device as for any other, as SyncControlInfo sends
    // to every device on its table, heard or not: here the switch for a block lost for want of a
    // master, in the cycle the master answers again. The block is heard again by that tick's read,
    // which starts its commands over as the block may have reset, so the round of one send is
    // dropped and the switch begins again under the next sequence.
    [Fact]
    public void TheScreenSwitch_IsBegunForABlockStillLost_AndStartedOverWhenItIsHeardAgain() {
        _rig.Records.Add(Device(1, 10));
        Build();
        Controller.SetTarget(0, 50);
        _rig.Master = new byte[6];
        for (int second = 0; second < WirelessDevice.MaximumMissedReads; second++) {
            Tick();
        }

        Assert.Contains(_log.Messages, m => m.Contains("not heard for 30 list reads"));
        Assert.Empty(Payloads(0x19)); // nothing goes out without a master
        _rig.Master = (byte[])Master.Clone();
        ClearWrites();

        Tick(); // the master is back and the cycle runs for the block, still lost until this tick's read
        Assert.NotEmpty(Payloads(0x21));
        Assert.Equal(1, Assert.Single(Payloads(0x19)).Payload[17]);
        Assert.Contains("W4:a00000000001 heard again", _log.Messages);
        Assert.False(_processState.IsScreenSwitched(MacText(1)));

        Tick();
        Assert.Equal(new byte[] { 1, 2 }, Payloads(0x19).Select(p => p.Payload[17]));
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
    public void TheClock_IsBroadcastEverySecond_WithTheLocalTime() {
        _configuration.Channels["112233445566"] = 21;
        Build();
        ClearWrites();

        Tick();
        DateTime first = _clock.UtcNow;
        Tick();
        DateTime second = _clock.UtcNow;

        List<(byte[] Header, byte[] Payload)> clocks = Payloads(0x14);
        Assert.Equal(2, clocks.Count);
        Assert.Equal(B(0x10, 0, 21, 0xFF), clocks[0].Header);
        Assert.Equal(ClockPayload(first), clocks[0].Payload);
        Assert.Equal(ClockPayload(second), clocks[1].Payload);
    }

    // RFController.UpdateSensorDataByWiredLess: the clock's screen table carries an entry for every
    // bound LCD FLEX group on its receiver slot, one per fan from the saved settings, the fans
    // numbered in reverse of the service's (UpdateSensorSettingByWiredLess) except on a
    // left-attached SL-INF FLEX group; a group with nothing saved gets 255 / 0 / 60; a group on a
    // slot the table has no place for, a group without screens, and another master's are left out.
    [Fact]
    public void TheClock_CarriesEveryLcdFlexGroupsScreens_FromTheSavedSettings() {
        _configuration.FanScreens[MacText(1)] = new WirelessFanScreenPresentation(B(5, 6, 7, 8), B(1, 2, 3, 4), 80, 2, false);
        _configuration.FanScreens[MacText(2)] = new WirelessFanScreenPresentation(B(9, 10, 11, 12), B(0, 0, 0, 0), 0, 1, false);
        _rig.Records.Add(Group(1, fans: 3, fanType: 51, receiver: 3)); // TL FLEX LCD: reversed
        _rig.Records.Add(Group(2, fans: 2, fanType: 47, receiver: 1)); // SL-INF FLEX LCD, left attached: in order
        _rig.Records.Add(Group(3, fans: 2, fanType: 43, receiver: 13)); // nothing saved
        _rig.Records.Add(Group(4, fans: 2, fanType: 43, receiver: 14)); // no slot in the table
        _rig.Records.Add(Group(5, fans: 2, fanType: 53, receiver: 5)); // TL FLEX LED: no screens
        _rig.Records.Add(Group(6, fans: 2, fanType: 51, receiver: 6, master: Other));
        Build();
        ClearWrites();

        Tick();

        Assert.Equal(
            ClockPayload(
                _clock.UtcNow,
                (64, B(9, 10, 0, 0, 0x20, 0x20, 0, 0, 60)), // slot 1: fans 0-1 in order, brightness 0 -> 60, direction 1
                (64 + 24, B(7, 6, 5, 0, 0x43, 0x42, 0x41, 0, 80)), // slot 3: fan 2 first, direction 2 << 5 | source
                (64 + 144, B(255, 255, 0, 0, 0, 0, 0, 0, 60))), // slot 13: the default entry
            Assert.Single(Payloads(0x14)).Payload);
    }

    // isINFRightAttach (the fan count byte ten or more) reverses an SL-INF FLEX group's numbering too.
    [Fact]
    public void TheClock_NumbersARightAttachedSlInfFlexGroupsScreensInReverse() {
        _configuration.FanScreens[MacText(1)] = new WirelessFanScreenPresentation(B(1, 2, 3, 4), B(0, 0, 0, 0), 60, 1, false);
        FakeWirelessRecord group = Group(1, fans: 2, fanType: 43, receiver: 2);
        group.FanCountByte = 12;
        _rig.Records.Add(group);
        Build();
        ClearWrites();

        Tick();

        Assert.Equal(ClockPayload(_clock.UtcNow, (64 + 12, B(2, 1, 0, 0, 0x20, 0x20, 0, 0, 60))), Assert.Single(Payloads(0x14)).Payload);
    }

    // CheckSaveConfig: an hour after start, one save (SaveConfig(1): one send, no pause after it).
    [Fact]
    public void TheConfiguration_IsSavedAnHourAfterStart() {
        Build();
        _clock.Advance(TimeSpan.FromMinutes(60));
        Tick();

        (byte[] header, byte[] payload) = Assert.Single(Payloads(0x15));
        Assert.Equal(B(0x10, 0, 8, 0xFF), header);
        Assert.Equal(Expected(240, (0, B(0x12, 0x15, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF)), (8, Master), (14, B(0xFF))), payload);
        Assert.DoesNotContain(TimeSpan.FromMilliseconds(200), _delay.Waits);
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
    // re-asserts the channel - runs at once instead of at the next second.
    [Fact]
    public void AReopenedDongle_RunsTheReplayAndTheCycleAtOnce() {
        Build();
        int replays = 0;
        Controller.ReplayOnReconnect(() => {
            replays++;
            return true;
        });
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

    // While either dongle's handle is still faulted nothing is replayed: the dongle is off the
    // bus, and the cycle's own transfers are what reopen it. A device's effect tried again then
    // would be sent, and given up on, against a dead transmitter. Once both are back the replay
    // runs and the cycle follows at once, the screen switch included, since the transmitter's
    // loss dropped its mark the moment it was seen.
    [Fact]
    public void AFaultedDongle_HasNothingReplayed_UntilBothDonglesAreBack() {
        _rig.Records.Add(Device(1, 10));
        Build();
        int replays = 0;
        Controller.ReplayOnReconnect(() => {
            replays++;
            return true;
        });
        Controller.SetTarget(0, 0);
        Tick();
        _rig.Records[0].Sequence = 1;
        Tick();
        Tick(); // the screen switch is acknowledged and done
        ClearWrites();

        _rig.Transmitter.Generation = 1;
        _rig.Transmitter.IsFaulted = true;
        Controller.ApplyPending();
        _rig.Receiver.IsFaulted = true;
        _rig.Transmitter.IsFaulted = false;
        Controller.ApplyPending();

        Assert.Equal(0, replays);
        Assert.DoesNotContain(_log.Messages, m => m.Contains("W4 reconnected"));
        Assert.Contains(TransmitterLostLine(1), _log.Messages);
        Assert.False(_processState.IsScreenSwitched(MacText(1)));
        Assert.Empty(Payloads(0x19));

        _rig.Receiver.IsFaulted = false;
        Controller.ApplyPending();

        Assert.Equal(1, replays);
        Assert.Contains("W4 reconnected (transmitter generation 1, receiver generation 0)", _log.Messages);
        Assert.Contains(_rig.Transmitter.Writes, w => w[0] == 0x11);
        Tick();
        Assert.NotEmpty(Payloads(0x21)); // the pump's parameters go out again with the cycle
        Assert.NotEmpty(Payloads(0x19)); // and so does the screen switch, its mark dropped with the transmitter
    }

    // A replay that reports failure stays owed and is tried again on the keepalive interval; the
    // reconnect itself is recorded and the cycle runs.
    [Fact]
    public void AReplayThatReportsFailure_StaysOwed_AndIsTriedAgainOnTheKeepaliveInterval() {
        Build();
        int replays = 0;
        Controller.ReplayOnReconnect(() => ++replays >= 2);
        Tick();
        _rig.Transmitter.Generation = 1;

        Controller.ApplyPending();
        Assert.Equal(1, replays);
        Assert.Contains("W4 reconnected (transmitter generation 1, receiver generation 0)", _log.Messages);
        Controller.ApplyPending();
        Assert.Equal(1, replays);

        _clock.Advance(ChannelWriteDecision.RefreshInterval);
        Controller.ApplyPending();
        Assert.Equal(2, replays);
        _clock.Advance(ChannelWriteDecision.RefreshInterval);
        Controller.ApplyPending();
        Assert.Equal(2, replays);
    }

    [Fact]
    public void AReplayThatThrows_IsTriedAgainNextTick() {
        Build();
        int attempts = 0;
        Controller.ReplayOnReconnect(() => {
            if (++attempts == 1) {
                throw new IOException("replay failed");
            }

            return true;
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

    // The fake clock with a hook: an action run inside the n-th read after it is set, so a test
    // can have another controller of the process act at one exact point of a pass.
    private sealed class ClockWithHook : IClock {
        private readonly FakeClock _inner;
        private int _readsToGo;
        private Action? _action;

        public ClockWithHook(FakeClock inner) => _inner = inner;

        public DateTime UtcNow {
            get {
                if (_readsToGo > 0 && --_readsToGo == 0) {
                    Action? action = _action;
                    _action = null;
                    action?.Invoke();
                }

                return _inner.UtcNow;
            }
        }

        public void RunOnRead(int reads, Action action) {
            _readsToGo = reads;
            _action = action;
        }
    }
}
