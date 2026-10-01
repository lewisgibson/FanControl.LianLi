#pragma warning disable CA1510, CA1512 // The released v1.1.34 source, kept verbatim as the oracle the current encoder is compared against (ReleasedLightingEncoderTests); it compiles here on net8.0, where the analyzer would have it use ThrowIfNull.
#if ENABLE_LIGHTING
using System;
using System.Collections.Generic;

namespace FanControl.LianLi.Protocol;

/// <summary>
/// The per-family parameters that drive <see cref="ReleasedUniFanLightingEncoder"/>. The Uni fan
/// controllers (SL, AL, SL v2, AL v2, and the Redragon SL variant) share one apply sequence -
/// fan-quantity, then per-port colour + effect, then a frame latch - and differ only in the
/// values captured here: the port apply order, the fan-quantity report layout, the mode-to-wire
/// table, the frame-latch value, and the colour-expansion rule.
/// </summary>
internal sealed class ReleasedUniFanLightingProfile
{
    /// <summary>Create a family profile from its wire parameters.</summary>
    /// <param name="reverseApplyOrder">True to apply ports high-to-low (AL, AL v2); false low-to-high (SL, SL v2).</param>
    /// <param name="quantityGroupCount">Number of fan-speed groups the fan-quantity report is sent for (four on every Uni family).</param>
    /// <param name="quantityRegister">The <c>byte[2]</c> register that selects the fan-quantity command for this family.</param>
    /// <param name="packQuantityNibbles">True to pack group (high nibble) and quantity (low nibble) into one byte (SL, SL v2); false to send group+1 then quantity in separate bytes (AL, AL v2).</param>
    /// <param name="maxQuantity">The inclusive upper bound L-Connect validates each group's fan quantity against (4 on SL/AL, 6 on the v2 families).</param>
    /// <param name="defaultQuantity">The per-group fan quantity used when the saved value is absent or out of range; its length must equal <paramref name="quantityGroupCount"/>.</param>
    /// <param name="frameValue">The value latched by the frame report (1 on every family except SL v2, which latches 4).</param>
    /// <param name="modeToWire">Saved lighting-mode value to on-wire effect byte; a mode absent here is one the controller does not apply, so its port is skipped.</param>
    /// <param name="expandColors">Given the saved mode and its colours, the exact per-LED buffer this family writes (fan-group palette, per-fan ring, outer-corner, or cycle-fill).</param>
    /// <exception cref="ArgumentNullException"><paramref name="defaultQuantity"/>, <paramref name="modeToWire"/>, or <paramref name="expandColors"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="quantityGroupCount"/> is not positive or <paramref name="maxQuantity"/> is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="defaultQuantity"/> does not have <paramref name="quantityGroupCount"/> entries.</exception>
    public ReleasedUniFanLightingProfile(
        bool reverseApplyOrder,
        int quantityGroupCount,
        byte quantityRegister,
        bool packQuantityNibbles,
        int maxQuantity,
        IReadOnlyList<int> defaultQuantity,
        byte frameValue,
        IReadOnlyDictionary<int, byte> modeToWire,
        Func<int, IReadOnlyList<RgbColor>, RgbColor[]> expandColors)
    {
        if (quantityGroupCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantityGroupCount));
        }

        if (maxQuantity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxQuantity));
        }

        if (defaultQuantity is null)
        {
            throw new ArgumentNullException(nameof(defaultQuantity));
        }

        if (defaultQuantity.Count != quantityGroupCount)
        {
            throw new ArgumentException("Default quantity length must match the group count.", nameof(defaultQuantity));
        }

        ReverseApplyOrder = reverseApplyOrder;
        QuantityGroupCount = quantityGroupCount;
        QuantityRegister = quantityRegister;
        PackQuantityNibbles = packQuantityNibbles;
        MaxQuantity = maxQuantity;
        DefaultQuantity = defaultQuantity;
        FrameValue = frameValue;
        ModeToWire = modeToWire ?? throw new ArgumentNullException(nameof(modeToWire));
        ExpandColors = expandColors ?? throw new ArgumentNullException(nameof(expandColors));
    }

    /// <summary>True to apply ports high-to-low; false low-to-high.</summary>
    public bool ReverseApplyOrder { get; }

    /// <summary>Number of fan-speed groups the fan-quantity report is sent for.</summary>
    public int QuantityGroupCount { get; }

    /// <summary>The <c>byte[2]</c> register that selects this family's fan-quantity command.</summary>
    public byte QuantityRegister { get; }

    /// <summary>True to pack group and quantity into one byte; false to send them in separate bytes.</summary>
    public bool PackQuantityNibbles { get; }

    /// <summary>Inclusive upper bound each group's saved fan quantity is validated against.</summary>
    public int MaxQuantity { get; }

    /// <summary>Per-group fan quantity used when the saved value is absent or out of range.</summary>
    public IReadOnlyList<int> DefaultQuantity { get; }

    /// <summary>The value latched by the frame report to display the assembled look.</summary>
    public byte FrameValue { get; }

    /// <summary>Saved lighting-mode value to on-wire effect byte; absent modes leave their port untouched.</summary>
    public IReadOnlyDictionary<int, byte> ModeToWire { get; }

    /// <summary>Given a saved mode and its colours, the exact per-LED buffer this family writes.</summary>
    public Func<int, IReadOnlyList<RgbColor>, RgbColor[]> ExpandColors { get; }
}
#endif

#pragma warning restore CA1510, CA1512
