using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Tests.Fakes;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

/// <summary>
/// The stand-in for a controller the plugin could not build on this scan: it registers the sensors
/// the controller had, so the user's curve bindings survive, and rebuilds the real controller in the
/// background. Most tests run the rebuild inline, so each attempt lands where the test can see it;
/// the last few run it on its own thread, as the plugin does.
/// </summary>
public class ReconnectingFanDeviceTests {
    private static readonly ChannelDescriptor[] TwoChannels = {
        new ChannelDescriptor("ctl/0", "Channel 1", "fan/0", "Channel 1 RPM"),
        new ChannelDescriptor("ctl/1", "Channel 2", "fan/1", "Channel 2 RPM"),
    };

    private static readonly TemperatureDescriptor[] OneTemperature = {
        new TemperatureDescriptor("temp/0", "Coolant"),
    };

    private static ReconnectingFanDevice NewDevice(
        Func<IFanDevice> build,
        FakeLogger? logger = null,
        IReadOnlyList<ChannelDescriptor>? channels = null,
        IReadOnlyList<TemperatureDescriptor>? temperatures = null,
        IReadOnlyList<FanSpeedDescriptor>? fanSpeeds = null)
        => new ReconnectingFanDevice(
            0,
            channels ?? TwoChannels,
            fanSpeeds ?? Array.Empty<FanSpeedDescriptor>(),
            temperatures ?? Array.Empty<TemperatureDescriptor>(),
            build,
            logger ?? new FakeLogger(),
            work => work());

    [Fact]
    public void BeforeItRebuilds_ItStillPresentsTheRememberedSensors() {
        var device = NewDevice(() => throw new IOException("still gone"));

        Assert.False(device.IsConnected);
        Assert.Equal(2, device.ChannelCount);
        Assert.True(device.IsChannelPopulated(0));
        Assert.Equal("ctl/0", device.Describe(0).ControlId);
        Assert.Equal("Channel 2 RPM", device.Describe(1).RpmName);
        Assert.Equal(0f, device.GetRpm(1));
    }

    [Fact]
    public void FirstTickBuildsTheController_AndLaterTicksDriveIt() {
        var built = new FakeFanDevice("ctl/0", "ctl/1");
        int builds = 0;
        var logger = new FakeLogger();
        var device = NewDevice(() => { builds++; return built; }, logger);

        device.ApplyPending();
        device.PollRpm();

        // The call that starts the rebuild does not wait for it, so it drives nothing; the next does.
        Assert.Equal(1, builds);
        Assert.True(device.IsConnected);
        Assert.Equal(0, built.ApplyCount);
        Assert.Equal(1, built.PollCount);
        Assert.Contains(logger.Messages, m => m.Contains("rebuilt after 1 attempt(s): 2 of 2 remembered channel(s) matched"));
    }

    [Fact]
    public void ARebuildFailureIsLogged_AndRetriedOnTheBackoff() {
        int attempts = 0;
        var built = new FakeFanDevice("ctl/0", "ctl/1");
        var logger = new FakeLogger();
        var device = NewDevice(
            () => {
                attempts++;
                if (attempts < 2) {
                    throw new IOException("not yet");
                }

                return built;
            },
            logger);

        // The first offer attempts and fails, and the failure is logged with the attempt count.
        device.ApplyPending();
        Assert.Contains(logger.Messages, m => m.Contains("C0 reconnect attempt 1 failed: not yet"));
        Assert.False(device.IsConnected);

        // The next offers are skipped rather than hammering a device that is not there...
        for (int offer = 0; offer < 10; offer++) {
            device.ApplyPending();
        }

        Assert.Equal(1, attempts);

        // ...until the schedule comes round again.
        device.ApplyPending();
        Assert.Equal(2, attempts);
        Assert.True(device.IsConnected);
    }

    [Fact]
    public void TargetsSetWhileDisconnectedAreHandedOverOnTheRebuild() {
        var built = new FakeFanDevice("ctl/0", "ctl/1");
        var device = NewDevice(() => built);

        device.SetTarget(0, 40);
        device.SetTarget(1, 60);
        device.ReleaseChannel(1); // released again before the rebuild: nothing to hand over
        Assert.Empty(built.Targets);

        device.ApplyPending();

        KeyValuePair<int, int> handed = Assert.Single(built.Targets);
        Assert.Equal(0, handed.Key);
        Assert.Equal(40, handed.Value);
    }

