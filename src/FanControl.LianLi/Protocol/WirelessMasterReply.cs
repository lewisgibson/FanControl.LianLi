using System;

namespace FanControl.LianLi.Protocol;

/// <summary>
/// The transmitter's answer to the master query (see <see cref="WirelessProtocol.DecodeMasterQuery"/>):
/// the master's RF address and its clock. L-Connect's <c>MasterDevice.QuerryMasterMac</c> takes the
/// clock from every reply that echoes the command, treats a reply whose clock is still zero as "not
/// started yet" and leaves the address it knew, and takes the address only from a reply whose clock
/// runs - an all-zero address there means the master is unknown until the next reply.
/// </summary>
internal sealed class WirelessMasterReply {
    /// <summary>Create a reply; the address is copied.</summary>
    public WirelessMasterReply(byte[] mac, int clockMilliseconds) {
        if (mac is null) {
            throw new ArgumentNullException(nameof(mac));
        }

        if (mac.Length != WirelessProtocol.MacLength) {
            throw new ArgumentException("An RF address is " + WirelessProtocol.MacLength + " bytes.", nameof(mac));
        }

        Mac = (byte[])mac.Clone();
        ClockMilliseconds = clockMilliseconds;
    }

    /// <summary>The master's 6-byte RF address as the reply carried it.</summary>
    public byte[] Mac { get; }

    /// <summary>The master's clock in milliseconds (the reply's 0.625 ms ticks, truncated as L-Connect does).</summary>
    public int ClockMilliseconds { get; }

    /// <summary>Whether the clock has started; a reply before it has carries no usable address.</summary>
    public bool IsClockRunning => ClockMilliseconds != 0;

    /// <summary>Whether the address is anything but all zero.</summary>
    public bool HasAddress {
        get {
            foreach (byte b in Mac) {
                if (b != 0) {
                    return true;
                }
            }

            return false;
        }
    }
}
