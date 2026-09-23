using System;
using System.Linq;
using System.Threading;
using FanControl.LianLi.Logging;
using FanControl.LianLi.Tests.Fakes;
using FanControl.Plugins;
using Xunit;

namespace FanControl.LianLi.Tests.Logging;

/// <summary>The adapter onto FanControl's logger: every line handed on in order, and the host never waited on.</summary>
public sealed class PluginLoggerLogTests {
    [Fact]
    public void EveryLine_ReachesTheHostLogger_InOrder() {
        var host = new FakePluginLogger();
        var log = new PluginLoggerLog(host, new FakeLogger());

        for (int i = 0; i < 50; i++) {
            log.Write("m" + i);
        }

        Assert.True(SpinWait.SpinUntil(() => host.Messages.Count == 50, TimeSpan.FromSeconds(5)));
        Assert.Equal(Enumerable.Range(0, 50).Select(i => "m" + i), host.Messages);
    }

    [Fact]
    public void AHostLoggerThatStopsReturning_NeverHoldsAWriter_AndTheLinesItMissedAreCounted() {
        using var release = new ManualResetEventSlim(false);
        var host = new FakePluginLogger { HoldUntil = release };
        var log = new PluginLoggerLog(host, new FakeLogger());
        log.Write("m0");
        Assert.True(host.Entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        // 1000 lines wait; the 99 after them are dropped. None of the writes waits on the host.
        var writer = new Thread(() => {
            for (int i = 1; i < 1100; i++) {
                log.Write("m" + i);
            }
        });
        writer.Start();
        Assert.True(writer.Join(TimeSpan.FromSeconds(5)));
        release.Set();

        Assert.True(SpinWait.SpinUntil(() => host.Messages.Count == 1002, TimeSpan.FromSeconds(10)));
        Assert.Equal("m0", host.Messages[0]);
        Assert.Equal("99 line(s) were not passed to FanControl's log while it was not taking them; the plugin's own log has them", host.Messages[1]);
        Assert.Equal("m1", host.Messages[2]);
        Assert.Equal("m1000", host.Messages[1001]);
    }

    [Fact]
    public void AThrowingHostLogger_IsToldToThePluginsOwnLogOnce_AndTheLinesAfterItStillGo() {
        var host = new ThrowingLogger(failures: 2);
        var file = new FakeLogger();
        var log = new PluginLoggerLog(host, file);

        log.Write("first");
        log.Write("second");
        log.Write("third");
        Assert.True(SpinWait.SpinUntil(() => host.Calls == 3, TimeSpan.FromSeconds(5)));
        host.Failures = 1;
        log.Write("fourth");

        // The failure line is written after the host call returns, so wait for the line itself.
        const string Refused = "FanControl's log refused a line (InvalidOperationException: host logger failed); the plugin's own log keeps every line";
        Assert.True(SpinWait.SpinUntil(() => file.Messages.Count(m => m == Refused) == 2, TimeSpan.FromSeconds(5)));
        Assert.True(SpinWait.SpinUntil(() => host.Calls == 4, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void RejectsAMissingFailureLog()
        => Assert.Throws<ArgumentNullException>(() => new PluginLoggerLog(new FakePluginLogger(), null!));

    [Fact]
    public void WithoutAHostLogger_WritesNowhere()
        => new PluginLoggerLog(null, new FakeLogger()).Write("dropped");

    // Throws for the next Failures calls, then takes lines.
    private sealed class ThrowingLogger : IPluginLogger {
        private int _calls;
        private int _failures;

        public ThrowingLogger(int failures) => _failures = failures;

        public int Calls => Volatile.Read(ref _calls);

        public int Failures {
            set => Volatile.Write(ref _failures, value);
        }

        // The call is counted after the decision to throw, so a test that sees the count also sees
        // whether that call fails.
        public void Log(string message) {
            bool fail = Interlocked.Decrement(ref _failures) >= 0;
            Interlocked.Increment(ref _calls);
            if (fail) {
                throw new InvalidOperationException("host logger failed");
            }
        }
    }
}
