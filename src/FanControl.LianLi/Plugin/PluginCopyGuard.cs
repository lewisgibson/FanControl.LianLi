using System;
using System.Collections.Generic;
using System.IO;
using FanControl.LianLi.Logging;

namespace FanControl.LianLi.Plugin;

/// <summary>
/// Keeps one copy of the plugin driving the controllers when FanControl has loaded two. FanControl
/// loads every <c>FanControl*.dll</c> in its Plugins folder, so a build left there by hand under an
/// older file name runs beside the one installed with the Install plugin button, and the two would
/// fight over every fan. The copy with the highest version wins (on a tie, the file installed last);
/// it renames each other copy's file out of FanControl's scan, and a later start deletes what it
/// renamed. A copy defers only to a copy that is actually driving: one holding the claim, or one
/// that predates this guard and that FanControl still creates, which it does while the file it was
/// loaded from is still in the Plugins folder under that name. Once that file is renamed, the next
/// refresh no longer creates the older copy and the winner takes over, though its assembly stays
/// loaded. A loser whose winner never starts (disabled in FanControl's settings, say) drives the
/// fans itself. Held process-wide, like <see cref="PluginRuntime"/>, because FanControl creates a
/// new plugin object on every refresh.
/// </summary>
internal sealed class PluginCopyGuard {
    // Added to a removed copy's file name. FanControl only loads files ending in .dll, and Windows
    // lets a loaded DLL be renamed but not deleted, so a rename takes it out of the next start's
    // scan while this process still has it mapped.
    internal const string RemovedSuffix = ".removed";

    // FanControl's own scan: files whose names start with this, at any depth under Plugins.
    private const string PluginFilePrefix = "FanControl";
    private const string PluginsDirectoryName = "Plugins";

    // The assembly names this plugin has shipped under: the standard build's, and the variants'
    // named after it with a dot (older releases) or a hyphen.
    private const string AssemblyName = "FanControl.LianLi";

    private readonly object _sync = new object();
    private readonly IPluginCopyScanner _scanner;
    private readonly IPluginFileSystem _files;
    private readonly IPluginCopyClaim _claim;

    // The other copies' files already tried this process: each is tried, and logged, once, not on
    // every refresh.
    private readonly HashSet<string> _handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // The files of copies without the guard that an activation has already found gone, or renamed
    // itself (see IsRunning).
    private readonly HashSet<string> _seenGone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private bool _leftoversChecked;

    // The plugin instance this copy's claim is held for, or null when this copy holds none.
    private object? _owner;

    // The last decision logged: why this copy is idle, or why it drives while a newer copy is
    // loaded; null when it drives with nothing to say. Each is logged once while it stands, rather
    // than on every refresh, and going back to driving plainly is logged too.
    private string? _reported;

    // This copy's file, while it drives the fans with another copy loaded: a winning copy that
    // started after it renames that file away, and from then on the host's next refresh no longer
    // creates this copy, so it asks for that refresh at once (OwnFileRemoved). Null otherwise.
    private string? _watchedFile;

    // Set when this copy took the claim beside another copy with its own file already gone: a
    // winner scanned before it renamed it first, so there was nothing to watch. One refresh hands
    // the winner the fans; asked for at most once per process, since a file gone for another reason
    // (the same build loaded from a second path) would otherwise ask on every pass.
    private bool _refreshForGoneFile;
    private bool _askedForGoneFile;

    // The winners this copy has already driven beside. Beside one a second time, the winner has had
    // its chance to remove this copy and has not: it is disabled, or the rename was refused.
    private readonly HashSet<string> _drivenBeside = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>A guard finding copies with <paramref name="scanner"/>, removing them through <paramref name="files"/>, and holding <paramref name="claim"/> while active.</summary>
    public PluginCopyGuard(IPluginCopyScanner scanner, IPluginFileSystem files, IPluginCopyClaim claim) {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _claim = claim ?? throw new ArgumentNullException(nameof(claim));
    }

    /// <summary>The guard for the copy this code is part of, over this process's loaded assemblies and the real file system.</summary>
    public static PluginCopyGuard Process { get; } = ForCurrentProcess();

