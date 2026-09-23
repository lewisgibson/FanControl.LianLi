using System;

namespace FanControl.LianLi.Tests.Fakes;

/// <summary>
/// One 42-byte receiver list record, laid out byte by byte as the record table in
/// docs/wireless.md (and MasterDevice.RefreshList) gives it. Every field is settable; the defaults
/// are a bound, idle, empty fan group.
/// </summary>
internal sealed class FakeWirelessRecord {
    public FakeWirelessRecord(byte[] mac, byte[] master) {
        Mac = mac;
        Master = master;
    }

    public byte[] Mac { get; set; }

    public byte[] Master { get; set; }

    public byte Channel { get; set; } = 8;

    public byte Receiver { get; set; } = 1;

    /// <summary>The device clock in 0.625 ms ticks, bytes 14-17.</summary>
    public uint ClockTicks { get; set; }

    public byte DeviceType { get; set; }

    /// <summary>Byte 19 as sent: ten or more flags a right-attached end cap.</summary>
    public byte FanCountByte { get; set; }

    public byte[] EffectIndex { get; set; } = new byte[4];

    public byte[] FanTypes { get; set; } = new byte[4];

    public int[] Rpm { get; set; } = new int[4];

    public byte[] Pwm { get; set; } = new byte[4];

    public byte Sequence { get; set; }

    public byte Marker { get; set; } = 0x1C;

    public byte[] ToBytes() {
        var record = new byte[42];
        Array.Copy(Mac, 0, record, 0, 6);
        Array.Copy(Master, 0, record, 6, 6);
        record[12] = Channel;
        record[13] = Receiver;
        record[14] = (byte)(ClockTicks >> 24);
        record[15] = (byte)(ClockTicks >> 16);
        record[16] = (byte)(ClockTicks >> 8);
        record[17] = (byte)ClockTicks;
        record[18] = DeviceType;
        record[19] = FanCountByte;
        Array.Copy(EffectIndex, 0, record, 20, 4);
        Array.Copy(FanTypes, 0, record, 24, 4);
        for (int slot = 0; slot < 4; slot++) {
            record[28 + (slot * 2)] = (byte)(Rpm[slot] >> 8);
            record[29 + (slot * 2)] = (byte)Rpm[slot];
        }

        Array.Copy(Pwm, 0, record, 36, 4);
        record[40] = Sequence;
        record[41] = Marker;
        return record;
    }
}
