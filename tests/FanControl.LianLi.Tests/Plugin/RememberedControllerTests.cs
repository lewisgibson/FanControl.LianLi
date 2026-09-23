using System;
using System.Collections.Generic;
using System.Linq;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Plugin;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Tests.Fakes;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Plugin;

/// <summary>What a scan remembers of a controller it built, and how two such records combine.</summary>
public sealed class RememberedControllerTests {
    private static readonly DateTime Now = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

    private static LocatedDevice Device(string path) => new LocatedDevice(0x0416, 0x8040, path, null);

    private static ControllerPlan Plan() => new ControllerPlan(DeviceKind.UniFan, Device("hid/a"));

    [Fact]
    public void KeepsThePopulatedChannelsAndTemperatures_AllSeenAtTheBuild() {
        ControllerPlan plan = Plan();
        var device = new FakeFanDevice(new[] { "c0", "c1", "c2" }, new[] { "t0" });
        device.UnpopulatedChannels.Add(1);

        var remembered = new RememberedController(plan, 3, device, Now);

        Assert.Same(plan, remembered.Plan);
        Assert.Equal(3, remembered.Index);
        Assert.Equal(new[] { "c0", "c2" }, remembered.Channels.Select(c => c.ControlId));
        Assert.Equal("t0", Assert.Single(remembered.Temperatures).Id);
        Assert.All(new[] { "c0", "c2", "c0/fan", "c2/fan", "t0" }, id => Assert.Equal(Now, remembered.LastSeenUtc(id)));
    }

    [Fact]
    public void ForADeviceWithoutTemperatures_HasNone() {
        // A wired controller measures no temperatures at all.
        var controller = new FanController(0, new FakeDeviceTransport(), new SlProtocol(), new bool[4], new FakeClock(), new FakeLogger());

        var remembered = new RememberedController(Plan(), 0, controller, Now);

        Assert.Empty(remembered.Temperatures);
        Assert.Equal(4, remembered.Channels.Count);
    }

    [Fact]
    public void RejectsMissingArguments() {
        Assert.Throws<ArgumentNullException>(() => new RememberedController(null!, 0, new FakeFanDevice("c"), Now));
        Assert.Throws<ArgumentNullException>(() => new RememberedController(Plan(), 0, null!, Now));
    }

    [Fact]
    public void KeepsAGroupControllersOwnFanSpeeds() {
        var remembered = new RememberedController(Plan(), 0, new FakeFanGroupDevice("ctl", "fan/0", "fan/1"), Now);

        Assert.Equal(new[] { "fan/0", "fan/1" }, remembered.FanSpeeds.Select(f => f.Id));
    }

    [Fact]
    public void GivesAWiredControllerOneFanSpeedPerPopulatedChannel() {
        var device = new FakeFanDevice("c0", "c1");
        device.UnpopulatedChannels.Add(0);

        var remembered = new RememberedController(Plan(), 0, device, Now);

        Assert.Equal("c1/fan", Assert.Single(remembered.FanSpeeds).Id);
    }

    [Fact]
    public void CoversAndMerges_ById_KeepingTheEarlierOrder() {
        ControllerPlan plan = Plan();
        var earlier = new RememberedController(plan, 1, new FakeFanDevice(new[] { "c0", "c1", "c2" }, new[] { "t0" }), Now.AddDays(-1));
        var now = new RememberedController(plan, 1, new FakeFanDevice(new[] { "c2", "c3" }, Array.Empty<string>()), Now);

        Assert.False(now.Covers(earlier));
        Assert.True(earlier.Covers(new RememberedController(plan, 1, new FakeFanDevice(new[] { "c1" }, Array.Empty<string>()), Now)));

        RememberedController merged = now.MergedWith(earlier, Now, TimeSpan.FromDays(30));

        Assert.Equal(new[] { "c0", "c1", "c2", "c3" }, merged.Channels.Select(c => c.ControlId));
        Assert.Equal(new[] { "c0/fan", "c1/fan", "c2/fan", "c3/fan" }, merged.FanSpeeds.Select(f => f.Id));
        Assert.Equal("t0", Assert.Single(merged.Temperatures).Id);
        Assert.Same(plan, merged.Plan);
        Assert.Equal(1, merged.Index);
        Assert.True(merged.Covers(earlier));
        Assert.True(merged.Covers(now));

        // What this build reported is seen now; what it only carried keeps its earlier time.
        Assert.Equal(Now, merged.LastSeenUtc("c2"));
        Assert.Equal(Now.AddDays(-1), merged.LastSeenUtc("c0"));
    }

