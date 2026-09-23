using System;
using System.Collections.Generic;
using FanControl.LianLi.Transport;

namespace FanControl.LianLi.Tests.Fakes;

/// <summary>
/// A scripted overlapped transfer. By default it completes at once, moving every byte (a read copies
/// <see cref="Reply"/> into its buffer). <see cref="Pending"/> keeps it from completing on its own;
/// then a cancel completes it with <c>ERROR_OPERATION_ABORTED</c>, unless <see cref="CancelCompletes"/>
/// is false (a driver that never finishes the cancel) or <see cref="FinishesAsCancelled"/> is true (it
/// completed successfully just as the cancel went in). Records every wait, cancel and disposal, and
/// whether it was disposed after its completion was seen.
/// </summary>
internal sealed class FakeHidTransfer : IHidTransfer {
    public const int ErrorOperationAborted = 995;

    private byte[]? _readInto;
    private int _length;
    private bool _cancelled;
    private bool _completionSeen;

    public bool Pending { get; set; }

    public bool CancelCompletes { get; set; } = true;

    public bool FinishesAsCancelled { get; set; }

    public int? CancelError { get; set; }

    /// <summary>The Win32 error the transfer ends with; 0 succeeds.</summary>
    public int Error { get; set; }

    /// <summary>The bytes it moves; null for the whole buffer, or all of <see cref="Reply"/> for a read.</summary>
    public int? Transferred { get; set; }

    public byte[]? Reply { get; set; }

    public List<int> Waits { get; } = new List<int>();

    public int Cancels { get; private set; }

    public int Disposals { get; private set; }

    public bool DisposedAfterCompletion { get; private set; }

    /// <summary>Whether the caller gave the transfer up to be released on completion.</summary>
    public bool ReleasedWhenComplete { get; private set; }

    /// <summary>What the caller asked to be called once the kernel completes the transfer, given up on.</summary>
    public Action? Released { get; private set; }

    public void ReleaseWhenComplete(Action released) {
        ReleasedWhenComplete = true;
        Released = released;
    }

    public void Bind(int length, byte[]? readInto) {
        _length = length;
        _readInto = readInto;
    }

    public bool Wait(int milliseconds) {
        Waits.Add(milliseconds);
        _completionSeen = !Pending || (_cancelled && CancelCompletes);
        return _completionSeen;
    }

    public bool Cancel(out int error) {
        Cancels++;
        _cancelled = true;
        error = CancelError ?? 0;
        return CancelError is null;
    }

    public bool GetResult(out int transferred, out int error) {
        if (!_completionSeen) {
            throw new InvalidOperationException("GetResult before the transfer was seen to complete");
        }

        transferred = 0;
        error = _cancelled && Pending && !FinishesAsCancelled ? ErrorOperationAborted : Error;
        if (error != 0) {
            return false;
        }

        if (_readInto != null) {
            byte[] reply = Reply ?? Array.Empty<byte>();
            Array.Copy(reply, _readInto, Math.Min(reply.Length, _readInto.Length));
            transferred = Transferred ?? reply.Length;
        } else {
            transferred = Transferred ?? _length;
        }

        return true;
    }

    public void Dispose() {
        Disposals++;
        DisposedAfterCompletion = _completionSeen;
    }
}
