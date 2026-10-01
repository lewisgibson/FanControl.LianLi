using System;
using System.Collections.Generic;
using System.Threading;
using FanControl.LianLi.Tests.Fakes;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

public class BoundedDeviceCallTests {
    // A late-failure sink for the tests that do not expect one: any report fails the test.
    private static readonly Action<Exception> NoLateFailure =
        failure => throw new InvalidOperationException("Unexpected late failure: " + failure.Message);

    // A return sink for the tests that are not about when a call is reported done with.
    private static readonly Action Ignored = () => { };

    // For an onTimeout that needs the call under way: whether it is, once the thread has decided
    // either way. It sets 'running' first thing in the call; a thread that found the slot claimed
    // instead closes the handle it opened itself, which the caller does only after onTimeout.
    private static bool CallIsRunning(ManualResetEventSlim running, FakeThreadCanceller threads) {
        _ = SpinWait.SpinUntil(() => running.IsSet || threads.Closed.Count > 0, TimeSpan.FromSeconds(5));
        return running.IsSet;
    }

    [Fact]
    public void TryRun_CompletesInTime_ReturnsTrue_AndDoesNotCancel() {
        bool cancelled = false;

        bool completed = BoundedDeviceCall.TryRun(_ => { }, 1000, () => cancelled = true, NoLateFailure, Ignored);

        Assert.True(completed);
        Assert.False(cancelled);
    }

    [Fact]
    public void TryRun_CallThrows_RethrowsOnCallerThread() {
        var thrown = new InvalidOperationException("boom");

        InvalidOperationException caught = Assert.Throws<InvalidOperationException>(
            () => BoundedDeviceCall.TryRun(_ => throw thrown, 1000, () => { }, NoLateFailure, Ignored));

        Assert.Same(thrown, caught);
    }

    [Fact]
    public void TryRun_CallBlocks_ReturnsFalse_AndInvokesOnTimeout() {
        AbandonedCallScenario.Run(() => {
            using var release = new ManualResetEventSlim(false);
            using var running = new ManualResetEventSlim(false);
            using var returned = new ManualResetEventSlim(false);
            bool cancelled = false;

            // The call blocks past the timeout; TryRun must give up and invoke onTimeout. The real
            // transport's onTimeout cancels the I/O so the blocked native call returns - here releasing
            // the gate stands in for that, letting the abandoned thread unwind and the test end cleanly.
            bool completed = BoundedDeviceCall.TryRun(
                _ => { running.Set(); release.Wait(CancellationToken.None); },
                50,
                () => { cancelled = true; release.Set(); },
                _ => { },
                returned.Set);

            Assert.False(completed);
            Assert.True(cancelled);
            Assert.True(returned.Wait(5000, TestContext.Current.CancellationToken));
            return running.IsSet;
        });
    }

    [Fact]
    public void TryRun_TimesOut_MarksTheCallAbandoned_BeforeCancellingItsIo() {
        AbandonedCallScenario.Run(() => {
            using var release = new ManualResetEventSlim(false);
            using var running = new ManualResetEventSlim(false);
            using var returned = new ManualResetEventSlim(false);
            var threads = new FakeThreadCanceller();
            CancellationToken seen = default;
            bool underWay = false;
            bool abandonedWhenCancelled = false;

            bool completed = BoundedDeviceCall.TryRun(
                token => { seen = token; running.Set(); release.Wait(CancellationToken.None); },
                50,
                () => {
                    underWay = CallIsRunning(running, threads);
                    abandonedWhenCancelled = seen.IsCancellationRequested;
                    release.Set();
                },
                NoLateFailure,
                returned.Set,
                threads);

            Assert.False(completed);
            Assert.True(returned.Wait(5000, TestContext.Current.CancellationToken));
            if (!underWay) {
                return false;
            }

            Assert.True(abandonedWhenCancelled);
            return true;
        });
    }

