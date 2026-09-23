using System;
using FanControl.LianLi.Protocol;

namespace FanControl.LianLi.Devices;

/// <summary>
/// One entry in a <see cref="WirelessDeviceTable"/>: a wireless device the receiver has reported,
/// with its latest record and the state L-Connect's <c>RfDevice</c> keeps for it - whether this master
/// took it as bound, the receiver slot it was bound under, its target PWMs, how many list reads it
/// has left before it is dropped - plus the plugin's own "lost" mark and lighting state. Owned by
/// the worker thread; nothing here is read from the host's.
/// </summary>
internal sealed class WirelessDevice {
    /// <summary>
    /// How many list reads a device may go unheard: <c>RfDevice.max_live_time</c>. L-Connect drops an
    /// unbound device (and a V150) after this many; the plugin also counts it for every device it
    /// drives, and reports one unheard this long as lost.
    /// </summary>
    public const int MaximumMissedReads = 30;

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

    /// <summary>L-Connect's <c>live</c> countdown: reset by each record, decremented by each list read for an unbound device or a V150.</summary>
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
    /// Whether the device has gone <see cref="MaximumMissedReads"/> reads unheard: it reads as 0 rpm
    /// and no temperature until it is heard again. It is still driven, as L-Connect drives it: a
    /// receiver that stops answering says nothing about whether the device still hears the transmitter.
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
    /// The command sequence of the screen-mode switch being sent to the device (a water block),
    /// until it reports it back or the sends run out.
    /// </summary>
    public byte? ScreenModeSequence { get; set; }

    /// <summary>How many times the current screen-mode switch has been sent.</summary>
    public int ScreenModeSends { get; set; }

    /// <summary>Whether the screen-mode switch is done with for this connection: acknowledged, or sent as often as L-Connect sends it.</summary>
    public bool ScreenModeSwitched { get; set; }

    /// <summary>
    /// Apply the saved look afresh - its lighting effect and its screen mode - because the device,
    /// or the dongle carrying it, came back.
    /// </summary>
    public void ReapplySavedLook() {
        UntakenStreams = 0;
        EffectAbandoned = false;
        ScreenModeSwitched = false;
        ScreenModeSequence = null;
        LookReapplied = true;
    }

    /// <summary>
    /// Whether <see cref="ReapplySavedLook"/> has run for this device: its screen is switched again even
    /// if an earlier controller in this process already switched it, since it may have lost that.
    /// </summary>
    public bool LookReapplied { get; private set; }

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
}
