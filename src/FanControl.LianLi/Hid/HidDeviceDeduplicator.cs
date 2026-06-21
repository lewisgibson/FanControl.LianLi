using System.Collections.Generic;
using System.Linq;

namespace FanControl.LianLi.Hid;

/// <summary>
/// Collapses the several HID interfaces a single physical controller can expose into one
/// logical device. A composite USB controller surfaces one <see cref="HidDeviceInfo"/> per
/// HID interface/top-level collection, so without this step the plugin would register a
/// duplicate set of channel sensors per interface for one physical controller.
///
/// The only reliable token shared across a controller's interfaces is its USB serial number,
/// so de-duplication groups strictly by non-empty serial: interfaces that report the same
/// serial are the same physical device. A device that reports no serial is never collapsed
/// (each is kept as its own controller), so two distinct serial-less controllers are never
/// mistaken for one - the safe failure direction. Pure and stateless so the grouping is
/// unit-testable without hardware.
/// </summary>
internal static class HidDeviceDeduplicator {
    /// <summary>
    /// Return one <see cref="HidDeviceInfo"/> per physical controller. Interfaces sharing a
    /// non-empty serial are collapsed to the one accepting the largest output report (the
    /// fan-control interface); serial-less devices pass through untouched. Representatives are
    /// returned in first-seen order - imposing a stable controller order is the caller's concern.
    /// </summary>
    public static IReadOnlyList<HidDeviceInfo> Deduplicate(IReadOnlyList<HidDeviceInfo> located) {
        // GroupBy keys each interface (unique-per-interface unless a non-empty serial ties a
        // controller's interfaces together) and preserves first-seen key order; Aggregate folds
        // each group's interfaces down to one representative with the same control-interface pick.
        return located
            .GroupBy(GroupKey)
            .Select(group => group.Aggregate(PreferControlInterface))
            .ToList();
    }

    private static string GroupKey(HidDeviceInfo info) {
        // A non-empty serial is the cross-interface identity; vendor/product scope it so two
        // different products cannot collide on a shared serial. Serial-less devices key on the
        // device path, which is unique per interface and so never groups.
        if (!string.IsNullOrEmpty(info.SerialNumber)) {
            return "sn:" + info.VendorId + ":" + info.ProductId + ":" + info.SerialNumber;
        }

        return "path:" + info.DevicePath;
    }

    private static HidDeviceInfo PreferControlInterface(HidDeviceInfo a, HidDeviceInfo b) {
        // Keep the interface that accepts the larger output report - the fan-control collection.
        // Tie-break on device path (ordinal) so the choice is deterministic across runs.
        if (b.MaxOutputReportLength != a.MaxOutputReportLength) {
            return b.MaxOutputReportLength > a.MaxOutputReportLength ? b : a;
        }

        return string.CompareOrdinal(b.DevicePath, a.DevicePath) < 0 ? b : a;
    }
}
