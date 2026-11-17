using System;

namespace FanControl.LianLi.Plugin;

/// <summary>
/// One copy of this plugin loaded into the FanControl process: the file it was loaded from, its
/// assembly version, and whether it is new enough to take part in <see cref="PluginCopyGuard"/>.
/// FanControl loads every <c>FanControl*.dll</c> in its Plugins folder, so two builds of the plugin
/// under different file names are two copies, each driving the same controllers.
/// </summary>
internal sealed class PluginCopy {
    /// <summary>A copy loaded from <paramref name="location"/> (empty when it was not loaded from a file).</summary>
    public PluginCopy(string location, Version version, bool hasGuard) {
        Location = location ?? throw new ArgumentNullException(nameof(location));
        Version = version ?? throw new ArgumentNullException(nameof(version));
        HasGuard = hasGuard;
    }

    /// <summary>The full path of the file the copy was loaded from, or empty when it was loaded from memory.</summary>
    public string Location { get; }

    /// <summary>The copy's assembly version: the release it was built for.</summary>
    public Version Version { get; }

    /// <summary>
    /// Whether the copy has <see cref="PluginCopyGuard"/>. One without it predates the guard: it
    /// drives the controllers whatever else is loaded, and never steps aside.
    /// </summary>
    public bool HasGuard { get; }

    /// <summary>The copy as the log names it: its file and its version.</summary>
    public override string ToString()
        => (Location.Length == 0 ? "a copy loaded from memory" : Location) + " (version " + Version.ToString() + ")";
}
