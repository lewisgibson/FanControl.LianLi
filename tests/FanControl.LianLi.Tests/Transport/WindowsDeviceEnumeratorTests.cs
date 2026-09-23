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
/// The enumerator's decisions: that a scan walks the dongles and the HID interfaces under one bound
/// and returns them in that order, how an open is routed and what it reads first, and what a scan or
/// open that outlives its bound becomes.
/// </summary>
public class WindowsDeviceEnumeratorTests {
    private const string UniPath = @"\\?\hid#vid_0cf2&pid_a102#uni";
    private const string TransmitterInstance = @"USB\VID_0416&PID_8040\6&1&0&2";
    private const string TransmitterPath = @"\\?\usb#vid_0416&pid_8040#6&1&0&2#{guid}";
    private static readonly Guid DongleInterface = new Guid("2c63e7a4-6d6e-4b0e-8f3d-1d8e5f0a9b71");
    private static readonly IReadOnlyList<int> Vendors = new[] { 0x0CF2, 0x0416 };
    private static readonly IReadOnlyList<int> Products = new[] { 0xA102, 0x7372, 0x8040 };
    private static readonly HidInterface Uni = new HidInterface(0x0CF2, 0xA102, UniPath, new HidCapabilities(0xFF72, 65, 353, 65));

    private readonly FakeHidApi _hid = new FakeHidApi();
    private readonly FakeWinUsbApi _winUsb = new FakeWinUsbApi();
    private readonly FakeConfigurationManagerApi _configurationManager = new FakeConfigurationManagerApi();
    private readonly FakeDeviceCallRunner _calls = new FakeDeviceCallRunner();
    private readonly FakeDeviceCallClock _clock = new FakeDeviceCallClock();
    private readonly FakeTransferDelay _delay = new FakeTransferDelay();
    private readonly FakeLogger _log = new FakeLogger();
    private readonly DeviceCallGate _gate;

    private WindowsDeviceEnumerator Enumerator()
        => new WindowsDeviceEnumerator(_log, _hid, _winUsb, _configurationManager, _gate, _delay);

    public WindowsDeviceEnumeratorTests() {
        _gate = new DeviceCallGate(_calls, _clock, _log);
    }

    private void AddTransmitter() {
        _configurationManager.DeviceIds.Add(TransmitterInstance);
        _configurationManager.DeviceNodes[TransmitterInstance] = 5;
        _configurationManager.HardwareKeys[5] = new Dictionary<string, (int Type, char[] Data)> {
            ["DeviceInterfaceGUIDs"] = (7, FakeConfigurationManagerApi.MultiString("{" + DongleInterface + "}")),
        };
        _configurationManager.Interfaces[(DongleInterface, TransmitterInstance)] = new[] { TransmitterPath };
    }

    [Fact]
    public void Locate_ReturnsTheDongles_ThenTheHidInterfaces_UnderTheScanBound() {
        AddTransmitter();
        _configurationManager.Interfaces[(FakeHidApi.HidClass, null)] = new[] { UniPath };
        _hid.Attributes[UniPath] = (0x0CF2, 0xA102);

        IReadOnlyList<LocatedDevice> located = Enumerator().Locate(Vendors, Products);

        Assert.Collection(
            located,
            d => { Assert.Equal(TransmitterPath, d.DevicePath); Assert.Null(d.Device); },
            d => { Assert.Equal(UniPath, d.DevicePath); Assert.NotNull(d.Device); });
        Assert.Equal("device scan", Assert.Single(_calls.Operations));
        Assert.Equal(5000, _calls.TimeoutOf("device scan"));
    }

    [Fact]
    public void Locate_OutlivingItsBound_Throws() {
        _calls.TimesOut = operation => operation == "device scan";

        IOException failure = Assert.Throws<IOException>(() => Enumerator().Locate(Vendors, Products));

        Assert.Equal(
            "Device scan timed out after 5000 ms; a device is not answering Windows (still resuming?)",
            failure.Message);
    }

