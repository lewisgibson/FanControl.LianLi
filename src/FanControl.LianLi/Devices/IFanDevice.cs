using System;

namespace FanControl.LianLi.Devices;

/// <summary>
/// One physical controllable device the worker drives: a fan/pump controller with a
/// fixed set of channels. The FanControl-thread methods (<see cref="SetTarget"/>,
/// <see cref="ReleaseChannel"/>, <see cref="GetRpm"/>) only mutates in-memory state;
/// every USB transfer happens on the worker-thread methods (<see cref="ApplyPending"/>,
/// <see cref="PollRpm"/>). This is the interface the worker and the plugin's sensor wiring
/// depend on, so a device family (the Uni 0x0CF2 controllers, the 0x0416 command-packet
/// controllers) is plugged in without either of them knowing the family.
/// </summary>
internal interface IFanDevice : IDisposable {
    /// <summary>How many controllable channels this device exposes.</summary>
    int ChannelCount { get; }

    /// <summary>
    /// Whether <paramref name="channel"/> has a fan attached and should be shown to the host.
    /// A device that cannot tell, or that knows every channel is real, returns <c>true</c> so the
    /// channel is shown; only a channel proven empty at startup returns <c>false</c>. The result is
    /// fixed before <see cref="Describe"/> is read, so it is stable across a run and safe to read
    /// from the host thread.
    /// </summary>
    bool IsChannelPopulated(int channel);

    /// <summary>
    /// The stable sensor identity and display names for <paramref name="channel"/>. The
    /// ids are keyed so a user's saved fan-curve bindings survive a restart, so a device
    /// must return the same strings run to run for the same physical channel.
    /// </summary>
    ChannelDescriptor Describe(int channel);

    /// <summary>Set the commanded duty for a channel. The worker pushes it to hardware.</summary>
    void SetTarget(int channel, int duty);

    /// <summary>Release a channel so the keepalive stops asserting it (used by Reset).</summary>
    void ReleaseChannel(int channel);

    /// <summary>Read the last measured RPM for a channel.</summary>
    float GetRpm(int channel);

    /// <summary>Push any changed-or-stale channel targets to the hardware.</summary>
    void ApplyPending();

    /// <summary>Read every channel's RPM into the cache, ignoring implausible readings.</summary>
    void PollRpm();

    /// <summary>
    /// Register work to replay whenever the transport has reopened the device after losing its
    /// handle (its <c>Generation</c> moved on at the fault, and its <c>IsFaulted</c> is false
    /// again). A re-enumerated device may have been power-cycled and lost its volatile state, so
    /// the Lighting build registers its saved-look replay here; the device's own setup writes are
    /// replayed regardless. Runs on the worker thread, in the same order Initialize used, before
    /// the next duty write, and only once the device is back. The replay returns whether it was
    /// done: one that returns false stays owed and is run again (see <see cref="ReconnectReplay"/>),
    /// so a look the device refused, or that was cut short by the device going away again, is never
    /// recorded as replayed. Holds at most one replay; the standard build registers none.
    /// </summary>
    void ReplayOnReconnect(Func<bool> replay);
}
