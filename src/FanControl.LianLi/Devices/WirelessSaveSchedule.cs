using System;

namespace FanControl.LianLi.Devices;

/// <summary>
/// When L-Connect tells every wireless device to commit its binding and effect to flash (RF command
/// 0x15). Two independent rules decide it, both reproduced exactly, and both are pure functions of
/// the times handed in:
///
/// <list type="bullet">
/// <item><b>After an effect stream</b> (<c>RFController.SaveConfig</c>): the first request starts a
/// wait; ten seconds later the save goes out if no request has come in the last five seconds,
/// otherwise the wait restarts for another ten. Requests during a wait only move its "last request"
/// mark, so a burst of streams costs one save.</item>
/// <item><b>Periodically</b> (<c>MasterDevice.CheckSaveConfig</c>, checked every second): once an hour
/// after start, then whenever three hours have passed since the last save - but never within thirty
/// seconds of an effect stream.</item>
/// </list>
/// </summary>
internal sealed class WirelessSaveSchedule {
    private static readonly TimeSpan DebounceWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DebounceQuiet = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FirstPeriodicSave = TimeSpan.FromHours(1);
    private static readonly TimeSpan PeriodicSaveInterval = TimeSpan.FromHours(3);
    private static readonly TimeSpan QuietAfterEffect = TimeSpan.FromSeconds(30);

    private DateTime _startUtc;
    private DateTime _lastSaveUtc;
    private DateTime _lastEffectUtc;
    private DateTime _lastRequestUtc;
    private DateTime? _debounceCheckUtc;

    /// <summary>Start both rules at <paramref name="nowUtc"/>, as <c>MasterDevice.Run</c> stamps its start, save and effect times.</summary>
    public WirelessSaveSchedule(DateTime nowUtc) {
        _startUtc = nowUtc;
        _lastSaveUtc = nowUtc;
        _lastEffectUtc = nowUtc;
    }

    /// <summary>
    /// An effect was just streamed to a device: stamp it (<c>last_set_effect_time</c>) and ask for a
    /// debounced save (<c>MasterDevice.SyncRgbData</c> calls <c>RFController.SaveConfig</c>).
    /// </summary>
    public void EffectStreamed(DateTime nowUtc) {
        _lastEffectUtc = nowUtc;
        _lastRequestUtc = nowUtc;
        if (!_debounceCheckUtc.HasValue) {
            _debounceCheckUtc = nowUtc + DebounceWait;
        }
    }

    /// <summary>Whether the debounced save is due now; true once per wait, which it then ends.</summary>
    public bool TakeDebouncedSave(DateTime nowUtc) {
        // A check further off than a whole wait means the clock went back since it was set: it is due.
        if (!_debounceCheckUtc.HasValue
            || (nowUtc < _debounceCheckUtc.Value && _debounceCheckUtc.Value - nowUtc <= DebounceWait)) {
            return false;
        }

        if (ClockSpan.Since(nowUtc, _lastRequestUtc) < DebounceQuiet) {
            _debounceCheckUtc = nowUtc + DebounceWait;
            return false;
        }

        _debounceCheckUtc = null;
        return true;
    }

    /// <summary>Whether the periodic save is due now. Taking it moves the start mark a month on, as L-Connect does.</summary>
    public bool TakePeriodicSave(DateTime nowUtc) {
        // The start mark is deliberately moved into the future once taken (a month on, as L-Connect
        // does), so it is compared plainly; the other marks are only ever in the past.
        if ((nowUtc - _startUtc > FirstPeriodicSave || ClockSpan.Since(nowUtc, _lastSaveUtc) > PeriodicSaveInterval)
            && ClockSpan.Since(nowUtc, _lastEffectUtc) > QuietAfterEffect) {
            _startUtc = nowUtc.AddMonths(1);
            return true;
        }

        return false;
    }

    /// <summary>Whether a stream has asked for a save that has not gone out yet.</summary>
    public bool HasPendingSave => _debounceCheckUtc.HasValue;

    /// <summary>
    /// A save went out at <paramref name="nowUtc"/> (<c>MasterDevice.SaveConfig</c> stamps
    /// <c>last_save_time</c>). It also ends a pending wait: the schedule outlives a controller closed by
    /// FanControl's refresh, which saves on the way out, and its replacement must not save that again.
    /// </summary>
    public void Saved(DateTime nowUtc) {
        _lastSaveUtc = nowUtc;
        _debounceCheckUtc = null;
    }
}
