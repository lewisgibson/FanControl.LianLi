using System;
using System.Globalization;
using System.IO;

namespace FanControl.LianLi.Logging;

/// <summary>
/// Crash-safe file logger. Writes timestamped lines to
/// <c>%LOCALAPPDATA%\FanControl.LianLi\plugin.log</c>, falling back to the
/// system temp directory if that location cannot be created. Every operation
/// is guarded so that logging can never throw into the caller. The file is kept to
/// a bounded size: once it passes the limit it becomes <c>plugin.log.1</c>
/// (replacing the one before) and a fresh file starts, so a machine that runs for
/// months holds at most two files' worth.
/// </summary>
internal sealed class FileLogger : ILog {
    // Several of these can write the same file at once: FanControl creates a new plugin object
    // (and so a new logger) on every refresh, while the last one's threads may still be logging.
    // One process-wide gate keeps their lines whole and the rotation single.
    private static readonly object Gate = new object();

    // Big enough to hold weeks of an ordinary run, and every line around a fault; small enough to
    // attach to an issue.
    private const long DefaultMaximumBytes = 4 * 1024 * 1024;

    private readonly string _filePath;
    private readonly long _maximumBytes;

    // Under Gate: whether the last rotation failed, so the failure is noted once, not on every line.
    private bool _rotationFailed;

    public FileLogger()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanControl.LianLi")) {
    }

    /// <summary>Log to <c>plugin.log</c> in <paramref name="directory"/>, or in TEMP if it cannot be created.</summary>
    internal FileLogger(string directory)
        : this(directory, DefaultMaximumBytes) {
    }

    /// <summary>As <see cref="FileLogger(string)"/>, rotating once the file passes <paramref name="maximumBytes"/>.</summary>
    internal FileLogger(string directory, long maximumBytes) {
        if (maximumBytes <= 0) {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        _maximumBytes = maximumBytes;
        string dir = directory;
        try {
            Directory.CreateDirectory(dir);
        }
#pragma warning disable CA1031 // logging must never throw: any failure falls back to TEMP
        catch (Exception) {
            dir = Path.GetTempPath();
        }
#pragma warning restore CA1031
        _filePath = Path.Combine(dir, "plugin.log");
    }

    /// <summary>The resolved path the logger appends to (for diagnostics/tests).</summary>
    public string FilePath => _filePath;

    /// <summary>Where the previous file goes when the current one is rotated.</summary>
    public string PreviousFilePath => _filePath + ".1";

    public void Write(string message) {
        try {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                + "  " + message + Environment.NewLine;
            lock (Gate) {
                // A rotation that fails (the old file held open without sharing, say) must not cost
                // the line: it is written to the current file, which keeps growing until a later
                // write's rotation succeeds, and the failure is noted in it once.
                try {
                    RotateIfFull();
                    _rotationFailed = false;
                } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                    if (!_rotationFailed) {
                        _rotationFailed = true;
                        line = line + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                            + "  log rotation failed, this file keeps growing until it succeeds: " + ex.Message + Environment.NewLine;
                    }
                }

                File.AppendAllText(_filePath, line);
            }
        }
#pragma warning disable CA1031 // logging must never throw
        catch (Exception) {
            // Intentionally swallowed: a logging failure must never disrupt fan control.
        }
#pragma warning restore CA1031
    }

    // Caller holds Gate. Throws when the old file cannot be moved aside; Write keeps the line.
    private void RotateIfFull() {
        var file = new FileInfo(_filePath);
        if (!file.Exists || file.Length < _maximumBytes) {
            return;
        }

        if (File.Exists(PreviousFilePath)) {
            File.Delete(PreviousFilePath);
        }

        File.Move(_filePath, PreviousFilePath);
    }
}
