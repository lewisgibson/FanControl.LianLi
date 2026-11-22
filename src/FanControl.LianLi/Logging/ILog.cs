namespace FanControl.LianLi.Logging;

/// <summary>
/// Minimal logging interface used by the plugin internals. A single implementation
/// is injected everywhere so that tests can capture output with a fake and production
/// can write to the plugin's own file.
/// </summary>
internal interface ILog {
    /// <summary>Write a single line. Implementations must never throw.</summary>
    void Write(string message);
}
