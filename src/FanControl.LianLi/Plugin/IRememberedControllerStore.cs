using System.Collections.Generic;
using FanControl.LianLi.Logging;

namespace FanControl.LianLi.Plugin;

/// <summary>
/// Where the controllers a process has built are kept between runs, so that after a reboot the
/// plugin registers their sensors before the devices have answered. FanControl only listens for a
/// plugin's refresh request once the plugin has registered at least one sensor, so without this a
/// start where nothing answers in time would leave the plugin unable to ask for the refresh that
/// brings the devices in.
/// </summary>
internal interface IRememberedControllerStore {
    /// <summary>
    /// What was saved: empty when nothing was, or what was is not usable (logged); null when it
    /// could not be read just now (logged) and is worth reading again - it is not to be replaced
    /// until it has been.
    /// </summary>
    IReadOnlyList<StoredController>? Load(ILog log);

    /// <summary>Replace what is saved with <paramref name="controllers"/>; a failure is logged.</summary>
    void Save(IReadOnlyList<StoredController> controllers, ILog log);
}
