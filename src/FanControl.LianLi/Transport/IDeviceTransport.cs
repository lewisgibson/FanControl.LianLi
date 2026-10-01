using System;

namespace FanControl.LianLi.Transport;

/// <summary>
/// The single seam through which the plugin performs USB I/O, over HID or WinUSB. Everything
/// above <c>Transport/</c> depends only on this interface, never on hid.dll or WinUSB,
/// which makes the protocol and worker layers trivially fakeable in tests.
/// </summary>
internal interface IDeviceTransport : IDisposable {
    /// <summary>True when the underlying stream accepts writes.</summary>
    bool CanWrite { get; }

    /// <summary>
    /// How many times the transport has lost its handle to the device (a USB re-enumeration
    /// across sleep/wake or hibernate, a device gone); <c>0</c> for a handle that has never
    /// faulted. It moves on the moment the handle faults, before the next transfer reopens the
    /// device inside its own call, so a controller sees it before anything reaches the reopened
    /// device, which may have reset or be another device on the same path. A controller compares
    /// it against the value it last set the device up (or identified it) under. One that
    /// identifies the device reads its identity first, whatever <see cref="IsFaulted"/> says, since
    /// that read is what reopens the path; one that sets the device up waits until
    /// <see cref="IsFaulted"/> is false again, so its setup writes (and any saved lighting) go to
    /// a device that is actually back. Read on the worker thread only.
    /// </summary>
    int Generation { get; }

    /// <summary>
    /// Whether the handle is known not to reach the device: a transfer faulted it and none has
    /// reopened it since. While true every transfer is refused fast, or reopens the device inside
    /// its own call on the backoff schedule. Read on the worker thread only.
    /// </summary>
    bool IsFaulted { get; }

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
