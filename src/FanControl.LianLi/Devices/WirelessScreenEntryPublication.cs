using System;

namespace FanControl.LianLi.Devices;

/// <summary>
/// The screen table entry a clock broadcast carried for one LCD FLEX group, and when that broadcast
/// completed: the plugin's record of the first half of <c>RFController.UpdateSensorSettingByWiredLess</c>,
/// which writes a screen's table entry, broadcasts the clock that carries it, sleeps 1200 ms once
/// the broadcast has returned, and only then queues the screen's colours. The group's colours may
/// begin 1200 ms after <see cref="CompletedUtc"/>, and only while this is still the entry the
/// group needs on the transmitter it has now (<see cref="Carries"/>): the same twelve bytes on the
/// same receiver slot, under the same transmitter lifetime and handle, since a rewritten or moved
/// entry, or a transmitter lost since, is one the screens have not had for that long. Immutable;
/// the device keeps the current one (<see cref="WirelessDevice.ScreenEntryPublication"/>).
/// </summary>
internal sealed class WirelessScreenEntryPublication {
    private readonly int _receiverType;
    private readonly byte[] _entry;
    private readonly int _transmitterLifetime;
    private readonly int _transmitterGeneration;

    /// <summary>
    /// A broadcast carrying <paramref name="entry"/> for the group on <paramref name="receiverType"/>
    /// completed at <paramref name="completedUtc"/>, under <paramref name="transmitterLifetime"/> on
    /// the transmitter handle of <paramref name="transmitterGeneration"/>. The entry is copied.
    /// </summary>
    public WirelessScreenEntryPublication(int receiverType, byte[] entry, int transmitterLifetime, int transmitterGeneration, DateTime completedUtc) {
        if (entry is null) {
            throw new ArgumentNullException(nameof(entry));
        }

        _receiverType = receiverType;
        _entry = (byte[])entry.Clone();
        _transmitterLifetime = transmitterLifetime;
        _transmitterGeneration = transmitterGeneration;
        CompletedUtc = completedUtc;
    }

    /// <summary>When the broadcast that carried the entry completed, by the injected clock read after its last packet was written.</summary>
    public DateTime CompletedUtc { get; }

    /// <summary>
    /// Whether this is the publication of <paramref name="entry"/> on <paramref name="receiverType"/>
    /// under <paramref name="transmitterLifetime"/> and the transmitter handle of
    /// <paramref name="transmitterGeneration"/>: the entry the group needs now went out, on the
    /// transmitter as it is now.
    /// </summary>
    public bool Carries(int receiverType, byte[] entry, int transmitterLifetime, int transmitterGeneration) {
        if (entry is null) {
            throw new ArgumentNullException(nameof(entry));
        }

        if (receiverType != _receiverType
            || transmitterLifetime != _transmitterLifetime
            || transmitterGeneration != _transmitterGeneration
            || entry.Length != _entry.Length) {
            return false;
        }

        for (int i = 0; i < _entry.Length; i++) {
            if (entry[i] != _entry[i]) {
                return false;
            }
        }

        return true;
    }
}