    [Fact]
    public void OnceConnectedEveryCallGoesStraightThrough() {
        var built = new FakeFanDevice("ctl/0", "ctl/1");
        built.SetRpm(1, 1234f);
        var device = NewDevice(() => built);
        device.ApplyPending();

        device.SetTarget(1, 55);
        device.ReleaseChannel(0);

        Assert.Equal(1234f, device.GetRpm(1));
        Assert.Contains(built.Targets, t => t.Key == 1 && t.Value == 55);
        Assert.Contains(0, built.Released);
    }

    [Fact]
    public void ChannelsAreMatchedByControlId_NotByPosition() {
        // The rebuilt controller reports the same two channels in the other order, and a third.
        var built = new FakeFanDevice("ctl/1", "other", "ctl/0");
        built.SetRpm(0, 111f);
        built.SetRpm(2, 222f);
        var device = NewDevice(() => built);

        device.ApplyPending();
        device.SetTarget(0, 30);

        Assert.Equal(222f, device.GetRpm(0)); // the remembered ctl/0 is the built controller's third
        Assert.Equal(111f, device.GetRpm(1));
        Assert.Contains(built.Targets, t => t.Key == 2 && t.Value == 30);
    }

    [Fact]
    public void AChannelTheRebuiltControllerNoLongerHasReportsNothing() {
        var built = new FakeFanDevice("ctl/0");
        var logger = new FakeLogger();
        var device = NewDevice(() => built, logger);

        device.ApplyPending();
        device.SetTarget(1, 50);

        Assert.Equal(0f, device.GetRpm(1));
        Assert.DoesNotContain(built.Targets, t => t.Value == 50);
        Assert.Contains(logger.Messages, m => m.Contains("1 of 2 remembered channel(s) matched"));
    }

    [Fact]
    public void TemperaturesAreRememberedAndForwardedOnceRebuilt() {
        var built = new FakeFanDevice(new[] { "ctl/0", "ctl/1" }, new[] { "temp/0" });
        built.SetTemperature(0, 31f);
        var device = NewDevice(() => built, temperatures: OneTemperature);

        Assert.Equal(1, device.TemperatureCount);
        Assert.Equal("temp/0", device.DescribeTemperature(0).Id);
        Assert.Null(device.GetTemperature(0)); // nothing to read until it is rebuilt

        device.ApplyPending();

        Assert.Equal(31f, device.GetTemperature(0));
    }

    [Fact]
    public void ATemperatureTheRebuiltControllerNoLongerHasReportsNothing() {
        var device = NewDevice(() => new FakeFanDevice("ctl/0", "ctl/1"), temperatures: OneTemperature);

        device.ApplyPending();

        Assert.Null(device.GetTemperature(0));
    }

    [Fact]
    public void AReplayRegisteredBeforeTheRebuildIsHandedToTheController() {
        var built = new FakeFanDevice("ctl/0", "ctl/1");
        var device = NewDevice(() => built);
        void Replay() { }

        device.ReplayOnReconnect(Replay);
        device.ApplyPending();

        Assert.Equal((Action)Replay, built.Replay);
    }

    [Fact]
    public void AReplayRegisteredAfterTheRebuildGoesStraightToTheController() {
        var built = new FakeFanDevice("ctl/0", "ctl/1");
        var device = NewDevice(() => built);
        device.ApplyPending();
        void Replay() { }

        device.ReplayOnReconnect(Replay);

        Assert.Equal((Action)Replay, built.Replay);
    }

    [Fact]
    public void DisposeReleasesTheControllerItBuilt() {
        var built = new FakeFanDevice("ctl/0", "ctl/1");
        var device = NewDevice(() => built);
        device.ApplyPending();

        device.Dispose();

        Assert.True(built.IsDisposed);
    }

    [Fact]
    public void DisposeBeforeARebuildIsHarmless() {
        var device = NewDevice(() => throw new IOException("gone"));

        device.Dispose();
    }

