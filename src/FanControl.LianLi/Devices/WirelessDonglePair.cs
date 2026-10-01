using System;
using System.Globalization;
using FanControl.LianLi.Logging;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Transport;

namespace FanControl.LianLi.Devices;

/// <summary>
/// The two dongles of one L-Wireless master, and the recovery L-Connect runs across them. Every
/// write to and read from either dongle goes through here so that consecutive failures are counted
/// the way L-Connect's <c>WinUsb</c> counts them: after five failed writes in a row to one dongle
/// (<c>RfSend</c>, whose <c>iSendErr</c> is per dongle), or five reads in a row answered with
/// nothing (<c>RfRead</c>: a reply whose first byte is zero, which is what a dongle that does not
/// answer within the pipe timeout gives; its <c>iReadErr</c> is one static count shared by both
/// dongles, so the fifth empty read resets whichever dongle it was read from, whatever the four
/// before were), it resets that dongle by sending command 0x15 through the <em>other</em> one
/// (<c>RFController.ResetTx</c> sends it through the receiver, <c>ResetRx</c> through the
/// transmitter). Writes and reads are counted apart, as L-Connect keeps <c>iSendErr</c> and
/// <c>iReadErr</c> apart, and each count restarts on a success of its own kind and after its
/// reset. Reopening a faulted handle is the transport's own job; this only adds the reset.
/// </summary>
internal sealed class WirelessDonglePair : IDisposable {
    // WinUsb.RfSend: iSendErr >= 5 resets the dongle and starts the count again; RfRead does the
    // same with iReadErr.
    private const int FailuresBeforeReset = 5;

    private readonly Dongle _transmitter;
    private readonly Dongle _receiver;
    private readonly int _index;
    private readonly ILog _log;
    private bool _disposed;

    // WinUsb.iReadErr: static in L-Connect, so one count across both dongles.
    private int _emptyReads;

    /// <summary>Own both dongles of controller <paramref name="index"/>; <see cref="Dispose"/> releases both.</summary>
    public WirelessDonglePair(IDeviceTransport transmitter, IDeviceTransport receiver, int index, ILog log) {
        _transmitter = new Dongle(transmitter ?? throw new ArgumentNullException(nameof(transmitter)), "transmitter");
        _receiver = new Dongle(receiver ?? throw new ArgumentNullException(nameof(receiver)), "receiver");
        _index = index;
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>How many times the transmitter's transport has lost its handle.</summary>
    public int TransmitterGeneration => _transmitter.Transport.Generation;

    /// <summary>How many times the receiver's transport has lost its handle.</summary>
    public int ReceiverGeneration => _receiver.Transport.Generation;

    /// <summary>Whether either dongle's handle is known not to reach it at the moment (see <see cref="IDeviceTransport.IsFaulted"/>).</summary>
    public bool IsFaulted => _transmitter.Transport.IsFaulted || _receiver.Transport.IsFaulted;

    /// <summary>Whether the transmitter's handle is known not to reach it at the moment: nothing written reaches the air.</summary>
    public bool IsTransmitterFaulted => _transmitter.Transport.IsFaulted;

    /// <summary>Write one packet to the transmitter. A failure is counted, then rethrown.</summary>
    public void WriteTransmitter(byte[] packet) => Write(_transmitter, _receiver, packet);

    /// <summary>Write one packet to the receiver. A failure is counted, then rethrown.</summary>
    public void WriteReceiver(byte[] packet) => Write(_receiver, _transmitter, packet);

    /// <summary>Read a reply of <paramref name="length"/> bytes from the transmitter. An empty reply, or a failure, is counted.</summary>
    public byte[] ReadTransmitter(int length) => Read(_transmitter, _receiver, length);

    /// <summary>Read a reply of <paramref name="length"/> bytes from the receiver. An empty reply, or a failure, is counted.</summary>
    public byte[] ReadReceiver(int length) => Read(_receiver, _transmitter, length);

    /// <summary>Release both dongles; safe to call more than once.</summary>
    public void Dispose() {
        if (_disposed) {
            return;
        }

        _disposed = true;
        _transmitter.Transport.Dispose();
        _receiver.Transport.Dispose();
    }

    private void Write(Dongle dongle, Dongle partner, byte[] packet) {
        try {
            dongle.Transport.Write(packet);
            dongle.WriteFailures = 0;
        } catch {
            if (++dongle.WriteFailures >= FailuresBeforeReset) {
                dongle.WriteFailures = 0;
                Reset(dongle, partner, "failed " + FailuresBeforeReset + " writes in a row");
            }

            throw;
        }
    }

    // RfRead: a reply is empty when its first byte is zero, and a read that throws counts as empty
    // too, since ReadAll returns what it got - zeros - for one that failed part way. The count is
    // the pair's, and the dongle reset is the one this read was from.
    private byte[] Read(Dongle dongle, Dongle partner, int length) {
        byte[] reply;
        try {
            reply = dongle.Transport.Read(length);
        } catch {
            CountEmptyRead(dongle, partner);
            throw;
        }

        if (reply.Length == 0 || reply[0] == 0) {
            CountEmptyRead(dongle, partner);
        } else {
            _emptyReads = 0;
        }

        return reply;
    }

    private void CountEmptyRead(Dongle dongle, Dongle partner) {
        if (++_emptyReads >= FailuresBeforeReset) {
            _emptyReads = 0;
            Reset(dongle, partner, "read nothing for the " + FailuresBeforeReset + "th time in a row across both dongles");
        }
    }

    // The reset goes out as an ordinary write to the partner, so it is itself counted against the
    // partner, as L-Connect's reset is an RfSend. Its own failure is logged, not thrown: the
    // caller is already failing with the write that triggered it, or reading nothing, and that is
    // the error to report.
    private void Reset(Dongle failing, Dongle through, string why) {
        try {
            Write(through, failing, WirelessProtocol.EncodeDongleReset());
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "W{0}: {1} {2}; reset it through the {3}",
                _index,
                failing.Name,
                why,
                through.Name));
        }
#pragma warning disable CA1031 // resilience: the reset is tried once and logged; the failure that triggered it is what the caller reports
        catch (Exception ex) {
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "W{0}: {1} {2}; resetting it through the {3} failed too: {4}",
                _index,
                failing.Name,
                why,
                through.Name,
                ex.Message));
        }
#pragma warning restore CA1031
    }

    // One dongle with L-Connect's per-dongle write failure count.
    private sealed class Dongle {
        public Dongle(IDeviceTransport transport, string name) {
            Transport = transport;
            Name = name;
        }

        public IDeviceTransport Transport { get; }

        public string Name { get; }

        public int WriteFailures { get; set; }
    }
}
