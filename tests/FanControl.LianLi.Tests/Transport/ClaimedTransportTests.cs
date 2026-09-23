using System;
using FanControl.LianLi.Tests.Fakes;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

/// <summary>A transport over a claimed path: every call passes through, and closing it lets go of the claim, once.</summary>
public sealed class ClaimedTransportTests {
    [Fact]
    public void EveryCall_GoesToTheTransport_AndClosingItReleasesTheClaimOnce() {
        var inner = new FakeDeviceTransport();
        inner.ReadReplies.Enqueue(new byte[] { 7 });
        int released = 0;
        var claimed = new ClaimedTransport(inner, () => released++);

        claimed.Write(new byte[] { 1 });
        claimed.SetFeature(new byte[] { 2 });
        _ = claimed.GetInputReport(0, 65);
        Assert.Equal(new byte[] { 7 }, claimed.Read(1));
        Assert.Equal(inner.CanWrite, claimed.CanWrite);
        Assert.Equal(inner.Generation, claimed.Generation);
        Assert.Same(inner, claimed.Inner);

        claimed.Dispose();
        claimed.Dispose();

        Assert.True(inner.IsDisposed);
        Assert.Equal(1, released);
        Assert.Single(inner.Writes);
    }

    [Fact]
    public void RejectsMissingParts() {
        Assert.Throws<ArgumentNullException>(() => new ClaimedTransport(null!, () => { }));
        Assert.Throws<ArgumentNullException>(() => new ClaimedTransport(new FakeDeviceTransport(), null!));
    }
}
