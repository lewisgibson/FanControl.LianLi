using System;
using System.Threading;

namespace FanControl.LianLi.Transport;

/// <summary>
/// A transport over a device path this process holds a claim on (<see cref="DeviceCallGate.TryClaim"/>):
/// every call goes straight through, and disposing it releases the claim once the device is closed,
/// so the path can be opened again only after this owner has let go of it.
/// </summary>
internal sealed class ClaimedTransport : IDeviceTransport {
    private readonly IDeviceTransport _inner;
    private readonly Action _release;
    private int _disposed;

    /// <summary>Wrap <paramref name="inner"/>, calling <paramref name="release"/> once it is closed.</summary>
    public ClaimedTransport(IDeviceTransport inner, Action release) {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _release = release ?? throw new ArgumentNullException(nameof(release));
    }

    /// <summary>The transport the claim is held for.</summary>
    public IDeviceTransport Inner => _inner;

    public bool CanWrite => _inner.CanWrite;

    public int Generation => _inner.Generation;

    public void Write(byte[] report) => _inner.Write(report);

    public void SetFeature(byte[] report) => _inner.SetFeature(report);

    public byte[] GetInputReport(byte reportId, int length) => _inner.GetInputReport(reportId, length);

    public byte[] Read(int length) => _inner.Read(length);

    // The transports' Dispose never throws; the claim is released after it either way.
    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) {
            return;
        }

        try {
            _inner.Dispose();
        } finally {
            _release();
        }
    }
}
