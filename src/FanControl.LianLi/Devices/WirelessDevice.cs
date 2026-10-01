using System;
using FanControl.LianLi.Protocol;

namespace FanControl.LianLi.Devices;

/// <summary>
/// One entry in a <see cref="WirelessDeviceTable"/>: a wireless device the receiver has reported,
/// with its latest record and the state L-Connect's <c>RfDevice</c> keeps for it - whether this master
/// took it as bound, the receiver slot it was bound under, its target PWMs, how many list reads it
/// has left before it is dropped, the commands being sent to it under a sequence - and the plugin's
/// own "lost" mark and lighting state. Owned by the worker thread; nothing here is read from the host's.
/// </summary>
internal sealed class WirelessDevice {
    /// <summary>
    /// How many list reads a device may go unheard: <c>RfDevice.max_live_time</c>. L-Connect drops
    /// every device but a Lancool 217 from its table after this many, bound or not; the plugin also
    /// counts it for every device it drives, and reports one unheard this long as lost.
    /// </summary>
    public const int MaximumMissedReads = 30;

    private Round? _round;

    /// <summary>Start tracking a device from the first record the receiver reported for it.</summary>
    public WirelessDevice(WirelessDeviceRecord record) {
        Record = record ?? throw new ArgumentNullException(nameof(record));
        Live = MaximumMissedReads;
    }

    /// <summary>The latest record the receiver reported for the device.</summary>
    public WirelessDeviceRecord Record { get; private set; }

    /// <summary>The device's RF address.</summary>
    public byte[] Mac => Record.Mac;

    /// <summary>The device's RF address as twelve hex digits.</summary>
    public string MacText => Record.MacText;

    /// <summary>What the device is, from its latest record.</summary>
    public WirelessDeviceKind Kind => Record.Kind;

    /// <summary>
    /// L-Connect's <c>bind_to_master</c>: set when the device is heard naming this master, and kept
    /// through anything but an unbind - which the plugin never sends, so it is never cleared.
    /// </summary>
    public bool IsBound { get; set; }

    /// <summary>Whether this master already had its limit of bound devices when this one appeared (see <see cref="WirelessDeviceTable"/>).</summary>
    public bool IsRefused { get; set; }

    /// <summary>
    /// L-Connect's <c>target_rx_type</c>: the receiver slot the device reported when it was taken as
    /// bound. Every command restates it at payload byte 14.
    /// </summary>
    public byte TargetReceiverType { get; set; }

    /// <summary>L-Connect's <c>target_fans_pwm</c>: the four slot PWMs the device should be running, starting from what it reported when taken as bound.</summary>
    public byte[] TargetPwm { get; } = new byte[WirelessProtocol.SlotsPerGroup];

    /// <summary>Which slots of <see cref="TargetPwm"/> a FanControl control is driving; only those are compared and resent for.</summary>
    public bool[] DrivenSlots { get; } = new bool[WirelessProtocol.SlotsPerGroup];

    /// <summary>L-Connect's <c>live</c> countdown: reset by each record, decremented by each list read for every device but a Lancool 217.</summary>
    public int Live { get; set; }

    /// <summary>L-Connect's <c>ConflictCnt</c>: how often the device's receiver slot has been seen shared with another device of this master.</summary>
    public int ConflictCount { get; set; }

    /// <summary>Whether the receiver slot conflict has been logged since it last cleared.</summary>
    public bool ConflictLogged { get; set; }

    /// <summary>Whether the latest list read carried the device.</summary>
    public bool Heard { get; set; }

    /// <summary>
    /// Whether any list read this controller made has carried the device. One put on the table only
    /// by L-Connect's locked list has not been, until the receiver reports it: until then nothing is
    /// sent to it, since its saved pairing is no evidence it is paired now, and the speed command
    /// restates a binding.
    /// </summary>
    public bool EverHeard { get; set; }

    /// <summary>Consecutive list reads that did not carry the device.</summary>
    public int MissedReads { get; set; }

