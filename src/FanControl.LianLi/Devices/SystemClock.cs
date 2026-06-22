using System;
using System.Threading;

namespace FanControl.LianLi.Devices;

/// <summary>The production <see cref="IClock"/> backed by <see cref="DateTime.UtcNow"/>.</summary>
internal sealed class SystemClock : IClock {
    /// <inheritdoc />
    public DateTime UtcNow => DateTime.UtcNow;

    /// <inheritdoc />
    public void Sleep(TimeSpan duration) => Thread.Sleep(duration);
}
