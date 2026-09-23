using System;

namespace FanControl.LianLi.Transport;

/// <summary>
/// The three thread operations <see cref="BoundedDeviceCall"/> needs to cancel a native call that
/// has stopped answering. A seam so the handle handshake between the caller and the throwaway
/// thread - which decides who closes the handle when the two race - can be tested without Windows.
/// </summary>
internal interface IThreadCanceller {
    /// <summary>
    /// A handle on the calling thread that can cancel its synchronous I/O, or
    /// <see cref="IntPtr.Zero"/> when there is none to be had.
    /// </summary>
    IntPtr OpenCurrentThread();

    /// <summary>Cancel the synchronous I/O the thread is blocked in, if any.</summary>
    void CancelSynchronousIo(IntPtr thread);

    /// <summary>Release a handle <see cref="OpenCurrentThread"/> returned.</summary>
    void Close(IntPtr thread);
}
