using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using FanControl.LianLi.Tests.Fakes;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

/// <summary>
/// The native adapters against the real Windows APIs, in ways that need no Lian Li hardware: the
/// device lists the locators walk, the attributes and capabilities of whatever HID interfaces the
/// machine has, an overlapped read cancelled and completed, the failures for a path or instance id
/// that does not exist, and a blocked synchronous read cancelled by thread. What these catch is a
/// marshalling slip - a struct layout, a string encoding, a return type - that the fakes cannot.
/// Nothing here writes to any device.
/// </summary>
public class WindowsNativeApiTests {
    private const string NowherePath = @"\\?\hid#vid_ffff&pid_ffff#0&0&0&0#{4d1e55b2-f16f-11cf-88cb-001111000030}";
    private const string NowhereInstance = @"USB\VID_FFFF&PID_FFFF\NOWHERE";

    // ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND, either of which Windows gives for a device path
    // nothing is listening on.
    private static readonly int[] NotThere = { 2, 3 };

    // CR_NO_SUCH_DEVNODE and ERROR_OPERATION_ABORTED.
    private const int CrNoSuchDeviceNode = 13;
    private const int ErrorOperationAborted = 995;

    private static readonly WindowsHidApi Hid = WindowsHidApi.Instance;
    private static readonly WindowsConfigurationManagerApi ConfigurationManager = WindowsConfigurationManagerApi.Instance;

    private static IReadOnlyList<string> HidInterfaces() {
        var failures = new List<(string Step, int Code)>();
        IReadOnlyList<string>? paths = DeviceInterfaceListReader.Read(
            ConfigurationManager, Hid.GetInterfaceClass(), null, (step, code) => failures.Add((step, code)), CancellationToken.None);

        Assert.Empty(failures);
        return paths!;
    }

    [WindowsFact]
    public void HidApi_ReportsTheHidInterfaceClass()
        => Assert.Equal(new Guid("4d1e55b2-f16f-11cf-88cb-001111000030"), Hid.GetInterfaceClass());

    [WindowsFact]
    public void HidApi_OpeningAPathNothingIsOn_FailsWithTheWin32Error() {
        Assert.Null(Hid.OpenQueryHandle(NowherePath, out int query));
        Assert.Null(Hid.OpenControlHandle(NowherePath, out int control));
        Assert.Null(Hid.OpenStreamHandle(NowherePath, out int stream));

        Assert.Contains(query, NotThere);
        Assert.Contains(control, NotThere);
        Assert.Contains(stream, NotThere);
    }

    [WindowsFact]
    public void HidApi_ReadsTheAttributesAndCapabilitiesOfEveryHidInterfaceThatOpens() {
        foreach (string path in HidInterfaces()) {
            using SafeHandle? handle = Hid.OpenQueryHandle(path, out _);
            if (handle is null) {
                continue;
            }

            Assert.True(Hid.GetAttributes(handle, out int vendorId, out int productId, out int attributeError), path);
            Assert.Equal(0, attributeError);
            if (UsbDevicePath.TryParseIds(path, out int pathVendorId, out int pathProductId)) {
                Assert.Equal((pathVendorId, pathProductId), (vendorId, productId));
            }

            Assert.True(HidDeviceLocator.ReadCapabilities(Hid, handle, out HidCapabilities capabilities, out int capabilityError, CancellationToken.None), path);
            Assert.Equal(0, capabilityError);
            Assert.NotEqual(0, capabilities.UsagePage);
            Assert.True(
                capabilities.InputReportLength > 0 || capabilities.OutputReportLength > 0 || capabilities.FeatureReportLength > 0,
                path);
        }
    }