    [Fact]
    public void OnItsOwnThread_TheRebuildDoesNotHoldUpTheTick() {
        using var gate = new ManualResetEventSlim(false);
        var built = new FakeFanDevice("ctl/0", "ctl/1");
        int builds = 0;
        var device = new ReconnectingFanDevice(
            0, TwoChannels, Array.Empty<FanSpeedDescriptor>(), Array.Empty<TemperatureDescriptor>(), () => { builds++; gate.Wait(); return built; }, new FakeLogger());

        device.ApplyPending(); // starts the rebuild and returns at once
        for (int offer = 0; offer < 20; offer++) {
            device.PollRpm(); // no second rebuild while the first is running
        }

        Assert.False(device.IsConnected);
        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => device.IsConnected, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, builds);
    }

    [Fact]
    public void DisposedWhileTheRebuildRuns_TheControllerItProducesIsDisposed() {
        using var gate = new ManualResetEventSlim(false);
        var built = new FakeFanDevice("ctl/0", "ctl/1");
        var device = new ReconnectingFanDevice(
            0, TwoChannels, Array.Empty<FanSpeedDescriptor>(), Array.Empty<TemperatureDescriptor>(), () => { gate.Wait(); return built; }, new FakeLogger());
        device.ApplyPending();

        device.Dispose();
        gate.Set();

        Assert.True(SpinWait.SpinUntil(() => built.IsDisposed, TimeSpan.FromSeconds(5)));
        Assert.False(device.IsConnected);
    }

    [Fact]
    public void DisposedWhileTheRebuildRuns_AControllerThatThrowsOnClose_IsLogged() {
        using var gate = new ManualResetEventSlim(false);
        var built = new FakeFanDevice("ctl/0", "ctl/1") { DisposeFault = new IOException("handle gone") };
        var logger = new FakeLogger();
        var device = new ReconnectingFanDevice(
            0, TwoChannels, Array.Empty<FanSpeedDescriptor>(), Array.Empty<TemperatureDescriptor>(), () => { gate.Wait(); return built; }, logger);
        device.ApplyPending();

        device.Dispose();
        gate.Set();

        Assert.True(SpinWait.SpinUntil(
            () => logger.Messages.Any(m => m == "C0 closing a controller rebuilt after shutdown failed: handle gone"), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void AnOfferedController_IsTakenWhileDisconnected_AndRefusedOnceConnectedOrDisposed() {
        var offered = new FakeFanDevice("ctl/0", "ctl/1");
        ReconnectingFanDevice device = NewDevice(() => throw new IOException("not yet"));
        device.SetTarget(1, 40);

        Assert.True(device.Offer(offered));
        Assert.True(device.IsConnected);
        Assert.Equal(new KeyValuePair<int, int>(1, 40), Assert.Single(offered.Targets));
        Assert.False(device.Offer(new FakeFanDevice("ctl/0", "ctl/1")));

        device.Dispose();
        ReconnectingFanDevice disposed = NewDevice(() => throw new IOException("not yet"));
        disposed.Dispose();
        Assert.False(disposed.Offer(new FakeFanDevice("ctl/0")));
        Assert.Throws<ArgumentNullException>(() => disposed.Offer(null!));
    }

    // A TL hub that came back with one of its remembered fans missing, which answers a poll later:
    // the controller adds it, and the stand-in wrapped around it matches it and hands it its target.
    [Fact]
    public void ARememberedTlFanThatAnswersLater_GetsItsReadingAndItsTarget() {
        static byte[] Handshake(params (int Port, int Fan, int Rpm)[] fans) {
            var payload = new List<byte>();
            foreach ((int port, int fan, int rpm) in fans) {
                payload.Add((byte)(0x80 | (port << 4) | fan));
                payload.Add((byte)(rpm >> 8));
                payload.Add((byte)(rpm & 0xFF));
            }

            return FanControl.LianLi.Protocol.CommandPacket.Build(0xA1, payload.ToArray());
        }

        var transport = new FakeDeviceTransport();
        transport.ReadReplies.Enqueue(Handshake((0, 0, 1000)));
        var tl = new TlFanController(0, transport, new FakeClock(), new FakeLogger());
        var remembered = new[] {
            new ChannelDescriptor("LianLi/0/p0f0/ctl", "a", "LianLi/0/p0f0/fan", "a"),
            new ChannelDescriptor("LianLi/0/p0f1/ctl", "b", "LianLi/0/p0f1/fan", "b"),
        };
        ReconnectingFanDevice device = ReconnectingFanDevice.Around(
            tl, 0, remembered, Array.Empty<FanSpeedDescriptor>(), Array.Empty<TemperatureDescriptor>(), () => throw new IOException("unused"), new FakeLogger());
        device.SetTarget(1, 70);
        Assert.Equal(0f, device.GetRpm(1));

        // Three polls in a row add the fan; the one after puts the remembered channel on it.
        for (int poll = 0; poll < 4; poll++) {
            transport.ReadReplies.Enqueue(Handshake((0, 0, 1000), (0, 1, 1100)));
            device.PollRpm();
        }

        transport.Writes.Clear();
        device.ApplyPending();

        Assert.Equal(1100f, device.GetRpm(1));
        Assert.Contains(transport.Writes, w => w[6] == 0x01 && w[7] == 70); // p0f1 set to 70%
    }

    [Fact]
    public void OnceDisposed_NoRebuildStarts() {
        int builds = 0;
        var device = NewDevice(() => { builds++; return new FakeFanDevice("ctl/0", "ctl/1"); });

        device.Dispose();
        device.ApplyPending();

        Assert.Equal(0, builds);
    }

    [Fact]
    public void NullArgumentsThrow() {
        Assert.Throws<ArgumentNullException>(
            () => new ReconnectingFanDevice(0, null!, Array.Empty<FanSpeedDescriptor>(), OneTemperature, () => new FakeFanDevice(), new FakeLogger()));
        Assert.Throws<ArgumentNullException>(
            () => new ReconnectingFanDevice(0, TwoChannels, Array.Empty<FanSpeedDescriptor>(), null!, () => new FakeFanDevice(), new FakeLogger()));
        Assert.Throws<ArgumentNullException>(
            () => new ReconnectingFanDevice(0, TwoChannels, Array.Empty<FanSpeedDescriptor>(), OneTemperature, null!, new FakeLogger()));
        Assert.Throws<ArgumentNullException>(
            () => new ReconnectingFanDevice(0, TwoChannels, Array.Empty<FanSpeedDescriptor>(), OneTemperature, () => new FakeFanDevice(), null!));
        Assert.Throws<ArgumentNullException>(() => NewDevice(() => new FakeFanDevice()).ReplayOnReconnect(null!));
        Assert.Throws<ArgumentNullException>(
            () => new ReconnectingFanDevice(0, TwoChannels, Array.Empty<FanSpeedDescriptor>(), OneTemperature, () => new FakeFanDevice(), new FakeLogger(), null!));
    }

    [Fact]
    public void ATemperatureOfARebuiltControllerThatMeasuresNone_ReportsNothing() {
        // A wired controller measures no temperatures at all.
        var built = new FanController(0, new FakeDeviceTransport(), new FanControl.LianLi.Protocol.SlProtocol(), new bool[4], new FakeClock(), new FakeLogger());
        var device = NewDevice(() => built, temperatures: OneTemperature, channels: new[] { built.Describe(0) });

        device.ApplyPending();

        Assert.True(device.IsConnected);
        Assert.Null(device.GetTemperature(0));
    }

    [Fact]
    public void ATemperatureMatchedByIdNotPosition_IsForwarded() {
        var built = new FakeFanDevice(new[] { "ctl/0", "ctl/1" }, new[] { "temp/other", "temp/0" });
        built.SetTemperature(1, 27f);
        var device = NewDevice(() => built, temperatures: OneTemperature);

        device.ApplyPending();

        Assert.Equal(27f, device.GetTemperature(0));
    }

    [Fact]
    public void OnceConnected_ApplyPendingDrivesTheController() {
        var built = new FakeFanDevice("ctl/0", "ctl/1");
        var device = NewDevice(() => built);
        device.PollRpm(); // builds

        device.ApplyPending();

        Assert.Equal(1, built.ApplyCount);
    }

    [Fact]
    public void ReleasingAChannelTheRebuiltControllerLacks_ReleasesNothing() {
        var built = new FakeFanDevice("ctl/0");
        var device = NewDevice(() => built);
        device.ApplyPending();

        device.ReleaseChannel(1);

        Assert.Empty(built.Released);
    }

    [Fact]
    public void FanSpeedsOfAGroupController_AreMatchedByIdAndForwarded() {
        var built = new FakeFanGroupDevice("ctl/0", "fan/b", "fan/a");
        built.SetSpeed(1, 900f);
        var device = NewDevice(
            () => built,
            channels: new[] { new ChannelDescriptor("ctl/0", "Group", "ctl/0/fan", "Group") },
            fanSpeeds: new[] { new FanSpeedDescriptor("fan/a", "A"), new FanSpeedDescriptor("fan/gone", "Gone") });

        Assert.Equal(2, device.FanSpeedCount);
        Assert.Equal("fan/a", device.DescribeFanSpeed(0).Id);
        Assert.Equal(0f, device.GetFanSpeed(0)); // nothing until it is rebuilt

        device.ApplyPending();

        Assert.Equal(900f, device.GetFanSpeed(0));
        Assert.Equal(0f, device.GetFanSpeed(1)); // the rebuilt group no longer has that fan
    }

    [Fact]
    public void FanSpeedsOfAWiredController_AreItsChannelsRpm() {
        var built = new FakeFanDevice("ctl/0", "ctl/1");
        built.SetRpm(1, 1234f);
        var device = NewDevice(
            () => built, fanSpeeds: new[] { new FanSpeedDescriptor("ctl/1/fan", "Channel 2 RPM"), new FanSpeedDescriptor("nope", "x") });

        device.ApplyPending();

        Assert.Equal(1234f, device.GetFanSpeed(0));
        Assert.Equal(0f, device.GetFanSpeed(1));
    }

    [Fact]
    public void NullFanSpeeds_Throw()
        => Assert.Throws<ArgumentNullException>(
            () => new ReconnectingFanDevice(0, TwoChannels, null!, OneTemperature, () => new FakeFanDevice(), new FakeLogger()));

    [Fact]
    public void Around_PresentsTheRememberedSensors_ForwardingThoseTheControllerHas() {
        var built = new FakeFanDevice("ctl/0");
        built.SetRpm(0, 800f);
        var logger = new FakeLogger();

        ReconnectingFanDevice device = ReconnectingFanDevice.Around(
            built, 0, TwoChannels, Array.Empty<FanSpeedDescriptor>(), Array.Empty<TemperatureDescriptor>(),
            () => throw new InvalidOperationException("never rebuilt"), logger);

        Assert.True(device.IsConnected);
        Assert.Equal(2, device.ChannelCount);
        Assert.Equal(800f, device.GetRpm(0));
        Assert.Equal(0f, device.GetRpm(1));
        Assert.Contains("C0 kept all 2 remembered channel(s); 1 of them are there now", logger.Messages);
        Assert.Throws<ArgumentNullException>(() => ReconnectingFanDevice.Around(
            null!, 0, TwoChannels, Array.Empty<FanSpeedDescriptor>(), Array.Empty<TemperatureDescriptor>(), () => built, logger));
    }

    // A wireless-like controller that hears one more group the first time it is polled.
    private sealed class GrowingDevice : IFanDevice, IFanSpeedSource {
        private readonly List<string> _controls = new List<string> { "ctl/0" };

        public List<KeyValuePair<int, int>> Targets { get; } = new List<KeyValuePair<int, int>>();

        public int ChannelCount => _controls.Count;

        public int FanSpeedCount => _controls.Count;

        public bool IsChannelPopulated(int channel) => true;

        public ChannelDescriptor Describe(int channel) => new ChannelDescriptor(_controls[channel], "c", _controls[channel] + "/fan", "r");

        public FanSpeedDescriptor DescribeFanSpeed(int index) => new FanSpeedDescriptor(_controls[index] + "/fan", "r");

        public float GetFanSpeed(int index) => 700f + index;

        public void SetTarget(int channel, int duty) => Targets.Add(new KeyValuePair<int, int>(channel, duty));

        public void ReleaseChannel(int channel) {
        }

        public float GetRpm(int channel) => 0f;

        public void ApplyPending() {
        }

        public void PollRpm() {
            if (_controls.Count == 1) {
                _controls.Add("ctl/1");
            }
        }

        public void ReplayOnReconnect(Action replay) {
        }

        public void Dispose() {
        }
    }

    [Fact]
    public void AControllerThatGainsSensors_HasThemMatchedOnTheNextPoll_WithTheirTargets() {
        var built = new GrowingDevice();
        var logger = new FakeLogger();
        var device = NewDevice(
            () => built,
            logger,
            fanSpeeds: new[] { new FanSpeedDescriptor("ctl/0/fan", "a"), new FanSpeedDescriptor("ctl/1/fan", "b") });
        device.SetTarget(1, 45); // set while the second group was not yet heard
        device.ApplyPending();   // builds; only ctl/0 is there
        Assert.Equal(0f, device.GetFanSpeed(1));

        device.PollRpm(); // the controller hears the second group

        Assert.Equal(701f, device.GetFanSpeed(1));
        Assert.Contains(built.Targets, t => t.Key == 1 && t.Value == 45);
        Assert.Contains("C0: 2 of 2 remembered channel(s) are there now", logger.Messages);
    }
}
