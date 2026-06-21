using System;
using System.Collections.Generic;
using System.Linq;
using FanControl.LianLi.Hid;
using Xunit;

namespace FanControl.LianLi.Tests.Hid;

public class HidDeviceDeduplicatorTests {
    private static HidDeviceInfo Device(
        string path, string? serial, int maxOutput = 0, int productId = 0xA102)
        => new HidDeviceInfo(0x0CF2, productId, path, null, serial, maxOutput);

    [Fact]
    public void InterfacesSharingASerial_CollapseToOne() {
        var result = HidDeviceDeduplicator.Deduplicate(new[] {
            Device("path/mi_00", "SN-A", maxOutput: 0),
            Device("path/mi_01", "SN-A", maxOutput: 65),
        });

        Assert.Single(result);
    }

    [Fact]
    public void Collapse_KeepsTheLargerOutputReportInterface() {
        // The control interface is the one that accepts output reports; keep it, not the sibling.
        var result = HidDeviceDeduplicator.Deduplicate(new[] {
            Device("path/mi_00", "SN-A", maxOutput: 0),
            Device("path/mi_01", "SN-A", maxOutput: 65),
        });

        Assert.Equal("path/mi_01", result[0].DevicePath);
    }

    [Fact]
    public void Collapse_KeepsLargerOutputInterface_RegardlessOfInputOrder() {
        var result = HidDeviceDeduplicator.Deduplicate(new[] {
            Device("path/mi_01", "SN-A", maxOutput: 65),
            Device("path/mi_00", "SN-A", maxOutput: 0),
        });

        Assert.Equal("path/mi_01", result[0].DevicePath);
    }

    [Fact]
    public void DistinctSerials_AreKeptSeparate() {
        var result = HidDeviceDeduplicator.Deduplicate(new[] {
            Device("hubA", "SN-A", maxOutput: 65),
            Device("hubB", "SN-B", maxOutput: 65),
        });

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void SerialLessDevices_AreNeverCollapsed() {
        // Two distinct controllers that report no serial must not be mistaken for one - the safe
        // direction. They key on their (unique) device paths instead.
        var result = HidDeviceDeduplicator.Deduplicate(new[] {
            Device("hubA", null, maxOutput: 65),
            Device("hubB", null, maxOutput: 65),
        });

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void EmptySerial_IsTreatedAsNoSerial() {
        var result = HidDeviceDeduplicator.Deduplicate(new[] {
            Device("hubA", "", maxOutput: 65),
            Device("hubB", "", maxOutput: 65),
        });

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void SameSerialDifferentProduct_IsNotCollapsed() {
        // A shared serial across different product ids is not one device; the key is scoped by
        // vendor/product so they stay separate.
        var result = HidDeviceDeduplicator.Deduplicate(new[] {
            Device("a", "SN", maxOutput: 65, productId: 0xA102),
            Device("b", "SN", maxOutput: 65, productId: 0xA103),
        });

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void RepresentativesAreReturnedInFirstSeenOrder() {
        var result = HidDeviceDeduplicator.Deduplicate(new[] {
            Device("zzz", "SN-Z", maxOutput: 65),
            Device("aaa", "SN-A", maxOutput: 65),
        });

        Assert.Equal(new[] { "zzz", "aaa" }, result.Select(d => d.DevicePath).ToArray());
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty() {
        IReadOnlyList<HidDeviceInfo> result =
            HidDeviceDeduplicator.Deduplicate(Array.Empty<HidDeviceInfo>());

        Assert.Empty(result);
    }
}
