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

    /// <summary>Saved LCD FLEX screen settings by RF address; a group not listed gets L-Connect's default entry.</summary>
    public Dictionary<string, WirelessFanScreenPresentation> FanScreens { get; } =
        new Dictionary<string, WirelessFanScreenPresentation>();

    /// <summary>The devices whose L-Connect "sync to motherboard" switch is saved on.</summary>
    public HashSet<string> MotherboardArgbSync { get; } = new HashSet<string>();

    /// <summary>Every address an effect was looked up for, in order.</summary>
    public List<string> EffectLookups { get; } = new List<string>();

    public int? FindChannel(string masterMacText)
        => Channels.TryGetValue(masterMacText, out int channel) ? channel : null;

    public WirelessSavedEffect? FindEffect(string macText) {
        EffectLookups.Add(macText);
        return Effects.TryGetValue(macText, out WirelessSavedEffect? effect) ? effect : null;
    }

    public bool FindMotherboardArgbSync(string macText) => MotherboardArgbSync.Contains(macText);

    public WirelessAioPresentation? FindPumpPresentation(string macText)
        => Presentations.TryGetValue(macText, out WirelessAioPresentation? presentation) ? presentation : null;

    public WirelessFanScreenPresentation? FindFanScreenPresentation(string macText)
        => FanScreens.TryGetValue(macText, out WirelessFanScreenPresentation? presentation) ? presentation : null;

    public IReadOnlyList<WirelessLockedDevice>? FindLockedDevices(string masterMacText)
        => LockedDevices.TryGetValue(masterMacText, out IReadOnlyList<WirelessLockedDevice>? locked) ? locked : null;
}
