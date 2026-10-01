namespace FanControl.LianLi.Logging;

/// <summary>
/// Minimal logging interface used by the plugin internals. A single implementation
/// (or a <see cref="CompositeLog"/> fan-out) is injected everywhere so that
/// tests can capture output with a fake and production can write to both the
/// host logger and a local file.
/// </summary>
internal interface ILog {
    /// <summary>Write a single line. Implementations must never throw.</summary>
    void Write(string message);
}
