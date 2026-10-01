using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Plugin;
using FanControl.LianLi.Tests.Fakes;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Plugin;

/// <summary>
/// The process-wide state: which instance's worker is running, the controllers remembered across
/// the new plugin object FanControl makes on every refresh, the indices they keep, and the
/// rate-limited refresh request.
/// </summary>
public sealed class PluginRuntimeTests {
    private static RememberedController Remembered(string path, int index)
        => new RememberedController(
            new ControllerPlan(DeviceKind.UniFan, new LocatedDevice(0x0CF2, 0xA102, path, null)),
            index,
            new FakeFanDevice(new[] { "LianLi/" + index + "/ch0/ctl" }, Array.Empty<string>()),
            new FakeClock().UtcNow);

    private static RememberedController Sensorless(string path, int index)
        => new RememberedController(
            new ControllerPlan(DeviceKind.UniFan, new LocatedDevice(0x0CF2, 0xA102, path, null)),
            index,
            new FakeFanDevice(Array.Empty<string>(), Array.Empty<string>()),
            new FakeClock().UtcNow);

    private static RememberedController Group(DeviceKind kind, int vendorId, int productId, string path, int index, params string[] ids)
        => new RememberedController(
            new ControllerPlan(kind, new LocatedDevice(vendorId, productId, path, null), new LocatedDevice(vendorId, productId, path + "/rx", null)),
            index,
            new FakeFanGroupDevice(ids[0], ids.Skip(1).ToArray()),
            new FakeClock().UtcNow);

    private static RememberedController Receiver(string path, int index, params string[] ids)
        => new RememberedController(
            new ControllerPlan(DeviceKind.FlexReceiver, new LocatedDevice(0x43A8, 0x0101, path, null)),
            index,
            ids.Length == 0 ? new FakeFanDevice() : new FakeFanGroupDevice(ids[0], ids.Skip(1).ToArray()),
            new FakeClock().UtcNow);

    private static RememberedController Pair(string path, int index, params string[] ids)
        => Group(DeviceKind.WirelessTransmitter, 0x0416, 0x8040, path, index, ids);

    private static ControllerPlan PairPlan(string path)
        => new ControllerPlan(DeviceKind.WirelessTransmitter, new LocatedDevice(0x0416, 0x8040, path, null), new LocatedDevice(0x0416, 0x8041, path + "/rx", null));

    private static ControllerPlan ReceiverPlan(string path)
        => new ControllerPlan(DeviceKind.FlexReceiver, new LocatedDevice(0x43A8, 0x0101, path, null));

    // A FLEX chain has the same ids on its USB receiver and on the dongles; whichever reports it
    // takes it from the other's memory, so only one stands in with it.
    [Fact]
    public void Remember_TakesTheSensorsAControllerReports_FromEveryOtherRememberedController() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        _ = runtime.Remember("dongles", Pair("dongles", 0, "LianLi/wa/ctl", "LianLi/wa/f0/fan", "LianLi/wb/ctl", "LianLi/wb/f0/fan"));

        _ = runtime.Remember("usb", Receiver("usb", 1, "LianLi/wa/ctl", "LianLi/wa/f0/fan"));
        Assert.Equal(new[] { "LianLi/wb/ctl", "LianLi/wb/f0/fan" }, runtime.Recall("dongles")!.Ids);
        Assert.Equal(new[] { "LianLi/wa/ctl", "LianLi/wa/f0/fan" }, runtime.Recall("usb")!.Ids);

