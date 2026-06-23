using System;

namespace FanControl.LianLi.Devices;

/// <summary>The production <see cref="IClock"/> backed by <see cref="DateTime.UtcNow"/>.</summary>
internal sealed class SystemClock : IClock {
    /// <inheritdoc />
    public DateTime UtcNow => DateTime.UtcNow;
}
