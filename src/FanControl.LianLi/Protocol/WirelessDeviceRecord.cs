using System;
using System.Globalization;
using System.Text;

namespace FanControl.LianLi.Protocol;

/// <summary>
/// One 42-byte record from the L-Wireless receiver dongle's device list: a fan group (or another
/// wireless product, or a master dongle) the receiver can hear, with the identity the RF commands
/// address it by and its live telemetry, decoded the way L-Connect's <c>MasterDevice.RefreshList</c>
/// reads it. Produced by <see cref="WirelessProtocol.DecodeDeviceList"/>.
/// </summary>
internal sealed class WirelessDeviceRecord {
    /// <summary>Create a record; every array is copied so the record owns its bytes.</summary>
    public WirelessDeviceRecord(
        byte[] mac,
        byte[] masterMac,
        byte channel,
        byte receiverType,
        long clockMilliseconds,
        byte deviceType,
        int fanCount,
        byte[] effectIndex,
        byte[] fanTypes,
        int[] rpm,
        byte[] pwm,
        byte commandSequence) {
        Mac = Copy(mac, WirelessProtocol.MacLength, nameof(mac));
        MasterMac = Copy(masterMac, WirelessProtocol.MacLength, nameof(masterMac));
        Channel = channel;
        ReceiverType = receiverType;
        ClockMilliseconds = clockMilliseconds;
        DeviceType = deviceType;
        if (fanCount < 0 || fanCount > WirelessProtocol.SlotsPerGroup) {
            throw new ArgumentOutOfRangeException(nameof(fanCount), "A device has 0-" + WirelessProtocol.SlotsPerGroup + " fans.");
        }

        FanCount = fanCount;
        EffectIndex = Copy(effectIndex, WirelessProtocol.EffectIndexLength, nameof(effectIndex));
        FanTypes = Copy(fanTypes, WirelessProtocol.SlotsPerGroup, nameof(fanTypes));
        if (rpm is null) {
            throw new ArgumentNullException(nameof(rpm));
        }

        if (rpm.Length != WirelessProtocol.SlotsPerGroup) {
            throw new ArgumentException("Expected " + WirelessProtocol.SlotsPerGroup + " readings.", nameof(rpm));
        }

        Rpm = (int[])rpm.Clone();
        Pwm = Copy(pwm, WirelessProtocol.SlotsPerGroup, nameof(pwm));
        CommandSequence = commandSequence;
        MacText = FormatMac(Mac);
        Kind = KindOf(deviceType);
    }

    /// <summary>The device's 6-byte RF address; the key every command to it carries.</summary>
    public byte[] Mac { get; }

    /// <summary>The RF address of the master (transmitter dongle) the device is bound to; all zero when unbound.</summary>
    public byte[] MasterMac { get; }

    /// <summary>The RF channel the device is on.</summary>
    public byte Channel { get; }

    /// <summary>The receiver slot (1-15) the master assigned the device when it bound it.</summary>
    public byte ReceiverType { get; }

    /// <summary>The device's clock in milliseconds, which L-Connect compares against the master's.</summary>
    public long ClockMilliseconds { get; }

    /// <summary>The raw product type byte; see <see cref="Kind"/>.</summary>
    public byte DeviceType { get; }

    /// <summary>What the device is.</summary>
    public WirelessDeviceKind Kind { get; }

    /// <summary>How many fans the device has, 0-4.</summary>
    public int FanCount { get; }

    /// <summary>
    /// The 4-byte identity of the lighting effect the device is currently running - the timestamp
    /// L-Connect stamped the effect with when it rendered it. Compared against the saved effect's
    /// index to decide whether the saved look needs sending again.
    /// </summary>
    public byte[] EffectIndex { get; }

    /// <summary>The per-slot fan type code (see <see cref="WirelessProtocol.FamilyOf"/>); 0 for an empty slot. On a water block, slot 3 carries the coolant temperature instead.</summary>
    public byte[] FanTypes { get; }

    /// <summary>Each slot's measured RPM.</summary>
    public int[] Rpm { get; }

    /// <summary>Each slot's current PWM on the device's 0-255 scale, as L-Connect reads it.</summary>
    public byte[] Pwm { get; }

