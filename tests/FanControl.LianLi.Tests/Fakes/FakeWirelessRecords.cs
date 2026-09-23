using System;
using FanControl.LianLi.Protocol;

namespace FanControl.LianLi.Tests.Fakes;

/// <summary>Decoded records and lists built through the real decoder from <see cref="FakeWirelessRecord"/> bytes.</summary>
internal static class FakeWirelessRecords {
    public static WirelessDeviceRecord Decode(FakeWirelessRecord record) => List(record).Records[0];

    public static WirelessDeviceList List(params FakeWirelessRecord[] records) => List(records.Length, records);

    public static WirelessDeviceList List(int total, params FakeWirelessRecord[] records) {
        int pages = Math.Max(1, WirelessProtocol.PagesFor(records.Length));
        var reply = new byte[434 * pages];
        reply[0] = 0x10;
        reply[1] = (byte)total;
        for (int i = 0; i < records.Length; i++) {
            Array.Copy(records[i].ToBytes(), 0, reply, 4 + (i * 42), 42);
        }

        return WirelessProtocol.DecodeDeviceList(reply, pages)!;
    }
}
