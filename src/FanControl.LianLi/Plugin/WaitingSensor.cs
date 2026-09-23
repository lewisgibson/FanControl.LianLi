using FanControl.Plugins;

namespace FanControl.LianLi.Plugin;

/// <summary>
/// The one sensor registered when nothing else was found but a device is still to come - a wireless
/// pair whose devices have not checked in over the radio yet, or a controller still opening at the
/// deadline - which in practice is only ever the very first run with it. FanControl only listens for
/// a plugin's refresh request when the plugin registered at least one sensor, so without this the
/// plugin could never ask for the refresh that brings that device in. It has no reading, and that
/// refresh replaces it with the real sensors.
/// </summary>
internal sealed class WaitingSensor : IPluginSensor {
    public string Id => "LianLi/waiting";

    public string Name => "Lian Li: waiting for devices";

    public float? Value => null;

    public void Update() {
    }
}
