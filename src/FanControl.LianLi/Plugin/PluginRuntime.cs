using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Logging;
using FanControl.LianLi.Worker;

namespace FanControl.LianLi.Plugin;

/// <summary>
/// The plugin's state that has to outlive a plugin object. FanControl creates a new plugin
/// instance on every refresh (its plugin loader instantiates the type afresh each Initialize, while
/// the assembly stays loaded), and it only calls Close on an instance that registered at least one
/// sensor. So what an instance starts - the worker and the controllers it drives - is owned here,
/// where the next instance can shut it down even when the previous one was never closed; and the
/// controllers the process has built are remembered here, where the next instance can stand in for
/// one it cannot reach. What is remembered is also kept between runs through an
/// <see cref="IRememberedControllerStore"/>, so after a reboot the sensors are registered before the
/// devices have answered, and one not built for <see cref="ForgetAfter"/> is let go.
/// </summary>
internal sealed class PluginRuntime {
    // The least time between two refresh requests. A device that keeps appearing and vanishing
    // must not keep FanControl refreshing; one refresh is at most two attempts three seconds apart, so
    // this leaves it room to finish and settle before the next.
    private static readonly TimeSpan RefreshSpacing = TimeSpan.FromSeconds(30);

    // How long a controller no scan has built is still stood in for, and a sensor no build has
    // reported still registered: long enough to outlast a holiday with the machine off, a device
    // unplugged for a while or a fan stopped by its curve, short enough that hardware that is gone
    // for good stops showing as a fan reading nothing.
    internal static readonly TimeSpan ForgetAfter = TimeSpan.FromDays(30);

    private readonly object _sync = new object();

    // Saves come from FanControl's thread (Load) and from a late build's own thread. They are one at
    // a time, each snapshot taken inside the gate, so the file always ends with the newest state and
    // two writers never share its temporary file.
    private readonly object _saveGate = new object();
    private readonly IClock _clock;
    private readonly IRememberedControllerStore _store;
    // Bumped when the file is read after a scan numbered controllers without it.
    private int _numberingEpoch;

    // Builds still running from before that read: they hold their device, but not the index they
    // were guessed, which IndexFor hands out by the file.
    private readonly HashSet<string> _unnumberedBuilds = new HashSet<string>(StringComparer.Ordinal);

    // The keys this process remembered first, rather than read back from the file, that have not yet
    // had a sensor. A move takes the mark with it.
    private readonly HashSet<string> _firstRememberedHere = new HashSet<string>(StringComparer.Ordinal);

