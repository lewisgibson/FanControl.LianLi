using System;
using System.Collections.Generic;
using System.IO;
using FanControl.LianLi.Transport;

namespace FanControl.LianLi.Tests.Fakes;

/// <summary>
/// One L-Wireless dongle as the controller sees it: every packet written is kept, and a read
/// returns what <see cref="Responder"/> makes of the last packet written, zero-padded to the length
/// asked for (an empty reply when there is no responder or it returns null, as a silent dongle's).
/// </summary>
internal sealed class FakeWirelessDongle : IDeviceTransport {
    private byte[] _lastWrite = new byte[64];

    public List<byte[]> Writes { get; } = new List<byte[]>();

    public Func<byte[], byte[]?>? Responder { get; set; }

    /// <summary>A write this returns true for throws instead of being kept.</summary>
    public Func<byte[], bool>? FailWrite { get; set; }

    /// <summary>When set, every read throws.</summary>
    public Exception? ReadFailure { get; set; }

    public int Generation { get; set; }

    public int DisposeCount { get; private set; }

    public int ReadCount { get; private set; }

    public bool CanWrite => true;

    public void Write(byte[] report) {
        if (FailWrite?.Invoke(report) == true) {
            throw new IOException("simulated write failure");
        }

        byte[] copy = (byte[])report.Clone();
        Writes.Add(copy);
        _lastWrite = copy;
    }

    public void SetFeature(byte[] report) => throw new NotSupportedException();

    public byte[] GetInputReport(byte reportId, int length) => throw new NotSupportedException();

    public byte[] Read(int length) {
        ReadCount++;
        if (ReadFailure != null) {
            throw ReadFailure;
        }

        var buffer = new byte[length];
        byte[]? reply = Responder?.Invoke(_lastWrite);
        if (reply != null) {
            Array.Copy(reply, buffer, Math.Min(reply.Length, length));
        }

        return buffer;
    }

    public void Dispose() => DisposeCount++;
}
