using System.Collections.Generic;
using FanControl.LianLi.Logging;

namespace FanControl.LianLi.Tests.Fakes;

/// <summary>
/// Collects log lines. The worker thread writes to this while the test thread reads it - a plugin
/// under test has a live keepalive loop behind it - so writes are locked and <see cref="Messages"/>
/// hands back a snapshot rather than the live list.
/// </summary>
internal sealed class FakeLogger : ILog {
    private readonly object _gate = new object();
    private readonly List<string> _messages = new List<string>();

    /// <summary>The lines written so far, as a snapshot safe to enumerate.</summary>
    public IReadOnlyList<string> Messages {
        get {
            lock (_gate) {
                return _messages.ToArray();
            }
        }
    }

    public void Write(string message) {
        lock (_gate) {
            _messages.Add(message);
        }
    }
}