    private readonly Dictionary<string, RememberedController> _remembered =
        new Dictionary<string, RememberedController>(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _lastSeen = new Dictionary<string, DateTime>(StringComparer.Ordinal);

    // The build running for each key, and the index it was given. A build can outlive the scan that
    // started it - and that scan's plugin instance - so its index is held here until its result lands,
    // or a later scan could hand the same index to another controller and the two would collide when
    // the late one is remembered. And only one build of a device runs at a time, whoever starts it -
    // a scan, or a stand-in rebuilding - so a device is never opened and set up by two owners at once.
    private readonly Dictionary<string, int> _building = new Dictionary<string, int>(StringComparer.Ordinal);
    private bool _restored;
    private bool _restoring;

    private object? _owner;
    private KeepAliveWorker? _worker;
    private string? _refreshReason;
    private DateTime? _lastRefresh;

    /// <summary>A runtime timed by <paramref name="clock"/>, keeping what it remembers in <paramref name="store"/>.</summary>
    public PluginRuntime(IClock clock, IRememberedControllerStore store) {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>What the wireless controller keeps across the new one each refresh builds: its channel, save schedule and switched screens.</summary>
    public WirelessProcessState WirelessState { get; } = new WirelessProcessState();

    /// <summary>
    /// How often the worker's loops tick, when not the worker's own once a second; a test lengthens
    /// it to see that a wake alone brings a write forward.
    /// </summary>
    internal int? WorkerTickIntervalMilliseconds { get; set; }

    /// <summary>The one runtime of this process, shared by every plugin instance the host creates.</summary>
    public static PluginRuntime Process { get; } = new PluginRuntime(new SystemClock(), RememberedControllerFile.Machine);

    /// <summary>
    /// Read what earlier runs remembered, once per process; later calls do nothing, unless the file
    /// could not be read then, when the next call reads it again (and nothing is saved until one has).
    /// A controller no scan has built for longer than <see cref="ForgetAfter"/> is dropped.
    /// </summary>
    public void Restore(ILog log) {
        if (log is null) {
            throw new ArgumentNullException(nameof(log));
        }

        lock (_sync) {
            if (_restored || _restoring) {
                return;
            }

            _restoring = true;
        }

        IReadOnlyList<StoredController>? read = _store.Load(log);
        lock (_sync) {
            _restoring = false;
            _restored = read != null;
        }

        if (read is null) {
            return;
        }

        IReadOnlyList<StoredController> stored = read;
        DateTime now = _clock.UtcNow;
        int kept = 0;
        int expired = 0;
        lock (_sync) {
            // A scan that ran before this read numbered its controllers without the file. Any build
            // it started that is still running carries those numbers, so a new epoch begins: what
            // such a build reports is not remembered (see NumberingEpoch).
            if (_remembered.Count > 0 || _building.Count > 0) {
                _numberingEpoch++;

                // Those builds' indices were guesses: the next scan numbers by the file instead.
                _unnumberedBuilds.UnionWith(_building.Keys);
            }

            var saved = new Dictionary<string, StoredController>(StringComparer.Ordinal);
            foreach (StoredController controller in stored) {
                if (ClockSpan.IsOlderThan(now, controller.LastSeenUtc, ForgetAfter)) {
                    expired++;
                    continue;
                }

                // Two controllers at one index would register the same wired sensor ids; a file
                // that has that is not one this process wrote, and the later entry is not used.
                if (saved.Values.Any(other => other.Controller.Index == controller.Controller.Index)) {
                    log.Write(string.Format(
                        CultureInfo.InvariantCulture,
                        "remembered controller {0} not used: index {1} is already another's",
                        controller.Key,
                        controller.Controller.Index));
                    continue;
                }

                saved[controller.Key] = controller;
                kept++;
            }

            // Anything already remembered was built by a scan that ran while the file could not be
            // read, and numbered without it. The file wins: a controller it has keeps its saved index
            // and every sensor it saved, and one numbered onto a saved index is let go, to be
            // numbered again by the next scan. Keeping the guesses would move a curve onto another
            // controller's fans for good once they were saved over the file.
            foreach (KeyValuePair<string, RememberedController> guessed in _remembered.ToList()) {
                if (!saved.ContainsKey(guessed.Key)
                    && saved.Values.Any(other => other.Controller.Index == guessed.Value.Index)) {
                    _ = _remembered.Remove(guessed.Key);
                    _ = _lastSeen.Remove(guessed.Key);
                    _ = _firstRememberedHere.Remove(guessed.Key);
                    log.Write(string.Format(
                        CultureInfo.InvariantCulture,
                        "controller {0} was numbered {1} before the saved controllers could be read; it is numbered again at the next scan",
                        guessed.Key,
                        guessed.Value.Index));
                }
            }

            foreach (StoredController controller in saved.Values) {
                RememberedController fromFile = controller.Controller.Pruned(now, ForgetAfter);
                if (_remembered.TryGetValue(controller.Key, out RememberedController? built) && built.Index == fromFile.Index) {
                    _remembered[controller.Key] = built.MergedWith(fromFile, now, ForgetAfter);
                } else {
                    _remembered[controller.Key] = fromFile;
                    _lastSeen[controller.Key] = controller.LastSeenUtc;
                }

                _ = _firstRememberedHere.Remove(controller.Key);
            }
        }

        if (stored.Count > 0) {
            log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "remembered {0} controller(s) from earlier runs; {1} not built for {2} days were let go",
                kept,
                expired,
                ForgetAfter.TotalDays));
        }
    }

    /// <summary>
    /// Let go of every remembered controller no scan has built for <see cref="ForgetAfter"/>, and of
    /// every sensor no build has reported for as long, on every scan: FanControl's service can run
    /// for months across sleeps and wakes, so the window cannot wait for the next process. A
    /// controller whose build is still running is kept.
    /// </summary>
    public void ForgetExpired(ILog log) {
        if (log is null) {
            throw new ArgumentNullException(nameof(log));
        }

        var forgotten = new List<string>();
        lock (_sync) {
            DateTime now = _clock.UtcNow;
            foreach (string key in new List<string>(_remembered.Keys)) {
                if (ClockSpan.IsOlderThan(now, _lastSeen[key], ForgetAfter) && !_building.ContainsKey(key)) {
                    _ = _remembered.Remove(key);
                    _ = _lastSeen.Remove(key);
                    forgotten.Add(key);
                } else {
                    _remembered[key] = _remembered[key].Pruned(now, ForgetAfter);
                }
            }
        }

        foreach (string key in forgotten) {
            log.Write(string.Format(
                CultureInfo.InvariantCulture, "  {0} not built for {1} days; no longer stood in for", key, ForgetAfter.TotalDays));
        }
    }