    [WindowsFact]
    public void HidApi_AnOverlappedReadThatIsCancelled_Completes_AndSaysItWasAborted() {
        // Only vendor-defined collections, which no input stack claims, and only reads: nothing any
        // other program relies on is touched. A read that a device happens to answer is fine too.
        foreach (string path in HidInterfaces()) {
            HidCapabilities capabilities;
            using (SafeHandle? query = Hid.OpenQueryHandle(path, out _)) {
                if (query is null || !HidDeviceLocator.ReadCapabilities(Hid, query, out capabilities, out _, CancellationToken.None)
                    || capabilities.UsagePage < 0xFF00 || capabilities.InputReportLength <= 0) {
                    continue;
                }
            }

            using SafeHandle? stream = Hid.OpenStreamHandle(path, out _);
            if (stream is null) {
                continue;
            }

            byte[] buffer = new byte[capabilities.InputReportLength];
            using IHidTransfer transfer = Hid.BeginRead(stream, buffer);
            if (transfer.Wait(100)) {
                Assert.True(transfer.GetResult(out int transferred, out int readError) || readError != 0, path);
                Assert.InRange(transferred, 0, buffer.Length);
                continue;
            }

            Assert.True(transfer.Cancel(out int cancelError), path + " cancel error " + cancelError);
            Assert.True(transfer.Wait(1000), path);
            Assert.False(transfer.GetResult(out _, out int error));
            Assert.Equal(ErrorOperationAborted, error);
        }
    }

    [WindowsFact]
    public void HidDeviceLocator_OverTheRealApis_ReadsEveryUsbHidInterfaceTheMachineHas() {
        IReadOnlyList<string> paths = HidInterfaces();
        var vendorIds = new List<int>();
        var productIds = new List<int>();
        foreach (string path in paths) {
            if (UsbDevicePath.TryParseIds(path, out int vendorId, out int productId)) {
                vendorIds.Add(vendorId);
                productIds.Add(productId);
            }
        }

        var log = new FakeLogger();
        var locator = new HidDeviceLocator(Hid, ConfigurationManager, new ContainerIdResolver(ConfigurationManager), log);

        IReadOnlyList<LocatedDevice> located = locator.Locate(vendorIds, productIds, CancellationToken.None);

        Assert.DoesNotContain(log.Messages, message => message.StartsWith("  HID scan:", StringComparison.Ordinal));
        foreach (LocatedDevice device in located) {
            Assert.Contains(device.DevicePath, paths.Select(path => path.ToLowerInvariant()));
            Assert.NotNull(device.ContainerId);
            if (device.Device != null) {
                Assert.Equal(device.Device.Capabilities.OutputReportLength, device.MaxOutputReportLength);
            }
        }
    }

    [WindowsFact]
    public void WindowsDeviceEnumerator_ScansTheMachine_WithinItsBound() {
        var log = new FakeLogger();

        IReadOnlyList<LocatedDevice> located = new WindowsDeviceEnumerator(log)
            .Locate(new[] { 0x0CF2, 0x0416, 0x1A86 }, new[] { 0xA100, 0xA101, 0xA102, 0xA103, 0xA104, 0xA105, 0x7372, 0x7371, 0x8040, 0x8041, 0xE304, 0xE305 });

        Assert.All(located, device => Assert.False(string.IsNullOrEmpty(device.DevicePath)));
        Assert.DoesNotContain(log.Messages, message => message.Contains("scan:", StringComparison.Ordinal));
    }

    [WindowsFact]
    public void ConfigurationManager_AnInstanceIdNothingIsOn_IsNoSuchDeviceNode() {
        Assert.Equal(CrNoSuchDeviceNode, ConfigurationManager.LocateDeviceNode(out _, NowhereInstance, 0));
        Assert.Null(new ContainerIdResolver(ConfigurationManager).Resolve(NowherePath, CancellationToken.None));
    }

    [WindowsFact]
    public void ConfigurationManager_AnInterfaceClassNothingIsRegisteredUnder_ListsNothing() {
        var failures = new List<(string Step, int Code)>();

        IReadOnlyList<string>? paths = DeviceInterfaceListReader.Read(
            ConfigurationManager, Guid.NewGuid(), null, (step, code) => failures.Add((step, code)), CancellationToken.None);

        Assert.Empty(failures);
        Assert.Empty(paths!);
    }