    [Fact]
    public void TryRun_Completes_WithATokenThatWasNeverCancelled() {
        CancellationToken seen = default;

        Assert.True(BoundedDeviceCall.TryRun(token => seen = token, 1000, () => { }, NoLateFailure, Ignored));

        Assert.True(seen.CanBeCanceled);
        Assert.False(seen.IsCancellationRequested);
    }

    [Fact]
    public void TryRun_ACallThatStopsOnSeeingItWasAbandoned_IsNotReportedAsAFailure() {
        // The call returns from its cancelled native call, checks its token and stops: the unwind the
        // bound asked for, which the caller has already logged as a timeout. A report, had there been
        // one, is made before the call is reported returned, so waiting for that proves there was none.
        AbandonedCallScenario.Run(() => {
            using var release = new ManualResetEventSlim(false);
            using var running = new ManualResetEventSlim(false);
            using var returned = new ManualResetEventSlim(false);
            int reports = 0;

            bool completed = BoundedDeviceCall.TryRun(
                token => {
                    running.Set();
                    release.Wait(CancellationToken.None);
                    token.ThrowIfCancellationRequested();
                },
                50,
                () => release.Set(),
                _ => Interlocked.Increment(ref reports),
                returned.Set,
                new FakeThreadCanceller());

            Assert.False(completed);
            Assert.True(returned.Wait(5000, TestContext.Current.CancellationToken));
            Assert.Equal(0, Volatile.Read(ref reports));
            return running.IsSet;
        });
    }

    [Fact]
    public void TryRun_ACancellationFromAnyOtherToken_IsAFailure_AndReported() {
        AbandonedCallScenario.Run(() => {
            using var release = new ManualResetEventSlim(false);
            using var running = new ManualResetEventSlim(false);
            using var returned = new ManualResetEventSlim(false);
            using var other = new CancellationTokenSource();
            other.Cancel();
            Exception? late = null;

            bool completed = BoundedDeviceCall.TryRun(
                _ => { running.Set(); release.Wait(CancellationToken.None); other.Token.ThrowIfCancellationRequested(); },
                50,
                () => { },
                failure => late = failure,
                returned.Set,
                new FakeThreadCanceller());

            Assert.False(completed);
            release.Set();
            Assert.True(returned.Wait(5000, TestContext.Current.CancellationToken));
            if (!running.IsSet) {
                return false;
            }

            Assert.IsType<OperationCanceledException>(late);
            return true;
        });
    }

    [Fact]
    public void TryRun_ACallStoppingOnItsTokenWhileTheCallerCancels_IsNotReported() {
        // The same unwind, landing before the caller has marked the call abandoned: the caller finds it
        // finished and must still not report it.
        AbandonedCallScenario.Run(() => {
            using var release = new ManualResetEventSlim(false);
            using var running = new ManualResetEventSlim(false);
            using var returned = new ManualResetEventSlim(false);
            var threads = new FakeThreadCanceller();
            Thread? callThread = null;
            bool underWay = false;

            bool completed = BoundedDeviceCall.TryRun(
                token => {
                    callThread = Thread.CurrentThread;
                    running.Set();
                    release.Wait(CancellationToken.None);
                    token.ThrowIfCancellationRequested();
                },
                50,
                () => {
                    underWay = CallIsRunning(running, threads);
                    release.Set();
                    if (underWay) {
                        Assert.True(callThread!.Join(5000));
                    }
                },
                NoLateFailure,
                returned.Set,
                threads);

            Assert.False(completed);
            Assert.True(returned.Wait(5000, TestContext.Current.CancellationToken));
            return underWay;
        });
    }

    [Fact]
    public void TryRun_NullCall_Throws()
        => Assert.Throws<ArgumentNullException>(() => BoundedDeviceCall.TryRun(null!, 100, () => { }, NoLateFailure, Ignored));

    [Fact]
    public void TryRun_NullOnTimeout_Throws()
        => Assert.Throws<ArgumentNullException>(() => BoundedDeviceCall.TryRun(_ => { }, 100, null!, NoLateFailure, Ignored));

    [Fact]
    public void TryRun_NullOnLateFailure_Throws()
        => Assert.Throws<ArgumentNullException>(() => BoundedDeviceCall.TryRun(_ => { }, 100, () => { }, null!, Ignored));

