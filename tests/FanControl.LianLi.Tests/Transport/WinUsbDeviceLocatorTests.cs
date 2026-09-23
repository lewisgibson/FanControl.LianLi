using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FanControl.LianLi.Tests.Fakes;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

/// <summary>
/// Locating the L-Wireless dongles through the configuration manager: the present USB instances, the
/// interface GUID each one's driver wrote to its hardware key, and the present interface of that class.
/// The flags and codes are asserted against the Windows SDK's literal values.
/// </summary>
public class WinUsbDeviceLocatorTests {
    private const string Transmitter = @"USB\VID_0416&PID_8040\6&1b2c3d4e&0&2";
    private const string Receiver = @"USB\VID_0416&PID_8041\6&1b2c3d4e&0&3";
    private const string TransmitterPath = @"\\?\usb#vid_0416&pid_8040#6&1b2c3d4e&0&2#{2c63e7a4-6d6e-4b0e-8f3d-1d8e5f0a9b71}";
    private const string ReceiverPath = @"\\?\usb#vid_0416&pid_8041#6&1b2c3d4e&0&3#{2c63e7a4-6d6e-4b0e-8f3d-1d8e5f0a9b71}";
    private const uint TransmitterNode = 7;
    private const uint ReceiverNode = 8;
    private static readonly Guid InterfaceGuid = new Guid("2c63e7a4-6d6e-4b0e-8f3d-1d8e5f0a9b71");
    private static readonly IReadOnlyList<int> Vendors = new[] { 0x0416 };
    private static readonly IReadOnlyList<int> Products = new[] { 0x8040, 0x8041 };

    // REG_SZ and REG_MULTI_SZ.
    private const int RegSz = 1;
    private const int RegMultiSz = 7;

    private static FakeConfigurationManagerApi TwoDongles() {
        var api = new FakeConfigurationManagerApi();
        api.DeviceIds.AddRange(new[] { @"USB\ROOT_HUB30\4&1a2b3c&0&0", Transmitter, Receiver });
        AddDongle(api, Transmitter, TransmitterNode, TransmitterPath);
        AddDongle(api, Receiver, ReceiverNode, ReceiverPath);
        return api;
    }

    private static void AddDongle(FakeConfigurationManagerApi api, string instance, uint node, string path) {
        api.DeviceNodes[instance] = node;
        api.HardwareKeys[node] = new Dictionary<string, (int Type, char[] Data)> {
            ["DeviceInterfaceGUIDs"] = (RegMultiSz, FakeConfigurationManagerApi.MultiString("{" + InterfaceGuid + "}")),
        };
        api.Interfaces[(InterfaceGuid, instance)] = new[] { path };
    }

    private static WinUsbDeviceLocator Locator(FakeConfigurationManagerApi api, FakeLogger? log = null)
        => new WinUsbDeviceLocator(api, new ContainerIdResolver(api), log ?? new FakeLogger());

    [Fact]
    public void Locate_FindsEachDongle_AtItsRegisteredInterfacePath() {
        FakeConfigurationManagerApi api = TwoDongles();

        IReadOnlyList<LocatedDevice> located = Locator(api).Locate(Vendors, Products, CancellationToken.None);

        Assert.Collection(
            located,
            d => { Assert.Equal(0x8040, d.ProductId); Assert.Equal(TransmitterPath, d.DevicePath); Assert.Null(d.Device); },
            d => { Assert.Equal(0x8041, d.ProductId); Assert.Equal(ReceiverPath, d.DevicePath); });
        Assert.All(located, d => Assert.Equal(0x0416, d.VendorId));
    }

    [Fact]
    public void Locate_AsksForPresentUsbDevices_ThePresentInterfaces_AndTheHardwareKeyReadOnly() {
        FakeConfigurationManagerApi api = TwoDongles();

        _ = Locator(api).Locate(Vendors, Products, CancellationToken.None);

        // CM_GETIDLIST_FILTER_ENUMERATOR (0x1) | CM_GETIDLIST_FILTER_PRESENT (0x100) under "USB".
        Assert.Equal(new[] { "USB 0x00000101" }, api.DeviceIdListQueries);
        // CM_GET_DEVICE_INTERFACE_LIST_PRESENT is 0 (1 would be ALL_DEVICES, disabled interfaces too).
        Assert.All(api.InterfaceListQueries, query => Assert.EndsWith(" 0x00000000", query));
        // KEY_READ, the current hardware profile, RegDisposition_OpenExisting, CM_REGISTRY_HARDWARE.
        Assert.Equal(
            new[] { "7 sam=0x20019 profile=0 disposition=1 flags=0", "8 sam=0x20019 profile=0 disposition=1 flags=0" },
            api.KeyOpens);
        Assert.Equal(new uint[] { TransmitterNode, ReceiverNode }, api.ClosedKeys);
        Assert.All(api.LocateFlags, flags => Assert.Equal(0u, flags));
    }

