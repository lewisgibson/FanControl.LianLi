using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Plugin;
using FanControl.LianLi.Tests.Fakes;
using Xunit;

namespace FanControl.LianLi.Tests.Plugin;

/// <summary>
/// The scan's controllers build side by side under one deadline, and whatever finishes after it is
/// handed to a callback rather than leaked.
/// </summary>
public sealed class ControllerBuilderTests {
    private static void NoLateController(int build, IFanDevice controller)
        => throw new InvalidOperationException("no build should have finished late");

    private static void NoLateFailure(int build, Exception error)
        => throw new InvalidOperationException("no build should have failed late");

    [Fact]
    public void Run_ReportsEachBuildInOrder() {
        var built = new FakeFanDevice("c0");
        var failure = new InvalidOperationException("will not open");

        BuildOutcome[] outcomes = ControllerBuilder.Run(
            new Func<IFanDevice>[] { () => built, () => throw failure },
            5000,
            NoLateController, NoLateFailure,
            new FakeLogger());

        Assert.Same(built, outcomes[0].Controller);
        Assert.Null(outcomes[0].Error);
        Assert.False(outcomes[0].TimedOut);
        Assert.Null(outcomes[1].Controller);
        Assert.Same(failure, outcomes[1].Error);
        Assert.False(outcomes[1].TimedOut);
    }

    [Fact]
    public void Run_BuildsSideBySide_SoTheirWaitsDoNotAddUp() {
        // Each build waits until all three have started, which they can only do if they overlap.
        using var started = new CountdownEvent(3);
        Func<IFanDevice> together = () => {
            started.Signal();
            return started.Wait(TimeSpan.FromSeconds(10)) ? new FakeFanDevice("c") : throw new TimeoutException("the builds ran one at a time");
        };

        BuildOutcome[] outcomes = ControllerBuilder.Run(new[] { together, together, together }, 20_000, NoLateController, NoLateFailure, new FakeLogger());

        Assert.All(outcomes, outcome => Assert.NotNull(outcome.Controller));
    }

    [Fact]
    public void Run_ABuildStillRunningAtTheDeadline_IsLate_AndItsResultReachesTheCallback() {
        using var release = new ManualResetEventSlim(false);
        using var handed = new ManualResetEventSlim(false);
        var device = new FakeFanDevice("c0");
        int lateBuild = -1;
        IFanDevice? lateController = null;

        BuildOutcome[] outcomes = ControllerBuilder.Run(
            new Func<IFanDevice>[] {
                () => new FakeFanDevice("fast"),
                () => {
                    release.Wait();
                    return device;
                },
            },
            50,
            (build, controller) => {
                lateBuild = build;
                lateController = controller;
                handed.Set();
            },
            NoLateFailure,
            new FakeLogger());

        Assert.NotNull(outcomes[0].Controller);
        Assert.True(outcomes[1].TimedOut);
        Assert.Null(outcomes[1].Controller);
        Assert.Null(outcomes[1].Error);

        release.Set();
        Assert.True(handed.Wait(5000, TestContext.Current.CancellationToken));
        Assert.Equal(1, lateBuild);
        Assert.Same(device, lateController);
    }

    [Fact]
    public void Run_ALateFailure_ReachesTheCallbackWithItsError() {
        using var release = new ManualResetEventSlim(false);
        using var handed = new ManualResetEventSlim(false);
        var failure = new InvalidOperationException("wedged");
        Exception? lateError = null;

        _ = ControllerBuilder.Run(
            new Func<IFanDevice>[] {
                () => {
                    release.Wait();
                    throw failure;
                },
            },
            20,
            NoLateController,
            (build, error) => {
                lateError = error;
                handed.Set();
            },
            new FakeLogger());

        release.Set();
        Assert.True(handed.Wait(5000, TestContext.Current.CancellationToken));
        Assert.Same(failure, lateError);
    }

    [Fact]
    public void Run_ACallbackThatThrows_IsLogged_NeverEscapesItsThread() {
        using var release = new ManualResetEventSlim(false);
        var logger = new FakeLogger();

        _ = ControllerBuilder.Run(
            new Func<IFanDevice>[] {
                () => {
                    release.Wait();
                    return new FakeFanDevice("c0");
                },
            },
            20,
            (build, controller) => throw new InvalidOperationException("callback broke"),
            NoLateFailure,
            logger);

        release.Set();
        Assert.True(SpinWait.SpinUntil(
            () => logger.Messages.Any(m => m.Contains("controller build 0: handling its late result failed: callback broke")),
            TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Run_NothingToBuild_ReturnsNothing()
        => Assert.Empty(ControllerBuilder.Run(Array.Empty<Func<IFanDevice>>(), 0, NoLateController, NoLateFailure, new FakeLogger()));

    [Fact]
    public void Run_RejectsBadArguments() {
        var builds = new Func<IFanDevice>[] { () => new FakeFanDevice("c") };

        Assert.Throws<ArgumentNullException>(() => ControllerBuilder.Run(null!, 1, NoLateController, NoLateFailure, new FakeLogger()));
        Assert.Throws<ArgumentOutOfRangeException>(() => ControllerBuilder.Run(builds, -1, NoLateController, NoLateFailure, new FakeLogger()));
        Assert.Throws<ArgumentNullException>(() => ControllerBuilder.Run(builds, 1, null!, NoLateFailure, new FakeLogger()));
        Assert.Throws<ArgumentNullException>(() => ControllerBuilder.Run(builds, 1, NoLateController, null!, new FakeLogger()));
        Assert.Throws<ArgumentNullException>(() => ControllerBuilder.Run(builds, 1, NoLateController, NoLateFailure, null!));
        Assert.Throws<ArgumentException>(() => ControllerBuilder.Run(new Func<IFanDevice>[] { null! }, 1, NoLateController, NoLateFailure, new FakeLogger()));
    }

    [Fact]
    public void BuildOutcome_RejectsMissingResults() {
        Assert.Throws<ArgumentNullException>(() => BuildOutcome.Built(null!));
        Assert.Throws<ArgumentNullException>(() => BuildOutcome.Failed(null!));
        Assert.Throws<ArgumentNullException>(() => BuildOutcome.Late().Deliver(0, null!, NoLateFailure));
        Assert.Throws<ArgumentNullException>(() => BuildOutcome.Late().Deliver(0, NoLateController, null!));
    }

    [Fact]
    public void BuildOutcome_DeliversToTheCallbackThatFits_AndALateOneToNeither() {
        var device = new FakeFanDevice("c0");
        var failure = new InvalidOperationException("x");
        var delivered = new List<string>();

        BuildOutcome.Built(device).Deliver(1, (i, c) => delivered.Add("built " + i), (i, e) => delivered.Add("failed " + i));
        BuildOutcome.Failed(failure).Deliver(2, (i, c) => delivered.Add("built " + i), (i, e) => delivered.Add("failed " + i));
        BuildOutcome.Late().Deliver(3, (i, c) => delivered.Add("built " + i), (i, e) => delivered.Add("failed " + i));

        Assert.Equal(new[] { "built 1", "failed 2" }, delivered);
    }
}
