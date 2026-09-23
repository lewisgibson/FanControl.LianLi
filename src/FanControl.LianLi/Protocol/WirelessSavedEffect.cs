using System;

namespace FanControl.LianLi.Protocol;

/// <summary>
/// A lighting effect as L-Connect saved it for one wireless device: the rendered, compressed frame
/// data and the figures the device needs to play it back, plus the 4-byte identity L-Connect stamped
/// it with. The device reports the identity of the effect it is running, so a saved effect whose
/// identity differs is streamed to it again (see <see cref="WirelessProtocol.EncodeEffectPayloads"/>).
/// </summary>
internal sealed class WirelessSavedEffect {
    /// <summary>Create an effect; the byte arrays are copied.</summary>
    public WirelessSavedEffect(
        byte[] data, byte[] effectIndex, int totalFrame, int totalSubFrame, byte ledNum, double interval, double subInterval) {
        if (data is null) {
            throw new ArgumentNullException(nameof(data));
        }

        if (effectIndex is null) {
            throw new ArgumentNullException(nameof(effectIndex));
        }

        if (effectIndex.Length != WirelessProtocol.EffectIndexLength) {
            throw new ArgumentException("An effect identity is " + WirelessProtocol.EffectIndexLength + " bytes.", nameof(effectIndex));
        }

        if (data.Length == 0) {
            throw new ArgumentException("An effect carries frame data.", nameof(data));
        }

        Data = (byte[])data.Clone();
        EffectIndex = (byte[])effectIndex.Clone();
        TotalFrame = totalFrame;
        TotalSubFrame = totalSubFrame;
        LedNum = ledNum;
        Interval = interval;
        SubInterval = subInterval;
    }

    /// <summary>The compressed frame data L-Connect rendered.</summary>
    public byte[] Data { get; }

    /// <summary>The effect's 4-byte identity.</summary>
    public byte[] EffectIndex { get; }

    /// <summary>How many frames the effect has.</summary>
    public int TotalFrame { get; }

    /// <summary>How many sub-frames (for effects with an inner layer).</summary>
    public int TotalSubFrame { get; }

    /// <summary>How many LEDs the frames address.</summary>
    public byte LedNum { get; }

    /// <summary>Milliseconds between frames.</summary>
    public double Interval { get; }

    /// <summary>Milliseconds between sub-frames.</summary>
    public double SubInterval { get; }

    /// <summary>
    /// The same effect played at another frame interval. L-Connect re-times a type 2 or 4 Strimer's
    /// effect to the first type 1 or 3 Strimer's before streaming it (<c>MasterDevice.SyncStrimmer_22</c>).
    /// </summary>
    public WirelessSavedEffect WithInterval(double interval)
        => new WirelessSavedEffect(Data, EffectIndex, TotalFrame, TotalSubFrame, LedNum, interval, SubInterval);
}