    /// <summary>
    /// Decide whether <paramref name="owner"/>, a plugin instance being initialised, drives the
    /// controllers. True when it does, holding the claim until <see cref="Release"/>; false when it
    /// must stay idle, which is logged (once while the reason stands) with which copy is active and
    /// what becomes of the other.
    /// </summary>
    public bool TryActivate(object owner, ILog log) {
        if (owner is null) {
            throw new ArgumentNullException(nameof(owner));
        }

        if (log is null) {
            throw new ArgumentNullException(nameof(log));
        }

        lock (_sync) {
            PluginCopy own = _scanner.Own;
            IReadOnlyList<PluginCopy> others = _scanner.Others();
            string? plugins = PluginsDirectory(own.Location);
            RemoveLeftovers(plugins, own, others, log);

            // Which copies without the guard may be driving the fans this pass, read before anything
            // is renamed (see IsRunning).
            var running = new List<PluginCopy>();
            foreach (PluginCopy other in others) {
                if (!other.HasGuard && IsRunning(other)) {
                    running.Add(other);
                }
            }

            PluginCopy winner = Winner(own, others);
            bool ownWins = ReferenceEquals(winner, own);
            if (ownWins) {
                foreach (PluginCopy other in others) {
                    Remove(other, own, plugins, log);
                }
            }

            string loser = "this copy of the plugin (" + own.ToString() + ") stays idle: the copy at " + winner.ToString() + " is newer and takes over the fans. "
                + (winner.HasGuard
                    ? "That copy removes this one from FanControl's Plugins folder"
                    : "Close FanControl and delete " + (own.Location.Length == 0 ? "this copy" : own.Location) + " from its Plugins folder");
            if (running.Count > 0) {
                // An old copy whose file is still there after the renames could not be removed: it
                // keeps driving until the user deletes it.
                PluginCopy? stuck = StillThere(running);
                return Idle(
                    !ownWins
                        ? loser
                        : stuck != null
                            ? "this copy of the plugin (" + own.ToString() + ") is the newest but stays idle: the older copy at " + stuck.ToString()
                                + " is still driving the fans and could not be removed. Close FanControl, delete that file from its Plugins folder, then start FanControl again"
                            : "this copy of the plugin (" + own.ToString() + ") is the newest but stays idle for now: the older copy at " + running[0].ToString()
                                + " may still be driving the fans in this FanControl process, and two copies would fight over them. Its file is no longer in the Plugins folder, "
                                + "so this copy takes over at FanControl's next refresh, or straight away if FanControl is restarted",
                    log);
            }

            if (Claim(owner)) {
                Watch(own, others.Count > 0);
                return Active(
                    own,
                    ownWins
                        ? null
                        : _drivenBeside.Add(winner.Location)
                            ? "this copy of the plugin (" + own.ToString() + ") drives the fans for now: the newer copy at " + winner.ToString()
                                + " is loaded but has not started. Once it starts it removes this copy and FanControl refreshes to hand it the fans"
                            : "this copy of the plugin (" + own.ToString() + ") drives the fans: the newer copy at " + winner.ToString()
                                + " has not taken over, because it is disabled in FanControl's plugin settings or could not remove this copy. "
                                + "Close FanControl and delete whichever of the two files you do not want from its Plugins folder",
                    log);
            }

            // Another copy holds the claim. One whose file is still there after the renames could
            // not be removed, and starts first on every refresh, so it keeps the fans.
            PluginCopy? driving = ownWins ? StillThere(others) : null;

            return Idle(
                others.Count == 0
                    ? "this copy of the plugin (" + own.ToString() + ") stays idle: another copy in this FanControl process is already driving the fans. "
                        + "Keep only one Lian Li plugin DLL in FanControl's Plugins folder"
                    : driving != null
                        ? "this copy of the plugin (" + own.ToString() + ") is the newest but stays idle: the copy at " + driving.ToString()
                            + " is driving the fans and could not be removed. Close FanControl, delete that file from its Plugins folder, then start FanControl again"
                        : ownWins
                            ? "this copy of the plugin (" + own.ToString() + ") is the newest but stays idle for now: another copy is still driving the fans in this FanControl process. "
                                + "It takes over at FanControl's next refresh, or straight away if FanControl is restarted"
                            : loser,
                log);
        }
    }

    /// <summary>
    /// Whether this copy, driving the fans beside another loaded copy, has had its own file renamed
    /// away by that copy: since it started driving, or before it took the claim (asked once per
    /// process). True once, so the caller asks FanControl for the refresh that hands the fans to the
    /// winner (FanControl's next refresh no longer creates this copy); false otherwise, and always
    /// false when no other copy was loaded.
    /// </summary>
    public bool OwnFileRemoved() {
        lock (_sync) {
            if (_refreshForGoneFile) {
                _refreshForGoneFile = false;
                return true;
            }

            if (_watchedFile is null || _files.Exists(_watchedFile)) {
                return false;
            }

            _watchedFile = null;
            return true;
        }
    }

