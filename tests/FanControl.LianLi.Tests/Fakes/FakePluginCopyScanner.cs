using System.Collections.Generic;
using FanControl.LianLi.Plugin;

namespace FanControl.LianLi.Tests.Fakes;

// Reports whatever copies a test describes, without loading a second plugin assembly.
internal sealed class FakePluginCopyScanner : IPluginCopyScanner {
    public FakePluginCopyScanner(PluginCopy own) {
        Own = own;
    }

    public PluginCopy Own { get; set; }

    public List<PluginCopy> Copies { get; } = new List<PluginCopy>();

    /// <summary>When set, the scan throws it, as a scan that hit something it could not read would.</summary>
    public System.Exception? Failure { get; set; }

    public IReadOnlyList<PluginCopy> Others() {
        if (Failure != null) {
            throw Failure;
        }

        return Copies.ToArray();
    }
}
