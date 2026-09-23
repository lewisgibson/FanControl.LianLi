using System;
using System.Collections.Generic;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Plugin;

namespace FanControl.LianLi.Tests.Fakes;

/// <summary>A remembered controller made from its parts, every sensor last seen at the same time.</summary>
internal static class RememberedFixture {
    public static RememberedController Of(
        ControllerPlan plan,
        int index,
        IReadOnlyList<ChannelDescriptor> channels,
        IReadOnlyList<FanSpeedDescriptor> fanSpeeds,
        IReadOnlyList<TemperatureDescriptor> temperatures,
        DateTime seenUtc) {
        var seen = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (ChannelDescriptor channel in channels) {
            seen[channel.ControlId] = seenUtc;
        }

        foreach (FanSpeedDescriptor speed in fanSpeeds) {
            seen[speed.Id] = seenUtc;
        }

        foreach (TemperatureDescriptor temperature in temperatures) {
            seen[temperature.Id] = seenUtc;
        }

        return new RememberedController(plan, index, channels, fanSpeeds, temperatures, seen);
    }
}
