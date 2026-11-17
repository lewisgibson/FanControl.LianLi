using FanControl.LianLi.Plugin;

namespace FanControl.LianLi.Tests.Fakes;

// A claim another copy can be made to hold, with no kernel object behind it. Copies given the same
// slot share one claim, as copies in one process share the named semaphore.
internal sealed class FakePluginCopyClaim : IPluginCopyClaim {
    private readonly Slot _slot;

    public FakePluginCopyClaim()
        : this(new Slot()) {
    }

    public FakePluginCopyClaim(Slot slot) {
        _slot = slot;
    }

    /// <summary>Whether another copy holds the claim, so taking it fails.</summary>
    public bool HeldElsewhere { get; set; }

    public bool Held => ReferenceEquals(_slot.Holder, this);

    public int Releases { get; private set; }

    public bool TryTake() {
        if (HeldElsewhere || (_slot.Holder != null && !Held)) {
            return false;
        }

        _slot.Holder = this;
        return true;
    }

    public void Release() {
        Releases++;
        if (Held) {
            _slot.Holder = null;
        }
    }

    /// <summary>The one claim the copies in a process share.</summary>
    internal sealed class Slot {
        public object? Holder { get; set; }
    }
}
