using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Logging;
using FanControl.LianLi.Transport;

namespace FanControl.LianLi.Plugin;

/// <summary>
/// Keeps the remembered controllers in a small JSON file beside the plugin's log. It is the
/// plugin's own record of what it has seen, not a setting: nothing in it is meant to be edited,
/// and a file that is missing, unreadable or not what this writes is logged and treated as empty,
/// costing nothing but the head start it gives after a reboot. Written whole to a temporary file
/// and then swapped in, so a power cut mid-write leaves the previous file rather than half of one.
/// </summary>
internal sealed class RememberedControllerFile : IRememberedControllerStore {
    // Bumped when the layout changes; a file of another version is not read.
    private const int FormatVersion = 1;

    // Generous next to any real file - a machine has a handful of controllers - and small enough
    // that a file which is not what this writes is not read into memory whole.
    private const long MaximumBytes = 1024 * 1024;

    private const string FileName = "remembered-controllers.json";

    private readonly string _directory;
    private readonly string _path;

    /// <summary>Keep the controllers in a file in <paramref name="directory"/>.</summary>
    public RememberedControllerFile(string directory) {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _path = Path.Combine(directory, FileName);
    }

    /// <summary>The file beside the plugin's log, under the local application data of whoever runs FanControl.</summary>
    public static RememberedControllerFile Machine => new RememberedControllerFile(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanControl.LianLi"));

    /// <summary>The file the controllers are kept in.</summary>
    public string FilePath => _path;

    /// <inheritdoc />
    public IReadOnlyList<StoredController>? Load(ILog log) {
        if (log is null) {
            throw new ArgumentNullException(nameof(log));
        }

        try {
            if (!File.Exists(_path)) {
                return Array.Empty<StoredController>();
            }

            if (new FileInfo(_path).Length > MaximumBytes) {
                throw new FormatException("the file is larger than " + MaximumBytes + " bytes");
            }

            return Parse(JsonValue.Parse(File.ReadAllText(_path, Encoding.UTF8)), log);
        } catch (FormatException ex) {
            log.Write("remembered controllers not read from " + _path + ": " + ex.Message + "; it is replaced at the next save");
            return Array.Empty<StoredController>();
        } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
            // Locked or unreadable for now (a scan holding it at boot, say): what it holds is still
            // wanted, so it is read again at the next scan and not overwritten before then.
            log.Write("remembered controllers not read from " + _path + " just now: " + ex.Message + "; tried again at the next scan");
            return null;
        }
    }

