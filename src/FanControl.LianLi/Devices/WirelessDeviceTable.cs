using System;
using System.Collections.Generic;
using System.Globalization;
using FanControl.LianLi.Logging;
using FanControl.LianLi.Protocol;

namespace FanControl.LianLi.Devices;

/// <summary>
/// Every wireless device the receiver has reported, in the order it was first heard, kept exactly
/// as L-Connect's <c>MasterDevice.RefreshList</c> keeps its <c>rfList</c>. The order matters beyond
/// display: a speed command's bind index is the device's position among the bound devices in this
/// order (<c>MasterDevice.SyncPwm</c>). Each <see cref="Apply"/> is one list read:
///
/// <list type="bullet">
/// <item>a device heard for the first time is appended; if it names this master it is taken as bound,
/// its receiver slot remembered and its targets started from what it reports;</item>
/// <item>a device's countdown restarts whenever it is heard, and counts down on every read for
/// every device but a Lancool 217, bound or not; every one whose countdown has run out is dropped
/// from the table before the read goes out, so a device that comes back is appended as new and
/// numbered last, as L-Connect numbers it;</item>
/// <item>a bound device that shares its receiver slot with another of this master's devices more than
/// four reads running is either a ghost - an address that differs from the other's only in a first
/// byte of 1, which L-Connect removes and never re-adds - or a conflict L-Connect would resolve by unbinding.</item>
/// </list>
///
/// Where L-Connect would unbind a device the plugin never does (it never sends a bind index of 0):
/// a device that already had a place on the table unbound and later names this master is taken as
/// bound, where L-Connect unbinds it; a device beyond this master's twelfth is refused rather than
/// unbound; and a receiver slot conflict is logged and left. A bound device that is dropped keeps
/// its FanControl sensors, which the controller holds by address: they read 0 rpm and no temperature
/// while the device is off the table, and its duty is sent again once it is heard, so the table
/// follows L-Connect's numbering and the user's bindings survive. On top of L-Connect's table the
/// plugin counts, for every device, the reads in a row that did not carry it, and marks one unheard
/// for <see cref="WirelessDevice.MaximumMissedReads"/> reads while still on the table (the list
/// locked, or no list read for want of a master) as lost, so its fans read 0 rather than a frozen
/// last value there too. The masters in radio range are kept beside the devices, and while the user
/// has L-Connect's device list locked the table is that list (<see cref="Lock"/>). Worker-thread only.
/// </summary>
internal sealed class WirelessDeviceTable {
    // RefreshList unbinds a newly heard device once twelve are bound, counting it.
    private const int MaximumBound = 12;

    // RefreshList acts on a receiver slot conflict once ConflictCnt is past 4.
    private const int ConflictsTolerated = 4;

    // A ghost address is a real device's with this first byte (RefreshList, CheckJustStartDiff).
    private const byte GhostFirstByte = 1;

    // RFList drops a bound Lancool 217 or V150 whose clock is more than two seconds off the master's.
    private const long ClockDriftLimitMilliseconds = 2000;

    private readonly List<WirelessDevice> _devices = new List<WirelessDevice>();
    private readonly List<WirelessMaster> _masters = new List<WirelessMaster>();
    private readonly HashSet<string> _ghostAddresses = new HashSet<string>(StringComparer.Ordinal);

    // The addresses of bound devices dropped from the table and not heard since, so that the log
    // tells one heard again from one never heard before. An address leaves it only when its device
    // returns, so one dropped as a ghost stays listed for the controller's life.
    private readonly HashSet<string> _droppedWhileBound = new HashSet<string>(StringComparer.Ordinal);
    private readonly int _index;
    private readonly ILog _log;

