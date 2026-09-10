using System;

namespace FanControl.LianLi.Hid;

/// <summary>
/// The single seam through which the plugin performs USB HID I/O. Everything
/// downstream of <c>Hid/</c> depends only on this interface, never on HidSharp,
/// which makes the protocol and worker layers trivially fakeable in tests.
/// </summary>
internal interface IHidTransport : IDisposable {
    /// <summary>True when the underlying stream accepts writes.</summary>
    bool CanWrite { get; }

    /// <summary>
    /// How many times the transport has reopened the device after a handle fault (a USB
    /// re-enumeration across sleep/wake or hibernate); <c>0</c> for a handle that has never
    /// faulted. A re-enumerated device may have reset, so a controller compares this against the
    /// value it last set the device up under and replays its setup writes (and any saved lighting)
    /// when it has moved on. Read on the worker thread only.
    /// </summary>
    int Generation { get; }

    /// <summary>Write a raw output report. No-ops if the stream is not writable.</summary>
    void Write(byte[] report);

    /// <summary>
    /// Send a raw HID feature report (SET_REPORT(Feature) / <c>HidD_SetFeature</c>). This is the Uni
    /// family's whole control path: set-speed, manual-mode, the RPM primer, and ARGB sync are all
    /// feature reports, as are the lighting effect, fan-quantity, and frame-latch commands - only
    /// lighting colour data goes through <see cref="Write"/> as an output report. A short command
    /// prefix is padded up to the device's feature report length by the transport. No-ops if the
    /// stream is not writable.
    /// </summary>
    void SetFeature(byte[] report);

    /// <summary>
    /// Read an input report of <paramref name="length"/> bytes for the given
    /// <paramref name="reportId"/>. The returned buffer has byte 0 set to the
    /// report id, matching the controller's report layout.
    /// </summary>
    byte[] GetInputReport(byte reportId, int length);

    /// <summary>
    /// Read a raw interrupt-IN report of <paramref name="length"/> bytes from the
    /// device's input endpoint (<c>HidStream.Read</c>), bounded by a read timeout so
    /// a silent device cannot freeze the caller. Used by the 0x0416 command-packet
    /// family, which answers a handshake or telemetry write on the interrupt-IN
    /// endpoint; the Uni 0x0CF2 family does not stream input reports and pulls RPM
    /// with <see cref="GetInputReport"/> instead.
    /// </summary>
    byte[] Read(int length);
}