    /// <summary>
    /// Whether the device has gone <see cref="MaximumMissedReads"/> reads unheard while staying on
    /// the table - the list is locked, or the master is unknown so no list is read: it reads as 0 rpm
    /// and no temperature until it is heard again. It is still driven, as L-Connect drives it: a
    /// receiver that stops answering says nothing about whether the device still hears the transmitter.
    /// Off the locked list a device unheard that long is dropped from the table instead, as L-Connect
    /// drops it, and its sensors read 0 the same way (<see cref="WirelessDeviceTable"/>).
    /// </summary>
    public bool IsLost { get; set; }

    /// <summary>L-Connect's <c>sys_offset</c>: the master's clock less the device's, kept for the Lancool 217 and the V150.</summary>
    public long ClockOffset { get; set; }

    /// <summary>
    /// L-Connect's <c>changingEffect</c>: the device has a saved effect it does not report running,
    /// so it is being streamed, and is skipped - and not numbered - by the speed resend until it takes it.
    /// </summary>
    public bool ChangingEffect { get; set; }

    /// <summary>Whether <see cref="Effect"/> has been looked up (it is read from L-Connect's files once).</summary>
    public bool EffectLoaded { get; set; }

    /// <summary>The saved lighting effect to replay to the device, when there is one.</summary>
    public WirelessSavedEffect? Effect { get; set; }

    /// <summary>The frame interval a Strimer's effect was last re-timed to (<c>MasterDevice.SyncStrimmer_22</c>), which L-Connect keeps on the effect.</summary>
    public double? RetimedInterval { get; set; }

    /// <summary>Streams tried in a row without the device taking the effect, sent or not; reset once it reports the effect.</summary>
    public int UntakenStreams { get; set; }

    /// <summary>When the first of <see cref="UntakenStreams"/> was tried.</summary>
    public DateTime FirstUntakenStreamUtc { get; set; }

    /// <summary>
    /// Whether the plugin gave up replaying the effect to this device, until the device is lost and
    /// heard again or a dongle reconnects.
    /// </summary>
    public bool EffectAbandoned { get; set; }

    /// <summary>
    /// The switch of a water block's screen to its wireless theme (RF command 0x19), sent until the
    /// block reports its sequence or the sends run out.
    /// </summary>
    public WirelessDeviceCommand ScreenMode { get; } = new WirelessDeviceCommand();

    /// <summary>
    /// The handover of the device's lighting to the motherboard's ARGB header (RF command 0x27),
    /// sent when <see cref="FollowsMotherboard"/> until the device reports its sequence or the sends
    /// run out, as <c>LWirelessController.ResumeSuspend</c> re-sends it for every device whose
    /// switch is saved on.
    /// </summary>
    public WirelessDeviceCommand LightingSync { get; } = new WirelessDeviceCommand();

    /// <summary>
    /// The switch of an LCD FLEX group's screens onto their wireless themes (RF command 0x29),
    /// sent while a screen's bit is clear until the record shows every bit, with L-Connect's rounds
    /// and readback (<c>FlexLCDSdkHelper.TryRestoreWirelessThemeSwitches</c>).
    /// </summary>
    public WirelessDeviceCommand ThemeSwitch { get; } = new WirelessDeviceCommand();

    /// <summary>
    /// The colours of an LCD FLEX group's screens' wireless themes (RF command 0x28), one screen
    /// per round, sent until the group reports the round's sequence or the sends run out, as
    /// <c>applyWirelessLCDMode</c> sends each fan's through <c>RFController.UpdateSensorColors</c>.
    /// </summary>
    public WirelessDeviceCommand ScreenColours { get; } = new WirelessDeviceCommand();

    /// <summary>How many of the group's screens have had their theme colours sent (a round each, acknowledged or not) since the command last started over: the next round is for this screen.</summary>
    public int ColouredScreens { get; set; }

