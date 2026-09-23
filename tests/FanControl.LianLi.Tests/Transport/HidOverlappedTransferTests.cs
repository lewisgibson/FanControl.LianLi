using System;
using System.Linq;
using System.Threading;
using FanControl.LianLi.Tests.Fakes;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

/// <summary>
/// How one overlapped transfer reads the start result, the wait and the completion, and when the
/// event, buffer and OVERLAPPED the kernel writes into are released: exactly once, and never while the
/// kernel may still complete into them.
/// </summary>
public class HidOverlappedTransferTests {
    // ERROR_GEN_FAILURE: a start the driver refused outright.
    private const int ErrorGenFailure = 31;

    // ERROR_NOT_ENOUGH_MEMORY: CreateEventW failing.
    private const int ErrorNotEnoughMemory = 8;

    // ERROR_INVALID_HANDLE: CancelIoEx refused.
    private const int ErrorInvalidHandle = 6;

    private static readonly FakeSafeHandle Stream = new FakeSafeHandle("stream");

    private static HidOverlappedTransfer BeginPendingRead(FakeHidOverlappedApi api, int length = 8) {
        api.StartError = FakeHidOverlappedApi.ErrorIoPending;
        return HidOverlappedTransfer.Begin(api, Stream, new byte[length], isRead: true);
    }

    // The overlapped transfer given up on: the wait ran out, and so did the wait after the cancel.
    private static HidOverlappedTransfer GiveUpOnPendingRead(FakeHidOverlappedApi api, Action? released = null) {
        api.CancelCompletes = false;
        HidOverlappedTransfer transfer = BeginPendingRead(api);
        Assert.False(transfer.Wait(500));
        Assert.True(transfer.Cancel(out _));
        Assert.False(transfer.Wait(100));
        transfer.ReleaseWhenComplete(released ?? (() => { }));
        return transfer;
    }

    [Fact]
    public void Begin_NullApi_Throws()
        => Assert.Throws<ArgumentNullException>(() => HidOverlappedTransfer.Begin(null!, Stream, new byte[1], isRead: true));

    [Fact]
    public void Begin_NullStream_Throws()
        => Assert.Throws<ArgumentNullException>(() => HidOverlappedTransfer.Begin(new FakeHidOverlappedApi(), null!, new byte[1], isRead: true));

    [Fact]
    public void Begin_NullData_Throws()
        => Assert.Throws<ArgumentNullException>(() => HidOverlappedTransfer.Begin(new FakeHidOverlappedApi(), Stream, null!, isRead: true));

    [Fact]
    public void Write_CompletedAtOnce_IsCompleteWithoutWaiting_AndDisposeReleasesEverythingOnce() {
        var api = new FakeHidOverlappedApi();
        byte[] report = { 0xE0, 0x10, 0x20, 0x30 };

        using (HidOverlappedTransfer transfer = HidOverlappedTransfer.Begin(api, Stream, report, isRead: false)) {
            Assert.Equal(report, api.Written);
            Assert.True(transfer.Wait(500));
            Assert.True(transfer.GetResult(out int transferred, out int error));
            Assert.Equal(4, transferred);
            Assert.Equal(0, error);
            Assert.Equal(3, api.Live.Count);
        }

        Assert.Equal(
            new[] { "CreateEventW", "AllocHGlobal buffer 4", "AllocHGlobal overlapped 4096", "WriteFile 4", "GetOverlappedResult", "FreeHGlobal 4097", "FreeHGlobal 4098", "CloseHandle 4096" },
            api.Calls);
        Assert.Empty(api.Live);
        Assert.Equal(3, api.Released.Count);
    }

    [Fact]
    public void Dispose_Twice_ReleasesEverythingOnce() {
        var api = new FakeHidOverlappedApi();
        HidOverlappedTransfer transfer = HidOverlappedTransfer.Begin(api, Stream, new byte[4], isRead: false);

        transfer.Dispose();
        transfer.Dispose();

        Assert.Equal(3, api.Released.Count);
        Assert.Empty(api.Live);
    }

    [Fact]
    public void Read_CompletedAtOnce_CopiesTheBytesTheKernelTransferred() {
        var api = new FakeHidOverlappedApi { Reply = new byte[] { 1, 2, 3 } };
        byte[] buffer = new byte[8];

        using HidOverlappedTransfer transfer = HidOverlappedTransfer.Begin(api, Stream, buffer, isRead: true);

        Assert.Null(api.Written);
        Assert.True(transfer.Wait(500));
        Assert.True(transfer.GetResult(out int transferred, out int error));
        Assert.Equal(3, transferred);
        Assert.Equal(0, error);
        Assert.Equal(new byte[] { 1, 2, 3, 0, 0, 0, 0, 0 }, buffer);
        Assert.Contains("Copy 3", api.Calls);
    }

    [Fact]
    public void Read_ReportingMoreThanTheBuffer_CopiesOnlyTheBuffer() {
        var api = new FakeHidOverlappedApi();
        byte[] buffer = new byte[4];
        using HidOverlappedTransfer transfer = BeginPendingRead(api, length: 4);

        api.Complete(transferred: 64);

        Assert.True(transfer.Wait(500));
        Assert.True(transfer.GetResult(out int transferred, out _));
        Assert.Equal(64, transferred);
        Assert.Contains("Copy 4", api.Calls);
    }