    [Fact]
    public void Locate_ResolvesTheContainerId_OfEachDongle() {
        FakeConfigurationManagerApi api = TwoDongles();
        api.InterfaceInstances[TransmitterPath] = Transmitter;
        api.ContainerIds[TransmitterNode] = new Guid("11111111-2222-3333-4444-555555555555").ToByteArray();

        IReadOnlyList<LocatedDevice> located = Locator(api).Locate(Vendors, Products, CancellationToken.None);

        Assert.Equal("{11111111-2222-3333-4444-555555555555}", located[0].ContainerId);
        Assert.Null(located[1].ContainerId);
    }

    [Fact]
    public void Locate_KeepsOnlyDongles_OnBothAllowLists() {
        FakeConfigurationManagerApi api = TwoDongles();
        api.DeviceIds.Add(@"USB\VID_0CF2&PID_A102\5&1&0&1"); // a wired controller: not a dongle

        Assert.Single(Locator(api).Locate(Vendors, new[] { 0x8041 }, CancellationToken.None));
        Assert.Empty(Locator(api).Locate(new[] { 0x1A86 }, Products, CancellationToken.None));
        Assert.Equal(2, Locator(api).Locate(new[] { 0x0CF2, 0x0416 }, new[] { 0xA102, 0x8040, 0x8041 }, CancellationToken.None).Count);
    }

    [Fact]
    public void Locate_TheAlternateVendorsDongles_AreFound() {
        var api = new FakeConfigurationManagerApi();
        const string Alternate = @"USB\VID_1A86&PID_E304\5&abcdef&0&1";
        api.DeviceIds.Add(Alternate);
        AddDongle(api, Alternate, 3, @"\\?\usb#vid_1a86&pid_e304#5&abcdef&0&1#{guid}");

        LocatedDevice located = Assert.Single(Locator(api).Locate(new[] { 0x1A86 }, new[] { 0xE304 }, CancellationToken.None));
        Assert.Equal(0xE304, located.ProductId);
    }

    [Fact]
    public void Locate_FallsBackToTheSingleStringGuidValue() {
        FakeConfigurationManagerApi api = TwoDongles();
        var log = new FakeLogger();
        api.HardwareKeys[TransmitterNode] = new Dictionary<string, (int Type, char[] Data)> {
            ["DeviceInterfaceGuid"] = (RegSz, ("{" + InterfaceGuid + "}\0").ToCharArray()),
        };

        Assert.Equal(TransmitterPath, Locator(api, log).Locate(Vendors, Products, CancellationToken.None)[0].DevicePath);
        Assert.Empty(log.Messages); // the plural value simply being absent is not a failure
    }

    [Fact]
    public void Locate_AnUnterminatedValue_StillReads() {
        FakeConfigurationManagerApi api = TwoDongles();
        api.HardwareKeys[TransmitterNode] = new Dictionary<string, (int Type, char[] Data)> {
            ["DeviceInterfaceGuid"] = (RegSz, ("{" + InterfaceGuid + "}").ToCharArray()),
        };

        Assert.Equal(TransmitterPath, Locator(api).Locate(Vendors, Products, CancellationToken.None)[0].DevicePath);
    }

    [Fact]
    public void Locate_SkipsAGuidThatDoesNotParse_AndTriesTheNext() {
        FakeConfigurationManagerApi api = TwoDongles();
        var log = new FakeLogger();
        api.HardwareKeys[TransmitterNode]["DeviceInterfaceGUIDs"] =
            (RegMultiSz, FakeConfigurationManagerApi.MultiString("not-a-guid", "{" + InterfaceGuid + "}"));

        Assert.Equal(TransmitterPath, Locator(api, log).Locate(Vendors, Products, CancellationToken.None)[0].DevicePath);
        Assert.Contains(log.Messages, m => m.Contains("registered interface GUID 'not-a-guid' is not a GUID"));
    }

    [Fact]
    public void Locate_TriesTheNextGuid_WhenTheFirstHasNoInterface() {
        FakeConfigurationManagerApi api = TwoDongles();
        var stale = new Guid("99999999-9999-9999-9999-999999999999");
        api.HardwareKeys[TransmitterNode]["DeviceInterfaceGUIDs"] =
            (RegMultiSz, FakeConfigurationManagerApi.MultiString("{" + stale + "}", "{" + InterfaceGuid + "}"));

        Assert.Equal(TransmitterPath, Locator(api).Locate(Vendors, Products, CancellationToken.None)[0].DevicePath);
    }

