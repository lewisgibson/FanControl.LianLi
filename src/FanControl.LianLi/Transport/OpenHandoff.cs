using System;

namespace FanControl.LianLi.Transport;

/// <summary>
/// Carries what a bounded open produced - a device handle, a transport - from the throwaway thread that
/// ran it (see <see cref="BoundedDeviceCall"/>) to the caller that waited for it. The two race at the
/// deadline: when the caller gives up first, a result the thread finishes afterwards belongs to nobody
/// and would leak an open handle on the device, so the handoff disposes it on that thread instead; when
/// the thread finishes first, the caller takes it. Pure and lock-guarded so the race is unit-tested.
/// </summary>
/// <typeparam name="T">The open's product; owned by whichever side ends up holding it.</typeparam>
internal sealed class OpenHandoff<T> where T : class, IDisposable {
    private readonly object _gate = new object();
    private T? _result;
    private bool _abandoned;

    /// <summary>
    /// Called on the opening thread once the open produced <paramref name="result"/>: stores it for the
    /// caller, or disposes it at once if the caller has already taken its turn and gone.
    /// </summary>
    public void Complete(T result) {
        if (result is null) {
            throw new ArgumentNullException(nameof(result));
        }

        lock (_gate) {
            if (!_abandoned) {
                _result = result;
                return;
            }
        }

        result.Dispose();
    }

    /// <summary>
    /// Called once by the waiting caller after its bounded wait ended, either way. Returns the result
    /// if the open has completed, else null - and from then on the handoff is abandoned, so a result
    /// completed later is disposed on its thread rather than leaked.
    /// </summary>
    public T? Take() {
        lock (_gate) {
            _abandoned = true;
            T? result = _result;
            _result = null;
            return result;
        }
    }
}
