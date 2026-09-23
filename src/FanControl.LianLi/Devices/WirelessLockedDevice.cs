using System;
using FanControl.LianLi.Protocol;

namespace FanControl.LianLi.Devices;

/// <summary>
/// One device of L-Connect's locked device list (<c>savedDevices.config</c>, written by
/// <c>RFController.LockDevice</c>): the device as L-Connect last read it, with the receiver slot and
/// the speeds it was driving it at. The list restores L-Connect's table as it was when locked.
/// </summary>
internal sealed class WirelessLockedDevice {
    /// <summary>A locked device: its <paramref name="record"/>, and the receiver slot and PWMs it was being driven at.</summary>
    public WirelessLockedDevice(WirelessDeviceRecord record, byte targetReceiverType, byte[] targetPwm) {
        Record = record ?? throw new ArgumentNullException(nameof(record));
        if (targetPwm is null) {
            throw new ArgumentNullException(nameof(targetPwm));
        }

        if (targetPwm.Length != WirelessProtocol.SlotsPerGroup) {
            throw new ArgumentException("A group has " + WirelessProtocol.SlotsPerGroup + " slots.", nameof(targetPwm));
        }

        TargetReceiverType = targetReceiverType;
        TargetPwm = (byte[])targetPwm.Clone();
    }

    /// <summary>The device as L-Connect last read it before the list was saved.</summary>
    public WirelessDeviceRecord Record { get; }

    /// <summary>L-Connect's <c>target_rx_type</c>: the receiver slot the device should be on.</summary>
    public byte TargetReceiverType { get; }

    /// <summary>L-Connect's <c>target_fans_pwm</c>: the speeds it was being driven at.</summary>
    public byte[] TargetPwm { get; }
}