    [Fact]
    public void Locate_ChoosesTheFirstPresentInterface() {
        FakeConfigurationManagerApi api = TwoDongles();
        api.Interfaces[(InterfaceGuid, Transmitter)] = new[] { TransmitterPath, TransmitterPath + "-second" };

        Assert.Equal(TransmitterPath, Locator(api).Locate(Vendors, Products, CancellationToken.None)[0].DevicePath);
    }

    [Fact]
    public void Locate_ADongleWithoutWinUsbBound_IsSkipped() {
        FakeConfigurationManagerApi api = TwoDongles();
        var log = new FakeLogger();
        api.HardwareKeys[TransmitterNode] = new Dictionary<string, (int Type, char[] Data)>();

        Assert.Equal(ReceiverPath, Assert.Single(Locator(api, log).Locate(Vendors, Products, CancellationToken.None)).DevicePath);
        Assert.Contains(log.Messages, m => m.Contains("has no WinUSB interface registered: Windows has not bound it to WinUSB, so L-Connect cannot reach it either"));
    }

    // WinUSB registered, but its interface not there yet: logged, so a dongle still starting is not
    // silently missing.
    [Fact]
    public void Locate_ADongleWithItsInterfaceRegisteredButNotPresent_IsSkipped_AndLogged() {
        FakeConfigurationManagerApi api = TwoDongles();
        var log = new FakeLogger();
        api.Interfaces[(InterfaceGuid, Transmitter)] = Array.Empty<string>();

        Assert.Equal(ReceiverPath, Assert.Single(Locator(api, log).Locate(Vendors, Products, CancellationToken.None)).DevicePath);
        Assert.Contains(log.Messages, m => m.Contains("has a WinUSB interface registered but none present: it may still be starting, and the next scan looks again"));
    }

    [Fact]
    public void Locate_ADongleWithNoHardwareKey_IsSkipped() {
        FakeConfigurationManagerApi api = TwoDongles();
        var log = new FakeLogger();
        api.HardwareKeys.Remove(TransmitterNode);

        Assert.Single(Locator(api, log).Locate(Vendors, Products, CancellationToken.None));
        Assert.Equal(new uint[] { ReceiverNode }, api.ClosedKeys);
        Assert.Contains(log.Messages, m => m.Contains("opening the hardware key failed for USB\\VID_0416&PID_8040\\6&1b2c3d4e&0&2 (code 37)"));
    }

    [Fact]
    public void Locate_ADongleWithNoDeviceNode_IsSkipped() {
        FakeConfigurationManagerApi api = TwoDongles();
        var log = new FakeLogger();
        api.DeviceNodes.Remove(Transmitter);

        Assert.Single(Locator(api, log).Locate(Vendors, Products, CancellationToken.None));
        Assert.Contains(log.Messages, m => m.Contains("locating the device node failed for USB\\VID_0416&PID_8040\\6&1b2c3d4e&0&2"));
    }

    [Theory]
    [InlineData(4)]  // REG_DWORD
    [InlineData(3)]  // REG_BINARY
    public void Locate_AGuidValueOfTheWrongType_ReadsAsNothing(int type) {
        FakeConfigurationManagerApi api = TwoDongles();
        api.HardwareKeys[TransmitterNode]["DeviceInterfaceGUIDs"] =
            (type, FakeConfigurationManagerApi.MultiString("{" + InterfaceGuid + "}"));

        Assert.Single(Locator(api).Locate(Vendors, Products, CancellationToken.None));
    }

