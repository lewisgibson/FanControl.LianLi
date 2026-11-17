using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace FanControl.LianLi.Plugin;

/// <summary>
/// The real file system behind <see cref="IPluginFileSystem"/>. Each call is the one framework call
/// it names, and a failure is thrown to the caller, which decides what it costs.
/// </summary>
internal sealed class PluginFileSystem : IPluginFileSystem {
    /// <inheritdoc />
    public bool Exists(string path) => File.Exists(path);

    /// <inheritdoc />
    public DateTime InstalledUtc(string path) {
        DateTime created = File.GetCreationTimeUtc(path);
        DateTime written = File.GetLastWriteTimeUtc(path);
        return created > written ? created : written;
    }

    /// <inheritdoc />
    public void Move(string source, string destination) => File.Move(source, destination);

    /// <inheritdoc />
    public void Delete(string path) => File.Delete(path);

    /// <inheritdoc />
    public IReadOnlyList<string> Find(string directory, string pattern)
        => Directory.GetFiles(directory, pattern, SearchOption.AllDirectories);

    // GetAssemblyName reads the file's metadata without loading it, whatever the file is called,
    // and an assembly's metadata always names it, so Name is never null here.
    /// <inheritdoc />
    public string AssemblyNameOf(string path) => AssemblyName.GetAssemblyName(path).Name!;
}
