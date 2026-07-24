using System;
using System.Collections.Generic;
using System.IO;
using FanControl.LianLi.Hid;

namespace FanControl.LianLi.Tests.Fakes;

internal sealed class FakeEnumerator : IHidDeviceEnumerator {
    private readonly List<HidDeviceInfo> _devices;

    public FakeEnumerator(params HidDeviceInfo[] devices) {
        _devices = new List<HidDeviceInfo>(devices);
    }

    /// <summary>Every transport handed out by <see cref="Open"/>, for disposal assertions.</summary>
    public List<FakeHidTransport> Opened { get; } = new List<FakeHidTransport>();

    /// <summary>Device paths in the order <see cref="Open"/> was called, so a test can assert which
    /// physical device became which controller after the stable ordering is applied.</summary>
    public List<string> OpenedPaths { get; } = new List<string>();

    /// <summary>When set, <see cref="Open"/> throws, simulating a device that cannot be opened.</summary>
    public bool FailOpen { get; set; }

    /// <summary>When set, every transport handed out by <see cref="Open"/> throws on Write.</summary>
    public bool FailWrites { get; set; }

    /// <summary>When set, every transport handed out by <see cref="Open"/> throws on SetFeature only
    /// - the feature-report path (fan control and lighting effects).</summary>
    public bool FailFeatures { get; set; }

    /// <summary>When set, <see cref="Locate"/> throws, simulating a HidSharp enumeration failure (e.g. RegisterClass failed).</summary>
    public bool FailLocate { get; set; }

    /// <summary>When set, invoked on every transport handed out by <see cref="Open"/> so a test can
    /// seed its <see cref="FakeHidTransport.InputReport"/> (e.g. to drive channel-population detection).</summary>
    public Action<HidDeviceInfo, FakeHidTransport>? ConfigureTransport { get; set; }

    public IReadOnlyList<HidDeviceInfo> Locate(
        IReadOnlyList<int> vendorIds,
        IReadOnlyList<int> productIds) {
        if (FailLocate) {
            throw new InvalidOperationException("simulated HidSharp enumeration failure");
        }

        var result = new List<HidDeviceInfo>();
        foreach (HidDeviceInfo device in _devices) {
            if (Contains(vendorIds, device.VendorId) && Contains(productIds, device.ProductId)) {
                result.Add(device);
            }
        }

        return result;
    }

    public IHidTransport Open(HidDeviceInfo info) {
        if (FailOpen) {
            throw new IOException("simulated open failure for " + info.DevicePath);
        }

        OpenedPaths.Add(info.DevicePath);
        var transport = new FakeHidTransport { FailWrites = FailWrites, FailFeatures = FailFeatures };
        ConfigureTransport?.Invoke(info, transport);
        Opened.Add(transport);
        return transport;
    }

    private static bool Contains(IReadOnlyList<int> list, int value) {
        for (int i = 0; i < list.Count; i++) {
            if (list[i] == value) {
                return true;
            }
        }

        return false;
    }
}