    /// <summary>
    /// The group's screen table entry as a clock broadcast last carried it, with the transmitter
    /// lifetime and handle it went out under and when the broadcast completed; null while no
    /// broadcast has carried the group's entry since the command last started over, or since a
    /// broadcast completed without it. Each screen's colours are sent 1.2 s after the broadcast
    /// that carried the entry the group needs now, as <c>RFController.UpdateSensorSettingByWiredLess</c>
    /// waits that long between the broadcast and queuing them.
    /// </summary>
    public WirelessScreenEntryPublication? ScreenEntryPublication { get; private set; }

    /// <summary>
    /// A clock broadcast completed carrying no entry for the group, so whatever was published is
    /// withdrawn: the next broadcast that carries the entry publishes it again, from then.
    /// </summary>
    public void WithdrawScreenEntry() => ScreenEntryPublication = null;

    /// <summary>
    /// A clock broadcast carrying <paramref name="entry"/> for the group on
    /// <paramref name="receiverType"/> completed at <paramref name="completedUtc"/>, under
    /// <paramref name="transmitterLifetime"/> on the transmitter handle of
    /// <paramref name="transmitterGeneration"/>. The same entry published the same way keeps its
    /// earlier time, since the clock goes out every second carrying it and the screens have had it
    /// since the first; anything else is published again, from now.
    /// </summary>
    public void PublishScreenEntry(int receiverType, byte[] entry, int transmitterLifetime, int transmitterGeneration, DateTime completedUtc) {
        if (ScreenEntryPublication is null || !ScreenEntryPublication.Carries(receiverType, entry, transmitterLifetime, transmitterGeneration)) {
            ScreenEntryPublication = new WirelessScreenEntryPublication(receiverType, entry, transmitterLifetime, transmitterGeneration, completedUtc);
        }
    }

    /// <summary>
    /// The command sequence the device's commands are sent under: L-Connect's <c>targe_cmd_seq</c>,
    /// one per device, raised for every round of sends; 0 before any round. The device acknowledges
    /// a command by reporting the sequence it carried (record byte 40), and it has one sequence, so
    /// the commands are sent one at a time (<see cref="SendingCommand"/>) and a reported sequence
    /// acknowledges exactly the command that carried it, never one lost while another got through.
    /// </summary>
    public byte TargetSequence { get; private set; }

    /// <summary>The command whose round is being sent under <see cref="TargetSequence"/>, or null while none is; the others wait their turn.</summary>
    public WirelessDeviceCommand? SendingCommand => _round?.Command;

    /// <summary>
    /// Start a round of <paramref name="command"/> under the device's next sequence: the one after
    /// the last round's (or after the sequence the record reports, for the first), as
    /// <c>RFController</c> raises <c>targe_cmd_seq</c> for every command it queues (1 to 254, as
    /// L-Connect wraps it); never the one the record reports now, which would acknowledge the round
    /// before it went out. <paramref name="payload"/> builds each send from the sequence and is
    /// kept with the round, so the device carries it to its end (<see cref="NextSend"/>) whatever
    /// its caller does next. Returns the round's first send, counted.
    /// </summary>
    public byte[] BeginCommand(WirelessDeviceCommand command, Func<byte, byte[]> payload) {
        if (command is null) {
            throw new ArgumentNullException(nameof(command));
        }

        if (payload is null) {
            throw new ArgumentNullException(nameof(payload));
        }

        byte next = WirelessProtocol.NextCommandSequence(TargetSequence == 0 ? Record.CommandSequence : TargetSequence);
        if (next == Record.CommandSequence) {
            next = WirelessProtocol.NextCommandSequence(next);
        }

        TargetSequence = next;
        _round = new Round(command, payload);
        command.Sequence = next;
        command.Sends = 1;
        command.Rounds++;
        command.Ended = null;
        return payload(next);
    }

