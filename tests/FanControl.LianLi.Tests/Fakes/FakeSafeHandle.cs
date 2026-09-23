using System;
using System.Runtime.InteropServices;

namespace FanControl.LianLi.Tests.Fakes;

/// <summary>
/// A stand-in native handle that counts its releases, which SafeHandle guarantees happen once, and
/// throws <see cref="ReleaseFailure"/> from its release when a test sets one.
/// </summary>
internal sealed class FakeSafeHandle : SafeHandle {
    public FakeSafeHandle(string name)
        : base(new IntPtr(1), ownsHandle: true) {
        Name = name;
    }

    public string Name { get; }

    public int Releases { get; private set; }

    public Exception? ReleaseFailure { get; set; }

    public override bool IsInvalid => false;

    public override string ToString() => Name;

    protected override bool ReleaseHandle() {
        Releases++;
        if (ReleaseFailure != null) {
            throw ReleaseFailure;
        }

        return true;
    }
}
