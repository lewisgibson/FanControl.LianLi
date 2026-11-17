using System;
using System.Collections.Generic;
using System.Reflection;

namespace FanControl.LianLi.Plugin;

/// <summary>
/// Finds the other copies of this plugin among the assemblies loaded into the process. A copy is
/// any assembly that defines the plugin type, whatever its file is called: FanControl loads each
/// plugin DLL with its own assembly name, so a renamed build is a separate assembly with the same
/// types. Which copies have the guard is read from whether they define it.
/// </summary>
internal sealed class LoadedPluginCopyScanner : IPluginCopyScanner {
    // The type that makes an assembly a copy of this plugin, and the type only a copy with the guard
    // defines. Neither name may change: a copy built before a rename would no longer be recognised,
    // and the second alone tells an older copy, which never steps aside, from one that does.
    internal const string PluginTypeName = "FanControl.LianLi.Plugin.LianLiPlugin";
    internal const string GuardTypeName = "FanControl.LianLi.Plugin.PluginCopyGuard";

    private readonly Assembly _own;
    private readonly Func<IEnumerable<Assembly>> _loaded;

    /// <summary>Scan what <paramref name="loaded"/> returns for copies other than <paramref name="own"/>.</summary>
    public LoadedPluginCopyScanner(Assembly own, Func<IEnumerable<Assembly>> loaded) {
        _own = own ?? throw new ArgumentNullException(nameof(own));
        _loaded = loaded ?? throw new ArgumentNullException(nameof(loaded));
    }

    /// <summary>The scanner over this process's loaded assemblies, from the copy this code is part of.</summary>
    public static LoadedPluginCopyScanner Process
        => new LoadedPluginCopyScanner(typeof(LianLiPlugin).Assembly, () => AppDomain.CurrentDomain.GetAssemblies());

    /// <inheritdoc />
    public PluginCopy Own => Describe(_own);

    /// <inheritdoc />
    public IReadOnlyList<PluginCopy> Others() {
        string own = LocationOf(_own);
        var copies = new List<PluginCopy>();
        foreach (Assembly assembly in _loaded()) {
            if (ReferenceEquals(assembly, _own) || assembly.GetType(PluginTypeName, throwOnError: false) is null) {
                continue;
            }

            // The same file loaded a second time is still this copy, and this copy's own file is
            // never another copy's to remove.
            PluginCopy copy = Describe(assembly);
            if (string.Equals(copy.Location, own, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            copies.Add(copy);
        }

        return copies;
    }

    // An assembly's definition always carries a version, so GetName().Version is never null for a
    // loaded assembly; the annotation allows null only for a hand-built AssemblyName.
    private static PluginCopy Describe(Assembly assembly)
        => new PluginCopy(LocationOf(assembly), assembly.GetName().Version!, assembly.GetType(GuardTypeName, throwOnError: false) != null);

    // An assembly built in memory has no file; .NET Framework throws for its Location rather than
    // returning an empty one.
    private static string LocationOf(Assembly assembly)
        => assembly.IsDynamic ? string.Empty : assembly.Location;
}