    [Fact]
    public void Locate_AScanGivenUpOnBeforeItStarted_CallsNothing() {
        AddTransmitter();
        _calls.TimesOut = operation => operation == "device scan";
        Assert.Throws<IOException>(() => Enumerator().Locate(Vendors, Products));

        Assert.Throws<OperationCanceledException>(() => Assert.Single(_calls.Abandoned)(FakeDeviceCallRunner.Cancelled));

        Assert.Empty(_configurationManager.DeviceIdListQueries);
        Assert.Empty(_hid.Calls);
    }

    [Fact]
    public void Locate_GivenUpOnWhileADeviceIsOpened_GoesNoFurther() {
        _configurationManager.Interfaces[(FakeHidApi.HidClass, null)] = new[] { UniPath, UniPath + "-second" };
        _hid.Attributes[UniPath] = (0x0CF2, 0xA102);
        _hid.OnCall = call => {
            if (call.StartsWith("OpenQueryHandle", StringComparison.Ordinal)) {
                _calls.AbandonRunning();
            }
        };

        Assert.Throws<IOException>(() => Enumerator().Locate(Vendors, Products));

        Assert.Equal("OpenQueryHandle " + UniPath, _hid.Calls.Last());
        Assert.Equal(1, _hid.Handles[0].Releases);
        Assert.Empty(_calls.LateFailures);
    }

    [Fact]
    public void Locate_WhileAnEarlierScanHasNotReturned_IsRefusedWithoutScanning_UntilItDoes() {
        WindowsDeviceEnumerator enumerator = Enumerator();
        _calls.TimesOut = operation => operation == "device scan";
        Assert.Throws<IOException>(() => enumerator.Locate(Vendors, Products));
        _calls.TimesOut = _ => false;
        _clock.NowMilliseconds = 5000;

        IOException refused = Assert.Throws<IOException>(() => enumerator.Locate(Vendors, Products));

        Assert.Equal("device scan not started: the earlier device scan, given up on 5000 ms ago, has not returned.", refused.Message);
        Assert.Single(_calls.Operations);
        Assert.Equal(
            "  device scan refused: the earlier device scan, given up on 5000 ms ago, has not returned; every call to the device scan fails fast until it does (logged once until then)",
            Assert.Single(_log.Messages));

        // A scan still out holds back only the next scan, not an open.
        using (enumerator.Open(new LocatedDevice(0x0CF2, 0xA102, UniPath, Uni))) {
        }

        Assert.Throws<OperationCanceledException>(() => Assert.Single(_calls.Abandoned)(FakeDeviceCallRunner.Cancelled));
        Assert.Empty(enumerator.Locate(Vendors, Products));
        Assert.Equal(3, _calls.Operations.Count(operation => operation == "device scan" || operation.StartsWith("open of", StringComparison.Ordinal)));
    }

    [Fact]
    public void Locate_NullAllowLists_Throw() {
        WindowsDeviceEnumerator enumerator = Enumerator();

        Assert.Throws<ArgumentNullException>(() => enumerator.Locate(null!, Products));
        Assert.Throws<ArgumentNullException>(() => enumerator.Locate(Vendors, null!));
    }

    [Fact]
    public void Open_AHidInterfaceTheScanRead_OpensAHidTransportWithoutReadingItAgain_UnderTheOpenBound() {
        using IDeviceTransport transport = Enumerator().Open(new LocatedDevice(0x0CF2, 0xA102, UniPath, Uni));

        Assert.IsType<HidTransport>(Assert.IsType<ClaimedTransport>(transport).Inner);
        Assert.Equal("OpenStreamHandle " + UniPath, _hid.Calls[0]);
        Assert.Equal(2000, _calls.TimeoutOf("open of " + UniPath));
    }

    [Fact]
    public void Open_ADongle_OpensAWinUsbTransport() {
        using IDeviceTransport transport = Enumerator().Open(new LocatedDevice(0x0416, 0x8040, TransmitterPath, null));

        Assert.IsType<WinUsbTransport>(Assert.IsType<ClaimedTransport>(transport).Inner);
        Assert.Equal("OpenDevice " + TransmitterPath, _winUsb.Calls[0]);
    }