    /// <summary>Save what is remembered now, for the next run.</summary>
    public void Persist(ILog log) {
        if (log is null) {
            throw new ArgumentNullException(nameof(log));
        }

        lock (_saveGate) {
            var stored = new List<StoredController>();
            lock (_sync) {
                // A saved file not read yet would lose what it holds; it is read again, and only
                // then replaced.
                if (!_restored) {
                    log.Write("remembered controllers not saved: the saved ones have not been read yet");
                    return;
                }

                foreach (KeyValuePair<string, RememberedController> entry in _remembered) {
                    stored.Add(new StoredController(entry.Key, entry.Value, _lastSeen[entry.Key]));
                }
            }

            stored.Sort((a, b) => a.Controller.Index.CompareTo(b.Controller.Index));
            _store.Save(stored, log);
        }
    }

    /// <summary>
    /// Stop the running worker and release its controllers, whichever instance started them.
    /// Bounded by the worker's own join.
    /// </summary>
    public void Stop() {
        KeepAliveWorker? worker;
        lock (_sync) {
            worker = _worker;
            _worker = null;
            _owner = null;
        }

        worker?.Dispose();
    }

    /// <summary>Stop the running worker only if <paramref name="owner"/> started it.</summary>
    public void StopIfOwnedBy(object owner) {
        KeepAliveWorker? worker = null;
        lock (_sync) {
            if (ReferenceEquals(_owner, owner)) {
                worker = _worker;
                _worker = null;
                _owner = null;
            }
        }

        worker?.Dispose();
    }

    /// <summary>
    /// Start a worker over <paramref name="controllers"/> on behalf of <paramref name="owner"/>,
    /// stopping whatever was running first.
    /// </summary>
    public void Start(object owner, IReadOnlyList<IFanDevice> controllers, ILog log) {
        if (owner is null) {
            throw new ArgumentNullException(nameof(owner));
        }

        KeepAliveWorker worker = WorkerTickIntervalMilliseconds is int interval
            ? new KeepAliveWorker(controllers, log, interval)
            : new KeepAliveWorker(controllers, log);
        KeepAliveWorker? previous;
        lock (_sync) {
            previous = _worker;
            _worker = worker;
            _owner = owner;
        }

        previous?.Dispose();
        worker.Start();
    }

    /// <summary>Whether <paramref name="owner"/> started the worker that is running now.</summary>
    public bool IsOwnedBy(object owner) {
        lock (_sync) {
            return _worker != null && ReferenceEquals(_owner, owner);
        }
    }

    /// <summary>Have the worker run a tick now rather than at its next interval: a target changed.</summary>
    public void Wake() {
        // Under the lock: a worker is only disposed after it has been taken out of _worker under
        // this same lock, so the one woken here is never one that is being disposed.
        lock (_sync) {
            _worker?.Wake();
        }
    }

    /// <summary>
    /// Remember what was built for <paramref name="key"/>, merged with what was remembered for it
    /// before (<see cref="RememberedController.MergedWith"/>): a sensor the build did not report is
    /// kept until no build has reported it for <see cref="ForgetAfter"/>. Returns what is now
    /// remembered, which is what the controller's sensors are registered from.
    /// </summary>
    public RememberedController Remember(string key, RememberedController controller) {
        if (key is null) {
            throw new ArgumentNullException(nameof(key));
        }

        if (controller is null) {
            throw new ArgumentNullException(nameof(controller));
        }

        lock (_sync) {
            DateTime now = _clock.UtcNow;
            RememberedController merged = _remembered.TryGetValue(key, out RememberedController? earlier)
                ? controller.MergedWith(earlier, now, ForgetAfter)
                : controller;
            // New until it first has a sensor: one a later prune empties is not new again.
            if (earlier is null && !HasSensors(merged)) {
                _ = _firstRememberedHere.Add(key);
            } else if (HasSensors(merged)) {
                _ = _firstRememberedHere.Remove(key);
            }

            _remembered[key] = merged;
            _lastSeen[key] = now;
            return merged;
        }
    }

    /// <summary>
    /// Which numbering the controllers are built under. It moves on when the saved controllers are
    /// read only after a scan had numbered controllers without them: a build or rebuild started
    /// under an earlier epoch carries an index the file may contradict, and its controller is not
    /// remembered.
    /// </summary>
    public int NumberingEpoch {
        get {
            lock (_sync) {
                return _numberingEpoch;
            }
        }
    }