    [Fact]
    public void Pending_ThenCompleted_WaitsUntilTheEventIsSet_AndReleasesOnlyAfterward() {
        var api = new FakeHidOverlappedApi { Reply = new byte[] { 9, 8 } };
        HidOverlappedTransfer transfer = BeginPendingRead(api);

        Assert.False(transfer.Wait(500));
        Assert.False(transfer.Wait(500));
        api.Complete();
        Assert.True(transfer.Wait(500));
        // Seen once, the completion is remembered rather than waited for again.
        Assert.True(transfer.Wait(500));
        Assert.Equal(3, api.Calls.Count(call => call == "WaitForSingleObject 500"));

        Assert.True(transfer.GetResult(out int transferred, out _));
        Assert.Equal(2, transferred);
        Assert.Empty(api.Released);

        transfer.Dispose();

        Assert.Equal(3, api.Released.Count);
        Assert.Empty(api.Live);
    }

    [Fact]
    public void ImmediateFailure_IsCompleteAtOnce_WithTheStartError_AndReleasesEverything() {
        var api = new FakeHidOverlappedApi { StartError = ErrorGenFailure };

        HidOverlappedTransfer transfer = HidOverlappedTransfer.Begin(api, Stream, new byte[4], isRead: false);

        Assert.True(transfer.Wait(500));
        Assert.False(transfer.GetResult(out int transferred, out int error));
        Assert.Equal(0, transferred);
        Assert.Equal(ErrorGenFailure, error);
        Assert.DoesNotContain("WaitForSingleObject 500", api.Calls);
        Assert.DoesNotContain("GetOverlappedResult", api.Calls);

        transfer.Dispose();

        Assert.Equal(3, api.Released.Count);
        Assert.Empty(api.Live);
    }

    [Fact]
    public void EventThatCannotBeCreated_IsCompleteAtOnce_WithItsError_AndHoldsNothing() {
        var api = new FakeHidOverlappedApi { EventError = ErrorNotEnoughMemory };

        HidOverlappedTransfer transfer = HidOverlappedTransfer.Begin(api, Stream, new byte[4], isRead: true);

        Assert.True(transfer.Wait(500));
        Assert.False(transfer.GetResult(out _, out int error));
        Assert.Equal(ErrorNotEnoughMemory, error);

        // Never queued, so a cancel reaches nothing: CancelIoEx with no OVERLAPPED would cancel
        // every transfer pending on the handle.
        Assert.True(transfer.Cancel(out int cancelError));
        Assert.Equal(0, cancelError);
        transfer.Dispose();

        Assert.Equal(new[] { "CreateEventW" }, api.Calls);
        Assert.Empty(api.Released);
    }

    [Fact]
    public void BufferAllocationFailure_Throws_AfterClosingTheEvent() {
        var failure = new InsufficientMemoryException();
        var api = new FakeHidOverlappedApi { BufferFailure = failure };

        Assert.Same(failure, Assert.Throws<InsufficientMemoryException>(() => HidOverlappedTransfer.Begin(api, Stream, new byte[4], isRead: true)));

        Assert.Equal(new[] { new IntPtr(0x1000) }, api.Released);
        Assert.Empty(api.Live);
    }

    [Fact]
    public void OverlappedAllocationFailure_Throws_AfterReleasingTheEventAndBuffer() {
        var api = new FakeHidOverlappedApi { OverlappedFailure = new InsufficientMemoryException() };

        Assert.Throws<InsufficientMemoryException>(() => HidOverlappedTransfer.Begin(api, Stream, new byte[4], isRead: false));

        Assert.Equal(2, api.Released.Count);
        Assert.Empty(api.Live);
    }

    [Fact]
    public void StartThatThrows_Throws_AfterReleasingEverything() {
        var api = new FakeHidOverlappedApi { StartFailure = new ObjectDisposedException("stream") };

        Assert.Throws<ObjectDisposedException>(() => HidOverlappedTransfer.Begin(api, Stream, new byte[4], isRead: true));

        Assert.Equal(3, api.Released.Count);
        Assert.Empty(api.Live);
    }

    [Fact]
    public void Cancel_CompletingWithinTheWait_EndsAborted_AndDisposeReleasesEverything() {
        var api = new FakeHidOverlappedApi();
        HidOverlappedTransfer transfer = BeginPendingRead(api);

        Assert.False(transfer.Wait(500));
        Assert.True(transfer.Cancel(out int cancelError));
        Assert.Equal(0, cancelError);
        Assert.Contains("CancelIoEx 4098", api.Calls);
        Assert.True(transfer.Wait(100));
        Assert.False(transfer.GetResult(out int transferred, out int error));
        Assert.Equal(0, transferred);
        Assert.Equal(FakeHidOverlappedApi.ErrorOperationAborted, error);
        Assert.DoesNotContain(api.Calls, call => call.StartsWith("Copy", StringComparison.Ordinal));

        transfer.Dispose();

        Assert.Equal(3, api.Released.Count);
        Assert.Empty(api.Registrations);
    }

