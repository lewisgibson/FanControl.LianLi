using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace FanControl.LianLi.Transport;

/// <summary>
/// One <c>ReadFile</c> or <c>WriteFile</c> on an overlapped HID stream handle, over
/// <see cref="IHidOverlappedApi"/>. The OVERLAPPED, the data buffer and the event the kernel signals are
/// unmanaged memory and a raw handle - none of them has a finalizer - because the kernel owns all three
/// until the transfer completes: a buffer the GC moved or freed, or an event a finalizer closed (and
/// Windows then reused), would be written or signalled after the fact. So they are released exactly
/// once, and only once the completion has been seen: by <see cref="Dispose"/> after <see cref="Wait"/>
/// saw it, or - for a transfer the caller gave up on - by a thread-pool wait that sees the kernel
/// complete it.
/// </summary>
internal sealed class HidOverlappedTransfer : IHidTransfer {
    // ERROR_IO_PENDING: the transfer was queued and completes later, which is the normal case.
    private const int ErrorIoPending = 997;

    // WaitForSingleObject's WAIT_OBJECT_0: the event was signalled, so the transfer completed.
    private const uint WaitObject0 = 0;

    private readonly IHidOverlappedApi _api;
    private readonly SafeHandle _stream;
    private readonly byte[]? _readInto;
    private readonly int _length;
    private IntPtr _buffer;
    private IntPtr _overlapped;
    private IntPtr _event;
    private int _startError;
    private bool _completed;
    private int _givenUp;
    private int _freed;
    private IDisposable? _registration;

    // ReleaseWhenComplete and the completion wait each count in here once. The wait can fire before
    // RegisterEventWait has even returned, so whichever of the two counts second is the one that knows
    // the registration exists and that the wait is over, and unregisters it.
    private int _registrationHandoff;

    // Called once the kernel has completed a transfer given up on and its memory is freed; written
    // before the completion wait is registered, so the wait's thread sees it.
    private Action _released = () => { };

    private HidOverlappedTransfer(IHidOverlappedApi api, SafeHandle stream, byte[]? readInto, int length) {
        _api = api;
        _stream = stream;
        _readInto = readInto;
        _length = length;
    }

    /// <summary>
    /// Allocate what the transfer needs and start it: a write of all of <paramref name="data"/>, or a
    /// read of up to its length into it. A start the driver refused, or whose event could not be
    /// created, comes back as a transfer that has already completed with that error. An exception from
    /// the start (no memory, a closed handle) is thrown before the kernel owns anything, so everything
    /// allocated so far is released first.
    /// </summary>
    public static HidOverlappedTransfer Begin(IHidOverlappedApi api, SafeHandle stream, byte[] data, bool isRead) {
        if (api is null) {
            throw new ArgumentNullException(nameof(api));
        }

        if (stream is null) {
            throw new ArgumentNullException(nameof(stream));
        }

        if (data is null) {
            throw new ArgumentNullException(nameof(data));
        }

        var transfer = new HidOverlappedTransfer(api, stream, isRead ? data : null, data.Length);
        try {
            transfer.Start(data, isRead);
            return transfer;
        } catch {
            transfer.Dispose();
            throw;
        }
    }

    public bool Wait(int milliseconds) {
        if (!_completed && _api.WaitForEvent(_event, milliseconds) == WaitObject0) {
            _completed = true;
        }

        return _completed;
    }

    // A transfer that never got an OVERLAPPED was never queued, so there is nothing to cancel; and
    // CancelIoEx with a null OVERLAPPED would cancel every transfer pending on the handle instead.
    public bool Cancel(out int error) {
        if (_overlapped == IntPtr.Zero) {
            error = 0;
            return true;
        }

        return _api.CancelTransfer(_stream, _overlapped, out error);
    }

    public bool GetResult(out int transferred, out int error) {
        transferred = 0;
        if (_startError != 0) {
            error = _startError;
            return false;
        }

        if (!_api.GetOverlappedResult(_stream, _overlapped, out int count, out error)) {
            return false;
        }

        transferred = count;
        if (_readInto != null) {
            _api.CopyFromBuffer(_buffer, _readInto, Math.Min(transferred, _length));
        }

        return true;
    }

    // A thread-pool wait on the transfer's event, which the kernel sets when it completes it; nothing
    // is blocked meanwhile, and the memory is freed only once the kernel is done with it. From here the
    // caller's Dispose leaves everything to that wait.
    public void ReleaseWhenComplete(Action released) {
        if (released is null) {
            throw new ArgumentNullException(nameof(released));
        }

        if (_completed) {
            released();
            return;
        }

        _released = released;
        Volatile.Write(ref _givenUp, 1);
        IDisposable registration = _api.RegisterEventWait(_event, Release);
        Volatile.Write(ref _registration, registration);
        if (Interlocked.Increment(ref _registrationHandoff) == 2) {
            registration.Dispose();
        }
    }

    public void Dispose() {
        if (!_completed || Volatile.Read(ref _givenUp) == 1) {
            return;
        }

        Free();
    }

    // Once the kernel has completed a transfer given up on, on the thread-pool thread that saw it.
    private void Release() {
        if (Interlocked.Increment(ref _registrationHandoff) == 2) {
            // Counting second means ReleaseWhenComplete stored the registration before it counted.
            Volatile.Read(ref _registration)!.Dispose();
        }

        Free();
        _released();
    }

    // Exactly once, whichever of Dispose and the completion wait gets here.
    private void Free() {
        if (Interlocked.Exchange(ref _freed, 1) == 1) {
            return;
        }

        if (_buffer != IntPtr.Zero) {
            _api.FreeMemory(_buffer);
            _buffer = IntPtr.Zero;
        }

        if (_overlapped != IntPtr.Zero) {
            _api.FreeMemory(_overlapped);
            _overlapped = IntPtr.Zero;
        }

        if (_event != IntPtr.Zero) {
            _api.CloseEvent(_event);
            _event = IntPtr.Zero;
        }
    }

    private void Start(byte[] data, bool isRead) {
        // Counted as complete until the native call below has queued the transfer: until then nothing
        // belongs to the kernel, and a start that fails releases what it allocated.
        _completed = true;
        _event = _api.CreateEvent(out int eventError);
        if (_event == IntPtr.Zero) {
            _startError = eventError;
            return;
        }

        _buffer = _api.AllocateBuffer(_length);
        if (!isRead) {
            _api.CopyToBuffer(data, _buffer);
        }

        _overlapped = _api.AllocateOverlapped(_event);
        int error;
        bool finished = isRead
            ? _api.StartRead(_stream, _buffer, _length, _overlapped, out error)
            : _api.StartWrite(_stream, _buffer, _length, _overlapped, out error);
        if (finished) {
            return;
        }

        if (error == ErrorIoPending) {
            _completed = false;
            return;
        }

        _startError = error;
    }
}