    /// <summary>
    /// Whether the controller under <paramref name="key"/> is new: never remembered, or first
    /// remembered by this process and never yet with a sensor. Only a new controller may still be
    /// waiting for its devices to answer; one an earlier process remembered with nothing (a pair that
    /// only hears a neighbour's kit, say) is not, so the placeholder it would bring cannot stay for good.
    /// </summary>
    public bool IsNew(string key) {
        lock (_sync) {
            return !_remembered.ContainsKey(key) || _firstRememberedHere.Contains(key);
        }
    }

    private static bool HasSensors(RememberedController controller)
        => controller.Channels.Count > 0 || controller.FanSpeeds.Count > 0 || controller.Temperatures.Count > 0;

    /// <summary>
    /// <see cref="Remember"/>, but only while the numbering is still <paramref name="epoch"/>, the
    /// one the build ran under; null, remembering nothing, once the saved controllers have been read
    /// since. Checked and remembered under one lock, so a read cannot come between the two.
    /// </summary>
    public RememberedController? RememberUnder(int epoch, string key, RememberedController controller) {
        lock (_sync) {
            return epoch == _numberingEpoch ? Remember(key, controller) : null;
        }
    }

    /// <summary>The time on this runtime's clock, for stamping what a build reported.</summary>
    public DateTime UtcNow => _clock.UtcNow;

    /// <summary>
    /// When nothing is remembered under <paramref name="plan"/>'s key, take over a remembered
    /// controller that is the same device on a new path: one of the same kind and product id whose
    /// own key is not in <paramref name="plannedKeys"/> (this scan did not find it where it was):
    /// the one in the same Windows container if there is one, otherwise the only one, and none when
    /// there are several and nothing tells them apart. A device's path is where it is plugged in, and a Lian Li controller has no
    /// serial of its own to tell it by (every Uni unit shares one), so this is the identity that
    /// survives a move to another port: the moved device keeps its index, so its wired sensor ids,
    /// and its sensors, so the user's curves stay bound, rather than registering beside a stand-in
    /// for its old path that can never connect. Returns the key taken over, or null.
    /// </summary>
    public string? TakeOverMoved(ControllerPlan plan, ISet<string> plannedKeys) {
        if (plan is null) {
            throw new ArgumentNullException(nameof(plan));
        }

        if (plannedKeys is null) {
            throw new ArgumentNullException(nameof(plannedKeys));
        }

        lock (_sync) {
            if (_remembered.ContainsKey(plan.Key)) {
                return null;
            }

            // The one candidate Windows put in the same container as the new path is the same unit
            // for certain (where the container outlives the move). Without one, only a single
            // candidate is taken over: with two or more missing, nothing says which moved, and a
            // guess could put one unit's curves on another's fans, so the new path gets an index of
            // its own and the missing ones are stood in for.
            KeyValuePair<string, RememberedController>? moved = null;
            KeyValuePair<string, RememberedController>? sameContainer = null;
            int candidates = 0;
            string? container = plan.Devices[0].ContainerId;
            foreach (KeyValuePair<string, RememberedController> entry in _remembered) {
                RememberedController candidate = entry.Value;
                if (candidate.Plan.Kind != plan.Kind
                    || candidate.Plan.Devices[0].ProductId != plan.Devices[0].ProductId
                    || plannedKeys.Contains(entry.Key)
                    || _building.ContainsKey(entry.Key)) {
                    continue;
                }

                candidates++;
                moved = entry;
                if (container != null && string.Equals(candidate.Plan.Devices[0].ContainerId, container, StringComparison.OrdinalIgnoreCase)) {
                    sameContainer = entry;
                }
            }

            moved = sameContainer ?? (candidates == 1 ? moved : null);
            if (moved is null) {
                return null;
            }

            string from = moved.Value.Key;
            _ = _remembered.Remove(from);
            _remembered[plan.Key] = moved.Value.Value.MovedTo(plan);
            _lastSeen[plan.Key] = _lastSeen[from];
            _ = _lastSeen.Remove(from);
            if (_firstRememberedHere.Remove(from)) {
                _ = _firstRememberedHere.Add(plan.Key);
            }

            return from;
        }
    }

    /// <summary>What was last built for <paramref name="key"/>, if anything has been.</summary>
    public RememberedController? Recall(string key) {
        lock (_sync) {
            return _remembered.TryGetValue(key, out RememberedController? controller) ? controller : null;
        }
    }

    /// <summary>Every remembered controller, in index order.</summary>
    public IReadOnlyList<KeyValuePair<string, RememberedController>> Remembered() {
        lock (_sync) {
            var entries = new List<KeyValuePair<string, RememberedController>>(_remembered);
            entries.Sort((a, b) => a.Value.Index.CompareTo(b.Value.Index));
            return entries;
        }
    }