    [Fact]
    public void Cancel_Refused_ReportsTheError() {
        var api = new FakeHidOverlappedApi { CancelError = ErrorInvalidHandle };
        HidOverlappedTransfer transfer = BeginPendingRead(api);

        Assert.False(transfer.Cancel(out int error));

        Assert.Equal(ErrorInvalidHandle, error);
    }

    [Fact]
    public void Dispose_OfATransferNeverSeenToComplete_ReleasesNothing() {
        var api = new FakeHidOverlappedApi();
        HidOverlappedTransfer transfer = BeginPendingRead(api);
        Assert.False(transfer.Wait(500));

        transfer.Dispose();

        // The kernel may still complete into all three, so they are kept rather than freed under it.
        Assert.Equal(3, api.Live.Count);
        Assert.Empty(api.Released);
    }

    [Fact]
    public void ReleaseWhenComplete_KeepsEverything_UntilTheKernelCompletes_ThenReleasesItOnce() {
        var api = new FakeHidOverlappedApi();
        int released = 0;
        HidOverlappedTransfer transfer = GiveUpOnPendingRead(api, () => released++);

        transfer.Dispose();

        Assert.Equal(3, api.Live.Count);
        FakeHidWaitRegistration registration = Assert.Single(api.Registrations);
        Assert.Equal(0, registration.Disposals);
        Assert.Equal(0, released);

        api.Complete(FakeHidOverlappedApi.ErrorOperationAborted);

        Assert.Equal(1, released); // after its memory is freed
        Assert.Empty(api.Live);
        Assert.Equal(3, api.Released.Count);
        Assert.Equal(1, registration.Disposals);

        transfer.Dispose();

        Assert.Equal(3, api.Released.Count);
        Assert.Equal(1, registration.Disposals);
    }

    [Fact]
    public void ReleaseWhenComplete_AfterTheCompletionWasSeen_LeavesTheReleaseToDispose() {
        var api = new FakeHidOverlappedApi();
        HidOverlappedTransfer transfer = BeginPendingRead(api);
        api.Complete();
        Assert.True(transfer.Wait(500));
        bool released = false;

        transfer.ReleaseWhenComplete(() => released = true);

        Assert.True(released); // nothing is pending, so nothing is held
        Assert.Throws<ArgumentNullException>(() => transfer.ReleaseWhenComplete(null!));
        Assert.Empty(api.Registrations);
        Assert.Empty(api.Released);

        transfer.Dispose();

        Assert.Equal(3, api.Released.Count);
    }

    [Fact]
    public void ReleaseWhenComplete_OnAnEventAlreadySet_ReleasesOnce_AndUnregistersTheWaitOnce() {
        var api = new FakeHidOverlappedApi();
        api.CancelCompletes = false;
        HidOverlappedTransfer transfer = BeginPendingRead(api);
        Assert.False(transfer.Wait(500));
        // The kernel completes it between the last wait and the registration, so the wait fires
        // before RegisterEventWait has returned the registration.
        api.CompletesOnRegister = true;

        transfer.ReleaseWhenComplete(() => { });
        transfer.Dispose();

        Assert.Equal(3, api.Released.Count);
        Assert.Empty(api.Live);
        Assert.Equal(1, Assert.Single(api.Registrations).Disposals);
    }

    [Fact]
    public void DisposeRacingTheCompletionWait_ReleasesEverythingExactlyOnce_AndNeverBeforeCompletion() {
        for (int attempt = 0; attempt < 200; attempt++) {
            var api = new FakeHidOverlappedApi();
            HidOverlappedTransfer transfer = GiveUpOnPendingRead(api);
            using var go = new ManualResetEventSlim();
            var kernel = new Thread(() => {
                go.Wait();
                api.Complete(FakeHidOverlappedApi.ErrorOperationAborted);
            });
            kernel.Start();

            go.Set();
            transfer.Dispose();
            kernel.Join();
            transfer.Dispose();

            // Released on the completing thread alone, so a release implies the completion came first.
            Assert.Equal(3, api.Released.Count);
            Assert.Empty(api.Live);
            Assert.Equal(1, Assert.Single(api.Registrations).Disposals);
        }
    }

    [Fact]
    public void ReleaseWhenCompleteRacingTheCompletion_ReleasesOnce_AndUnregistersTheWaitOnce() {
        for (int attempt = 0; attempt < 200; attempt++) {
            var api = new FakeHidOverlappedApi { CancelCompletes = false };
            HidOverlappedTransfer transfer = BeginPendingRead(api);
            Assert.False(transfer.Wait(500));
            using var go = new ManualResetEventSlim();
            var kernel = new Thread(() => {
                go.Wait();
                api.Complete();
            });
            kernel.Start();

            go.Set();
            transfer.ReleaseWhenComplete(() => { });
            transfer.Dispose();
            kernel.Join();

            Assert.Equal(3, api.Released.Count);
            Assert.Empty(api.Live);
            Assert.Equal(1, Assert.Single(api.Registrations).Disposals);
        }
    }
}
