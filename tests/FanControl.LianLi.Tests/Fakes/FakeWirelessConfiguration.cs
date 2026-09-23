using System.Collections.Generic;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Protocol;

namespace FanControl.LianLi.Tests.Fakes;

internal sealed class FakeWirelessConfiguration : IWirelessConfigurationSource {
    /// <summary>Saved channels by master address; a master not listed has none.</summary>
    public Dictionary<string, int> Channels { get; } = new Dictionary<string, int>();

    /// <summary>The locked device list per master, as <see cref="FindLockedDevices"/> returns it.</summary>
    public Dictionary<string, IReadOnlyList<WirelessLockedDevice>> LockedDevices { get; } = new Dictionary<string, IReadOnlyList<WirelessLockedDevice>>();

    /// <summary>Saved effects by RF address; a device not listed has none.</summary>
    public Dictionary<string, WirelessSavedEffect> Effects { get; } = new Dictionary<string, WirelessSavedEffect>();

    /// <summary>Saved screen presentations by RF address; a device not listed falls back to the default.</summary>
    public Dictionary<string, WirelessAioPresentation> Presentations { get; } =
        new Dictionary<string, WirelessAioPresentation>();

    /// <summary>Every address an effect was looked up for, in order.</summary>
    public List<string> EffectLookups { get; } = new List<string>();

    public int? FindChannel(string masterMacText)
        => Channels.TryGetValue(masterMacText, out int channel) ? channel : null;

    public WirelessSavedEffect? FindEffect(string macText) {
        EffectLookups.Add(macText);
        return Effects.TryGetValue(macText, out WirelessSavedEffect? effect) ? effect : null;
    }

    public WirelessAioPresentation? FindPumpPresentation(string macText)
        => Presentations.TryGetValue(macText, out WirelessAioPresentation? presentation) ? presentation : null;

    public IReadOnlyList<WirelessLockedDevice>? FindLockedDevices(string masterMacText)
        => LockedDevices.TryGetValue(masterMacText, out IReadOnlyList<WirelessLockedDevice>? locked) ? locked : null;
}
