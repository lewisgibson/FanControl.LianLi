using System;
using System.Collections.Generic;
using System.Threading;
using FanControl.LianLi.Transport;

namespace FanControl.LianLi.Tests.Fakes;

/// <summary>
/// Runs bounded calls inline and decides their deadline by rule instead of by clock, so a transport's
/// timeout paths are driven deterministically. <see cref="TimesOut"/> picks the calls that run out
/// their bound before they start: those invoke their cancel and return false with the call itself held
/// back in <see cref="Abandoned"/>, as an abandoned thread would be, for a test to finish late with
/// whichever token it wants to model. <see cref="FinishesAtDeadline"/> picks calls that do complete but
/// only as the bound runs out, the race where a result lands just after the caller gave up.
/// <see cref="AbandonRunning"/>, called from inside a native fake while a call is running, is the
/// deadline passing while that native call is blocked: it cancels the call's token and then invokes its
/// cancel, in <see cref="BoundedDeviceCall"/>'s order, and the call carries on inline to show what it
/// does next. Its own cancellation is dropped, as the real bound drops it; anything else it throws is
/// kept in <see cref="LateFailures"/>. Every call is recorded with its bound. A call reports that it
/// returned as the real bound does: at once for every call that ran inline, and for one held in
/// <see cref="Abandoned"/> only when a test finishes it, so a call a test never finishes is one that
/// never returned.
/// </summary>
internal sealed class FakeDeviceCallRunner : IDeviceCallRunner {
    private readonly List<string> _operations = new List<string>();
    private readonly List<int> _timeouts = new List<int>();
    private readonly Stack<Running> _running = new Stack<Running>();

    public Func<string, bool> TimesOut { get; set; } = _ => false;

    public Func<string, bool> FinishesAtDeadline { get; set; } = _ => false;

    public IReadOnlyList<string> Operations => _operations;

    public IReadOnlyList<int> Timeouts => _timeouts;

    public List<Action<CancellationToken>> Abandoned { get; } = new List<Action<CancellationToken>>();

    public List<Exception> LateFailures { get; } = new List<Exception>();

    /// <summary>A token already cancelled, for finishing an <see cref="Abandoned"/> call as the real bound would.</summary>
    public static CancellationToken Cancelled => new CancellationToken(true);

    public bool TryRun(
        string operation, Action<CancellationToken> call, int timeoutMilliseconds, Action onTimeout, Action onReturned) {
        _operations.Add(operation);
        _timeouts.Add(timeoutMilliseconds);
        if (TimesOut(operation)) {
            onTimeout();
            Abandoned.Add(token => {
                try {
                    call(token);
                } finally {
                    onReturned();
                }
            });
            return false;
        }

        using var source = new CancellationTokenSource();
        var running = new Running(source, onTimeout);
        _running.Push(running);
        try {
            call(source.Token);
        } catch (OperationCanceledException canceled) when (running.Abandoned && canceled.CancellationToken == source.Token) {
            return false;
        } catch (Exception failure) when (running.Abandoned) {
            LateFailures.Add(failure);
            return false;
        } finally {
            _running.Pop();
            onReturned();
        }

        if (running.Abandoned) {
            return false;
        }

        if (FinishesAtDeadline(operation)) {
            source.Cancel();
            onTimeout();
            return false;
        }

        return true;
    }

    /// <summary>The deadline passes while the innermost running call is blocked in a native call.</summary>
    public void AbandonRunning() {
        Running running = _running.Peek();
        running.Abandoned = true;
        running.Source.Cancel();
        running.OnTimeout();
    }

    /// <summary>The bound the most recent call whose operation starts with <paramref name="prefix"/> was given.</summary>
    public int TimeoutOf(string prefix) {
        for (int i = _operations.Count - 1; i >= 0; i--) {
            if (_operations[i].StartsWith(prefix, StringComparison.Ordinal)) {
                return _timeouts[i];
            }
        }

        throw new InvalidOperationException("No bounded call named " + prefix);
    }

    private sealed class Running {
        public Running(CancellationTokenSource source, Action onTimeout) {
            Source = source;
            OnTimeout = onTimeout;
        }

        public CancellationTokenSource Source { get; }

        public Action OnTimeout { get; }

        public bool Abandoned { get; set; }
    }
}
