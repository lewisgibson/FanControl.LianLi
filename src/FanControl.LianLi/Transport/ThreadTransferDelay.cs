using System.Threading;

namespace FanControl.LianLi.Transport;

/// <summary>
/// The production <see cref="ITransferDelay"/>: a plain thread sleep. Transfers run on the worker
/// thread (or a bounded call's throwaway thread), never on FanControl's, so the pause stalls only
/// the device it paces.
/// </summary>
internal sealed class ThreadTransferDelay : ITransferDelay {
    /// <summary>The one instance; it holds no state.</summary>
    public static readonly ThreadTransferDelay Instance = new ThreadTransferDelay();

    private ThreadTransferDelay() {
    }

    public void Wait(int milliseconds) => Thread.Sleep(milliseconds);
}
