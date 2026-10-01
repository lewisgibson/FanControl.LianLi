using System;
using FanControl.LianLi.Devices;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

/// <summary>
/// What an LCD FLEX group's screens were last told by the clock broadcast, and whether that is
/// still the entry they need from the transmitter they have now.
/// </summary>
public sealed class WirelessScreenEntryPublicationTests {
    private static readonly DateTime Completed = new DateTime(2026, 9, 30, 20, 0, 0, DateTimeKind.Utc);

    // The same twelve bytes on the same receiver slot, under the same transmitter lifetime and
    // handle, are the published entry; anything else is an entry the screens have not had for as
    // long as the publication says.
    [Fact]
    public void Carries_TheSameEntryOnTheSameSlot_UnderTheSameLifetimeAndHandle() {
        var entry = new byte[] { 5, 6, 0, 0, 0x20, 0x20, 0, 0, 60, 0, 0, 0 };
        var publication = new WirelessScreenEntryPublication(3, entry, 1, 2, Completed);

        Assert.Equal(Completed, publication.CompletedUtc);
        Assert.True(publication.Carries(3, (byte[])entry.Clone(), 1, 2));
        Assert.False(publication.Carries(4, entry, 1, 2)); // another slot
        Assert.False(publication.Carries(3, entry, 2, 2)); // a transmitter lifetime counted since
        Assert.False(publication.Carries(3, entry, 1, 3)); // the transmitter's handle lost since
        var renumbered = (byte[])entry.Clone();
        renumbered[0] = 6;
        renumbered[1] = 5;
        Assert.False(publication.Carries(3, renumbered, 1, 2)); // the screens renumbered
        Assert.False(publication.Carries(3, new byte[] { 5, 6 }, 1, 2)); // not an entry at all
        Assert.Throws<ArgumentNullException>(() => publication.Carries(3, null!, 1, 2));
    }

    [Fact]
    public void TheEntry_IsCopied() {
        var entry = new byte[12];
        entry[0] = 5;
        var publication = new WirelessScreenEntryPublication(3, entry, 0, 0, Completed);

        entry[0] = 6;

        Assert.True(publication.Carries(3, new byte[] { 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 0, 0));
        Assert.False(publication.Carries(3, entry, 0, 0));
        Assert.Throws<ArgumentNullException>(() => new WirelessScreenEntryPublication(3, null!, 0, 0, Completed));
    }
}
