using System;

namespace FanControl.LianLi.Plugin;

/// <summary>One remembered controller as it is saved: under its key, with when it was last built.</summary>
internal sealed class StoredController {
    /// <summary>The controller remembered under <paramref name="key"/>, last built at <paramref name="lastSeenUtc"/>.</summary>
    public StoredController(string key, RememberedController controller, DateTime lastSeenUtc) {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Controller = controller ?? throw new ArgumentNullException(nameof(controller));
        LastSeenUtc = lastSeenUtc;
    }

    /// <summary>The key the controller is remembered under: its first device's path.</summary>
    public string Key { get; }

    /// <summary>What is remembered about it.</summary>
    public RememberedController Controller { get; }

    /// <summary>When a scan last built it.</summary>
    public DateTime LastSeenUtc { get; }
}
