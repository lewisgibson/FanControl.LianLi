namespace FanControl.LianLi.Plugin;

/// <summary>
/// The process-wide claim on driving the controllers that at most one copy of the plugin holds. A
/// seam so a test can decide whether another copy holds it without a named kernel object.
/// </summary>
internal interface IPluginCopyClaim {
    /// <summary>Take the claim if nothing holds it; true when this now holds it (or already did).</summary>
    bool TryTake();

    /// <summary>Give the claim up, if this holds it. Safe to call when it does not.</summary>
    void Release();
}
