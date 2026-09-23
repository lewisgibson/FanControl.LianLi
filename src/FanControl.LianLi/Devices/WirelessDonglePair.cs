using System;
using System.Globalization;
using FanControl.LianLi.Logging;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Transport;

namespace FanControl.LianLi.Devices;

/// <summary>
/// The two dongles of one L-Wireless master, and the recovery L-Connect runs across them. Every
/// write to either dongle goes through here so that consecutive failures are counted per dongle the
/// way L-Connect's <c>WinUsb.RfSend</c> counts them: after five failed writes in a row to one dongle
/// it resets that dongle by sending command 0x15 through the <em>other</em> one
/// (<c>RFController.ResetTx</c> sends it through the receiver, <c>ResetRx</c> through the
/// transmitter). Reopening a faulted handle is the transport's own job; this only adds the reset.
/// Reads are not counted: L-Connect's <c>RfRead</c> counts only reads it could not start for want of
/// an open handle, and every cycle writes to both dongles before it reads, so a dongle that has
/// gone is already being counted through its writes.
/// </summary>
internal sealed class WirelessDonglePair : IDisposable {
    // WinUsb.RfSend: iSendErr >= 5 resets the dongle and starts the count again.
    private const int FailuresBeforeReset = 5;

    private readonly IDeviceTransport _transmitter;
    private readonly IDeviceTransport _receiver;
    private readonly int _index;
    private readonly ILog _log;

    private int _transmitterFailures;
    private int _receiverFailures;
    private bool _disposed;

    /// <summary>Own both dongles of controller <paramref name="index"/>; <see cref="Dispose"/> releases both.</summary>
    public WirelessDonglePair(IDeviceTransport transmitter, IDeviceTransport receiver, int index, ILog log) {
        _transmitter = transmitter ?? throw new ArgumentNullException(nameof(transmitter));
        _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        _index = index;
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>How many times the transmitter's transport has reopened its handle.</summary>
    public int TransmitterGeneration => _transmitter.Generation;

    /// <summary>How many times the receiver's transport has reopened its handle.</summary>
    public int ReceiverGeneration => _receiver.Generation;

    /// <summary>Write one packet to the transmitter. A failure is counted, then rethrown.</summary>
    public void WriteTransmitter(byte[] packet) {
        try {
            _transmitter.Write(packet);
            _transmitterFailures = 0;
        } catch {
            if (++_transmitterFailures >= FailuresBeforeReset) {
                _transmitterFailures = 0;
                Reset("transmitter", "receiver", WriteReceiver);
            }

            throw;
        }
    }

    /// <summary>Write one packet to the receiver. A failure is counted, then rethrown.</summary>
    public void WriteReceiver(byte[] packet) {
        try {
            _receiver.Write(packet);
            _receiverFailures = 0;
        } catch {
            if (++_receiverFailures >= FailuresBeforeReset) {
                _receiverFailures = 0;
                Reset("receiver", "transmitter", WriteTransmitter);
            }

            throw;
        }
    }

    /// <summary>Read a reply of <paramref name="length"/> bytes from the transmitter.</summary>
    public byte[] ReadTransmitter(int length) => _transmitter.Read(length);

    /// <summary>Read a reply of <paramref name="length"/> bytes from the receiver.</summary>
    public byte[] ReadReceiver(int length) => _receiver.Read(length);

    /// <summary>Release both dongles; safe to call more than once.</summary>
    public void Dispose() {
        if (_disposed) {
            return;
        }

        _disposed = true;
        _transmitter.Dispose();
        _receiver.Dispose();
    }

    // The reset goes out as an ordinary write to the partner, so it is itself counted against the
    // partner, as L-Connect's reset is an RfSend. Its own failure is logged rather than thrown: the
    // caller is already failing with the write that triggered it, and that is the error to report.
    private void Reset(string failing, string through, Action<byte[]> writeThrough) {
        try {
            writeThrough(WirelessProtocol.EncodeDongleReset());
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "W{0}: {1} failed {2} writes in a row; reset it through the {3}",
                _index,
                failing,
                FailuresBeforeReset,
                through));
        }
#pragma warning disable CA1031 // resilience: the reset is best effort and logged; the write that triggered it is rethrown by the caller
        catch (Exception ex) {
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "W{0}: {1} failed {2} writes in a row; resetting it through the {3} failed too: {4}",
                _index,
                failing,
                FailuresBeforeReset,
                through,
                ex.Message));
        }
#pragma warning restore CA1031
    }
}
