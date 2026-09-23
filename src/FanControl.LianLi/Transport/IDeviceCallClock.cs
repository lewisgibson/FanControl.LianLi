namespace FanControl.LianLi.Transport;

/// <summary>
/// The time source <see cref="DeviceCallGate"/> measures an abandoned call's age with, for the line
/// that says how long a device has been left with a call that has not returned. The transports sit
/// below the plugin's injected clock, so this is their own seam, and a test sets the time rather than
/// waiting for it.
/// </summary>
internal interface IDeviceCallClock {
    /// <summary>
    /// Milliseconds on a monotonic clock with an arbitrary origin: only the difference between two
    /// readings means anything, and it is not moved by a change to the wall clock.
    /// </summary>
    long NowMilliseconds { get; }
}
