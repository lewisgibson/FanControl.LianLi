using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FanControl.LianLi.Tests.Fakes;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

/// <summary>
/// The HID walk's decisions: which interfaces it opens at all (only those whose path names a device the
/// plugin drives), what it reads from each and what it keeps (vendor and product from the device, the
/// 0x0416 command interface's usage page), what a native failure costs, and a walk given up on making
/// no further native call.
/// </summary>
public class HidDeviceLocatorTests {
    private const string UniPath = @"\\?\hid#vid_0cf2&pid_a102&mi_01#7&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
    private const string UniInstance = @"HID\VID_0CF2&PID_A102&MI_01\7&1&0&0000";
    private const string CommandPath = @"\\?\hid#vid_0416&pid_7372&mi_01#command";
    private const string KeyboardPath = @"\\?\hid#vid_0416&pid_7372&mi_00#keyboard";
    private const string OtherVendorPath = @"\\?\hid#vid_046d&pid_c52b&mi_00#mouse";
    private const string BluetoothPath = @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002046d_pid&b01a#headset";
    private static readonly IReadOnlyList<int> Vendors = new[] { 0x0CF2, 0x0416 };
    private static readonly IReadOnlyList<int> Products = new[] { 0xA102, 0x7372 };

    private readonly FakeHidApi _hid = new FakeHidApi();
    private readonly FakeConfigurationManagerApi _configurationManager = new FakeConfigurationManagerApi();
    private readonly FakeLogger _log = new FakeLogger();

    private HidDeviceLocator Locator()
        => new HidDeviceLocator(_hid, _configurationManager, new ContainerIdResolver(_configurationManager), _log);

    private void List(params string[] paths) => _configurationManager.Interfaces[(FakeHidApi.HidClass, null)] = paths;

    private void Present(string path, int vendorId, int productId) => _hid.Attributes[path] = (vendorId, productId);

    [Fact]
    public void Locate_ListsThePresentHidInterfaces_AndReadsEachOneThePathNames() {
        List(UniPath);
        Present(UniPath, 0x0CF2, 0xA102);
        _hid.Capabilities[UniPath] = new HidCapabilities(0xFF72, 65, 353, 65);
        _configurationManager.InterfaceInstances[UniPath] = UniInstance;
        _configurationManager.DeviceNodes[UniInstance] = 9;
        _configurationManager.ContainerIds[9] = new Guid("abcdefab-cdef-abcd-efab-cdefabcdefab").ToByteArray();

        LocatedDevice located = Assert.Single(Locator().Locate(Vendors, Products, CancellationToken.None));

        // CM_GET_DEVICE_INTERFACE_LIST_PRESENT for the HID class, the whole class rather than one device.
        Assert.Equal(FakeHidApi.HidClass + "  0x00000000", _configurationManager.InterfaceListQueries[0]);
        Assert.Equal(
            new[] { "HidD_GetHidGuid", "OpenQueryHandle " + UniPath, "HidD_GetAttributes query 0", "HidD_GetPreparsedData query 0" },
            _hid.Calls);
        Assert.Equal(1, _hid.Handles[0].Releases);
        Assert.Equal((0x0CF2, 0xA102, UniPath), (located.VendorId, located.ProductId, located.DevicePath));
        Assert.Equal("{abcdefab-cdef-abcd-efab-cdefabcdefab}", located.ContainerId);
        Assert.Equal(353, located.MaxOutputReportLength);
        HidInterface device = Assert.IsType<HidInterface>(located.Device);
        Assert.Equal((0xFF72, 65, 353, 65), (
            device.Capabilities.UsagePage,
            device.Capabilities.InputReportLength,
            device.Capabilities.OutputReportLength,
            device.Capabilities.FeatureReportLength));
        Assert.Empty(_log.Messages);
    }

    [Fact]
    public void Locate_KeepsThePathInTheLowerCaseFormControllersHaveAlwaysBeenKeyedOn() {
        List(UniPath.ToUpperInvariant().Replace("#{4D1E55B2-F16F-11CF-88CB-001111000030}", "#{4d1e55b2-f16f-11cf-88cb-001111000030}"));
        Present(UniPath, 0x0CF2, 0xA102);

        LocatedDevice located = Assert.Single(Locator().Locate(Vendors, Products, CancellationToken.None));

        Assert.Equal(UniPath, located.DevicePath);
        Assert.Equal("OpenQueryHandle " + UniPath, _hid.Calls[1]);
    }

