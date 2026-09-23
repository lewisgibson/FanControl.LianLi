using System;
using System.Collections.Generic;
using System.Threading;
using FanControl.LianLi.Transport;

namespace FanControl.LianLi.Tests.Fakes;

// Hands out numbered stand-in handles and records what BoundedDeviceCall does with them. OpenGate,
// when set, holds the throwaway thread inside OpenCurrentThread until a test releases it, which is
// how a test lands the caller's timeout before the thread has published its handle.
internal sealed class FakeThreadCanceller : IThreadCanceller {
    private readonly object _lock = new object();
    private readonly List<IntPtr> _opened = new List<IntPtr>();
    private readonly List<IntPtr> _cancelled = new List<IntPtr>();
    private readonly List<IntPtr> _closed = new List<IntPtr>();
    private int _next = 100;

    public ManualResetEventSlim? OpenGate { get; set; }

    public bool HandsOutHandles { get; set; } = true;

    public IReadOnlyList<IntPtr> OpenedHandles { get { lock (_lock) { return _opened.ToArray(); } } }

    public IReadOnlyList<IntPtr> Cancelled { get { lock (_lock) { return _cancelled.ToArray(); } } }

    public IReadOnlyList<IntPtr> Closed { get { lock (_lock) { return _closed.ToArray(); } } }

    public IntPtr OpenCurrentThread() {
        OpenGate?.Wait();
        if (!HandsOutHandles) {
            return IntPtr.Zero;
        }

        lock (_lock) {
            var handle = new IntPtr(_next++);
            _opened.Add(handle);
            return handle;
        }
    }

    public void CancelSynchronousIo(IntPtr thread) {
        lock (_lock) {
            _cancelled.Add(thread);
        }
    }

    public void Close(IntPtr thread) {
        lock (_lock) {
            _closed.Add(thread);
        }
    }
}
