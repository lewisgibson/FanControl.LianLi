using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using FanControl.LianLi.Plugin;
using Xunit;

namespace FanControl.LianLi.Tests.Plugin;

/// <summary>
/// The copies of the plugin found among loaded assemblies: any assembly defining the plugin type,
/// whatever it is called, other than this one and other than this one's own file loaded again.
/// </summary>
public sealed class LoadedPluginCopyScannerTests : IDisposable {
    private static readonly Assembly Own = typeof(LianLiPlugin).Assembly;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lianli-copies-" + Guid.NewGuid().ToString("N"));
    private readonly AssemblyLoadContext _context = new AssemblyLoadContext("lianli-copies", isCollectible: true);
    private readonly AssemblyLoadContext _again = new AssemblyLoadContext("lianli-same-file", isCollectible: true);

    public void Dispose() {
        _context.Unload();
        _again.Unload();
    }

    // An assembly built in memory that defines the plugin type, and the guard type when asked: an
    // older copy of the plugin, or a newer one.
    private static AssemblyBuilder InMemory(string name, bool withGuard) {
        var builder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(name) { Version = new Version(1, 1, 9, 0) }, AssemblyBuilderAccess.RunAndCollect);
        ModuleBuilder module = builder.DefineDynamicModule(name);
        module.DefineType(LoadedPluginCopyScanner.PluginTypeName, TypeAttributes.Public).CreateType();
        if (withGuard) {
            module.DefineType(LoadedPluginCopyScanner.GuardTypeName, TypeAttributes.Public).CreateType();
        }

        return builder;
    }

    [Fact]
    public void Constructor_RejectsNullArguments() {
        Assert.Throws<ArgumentNullException>(() => new LoadedPluginCopyScanner(null!, () => Array.Empty<Assembly>()));
        Assert.Throws<ArgumentNullException>(() => new LoadedPluginCopyScanner(Own, null!));
    }

    [Fact]
    public void TypeNames_AreThePluginsOwn() {
        Assert.Equal(LoadedPluginCopyScanner.PluginTypeName, typeof(LianLiPlugin).FullName);
        Assert.Equal(LoadedPluginCopyScanner.GuardTypeName, typeof(PluginCopyGuard).FullName);
    }

    [Fact]
    public void Own_IsThisAssembly_WithTheGuard() {
        PluginCopy own = LoadedPluginCopyScanner.Process.Own;

        Assert.Equal(Own.Location, own.Location);
        Assert.Equal(Own.GetName().Version, own.Version);
        Assert.True(own.HasGuard);
    }

    [Fact]
    public void Process_ScansThisProcess_AndNeverReportsThisCopysFile()
        => Assert.DoesNotContain(LoadedPluginCopyScanner.Process.Others(), copy => string.Equals(copy.Location, Own.Location, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Others_FindsEveryOtherCopy_ByThePluginType_AndTellsWhichHaveTheGuard() {
        Directory.CreateDirectory(_directory);
        string renamed = Path.Combine(_directory, "FanControl.LianLi-Argb.dll");
        File.Copy(Own.Location, renamed);
        Assembly fromFile = _context.LoadFromAssemblyPath(renamed);
        Assembly sameFileAgain = _again.LoadFromAssemblyPath(Own.Location);
        Assembly older = InMemory("FanControl.LianLi.Argb", withGuard: false);
        Assembly newer = InMemory("FanControl.LianLi.Lighting", withGuard: true);

        var scanner = new LoadedPluginCopyScanner(Own, () => new[] { Own, typeof(object).Assembly, fromFile, sameFileAgain, older, newer });
        PluginCopy[] others = scanner.Others().ToArray();

        Assert.Equal(3, others.Length);
        Assert.Equal(renamed, others[0].Location);
        Assert.Equal(Own.GetName().Version, others[0].Version);
        Assert.True(others[0].HasGuard);
        Assert.Equal(string.Empty, others[1].Location);
        Assert.Equal(new Version(1, 1, 9, 0), others[1].Version);
        Assert.False(others[1].HasGuard);
        Assert.True(others[2].HasGuard);
    }
}
