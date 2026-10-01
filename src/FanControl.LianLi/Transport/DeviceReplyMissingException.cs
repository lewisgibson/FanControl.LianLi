using System;
using System.IO;

namespace FanControl.LianLi.Transport;

/// <summary>
/// A read that got nothing at all within the pipe timeout: the device did not answer the request
/// that went before it. An <see cref="IOException"/>, so every caller that treats a failed read as
/// a failed exchange still does; a caller for whom a particular reply is optional (L-Connect reads
/// the HydroShift II OLED Curve's reply to a set-pump or sync command and never looks at it) can
/// tell this case from a pipe that failed, which is any other <see cref="IOException"/>.
/// </summary>
internal sealed class DeviceReplyMissingException : IOException {
    /// <summary>A missing reply with no detail.</summary>
    public DeviceReplyMissingException() {
    }

    /// <summary>A missing reply described by <paramref name="message"/>: which read, on which transport.</summary>
    public DeviceReplyMissingException(string message)
        : base(message) {
    }

    /// <summary>A missing reply with an inner cause.</summary>
    public DeviceReplyMissingException(string message, Exception innerException)
        : base(message, innerException) {
    }
}
