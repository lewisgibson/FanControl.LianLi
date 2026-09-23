using System;
using System.Threading;

namespace FanControl.LianLi.Devices;

/// <summary>The production <see cref="IDelay"/> backed by <see cref="Thread.Sleep(TimeSpan)"/>.</summary>
internal sealed class SystemDelay : IDelay {
    /// <inheritdoc />
    public void Wait(TimeSpan duration) => Thread.Sleep(duration);
}
