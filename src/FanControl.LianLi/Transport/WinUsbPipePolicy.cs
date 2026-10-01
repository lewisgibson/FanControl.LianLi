using FanControl.LianLi.Protocol;

namespace FanControl.LianLi.Transport;

/// <summary>
/// How <see cref="WinUsbTransport"/> times its pipes and reads a reply for one kind of WinUSB
/// device, taken from the timing L-Connect gives that device. The L-Wireless dongles answer from
/// their own buffer within a few milliseconds with as many packets as they have queued, so a read
/// gathers packets until one does not arrive (<c>WinUsb.ReadAll</c>); the HydroShift II OLED
/// Curve's pump MCU and a FLEX receiver answer every command with exactly one packet
/// (<c>WinUsbHS2.SendAndReadLed</c> and <c>WinUsbLed.SendAndRead</c>, each one write then one
/// read), so a read takes that packet and does not wait for a second that never comes.
/// </summary>
internal sealed class WinUsbPipePolicy {
    // L-Connect gives a dongle packet write 100 ms (WinUsb.RfSend: writer.Write(bytes, 100, ...)); a
    // healthy dongle takes it in under a millisecond. Each IN transfer is given 50 ms: L-Connect uses
    // 10 ms per packet (WinUsb.ReadAll), and this is more generous so a busy host does not truncate
    // a multi-packet list reply.
    private const uint DongleWriteTimeoutMilliseconds = 100;
    private const uint DongleReadTimeoutMilliseconds = 50;

    // WinUsbHS2.SendAndReadLed: writer.Write(bytes, 200), then WinUsb.Read's reader.Read(buffer, 200).
    private const uint PumpWriteTimeoutMilliseconds = 200;
    private const uint PumpReadTimeoutMilliseconds = 200;

    // WinUsbLed.SendAndRead: writer.Write(sendBytes, 2000), since the receiver has to gather the
    // chain's RF state to answer, then Led_Read with its default 100 ms.
    private const uint FlexReceiverWriteTimeoutMilliseconds = 2000;
    private const uint FlexReceiverReadTimeoutMilliseconds = 100;

    private WinUsbPipePolicy(uint writeTimeoutMilliseconds, uint readTimeoutMilliseconds, bool readsWholeReply) {
        WriteTimeoutMilliseconds = writeTimeoutMilliseconds;
        ReadTimeoutMilliseconds = readTimeoutMilliseconds;
        ReadsWholeReply = readsWholeReply;
    }

    /// <summary>The L-Wireless dongles: 100 ms per write, 50 ms per packet, a reply read to its end.</summary>
    public static WinUsbPipePolicy Dongle { get; } =
        new WinUsbPipePolicy(DongleWriteTimeoutMilliseconds, DongleReadTimeoutMilliseconds, readsWholeReply: true);

    /// <summary>The HydroShift II OLED Curve's pump MCU: 200 ms per write, 200 ms for its one reply packet.</summary>
    public static WinUsbPipePolicy PumpMcu { get; } =
        new WinUsbPipePolicy(PumpWriteTimeoutMilliseconds, PumpReadTimeoutMilliseconds, readsWholeReply: false);

    /// <summary>A FLEX or P28 V2 chain's USB receiver: 2000 ms per write, 100 ms for its one reply packet.</summary>
    public static WinUsbPipePolicy FlexReceiver { get; } =
        new WinUsbPipePolicy(FlexReceiverWriteTimeoutMilliseconds, FlexReceiverReadTimeoutMilliseconds, readsWholeReply: false);

    /// <summary>The OUT pipe's transfer timeout.</summary>
    public uint WriteTimeoutMilliseconds { get; }

    /// <summary>The IN pipe's transfer timeout: how long one packet is waited for.</summary>
    public uint ReadTimeoutMilliseconds { get; }

    /// <summary>
    /// Whether a read gathers packets until the device stops sending (the dongles, whose reply ends
    /// with the first packet that does not arrive), or takes the packets asked for and no more (a
    /// device that answers every command with exactly one, where waiting for another would cost a
    /// whole timeout on every exchange).
    /// </summary>
    public bool ReadsWholeReply { get; }

    /// <summary>The policy for the WinUSB device with the given ids: the dongles' for anything that is not the pump MCU or a FLEX receiver.</summary>
    public static WinUsbPipePolicy For(int vendorId, int productId)
        => HydroShiftCurveProtocol.IsPump(vendorId, productId) ? PumpMcu
            : FlexReceiverProtocol.IsReceiver(vendorId, productId) ? FlexReceiver
            : Dongle;
}
