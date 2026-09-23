using System;
using FanControl.LianLi.Tests.Fakes;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

public class OpenHandoffTests {
    [Fact]
    public void Take_AfterComplete_ReturnsTheResultUndisposed() {
        var handoff = new OpenHandoff<FakeDeviceTransport>();
        var opened = new FakeDeviceTransport();

        handoff.Complete(opened);
        FakeDeviceTransport? taken = handoff.Take();

        Assert.Same(opened, taken);
        Assert.False(opened.IsDisposed);
    }

    [Fact]
    public void Complete_AfterTake_DisposesTheLateResult() {
        var handoff = new OpenHandoff<FakeDeviceTransport>();
        var opened = new FakeDeviceTransport();

        // The caller's bounded wait ran out before the open finished...
        Assert.Null(handoff.Take());
        // ...so the result the thread produces afterwards is disposed on the spot, not leaked.
        handoff.Complete(opened);

        Assert.True(opened.IsDisposed);
        Assert.Null(handoff.Take());
    }

    [Fact]
    public void Take_Twice_ReturnsTheResultOnce() {
        var handoff = new OpenHandoff<FakeDeviceTransport>();
        handoff.Complete(new FakeDeviceTransport());

        Assert.NotNull(handoff.Take());
        Assert.Null(handoff.Take());
    }

    [Fact]
    public void Complete_NullResult_Throws()
        => Assert.Throws<ArgumentNullException>(() => new OpenHandoff<FakeDeviceTransport>().Complete(null!));
}