    /// <summary>Give up the claim, if <paramref name="owner"/> is the instance it is held for. Safe to call more than once.</summary>
    public void Release(object owner) {
        lock (_sync) {
            if (ReferenceEquals(_owner, owner)) {
                Unclaim();
            }
        }
    }

    // The claim lives as long as the process, as this guard does: it holds a handle only while this
    // copy is active, and Release (from the plugin's Close) closes it, so nothing disposes it.
    private static PluginCopyGuard ForCurrentProcess() {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
#pragma warning disable CA2000 // Owned by the process-wide guard for the life of the process; Release closes its handle.
        return new PluginCopyGuard(LoadedPluginCopyScanner.Process, new PluginFileSystem(), new PluginCopyClaim(PluginCopyClaim.NameFor(process.Id)));
#pragma warning restore CA2000
    }

    // Another instance of this copy taking over from the one that holds the claim keeps it: it is
    // the same copy, and FanControl may initialise the new instance before it closes the old.
    private bool Claim(object owner) {
        if (_owner is null && !_claim.TryTake()) {
            return false;
        }

        _owner = owner;
        return true;
    }

    private void Unclaim() {
        if (_owner is null) {
            return;
        }

        _claim.Release();
        _owner = null;
    }

    private bool Active(PluginCopy own, string? note, ILog log) {
        if (!string.Equals(note, _reported, StringComparison.Ordinal)) {
            log.Write(note ?? "this copy of the plugin (" + own.ToString() + ") now drives the fans");
            _reported = note;
        }

        return true;
    }

    private bool Idle(string reason, ILog log) {
        Unclaim();
        _watchedFile = null;
        _refreshForGoneFile = false;
        if (!string.Equals(reason, _reported, StringComparison.Ordinal)) {
            log.Write(reason);
            _reported = reason;
        }

        return false;
    }

    // The highest version wins; on a tie, the file installed last, which is the one the user just
    // installed; on a tie of both, the path, so every copy picks the same winner.
    private PluginCopy Winner(PluginCopy own, IReadOnlyList<PluginCopy> others) {
        PluginCopy winner = own;
        DateTime winnerInstalled = InstalledAt(own);
        foreach (PluginCopy other in others) {
            DateTime installed = InstalledAt(other);
            int byVersion = other.Version.CompareTo(winner.Version);
            int byInstalled = installed.CompareTo(winnerInstalled);
            if (byVersion > 0 || (byVersion == 0 && (byInstalled > 0
                || (byInstalled == 0 && string.Compare(other.Location, winner.Location, StringComparison.OrdinalIgnoreCase) > 0)))) {
                winner = other;
                winnerInstalled = installed;
            }
        }

        return winner;
    }

    // The first of copies whose file is still there (or that was never loaded from one): one that
    // a rename did not take out of FanControl's scan.
    private PluginCopy? StillThere(IReadOnlyList<PluginCopy> copies) {
        foreach (PluginCopy copy in copies) {
            if (copy.Location.Length == 0 || _files.Exists(copy.Location)) {
                return copy;
            }
        }

        return null;
    }

    // Watch this copy's file while it drives beside another copy (see OwnFileRemoved). One already
    // gone at the claim was renamed by a winner scanned first, so a refresh is wanted straight away.
    private void Watch(PluginCopy own, bool beside) {
        _watchedFile = null;
        if (!beside || own.Location.Length == 0) {
            return;
        }

        if (_files.Exists(own.Location)) {
            _watchedFile = own.Location;
        } else if (!_askedForGoneFile) {
            _askedForGoneFile = true;
            _refreshForGoneFile = true;
        }
    }

    // Whether a copy without the guard may be driving the fans this pass. FanControl creates one only
    // while its file is there, and it then drives until the next refresh. So it is running while its
    // file exists, and also at the first activation that finds the file gone, since another copy may
    // have renamed it earlier in this same pass, after FanControl created it. From the next
    // activation on it is stopped. Renaming it counts as finding it gone: the pass that renamed it
    // is the one that created it. A copy loaded from memory never goes away.
    private bool IsRunning(PluginCopy other)
        => other.Location.Length == 0 || _files.Exists(other.Location) || _seenGone.Add(other.Location);

    private DateTime InstalledAt(PluginCopy copy)
        => copy.Location.Length == 0 ? DateTime.MinValue : _files.InstalledUtc(copy.Location);