    /// <summary>
    /// One pass of <c>MasterDevice.SyncControlInfo</c> for the round under way: the round ends
    /// when the record reports its sequence (acknowledged) or its sends are out (exhausted), which
    /// frees the device's sequence and leaves how it ended on the command
    /// (<see cref="WirelessDeviceCommand.Ended"/>) for the pass that next services it; otherwise the
    /// round's next send is returned, counted. Null when no round is under way or it has just
    /// ended. The device does this, not the command's caller, so the device's one sequence is never
    /// held indefinitely by a round nothing finishes.
    /// </summary>
    public byte[]? NextSend() {
        Round? round = _round;
        if (round is null) {
            return null;
        }

        if (Record.CommandSequence == TargetSequence) {
            EndRound(round.Command, WirelessRoundEnd.Acknowledged);
            return null;
        }

        if (round.Command.Sends >= WirelessDeviceCommand.SendsPerRound) {
            EndRound(round.Command, WirelessRoundEnd.Exhausted);
            return null;
        }

        round.Command.Sends++;
        return round.Payload(TargetSequence);
    }

    /// <summary>
    /// Whether L-Connect's per-device "sync to motherboard" switch is saved on for the device (read
    /// with <see cref="Effect"/>): its lighting is handed to the motherboard's ARGB header. Its saved
    /// effect is still streamed to it whenever it does not report running it, as
    /// <c>MasterDevice.SyncRgbData</c> streams it whatever the switch says; the switch only decides
    /// what the LEDs show.
    /// </summary>
    public bool FollowsMotherboard { get; set; }

    /// <summary>
    /// Start the device's effect and every command over, a round under way included, because the
    /// device, or the dongle carrying it, came back and may have reset. A screen switch or a
    /// colouring the process has marked under the current transmitter lifetime is the exception:
    /// the next pass finds the mark and leaves it, as L-Connect repeats neither on a device's
    /// return (<see cref="WirelessProcessState"/>).
    /// </summary>
    public void ReapplySavedLook() {
        UntakenStreams = 0;
        EffectAbandoned = false;
        ScreenMode.Reset();
        LightingSync.Reset();
        ThemeSwitch.Reset();
        ScreenColours.Reset();
        ColouredScreens = 0;
        ScreenEntryPublication = null;
        _round = null;
    }

    /// <summary>Whether the current run of effect streams has been logged.</summary>
    public bool EffectStreamLogged { get; set; }

    /// <summary>The target last sent, to tell a resend from a new speed in the log.</summary>
    public byte[]? LastSentTarget { get; set; }

    /// <summary>Whether the current run of resends has been logged.</summary>
    public bool ResendLogged { get; set; }

    /// <summary>A new record for the device: it is heard, so its countdown starts again (<c>MasterDevice.FindDev</c>).</summary>
    public void Update(WirelessDeviceRecord record) {
        Record = record ?? throw new ArgumentNullException(nameof(record));
        Live = MaximumMissedReads;
    }

    /// <summary>
    /// Take the device as bound to this master, as <c>RefreshList</c> does for a device first heard
    /// naming it: remember its receiver slot and start its targets from the PWMs it reports.
    /// </summary>
    public void TakeAsBound() => TakeAsBound(Record.ReceiverType, Record.Pwm);

    /// <summary>
    /// Take the device as bound on <paramref name="targetReceiverType"/> with targets
    /// <paramref name="targetPwm"/>, as L-Connect restores a device from its locked list.
    /// </summary>
    public void TakeAsBound(byte targetReceiverType, byte[] targetPwm) {
        if (targetPwm is null) {
            throw new ArgumentNullException(nameof(targetPwm));
        }

        IsBound = true;
        TargetReceiverType = targetReceiverType;
        Array.Copy(targetPwm, TargetPwm, WirelessProtocol.SlotsPerGroup);
    }

    private void EndRound(WirelessDeviceCommand command, WirelessRoundEnd how) {
        command.Sequence = null;
        command.Ended = how;
        _round = null;
    }

    // The round under way: the command that has the device's sequence and what each of its sends is.
    private sealed class Round {
        public Round(WirelessDeviceCommand command, Func<byte, byte[]> payload) {
            Command = command;
            Payload = payload;
        }

        public WirelessDeviceCommand Command { get; }

        public Func<byte, byte[]> Payload { get; }
    }
}
