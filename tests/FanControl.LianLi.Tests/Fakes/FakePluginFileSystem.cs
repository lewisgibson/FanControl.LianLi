using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FanControl.LianLi.Plugin;

namespace FanControl.LianLi.Tests.Fakes;

// An in-memory folder: each file has a write time and the assembly name its metadata gives, and
// any operation can be made to fail the way the real one does.
internal sealed class FakePluginFileSystem : IPluginFileSystem {
    private readonly Dictionary<string, (DateTime Written, string AssemblyName)> _files =
        new Dictionary<string, (DateTime, string)>(StringComparer.OrdinalIgnoreCase);

    public List<string> Moves { get; } = new List<string>();

    public List<string> Deletes { get; } = new List<string>();

    public int Finds { get; private set; }

    public Exception? MoveFailure { get; set; }

    public Exception? DeleteFailure { get; set; }

    public Exception? FindFailure { get; set; }

    public Exception? ReadFailure { get; set; }

    public IReadOnlyCollection<string> Files => _files.Keys.ToArray();

    public void Add(string path, DateTime writtenUtc, string assemblyName = "FanControl.LianLi")
        => _files[path] = (writtenUtc, assemblyName);

    public bool Exists(string path) => _files.ContainsKey(path);

    public DateTime InstalledUtc(string path)
        => _files.TryGetValue(path, out var file) ? file.Written : new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public void Move(string source, string destination) {
        if (MoveFailure != null) {
            throw MoveFailure;
        }

        Moves.Add(source + " -> " + destination);
        _files[destination] = _files[source];
        _files.Remove(source);
    }

    public void Delete(string path) {
        if (DeleteFailure != null) {
            throw DeleteFailure;
        }

        Deletes.Add(path);
        _files.Remove(path);
    }

    public IReadOnlyList<string> Find(string directory, string pattern) {
        Finds++;
        if (FindFailure != null) {
            throw FindFailure;
        }

        // The guard only ever asks for FanControl*.dll.removed.
        return _files.Keys
            .Where(path => path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(path).StartsWith("FanControl", StringComparison.Ordinal)
                && path.EndsWith(".dll.removed", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public string AssemblyNameOf(string path) {
        if (ReadFailure != null) {
            throw ReadFailure;
        }

        return _files[path].AssemblyName;
    }
}
