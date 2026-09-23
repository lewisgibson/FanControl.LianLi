using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Plugin;
using FanControl.LianLi.Tests.Fakes;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Plugin;

/// <summary>
/// The file the remembered controllers are kept in between runs: what it writes reads back exactly,
/// and anything else is logged and treated as nothing.
/// </summary>
public sealed class RememberedControllerFileTests : IDisposable {
    private static readonly DateTime Seen = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lianli-remembered-" + Guid.NewGuid().ToString("N"));
    private readonly FakeLogger _log = new FakeLogger();

    public void Dispose() {
        if (Directory.Exists(_directory)) {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private RememberedControllerFile File() => new RememberedControllerFile(_directory);

    // A load that must have read the file (or found none).
    private IReadOnlyList<StoredController> Read() {
        IReadOnlyList<StoredController>? read = File().Load(_log);
        Assert.NotNull(read);
        return read;
    }

    private static StoredController Wired()
        => new StoredController(
            @"\\?\hid#vid_0cf2&pid_a102",
            RememberedFixture.Of(
                new ControllerPlan(DeviceKind.UniFan, new LocatedDevice(0x0CF2, 0xA102, @"\\?\hid#vid_0cf2&pid_a102", null, "{abc}", 65)),
                2,
                new[] { new ChannelDescriptor("LianLi/2/ch0/ctl", "Channel \"1\"", "LianLi/2/ch0/fan", "Channel 1 RPM\t") },
                new[] { new FanSpeedDescriptor("LianLi/2/ch0/fan", "Channel 1 RPM") },
                Array.Empty<TemperatureDescriptor>(),
                Seen),
            Seen);

    private static StoredController Wireless()
        => new StoredController(
            "usb/tx",
            RememberedFixture.Of(
                new ControllerPlan(
                    DeviceKind.WirelessTransmitter,
                    new LocatedDevice(0x0416, 0x8040, "usb/tx", null),
                    new LocatedDevice(0x0416, 0x8041, "usb/rx", null)),
                0,
                new[] { new ChannelDescriptor("LianLi/wd00000000001/pump/ctl", "Pump", "LianLi/wd00000000001/pump/fan", "Pump RPM") },
                new[] { new FanSpeedDescriptor("LianLi/wd00000000001/pump/fan", "Pump RPM") },
                new[] { new TemperatureDescriptor("LianLi/wd00000000001/coolant/temp", "Coolant") },
                Seen),
            Seen);

    private void WriteText(string text) {
        Directory.CreateDirectory(_directory);
        System.IO.File.WriteAllText(File().FilePath, text);
    }

    [Fact]
    public void WhatIsSaved_ReadsBackExactly() {
        File().Save(new[] { Wired(), Wireless() }, _log);

        IReadOnlyList<StoredController> read = Read();

        Assert.Empty(_log.Messages);
        Assert.Equal(2, read.Count);
        StoredController wired = read[0];
        Assert.Equal(@"\\?\hid#vid_0cf2&pid_a102", wired.Key);
        Assert.Equal(Seen, wired.LastSeenUtc);
        Assert.Equal(DateTimeKind.Utc, wired.LastSeenUtc.Kind);
        Assert.Equal(DeviceKind.UniFan, wired.Controller.Plan.Kind);
        Assert.Equal(2, wired.Controller.Index);
        LocatedDevice device = Assert.Single(wired.Controller.Plan.Devices);
        Assert.Equal((0x0CF2, 0xA102, "{abc}", 65), (device.VendorId, device.ProductId, device.ContainerId, device.MaxOutputReportLength));
        Assert.Null(device.Device);
        ChannelDescriptor channel = Assert.Single(wired.Controller.Channels);
        Assert.Equal(("Channel \"1\"", "Channel 1 RPM\t"), (channel.ControlName, channel.RpmName));
        Assert.Equal("LianLi/2/ch0/fan", Assert.Single(wired.Controller.FanSpeeds).Id);

        StoredController wireless = read[1];
        Assert.Equal(new[] { "usb/tx", "usb/rx" }, wireless.Controller.Plan.Devices.Select(d => d.DevicePath));
        Assert.Null(wireless.Controller.Plan.Devices[1].ContainerId);
        Assert.Equal("Coolant", Assert.Single(wireless.Controller.Temperatures).Name);
    }

    [Fact]
    public void Save_ReplacesAnEarlierFile_AndLeavesNoTemporaryFileBehind() {
        File().Save(new[] { Wired(), Wireless() }, _log);

        File().Save(new[] { Wireless() }, _log);

        Assert.Equal("usb/tx", Assert.Single(Read()).Key);
        Assert.Equal(new[] { File().FilePath }, Directory.GetFiles(_directory));
    }

    [Fact]
    public void Save_ThatCannotWrite_IsLogged() {
        // The directory cannot be created where a file already is.
        System.IO.File.WriteAllText(_directory, "not a directory");
        try {
            File().Save(new[] { Wired() }, _log);

            Assert.Contains(_log.Messages, m => m.StartsWith("remembered controllers not saved to ", StringComparison.Ordinal));
        } finally {
            System.IO.File.Delete(_directory);
        }
    }

    [Fact]
    public void Load_WithNoFile_IsEmpty_AndQuiet() {
        Assert.Empty(Read());
        Assert.Empty(_log.Messages);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"version\":2,\"controllers\":[]}")]
    [InlineData("[]")]
    public void Load_AFileThisDidNotWrite_IsLoggedAndEmpty(string text) {
        WriteText(text);

        Assert.Empty(Read());
        Assert.Contains(_log.Messages, m => m.StartsWith("remembered controllers not read from ", StringComparison.Ordinal));
    }

    // Locked (on Windows) or unreadable (elsewhere): worth reading again, so it is reported as null.
    [Fact]
    public void Load_AFileThatCannotBeReadNow_IsNull_AndLogged() {
        File().Save(new[] { Wired() }, _log);
        string path = File().FilePath;
        FileStream? held = null;
        if (OperatingSystem.IsWindows()) {
            held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        } else {
            System.IO.File.SetUnixFileMode(path, UnixFileMode.None);
        }

        try {
            Assert.Null(File().Load(_log));
            Assert.Contains(_log.Messages, m => m.EndsWith("; tried again at the next scan", StringComparison.Ordinal));
        } finally {
            held?.Dispose();
            if (!OperatingSystem.IsWindows()) {
                System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    [Fact]
    public void Load_AnOversizedFile_IsNotRead() {
        WriteText("{\"version\":1,\"controllers\":[]," + new string(' ', 1024 * 1024) + "}");

        Assert.Empty(Read());
        Assert.Contains(_log.Messages, m => m.Contains("larger than 1048576 bytes"));
    }

    [Fact]
    public void Load_AFileWithNoControllerList_IsEmpty() {
        WriteText("{\"version\":1}");

        Assert.Empty(Read());
        Assert.Empty(_log.Messages);
    }

    // Each entry breaks one rule; each is logged and skipped while the good entry is kept.
    [Theory]
    [InlineData("{\"lastSeen\":\"2026-09-01T12:00:00Z\",\"kind\":\"UniFan\",\"index\":0,\"devices\":[]}")]
    [InlineData("{\"key\":\"k\",\"kind\":\"UniFan\",\"index\":0}")]
    [InlineData("{\"key\":\"k\",\"lastSeen\":\"2026-09-01T12:00:00Z\",\"index\":0}")]
    [InlineData("{\"key\":\"k\",\"lastSeen\":\"2026-09-01T12:00:00Z\",\"kind\":\"UniFan\",\"index\":0}")]
    [InlineData("{\"key\":\"k\",\"lastSeen\":\"yesterday\",\"kind\":\"UniFan\",\"index\":0}")]
    [InlineData("{\"key\":\"k\",\"lastSeen\":\"2026-09-01T12:00:00Z\",\"kind\":\"Toaster\",\"index\":0}")]
    [InlineData("{\"key\":\"k\",\"lastSeen\":\"2026-09-01T12:00:00Z\",\"kind\":\"UniFan\"}")]
    [InlineData("{\"key\":\"k\",\"lastSeen\":\"2026-09-01T12:00:00Z\",\"kind\":\"UniFan\",\"index\":-1}")]
    [InlineData("{\"key\":\"k\",\"lastSeen\":\"2026-09-01T12:00:00Z\",\"kind\":\"LightingOnly\",\"index\":0,\"devices\":[{\"vendorId\":1,\"productId\":2,\"path\":\"p\"}]}")]
    [InlineData("{\"key\":\"k\",\"lastSeen\":\"2026-09-01T12:00:00Z\",\"kind\":\"UniFan\",\"index\":0,\"devices\":[]}")]
    [InlineData("{\"key\":\"k\",\"lastSeen\":\"2026-09-01T12:00:00Z\",\"kind\":\"UniFan\",\"index\":0,\"devices\":[{\"vendorId\":1,\"path\":\"p\"}]}")]
    [InlineData("{\"key\":\"k\",\"lastSeen\":\"2026-09-01T12:00:00Z\",\"kind\":\"UniFan\",\"index\":0,\"devices\":[{\"productId\":1,\"path\":\"p\"}]}")]
    [InlineData("{\"key\":\"k\",\"lastSeen\":\"2026-09-01T12:00:00Z\",\"kind\":\"UniFan\",\"index\":0,\"devices\":[{\"vendorId\":1,\"productId\":2}]}")]
    [InlineData("{\"key\":\"k\",\"lastSeen\":\"2026-09-01T12:00:00Z\",\"kind\":\"UniFan\",\"index\":0,\"devices\":[{\"vendorId\":1,\"productId\":2,\"path\":\"p\"}],\"channels\":[{\"controlId\":\"a\",\"controlName\":\"b\",\"rpmId\":\"c\"}]}")]
    [InlineData("{\"key\":\"k\",\"lastSeen\":\"2026-09-01T12:00:00Z\",\"kind\":\"UniFan\",\"index\":0,\"devices\":[{\"vendorId\":1,\"productId\":2,\"path\":\"p\"}],\"channels\":[{\"controlId\":\"a\",\"controlName\":\"b\",\"rpmName\":\"d\"}]}")]
    [InlineData("{\"key\":\"k\",\"lastSeen\":\"2026-09-01T12:00:00Z\",\"kind\":\"UniFan\",\"index\":0,\"devices\":[{\"vendorId\":1,\"productId\":2,\"path\":\"p\"}],\"channels\":[{\"controlId\":\"a\",\"rpmId\":\"c\",\"rpmName\":\"d\"}]}")]
    [InlineData("{\"key\":\"k\",\"lastSeen\":\"2026-09-01T12:00:00Z\",\"kind\":\"UniFan\",\"index\":0,\"devices\":[{\"vendorId\":1,\"productId\":2,\"path\":\"p\"}],\"channels\":[{\"controlName\":\"b\",\"rpmId\":\"c\",\"rpmName\":\"d\"}]}")]
    [InlineData("{\"key\":\"k\",\"lastSeen\":\"2026-09-01T12:00:00Z\",\"kind\":\"UniFan\",\"index\":0,\"devices\":[{\"vendorId\":1,\"productId\":2,\"path\":\"p\"}],\"fanSpeeds\":[{\"id\":\"a\"}]}")]
    [InlineData("{\"key\":\"k\",\"lastSeen\":\"2026-09-01T12:00:00Z\",\"kind\":\"UniFan\",\"index\":0,\"devices\":[{\"vendorId\":1,\"productId\":2,\"path\":\"p\"}],\"fanSpeeds\":[{\"name\":\"a\"}]}")]
    [InlineData("{\"key\":\"k\",\"lastSeen\":\"2026-09-01T12:00:00Z\",\"kind\":\"UniFan\",\"index\":0,\"devices\":[{\"vendorId\":1,\"productId\":2,\"path\":\"p\"}],\"temperatures\":[{\"id\":\"a\"}]}")]
    [InlineData("{\"key\":\"k\",\"lastSeen\":\"2026-09-01T12:00:00Z\",\"kind\":\"UniFan\",\"index\":0,\"devices\":[{\"vendorId\":1,\"productId\":2,\"path\":\"p\"}],\"temperatures\":[{\"name\":\"a\"}]}")]
    [InlineData("{\"key\":\"k\",\"lastSeen\":\"2026-09-01T12:00:00Z\",\"kind\":\"UniFan\",\"index\":0,\"devices\":[{\"vendorId\":1,\"productId\":2,\"path\":\"p\"}],\"channels\":[{\"controlId\":\"a\",\"controlName\":\"b\",\"rpmId\":\"c\",\"rpmName\":\"d\"}]}")]
    [InlineData("{\"key\":\"k\",\"lastSeen\":\"2026-09-01T12:00:00Z\",\"kind\":\"UniFan\",\"index\":0,\"devices\":[{\"vendorId\":1,\"productId\":2,\"path\":\"p\"}],\"fanSpeeds\":[{\"id\":\"a\",\"name\":\"b\"}]}")]
    [InlineData("{\"key\":\"k\",\"lastSeen\":\"2026-09-01T12:00:00Z\",\"kind\":\"UniFan\",\"index\":0,\"devices\":[{\"vendorId\":1,\"productId\":2,\"path\":\"p\"}],\"temperatures\":[{\"id\":\"a\",\"name\":\"b\"}]}")]
    [InlineData("{\"key\":\"k\",\"lastSeen\":\"2026-09-01T12:00:00Z\",\"kind\":\"UniFan\",\"index\":0,\"devices\":[{\"vendorId\":1,\"productId\":2,\"path\":\"p\"}],\"fanSpeeds\":[{\"id\":\"a\",\"name\":\"b\",\"lastSeen\":\"2026-09-01T12:00:00Z\"},{\"id\":\"a\",\"name\":\"b\",\"lastSeen\":\"2026-09-01T12:00:00Z\"}]}")]
    public void Load_AnEntryThisDidNotWrite_IsSkippedAndLogged_TheOthersKept(string badEntry) {
        string good = RememberedControllerFile.Format(new[] { Wired() });
        WriteText(good.Replace("\"controllers\":[", "\"controllers\":[" + badEntry + ",", StringComparison.Ordinal));

        StoredController kept = Assert.Single(Read());

        Assert.Equal(Wired().Key, kept.Key);
        Assert.Contains("remembered controller 0 skipped: not an entry this plugin writes", _log.Messages);
    }

    [Fact]
    public void AMinimalEntry_ReadsWithEmptySensorLists() {
        WriteText("{\"version\":1,\"controllers\":[{\"key\":\"k\",\"lastSeen\":\"2026-09-01T12:00:00Z\",\"kind\":\"TlFan\",\"index\":1,\"devices\":[{\"vendorId\":1046,\"productId\":29554,\"path\":\"p\"}]}]}");

        StoredController entry = Assert.Single(Read());

        Assert.Empty(entry.Controller.Channels);
        Assert.Empty(entry.Controller.FanSpeeds);
        Assert.Empty(entry.Controller.Temperatures);
        Assert.Equal(0, entry.Controller.Plan.Devices[0].MaxOutputReportLength);
    }

    [Fact]
    public void Machine_IsBesideThePluginLog()
        => Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanControl.LianLi", "remembered-controllers.json"),
            RememberedControllerFile.Machine.FilePath);

    [Fact]
    public void RejectsMissingArguments() {
        Assert.Throws<ArgumentNullException>(() => new RememberedControllerFile(null!));
        Assert.Throws<ArgumentNullException>(() => File().Load(null!));
        Assert.Throws<ArgumentNullException>(() => File().Save(null!, _log));
        Assert.Throws<ArgumentNullException>(() => File().Save(Array.Empty<StoredController>(), null!));
        Assert.Throws<ArgumentNullException>(() => new StoredController(null!, Wired().Controller, Seen));
        Assert.Throws<ArgumentNullException>(() => new StoredController("k", null!, Seen));
    }

    [Fact]
    public void SeveralSensorsOfEachKind_ReadBackInOrder() {
        var controller = RememberedFixture.Of(
            new ControllerPlan(DeviceKind.UniFan, new LocatedDevice(0x0CF2, 0xA102, "p", null)),
            0,
            new[] { new ChannelDescriptor("c0", "n", "r0", "n"), new ChannelDescriptor("c1", "n", "r1", "n") },
            new[] { new FanSpeedDescriptor("f0", "n"), new FanSpeedDescriptor("f1", "n") },
            new[] { new TemperatureDescriptor("t0", "n"), new TemperatureDescriptor("t1", "n") },
            Seen);
        var older = new RememberedController(
            controller.Plan, 0, controller.Channels, controller.FanSpeeds, controller.Temperatures,
            new Dictionary<string, DateTime> {
                ["c0"] = Seen,
                ["c1"] = Seen.AddDays(-3),
                ["f0"] = Seen,
                ["f1"] = Seen.AddDays(-4),
                ["t0"] = Seen,
                ["t1"] = Seen.AddDays(-5),
            });
        File().Save(new[] { new StoredController("p", older, Seen) }, _log);

        RememberedController read = Assert.Single(Read()).Controller;

        Assert.Equal(Seen.AddDays(-3), read.LastSeenUtc("c1"));
        Assert.Equal(Seen.AddDays(-4), read.LastSeenUtc("f1"));
        Assert.Equal(Seen.AddDays(-5), read.LastSeenUtc("t1"));
        Assert.Equal(Seen, read.LastSeenUtc("c0"));

        Assert.Equal(new[] { "c0", "c1" }, read.Channels.Select(c => c.ControlId));
        Assert.Equal(new[] { "f0", "f1" }, read.FanSpeeds.Select(f => f.Id));
        Assert.Equal(new[] { "t0", "t1" }, read.Temperatures.Select(t => t.Id));
    }
}