    [Fact]
    public void Open_ARememberedHidDevice_ReadsItsCapabilitiesFromItsPath_ThenOpens() {
        _hid.Capabilities[UniPath] = new HidCapabilities(0xFF72, 65, 353, 65);

        using IDeviceTransport transport = Enumerator().Open(new LocatedDevice(0x0CF2, 0xA102, UniPath, null));

        Assert.IsType<HidTransport>(Assert.IsType<ClaimedTransport>(transport).Inner);
        Assert.Equal(new[] { "OpenQueryHandle " + UniPath, "HidD_GetPreparsedData query 0", "OpenStreamHandle " + UniPath }, _hid.Calls.Take(3));
        transport.Write(new byte[] { 0x01 });
        Assert.Equal(353, _hid.Written[0].Length);
    }

    [Fact]
    public void Open_ARememberedHidDeviceThatIsNotThere_Throws() {
        _hid.QueryOpenErrors[UniPath] = 2;

        IOException failure = Assert.Throws<IOException>(
            () => Enumerator().Open(new LocatedDevice(0x0CF2, 0xA102, UniPath, null)));

        Assert.Equal("No HID device is present at " + UniPath, failure.Message);
    }

    [Fact]
    public void Open_ARefusedOpen_Propagates() {
        _hid.StreamOpenErrors.Enqueue(5);

        Assert.Throws<IOException>(() => Enumerator().Open(new LocatedDevice(0x0CF2, 0xA102, UniPath, Uni)));
    }

    [Fact]
    public void Open_OutlivingItsBound_Throws_AndATransportOpenedLateIsDisposed() {
        _calls.TimesOut = operation => operation.StartsWith("open of", StringComparison.Ordinal);

        IOException failure = Assert.Throws<IOException>(
            () => Enumerator().Open(new LocatedDevice(0x0CF2, 0xA102, UniPath, Uni)));

        Assert.Equal(
            "Open of device at " + UniPath + " timed out after 2000 ms; device not answering Windows.",
            failure.Message);
        Assert.Single(_calls.Abandoned)(CancellationToken.None);
        Assert.All(_hid.Handles, handle => Assert.Equal(1, handle.Releases));
    }

    [Fact]
    public void Open_GivenUpOnBeforeItStarted_OpensNothing() {
        _calls.TimesOut = operation => operation.StartsWith("open of", StringComparison.Ordinal);
        Assert.Throws<IOException>(() => Enumerator().Open(new LocatedDevice(0x0CF2, 0xA102, UniPath, null)));

        Assert.Throws<OperationCanceledException>(() => Assert.Single(_calls.Abandoned)(FakeDeviceCallRunner.Cancelled));

        Assert.Empty(_hid.Calls);
    }

    [Fact]
    public void Open_WhileAnEarlierOpenOfThatPathHasNotReturned_IsRefused_ButOtherPathsOpen() {
        WindowsDeviceEnumerator enumerator = Enumerator();
        _calls.TimesOut = operation => operation == "open of " + UniPath;
        Assert.Throws<IOException>(() => enumerator.Open(new LocatedDevice(0x0CF2, 0xA102, UniPath, Uni)));
        _calls.TimesOut = _ => false;

        IOException refused = Assert.Throws<IOException>(
            () => enumerator.Open(new LocatedDevice(0x0CF2, 0xA102, UniPath.ToUpperInvariant(), Uni)));

        Assert.StartsWith("open of " + UniPath.ToUpperInvariant() + " not started: the earlier open of " + UniPath + ",", refused.Message);
        Assert.Empty(_hid.Calls);
        using (IDeviceTransport dongle = enumerator.Open(new LocatedDevice(0x0416, 0x8040, TransmitterPath, null))) {
            Assert.IsType<WinUsbTransport>(Assert.IsType<ClaimedTransport>(dongle).Inner);
        }

        Assert.Single(_calls.Abandoned)(CancellationToken.None);
        using IDeviceTransport transport = enumerator.Open(new LocatedDevice(0x0CF2, 0xA102, UniPath, Uni));
        Assert.IsType<HidTransport>(Assert.IsType<ClaimedTransport>(transport).Inner);
    }