    [Fact]
    public void TryRun_NullOnReturned_Throws()
        => Assert.Throws<ArgumentNullException>(() => BoundedDeviceCall.TryRun(_ => { }, 100, () => { }, NoLateFailure, null!));

    [Fact]
    public void TryRun_NullThreads_Throws()
        => Assert.Throws<ArgumentNullException>(
            () => BoundedDeviceCall.TryRun(_ => { }, 100, () => { }, NoLateFailure, Ignored, null!));

    [Fact]
    public void TryRun_Completes_ClosesTheThreadHandle_WithoutCancelling() {
        var threads = new FakeThreadCanceller();

        Assert.True(BoundedDeviceCall.TryRun(_ => { }, 1000, () => { }, NoLateFailure, Ignored, threads));

        IntPtr handle = Assert.Single(threads.OpenedHandles);
        Assert.Equal(new[] { handle }, threads.Closed);
        Assert.Empty(threads.Cancelled);
    }

    [Fact]
    public void TryRun_CallThrows_StillClosesTheThreadHandle() {
        var threads = new FakeThreadCanceller();

        Assert.Throws<InvalidOperationException>(
            () => BoundedDeviceCall.TryRun(_ => throw new InvalidOperationException(), 1000, () => { }, NoLateFailure, Ignored, threads));

        Assert.Equal(threads.OpenedHandles, threads.Closed);
    }

    [Fact]
    public void TryRun_TimesOut_CancelsByHandle_ThenByThread_ThenClosesTheHandle() {
        AbandonedCallScenario.Run(() => {
            var threads = new FakeThreadCanceller();
            using var release = new ManualResetEventSlim(false);
            using var running = new ManualResetEventSlim(false);
            using var returned = new ManualResetEventSlim(false);
            bool cancelledByHandleFirst = false;

            bool completed = BoundedDeviceCall.TryRun(
                _ => { running.Set(); release.Wait(CancellationToken.None); },
                50,
                () => { cancelledByHandleFirst = threads.Cancelled.Count == 0; release.Set(); },
                _ => { },
                returned.Set,
                threads);

            Assert.False(completed);
            Assert.True(returned.Wait(5000, TestContext.Current.CancellationToken));
            if (!running.IsSet) {
                return false;
            }

            Assert.True(cancelledByHandleFirst);
            IntPtr handle = Assert.Single(threads.OpenedHandles);
            Assert.Equal(new[] { handle }, threads.Cancelled);
            Assert.Equal(new[] { handle }, threads.Closed);
            return true;
        });
    }

