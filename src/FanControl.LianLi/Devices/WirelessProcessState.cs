using System;
using System.Collections.Generic;

namespace FanControl.LianLi.Devices;

/// <summary>
/// What the wireless controller keeps for the whole process rather than for itself, because
/// FanControl builds a new controller on every refresh - each wake, logon and unlock - where
/// L-Connect's service runs one from start to stop, and does some things only when it starts:
///
/// <list type="bullet">
/// <item>the RF channel the master was last driven on: the master query sets the transmitter's
/// channel, and a new controller does not know the master until its first query is answered, so
/// without it every refresh would put the transmitter on channel 8 for a moment;</item>
/// <item>the save schedule: its first save comes an hour after the start, which a machine that
/// refreshes more often than hourly would otherwise never reach;</item>
/// <item>the water blocks whose screens have been switched to their wireless theme, which L-Connect
/// does at its start and when a block binds, not on every refresh.</item>
/// </list>
///
/// Thread-safe; only one controller drives the dongles at a time.
/// </summary>
internal sealed class WirelessProcessState {
    private readonly object _gate = new object();
    private readonly HashSet<string> _switchedScreens = new HashSet<string>(StringComparer.Ordinal);
    private string? _masterMacText;
    private int? _channel;
    private WirelessSaveSchedule? _saves;

    /// <summary>The channel last driven on, whichever master it was, or null before any; the first query uses it.</summary>
    public int? Last {
        get {
            lock (_gate) {
                return _channel;
            }
        }
    }

    /// <summary>Remember that the master at <paramref name="masterMacText"/> is driven on <paramref name="channel"/>.</summary>
    public void Remember(string masterMacText, int channel) {
        if (masterMacText is null) {
            throw new ArgumentNullException(nameof(masterMacText));
        }

        lock (_gate) {
            _masterMacText = masterMacText;
            _channel = channel;
        }
    }

    /// <summary>The channel the master at <paramref name="masterMacText"/> was last driven on, or null when it is another master or none.</summary>
    public int? RecallFor(string masterMacText) {
        lock (_gate) {
            return string.Equals(_masterMacText, masterMacText, StringComparison.Ordinal) ? _channel : null;
        }
    }

    /// <summary>The process's save schedule, started at <paramref name="nowUtc"/> the first time it is asked for.</summary>
    public WirelessSaveSchedule SaveSchedule(DateTime nowUtc) {
        lock (_gate) {
            return _saves ??= new WirelessSaveSchedule(nowUtc);
        }
    }

    /// <summary>Whether the screen of the water block at <paramref name="macText"/> has been switched in this process.</summary>
    public bool IsScreenSwitched(string macText) {
        lock (_gate) {
            return _switchedScreens.Contains(macText);
        }
    }

    /// <summary>Record that the screen of the water block at <paramref name="macText"/> has been switched.</summary>
    public void MarkScreenSwitched(string macText) {
        if (macText is null) {
            throw new ArgumentNullException(nameof(macText));
        }

        lock (_gate) {
            _ = _switchedScreens.Add(macText);
        }
    }
}
