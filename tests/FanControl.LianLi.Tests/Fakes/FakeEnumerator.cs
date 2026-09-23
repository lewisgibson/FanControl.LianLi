using System;
using System.Collections.Generic;
using System.IO;
using FanControl.LianLi.Transport;

namespace FanControl.LianLi.Tests.Fakes;

// The plugin opens a scan's devices side by side, so every record here is kept under a lock and
// read back as a snapshot.
internal sealed class FakeEnumerator : IDeviceEnumerator {
    private readonly object _lock = new object();
    private readonly List<LocatedDevice> _devices;
    private readonly List<FakeDeviceTransport> _opened = new List<FakeDeviceTransport>();
    private readonly List<string> _openedPaths = new List<string>();

    public FakeEnumerator(params LocatedDevice[] devices) {
        _devices = new List<LocatedDevice>(devices);
    }

    /// <summary>Every transport handed out by <see cref="Open"/>, in the order they were opened.</summary>
    public IReadOnlyList<FakeDeviceTransport> Opened {
        get {
            lock (_lock) {
                return _opened.ToArray();
            }
        }
    }

    /// <summary>The device paths <see cref="Open"/> was called for, in call order.</summary>
    public IReadOnlyList<string> OpenedPaths {
        get {
            lock (_lock) {
                return _openedPaths.ToArray();
            }
        }
    }

    /// <summary>The last transport opened for <paramref name="devicePath"/>.</summary>
    public FakeDeviceTransport TransportFor(string devicePath) {
        lock (_lock) {
            return _opened[_openedPaths.LastIndexOf(devicePath)];
        }
    }

    /// <summary>When set, <see cref="Open"/> throws, simulating a device that cannot be opened.</summary>
    public bool FailOpen { get; set; }

    /// <summary>When set, <see cref="Open"/> throws for the devices it matches and opens the rest.</summary>
    public Func<LocatedDevice, bool>? FailOpenWhen { get; set; }

    /// <summary>When set, every transport handed out by <see cref="Open"/> throws on Write.</summary>
    public bool FailWrites { get; set; }

    /// <summary>When set, every transport handed out by <see cref="Open"/> throws on SetFeature only
    /// - the feature-report path (fan control and lighting effects).</summary>
    public bool FailFeatures { get; set; }

    /// <summary>When set, <see cref="Locate"/> throws, simulating a device scan failure (e.g. the configuration manager refusing the interface list).</summary>
    public bool FailLocate { get; set; }

    /// <summary>When set, invoked on every transport handed out by <see cref="Open"/> so a test can
    /// seed its <see cref="FakeDeviceTransport.InputReport"/> (e.g. to drive channel-population detection).</summary>
    public Action<LocatedDevice, FakeDeviceTransport>? ConfigureTransport { get; set; }

    public IReadOnlyList<LocatedDevice> Locate(
        IReadOnlyList<int> vendorIds,
        IReadOnlyList<int> productIds) {
        if (FailLocate) {
            throw new InvalidOperationException("simulated device scan failure");
        }

        var result = new List<LocatedDevice>();
        foreach (LocatedDevice device in _devices) {
            if (Contains(vendorIds, device.VendorId) && Contains(productIds, device.ProductId)) {
                result.Add(device);
            }
        }

        return result;
    }

    public IDeviceTransport Open(LocatedDevice info) {
        if (FailOpen || (FailOpenWhen?.Invoke(info) ?? false)) {
            throw new IOException("simulated open failure for " + info.DevicePath);
        }

        var transport = new FakeDeviceTransport { FailWrites = FailWrites, FailFeatures = FailFeatures };
        ConfigureTransport?.Invoke(info, transport);
        lock (_lock) {
            _openedPaths.Add(info.DevicePath);
            _opened.Add(transport);
        }

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