    [Fact]
    public void Open_WhileATransportItOpenedIsStuckReopeningThatPath_IsRefused() {
        // The transport shares the enumerator's gate, so a reopen still out on a path holds back a
        // fresh open of the same path - the one a stand-in's rebuild would make next.
        WindowsDeviceEnumerator enumerator = Enumerator();
        IDeviceTransport transport = enumerator.Open(new LocatedDevice(0x0CF2, 0xA102, UniPath, Uni));
        _hid.TransferResults.Enqueue(1167);
        Assert.Throws<IOException>(() => transport.SetFeature(new byte[] { 0xE0 }));
        _calls.TimesOut = operation => operation.StartsWith("reopen", StringComparison.Ordinal);
        Assert.Throws<IOException>(() => transport.SetFeature(new byte[] { 0xE0 }));
        _calls.TimesOut = _ => false;
        transport.Dispose(); // its owner lets go, so only the stuck reopen holds the path back

        IOException refused = Assert.Throws<IOException>(
            () => enumerator.Open(new LocatedDevice(0x0CF2, 0xA102, UniPath, Uni)));

        Assert.Equal(
            "open of " + UniPath + " not started: the earlier reopen on " + UniPath + ", given up on 0 ms ago, has not returned.",
            refused.Message);
    }

    // One owner per device path in the process: a controller from before FanControl's refresh that
    // has not closed its device yet keeps the next one from opening it.
    [Fact]
    public void Open_OfAPathAnotherOwnerStillHasOpen_IsRefused_UntilItIsClosed() {
        WindowsDeviceEnumerator enumerator = Enumerator();
        IDeviceTransport first = enumerator.Open(new LocatedDevice(0x0CF2, 0xA102, UniPath, Uni));

        IOException refused = Assert.Throws<IOException>(() => enumerator.Open(new LocatedDevice(0x0CF2, 0xA102, UniPath, Uni)));
        Assert.Equal(
            UniPath + " is still open for a controller from before FanControl's last refresh; it is opened once that has closed it.",
            refused.Message);

        first.Dispose();
        first.Dispose();
        using IDeviceTransport second = enumerator.Open(new LocatedDevice(0x0CF2, 0xA102, UniPath, Uni));
        Assert.IsType<ClaimedTransport>(second);
    }

    // An open that fails lets go of the claim, so the path can be tried again.
    [Fact]
    public void Open_ThatFails_LetsGoOfThePath() {
        WindowsDeviceEnumerator enumerator = Enumerator();
        _calls.TimesOut = _ => true;
        Assert.Throws<IOException>(() => enumerator.Open(new LocatedDevice(0x0CF2, 0xA102, UniPath, Uni)));
        _calls.TimesOut = _ => false;
        _calls.Abandoned.Clear();

        Assert.True(_gate.TryClaim(UniPath));
        _gate.Release(UniPath);
        Assert.Throws<ArgumentNullException>(() => _gate.TryClaim(null!));
    }

    [Fact]
    public void Open_NullDevice_Throws()
        => Assert.Throws<ArgumentNullException>(() => Enumerator().Open(null!));

    [Fact]
    public void Constructor_ValidatesItsDependencies() {
        Assert.Throws<ArgumentNullException>(() => new WindowsDeviceEnumerator(null!, _hid, _winUsb, _configurationManager, _gate, _delay));
        Assert.Throws<ArgumentNullException>(() => new WindowsDeviceEnumerator(_log, null!, _winUsb, _configurationManager, _gate, _delay));
        Assert.Throws<ArgumentNullException>(() => new WindowsDeviceEnumerator(_log, _hid, null!, _configurationManager, _gate, _delay));
        Assert.Throws<ArgumentNullException>(() => new WindowsDeviceEnumerator(_log, _hid, _winUsb, null!, _gate, _delay));
        Assert.Throws<ArgumentNullException>(() => new WindowsDeviceEnumerator(_log, _hid, _winUsb, _configurationManager, null!, _delay));
        Assert.Throws<ArgumentNullException>(() => new WindowsDeviceEnumerator(_log, _hid, _winUsb, _configurationManager, _gate, null!));
    }

    [Fact]
    public void Constructor_OverWindows_NeedsOnlyALog() {
        Assert.NotNull(new WindowsDeviceEnumerator(new FakeLogger()));
        Assert.Throws<ArgumentNullException>(() => new WindowsDeviceEnumerator(null!));
    }
}