    [Fact]
    public void TryRun_TimesOutBeforeTheThreadStarts_TheCallNeverRuns_AndTheThreadClosesItsHandle() {
        // The race: the caller gives up while the thread is still opening its own handle. The caller
        // finds the slot empty, so has nothing to cancel or close. The thread then finds the slot
        // claimed: it closes the handle itself, and it does not start a call nobody can cancel now.
        // It reports the call returned only once it has decided, so waiting for that proves the call
        // was never started rather than merely not started yet.
        using var gate = new ManualResetEventSlim(false);
        using var returned = new ManualResetEventSlim(false);
        var threads = new FakeThreadCanceller { OpenGate = gate };
        int calls = 0;

        bool completed = BoundedDeviceCall.TryRun(_ => calls++, 50, () => { }, NoLateFailure, returned.Set, threads);

        Assert.False(completed);
        Assert.Empty(threads.Cancelled);
        Assert.Empty(threads.Closed);
        Assert.False(returned.IsSet);

        gate.Set();
        Assert.True(returned.Wait(5000, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { Assert.Single(threads.OpenedHandles) }, threads.Closed);
        Assert.Empty(threads.Cancelled);
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact]
    public void TryRun_TimesOutBeforeAThreadWithNoHandleStarts_TheCallNeverRuns() {
        using var gate = new ManualResetEventSlim(false);
        using var returned = new ManualResetEventSlim(false);
        var threads = new FakeThreadCanceller { OpenGate = gate, HandsOutHandles = false };
        int calls = 0;

        Assert.False(BoundedDeviceCall.TryRun(_ => calls++, 50, () => { }, NoLateFailure, returned.Set, threads));
        gate.Set();

        Assert.True(returned.Wait(5000, TestContext.Current.CancellationToken));
        Assert.Equal(0, Volatile.Read(ref calls));
        Assert.Empty(threads.Closed);
    }

    [Fact]
    public void TryRun_NoThreadHandle_TimesOutWithoutCancellingOrClosing() {
        AbandonedCallScenario.Run(() => {
            var threads = new FakeThreadCanceller { HandsOutHandles = false };
            using var release = new ManualResetEventSlim(false);
            using var running = new ManualResetEventSlim(false);
            using var returned = new ManualResetEventSlim(false);
            bool cancelled = false;

            bool completed = BoundedDeviceCall.TryRun(
                _ => { running.Set(); release.Wait(CancellationToken.None); },
                50,
                () => { cancelled = true; release.Set(); },
                _ => { },
                returned.Set,
                threads);

            Assert.False(completed);
            Assert.True(cancelled);
            Assert.Empty(threads.Cancelled);
            Assert.Empty(threads.Closed);
            Assert.True(returned.Wait(5000, TestContext.Current.CancellationToken));
            return running.IsSet;
        });
    }

    [Fact]
    public void TryRun_CallThrowsAfterTheCallerGaveUp_ReportsTheFailureFromTheAbandonedThread_ThenThatItReturned() {
        // The caller has returned before the call throws, so nobody can rethrow it: it must reach the
        // late-failure sink instead of vanishing with the thread. Only then is it reported returned,
        // from the same thread, so whoever hears it returned has already heard how it ended.
        AbandonedCallScenario.Run(() => {
            using var release = new ManualResetEventSlim(false);
            using var running = new ManualResetEventSlim(false);
            using var returned = new ManualResetEventSlim(false);
            var thrown = new InvalidOperationException("late");
            var events = new List<string>();
            Exception? late = null;

            bool completed = BoundedDeviceCall.TryRun(
                _ => { running.Set(); release.Wait(CancellationToken.None); throw thrown; },
                50,
                () => { },
                failure => { late = failure; events.Add("reported"); },
                () => { events.Add("returned"); returned.Set(); },
                new FakeThreadCanceller());

            Assert.False(completed);
            Assert.Null(late);
            release.Set();
            Assert.True(returned.Wait(5000, TestContext.Current.CancellationToken));
            if (!running.IsSet) {
                return false;
            }

            Assert.Same(thrown, late);
            Assert.Equal(new[] { "reported", "returned" }, events);
            return true;
        });
    }

    [Fact]
    public void TryRun_CallThrowsWhileTheCallerIsCancellingIt_ReportsTheFailureOnTheCallersThread() {
        // The other side of the race: the cancel makes the call fail and the thread finishes before the
        // caller has marked it abandoned. The caller finds it finished and reports the failure itself,
        // and that the call returned, before TryRun returns.
        AbandonedCallScenario.Run(() => {
            using var release = new ManualResetEventSlim(false);
            using var running = new ManualResetEventSlim(false);
            using var returned = new ManualResetEventSlim(false);
            var threads = new FakeThreadCanceller();
            var thrown = new InvalidOperationException("cancelled");
            Thread? callThread = null;
            bool underWay = false;
            var reports = new List<(Exception Failure, int ThreadId)>();
            var returns = new List<int>();

            bool completed = BoundedDeviceCall.TryRun(
                _ => { callThread = Thread.CurrentThread; running.Set(); release.Wait(CancellationToken.None); throw thrown; },
                50,
                () => {
                    underWay = CallIsRunning(running, threads);
                    release.Set();
                    if (underWay) {
                        Assert.True(callThread!.Join(5000));
                    }
                },
                failure => reports.Add((failure, Environment.CurrentManagedThreadId)),
                () => { returns.Add(Environment.CurrentManagedThreadId); returned.Set(); },
                threads);

            Assert.False(completed);
            Assert.True(returned.Wait(5000, TestContext.Current.CancellationToken));
            if (!underWay) {
                return false;
            }

            (Exception failure, int threadId) = Assert.Single(reports);
            Assert.Same(thrown, failure);
            Assert.Equal(Environment.CurrentManagedThreadId, threadId);
            Assert.Equal(new[] { Environment.CurrentManagedThreadId }, returns);
            return true;
        });
    }

    [Fact]
    public void TryRun_CallSucceedsWhileTheCallerIsCancellingIt_ReportsNothing_ButThatItReturned() {
        AbandonedCallScenario.Run(() => {
            using var release = new ManualResetEventSlim(false);
            using var running = new ManualResetEventSlim(false);
            using var returned = new ManualResetEventSlim(false);
            var threads = new FakeThreadCanceller();
            Thread? callThread = null;
            bool underWay = false;
            int returns = 0;

            bool completed = BoundedDeviceCall.TryRun(
                _ => { callThread = Thread.CurrentThread; running.Set(); release.Wait(CancellationToken.None); },
                50,
                () => {
                    underWay = CallIsRunning(running, threads);
                    release.Set();
                    if (underWay) {
                        Assert.True(callThread!.Join(5000));
                    }
                },
                NoLateFailure,
                () => { returns++; returned.Set(); },
                threads);

            Assert.False(completed);
            if (!underWay) {
                Assert.True(returned.Wait(5000, TestContext.Current.CancellationToken));
                return false;
            }

            // The caller joined the call thread before it gave up, so the return was reported by then.
            Assert.Equal(1, returns);
            return true;
        });
    }

    [Fact]
    public void TryRun_CallSucceedsAfterTheCallerGaveUp_ReportsNothing_AndIsReportedReturnedOnlyOnceItHas() {
        // The call is held on a gate the test owns, so it cannot have returned before the release: a
        // return reported before then would be a call still out being mistaken for one done with.
        AbandonedCallScenario.Run(() => {
            using var release = new ManualResetEventSlim(false);
            using var running = new ManualResetEventSlim(false);
            using var returned = new ManualResetEventSlim(false);
            Thread? callThread = null;
            int reports = 0;
            int returnedOn = 0;

            bool completed = BoundedDeviceCall.TryRun(
                _ => { callThread = Thread.CurrentThread; running.Set(); release.Wait(CancellationToken.None); },
                50,
                () => { },
                _ => Interlocked.Increment(ref reports),
                () => { returnedOn = Environment.CurrentManagedThreadId; returned.Set(); },
                new FakeThreadCanceller());

            // Either the call is now held on the gate, or the thread found the deadline had passed
            // and reported it returned without it; only the first is the case here.
            Assert.False(completed);
            Assert.True(SpinWait.SpinUntil(() => running.IsSet || returned.IsSet, TimeSpan.FromSeconds(5)));
            if (!running.IsSet) {
                return false;
            }

            Assert.False(returned.IsSet);
            release.Set();
            Assert.True(returned.Wait(5000, TestContext.Current.CancellationToken));
            Assert.Equal(callThread!.ManagedThreadId, returnedOn);
            Assert.True(callThread.Join(5000));
            Assert.Equal(0, reports);
            return true;
        });
    }

    [Fact]
    public void TryRun_CompletesInTime_ReportsItReturned_BeforeReturning() {
        int returns = 0;

        Assert.True(BoundedDeviceCall.TryRun(_ => { }, 1000, () => { }, NoLateFailure, () => returns++, new FakeThreadCanceller()));

        Assert.Equal(1, returns);
    }

    [Fact]
    public void TryRun_CallThrowsInTime_ReportsItReturned_BeforeRethrowing() {
        int returns = 0;

        Assert.Throws<InvalidOperationException>(() => BoundedDeviceCall.TryRun(
            _ => throw new InvalidOperationException(), 1000, () => { }, NoLateFailure, () => returns++, new FakeThreadCanceller()));

        Assert.Equal(1, returns);
    }
}
