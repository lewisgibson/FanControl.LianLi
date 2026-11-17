using System;
using System.Collections.Generic;

namespace FanControl.LianLi.Plugin;

/// <summary>
/// The file operations <see cref="PluginCopyGuard"/> uses to take another copy of the plugin out of
/// FanControl's Plugins folder. A seam so a test can give it any folder, and any failure, without
/// touching a real FanControl install.
/// </summary>
internal interface IPluginFileSystem {
    /// <summary>Whether a file exists at <paramref name="path"/>.</summary>
    bool Exists(string path);

    /// <summary>
    /// When the file at <paramref name="path"/> was put where it is, in UTC: the later of its
    /// creation and last write times, since extracting a zip or copying a file keeps the build's
    /// write time but gives the new file a creation time of its own.
    /// </summary>
    DateTime InstalledUtc(string path);

    /// <summary>Rename <paramref name="source"/> to <paramref name="destination"/>, which must not exist.</summary>
    void Move(string source, string destination);

    /// <summary>Delete the file at <paramref name="path"/>.</summary>
    void Delete(string path);

    /// <summary>The files under <paramref name="directory"/>, at any depth, whose names match <paramref name="pattern"/>.</summary>
    IReadOnlyList<string> Find(string directory, string pattern);

    /// <summary>The simple name of the assembly in the file at <paramref name="path"/>, read without loading it.</summary>
    string AssemblyNameOf(string path);
}
