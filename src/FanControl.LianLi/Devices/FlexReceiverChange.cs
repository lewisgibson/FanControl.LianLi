namespace FanControl.LianLi.Devices;

/// <summary>
/// What a <see cref="FlexReceiverController"/> noticed on a poll that the plugin may have to
/// answer with a FanControl refresh. Each is answered differently: a change of hands means the
/// sensors the host holds are on the wrong controller, a fan reported later is a new sensor only
/// if the host does not already have it, and another receiver answering means this controller is
/// for a chain that is no longer on its path.
/// </summary>
internal enum FlexReceiverChange {
    /// <summary>The wireless controller now drives the chain; nothing more is sent over USB.</summary>
    TakenByRadio,

    /// <summary>The wireless controller no longer drives the chain; it is driven over USB again, with sensors if it had none.</summary>
    ReleasedByRadio,

    /// <summary>The chain reported a fan it had not before, so it has a sensor it did not have.</summary>
    FanReported,

    /// <summary>The device on the receiver's path answered with another RF address; nothing is sent to it again.</summary>
    AnotherReceiverAnswered,
}