    [Fact]
    public void Locate_NeverOpensAnInterfaceWhosePathNamesAnotherDevice_OrNoUsbDeviceAtAll() {
        List(OtherVendorPath, BluetoothPath, @"\\?\hid#vid_0cf2&pid_a199#other-product", UniPath);
        Present(UniPath, 0x0CF2, 0xA102);

        Assert.Single(Locator().Locate(Vendors, Products, CancellationToken.None));

        Assert.Equal(new[] { "OpenQueryHandle " + UniPath }, _hid.Calls.Where(call => call.StartsWith("Open", StringComparison.Ordinal)));
    }

    [Fact]
    public void Locate_AnInterfaceThatReportsIdsItsPathDoesNotName_IsPassedOver_AndSaysSo() {
        List(UniPath);
        Present(UniPath, 0x0CF2, 0xA199);

        Assert.Empty(Locator().Locate(Vendors, Products, CancellationToken.None));

        Assert.Equal(
            "  HID interface " + UniPath + " reports 0CF2:A199, not ids the plugin drives; passed over",
            Assert.Single(_log.Messages));
        Assert.Equal(1, _hid.Handles[0].Releases);
    }

    [Fact]
    public void Locate_TheVendorIsCheckedFromTheDeviceToo() {
        List(UniPath);
        Present(UniPath, 0x046D, 0xA102);

        Assert.Empty(Locator().Locate(Vendors, Products, CancellationToken.None));
    }

    [Fact]
    public void Locate_AnInterfaceThatWillNotOpen_IsPassedOver_AndLogged() {
        List(UniPath, CommandPath);
        _hid.QueryOpenErrors[UniPath] = 5;
        Present(CommandPath, 0x0416, 0x7372);
        _hid.Capabilities[CommandPath] = new HidCapabilities(0xFF1B, 64, 64, 0);

        Assert.Equal(CommandPath, Assert.Single(Locator().Locate(Vendors, Products, CancellationToken.None)).DevicePath);

        Assert.Contains("  HID interface " + UniPath + ": opening failed (error 5); passed over", _log.Messages);
    }

    [Fact]
    public void Locate_AnInterfaceWhoseAttributesAreRefused_IsPassedOver_AndLogged() {
        List(UniPath);
        _hid.AttributeErrors[UniPath] = 31;

        Assert.Empty(Locator().Locate(Vendors, Products, CancellationToken.None));

        Assert.Equal("  HID interface " + UniPath + ": HidD_GetAttributes failed (error 31); passed over", Assert.Single(_log.Messages));
        Assert.Equal(1, _hid.Handles[0].Releases);
    }

    [Fact]
    public void Locate_ARefusedCapabilitiesProbe_IsLogged_AndTheInterfaceKeptWithNothingKnown() {
        List(UniPath);
        Present(UniPath, 0x0CF2, 0xA102);
        _hid.CapabilityErrors[UniPath] = 87;

        LocatedDevice located = Assert.Single(Locator().Locate(Vendors, Products, CancellationToken.None));

        Assert.Null(located.Device);
        Assert.Equal(0, located.MaxOutputReportLength);
        Assert.Contains("  report-capabilities probe refused for " + UniPath + " (error 87)", _log.Messages);
    }

    [Fact]
    public void Locate_ADescriptorThatDoesNotParse_IsLogged_AndItsPreparsedDataFreed() {
        List(UniPath);
        Present(UniPath, 0x0CF2, 0xA102);
        _hid.CapabilityStatuses[UniPath] = unchecked((int)0xC0110001);

        LocatedDevice located = Assert.Single(Locator().Locate(Vendors, Products, CancellationToken.None));

        Assert.Equal(0, located.MaxOutputReportLength);
        Assert.Contains("  report-capabilities probe refused for " + UniPath + " (error -1072627711)", _log.Messages);
        Assert.Equal(new[] { UniPath }, _hid.Freed);
    }

    [Fact]
    public void Locate_EveryDescriptorFetched_IsFreed() {
        List(UniPath);
        Present(UniPath, 0x0CF2, 0xA102);

        Assert.Single(Locator().Locate(Vendors, Products, CancellationToken.None));

        Assert.Equal(_hid.Calls.Count(call => call.StartsWith("HidD_GetPreparsedData", StringComparison.Ordinal)), _hid.Freed.Count);
        Assert.All(_hid.Freed, path => Assert.Equal(UniPath, path));
    }

    // Given up on while the descriptor was being fetched: what was fetched is still released.
    [Fact]
    public void Locate_GivenUpOnDuringTheDescriptorFetch_StillFreesIt() {
        List(UniPath);
        Present(UniPath, 0x0CF2, 0xA102);
        using var abandonment = new CancellationTokenSource();
        _hid.OnCall = call => {
            if (call.StartsWith("HidD_GetPreparsedData", StringComparison.Ordinal)) {
                abandonment.Cancel();
            }
        };

        Assert.Throws<OperationCanceledException>(() => Locator().Locate(Vendors, Products, abandonment.Token));

        Assert.Equal(new[] { UniPath }, _hid.Freed);
    }