    /// <summary>The command sequence number the device last acknowledged.</summary>
    public byte CommandSequence { get; }

    /// <summary>The address as twelve lowercase hex digits, no separators - stable across runs, so it keys the sensor ids.</summary>
    public string MacText { get; }

    /// <summary>Whether the record names no master at all (the all-zero address).</summary>
    public bool IsUnbound {
        get {
            foreach (byte b in MasterMac) {
                if (b != 0) {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// This record as L-Connect reads it while its device list is locked (<c>RefreshList</c> under
    /// <c>lock_list</c>): the product type stays <paramref name="locked"/>'s, and so do the fan types,
    /// except on a water block or a case (types 10, 11, 65 and 66), whose slots carry what is fitted
    /// and the coolant temperature and so are always read.
    /// </summary>
    public WirelessDeviceRecord WithLockedIdentity(WirelessDeviceRecord locked) {
        if (locked is null) {
            throw new ArgumentNullException(nameof(locked));
        }

        bool liveFanTypes = locked.DeviceType == WirelessProtocol.WaterBlockDeviceType
            || locked.DeviceType == WirelessProtocol.WaterBlock2DeviceType
            || locked.DeviceType == WirelessProtocol.CaseFanDeviceType
            || locked.DeviceType == WirelessProtocol.V150DeviceType;
        return new WirelessDeviceRecord(
            Mac,
            MasterMac,
            Channel,
            ReceiverType,
            ClockMilliseconds,
            locked.DeviceType,
            FanCount,
            EffectIndex,
            liveFanTypes ? FanTypes : locked.FanTypes,
            Rpm,
            Pwm,
            CommandSequence);
    }

    /// <summary>Whether the device's current effect is the one with the given index.</summary>
    public bool IsRunningEffect(byte[] effectIndex) => SameBytes(EffectIndex, effectIndex, nameof(effectIndex));

    /// <summary>Whether the device is bound to the master with the given address.</summary>
    public bool IsBoundTo(byte[] masterMac) => SameBytes(MasterMac, masterMac, nameof(masterMac));

    /// <summary>Format a 6-byte address as twelve lowercase hex digits.</summary>
    public static string FormatMac(byte[] mac) {
        if (mac is null) {
            throw new ArgumentNullException(nameof(mac));
        }

        var text = new StringBuilder(mac.Length * 2);
        for (int i = 0; i < mac.Length; i++) {
            text.Append(mac[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    // RfDevice.InitAttr: 1-9 Strimer, 10 WaterBlock, 11 WaterBlock2, 65 LC217, 66 V150; a master
    // dongle (255) goes to MasterDevice.masterList instead of the device list.
    private static WirelessDeviceKind KindOf(byte deviceType) {
        if (deviceType == WirelessProtocol.FanGroupDeviceType) {
            return WirelessDeviceKind.FanGroup;
        }

        if (deviceType >= 1 && deviceType <= 9) {
            return WirelessDeviceKind.Strimer;
        }

        switch (deviceType) {
            case WirelessProtocol.WaterBlockDeviceType:
            case WirelessProtocol.WaterBlock2DeviceType:
                return WirelessDeviceKind.WaterBlock;
            case WirelessProtocol.CaseFanDeviceType:
                return WirelessDeviceKind.CaseFans;
            case WirelessProtocol.V150DeviceType:
                return WirelessDeviceKind.V150;
            case WirelessProtocol.MasterDeviceType:
                return WirelessDeviceKind.Master;
            default:
                return WirelessDeviceKind.Unrecognised;
        }
    }

    private static bool SameBytes(byte[] mine, byte[] other, string name) {
        if (other is null) {
            throw new ArgumentNullException(name);
        }

        if (other.Length != mine.Length) {
            return false;
        }

        for (int i = 0; i < mine.Length; i++) {
            if (mine[i] != other[i]) {
                return false;
            }
        }

        return true;
    }

    private static byte[] Copy(byte[] source, int expectedLength, string name) {
        if (source is null) {
            throw new ArgumentNullException(name);
        }

        if (source.Length != expectedLength) {
            throw new ArgumentException("Expected " + expectedLength + " bytes.", name);
        }

        return (byte[])source.Clone();
    }
}
