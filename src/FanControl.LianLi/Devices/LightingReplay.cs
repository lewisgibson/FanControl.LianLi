#if ENABLE_LIGHTING
using System;
using System.Collections.Generic;
using System.Threading;
using FanControl.LianLi.Transport;
using FanControl.LianLi.Protocol;

namespace FanControl.LianLi.Devices;

/// <summary>
/// Writes an encoded lighting sequence to a controller in order, routing feature reports to
/// <see cref="IDeviceTransport.SetFeature"/> and colour output reports to
/// <see cref="IDeviceTransport.Write"/>. The bytes come from <see cref="SlInfinityLightingEncoder"/>;
/// this is faithful playback with no byte math of its own.
/// </summary>
internal static class LightingReplay
{
    // The controller processes one report at a time; L-Connect itself paces its writes
    // (~20 ms apart). A short gap after each transfer matches that and avoids dropping reports
    // during the one-shot startup apply. This is one-time setup I/O, not a worker cadence, so a
    // fixed sleep is appropriate here.
    private const int WriteSpacingMilliseconds = 15;

    /// <summary>
    /// Write every transfer in <paramref name="transfers"/> to <paramref name="transport"/>,
    /// in order, paced like L-Connect.
    /// </summary>
    public static void Apply(IDeviceTransport transport, IReadOnlyList<LightingTransfer> transfers)
    {
        if (transport is null)
        {
            throw new ArgumentNullException(nameof(transport));
        }

        if (transfers is null)
        {
            throw new ArgumentNullException(nameof(transfers));
        }

        foreach (LightingTransfer transfer in transfers)
        {
            if (transfer.IsFeature)
            {
                transport.SetFeature(transfer.Report);
            }
            else
            {
                transport.Write(transfer.Report);
            }

            Thread.Sleep(WriteSpacingMilliseconds);
        }
    }
}
#endif