    [Fact]
    public void Locate_AnUnresolvedContainerId_IsLogged_AndTheInterfaceKept() {
        List(UniPath);
        Present(UniPath, 0x0CF2, 0xA102);

        LocatedDevice located = Assert.Single(Locator().Locate(Vendors, Products, CancellationToken.None));

        Assert.Null(located.ContainerId);
        Assert.Equal(
            "  container-id probe unresolved for " + UniPath + ", de-dup falls back to the device path",
            Assert.Single(_log.Messages));
    }

    [Fact]
    public void Locate_KeepsOnlyTheCommandInterfaceOfA0416Device() {
        List(CommandPath, KeyboardPath);
        Present(CommandPath, 0x0416, 0x7372);
        Present(KeyboardPath, 0x0416, 0x7372);
        _hid.Capabilities[CommandPath] = new HidCapabilities(0xFF1B, 64, 64, 0);
        _hid.Capabilities[KeyboardPath] = new HidCapabilities(0x0001, 9, 2, 0);

        Assert.Equal(CommandPath, Assert.Single(Locator().Locate(Vendors, Products, CancellationToken.None)).DevicePath);
    }

    [Fact]
    public void Locate_A0416InterfaceWhoseCapabilitiesAreRefused_IsKept() {
        List(CommandPath);
        Present(CommandPath, 0x0416, 0x7372);
        _hid.CapabilityErrors[CommandPath] = 87;

        Assert.Single(Locator().Locate(Vendors, Products, CancellationToken.None));
    }

    [Fact]
    public void Locate_TheUniFamily_IsKeptWhateverItsUsagePage() {
        List(UniPath);
        Present(UniPath, 0x0CF2, 0xA102);
        _hid.Capabilities[UniPath] = new HidCapabilities(0x0001, 65, 65, 65);

        Assert.Single(Locator().Locate(Vendors, Products, CancellationToken.None));
    }

    [Fact]
    public void Locate_AListTheConfigurationManagerRefuses_FindsNothing_AndSaysWhy() {
        List(UniPath);
        _configurationManager.InterfaceListSizeResult = FakeConfigurationManagerApi.CrFailure;

        Assert.Empty(Locator().Locate(Vendors, Products, CancellationToken.None));

        Assert.Equal("  HID scan: sizing the interface list failed (code 19)", Assert.Single(_log.Messages));
    }

    // Every native call of the walk, in order, for an interface that resolves all the way. A walk given
    // up on while any one of them is in flight makes no further native call of either kind.
    [Theory]
    [InlineData("OpenQueryHandle")]
    [InlineData("HidD_GetAttributes")]
    [InlineData("HidD_GetPreparsedData")]
    public void Locate_GivenUpOnDuringAHidCall_MakesNoFurtherCall_AndClosesItsHandle(string blocked) {
        List(UniPath, UniPath + "-second");
        Present(UniPath, 0x0CF2, 0xA102);
        Present(UniPath + "-second", 0x0CF2, 0xA102);
        using var abandonment = new CancellationTokenSource();
        var calls = new List<string>();
        _configurationManager.OnCall = calls.Add;
        _hid.OnCall = call => {
            calls.Add(call);
            if (call.StartsWith(blocked, StringComparison.Ordinal)) {
                abandonment.Cancel();
            }
        };

        Assert.Throws<OperationCanceledException>(() => Locator().Locate(Vendors, Products, abandonment.Token));

        Assert.StartsWith(blocked, calls.Last(), StringComparison.Ordinal);
        Assert.All(_hid.Handles, handle => Assert.Equal(1, handle.Releases));
    }

    [Theory]
    [InlineData("HidD_GetHidGuid")]
    [InlineData("GetDeviceInterfaceListSize")]
    [InlineData("GetDeviceInterfaceList")]
    public void Locate_GivenUpOnWhileListing_OpensNothing(string blocked) {
        List(UniPath);
        Present(UniPath, 0x0CF2, 0xA102);
        using var abandonment = new CancellationTokenSource();
        Action<string> cancelOn = call => {
            if (call == blocked) {
                abandonment.Cancel();
            }
        };
        _configurationManager.OnCall = cancelOn;
        _hid.OnCall = cancelOn;

        Assert.Throws<OperationCanceledException>(() => Locator().Locate(Vendors, Products, abandonment.Token));

        Assert.Empty(_hid.Handles);
    }

    [Fact]
    public void Locate_AlreadyGivenUpOn_CallsNothing() {
        Assert.Throws<OperationCanceledException>(() => Locator().Locate(Vendors, Products, FakeDeviceCallRunner.Cancelled));

        Assert.Empty(_hid.Calls);
    }