    // Take another copy out of FanControl's scan by renaming its file, but only a file that is
    // provably this plugin's: a loaded copy (the scanner found the plugin type in it) whose file is
    // a FanControl*.dll under the Plugins folder this copy was loaded from. A file already gone (an
    // earlier rename, by this copy or another) needs nothing.
    private void Remove(PluginCopy other, PluginCopy own, string? plugins, ILog log) {
        if (!_handled.Add(other.Location) || (other.Location.Length > 0 && !_files.Exists(other.Location))) {
            return;
        }

        string? refusal = Refusal(other, own, plugins);
        if (refusal != null) {
            log.Write("the other copy of the plugin (" + other.ToString() + ") was left in place: " + refusal
                + ". Close FanControl and delete it from FanControl's Plugins folder");
            return;
        }

        string renamed = other.Location + RemovedSuffix;
        try {
            _files.Move(other.Location, renamed);
            _seenGone.Add(other.Location);
            log.Write("removed the other copy of the plugin (" + other.ToString() + ") from FanControl's Plugins folder: it is renamed to "
                + renamed + ", which FanControl no longer loads, and deleted at the next start");
        } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
            // A FanControl app running as the user cannot write under Program Files; the service,
            // running as SYSTEM, can. Either way the fans are unaffected: this copy stays as it is.
            log.Write("could not remove the other copy of the plugin (" + other.ToString() + "): " + ex.Message
                + ". Close FanControl and delete it from FanControl's Plugins folder");
        }
    }

    private static string? Refusal(PluginCopy other, PluginCopy own, string? plugins) {
        if (plugins is null) {
            return "this copy was not loaded from a FanControl Plugins folder";
        }

        if (other.Location.Length == 0) {
            return "it was not loaded from a file";
        }

        if (string.Equals(other.Location, own.Location, StringComparison.OrdinalIgnoreCase)) {
            return "it is this copy's own file";
        }

        if (!IsInside(plugins, other.Location)) {
            return "it is not in FanControl's Plugins folder";
        }

        string name = Path.GetFileName(other.Location);
        return name.StartsWith(PluginFilePrefix, StringComparison.Ordinal) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? null
            : "its file is not named like a FanControl plugin";
    }

    // Delete the files earlier starts renamed, once per process. One that is still mapped by this
    // process - renamed this run, from a copy still loaded - is left for the next start.
    private void RemoveLeftovers(string? plugins, PluginCopy own, IReadOnlyList<PluginCopy> others, ILog log) {
        if (_leftoversChecked || plugins is null) {
            return;
        }

        _leftoversChecked = true;
        IReadOnlyList<string> found;
        try {
            found = _files.Find(plugins, PluginFilePrefix + "*.dll" + RemovedSuffix);
        } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
            log.Write("could not look for removed copies of the plugin in " + plugins + ": " + ex.Message);
            return;
        }

        foreach (string path in found) {
            string original = path.Substring(0, path.Length - RemovedSuffix.Length);
            if (IsLoaded(original, own, others)) {
                continue;
            }

            try {
                if (!IsPluginAssembly(_files.AssemblyNameOf(path))) {
                    log.Write("left " + path + " in place: it is not a copy of this plugin");
                    continue;
                }

                _files.Delete(path);
                log.Write("deleted " + path + ", a copy of the plugin removed at an earlier start");
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is BadImageFormatException) {
                log.Write("could not delete " + path + ", a removed copy of the plugin: " + ex.Message
                    + ". FanControl no longer loads it, so it can be deleted by hand");
            }
        }
    }

    private static bool IsLoaded(string path, PluginCopy own, IReadOnlyList<PluginCopy> others) {
        if (string.Equals(path, own.Location, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        foreach (PluginCopy other in others) {
            if (string.Equals(path, other.Location, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }

        return false;
    }

    private static bool IsPluginAssembly(string name)
        => string.Equals(name, AssemblyName, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(AssemblyName + ".", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(AssemblyName + "-", StringComparison.OrdinalIgnoreCase);

    // FanControl's Plugins folder, found from where this copy was loaded: the Install plugin
    // button puts a plugin in a folder of its own under Plugins, and a hand-copied one sits in
    // Plugins itself. Null when this copy is anywhere else (a test, a harness), where no file is touched.
    private static string? PluginsDirectory(string location) {
        if (location.Length == 0) {
            return null;
        }

        string? directory = Path.GetDirectoryName(location);
        while (directory != null) {
            if (string.Equals(Path.GetFileName(directory), PluginsDirectoryName, StringComparison.OrdinalIgnoreCase)) {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    private static bool IsInside(string directory, string path)
        => Path.GetFullPath(path).StartsWith(
            directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}
