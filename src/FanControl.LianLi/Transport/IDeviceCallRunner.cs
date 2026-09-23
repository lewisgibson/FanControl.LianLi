using System;
using System.Threading;

namespace FanControl.LianLi.Transport;

/// <summary>
/// The bounded wait every call that reaches a device goes through (see <see cref="BoundedDeviceCall"/>).
/// A seam so the transports' timeout decisions - which handle faults, what is cancelled, what a late
/// result becomes - are tested deterministically, without real threads racing real deadlines.
/// </summary>
internal interface IDeviceCallRunner {
    /// <summary>
    /// Run <paramref name="call"/> for at most <paramref name="timeoutMilliseconds"/>. Returns true if it
    /// completed in time, rethrowing any exception it threw; returns false once the deadline passed,
    /// having invoked <paramref name="onTimeout"/> to cancel it. <paramref name="operation"/> names the
    /// call (and the device) in the log line for a failure that arrives after the caller gave up.
    /// <paramref name="call"/> is handed a token that is cancelled once the deadline has passed and
    /// before its pending I/O is cancelled; a call that makes more than one native call checks it before
    /// each, so an abandoned call never starts another one that nothing would cancel.
    /// <paramref name="onReturned"/> is invoked exactly once, when the call is done with - returned,
    /// thrown, or never started because the deadline passed first: before this returns for a call that
    /// completed in time, and whenever its thread gets there for one given up on. It is never invoked
    /// for a call that never returns, so <see cref="DeviceCallGate"/> can tell a call still out on a
    /// device from one that has finished. It must not throw.
    /// </summary>
    bool TryRun(
        string operation, Action<CancellationToken> call, int timeoutMilliseconds, Action onTimeout, Action onReturned);
}
