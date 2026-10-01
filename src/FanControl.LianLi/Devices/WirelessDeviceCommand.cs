namespace FanControl.LianLi.Devices;

/// <summary>
/// One of the RF commands a device is sent under a command sequence until it reports that sequence
/// back in its record - a water block's switch to its wireless theme (0x19), a device's lighting
/// handed to the motherboard's ARGB header (0x27), an LCD FLEX group's screens' theme colours
/// (0x28), its screens switched onto their themes (0x29) - with the state L-Connect's
/// <c>RfDevice</c> keeps for it: the sequence a round of sends carries and how many of the ten
/// sends <c>MasterDevice.SyncControlInfo</c> makes have gone out. A round, once begun, is carried
/// to its end by the device (<see cref="WirelessDevice.NextSend"/>), not by whoever began it, and
/// how it ended waits in <see cref="Ended"/> for the pass that next services the command. A
/// command whose result the record reports is confirmed by that result, read back after each
/// round, with one more round when it is missing, as
/// <c>FlexLCDSdkHelper.SaveThemeSwitchAfterReadback</c> retries once. Owned by the worker thread.
/// </summary>
internal sealed class WirelessDeviceCommand {
    /// <summary>
    /// How many sends a round makes before the command gives up on the acknowledgement:
    /// <c>RFController</c> queues ten of every command it sends under a sequence
    /// (<c>SwitchAioLcdWirelessMode</c>, <c>SyncMBLightSwitch</c>, <c>UpdateSensorColors</c>,
    /// <c>PlayWiredlessThemeSwitch</c>), one a pass of <c>MasterDevice.SyncControlInfo</c>.
    /// </summary>
    public const int SendsPerRound = 10;

    /// <summary>The command sequence the sends carry, or null while none is being sent.</summary>
    public byte? Sequence { get; set; }

    /// <summary>How many times the current sequence has been sent.</summary>
    public int Sends { get; set; }

    /// <summary>How many rounds of sends have been started for this command since it was last reset.</summary>
    public int Rounds { get; set; }

    /// <summary>How many list reads the readback has waited for the record to show the switch since the last round ended.</summary>
    public int ReadbackReads { get; set; }

    /// <summary>Whether the command is done with for this connection: acknowledged, confirmed, sent as often as L-Connect sends it, or not needed.</summary>
    public bool Done { get; set; }

    /// <summary>
    /// For a command marked for the process when it ends (the screen switch, the screen colours):
    /// the <see cref="WirelessProcessState.TransmitterLifetime"/> its state belongs to, stamped as
    /// its first round begins or as it takes the process's mark as its own. Its rounds and its
    /// <see cref="Done"/> count only while that is still the process's lifetime.
    /// </summary>
    public int TransmitterLifetime { get; set; }

    /// <summary>
    /// How the last round ended, from the moment the device ended it until the pass that services
    /// the command takes it (<see cref="TakeEnd"/>); null while a round is under way or none has
    /// ended unserviced. The end waits here for a caller that stopped calling mid-round (a pump
    /// control released, a screen gone from its group's count), so it is never lost.
    /// </summary>
    public WirelessRoundEnd? Ended { get; set; }

    /// <summary>Whether a round is being sent, or has ended and not been serviced yet: either way the command has a pass owed to it.</summary>
    public bool IsUnderWay => Sequence != null || Ended != null;

    /// <summary>How the last round ended, once, or null when no round has ended since the command was last serviced.</summary>
    public WirelessRoundEnd? TakeEnd() {
        WirelessRoundEnd? ended = Ended;
        Ended = null;
        return ended;
    }

    /// <summary>Start over: the device, or the dongle carrying it, came back, so the command is sent again if it is needed.</summary>
    public void Reset() {
        Sequence = null;
        Sends = 0;
        Rounds = 0;
        ReadbackReads = 0;
        Done = false;
        TransmitterLifetime = 0;
        Ended = null;
    }
}