    /// <summary>
    /// The index for the controller at <paramref name="key"/>: the one it was first built at, so its
    /// sensor ids never move within a process, or else the lowest index neither remembered for
    /// another controller nor already in <paramref name="taken"/>. The first scan of a process
    /// therefore numbers controllers in scan order, exactly as before any were remembered. The
    /// chosen index is added to <paramref name="taken"/>.
    /// </summary>
    public int IndexFor(string key, ISet<int> taken) {
        if (key is null) {
            throw new ArgumentNullException(nameof(key));
        }

        if (taken is null) {
            throw new ArgumentNullException(nameof(taken));
        }

        lock (_sync) {
            if (_remembered.TryGetValue(key, out RememberedController? known) && taken.Add(known.Index)) {
                return known.Index;
            }

            if (_building.TryGetValue(key, out int running) && !_unnumberedBuilds.Contains(key) && taken.Add(running)) {
                return running;
            }

            var reserved = new HashSet<int>();
            foreach (KeyValuePair<string, RememberedController> entry in _remembered) {
                if (!string.Equals(entry.Key, key, StringComparison.Ordinal)) {
                    _ = reserved.Add(entry.Value.Index);
                }
            }

            foreach (KeyValuePair<string, int> entry in _building) {
                if (!string.Equals(entry.Key, key, StringComparison.Ordinal) && !_unnumberedBuilds.Contains(entry.Key)) {
                    _ = reserved.Add(entry.Value);
                }
            }

            int index = 0;
            while (taken.Contains(index) || reserved.Contains(index)) {
                index++;
            }

            _ = taken.Add(index);
            return index;
        }
    }

    /// <summary>
    /// <see cref="TryBeginBuild"/>, but only while the numbering is still <paramref name="epoch"/>:
    /// a rebuild from an instance whose scan numbered its controllers before the saved ones were read
    /// would claim the device at a guessed index, which the file may give another controller.
    /// Checked and claimed under one lock.
    /// </summary>
    public bool TryBeginBuildUnder(int epoch, string key, int index) {
        lock (_sync) {
            return epoch == _numberingEpoch && TryBeginBuild(key, index);
        }
    }

    /// <summary>
    /// Start a build of the controller at <paramref name="key"/> at <paramref name="index"/>: false,
    /// starting nothing, when one is already running, whoever started it. The index is held, and no
    /// other build of the device starts, until <see cref="EndBuild"/>.
    /// </summary>
    public bool TryBeginBuild(string key, int index) {
        if (key is null) {
            throw new ArgumentNullException(nameof(key));
        }

        lock (_sync) {
            if (_building.ContainsKey(key)) {
                return false;
            }

            _building[key] = index;
            return true;
        }
    }

    /// <summary>The build of the controller at <paramref name="key"/> finished, however it ended.</summary>
    public void EndBuild(string key) {
        lock (_sync) {
            _ = _building.Remove(key);
            _ = _unnumberedBuilds.Remove(key);
        }
    }

    /// <summary>
    /// Ask for a FanControl refresh, because the devices changed in a way only a refresh can show:
    /// a new one appeared, or one finished opening after the scan had moved on. Safe from any
    /// thread; the request is raised by the owning instance on FanControl's own thread.
    /// </summary>
    public void RequestRefresh(string reason, ILog log) {
        if (reason is null) {
            throw new ArgumentNullException(nameof(reason));
        }

        if (log is null) {
            throw new ArgumentNullException(nameof(log));
        }

        bool first;
        lock (_sync) {
            first = _refreshReason is null;
            _refreshReason ??= reason;
        }

        if (first) {
            log.Write("refresh wanted: " + reason);
        }
    }

    /// <summary>
    /// Take a pending refresh request on behalf of <paramref name="owner"/>: true, with the reason,
    /// when one is pending, the owner is the running instance, and the last request was long enough
    /// ago. A request made while another instance owns the runtime waits for that instance.
    /// </summary>
    public bool TryTakeRefresh(object owner, out string reason) {
        lock (_sync) {
            DateTime now = _clock.UtcNow;
            if (_refreshReason is null
                || !ReferenceEquals(_owner, owner)
                || (_lastRefresh.HasValue && ClockSpan.Since(now, _lastRefresh.Value) < RefreshSpacing)) {
                reason = string.Empty;
                return false;
            }

            reason = _refreshReason;
            _refreshReason = null;
            _lastRefresh = now;
            return true;
        }
    }

    /// <summary>How many controllers the process has built and remembers.</summary>
    public int RememberedCount {
        get {
            lock (_sync) {
                return _remembered.Count;
            }
        }
    }
}
