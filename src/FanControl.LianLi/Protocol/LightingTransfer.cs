#if ENABLE_LIGHTING
using System;

namespace FanControl.LianLi.Protocol;

/// <summary>
/// One encoded HID transfer in a lighting apply sequence: the exact report bytes and
/// whether it is sent as a feature report (effect / fan-quantity / frame-latch) or an
/// output report (colour data). Produced by <see cref="SlInfinityLightingEncoder"/> and
/// written verbatim by the replay; the bytes include the leading report id (0xE0).
/// </summary>
internal readonly struct LightingTransfer
{
    /// <summary>Create a transfer of the given kind carrying <paramref name="report"/>.</summary>
    public LightingTransfer(bool isFeature, byte[] report)
    {
        IsFeature = isFeature;
        Report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>True for a feature report (<c>SetFeature</c>); false for an output report (<c>Write</c>).</summary>
    public bool IsFeature { get; }

    /// <summary>The exact report bytes, including the leading report id (0xE0).</summary>
    public byte[] Report { get; }
}
#endif
