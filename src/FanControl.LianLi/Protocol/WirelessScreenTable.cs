using System;

namespace FanControl.LianLi.Protocol;

/// <summary>
/// The per-receiver-slot screen table the clock broadcast carries: fourteen slots of twelve bytes,
/// one per receiver slot, laid out as <c>RFController.UpdateSensorDataByWiredLess</c> lays it out.
/// For the group on receiver slot <c>s</c> (1-13) at <c>(s - 1) * 12</c>: bytes 0-3 the theme index
/// of the screen on fan 0-3, bytes 4-7 that fan's direction in the top three bits and its data
/// source index in the rest, byte 8 the brightness, bytes 9-11 zero. An LCD FLEX group reads its
/// screens' theme, data source and brightness from here. Pure: it holds bytes and nothing else.
/// </summary>
internal sealed class WirelessScreenTable {
    /// <summary>The table is fourteen twelve-byte slots.</summary>
    public const int Length = SlotCount * SlotLength;

    // L-Connect fills the table with fourteen slots but assigns and accepts receiver slots 1-13
    // (MasterDevice.GetRxUnused, UpdateSensorDataByWiredLess's range check).
    private const int SlotCount = 14;
    private const int SlotLength = 12;
    private const int LowestReceiverType = 1;
    private const int HighestReceiverType = 13;

    // Within a slot: the four theme bytes, then the four direction/source bytes, then brightness.
    private const int DirectionOffset = 4;
    private const int BrightnessOffset = 8;
    private const int DirectionShift = 5;

    private readonly byte[] _bytes = new byte[Length];

    /// <summary>Whether <paramref name="receiverType"/> is a slot the table has a place for (1-13).</summary>
    public static bool HasSlot(int receiverType) => receiverType >= LowestReceiverType && receiverType <= HighestReceiverType;

    /// <summary>
    /// Write one screen's entry: the group on <paramref name="receiverType"/> (1-13), its fan at
    /// <paramref name="fanIndex"/> (0-3, in the table's own order), showing <paramref name="theme"/>
    /// from <paramref name="dataSource"/>, facing <paramref name="direction"/>, at
    /// <paramref name="brightness"/>. The brightness is the group's, so the last fan written sets it.
    /// </summary>
    public void SetFan(int receiverType, int fanIndex, byte theme, byte direction, byte dataSource, byte brightness) {
        if (!HasSlot(receiverType)) {
            throw new ArgumentOutOfRangeException(nameof(receiverType), "The table has slots " + LowestReceiverType + "-" + HighestReceiverType + ".");
        }

        if (fanIndex < 0 || fanIndex >= WirelessProtocol.SlotsPerGroup) {
            throw new ArgumentOutOfRangeException(nameof(fanIndex), "A group has fans 0-" + (WirelessProtocol.SlotsPerGroup - 1) + ".");
        }

        int slot = (receiverType - LowestReceiverType) * SlotLength;
        _bytes[slot + fanIndex] = theme;
        _bytes[slot + DirectionOffset + fanIndex] = unchecked((byte)((direction << DirectionShift) | dataSource));
        _bytes[slot + BrightnessOffset] = brightness;
    }

    /// <summary>
    /// The twelve bytes of the entry for the group on <paramref name="receiverType"/> (1-13), copied:
    /// what a clock broadcast carrying this table tells that group's screens.
    /// </summary>
    public byte[] EntryOf(int receiverType) {
        if (!HasSlot(receiverType)) {
            throw new ArgumentOutOfRangeException(nameof(receiverType), "The table has slots " + LowestReceiverType + "-" + HighestReceiverType + ".");
        }

        var entry = new byte[SlotLength];
        Array.Copy(_bytes, (receiverType - LowestReceiverType) * SlotLength, entry, 0, SlotLength);
        return entry;
    }

    /// <summary>The table's bytes, copied.</summary>
    public byte[] ToBytes() => (byte[])_bytes.Clone();
}
