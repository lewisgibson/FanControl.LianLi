using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FanControl.LianLi.Transport;

namespace FanControl.LianLi.Tests.Fakes;

/// <summary>
/// A scripted kernel32 for one overlapped transfer at a time. Memory and events are numbered stand-in
/// pointers; a buffer is backed by a managed array so what a write sends and what a read brings back
/// can be asserted. Every call is appended to <see cref="Calls"/>, and every pointer still held is in
/// <see cref="Live"/> until it is freed or closed, when it moves to <see cref="Released"/> - so a test
/// sees that nothing is released before the kernel is done with it, and that each thing is released
/// exactly once. A start completes at once unless <see cref="StartError"/> is set
/// (<see cref="ErrorIoPending"/> queues it); a queued transfer completes when the test calls
/// <see cref="Complete"/>, or when it is cancelled and <see cref="CancelCompletes"/> is true. Thread-safe,
/// because the completion wait's callback runs on whichever thread calls <see cref="Complete"/>.
/// </summary>
internal sealed class FakeHidOverlappedApi : IHidOverlappedApi {
    public const int ErrorIoPending = 997;

    public const int ErrorOperationAborted = 995;

    // GetOverlappedResult's error on a transfer that has not completed yet.
    public const int ErrorIoIncomplete = 996;

    public const uint WaitObject0 = 0;

    public const uint WaitTimeout = 258;

    private readonly object _lock = new object();
    private readonly Dictionary<IntPtr, byte[]> _buffers = new Dictionary<IntPtr, byte[]>();
    private readonly List<IntPtr> _live = new List<IntPtr>();
    private readonly List<IntPtr> _released = new List<IntPtr>();
    private readonly List<string> _calls = new List<string>();
    private int _next = 0x1000;
    private IntPtr _transferBuffer;
    private int _transferLength;
    private bool _signalled;
    private int _transferred;
    private int _error;
    private Action? _onSignalled;

    /// <summary>The Win32 error CreateEvent fails with; null creates the event.</summary>
    public int? EventError { get; set; }

    public Exception? BufferFailure { get; set; }

    public Exception? OverlappedFailure { get; set; }

    /// <summary>Thrown by the start itself, as a closed stream handle would.</summary>
    public Exception? StartFailure { get; set; }

    /// <summary>The error ReadFile or WriteFile returns false with; null completes it at once.</summary>
    public int? StartError { get; set; }

    /// <summary>The bytes a read that completes brings back; the transfer count defaults to its length.</summary>
    public byte[] Reply { get; set; } = Array.Empty<byte>();

    public bool CancelCompletes { get; set; } = true;

    public int? CancelError { get; set; }

    /// <summary>Signal the event while the completion wait is being registered, as an already-set event does.</summary>
    public bool CompletesOnRegister { get; set; }

    /// <summary>What the buffer held when the write was started.</summary>
    public byte[]? Written { get; private set; }

    public List<FakeHidWaitRegistration> Registrations { get; } = new List<FakeHidWaitRegistration>();

    public IReadOnlyList<string> Calls { get { lock (_lock) { return _calls.ToArray(); } } }

    public IReadOnlyList<IntPtr> Live { get { lock (_lock) { return _live.ToArray(); } } }

    public IReadOnlyList<IntPtr> Released { get { lock (_lock) { return _released.ToArray(); } } }

    /// <summary>The kernel completes the queued transfer; a registered completion wait runs on this thread.</summary>
    public void Complete(int error = 0, int? transferred = null) {
        Action? callback;
        lock (_lock) {
            Finish(error, transferred);
            callback = _onSignalled;
            _onSignalled = null;
        }

        callback?.Invoke();
    }

    public IntPtr CreateEvent(out int error) {
        lock (_lock) {
            _calls.Add("CreateEventW");
            error = EventError ?? 0;
            return EventError is null ? Hold() : IntPtr.Zero;
        }
    }

    public void CloseEvent(IntPtr completionEvent) => Release("CloseHandle", completionEvent);

    public IntPtr AllocateBuffer(int length) {
        lock (_lock) {
            _calls.Add("AllocHGlobal buffer " + length);
            if (BufferFailure != null) {
                throw BufferFailure;
            }

            IntPtr buffer = Hold();
            _buffers[buffer] = new byte[length];
            return buffer;
        }
    }

