using System;
using System.Collections.Generic;

namespace FanControl.LianLi.Devices;

/// <summary>
/// What the wireless controller keeps for the whole process not for itself, because
/// FanControl builds a new controller on every refresh - each wake, logon and unlock - where
/// L-Connect's service runs one from start to stop, and does some things only when it starts:
///
/// <list type="bullet">
/// <item>the RF channel the master was last driven on: the master query sets the transmitter's
/// channel, and a new controller does not know the master until its first query is answered, so
/// without it every refresh would put the transmitter on channel 8 for a moment;</item>
/// <item>the save schedule: its first save comes an hour after the start, which a machine that
/// refreshes more often than hourly would otherwise never reach;</item>
/// <item>the water blocks whose screens have been switched to their wireless theme, and the LCD
/// FLEX groups whose screens have been given their themes' colours. L-Connect sends both when its
/// service starts and relies on the device keeping them: it repeats neither when a device drops
/// off its table and returns, nor on a resume. The only thing that does repeat them is the
/// transmitter dongle leaving the USB bus, since L-Connect's service builds a new controller when
/// it returns, whose <c>ApplyAll</c> sends both again to every bound device. So the marks belong
/// to a <em>transmitter lifetime</em>: the moment any controller of the process sees the
/// transmitter's handle lost (<see cref="TransmitterLost"/>), every mark is dropped and the
/// lifetime moves on, and a look begun under the lifetime before marks nothing for this one;</item>
/// <item>which devices each wireless controller drives over the radio at the moment, so that a
/// controller reaching the same device another way (a FLEX group on its USB receiver) can leave it
/// to the radio. Each controller publishes its own set, keyed by the controller, so one closing
/// after FanControl's refresh does not take the set of the one built in its place with it;</item>
/// <item>the duty FanControl last commanded for each fan group, keyed on the group's RF address. A
/// FLEX chain can be driven by two controllers under one set of sensor ids, and the host's control
/// for it is registered on one of them, so the duty is kept here by whichever the host gives it to
/// (either controller, or the stand-in's control, <c>Plugin/ControlSensor</c>, while it stands in
/// for one) and read by whichever drives the chain now.</item>
/// </list>
///
/// Thread-safe; only one controller drives the dongles at a time.
/// </summary>
internal sealed class WirelessProcessState {
    private readonly object _gate = new object();
    private readonly HashSet<string> _switchedScreens = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> _colouredScreens = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<object, HashSet<string>> _boundDevices = new Dictionary<object, HashSet<string>>();
    private readonly Dictionary<string, int> _chainTargets = new Dictionary<string, int>(StringComparer.Ordinal);
    private string? _masterMacText;
    private int? _channel;
    private WirelessSaveSchedule? _saves;
    private int _transmitterLifetime;

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

    /// <summary>
    /// How many times a controller of this process has seen the transmitter's handle lost: the
    /// lifetime the screen switch and screen colour marks belong to. A look takes it as it begins
    /// and returns it when it is marked, so a look begun under an earlier lifetime marks nothing.
    /// </summary>
    public int TransmitterLifetime {
        get {
            lock (_gate) {
                return _transmitterLifetime;
            }
        }
    }

    /// <summary>
    /// A controller has seen the transmitter's handle lost: every screen switch and screen colour
    /// mark is dropped and the <see cref="TransmitterLifetime"/> moves on, so each device is sent
    /// both looks again once the transmitter is back, as L-Connect's new controller sends them.
    /// </summary>
    public void TransmitterLost() {
        lock (_gate) {
            _transmitterLifetime++;
            _switchedScreens.Clear();
            _colouredScreens.Clear();
        }
    }

    /// <summary>Whether the screen of the water block at <paramref name="macText"/> has been switched to its wireless theme under the current <see cref="TransmitterLifetime"/>.</summary>
    public bool IsScreenSwitched(string macText) => ScreenSwitchedUnder(macText) != null;

    /// <summary>
    /// The <see cref="TransmitterLifetime"/> under which the screen of the water block at
    /// <paramref name="macText"/> was marked switched, or null while it is not marked. A mark only
    /// ever stands under the current lifetime, and is read together with it so that a controller
    /// taking the mark as its own look's done state takes the lifetime it stands under, not one
    /// moved on meanwhile.
    /// </summary>
    public int? ScreenSwitchedUnder(string macText) {
        lock (_gate) {
            return _switchedScreens.Contains(macText) ? _transmitterLifetime : (int?)null;
        }
    }

