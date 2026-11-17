using System.Collections.Generic;

namespace FanControl.LianLi.Plugin;

/// <summary>
/// Finds the copies of this plugin loaded into the process, so a copy can tell whether another
/// one is driving the same controllers. The seam lets a test describe any mix of copies without
/// loading a second plugin assembly into the test process.
/// </summary>
internal interface IPluginCopyScanner {
    /// <summary>This copy: the assembly the code asking is running from.</summary>
    PluginCopy Own { get; }

    /// <summary>Every other copy loaded into the process, each once, never including this copy's own file.</summary>
    IReadOnlyList<PluginCopy> Others();
}
