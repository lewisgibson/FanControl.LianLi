using System.Diagnostics;

namespace FanControl.LianLi.Transport;

/// <summary>
/// The production <see cref="IDeviceCallClock"/>: the high-resolution performance counter, which is
/// monotonic, so a wall-clock change (a time sync after a wake, say) never makes a call look younger
/// or older than it is.
/// </summary>
internal sealed class StopwatchDeviceCallClock : IDeviceCallClock {
    /// <summary>The one instance; it holds no state.</summary>
    public static readonly StopwatchDeviceCallClock Instance = new StopwatchDeviceCallClock();

    private StopwatchDeviceCallClock() {
    }

    // Scaled in floating point because the counter frequency need not be a multiple of 1000.
    public long NowMilliseconds => (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
}
