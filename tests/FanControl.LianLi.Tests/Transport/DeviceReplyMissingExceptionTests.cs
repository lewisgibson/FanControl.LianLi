using System;
using System.IO;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

/// <summary>A missing reply is an <see cref="IOException"/> a caller can tell apart from a failed pipe.</summary>
public class DeviceReplyMissingExceptionTests {
    [Fact]
    public void IsAnIoException_WithTheUsualConstructors() {
        var inner = new InvalidOperationException("inner");

        var bare = new DeviceReplyMissingException();
        var described = new DeviceReplyMissingException("nothing came");
        var caused = new DeviceReplyMissingException("nothing came", inner);

        Assert.IsAssignableFrom<IOException>(bare);
        Assert.Equal("nothing came", described.Message);
        Assert.Same(inner, caused.InnerException);
    }
}
