using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using FanControl.LianLi.Transport;

namespace FanControl.LianLi.Tests.Fakes;

/// <summary>
/// A scripted winusb.dll. Every call is appended to <see cref="Calls"/> as text, so a test asserts
/// the exact native sequence a transport makes. Each open hands out fresh <see cref="FakeSafeHandle"/>s
/// kept in <see cref="Devices"/> and <see cref="Interfaces"/>. Reads come from <see cref="Replies"/>;
/// once it is empty a read ends the reply the way the pipe timeout does (ERROR_SEM_TIMEOUT).
/// <see cref="OnCall"/> runs with each call's text as it is recorded, so a test can land a deadline
/// inside any one native call.
/// </summary>
internal sealed class FakeWinUsbApi : IWinUsbApi {
    public const int ErrorSemTimeout = 121;

    public List<string> Calls { get; } = new List<string>();

    public List<FakeSafeHandle> Devices { get; } = new List<FakeSafeHandle>();

    public List<FakeSafeHandle> Interfaces { get; } = new List<FakeSafeHandle>();

    /// <summary>Win32 errors the next opens fail with, in order; empty means the open succeeds.</summary>
    public Queue<int> OpenDeviceErrors { get; } = new Queue<int>();

    public int? InitializeError { get; set; }

    /// <summary>A pipe whose SetPipePolicy fails, with the error it fails with.</summary>
    public Dictionary<byte, int> PipePolicyErrors { get; } = new Dictionary<byte, int>();

    public bool FlushSucceeds { get; set; } = true;

    /// <summary>Scripted writes: (succeeded, bytes transferred or -1 for all, error). Empty means a full write.</summary>
    public Queue<(bool Succeeded, int Transferred, int Error)> WriteResults { get; } =
        new Queue<(bool Succeeded, int Transferred, int Error)>();

    public List<byte[]> Written { get; } = new List<byte[]>();

    /// <summary>Scripted IN transfers: a packet, or a failure with its error.</summary>
    public Queue<(byte[]? Packet, int Error)> Replies { get; } = new Queue<(byte[]? Packet, int Error)>();

    public Dictionary<byte, int> AbortErrors { get; } = new Dictionary<byte, int>();

    public int? CancelError { get; set; }

    public Action<string>? OnCall { get; set; }

    public SafeHandle? OpenDevice(string devicePath, out int error) {
        Record("OpenDevice " + devicePath);
        if (OpenDeviceErrors.Count > 0) {
            error = OpenDeviceErrors.Dequeue();
            return null;
        }

        var handle = new FakeSafeHandle("device" + Devices.Count);
        Devices.Add(handle);
        error = 0;
        return handle;
    }

    public SafeHandle? Initialize(SafeHandle device, out int error) {
        Record("Initialize " + device);
        if (InitializeError is int failure) {
            error = failure;
            return null;
        }

        var handle = new FakeSafeHandle("interface" + Interfaces.Count);
        Interfaces.Add(handle);
        error = 0;
        return handle;
    }

    public bool SetPipeTransferTimeout(SafeHandle usbInterface, byte pipe, uint milliseconds, out int error) {
        Record(string.Format(CultureInfo.InvariantCulture, "SetPipeTransferTimeout {0} 0x{1:X2} {2}", usbInterface, pipe, milliseconds));
        return Result(PipePolicyErrors.TryGetValue(pipe, out int failure) ? failure : 0, out error);
    }

    public bool FlushPipe(SafeHandle usbInterface, byte pipe, out int error) {
        Record(string.Format(CultureInfo.InvariantCulture, "FlushPipe {0} 0x{1:X2}", usbInterface, pipe));
        return Result(FlushSucceeds ? 0 : 31, out error);
    }

    public bool WritePipe(SafeHandle usbInterface, byte pipe, byte[] buffer, out int transferred, out int error) {
        Record(string.Format(CultureInfo.InvariantCulture, "WritePipe {0} 0x{1:X2} {2}", usbInterface, pipe, buffer.Length));
        Written.Add((byte[])buffer.Clone());
        (bool succeeded, int count, int failure) = WriteResults.Count > 0 ? WriteResults.Dequeue() : (true, -1, 0);
        transferred = count < 0 ? buffer.Length : count;
        error = succeeded ? 0 : failure;
        return succeeded;
    }

    public bool ReadPipe(SafeHandle usbInterface, byte pipe, byte[] buffer, out int transferred, out int error) {
        Record(string.Format(CultureInfo.InvariantCulture, "ReadPipe {0} 0x{1:X2} {2}", usbInterface, pipe, buffer.Length));
        (byte[]? packet, int failure) = Replies.Count > 0 ? Replies.Dequeue() : (null, ErrorSemTimeout);
        if (packet is null) {
            transferred = 0;
            error = failure;
            return false;
        }

        Array.Copy(packet, buffer, Math.Min(packet.Length, buffer.Length));
        transferred = packet.Length;
        error = 0;
        return true;
    }

    public bool AbortPipe(SafeHandle usbInterface, byte pipe, out int error) {
        Record(string.Format(CultureInfo.InvariantCulture, "AbortPipe {0} 0x{1:X2}", usbInterface, pipe));
        return Result(AbortErrors.TryGetValue(pipe, out int failure) ? failure : 0, out error);
    }

    public bool CancelPendingIo(SafeHandle device, out int error) {
        Record("CancelPendingIo " + device);
        return Result(CancelError ?? 0, out error);
    }

    private void Record(string call) {
        Calls.Add(call);
        OnCall?.Invoke(call);
    }

    private static bool Result(int failure, out int error) {
        error = failure;
        return failure == 0;
    }
}