    /// <summary>
    /// Record that the screen of the water block at <paramref name="macText"/> has been switched by
    /// a look begun under <paramref name="transmitterLifetime"/>. A look begun under an earlier
    /// lifetime marks nothing, since its sends were cut short by the transmitter's loss.
    /// </summary>
    public void MarkScreenSwitched(string macText, int transmitterLifetime) {
        if (macText is null) {
            throw new ArgumentNullException(nameof(macText));
        }

        lock (_gate) {
            if (transmitterLifetime == _transmitterLifetime) {
                _ = _switchedScreens.Add(macText);
            }
        }
    }

    /// <summary>Whether the screens of the LCD FLEX group at <paramref name="macText"/> have been given their themes' colours under the current <see cref="TransmitterLifetime"/>.</summary>
    public bool AreScreensColoured(string macText) => ScreensColouredUnder(macText) != null;

    /// <summary>
    /// The <see cref="TransmitterLifetime"/> under which the screens of the LCD FLEX group at
    /// <paramref name="macText"/> were marked coloured, or null while they are not marked; read
    /// together with the lifetime for the reason <see cref="ScreenSwitchedUnder"/> gives.
    /// </summary>
    public int? ScreensColouredUnder(string macText) {
        lock (_gate) {
            return _colouredScreens.Contains(macText) ? _transmitterLifetime : (int?)null;
        }
    }

    /// <summary>
    /// Record that the screens of the LCD FLEX group at <paramref name="macText"/> have been given
    /// their themes' colours by a look begun under <paramref name="transmitterLifetime"/>. A look
    /// begun under an earlier lifetime marks nothing.
    /// </summary>
    public void MarkScreensColoured(string macText, int transmitterLifetime) {
        if (macText is null) {
            throw new ArgumentNullException(nameof(macText));
        }

        lock (_gate) {
            if (transmitterLifetime == _transmitterLifetime) {
                _ = _colouredScreens.Add(macText);
            }
        }
    }

    /// <summary>Whether the device at <paramref name="macText"/> is one that a live wireless controller drives at the moment: bound to its master, heard by it and naming it.</summary>
    public bool IsBoundToMaster(string macText) {
        if (macText is null) {
            throw new ArgumentNullException(nameof(macText));
        }

        lock (_gate) {
            foreach (HashSet<string> published in _boundDevices.Values) {
                if (published.Contains(macText)) {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Replace the set of devices the wireless controller <paramref name="owner"/> drives with
    /// <paramref name="macTexts"/>: after every list read, and with nothing while it has no master.
    /// Another controller's set is untouched.
    /// </summary>
    public void RecordBoundDevices(object owner, IEnumerable<string> macTexts) {
        if (owner is null) {
            throw new ArgumentNullException(nameof(owner));
        }

        if (macTexts is null) {
            throw new ArgumentNullException(nameof(macTexts));
        }

        lock (_gate) {
            _boundDevices[owner] = new HashSet<string>(macTexts, StringComparer.Ordinal);
        }
    }

    /// <summary>Forget every device <paramref name="owner"/> drives: that wireless controller has closed.</summary>
    public void ForgetBoundDevices(object owner) {
        if (owner is null) {
            throw new ArgumentNullException(nameof(owner));
        }

        lock (_gate) {
            _ = _boundDevices.Remove(owner);
        }
    }

    /// <summary>Keep <paramref name="duty"/> as the commanded duty of the fan group at <paramref name="macText"/>, for whichever controller drives it.</summary>
    public void SetChainTarget(string macText, int duty) {
        if (macText is null) {
            throw new ArgumentNullException(nameof(macText));
        }

        lock (_gate) {
            _chainTargets[macText] = duty;
        }
    }

    /// <summary>Release the fan group at <paramref name="macText"/>: FanControl no longer commands it, so nothing re-asserts a duty for it.</summary>
    public void ReleaseChainTarget(string macText) {
        if (macText is null) {
            throw new ArgumentNullException(nameof(macText));
        }

        lock (_gate) {
            _ = _chainTargets.Remove(macText);
        }
    }

    /// <summary>The commanded duty of the fan group at <paramref name="macText"/>, or -1 while FanControl commands none.</summary>
    public int ChainTarget(string macText) {
        if (macText is null) {
            throw new ArgumentNullException(nameof(macText));
        }

        lock (_gate) {
            return _chainTargets.TryGetValue(macText, out int duty) ? duty : -1;
        }
    }
}