    /// <summary>An empty table for controller <paramref name="index"/>.</summary>
    public WirelessDeviceTable(int index, ILog log) {
        _index = index;
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Every device on the table, in first-heard order.</summary>
    public IReadOnlyList<WirelessDevice> Devices => _devices;

    /// <summary>Every master the receiver hears, this one included, in first-heard order (<c>MasterDevice.masterList</c>).</summary>
    public IReadOnlyList<WirelessMaster> Masters => _masters;

    /// <summary>
    /// Whether the table is L-Connect's locked device list (<c>lock_list</c>): restored as it was saved,
    /// taking no newly heard device, keeping each device's saved product type, and dropping nothing.
    /// </summary>
    public bool IsLocked { get; private set; }

    /// <summary>
    /// Replace the table with L-Connect's locked list, in its order, every device bound as it was
    /// driven when the list was saved (<c>RFController.CheckLockAndInitData</c>).
    /// </summary>
    public void Lock(IReadOnlyList<WirelessLockedDevice> locked) {
        if (locked is null) {
            throw new ArgumentNullException(nameof(locked));
        }

        _devices.Clear();
        foreach (WirelessLockedDevice saved in locked) {
            var device = new WirelessDevice(saved.Record);
            device.TakeAsBound(saved.TargetReceiverType, saved.TargetPwm);
            _devices.Add(device);
        }

        IsLocked = true;
    }

    /// <summary>
    /// Stop keeping the locked list, as L-Connect does once none of its devices names this master
    /// any more (<c>RFController.LockDevice(false)</c>); from the next read the table works as usual.
    /// </summary>
    public void Unlock() => IsLocked = false;

    /// <summary>The device with the given address, or null when it is not on the table.</summary>
    public WirelessDevice? Find(string macText) {
        if (macText is null) {
            throw new ArgumentNullException(nameof(macText));
        }

        foreach (WirelessDevice device in _devices) {
            if (device.MacText == macText) {
                return device;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether L-Connect would leave the device out of its bound-device list for now: a Lancool 217
    /// or V150 whose clock has drifted more than two seconds from the master's (the <c>RFList</c>
    /// getter), so its targets are not updated until the clock pulse brings it back.
    /// </summary>
    public static bool IsClockDrifted(WirelessDevice device) {
        if (device is null) {
            throw new ArgumentNullException(nameof(device));
        }

        return (device.Kind == WirelessDeviceKind.CaseFans || device.Kind == WirelessDeviceKind.V150)
            && Math.Abs(device.ClockOffset) > ClockDriftLimitMilliseconds;
    }

    /// <summary>
    /// A cycle with no list to read, because the master is not known: L-Connect reads no list then,
    /// so none of its own countdowns move, but the plugin still counts the read as one in which nothing was
    /// heard in, so a device is not shown live for ever on readings nothing is refreshing.
    /// </summary>
    public void MissRead() {
        foreach (WirelessDevice device in _devices) {
            device.Heard = false;
        }

        CountMisses();
    }

    /// <summary>
    /// Apply one list read to the table. <paramref name="list"/> is null when the read failed or the
    /// reply was not a list, which still counts as a read in which nothing was heard.
    /// </summary>
    public void Apply(WirelessDeviceList? list, byte[] masterMac, long masterClockMilliseconds) {
        if (masterMac is null) {
            throw new ArgumentNullException(nameof(masterMac));
        }

        // RefreshList counts the read down before it is even sent, for every device but a Lancool
        // 217 (RecType[0] != LC217), and drops whatever has run out before the request goes out, so a
        // read that then fails still drops. A locked list drops nothing.
        foreach (WirelessDevice device in _devices) {
            if (device.Kind != WirelessDeviceKind.CaseFans) {
                device.Live--;
            }

            device.Heard = false;
        }

        foreach (WirelessMaster master in _masters) {
            master.Live--;
        }

        if (!IsLocked) {
            DropExpired();
        }

        // RefreshList stops here for a failed read or an empty list; nothing is added.
        if (list is null || list.Total == 0) {
            CountMisses();
            return;
        }

        bool conflict = ApplyRecords(list.Records, masterMac);
        CountMisses();
        // After the records RefreshList eases the conflict counts and runs the same removal again,
        // which can find nothing the first did not, since every device it has just heard is back at
        // its full countdown; then it takes the clock offsets, whatever was dropped.
        if (!IsLocked) {
            EaseConflicts(conflict);
        }

        UpdateClockOffsets(masterClockMilliseconds);
    }

    private bool ApplyRecords(IReadOnlyList<WirelessDeviceRecord> records, byte[] masterMac) {
        bool conflict = false;
        var ghosts = new HashSet<WirelessDevice>();
        foreach (WirelessDeviceRecord record in records) {
            // A master dongle's record goes to L-Connect's separate master list, never this one.
            if (record.Kind == WirelessDeviceKind.Master) {
                HearMaster(record);
                continue;
            }

            WirelessDevice? device = Find(record.MacText);
            bool first = device is null;
            if (device is null) {
                // A locked list takes no new device.
                if (IsLocked || _ghostAddresses.Contains(record.MacText)) {
                    continue;
                }

                device = new WirelessDevice(record);
                _devices.Add(device);
                if (_droppedWhileBound.Remove(record.MacText)) {
                    // Back after being dropped: a fresh entry, tried as any device first heard is;
                    // only the log tells it from a new one.
                    _log.Write(string.Format(CultureInfo.InvariantCulture, "W{0}:{1} heard again", _index, device.MacText));
                }
            } else {
                device.Update(IsLocked ? record.WithLockedIdentity(device.Record) : record);
            }

            device.Heard = true;
            device.EverHeard = true;
            if (record.IsBoundTo(masterMac) && !device.IsBound) {
                TakeAsBound(device, first);
            }

            // RefreshList checks receiver slots only while the list is not locked.
            if (device.IsBound && !IsLocked && CheckReceiverSlot(device, masterMac, ghosts)) {
                conflict = true;
            } else {
                device.ConflictLogged = false;
            }
        }

        foreach (WirelessDevice ghost in ghosts) {
            _devices.Remove(ghost);
            _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "W{0}:{1} is a ghost of another device's address sharing its receiver slot; dropped and not added again, as L-Connect does",
                _index,
                ghost.MacText));
        }

        return conflict;
    }

    private void HearMaster(WirelessDeviceRecord record) {
        foreach (WirelessMaster master in _masters) {
            if (master.MacText == record.MacText) {
                master.Channel = record.Channel;
                master.Live = WirelessDevice.MaximumMissedReads;
                return;
            }
        }

        _masters.Add(new WirelessMaster(record.MacText, record.Channel));
    }

    private void TakeAsBound(WirelessDevice device, bool first) {
        int bound = 1;
        foreach (WirelessDevice other in _devices) {
            if (other.IsBound) {
                bound++;
            }
        }

        // A refused device is looked at again on every read that carries it: a bound device can be
        // dropped (its countdown runs even while bound), and then there is room for it. The refusal
        // is logged once, not on every read.
        if (bound >= MaximumBound) {
            if (!device.IsRefused) {
                device.IsRefused = true;
                _log.Write(string.Format(
                    CultureInfo.InvariantCulture,
                    "W{0}:{1} names this master but {2} devices already do; L-Connect would unbind it, so it is not driven",
                    _index,
                    device.MacText,
                    bound - 1));
            }

            return;
        }

        string how = device.IsRefused
            ? " (refused earlier, now there is room)"
            : first ? string.Empty : " (first heard unbound; L-Connect would unbind it, the plugin drives it)";
        device.IsRefused = false;
        device.TakeAsBound();
        _log.Write(string.Format(
            CultureInfo.InvariantCulture,
            "W{0}:{1} bound to this master{2}: type {3}, {4} fan(s), receiver slot {5}, channel {6}",
            _index,
            device.MacText,
            how,
            device.Record.DeviceType,
            device.Record.FanCount,
            device.Record.ReceiverType,
            device.Record.Channel));
    }

    // True when the device shares its receiver slot with another of this master's devices.
    private bool CheckReceiverSlot(WirelessDevice device, byte[] masterMac, HashSet<WirelessDevice> ghosts) {
        bool conflict = false;
        foreach (WirelessDevice other in _devices) {
            if (!other.Record.IsBoundTo(masterMac) || ReferenceEquals(other, device)
                || other.Record.ReceiverType != device.Record.ReceiverType) {
                continue;
            }

            conflict = true;
            device.ConflictCount++;
            if (device.ConflictCount <= ConflictsTolerated) {
                continue;
            }

            if ((device.Mac[0] == GhostFirstByte || other.Mac[0] == GhostFirstByte) && SameAfterFirstByte(device.Mac, other.Mac)) {
                WirelessDevice ghost = device.Mac[0] == GhostFirstByte ? device : other;
                _ghostAddresses.Add(ghost.MacText);
                ghosts.Add(ghost);
                continue;
            }

            device.ConflictCount = 0;
            if (!device.ConflictLogged) {
                device.ConflictLogged = true;
                _log.Write(string.Format(
                    CultureInfo.InvariantCulture,
                    "W{0}:{1} shares receiver slot {2} with {3}; L-Connect would unbind it, the plugin leaves the binding alone",
                    _index,
                    device.MacText,
                    device.Record.ReceiverType,
                    other.MacText));
            }
        }

        return conflict;
    }

    private void CountMisses() {
        foreach (WirelessDevice device in _devices) {
            if (device.Heard) {
                device.MissedReads = 0;
                if (device.IsLost) {
                    device.IsLost = false;
                    device.ReapplySavedLook();
                    Report(device, "heard again");
                }

                continue;
            }

            if (++device.MissedReads == WirelessDevice.MaximumMissedReads) {
                device.IsLost = true;
                Report(device, string.Format(
                    CultureInfo.InvariantCulture, "not heard for {0} list reads; reading 0 rpm until it is", WirelessDevice.MaximumMissedReads));
            }
        }
    }

    // RefreshList's rfList.RemoveAll(live <= 0 && !visual), and the same for the masters: every
    // device and master whose countdown has run out goes, in one read.
    private void DropExpired() {
        for (int i = _devices.Count - 1; i >= 0; i--) {
            WirelessDevice device = _devices[i];
            if (device.Live > 0) {
                continue;
            }

            _devices.RemoveAt(i);
            if (device.IsBound) {
                _droppedWhileBound.Add(device.MacText);
            }

            Report(device, string.Format(
                CultureInfo.InvariantCulture,
                "not heard for {0} list reads; dropped from the device list as L-Connect drops it, reading 0 rpm until it returns",
                WirelessDevice.MaximumMissedReads));
        }

        _masters.RemoveAll(master => master.Live <= 0);
    }

    // After the records: without a conflict anywhere in this read every conflict count eases by one.
    private void EaseConflicts(bool conflict) {
        if (conflict) {
            return;
        }

        foreach (WirelessDevice device in _devices) {
            if (device.ConflictCount > 0) {
                device.ConflictCount--;
            }
        }
    }

    private void UpdateClockOffsets(long masterClockMilliseconds) {
        foreach (WirelessDevice device in _devices) {
            if (device.Kind == WirelessDeviceKind.CaseFans || device.Kind == WirelessDeviceKind.V150) {
                device.ClockOffset = masterClockMilliseconds - device.Record.ClockMilliseconds;
            }
        }
    }

    private void Report(WirelessDevice device, string what) {
        if (device.IsBound) {
            _log.Write(string.Format(CultureInfo.InvariantCulture, "W{0}:{1} {2}", _index, device.MacText, what));
        }
    }

    private static bool SameAfterFirstByte(byte[] a, byte[] b) {
        for (int i = 1; i < a.Length; i++) {
            if (a[i] != b[i]) {
                return false;
            }
        }

        return true;
    }
}