        _ = runtime.Remember("dongles", Pair("dongles", 0, "LianLi/wa/ctl", "LianLi/wa/f0/fan"));
        Assert.Empty(runtime.Recall("usb")!.Ids);
        Assert.Equal(new[] { "LianLi/wa/ctl", "LianLi/wb/ctl", "LianLi/wb/f0/fan", "LianLi/wa/f0/fan" }, runtime.Recall("dongles")!.Ids);
    }

    // The wireless controller keeps a group's sensors after the group is unbound; a receiver keeps
    // its chain's after the radio takes it. What a controller only retains is not added to its
    // memory and is not taken from another controller: only what it drives is. A chain no controller
    // drives stays with the one that drove it last.
    [Fact]
    public void Remember_AddsAndClaimsOnlyTheSensorsAControllerDrives_NotThoseItRetains() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        _ = runtime.Remember("usb", Receiver("usb", 1, "LianLi/wa/ctl", "LianLi/wa/f0/fan"));

        var pair = new FakeFanGroupDevice("LianLi/wa/ctl", "LianLi/wa/f0/fan");
        pair.Retained.UnionWith(new[] { "LianLi/wa/ctl", "LianLi/wa/f0/fan" });
        RememberedController pairMemory = runtime.Remember(
            "dongles", new RememberedController(PairPlan("dongles"), 0, pair, new FakeClock().UtcNow), out IReadOnlyCollection<string> claimed);

        Assert.Empty(pairMemory.Ids);
        Assert.Empty(claimed);
        Assert.Equal(new[] { "LianLi/wa/ctl", "LianLi/wa/f0/fan" }, runtime.Recall("usb")!.Ids);
        Assert.True(runtime.IsNew("dongles"));

        // Driven now: taken from the receiver, and the receiver retaining it adds nothing back.
        pair.Retained.Clear();
        _ = runtime.Remember("dongles", new RememberedController(PairPlan("dongles"), 0, pair, new FakeClock().UtcNow), out claimed);
        Assert.Equal(new[] { "LianLi/wa/ctl", "LianLi/wa/f0/fan" }, claimed);
        Assert.Empty(runtime.Recall("usb")!.Ids);
        var receiver = new FakeFanGroupDevice("LianLi/wa/ctl", "LianLi/wa/f0/fan");
        receiver.Retained.UnionWith(new[] { "LianLi/wa/ctl", "LianLi/wa/f0/fan" });
        _ = runtime.Remember("usb", new RememberedController(ReceiverPlan("usb"), 1, receiver, new FakeClock().UtcNow), out claimed);
        Assert.Empty(claimed);
        Assert.Empty(runtime.Recall("usb")!.Ids);
        Assert.Equal(new[] { "LianLi/wa/ctl", "LianLi/wa/f0/fan" }, runtime.Recall("dongles")!.Ids);

        // Nothing drives it now: it stays where it was.
        pair.Retained.UnionWith(new[] { "LianLi/wa/ctl", "LianLi/wa/f0/fan" });
        _ = runtime.Remember("dongles", new RememberedController(PairPlan("dongles"), 0, pair, new FakeClock().UtcNow), out claimed);
        Assert.Empty(claimed);
        Assert.Equal(new[] { "LianLi/wa/ctl", "LianLi/wa/f0/fan" }, runtime.Recall("dongles")!.Ids);
        Assert.Equal("dongles", runtime.OwnerOf("LianLi/wa/ctl"));
        Assert.Null(runtime.OwnerOf("LianLi/wz/ctl"));
        Assert.Throws<ArgumentNullException>(() => runtime.OwnerOf(null!));
    }

    [Fact]
    public void Remember_SaysWhichIdsItTookFromOtherControllers() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        _ = runtime.Remember("dongles", Pair("dongles", 0, "LianLi/wa/ctl", "LianLi/wa/f0/fan", "LianLi/wb/ctl"), out IReadOnlyCollection<string> claimed);
        Assert.Empty(claimed);

        _ = runtime.Remember("usb", Receiver("usb", 1, "LianLi/wa/ctl", "LianLi/wa/f0/fan"), out claimed);
        Assert.Equal(new[] { "LianLi/wa/ctl", "LianLi/wa/f0/fan" }, claimed);
        Assert.Equal("usb", runtime.OwnerOf("LianLi/wa/ctl"));
        Assert.Equal("dongles", runtime.OwnerOf("LianLi/wb/ctl"));

        _ = runtime.Remember("usb", Receiver("usb", 1, "LianLi/wa/ctl", "LianLi/wa/f0/fan"), out claimed);
        Assert.Empty(claimed); // nothing left to take
    }

    // Load decides whose each id is from one snapshot of the memory, so a change of hands the
    // worker makes while the pass runs cannot leave an id registered under both or neither.
    [Fact]
    public void Owners_IsASnapshotOfEveryIdAndItsController() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        _ = runtime.Remember("dongles", Pair("dongles", 0, "LianLi/wa/ctl", "LianLi/wb/ctl"));
        _ = runtime.Remember("usb", Receiver("usb", 1, "LianLi/wa/ctl"));

        Dictionary<string, string> owners = runtime.Owners();

        Assert.Equal(new Dictionary<string, string> { ["LianLi/wa/ctl"] = "usb", ["LianLi/wb/ctl"] = "dongles" }, owners);
        _ = runtime.Remember("dongles", Pair("dongles", 0, "LianLi/wa/ctl", "LianLi/wb/ctl"));
        Assert.Equal("usb", owners["LianLi/wa/ctl"]); // the snapshot does not follow the memory
        Assert.Equal("dongles", runtime.OwnerOf("LianLi/wa/ctl"));
    }

    [Fact]
    public void Remember_LeavesOtherControllersAlone_WhenNothingItReportsIsTheirs() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        RememberedController pair = runtime.Remember("dongles", Pair("dongles", 0, "LianLi/wa/ctl", "LianLi/wa/f0/fan"));

        _ = runtime.Remember("usb", Receiver("usb", 1, "LianLi/wb/ctl", "LianLi/wb/f0/fan"));
        _ = runtime.Remember("empty", Receiver("empty", 2));

        Assert.Same(pair, runtime.Recall("dongles"));
    }

    // New: never remembered, or first remembered by this process and still without a sensor.
    [Fact]
    public void IsNew_UntilASensorIsRemembered_OrAnEarlierProcessRememberedIt() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        runtime.Restore(new FakeLogger());
        Assert.True(runtime.IsNew("a"));

        _ = runtime.Remember("a", Sensorless("a", 0));
        Assert.True(runtime.IsNew("a"));

        _ = runtime.Remember("a", Remembered("a", 0));
        Assert.False(runtime.IsNew("a"));
    }

    [Fact]
    public void IndexFor_OnAFreshProcess_NumbersInScanOrder() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        var taken = new HashSet<int>();

        Assert.Equal(new[] { 0, 1, 2 }, new[] { "a", "b", "c" }.Select(key => runtime.IndexFor(key, taken)));
    }

    [Fact]
    public void IndexFor_KeepsARememberedIndex_AndNeverHandsAnotherControllersToANewDevice() {
        // A was built at 0, B at 1, C at 2. On this scan A is missing and a new device D appears
        // first in path order: B and C keep 1 and 2, and D must not take A's 0.
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        runtime.Remember("a", Remembered("a", 0));
        runtime.Remember("b", Remembered("b", 1));
        runtime.Remember("c", Remembered("c", 2));
        var taken = new HashSet<int>();

        Assert.Equal(3, runtime.IndexFor("d", taken));
        Assert.Equal(1, runtime.IndexFor("b", taken));
        Assert.Equal(2, runtime.IndexFor("c", taken));
        Assert.Equal(new HashSet<int> { 1, 2, 3 }, taken);
    }

    [Fact]
    public void IndexFor_ARememberedIndexAlreadyTaken_FallsBackToAFreeOne() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        runtime.Remember("a", Remembered("a", 0));
        var taken = new HashSet<int> { 0 };

        Assert.Equal(1, runtime.IndexFor("a", taken));
    }

    [Fact]
    public void Remember_MergesWithWhatWasRemembered_AndRecallReadsBack_InIndexOrder() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        RememberedController first = Remembered("b", 5);
        Assert.Same(first, runtime.Remember("b", first));
        RememberedController merged = runtime.Remember("b", Remembered("b", 1));
        _ = runtime.Remember("a", Remembered("a", 0));

        Assert.Same(merged, runtime.Recall("b"));
        Assert.Equal(1, merged.Index);
        Assert.Equal(new[] { "LianLi/5/ch0/ctl", "LianLi/1/ch0/ctl" }, merged.Channels.Select(c => c.ControlId));
        Assert.Null(runtime.Recall("z"));
        Assert.Equal(new[] { "a", "b" }, runtime.Remembered().Select(entry => entry.Key));
        Assert.Equal(2, runtime.RememberedCount);
    }

    [Fact]
    public void TakeOverMoved_TakesTheOnlyCandidate_NeverGuessesBetweenSeveral_AndOnlyForANewPath() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        _ = runtime.Remember("old-high", Remembered("old-high", 4));
        _ = runtime.Remember("old-low", Remembered("old-low", 1));
        _ = runtime.Remember("known", Remembered("known", 0));
        var planned = new HashSet<string> { "known", "new", "newer", "newest", "old-high" };

        Assert.Null(runtime.TakeOverMoved(Plan("known"), planned));
        Assert.Equal("old-low", runtime.TakeOverMoved(Plan("new"), planned)); // old-high was found: only old-low is missing
        Assert.Equal(1, runtime.Recall("new")!.Index);
        Assert.Equal("new", runtime.Recall("new")!.Plan.Key);
        Assert.Null(runtime.TakeOverMoved(Plan("newest"), planned));

        _ = runtime.Remember("gone-a", Remembered("gone-a", 7));
        _ = runtime.Remember("gone-b", Remembered("gone-b", 8));
        Assert.Null(runtime.TakeOverMoved(Plan("newer"), planned)); // two missing: nothing says which moved
        Assert.Null(runtime.TakeOverMoved(
            new ControllerPlan(DeviceKind.TlFan, new LocatedDevice(0x0416, 0x7372, "tl", null)), planned));
        Assert.Throws<ArgumentNullException>(() => runtime.TakeOverMoved(null!, planned));
        Assert.Throws<ArgumentNullException>(() => runtime.TakeOverMoved(Plan("x"), null!));
    }

    // Of two remembered units, the one Windows kept in the same container is the one that moved.
    [Fact]
    public void TakeOverMoved_PrefersTheCandidateInTheSameContainer_OverTheLowerIndex() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        _ = runtime.Remember("low", RememberedIn("low", 0, "{other}"));
        _ = runtime.Remember("high", RememberedIn("high", 1, "{unit}"));
        _ = runtime.Remember("none", RememberedIn("none", 2, null));
        var planned = new HashSet<string> { "new", "newer" };

        Assert.Equal("high", runtime.TakeOverMoved(
            new ControllerPlan(DeviceKind.UniFan, new LocatedDevice(0x0CF2, 0xA102, "new", null, "{UNIT}")), planned));
        Assert.Null(runtime.TakeOverMoved(
            new ControllerPlan(DeviceKind.UniFan, new LocatedDevice(0x0CF2, 0xA102, "newer", null, "{elsewhere}")), planned));
    }

    private static RememberedController RememberedIn(string path, int index, string? container)
        => new RememberedController(
            new ControllerPlan(DeviceKind.UniFan, new LocatedDevice(0x0CF2, 0xA102, path, null, container)),
            index,
            new FakeFanDevice(new[] { "LianLi/" + index + "/ch0/ctl" }, Array.Empty<string>()),
            new FakeClock().UtcNow);

    private static ControllerPlan Plan(string path) => new ControllerPlan(DeviceKind.UniFan, new LocatedDevice(0x0CF2, 0xA102, path, null));

    [Fact]
    public void Persist_FromTwoThreads_SavesOneAtATime() {
        var store = new FakeRememberedControllerStore();
        var runtime = new PluginRuntime(new FakeClock(), store);
        runtime.Restore(new FakeLogger());
        _ = runtime.Remember("a", Remembered("a", 0));
        using var inside = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        store.DuringSave = () => {
            inside.Set();
            release.Wait();
        };
        var first = new Thread(() => runtime.Persist(new FakeLogger()));
        first.Start();
        Assert.True(inside.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        var second = new Thread(() => runtime.Persist(new FakeLogger()));
        second.Start();
        // The second is blocked - on the save gate, the only place it can wait - not in the store.
        Assert.True(SpinWait.SpinUntil(() => (second.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, store.MostAtOnce);

        release.Set();
        first.Join();
        second.Join();

        Assert.Equal(1, store.MostAtOnce);
        Assert.Equal(2, store.Saves);
    }

    // A saved file that could not be read at boot is read again at the next scan, and nothing is
    // saved over it before then.
    [Fact]
    public void Restore_OfAFileThatCouldNotBeRead_IsTriedAgain_AndNothingIsSavedOverItMeanwhile() {
        var clock = new FakeClock();
        var store = new FakeRememberedControllerStore { UnreadableLoads = 1 };
        store.Stored.Add(new StoredController("a", Remembered("a", 0), clock.UtcNow));
        var runtime = new PluginRuntime(clock, store);
        var logger = new FakeLogger();

        runtime.Restore(logger);
        _ = runtime.Remember("b", Remembered("b", 1));
        runtime.Persist(logger);

        Assert.Equal(0, store.Saves);
        Assert.Contains("remembered controllers not saved: the saved ones have not been read yet", logger.Messages);
        runtime.Restore(logger);
        Assert.NotNull(runtime.Recall("a"));
        runtime.Persist(logger);
        Assert.Equal(new[] { "a", "b" }, store.Stored.Select(c => c.Key));
    }

    // A clock that stepped back (a dual-boot clock, a time sync at boot) forgets nothing: what was
    // seen "in the future" was seen a moment ago.
    [Fact]
    public void AClockThatSteppedBack_ForgetsNoControllerOrSensor() {
        var clock = new FakeClock();
        var store = new FakeRememberedControllerStore();
        DateTime ahead = clock.UtcNow + TimeSpan.FromHours(1);
        var plan = new ControllerPlan(DeviceKind.UniFan, new LocatedDevice(0x0CF2, 0xA102, "a", null));
        store.Stored.Add(new StoredController(
            "a",
            RememberedFixture.Of(plan, 0, new[] { new ChannelDescriptor("c", "n", "r", "n") }, Array.Empty<FanSpeedDescriptor>(), Array.Empty<TemperatureDescriptor>(), ahead),
            ahead));
        var runtime = new PluginRuntime(clock, store);

        runtime.Restore(new FakeLogger());
        runtime.ForgetExpired(new FakeLogger());

        RememberedController kept = runtime.Recall("a")!;
        Assert.Single(kept.Channels);
        _ = runtime.Remember("a", RememberedFixture.Of(kept.Plan, 0, Array.Empty<ChannelDescriptor>(), Array.Empty<FanSpeedDescriptor>(), Array.Empty<TemperatureDescriptor>(), clock.UtcNow));
        Assert.Single(runtime.Recall("a")!.Channels);
    }

    [Fact]
    public void Restore_ASecondEntryAtAnIndexAlreadyTaken_IsNotUsed() {
        var clock = new FakeClock();
        var store = new FakeRememberedControllerStore();
        store.Stored.Add(new StoredController("a", Remembered("a", 0), clock.UtcNow));
        store.Stored.Add(new StoredController("b", Remembered("b", 0), clock.UtcNow));
        var runtime = new PluginRuntime(clock, store);
        var logger = new FakeLogger();

        runtime.Restore(logger);

        Assert.NotNull(runtime.Recall("a"));
        Assert.Null(runtime.Recall("b"));
        Assert.Contains("remembered controller b not used: index 0 is already another's", logger.Messages);
        Assert.Contains(logger.Messages, m => m.Contains("remembered 1 controller(s) from earlier runs; 0 not built for 30 days were let go"));
    }

    [Fact]
    public void ABuildInFlight_HoldsItsIndex_AndIsTheOnlyBuildOfItsDevice_UntilItEnds() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        Assert.True(runtime.TryBeginBuild("slow", 3));

        Assert.False(runtime.TryBeginBuild("slow", 3));
        Assert.Equal(3, runtime.IndexFor("slow", new HashSet<int>()));
        Assert.Equal(4, runtime.IndexFor("other", new HashSet<int> { 0, 1, 2 })); // 3 is held
        runtime.EndBuild("slow");
        runtime.EndBuild("never-started");
        Assert.Equal(3, runtime.IndexFor("other", new HashSet<int> { 0, 1, 2 }));
        Assert.True(runtime.TryBeginBuild("slow", 3));
        Assert.Throws<ArgumentNullException>(() => runtime.TryBeginBuild(null!, 0));
        Assert.Throws<ArgumentNullException>(() => runtime.ForgetExpired(null!));
    }

    [Fact]
    public void Start_StopsWhatWasRunning_WhoeverStartedIt() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        var first = new FakeFanDevice(new[] { "c0" }, Array.Empty<string>());
        var second = new FakeFanDevice(new[] { "c1" }, Array.Empty<string>());
        object ownerA = new object();
        object ownerB = new object();

        runtime.Start(ownerA, new IFanDevice[] { first }, new FakeLogger());
        runtime.Start(ownerB, new IFanDevice[] { second }, new FakeLogger());

        Assert.True(first.IsDisposed);
        Assert.False(second.IsDisposed);
        Assert.False(runtime.IsOwnedBy(ownerA));
        Assert.True(runtime.IsOwnedBy(ownerB));
        runtime.Stop();
        Assert.True(second.IsDisposed);
        Assert.False(runtime.IsOwnedBy(ownerB));
    }

    [Fact]
    public void StopIfOwnedBy_LeavesAnotherInstancesWorkerRunning() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        var device = new FakeFanDevice(new[] { "c0" }, Array.Empty<string>());
        object owner = new object();
        runtime.Start(owner, new IFanDevice[] { device }, new FakeLogger());

        runtime.StopIfOwnedBy(new object());
        Assert.False(device.IsDisposed);

        runtime.StopIfOwnedBy(owner);
        Assert.True(device.IsDisposed);
    }

    [Fact]
    public void Wake_WithNothingRunning_IsANoOp() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());

        runtime.Wake();

        Assert.False(runtime.IsOwnedBy(new object()));
    }

    [Fact]
    public void TryTakeRefresh_OnlyTheRunningInstanceTakesIt_AtMostOnceEveryThirtySeconds() {
        var clock = new FakeClock();
        var runtime = new PluginRuntime(clock, new FakeRememberedControllerStore());
        var logger = new FakeLogger();
        object owner = new object();
        runtime.Start(owner, Array.Empty<IFanDevice>(), logger);

        Assert.False(runtime.TryTakeRefresh(owner, out _)); // nothing asked for

        runtime.RequestRefresh("first", logger);
        runtime.RequestRefresh("second", logger);
        Assert.False(runtime.TryTakeRefresh(new object(), out _)); // not the running instance
        Assert.True(runtime.TryTakeRefresh(owner, out string reason));
        Assert.Equal("first", reason);
        Assert.Single(logger.Messages, m => m.Contains("refresh wanted: first"));

        runtime.RequestRefresh("again", logger);
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(runtime.TryTakeRefresh(owner, out _));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(runtime.TryTakeRefresh(owner, out reason));
        Assert.Equal("again", reason);
    }

    [Fact]
    public void Process_IsOneSharedRuntime()
        => Assert.Same(PluginRuntime.Process, PluginRuntime.Process);

    [Fact]
    public void NullArgumentsThrow() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());

        Assert.Throws<ArgumentNullException>(() => new PluginRuntime(null!, new FakeRememberedControllerStore()));
        Assert.Throws<ArgumentNullException>(() => new PluginRuntime(new FakeClock(), null!));
        Assert.Throws<ArgumentNullException>(() => runtime.Restore(null!));
        Assert.Throws<ArgumentNullException>(() => runtime.Persist(null!));
        Assert.Throws<ArgumentNullException>(() => runtime.Start(null!, Array.Empty<IFanDevice>(), new FakeLogger()));
        Assert.Throws<ArgumentNullException>(() => runtime.Remember(null!, Remembered("a", 0)));
        Assert.Throws<ArgumentNullException>(() => runtime.Remember("a", null!));
        Assert.Throws<ArgumentNullException>(() => runtime.IndexFor(null!, new HashSet<int>()));
        Assert.Throws<ArgumentNullException>(() => runtime.IndexFor("a", null!));
        Assert.Throws<ArgumentNullException>(() => runtime.RequestRefresh(null!, new FakeLogger()));
        Assert.Throws<ArgumentNullException>(() => runtime.RequestRefresh("a", null!));
    }

    [Fact]
    public void Restore_TakesWhatEarlierRunsSaved_OnceAProcess_AndLetsTheStaleGo() {
        var clock = new FakeClock();
        var store = new FakeRememberedControllerStore();
        store.Stored.Add(new StoredController("fresh", Remembered("fresh", 0), clock.UtcNow - TimeSpan.FromDays(29)));
        store.Stored.Add(new StoredController("stale", Remembered("stale", 1), clock.UtcNow - TimeSpan.FromDays(31)));
        var runtime = new PluginRuntime(clock, store);
        var logger = new FakeLogger();

        runtime.Restore(logger);
        store.Stored.Add(new StoredController("later", Remembered("later", 2), clock.UtcNow));
        runtime.Restore(logger); // once a process

        Assert.NotNull(runtime.Recall("fresh"));
        Assert.Null(runtime.Recall("stale"));
        Assert.Null(runtime.Recall("later"));
        Assert.Single(logger.Messages, m => m.Contains("remembered 1 controller(s) from earlier runs; 1 not built for 30 days were let go"));
    }

    [Fact]
    public void Restore_LetsGoOfTheSensorsNotSeenFor30Days_OfAControllerItKeeps() {
        var clock = new FakeClock();
        var store = new FakeRememberedControllerStore();
        ControllerPlan plan = new ControllerPlan(DeviceKind.UniFan, new LocatedDevice(0x0CF2, 0xA102, "a", null));
        var seen = new Dictionary<string, DateTime>(StringComparer.Ordinal) {
            ["fresh"] = clock.UtcNow - TimeSpan.FromDays(1),
            ["stale"] = clock.UtcNow - TimeSpan.FromDays(31),
        };
        store.Stored.Add(new StoredController(
            "a",
            new RememberedController(
                plan,
                0,
                new[] { new ChannelDescriptor("fresh", "n", "fresh/fan", "n"), new ChannelDescriptor("stale", "n", "stale/fan", "n") },
                Array.Empty<FanSpeedDescriptor>(),
                Array.Empty<TemperatureDescriptor>(),
                seen),
            clock.UtcNow - TimeSpan.FromDays(1)));
        var runtime = new PluginRuntime(clock, store);

        runtime.Restore(new FakeLogger());

        Assert.Equal(new[] { "fresh" }, runtime.Recall("a")!.Channels.Select(c => c.ControlId));
    }

    [Fact]
    public void Restore_KeepsWhatThisProcessAlreadyBuilt_AndIsQuietWithNothingSaved() {
        var store = new FakeRememberedControllerStore();
        var runtime = new PluginRuntime(new FakeClock(), store);
        RememberedController built = Remembered("a", 3);
        runtime.Remember("a", built);
        var logger = new FakeLogger();

        runtime.Restore(logger);

        Assert.Empty(logger.Messages);
        Assert.Same(built, runtime.Recall("a"));
    }

    // A scan that ran while the file could not be read numbered its controllers without it. When the
    // file is read, it wins: a controller keeps its saved index, and the sensors it saved are merged
    // with the ones the scan found.
    [Fact]
    public void Restore_ReadLate_KeepsASavedControllersIndex_AndEverySensorItSaved() {
        var clock = new FakeClock();
        var saved = new FakeRememberedControllerStore();
        saved.Stored.Add(new StoredController("a", Channels("a", 9, "LianLi/9/ch0/ctl", "LianLi/9/ch1/ctl"), clock.UtcNow));
        saved.Stored.Add(new StoredController("b", Channels("b", 4, "LianLi/4/ch0/ctl", "LianLi/4/ch1/ctl"), clock.UtcNow));
        var runtime = new PluginRuntime(clock, saved);
        _ = runtime.Remember("a", Channels("a", 3, "LianLi/3/ch0/ctl"));
        _ = runtime.Remember("b", Channels("b", 4, "LianLi/4/ch0/ctl"));

        runtime.Restore(new FakeLogger());

        Assert.Equal(9, runtime.Recall("a")!.Index);
        Assert.Equal(new[] { "LianLi/9/ch0/ctl", "LianLi/9/ch1/ctl" }, runtime.Recall("a")!.Channels.Select(c => c.ControlId));
        Assert.Equal(new[] { "LianLi/4/ch0/ctl", "LianLi/4/ch1/ctl" }, runtime.Recall("b")!.Channels.Select(c => c.ControlId));
        Assert.False(runtime.IsNew("a"));
    }

    // One the scan numbered onto a saved controller's index - that controller was late - is let go,
    // to be numbered again by the next scan, so the saved one's curves never move onto its fans.
    [Fact]
    public void Restore_ReadLate_LetsGoOfOneNumberedOntoASavedIndex() {
        var clock = new FakeClock();
        var saved = new FakeRememberedControllerStore();
        saved.Stored.Add(new StoredController("a", Channels("a", 0, "LianLi/0/ch0/ctl"), clock.UtcNow));
        saved.Stored.Add(new StoredController("b", Channels("b", 1, "LianLi/1/ch0/ctl"), clock.UtcNow));
        saved.Stored.Add(new StoredController("c", Channels("c", 5, "LianLi/5/ch0/ctl"), clock.UtcNow));
        var runtime = new PluginRuntime(clock, saved);
        _ = runtime.Remember("b", Channels("b", 0, "LianLi/0/ch0/ctl"));
        _ = runtime.Remember("new", Channels("new", 5, "LianLi/5/ch0/ctl"));
        var logger = new FakeLogger();

        runtime.Restore(logger);

        Assert.Equal(new[] { ("a", 0), ("b", 1), ("c", 5) }, runtime.Remembered().Select(e => (e.Key, e.Value.Index)));
        Assert.Contains("controller new was numbered 5 before the saved controllers could be read; it is numbered again at the next scan", logger.Messages);
    }

    // The numbering moves on only when the file is read after a scan numbered controllers without
    // it, and what a build under the earlier numbering reports is then not remembered.
    [Fact]
    public void NumberingEpoch_MovesOnWhenTheFileIsReadLate_AndRememberUnderRefusesTheOldOne() {
        var normal = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        normal.Restore(new FakeLogger());
        Assert.Equal(0, normal.NumberingEpoch);
        Assert.NotNull(normal.RememberUnder(0, "a", Remembered("a", 0)));

        var store = new FakeRememberedControllerStore { UnreadableLoads = 1 };
        var late = new PluginRuntime(new FakeClock(), store);
        late.Restore(new FakeLogger());
        _ = late.Remember("a", Remembered("a", 0));
        late.Restore(new FakeLogger());

        Assert.Equal(1, late.NumberingEpoch);
        Assert.Null(late.RememberUnder(0, "b", Remembered("b", 1)));
        Assert.Null(late.Recall("b"));
        Assert.NotNull(late.RememberUnder(1, "b", Remembered("b", 1)));
    }

    // A rebuild from an instance of the earlier numbering cannot claim its device after the late
    // read, so it never holds a guessed index the next scan would have to number around.
    [Fact]
    public void TryBeginBuildUnder_AnEarlierNumbering_ClaimsNothing() {
        var clock = new FakeClock();
        var store = new FakeRememberedControllerStore { UnreadableLoads = 1 };
        store.Stored.Add(new StoredController("b", Remembered("b", 0), clock.UtcNow));
        var runtime = new PluginRuntime(clock, store);
        runtime.Restore(new FakeLogger());
        _ = runtime.Remember("a", Remembered("a", 0));
        runtime.Restore(new FakeLogger());

        Assert.False(runtime.TryBeginBuildUnder(0, "a", 0));

        var taken = new HashSet<int>();
        Assert.NotEqual(0, runtime.IndexFor("a", taken));
        Assert.Equal(0, runtime.IndexFor("b", taken));
        Assert.True(runtime.TryBeginBuildUnder(1, "a", 1));
    }

    // What Load registered is published under the memory's lock and handed to each instance that remembers a
    // controller, so a claim and the publication are ordered: one remembered before is in the
    // memory Load reads after publishing, one remembered after is handed the published set.
    [Fact]
    public void Publish_KeepsWhatLoadRegistered_HandedToEveryClaimAfterIt_AndClearedWhenANewInstanceStarts() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());
        var registered = new Dictionary<string, string>(StringComparer.Ordinal) { ["LianLi/wa/ctl"] = "usb" };
        Assert.Null(runtime.Registered);
        Assert.NotNull(runtime.RememberUnder(0, "usb", Receiver("usb", 0, "LianLi/wa/ctl"), out _, out Dictionary<string, string>? before));
        Assert.Null(before);

        runtime.Publish(registered);

        Assert.Same(registered, runtime.Registered);
        Assert.NotNull(runtime.RememberUnder(0, "dongles", Pair("dongles", 1, "LianLi/wa/ctl"), out IReadOnlyCollection<string> claimed, out Dictionary<string, string>? after));
        Assert.Same(registered, after);
        Assert.Equal(new[] { "LianLi/wa/ctl" }, claimed);
        Assert.Throws<ArgumentNullException>(() => runtime.Publish(null!));

        runtime.Start(new object(), Array.Empty<IFanDevice>(), new FakeLogger());
        Assert.Null(runtime.Registered);
        runtime.Stop();
    }

    [Fact]
    public void IsBuilding_SaysWhetherABuildOfTheControllerIsRunning() {
        var runtime = new PluginRuntime(new FakeClock(), new FakeRememberedControllerStore());

        Assert.False(runtime.IsBuilding("a"));
        Assert.True(runtime.TryBeginBuild("a", 0));
        Assert.True(runtime.IsBuilding("a"));
        runtime.EndBuild("a");
        Assert.False(runtime.IsBuilding("a"));
        Assert.Throws<ArgumentNullException>(() => runtime.IsBuilding(null!));
    }

    [Fact]
    public void RememberUnder_AnEarlierNumbering_RemembersNothing_AndTakesNothing() {
        var store = new FakeRememberedControllerStore { UnreadableLoads = 1 };
        var runtime = new PluginRuntime(new FakeClock(), store);
        var log = new FakeLogger();
        runtime.Restore(log);
        _ = runtime.Remember("usb", Receiver("usb", 0, "LianLi/wa/ctl", "LianLi/wa/f0/fan"));
        runtime.Restore(log); // read now: the numbering moves on

        Assert.Null(runtime.RememberUnder(0, "dongles", Pair("dongles", 1, "LianLi/wa/ctl", "LianLi/wa/f0/fan"), out IReadOnlyCollection<string> claimed, out _));
        Assert.Empty(claimed);
        Assert.Equal("usb", runtime.OwnerOf("LianLi/wa/ctl"));
    }

    // A stand-in's rebuild claims the device only for the instance that owns the worker, under the
    // current numbering, and while no other build has it; an instance that has stopped, or been
    // succeeded, claims nothing however late its rebuild thread gets here.
    [Fact]
    public void TryBeginRebuild_ClaimsOnlyForTheOwner_UnderTheCurrentNumbering_WhileNoOtherBuildHasTheDevice() {
        var clock = new FakeClock();
        var store = new FakeRememberedControllerStore { UnreadableLoads = 1 };
        store.Stored.Add(new StoredController("b", Remembered("b", 0), clock.UtcNow));
        var runtime = new PluginRuntime(clock, store);
        object owner = new object();
        Assert.Throws<ArgumentNullException>(() => runtime.TryBeginRebuild(null!, 0, "a", 0));
        Assert.False(runtime.TryBeginRebuild(owner, 0, "a", 0)); // nothing running

        runtime.Start(owner, Array.Empty<IFanDevice>(), new FakeLogger());
        Assert.True(runtime.TryBeginRebuild(owner, 0, "a", 0));
        Assert.False(runtime.TryBeginRebuild(owner, 0, "a", 0)); // that build still has it
        runtime.EndBuild("a");
        Assert.False(runtime.TryBeginRebuild(new object(), 0, "a", 0)); // another instance

        runtime.Restore(new FakeLogger());
        _ = runtime.Remember("a", Remembered("a", 0));
        runtime.Restore(new FakeLogger()); // the numbering moves on
        Assert.False(runtime.TryBeginRebuild(owner, 0, "a", 0));
        Assert.True(runtime.TryBeginRebuild(owner, 1, "a", 1));
        runtime.EndBuild("a");

        runtime.Stop();
        Assert.False(runtime.TryBeginRebuild(owner, 1, "a", 1));
        Assert.False(runtime.IsBuilding("a"));
    }

    private static RememberedController Channels(string path, int index, params string[] ids)
        => new RememberedController(Plan(path), index, new FakeFanDevice(ids), new FakeClock().UtcNow);

    // A first-run controller that moves to a new path in the same process is still new; one that has
    // had a sensor is not new again once a prune empties it.
    [Fact]
    public void IsNew_FollowsAMove_AndIsNotRegainedWhenASensorIsPruned() {
        var clock = new FakeClock();
        var runtime = new PluginRuntime(clock, new FakeRememberedControllerStore());
        runtime.Restore(new FakeLogger());
        _ = runtime.Remember("old", Sensorless("old", 0));

        Assert.Equal("old", runtime.TakeOverMoved(Plan("moved"), new HashSet<string> { "moved" }));
        Assert.True(runtime.IsNew("moved"));

        _ = runtime.Remember("had", Channels("had", 1, "LianLi/1/ch0/ctl"));
        clock.Advance(PluginRuntime.ForgetAfter + TimeSpan.FromDays(1));
        _ = runtime.Remember("had", Sensorless("had", 1));
        runtime.ForgetExpired(new FakeLogger());

        Assert.Empty(runtime.Recall("had")!.Channels);
        Assert.False(runtime.IsNew("had"));
    }

    [Fact]
    public void Persist_SavesEveryRememberedController_InIndexOrder_WithWhenItWasLastBuilt() {
        var clock = new FakeClock();
        var store = new FakeRememberedControllerStore();
        var runtime = new PluginRuntime(clock, store);
        runtime.Restore(new FakeLogger());
        runtime.Remember("b", Remembered("b", 1));
        clock.Advance(TimeSpan.FromMinutes(5));
        runtime.Remember("a", Remembered("a", 0));

        runtime.Persist(new FakeLogger());

        Assert.Equal(new[] { "a", "b" }, store.Stored.Select(stored => stored.Key));
        Assert.Equal(clock.UtcNow, store.Stored[0].LastSeenUtc);
        Assert.Equal(clock.UtcNow - TimeSpan.FromMinutes(5), store.Stored[1].LastSeenUtc);
    }

    [Fact]
    public void TryTakeRefresh_AfterTheClockWentBack_IsNotHeldForTheJump() {
        var clock = new FakeClock();
        var runtime = new PluginRuntime(clock, new FakeRememberedControllerStore());
        object owner = new object();
        runtime.Start(owner, Array.Empty<IFanDevice>(), new FakeLogger());
        runtime.RequestRefresh("first", new FakeLogger());
        Assert.True(runtime.TryTakeRefresh(owner, out _));

        clock.Advance(TimeSpan.FromHours(-1));
        runtime.RequestRefresh("second", new FakeLogger());

        Assert.True(runtime.TryTakeRefresh(owner, out _));
    }
}
