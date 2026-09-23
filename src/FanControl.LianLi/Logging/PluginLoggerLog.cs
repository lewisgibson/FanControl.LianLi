using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using FanControl.Plugins;

namespace FanControl.LianLi.Logging;

/// <summary>
/// Adapts the host-supplied <see cref="IPluginLogger"/> to the internal <see cref="ILog"/> seam so
/// the rest of the plugin depends only on <see cref="ILog"/>. The host logger is treated as
/// optional: a null logger or a throwing one is swallowed, because logging must never disrupt fan
/// control. It is also never waited on. The plugin logs from several threads at once - FanControl's
/// own among them - and the host's logger makes no promise about concurrent calls, or about how
/// long a call takes (it hands each line to whatever FanControl has listening). So a message is
/// queued and handed on, one at a time and in order, by a thread of the adapter's own that runs only
/// while there is something to hand on. A host logger that stops returning costs only the copies it
/// would have received; the plugin's own file log still has every line.
/// </summary>
internal sealed class PluginLoggerLog : ILog {
    // Far more than the plugin ever has waiting - it logs a few lines a second at most - and small
    // enough that a host logger that stops returning holds only a little memory.
    private const int MaximumQueued = 1000;

    private readonly Queue<string> _queued = new Queue<string>();
    private readonly IPluginLogger? _logger;
    private readonly ILog _failures;
    private bool _handingOn;
    private int _dropped;
    private bool _failing;

    /// <summary>Hand lines on to <paramref name="logger"/>, telling <paramref name="failures"/> when it refuses them.</summary>
    public PluginLoggerLog(IPluginLogger? logger, ILog failures) {
        _logger = logger;
        _failures = failures ?? throw new ArgumentNullException(nameof(failures));
    }

    public void Write(string message) {
        if (_logger is null) {
            return;
        }

        lock (_queued) {
            if (_queued.Count >= MaximumQueued) {
                _dropped++;
                return;
            }

            _queued.Enqueue(message);
            if (_handingOn) {
                return;
            }

            _handingOn = true;
        }

        new Thread(HandOn) { IsBackground = true, Name = "LianLiHostLog" }.Start();
    }

    private void HandOn() {
        while (true) {
            string message;
            int dropped;
            lock (_queued) {
                if (_queued.Count == 0) {
                    _handingOn = false;
                    return;
                }

                message = _queued.Dequeue();
                dropped = _dropped;
                _dropped = 0;
            }

            if (dropped > 0) {
                Log(string.Format(
                    CultureInfo.InvariantCulture, "{0} line(s) were not passed to FanControl's log while it was not taking them; the plugin's own log has them", dropped));
            }

            Log(message);
        }
    }

    // Only this adapter's own thread calls this, so _failing needs no lock.
    private void Log(string message) {
        try {
            _logger!.Log(message); // Write queues nothing without a logger
            _failing = false;
        }
#pragma warning disable CA1031 // logging must never throw: this runs on the adapter's own thread, where an escaping exception would end FanControl's process
        catch (Exception ex) {
            // The host logger must never disrupt fan control. Its failure is told to the plugin's
            // own log, once until it takes a line again, since that log has every line anyway.
            if (!_failing) {
                _failing = true;
                _failures.Write("FanControl's log refused a line (" + ex.GetType().Name + ": " + ex.Message + "); the plugin's own log keeps every line");
            }
        }
#pragma warning restore CA1031
    }
}
