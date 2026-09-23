using System.Collections.Generic;
using System.Threading;
using FanControl.Plugins;

namespace FanControl.LianLi.Tests.Fakes;

/// <summary>FanControl's logger: records every line, and can be made to hold a call, as a hung host would.</summary>
internal sealed class FakePluginLogger : IPluginLogger {
    private readonly List<string> _messages = new List<string>();

    /// <summary>A snapshot of every line logged, in order.</summary>
    public IReadOnlyList<string> Messages {
        get {
            lock (_messages) {
                return _messages.ToArray();
            }
        }
    }

    /// <summary>When set, each call waits for it before returning.</summary>
    public ManualResetEventSlim? HoldUntil { get; set; }

    /// <summary>Set once a call has started.</summary>
    public ManualResetEventSlim Entered { get; } = new ManualResetEventSlim(false);

    public void Log(string message) {
        Entered.Set();
        HoldUntil?.Wait();
        lock (_messages) {
            _messages.Add(message);
        }
    }
}
