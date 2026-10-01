using System;

namespace FanControl.LianLi.Devices;

/// <summary>
/// The work a controller was given to replay to its device whenever the device comes back
/// (<see cref="IFanDevice.ReplayOnReconnect"/>): the Lighting build's saved look. A controller
/// owes it from the moment it sets a reopened device up again, and it stays owed until a replay
/// reports that it was done, so a look the device refused, or that was cut short by the device
/// going away again, is tried again rather than recorded as replayed. A refused look is tried
/// again on the keepalive cadence (<see cref="ChannelWriteDecision.RefreshInterval"/>), like the
/// duty, so a device that rejects its look for good costs a line in the log every fifteen seconds
/// rather than every tick; a look owed afresh, because the device came back again, is tried at
/// once. Worker-thread only, like the controller that owns it.
/// </summary>
internal sealed class ReconnectReplay {
    private readonly IClock _clock;
    private Func<bool>? _replay;

    // What was registered when the device last came back, until it reports itself done; null
    // while nothing is owed.
    private Func<bool>? _owed;
    private DateTime _lastAttemptUtc = DateTime.MinValue;

    public ReconnectReplay(IClock clock) {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Whether a replay is owed to the device and has not yet reported itself done.</summary>
    public bool IsOwed => _owed != null;

    /// <summary>Hold <paramref name="replay"/> as the work to replay; at most one is held.</summary>
    public void Register(Func<bool> replay) {
        _replay = replay ?? throw new ArgumentNullException(nameof(replay));
    }

    /// <summary>
    /// The device is back: whatever is registered is owed to it now, and is tried at once by the
    /// next <see cref="Apply"/> however recently it was last tried. Nothing is owed when nothing
    /// is registered.
    /// </summary>
    public void Owe() {
        _owed = _replay;
        _lastAttemptUtc = DateTime.MinValue;
    }

    /// <summary>
    /// Run the replay if it is owed and due, and clear the debt if it reports itself done. One
    /// that reports failure stays owed and is due again after the keepalive interval; one that
    /// throws propagates and stays owed.
    /// </summary>
    public void Apply() {
        if (_owed is null) {
            return;
        }

        DateTime now = _clock.UtcNow;
        if (_lastAttemptUtc != DateTime.MinValue && ClockSpan.Since(now, _lastAttemptUtc) < ChannelWriteDecision.RefreshInterval) {
            return;
        }

        _lastAttemptUtc = now;
        if (_owed()) {
            _owed = null;
        }
    }
}
