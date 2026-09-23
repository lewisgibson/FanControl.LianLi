using System;
using System.Collections.Generic;
using System.Linq;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

public class HidDeviceDeduplicatorTests {
    private static LocatedDevice Device(
        string path, string? containerId, int maxOutput = 0, int productId = 0xA102)
        => new LocatedDevice(0x0CF2, productId, path, null, containerId, maxOutput);

    [Fact]
    public void InterfacesSharingAContainerId_CollapseToOne() {
        // The #30 case: one physical controller exposing two matching HID interfaces (same physical
        // device, so the same ContainerId) must collapse to a single logical controller.
        var result = HidDeviceDeduplicator.Deduplicate(new[] {
            Device("path/mi_00", "CID-A", maxOutput: 0),
            Device("path/mi_01", "CID-A", maxOutput: 65),
        });

        Assert.Single(result);
    }

    [Fact]
    public void Collapse_KeepsTheLargerOutputReportInterface() {
        // The control interface is the one that accepts output reports; keep it, not the sibling.
        var result = HidDeviceDeduplicator.Deduplicate(new[] {
            Device("path/mi_00", "CID-A", maxOutput: 0),
            Device("path/mi_01", "CID-A", maxOutput: 65),
        });

        Assert.Equal("path/mi_01", result[0].DevicePath);
    }

    [Fact]
    public void Collapse_KeepsLargerOutputInterface_RegardlessOfInputOrder() {
        var result = HidDeviceDeduplicator.Deduplicate(new[] {
            Device("path/mi_01", "CID-A", maxOutput: 65),
            Device("path/mi_00", "CID-A", maxOutput: 0),
        });

        Assert.Equal("path/mi_01", result[0].DevicePath);
    }

    [Fact]
    public void DistinctContainerIds_AreKeptSeparate() {
        // The #15 case: three physically distinct controllers each have their own ContainerId, so all
        // three survive - even though on real hardware they report the same firmware-fixed serial.
        var result = HidDeviceDeduplicator.Deduplicate(new[] {
            Device("hubA", "CID-A", maxOutput: 65),
            Device("hubB", "CID-B", maxOutput: 65),
            Device("hubC", "CID-C", maxOutput: 65),
        });

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void ContainerLessDevices_AreNeverCollapsed() {
        // Two distinct controllers whose ContainerId could not be resolved must not be mistaken for
        // one - the safe direction. They key on their (unique) device paths instead.
        var result = HidDeviceDeduplicator.Deduplicate(new[] {
            Device("hubA", null, maxOutput: 65),
            Device("hubB", null, maxOutput: 65),
        });

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void EmptyContainerId_IsTreatedAsNoContainer() {
        var result = HidDeviceDeduplicator.Deduplicate(new[] {
            Device("hubA", "", maxOutput: 65),
            Device("hubB", "", maxOutput: 65),
        });

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void SameContainerDifferentProduct_IsNotCollapsed() {
        // A shared ContainerId across different product ids is not one logical controller; the key is
        // scoped by vendor/product so they stay separate.
        var result = HidDeviceDeduplicator.Deduplicate(new[] {
            Device("a", "CID", maxOutput: 65, productId: 0xA102),
            Device("b", "CID", maxOutput: 65, productId: 0xA103),
        });

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void RepresentativesAreReturnedInFirstSeenOrder() {
        var result = HidDeviceDeduplicator.Deduplicate(new[] {
            Device("zzz", "CID-Z", maxOutput: 65),
            Device("aaa", "CID-A", maxOutput: 65),
        });

        Assert.Equal(new[] { "zzz", "aaa" }, result.Select(d => d.DevicePath).ToArray());
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty() {
        IReadOnlyList<LocatedDevice> result =
            HidDeviceDeduplicator.Deduplicate(Array.Empty<LocatedDevice>());

        Assert.Empty(result);
    }

    [Fact]
    public void Collapse_OnEqualOutputReports_KeepsTheOrdinallyFirstPath_RegardlessOfInputOrder() {
        IReadOnlyList<LocatedDevice> forward = HidDeviceDeduplicator.Deduplicate(new[] {
            Device("path/b", "CID-A", maxOutput: 65),
            Device("path/a", "CID-A", maxOutput: 65),
        });
        IReadOnlyList<LocatedDevice> reverse = HidDeviceDeduplicator.Deduplicate(new[] {
            Device("path/a", "CID-A", maxOutput: 65),
            Device("path/b", "CID-A", maxOutput: 65),
        });

        Assert.Equal("path/a", Assert.Single(forward).DevicePath);
        Assert.Equal("path/a", Assert.Single(reverse).DevicePath);
    }

    [Fact]
    public void TestConstructedDevices_CarryNoHidHandle()
        => Assert.Null(Device("path/a", null).Device);
}
