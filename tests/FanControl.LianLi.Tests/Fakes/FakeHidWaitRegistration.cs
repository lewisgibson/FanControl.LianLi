using System;
using System.Threading;

namespace FanControl.LianLi.Tests.Fakes;

/// <summary>A registered completion wait handed out by <see cref="FakeHidOverlappedApi"/>; counts its unregistrations.</summary>
internal sealed class FakeHidWaitRegistration : IDisposable {
    private int _disposals;

    public int Disposals => Volatile.Read(ref _disposals);

    public void Dispose() => Interlocked.Increment(ref _disposals);
}