    [WindowsFact]
    public void ConfigurationManager_ListsThePresentUsbDevices_ForTheDongleWalk() {
        var log = new FakeLogger();
        var locator = new WinUsbDeviceLocator(ConfigurationManager, new ContainerIdResolver(ConfigurationManager), log);

        _ = locator.Locate(new[] { 0x0416, 0x1A86 }, new[] { 0x8040, 0x8041, 0xE304, 0xE305 }, CancellationToken.None);

        Assert.DoesNotContain(log.Messages, message => message.Contains("the USB devices", StringComparison.Ordinal));
    }

    // The dongle walk's registry read, over the real calls, on whatever USB devices the machine has:
    // it opens each one's hardware key read-only and asks for a value, which opens no device.
    [WindowsFact]
    public void ConfigurationManager_ReadsTheHardwareKeyOfAPresentUsbDevice() {
        const uint filterPresentUsb = 0x00000001 | 0x00000100; // CM_GETIDLIST_FILTER_ENUMERATOR | _PRESENT
        Assert.Equal(0, ConfigurationManager.GetDeviceIdListSize(out uint length, "USB", filterPresentUsb));
        var buffer = new char[length];
        Assert.Equal(0, ConfigurationManager.GetDeviceIdList("USB", buffer, length, filterPresentUsb));
        string[] ids = new string(buffer).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(ids);

        int opened = 0;
        foreach (string id in ids.Take(10)) {
            if (ConfigurationManager.LocateDeviceNode(out uint node, id, 0) != 0) {
                continue;
            }

            // KEY_READ, the current hardware profile, RegDisposition_OpenExisting, CM_REGISTRY_HARDWARE.
            if (ConfigurationManager.OpenDeviceNodeKey(node, 0x20019, 0, 1, out IntPtr key, 0) != 0) {
                continue;
            }

            try {
                opened++;
                uint size = 0;
                int queried = ConfigurationManager.QueryValue(key, "DeviceInterfaceGUIDs", out int type, null, ref size);
                Assert.True(queried == 0 || queried == 2, "RegQueryValueExW returned " + queried); // present, or ERROR_FILE_NOT_FOUND
                if (queried == 0) {
                    var data = new char[(size / 2) + 1];
                    Assert.Equal(0, ConfigurationManager.QueryValue(key, "DeviceInterfaceGUIDs", out type, data, ref size));
                    Assert.True(type == 1 || type == 7); // REG_SZ or REG_MULTI_SZ
                }
            } finally {
                ConfigurationManager.CloseKey(key);
            }
        }

        Assert.True(opened > 0, "no present USB device's hardware key opened");
    }

    [WindowsFact]
    public void WinUsbApi_OpeningAPathNothingIsOn_FailsWithTheWin32Error() {
        Assert.Null(WindowsWinUsbApi.Instance.OpenDevice(@"\\?\usb#vid_ffff&pid_ffff#nowhere#{2c63e7a4-6d6e-4b0e-8f3d-1d8e5f0a9b71}", out int error));

        Assert.Contains(error, NotThere);
    }

    [WindowsFact]
    public void ThreadCanceller_CancelsASynchronousReadBlockedOnAPipe_ByThread() {
        // An anonymous pipe nobody writes to blocks a synchronous ReadFile for good: the shape of a
        // wedged device's synchronous IOCTL. The bound gives up, CancelSynchronousIo reaches the
        // blocked read, and the abandoned call's failure is reported rather than lost.
        using var server = new AnonymousPipeServerStream(PipeDirection.In);
        using var client = new AnonymousPipeClientStream(PipeDirection.Out, server.ClientSafePipeHandle);
        using var reported = new ManualResetEventSlim(false);
        Exception? late = null;

        bool completed = BoundedDeviceCall.TryRun(
            _ => server.ReadByte(),
            100,
            () => { },
            failure => { late = failure; reported.Set(); },
            () => { });

        Assert.False(completed);
        Assert.True(reported.Wait(5000, TestContext.Current.CancellationToken));
        Assert.NotNull(late);
    }
}
