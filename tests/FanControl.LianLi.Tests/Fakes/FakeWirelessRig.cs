using System;
using System.Collections.Generic;
using System.Linq;

namespace FanControl.LianLi.Tests.Fakes;

/// <summary>
/// A transmitter and a receiver answering as a real L-Wireless pair would: the transmitter
/// answers the master query (0x11) with <see cref="MasterReply"/>, the receiver answers a list
/// request (0x10, pages in byte 1) with every record in <see cref="Records"/> that the requested
/// pages can carry, reporting <see cref="Total"/> (the record count unless set). Tests change the
/// records between calls to play out devices appearing, moving and going quiet.
/// </summary>
internal sealed class FakeWirelessRig {
    public static readonly byte[] MasterMac = { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };

    public FakeWirelessRig() {
        Transmitter.Responder = packet => packet[0] == 0x11 ? MasterReply() : null;
        Receiver.Responder = packet => packet[0] == 0x10 ? ListReply(packet[1]) : null;
    }

    public FakeWirelessDongle Transmitter { get; } = new FakeWirelessDongle();

    public FakeWirelessDongle Receiver { get; } = new FakeWirelessDongle();

    public byte[] Master { get; set; } = (byte[])MasterMac.Clone();

    /// <summary>The master clock in 0.625 ms ticks; 0 is a transmitter that has not started.</summary>
    public uint MasterClockTicks { get; set; } = 1600;

    /// <summary>When false the transmitter does not answer the query at all.</summary>
    public bool MasterAnswers { get; set; } = true;

    public List<FakeWirelessRecord> Records { get; } = new List<FakeWirelessRecord>();

    public int? Total { get; set; }

    /// <summary>When false the receiver answers with something that is not a list.</summary>
    public bool ReceiverAnswers { get; set; } = true;

    public byte[]? MasterReply() {
        if (!MasterAnswers) {
            return null;
        }

        var reply = new byte[64];
        reply[0] = 0x11;
        Array.Copy(Master, 0, reply, 1, 6);
        reply[7] = (byte)(MasterClockTicks >> 24);
        reply[8] = (byte)(MasterClockTicks >> 16);
        reply[9] = (byte)(MasterClockTicks >> 8);
        reply[10] = (byte)MasterClockTicks;
        reply[11] = 0x01;
        reply[12] = 0x02;
        return reply;
    }

    public byte[]? ListReply(int pages) {
        if (!ReceiverAnswers) {
            return new byte[] { 0x42 };
        }

        var reply = new byte[434 * Math.Max(pages, 1)];
        reply[0] = 0x10;
        reply[1] = (byte)(Total ?? Records.Count);
        byte[][] carried = Records.Take(pages * 10).Select(r => r.ToBytes()).ToArray();
        for (int i = 0; i < carried.Length; i++) {
            Array.Copy(carried[i], 0, reply, 4 + (i * 42), 42);
        }

        return reply;
    }

    /// <summary>Every RF payload (the 240 bytes after the four 64-byte packets' headers) written to the transmitter, with the header of its first packet.</summary>
    public List<(byte[] Header, byte[] Payload)> Payloads() {
        var payloads = new List<(byte[], byte[])>();
        List<byte[]> writes = Transmitter.Writes.Where(w => w[0] == 0x10).ToList();
        for (int i = 0; i + 3 < writes.Count; i += 4) {
            var payload = new byte[240];
            for (int chunk = 0; chunk < 4; chunk++) {
                Array.Copy(writes[i + chunk], 4, payload, chunk * 60, 60);
            }

            payloads.Add((writes[i][..4], payload));
        }

        return payloads;
    }
}