    /// <inheritdoc />
    public void Save(IReadOnlyList<StoredController> controllers, ILog log) {
        if (controllers is null) {
            throw new ArgumentNullException(nameof(controllers));
        }

        if (log is null) {
            throw new ArgumentNullException(nameof(log));
        }

        string temporary = _path + ".tmp";
        try {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(temporary, Format(controllers), new UTF8Encoding(false));
            if (File.Exists(_path)) {
                File.Replace(temporary, _path, null);
            } else {
                File.Move(temporary, _path);
            }
        } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
            log.Write("remembered controllers not saved to " + _path + ": " + ex.Message);
        }
    }

    /// <summary>The text this writes for <paramref name="controllers"/>.</summary>
    internal static string Format(IReadOnlyList<StoredController> controllers) {
        var json = new StringBuilder();
        json.Append("{\"version\":").Append(FormatVersion.ToString(CultureInfo.InvariantCulture)).Append(",\"controllers\":[");
        for (int i = 0; i < controllers.Count; i++) {
            if (i > 0) {
                json.Append(',');
            }

            StoredController stored = controllers[i];
            RememberedController controller = stored.Controller;
            json.Append("{\"key\":").Append(Quote(stored.Key))
                .Append(",\"lastSeen\":").Append(Time(stored.LastSeenUtc))
                .Append(",\"kind\":").Append(Quote(controller.Plan.Kind.ToString()))
                .Append(",\"index\":").Append(controller.Index.ToString(CultureInfo.InvariantCulture))
                .Append(",\"devices\":[");
            for (int d = 0; d < controller.Plan.Devices.Count; d++) {
                LocatedDevice device = controller.Plan.Devices[d];
                json.Append(d > 0 ? "," : string.Empty)
                    .Append("{\"vendorId\":").Append(device.VendorId.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"productId\":").Append(device.ProductId.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"path\":").Append(Quote(device.DevicePath))
                    .Append(",\"containerId\":").Append(device.ContainerId is null ? "null" : Quote(device.ContainerId))
                    .Append(",\"maxOutputReportLength\":").Append(device.MaxOutputReportLength.ToString(CultureInfo.InvariantCulture))
                    .Append('}');
            }

            json.Append("],\"channels\":[");
            for (int c = 0; c < controller.Channels.Count; c++) {
                ChannelDescriptor channel = controller.Channels[c];
                json.Append(c > 0 ? "," : string.Empty)
                    .Append("{\"controlId\":").Append(Quote(channel.ControlId))
                    .Append(",\"controlName\":").Append(Quote(channel.ControlName))
                    .Append(",\"rpmId\":").Append(Quote(channel.RpmId))
                    .Append(",\"rpmName\":").Append(Quote(channel.RpmName))
                    .Append(",\"lastSeen\":").Append(Time(controller.LastSeenUtc(channel.ControlId)))
                    .Append('}');
            }

            json.Append("],\"fanSpeeds\":[");
            for (int f = 0; f < controller.FanSpeeds.Count; f++) {
                json.Append(f > 0 ? "," : string.Empty)
                    .Append("{\"id\":").Append(Quote(controller.FanSpeeds[f].Id))
                    .Append(",\"name\":").Append(Quote(controller.FanSpeeds[f].Name))
                    .Append(",\"lastSeen\":").Append(Time(controller.LastSeenUtc(controller.FanSpeeds[f].Id)))
                    .Append('}');
            }

            json.Append("],\"temperatures\":[");
            for (int t = 0; t < controller.Temperatures.Count; t++) {
                json.Append(t > 0 ? "," : string.Empty)
                    .Append("{\"id\":").Append(Quote(controller.Temperatures[t].Id))
                    .Append(",\"name\":").Append(Quote(controller.Temperatures[t].Name))
                    .Append(",\"lastSeen\":").Append(Time(controller.LastSeenUtc(controller.Temperatures[t].Id)))
                    .Append('}');
            }

            json.Append("]}");
        }

        return json.Append("]}").ToString();
    }

    /// <summary>
    /// The controllers in a parsed file. Throws <see cref="FormatException"/> for a file of another
    /// version; one entry that does not read is logged and skipped, and the others kept.
    /// </summary>
    internal static IReadOnlyList<StoredController> Parse(JsonValue root, ILog log) {
        if (root.Member("version")?.AsInt() != FormatVersion) {
            throw new FormatException("not a version " + FormatVersion + " file");
        }

        var controllers = new List<StoredController>();
        IReadOnlyList<JsonValue> entries = root.Member("controllers")?.Elements ?? Array.Empty<JsonValue>();
        for (int i = 0; i < entries.Count; i++) {
            StoredController? stored = ParseController(entries[i]);
            if (stored is null) {
                log.Write(string.Format(
                    CultureInfo.InvariantCulture, "remembered controller {0} skipped: not an entry this plugin writes", i));
                continue;
            }

            controllers.Add(stored);
        }

        return controllers;
    }

    private static StoredController? ParseController(JsonValue entry) {
        string? key = entry.Member("key")?.AsString();
        DateTime? lastSeenUtc = ReadTime(entry);
        string? kindText = entry.Member("kind")?.AsString();
        int? index = entry.Member("index")?.AsInt();
        if (key is null || kindText is null || index is null || index < 0 || lastSeenUtc is null
            || !Enum.TryParse(kindText, out DeviceKind kind)) {
            return null;
        }

        var devices = new List<LocatedDevice>();
        foreach (JsonValue device in entry.Member("devices")?.Elements ?? Array.Empty<JsonValue>()) {
            int? vendorId = device.Member("vendorId")?.AsInt();
            int? productId = device.Member("productId")?.AsInt();
            string? path = device.Member("path")?.AsString();
            if (vendorId is null || productId is null || path is null) {
                return null;
            }

            devices.Add(new LocatedDevice(
                vendorId.Value,
                productId.Value,
                path,
                null,
                device.Member("containerId")?.AsString(),
                device.Member("maxOutputReportLength")?.AsInt() ?? 0));
        }

        if (!IsBuildable(kind) || devices.Count != (kind == DeviceKind.WirelessTransmitter ? 2 : 1)) {
            return null;
        }

        var plan = new ControllerPlan(kind, devices.ToArray());

        // A sensor id appears once per controller; a repeated one is not something this writes.
        var seen = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var channels = new List<ChannelDescriptor>();
        foreach (JsonValue channel in entry.Member("channels")?.Elements ?? Array.Empty<JsonValue>()) {
            string? controlId = channel.Member("controlId")?.AsString();
            string? controlName = channel.Member("controlName")?.AsString();
            string? rpmId = channel.Member("rpmId")?.AsString();
            string? rpmName = channel.Member("rpmName")?.AsString();
            if (controlId is null || controlName is null || rpmId is null || rpmName is null || !Seen(seen, controlId, channel)) {
                return null;
            }

            channels.Add(new ChannelDescriptor(controlId, controlName, rpmId, rpmName));
        }

        var fanSpeeds = new List<FanSpeedDescriptor>();
        foreach (JsonValue speed in entry.Member("fanSpeeds")?.Elements ?? Array.Empty<JsonValue>()) {
            if (!(speed.Member("id")?.AsString() is string id) || !(speed.Member("name")?.AsString() is string name) || !Seen(seen, id, speed)) {
                return null;
            }

            fanSpeeds.Add(new FanSpeedDescriptor(id, name));
        }

        var temperatures = new List<TemperatureDescriptor>();
        foreach (JsonValue temperature in entry.Member("temperatures")?.Elements ?? Array.Empty<JsonValue>()) {
            if (!(temperature.Member("id")?.AsString() is string id) || !(temperature.Member("name")?.AsString() is string name)
                || !Seen(seen, id, temperature)) {
                return null;
            }

            temperatures.Add(new TemperatureDescriptor(id, name));
        }

        return new StoredController(
            key,
            new RememberedController(plan, index.Value, channels, fanSpeeds, temperatures, seen),
            lastSeenUtc.Value);
    }

    // Record when the sensor id in element was last seen; false when it has no time or is repeated.
    private static bool Seen(Dictionary<string, DateTime> seen, string id, JsonValue element) {
        DateTime? when = ReadTime(element);
        if (when is null || seen.ContainsKey(id)) {
            return false;
        }

        seen[id] = when.Value;
        return true;
    }

    // An element's "lastSeen", as UTC; null when it is missing or not a round-trip time.
    private static DateTime? ReadTime(JsonValue element)
        => DateTime.TryParse(element.Member("lastSeen")?.AsString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime when)
            ? when.ToUniversalTime()
            : (DateTime?)null;

    // A time as a round-trip string.
    private static string Time(DateTime utc) => Quote(utc.ToString("o", CultureInfo.InvariantCulture));

    // The kinds a plan is ever made for.
    private static bool IsBuildable(DeviceKind kind)
        => kind == DeviceKind.UniFan || kind == DeviceKind.TlFan || kind == DeviceKind.Galahad2 || kind == DeviceKind.WirelessTransmitter;

    // A JSON string: quotes, backslashes and control characters escaped, everything else as is.
    private static string Quote(string text) {
        var quoted = new StringBuilder(text.Length + 2);
        quoted.Append('"');
        foreach (char c in text) {
            switch (c) {
                case '"':
                    quoted.Append("\\\"");
                    break;
                case '\\':
                    quoted.Append("\\\\");
                    break;
                default:
                    if (c < ' ') {
                        quoted.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    } else {
                        quoted.Append(c);
                    }

                    break;
            }
        }

        return quoted.Append('"').ToString();
    }
}
