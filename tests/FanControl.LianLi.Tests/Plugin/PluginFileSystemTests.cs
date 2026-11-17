using System;
using System.IO;
using FanControl.LianLi.Plugin;
using Xunit;

namespace FanControl.LianLi.Tests.Plugin;

/// <summary>The real file operations behind the guard, on a folder of the test's own.</summary>
public sealed class PluginFileSystemTests : IDisposable {
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lianli-files-" + Guid.NewGuid().ToString("N"));
    private readonly PluginFileSystem _files = new PluginFileSystem();

    public PluginFileSystemTests() {
        Directory.CreateDirectory(Path.Combine(_directory, "LianLi"));
    }

    public void Dispose() {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Exists_ReadsTheFile() {
        string path = Path.Combine(_directory, "FanControl.LianLi.dll");
        Assert.False(_files.Exists(path));

        File.WriteAllText(path, "x");
        Assert.True(_files.Exists(path));
    }

    [Fact]
    public void InstalledUtc_IsTheLaterOfCreationAndLastWrite() {
        // An extracted or copied file keeps the build's write time but is created when it lands.
        string path = Path.Combine(_directory, "FanControl.LianLi.dll");
        File.WriteAllText(path, "x");
        var built = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var landed = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

        File.SetLastWriteTimeUtc(path, built);
        File.SetCreationTimeUtc(path, landed);
        Assert.Equal(landed, _files.InstalledUtc(path));

        File.SetCreationTimeUtc(path, built);
        File.SetLastWriteTimeUtc(path, landed);
        Assert.Equal(landed, _files.InstalledUtc(path));
    }

    [Fact]
    public void MoveAndDelete_RenameThenRemoveTheFile() {
        string path = Path.Combine(_directory, "FanControl.LianLi.dll");
        File.WriteAllText(path, "x");

        _files.Move(path, path + ".removed");
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".removed"));

        _files.Delete(path + ".removed");
        Assert.False(File.Exists(path + ".removed"));
    }

    [Fact]
    public void Find_SearchesEveryFolderBelow() {
        string top = Path.Combine(_directory, "FanControl.LianLi.Argb.dll.removed");
        string nested = Path.Combine(_directory, "LianLi", "FanControl.LianLi.dll.removed");
        File.WriteAllText(top, "x");
        File.WriteAllText(nested, "x");
        File.WriteAllText(Path.Combine(_directory, "FanControl.LianLi.dll"), "x");

        string[] found = (string[])_files.Find(_directory, "FanControl*.dll.removed");
        Array.Sort(found, StringComparer.Ordinal);

        Assert.Equal(new[] { top, nested }, found);
    }

    [Fact]
    public void AssemblyNameOf_ReadsARenamedAssemblyWithoutLoadingIt() {
        string path = Path.Combine(_directory, "FanControl.LianLi.Argb.dll.removed");
        File.Copy(typeof(PluginFileSystem).Assembly.Location, path);

        Assert.Equal("FanControl.LianLi", _files.AssemblyNameOf(path));
    }
}