    [Fact]
    public void Locate_AnEmptyGuidValue_ReadsAsNothing() {
        FakeConfigurationManagerApi api = TwoDongles();
        api.HardwareKeys[TransmitterNode]["DeviceInterfaceGUIDs"] = (RegMultiSz, Array.Empty<char>());

        Assert.Single(Locator(api).Locate(Vendors, Products, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]    // ERROR_SUCCESS, the documented answer to a null buffer
    [InlineData(234)]  // ERROR_MORE_DATA also carries the size
    public void Locate_TakesEitherSizingAnswerFromTheRegistry(int sizingResult) {
        FakeConfigurationManagerApi api = TwoDongles();
        api.QueryValueSizingResult = sizingResult;

        Assert.Equal(2, Locator(api).Locate(Vendors, Products, CancellationToken.None).Count);
    }

    [Fact]
    public void Locate_TheConfigurationManagerBufferSmallCode_IsNotARegistrySizingAnswer() {
        // CR_BUFFER_SMALL (26) is a configuration-manager code; from the registry it is some other
        // failure and must not be taken as "sized".
        FakeConfigurationManagerApi api = TwoDongles();
        var log = new FakeLogger();
        api.QueryValueSizingResult = 26;

        Assert.Empty(Locator(api, log).Locate(Vendors, Products, CancellationToken.None));
        Assert.Contains(log.Messages, m => m.Contains("reading DeviceInterfaceGUIDs failed for USB\\VID_0416&PID_8040\\6&1b2c3d4e&0&2 (code 26)"));
    }

    [Fact]
    public void Locate_ARegistryReadThatFails_ReadsAsNothing() {
        FakeConfigurationManagerApi api = TwoDongles();
        var log = new FakeLogger();
        api.QueryValueReadResult = FakeConfigurationManagerApi.ErrorMoreData;

        Assert.Empty(Locator(api, log).Locate(Vendors, Products, CancellationToken.None));
        Assert.Contains(log.Messages, m => m.Contains("reading DeviceInterfaceGUIDs failed for USB\\VID_0416&PID_8040\\6&1b2c3d4e&0&2 (code 234)"));
    }

    [Fact]
    public void Locate_NoInterfaceOfThatClass_IsSkipped() {
        FakeConfigurationManagerApi api = TwoDongles();
        api.Interfaces.Remove((InterfaceGuid, Transmitter));

        Assert.Single(Locator(api).Locate(Vendors, Products, CancellationToken.None));
    }

    [Fact]
    public void Locate_InterfaceListSizingFails_IsSkipped() {
        FakeConfigurationManagerApi api = TwoDongles();
        var log = new FakeLogger();
        api.InterfaceListSizeResult = FakeConfigurationManagerApi.CrFailure;

        Assert.Empty(Locator(api, log).Locate(Vendors, Products, CancellationToken.None));
        Assert.Contains(log.Messages, m => m.Contains("sizing the interface list failed for USB\\VID_0416&PID_8040\\6&1b2c3d4e&0&2 (code 19)"));
    }

    [Fact]
    public void Locate_InterfaceListOfZeroLength_IsSkipped() {
        FakeConfigurationManagerApi api = TwoDongles();
        api.InterfaceListLengthOverride = 0;

        Assert.Empty(Locator(api).Locate(Vendors, Products, CancellationToken.None));
    }

    [Fact]
    public void Locate_InterfaceListFillFails_IsSkipped() {
        FakeConfigurationManagerApi api = TwoDongles();
        var log = new FakeLogger();
        api.InterfaceListResults.Enqueue(FakeConfigurationManagerApi.CrFailure);

        Assert.Equal(ReceiverPath, Assert.Single(Locator(api, log).Locate(Vendors, Products, CancellationToken.None)).DevicePath);
        Assert.Contains(log.Messages, m => m.Contains("listing the interfaces failed for USB\\VID_0416&PID_8040\\6&1b2c3d4e&0&2 (code 19)"));
    }

    [Fact]
    public void Locate_AnInterfaceListThatGrewMidRead_IsSizedAgain() {
        FakeConfigurationManagerApi api = TwoDongles();
        api.InterfaceListResults.Enqueue(FakeConfigurationManagerApi.CrBufferSmall);

        Assert.Equal(2, Locator(api).Locate(Vendors, Products, CancellationToken.None).Count);
        Assert.Equal(3, api.InterfaceListQueries.Count);
    }

    [Fact]
    public void Locate_AnInterfaceListThatKeepsGrowing_IsGivenUpOnAfterThreeAttempts() {
        FakeConfigurationManagerApi api = TwoDongles();
        var log = new FakeLogger();
        for (int i = 0; i < 3; i++) {
            api.InterfaceListResults.Enqueue(FakeConfigurationManagerApi.CrBufferSmall);
        }

        Assert.Equal(ReceiverPath, Assert.Single(Locator(api, log).Locate(Vendors, Products, CancellationToken.None)).DevicePath);
        Assert.Equal(4, api.InterfaceListQueries.Count);
        Assert.Contains(log.Messages, m => m.Contains("listing the interfaces (it kept growing) failed for USB\\VID_0416&PID_8040\\6&1b2c3d4e&0&2"));
    }

    [Fact]
    public void Locate_AnIdListThatGrewMidRead_IsSizedAgain() {
        FakeConfigurationManagerApi api = TwoDongles();
        api.DeviceIdListResults.Enqueue(FakeConfigurationManagerApi.CrBufferSmall);

        Assert.Equal(2, Locator(api).Locate(Vendors, Products, CancellationToken.None).Count);
        Assert.Equal(2, api.DeviceIdListQueries.Count);
    }

    [Fact]
    public void Locate_AnIdListThatKeepsGrowing_IsGivenUpOnAfterThreeAttempts() {
        FakeConfigurationManagerApi api = TwoDongles();
        var log = new FakeLogger();
        for (int i = 0; i < 3; i++) {
            api.DeviceIdListResults.Enqueue(FakeConfigurationManagerApi.CrBufferSmall);
        }

        Assert.Empty(Locator(api, log).Locate(Vendors, Products, CancellationToken.None));
        Assert.Equal(3, api.DeviceIdListQueries.Count);
        Assert.Contains(log.Messages, m => m.Contains("listing the USB devices (it kept growing) failed for USB"));
    }

    [Fact]
    public void Locate_AnIdListFillThatFails_FindsNothing() {
        FakeConfigurationManagerApi api = TwoDongles();
        var log = new FakeLogger();
        api.DeviceIdListResults.Enqueue(FakeConfigurationManagerApi.CrFailure);

        Assert.Empty(Locator(api, log).Locate(Vendors, Products, CancellationToken.None));
        Assert.Contains(log.Messages, m => m.Contains("listing the USB devices failed for USB (code 19)"));
    }

    [Fact]
    public void Locate_IdListSizingFails_FindsNothing() {
        FakeConfigurationManagerApi api = TwoDongles();
        var log = new FakeLogger();
        api.DeviceIdListSizeResult = FakeConfigurationManagerApi.CrFailure;

        Assert.Empty(Locator(api, log).Locate(Vendors, Products, CancellationToken.None));
        Assert.Contains(log.Messages, m => m.Contains("sizing the USB device list failed for USB (code 19)"));
    }

    [Fact]
    public void Locate_AnIdListOfZeroLength_FindsNothing() {
        FakeConfigurationManagerApi api = TwoDongles();
        api.DeviceIdListLengthOverride = 0;

        Assert.Empty(Locator(api).Locate(Vendors, Products, CancellationToken.None));
    }

    // Every configuration-manager call the dongle walk makes. The walk runs inside the bounded scan, so
    // one given up on while any of these is in flight makes no further call - except closing a key it
    // has open, which must never leak.
    [Theory]
    [InlineData("GetDeviceIdListSize")]
    [InlineData("GetDeviceIdList")]
    [InlineData("LocateDeviceNode")]
    [InlineData("OpenDeviceNodeKey")]
    [InlineData("QueryValue")]
    [InlineData("GetDeviceInterfaceListSize")]
    [InlineData("GetDeviceInterfaceList")]
    [InlineData("GetDeviceInterfaceProperty")]
    public void Locate_GivenUpOnDuringACall_MakesNoFurtherCall_ButStillClosesTheKey(string blocked) {
        FakeConfigurationManagerApi api = TwoDongles();
        api.InterfaceInstances[TransmitterPath] = Transmitter;
        using var abandonment = new CancellationTokenSource();
        var calls = new List<string>();
        api.OnCall = name => {
            calls.Add(name);
            if (name == blocked) {
                abandonment.Cancel();
            }
        };

        Assert.Throws<OperationCanceledException>(() => Locator(api).Locate(Vendors, Products, abandonment.Token));

        Assert.All(calls.Skip(calls.IndexOf(blocked) + 1), name => Assert.Equal("CloseKey", name));
        Assert.Equal(api.KeyOpens.Count, api.ClosedKeys.Count);
    }

    [Fact]
    public void Locate_NoUsbDevices_FindsNothing()
        => Assert.Empty(Locator(new FakeConfigurationManagerApi()).Locate(Vendors, Products, CancellationToken.None));

    [Fact]
    public void Locate_NullAllowLists_Throw() {
        WinUsbDeviceLocator locator = Locator(TwoDongles());

        Assert.Throws<ArgumentNullException>(() => locator.Locate(null!, Products, CancellationToken.None));
        Assert.Throws<ArgumentNullException>(() => locator.Locate(Vendors, null!, CancellationToken.None));
    }

    [Fact]
    public void Constructor_NullDependencies_Throw() {
        var api = new FakeConfigurationManagerApi();

        Assert.Throws<ArgumentNullException>(() => new WinUsbDeviceLocator(null!, new ContainerIdResolver(api), new FakeLogger()));
        Assert.Throws<ArgumentNullException>(() => new WinUsbDeviceLocator(api, null!, new FakeLogger()));
        Assert.Throws<ArgumentNullException>(() => new WinUsbDeviceLocator(api, new ContainerIdResolver(api), null!));
    }
}