    public IntPtr AllocateOverlapped(IntPtr completionEvent) {
        lock (_lock) {
            _calls.Add("AllocHGlobal overlapped " + completionEvent);
            if (OverlappedFailure != null) {
                throw OverlappedFailure;
            }

            return Hold();
        }
    }

    public void FreeMemory(IntPtr memory) => Release("FreeHGlobal", memory);

    public void CopyToBuffer(byte[] source, IntPtr buffer) {
        lock (_lock) {
            Array.Copy(source, _buffers[buffer], source.Length);
        }
    }

    public void CopyFromBuffer(IntPtr buffer, byte[] destination, int count) {
        lock (_lock) {
            _calls.Add("Copy " + count);
            Array.Copy(_buffers[buffer], destination, count);
        }
    }

    public bool StartRead(SafeHandle stream, IntPtr buffer, int length, IntPtr overlapped, out int error)
        => Start("ReadFile", buffer, length, out error);

    public bool StartWrite(SafeHandle stream, IntPtr buffer, int length, IntPtr overlapped, out int error) {
        lock (_lock) {
            Written = (byte[])_buffers[buffer].Clone();
        }

        return Start("WriteFile", buffer, length, out error);
    }

    public uint WaitForEvent(IntPtr completionEvent, int milliseconds) {
        lock (_lock) {
            _calls.Add("WaitForSingleObject " + milliseconds);
            return _signalled ? WaitObject0 : WaitTimeout;
        }
    }

    public bool CancelTransfer(SafeHandle stream, IntPtr overlapped, out int error) {
        lock (_lock) {
            _calls.Add("CancelIoEx " + overlapped);
            if (CancelError != null) {
                error = CancelError.Value;
                return false;
            }

            if (CancelCompletes && !_signalled) {
                Finish(ErrorOperationAborted, 0);
            }

            error = 0;
            return true;
        }
    }

    public bool GetOverlappedResult(SafeHandle stream, IntPtr overlapped, out int transferred, out int error) {
        lock (_lock) {
            _calls.Add("GetOverlappedResult");
            transferred = _signalled ? _transferred : 0;
            error = _signalled ? _error : ErrorIoIncomplete;
            return error == 0;
        }
    }

    public IDisposable RegisterEventWait(IntPtr completionEvent, Action onSignalled) {
        var registration = new FakeHidWaitRegistration();
        bool runNow;
        lock (_lock) {
            _calls.Add("RegisterWaitForSingleObject " + completionEvent);
            Registrations.Add(registration);
            if (CompletesOnRegister) {
                Finish(0, null);
            }

            runNow = _signalled;
            if (!runNow) {
                _onSignalled = onSignalled;
            }
        }

        if (runNow) {
            onSignalled();
        }

        return registration;
    }

    private bool Start(string call, IntPtr buffer, int length, out int error) {
        lock (_lock) {
            _calls.Add(call + " " + length);
            if (StartFailure != null) {
                throw StartFailure;
            }

            _transferBuffer = buffer;
            _transferLength = length;
            if (StartError is null) {
                Finish(0, null);
                error = 0;
                return true;
            }

            error = StartError.Value;
            return false;
        }
    }

    // Under the lock. A read's reply lands in the buffer the kernel was given, as far as it fits.
    private void Finish(int error, int? transferred) {
        _signalled = true;
        _error = error;
        _transferred = transferred ?? (Reply.Length > 0 ? Reply.Length : _transferLength);
        if (error == 0) {
            Array.Copy(Reply, _buffers[_transferBuffer], Math.Min(Reply.Length, _transferLength));
        }
    }

    private IntPtr Hold() {
        var pointer = new IntPtr(_next++);
        _live.Add(pointer);
        return pointer;
    }

    private void Release(string call, IntPtr pointer) {
        lock (_lock) {
            _calls.Add(call + " " + pointer);
            if (!_live.Remove(pointer)) {
                throw new InvalidOperationException(call + " on " + pointer + ", which is not held");
            }

            _released.Add(pointer);
        }
    }
}
