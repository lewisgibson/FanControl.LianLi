using System;
using System.Threading;

namespace FanControl.LianLi.Plugin;

/// <summary>
/// The claim on driving the controllers, as a named semaphore of one. A name is the one thing two
/// copies of the plugin share: each is its own assembly, with its own statics, and on .NET Framework
/// may even be in another application domain. A semaphore, not a mutex, because a mutex belongs to
/// the thread that took it, and FanControl closes a plugin on a different thread from the one it
/// initialised it on. The name carries the process id, so a second FanControl process (the app
/// beside the service) has a claim of its own.
/// </summary>
internal sealed class PluginCopyClaim : IPluginCopyClaim, IDisposable {
    private readonly string _name;

    // Held only while the claim is: closing the last handle to a named semaphore destroys it, so a
    // copy that is not driving anything keeps no kernel object open.
    private Semaphore? _held;

    /// <summary>A claim on the semaphore named <paramref name="name"/>.</summary>
    public PluginCopyClaim(string name) {
        if (string.IsNullOrEmpty(name)) {
            throw new ArgumentException("The claim needs a name.", nameof(name));
        }

        _name = name;
    }

    /// <summary>The name every copy of the plugin in process <paramref name="processId"/> claims.</summary>
    public static string NameFor(int processId) => "FanControl.LianLi.Driving." + processId;

    /// <inheritdoc />
    public bool TryTake() {
        if (_held != null) {
            return true;
        }

        var semaphore = new Semaphore(1, 1, _name);
        if (!semaphore.WaitOne(0)) {
            semaphore.Dispose();
            return false;
        }

        _held = semaphore;
        return true;
    }

    /// <inheritdoc />
    public void Release() {
        if (_held is null) {
            return;
        }

        _held.Release();
        _held.Dispose();
        _held = null;
    }

    /// <summary>Give the claim up if it is held. Safe to call more than once.</summary>
    public void Dispose() => Release();
}