    // A sensor no build has reported for the whole window is let go; one inside it is kept.
    [Fact]
    public void MergedWith_LetsGoOfASensorNotSeenForTheWindow() {
        ControllerPlan plan = Plan();
        var old = RememberedFixture.Of(
            plan,
            0,
            new[] { new ChannelDescriptor("gone", "n", "gone/fan", "n"), new ChannelDescriptor("kept", "n", "kept/fan", "n") },
            new[] { new FanSpeedDescriptor("gone/fan", "n"), new FanSpeedDescriptor("kept/fan", "n") },
            new[] { new TemperatureDescriptor("gone/temp", "n") },
            Now.AddDays(-31));
        var seen = new Dictionary<string, DateTime>(StringComparer.Ordinal) {
            ["gone"] = Now.AddDays(-31),
            ["gone/fan"] = Now.AddDays(-31),
            ["gone/temp"] = Now.AddDays(-31),
            ["kept"] = Now.AddDays(-29),
            ["kept/fan"] = Now.AddDays(-29),
        };
        var earlier = new RememberedController(plan, 0, old.Channels, old.FanSpeeds, old.Temperatures, seen);
        var now = new RememberedController(plan, 0, new FakeFanDevice("c9"), Now);

        RememberedController merged = now.MergedWith(earlier, Now, TimeSpan.FromDays(30));

        Assert.Equal(new[] { "kept", "c9" }, merged.Channels.Select(c => c.ControlId));
        Assert.Equal(new[] { "kept/fan", "c9/fan" }, merged.FanSpeeds.Select(f => f.Id));
        Assert.Empty(merged.Temperatures);
    }

    [Fact]
    public void Pruned_KeepsOnlyTheSensorsSeenInsideTheWindow() {
        ControllerPlan plan = Plan();
        var seen = new Dictionary<string, DateTime>(StringComparer.Ordinal) {
            ["a"] = Now.AddDays(-40),
            ["b"] = Now.AddDays(-1),
            ["a/fan"] = Now.AddDays(-40),
            ["b/fan"] = Now,
            ["t"] = Now.AddDays(-40),
        };
        var remembered = new RememberedController(
            plan,
            4,
            new[] { new ChannelDescriptor("a", "n", "a/fan", "n"), new ChannelDescriptor("b", "n", "b/fan", "n") },
            new[] { new FanSpeedDescriptor("a/fan", "n"), new FanSpeedDescriptor("b/fan", "n") },
            new[] { new TemperatureDescriptor("t", "n") },
            seen);

        RememberedController pruned = remembered.Pruned(Now, TimeSpan.FromDays(30));

        Assert.Equal(new[] { "b" }, pruned.Channels.Select(c => c.ControlId));
        Assert.Equal(new[] { "b/fan" }, pruned.FanSpeeds.Select(f => f.Id));
        Assert.Empty(pruned.Temperatures);
        Assert.Equal(4, pruned.Index);
        Assert.Equal(Now.AddDays(-1), pruned.LastSeenUtc("b"));
    }

    [Fact]
    public void CoversAndMergedWith_RejectNull() {
        var remembered = new RememberedController(Plan(), 0, new FakeFanDevice("c"), Now);

        Assert.Throws<ArgumentNullException>(() => remembered.Covers(null!));
        Assert.Throws<ArgumentNullException>(() => remembered.MergedWith(null!, Now, TimeSpan.FromDays(30)));
    }

    [Fact]
    public void AsSaved_RejectsMissingParts_AndASensorWithNoTime() {
        ControllerPlan plan = Plan();
        var none = Array.Empty<ChannelDescriptor>();
        var speeds = Array.Empty<FanSpeedDescriptor>();
        var temperatures = Array.Empty<TemperatureDescriptor>();
        var seen = new Dictionary<string, DateTime>();

        Assert.Throws<ArgumentNullException>(() => new RememberedController(null!, 0, none, speeds, temperatures, seen));
        Assert.Throws<ArgumentNullException>(() => new RememberedController(plan, 0, null!, speeds, temperatures, seen));
        Assert.Throws<ArgumentNullException>(() => new RememberedController(plan, 0, none, null!, temperatures, seen));
        Assert.Throws<ArgumentNullException>(() => new RememberedController(plan, 0, none, speeds, null!, seen));
        Assert.Throws<ArgumentNullException>(() => new RememberedController(plan, 0, none, speeds, temperatures, null!));
        Assert.Throws<ArgumentException>(() => new RememberedController(
            plan, 0, new[] { new ChannelDescriptor("c", "n", "r", "n") }, speeds, temperatures, seen));
    }
}
