using System.Collections.Generic;
using System.Linq;

namespace FanControl.LianLi.Transport;

/// <summary>
/// Collapses the several HID interfaces a single physical controller can expose into one
/// logical device. A composite USB controller surfaces one <see cref="LocatedDevice"/> per
/// HID interface/top-level collection, so without this step the plugin would register a
/// duplicate set of channel sensors per interface for one physical controller.
///
/// De-duplication groups by the Windows ContainerId: every HID interface a single controller
/// exposes shares it, and it differs across physical controllers. The USB serial is deliberately
/// not the key - the Lian Li Uni controllers all report the same firmware-fixed serial, so a
/// serial key wrongly collapses every distinct controller into one (the ContainerId does not). A
/// device whose ContainerId could not be resolved is never collapsed (it keys on its per-interface
/// device path), so two distinct controllers are never mistaken for one - the safe failure
/// direction. Pure and stateless so the grouping is unit-testable without hardware.
/// </summary>
internal static class HidDeviceDeduplicator {
    /// <summary>
    /// Return one <see cref="LocatedDevice"/> per physical controller. Interfaces sharing a
    /// ContainerId are collapsed to the one accepting the largest output report (the fan-control
    /// interface); a device with no resolved ContainerId passes through untouched (keyed on its
    /// device path). Representatives are returned in first-seen order - imposing a stable controller
    /// order is the caller's concern.
    /// </summary>
    public static IReadOnlyList<LocatedDevice> Deduplicate(IReadOnlyList<LocatedDevice> located) {
        // GroupBy keys each interface (one key per physical device when its ContainerId resolved,
        // otherwise unique-per-interface) and preserves first-seen key order; Aggregate folds each
        // group's interfaces down to one representative with the same control-interface pick.
        return located
            .GroupBy(GroupKey)
            .Select(group => group.Aggregate(PreferControlInterface))
            .ToList();
    }

    private static string GroupKey(LocatedDevice info) {
        // The ContainerId is the cross-interface identity of one physical device; vendor/product
        // scope it so the rare case of one container exposing two different products is never
        // merged. A device with no resolved ContainerId keys on its device path, which is unique per
        // interface and so never groups - the safe direction (never collapse distinct controllers).
        if (!string.IsNullOrEmpty(info.ContainerId)) {
            return "cid:" + info.VendorId + ":" + info.ProductId + ":" + info.ContainerId;
        }

        return "path:" + info.DevicePath;
    }

    private static LocatedDevice PreferControlInterface(LocatedDevice a, LocatedDevice b) {
        // Keep the interface that accepts the larger output report - the fan-control collection.
        // Tie-break on device path (ordinal) so the choice is deterministic across runs.
        if (b.MaxOutputReportLength != a.MaxOutputReportLength) {
            return b.MaxOutputReportLength > a.MaxOutputReportLength ? b : a;
        }

        return string.CompareOrdinal(b.DevicePath, a.DevicePath) < 0 ? b : a;
    }
}
