using System.Collections.Generic;

namespace FanControl.LianLi.Devices;

/// <summary>
/// A controller that keeps a sensor after it has stopped driving the device behind it - the
/// wireless controller keeps a group's sensors when the group is unbound or unheard, a FLEX
/// receiver keeps its chain's when the radio takes the chain - so that an index keeps naming the
/// same thing and a curve bound to the sensor is not dropped. What such a controller reports
/// (<see cref="IFanDevice.Describe"/> and the readings) is therefore the union of what it drives
/// and what it retains, and this tells the two apart: only a sensor whose device the controller
/// drives at the moment is one it may claim from another controller that has the same id
/// (a FLEX chain's, on its receiver and on the dongles).
/// </summary>
internal interface IDrivenSensorSource {
    /// <summary>The ids of the sensors whose device this controller drives at the moment; every other sensor it reports is retained.</summary>
    IEnumerable<string> DrivenSensorIds { get; }
}