    [Fact]
    public void Locate_NullAllowLists_Throw() {
        HidDeviceLocator locator = Locator();

        Assert.Throws<ArgumentNullException>(() => locator.Locate(null!, Products, CancellationToken.None));
        Assert.Throws<ArgumentNullException>(() => locator.Locate(Vendors, null!, CancellationToken.None));
    }

    [Fact]
    public void ReadInterface_ReadsTheCapabilitiesOfARememberedDevice_FromItsPath() {
        _hid.Capabilities[UniPath] = new HidCapabilities(0xFF72, 65, 353, 65);

        HidInterface device = Locator().ReadInterface(new LocatedDevice(0x0CF2, 0xA102, UniPath, null), CancellationToken.None);

        Assert.Equal((0x0CF2, 0xA102, UniPath, 353), (device.VendorId, device.ProductId, device.DevicePath, device.Capabilities.OutputReportLength));
        Assert.Equal(new[] { "OpenQueryHandle " + UniPath, "HidD_GetPreparsedData query 0" }, _hid.Calls);
        Assert.Equal(1, _hid.Handles[0].Releases);
    }

    [Theory]
    [InlineData(2)] // ERROR_FILE_NOT_FOUND
    [InlineData(3)] // ERROR_PATH_NOT_FOUND
    public void ReadInterface_ADeviceThatIsNotThere_SaysSo(int error) {
        _hid.QueryOpenErrors[UniPath] = error;

        IOException failure = Assert.Throws<IOException>(
            () => Locator().ReadInterface(new LocatedDevice(0x0CF2, 0xA102, UniPath, null), CancellationToken.None));

        Assert.Equal("No HID device is present at " + UniPath, failure.Message);
    }

    [Fact]
    public void ReadInterface_AnOpenRefusedForAnyOtherReason_SaysWhy() {
        _hid.QueryOpenErrors[UniPath] = 5;

        IOException failure = Assert.Throws<IOException>(
            () => Locator().ReadInterface(new LocatedDevice(0x0CF2, 0xA102, UniPath, null), CancellationToken.None));

        Assert.Equal("Failed to open HID device at " + UniPath + " (error 5).", failure.Message);
    }

    [Fact]
    public void ReadInterface_RefusedCapabilities_SaysWhy_AndClosesTheHandle() {
        _hid.CapabilityErrors[UniPath] = 87;

        IOException failure = Assert.Throws<IOException>(
            () => Locator().ReadInterface(new LocatedDevice(0x0CF2, 0xA102, UniPath, null), CancellationToken.None));

        Assert.Equal("Report capabilities of " + UniPath + " could not be read (error 87).", failure.Message);
        Assert.Equal(1, _hid.Handles[0].Releases);
    }

    [Fact]
    public void ReadInterface_GivenUpOnWhileItOpens_ReadsNothing_AndClosesTheHandle() {
        using var abandonment = new CancellationTokenSource();
        _hid.OnCall = call => {
            if (call.StartsWith("OpenQueryHandle", StringComparison.Ordinal)) {
                abandonment.Cancel();
            }
        };

        Assert.Throws<OperationCanceledException>(
            () => Locator().ReadInterface(new LocatedDevice(0x0CF2, 0xA102, UniPath, null), abandonment.Token));

        Assert.Single(_hid.Calls);
        Assert.Equal(1, _hid.Handles[0].Releases);
    }

    [Fact]
    public void ReadInterface_AlreadyGivenUpOn_OpensNothing() {
        Assert.Throws<OperationCanceledException>(
            () => Locator().ReadInterface(new LocatedDevice(0x0CF2, 0xA102, UniPath, null), FakeDeviceCallRunner.Cancelled));

        Assert.Empty(_hid.Calls);
    }

    [Fact]
    public void ReadInterface_NullDevice_Throws()
        => Assert.Throws<ArgumentNullException>(() => Locator().ReadInterface(null!, CancellationToken.None));

    [Fact]
    public void Constructor_ValidatesItsDependencies() {
        var containers = new ContainerIdResolver(_configurationManager);

        Assert.Throws<ArgumentNullException>(() => new HidDeviceLocator(null!, _configurationManager, containers, _log));
        Assert.Throws<ArgumentNullException>(() => new HidDeviceLocator(_hid, null!, containers, _log));
        Assert.Throws<ArgumentNullException>(() => new HidDeviceLocator(_hid, _configurationManager, null!, _log));
        Assert.Throws<ArgumentNullException>(() => new HidDeviceLocator(_hid, _configurationManager, containers, null!));
    }
}
