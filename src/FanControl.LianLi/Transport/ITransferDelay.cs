namespace FanControl.LianLi.Transport;

/// <summary>
/// The pause a transport takes between transfers where the device needs one: the settle after a
/// feature write, the wait before reopening a dongle whose write failed. An interface so that pacing is
/// asserted in tests, not slept through.
/// </summary>
internal interface ITransferDelay {
    /// <summary>Block the calling thread for <paramref name="milliseconds"/>.</summary>
    void Wait(int milliseconds);
}
