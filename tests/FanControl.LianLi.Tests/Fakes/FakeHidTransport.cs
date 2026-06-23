using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using FanControl.LianLi.Hid;

namespace FanControl.LianLi.Tests.Fakes;

internal sealed class FakeHidTransport : IHidTransport {
    private readonly object _lock = new object();

    public List<byte[]> Writes { get; } = new List<byte[]>();

    /// <summary>Feature reports passed to <see cref="SetFeature"/>, in order.</summary>
    public List<byte[]> Features { get; } = new List<byte[]>();

    /// <summary>
    /// Every transfer in order across both report kinds: <c>true</c> = feature
    /// report (<see cref="SetFeature"/>), <c>false</c> = output report
    /// (<see cref="Write"/>). Lets a test assert the exact replay sequence.
    /// </summary>
    public List<KeyValuePair<bool, byte[]>> Transfers { get; } = new List<KeyValuePair<bool, byte[]>>();

    /// <summary>Number of <see cref="GetInputReport"/> calls, for asserting a read did or did not happen.</summary>
    public int ReadCount { get; private set; }

    /// <summary>
    /// Replies dequeued in order from <see cref="Read"/>, simulating the 0x0416
    /// interrupt-IN answers a device sends after a command write. A reply shorter than
    /// the requested length is zero-padded; an empty queue yields an all-zero buffer.
    /// </summary>
    public Queue<byte[]> ReadReplies { get; } = new Queue<byte[]>();

    /// <summary>Number of <see cref="Read"/> (interrupt-IN) calls.</summary>
    public int InterruptReadCount { get; private set; }

    /// <summary>
    /// When set, <see cref="GetInputReport"/> blocks on this event before returning, letting a
    /// test hold a tick mid-read (simulating the slow post-hibernate HID read that stalls the
    /// keepalive thread while it holds the tick gate).
    /// </summary>
    public ManualResetEventSlim? BlockReadsUntil { get; set; }

    /// <summary>Buffer returned (copied) from <see cref="GetInputReport"/>.</summary>
    public byte[] InputReport { get; set; } = new byte[65];

    /// <summary>When set, <see cref="GetInputReport"/> throws, simulating a device fault.</summary>
    public bool FailReads { get; set; }

    /// <summary>When set, <see cref="Write"/> and <see cref="SetFeature"/> throw, simulating a setup-write fault.</summary>
    public bool FailWrites { get; set; }

    /// <summary>When set, only <see cref="SetFeature"/> throws (a feature-report fault), simulating a lighting write the device rejects while output writes still work.</summary>
    public bool FailFeatures { get; set; }

    public bool IsDisposed { get; private set; }

    public bool CanWrite => true;

    public void Write(byte[] report) {
        if (FailWrites) {
            throw new IOException("simulated device write failure");
        }

        lock (_lock) {
            var copy = (byte[])report.Clone();
            Writes.Add(copy);
            Transfers.Add(new KeyValuePair<bool, byte[]>(false, copy));
        }
    }

    public void SetFeature(byte[] report) {
        if (FailWrites || FailFeatures) {
            throw new IOException("simulated device feature-report failure");
        }

        lock (_lock) {
            var copy = (byte[])report.Clone();
            Features.Add(copy);
            Transfers.Add(new KeyValuePair<bool, byte[]>(true, copy));
        }
    }

    public byte[] GetInputReport(byte reportId, int length) {
        if (FailReads) {
            throw new IOException("simulated device read failure");
        }

        byte[] buffer;
        lock (_lock) {
            ReadCount++;
            buffer = new byte[length];
            Array.Copy(InputReport, buffer, Math.Min(InputReport.Length, length));
        }

        // Block outside the lock so a test holding a tick mid-read does not deadlock the fake's own
        // bookkeeping. The counters above already reflect that the read started before it blocks.
        BlockReadsUntil?.Wait();
        return buffer;
    }

    public byte[] Read(int length) {
        if (FailReads) {
            throw new IOException("simulated device read failure");
        }

        lock (_lock) {
            InterruptReadCount++;
            byte[] buffer = new byte[length];
            if (ReadReplies.Count > 0) {
                byte[] reply = ReadReplies.Dequeue();
                Array.Copy(reply, buffer, Math.Min(reply.Length, length));
            }

            return buffer;
        }
    }

    public void Clear() {
        lock (_lock) {
            Writes.Clear();
            Features.Clear();
            Transfers.Clear();
            ReadCount = 0;
            InterruptReadCount = 0;
        }
    }

    public void Dispose() => IsDisposed = true;
}
