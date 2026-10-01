using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security;
using FanControl.LianLi.Logging;
using FanControl.LianLi.Protocol;

namespace FanControl.LianLi.Devices;

/// <summary>
/// Reads what L-Connect saved for the wireless devices. Five documents matter, all described in
/// <c>docs/wireless.md</c>:
///
/// <list type="bullet">
/// <item>the master's RF channel, at <c>slv3\config\&lt;master address&gt;.config</c>;</item>
/// <item>the rendered lighting effect per device, at <c>slv3\config\&lt;address&gt;.effect</c>, which
/// the plugin streams back verbatim because it cannot render one itself;</item>
/// <item>the wireless pump settings, one gzipped document under <c>device\</c> holding every water
/// block's screen presentation, which the plugin carries through so driving the pump does not
/// flatten the user's screen;</item>
/// <item>the locked device list, at <c>slv3\config\savedDevices.config</c>, which fixes the table
/// and its order while the user has it locked;</item>
/// <item>the wireless LCD FLEX fan screen settings, one gzipped document under <c>device\</c>
/// holding every such group's screen theme, data source, brightness and rotation, which the plugin
/// carries through the clock broadcast so driving the group does not blank its screens;</item>
/// <item>the per-device "sync to motherboard" switch, one gzipped <c>MotherboardARGBSync</c>
/// document under <c>device\</c> per wireless device, keyed by its RF address, which the Lighting
/// build honours by handing that device's lighting to the motherboard (its saved effect is still
/// streamed to it, as L-Connect streams it).</item>
/// </list>
///
/// Every one of them is optional to fan control. A file that is missing is the normal case; one
/// that is locked, unreadable, oversized, not gzip or not the JSON L-Connect writes is logged with
/// its path and the reason and yields nothing, so it costs only its own feature - the saved channel,
/// the saved look, or the saved screens - and never a fan.
/// </summary>
internal sealed class LConnectWirelessConfiguration : IWirelessConfigurationSource {
    // The most an effect stream can carry: its header counts the chunks in one byte, the header
    // itself included, so 254 chunks of 220 bytes. L-Connect's own 12288-byte cap
    // (MasterDevice.LzoMaxRgbDataLen) is checked by only one of its effect renderers and never by its
    // replay, so a saved effect larger than that is still one L-Connect streams; only one past what
    // the stream can carry is not an effect this could be.
    private const int MaxEffectBytes = 254 * 220;

    // RFController.LockDevice and CheckLockAndInitData.
    private const string LockedDevicesFileName = "savedDevices.config";

    // MainService.handleSetMotherboardARGBSyncRequest saves the switch for a wireless device as
    // DeviceSetting<bool> { DeviceID = its RF address, Type = "MotherboardARGBSync", Data = value },
    // and LWirelessController.ResumeSuspend reads it back with TryParseData<bool>.
    private const string MotherboardArgbSyncSettingKey = "MotherboardARGBSync";

    private readonly string _wirelessDirectory;
    private readonly string _deviceDirectory;
    private readonly bool _readsEffects;
    private readonly ILog _log;
    private readonly IReadOnlyDictionary<string, WirelessAioPresentation> _presentations;
    private readonly IReadOnlyDictionary<string, WirelessFanScreenPresentation> _fanScreens;

    /// <summary>
    /// Read L-Connect's wireless settings from <paramref name="wirelessDirectory"/> (its
    /// <c>slv3\config</c>), the per-device documents under <paramref name="deviceDirectory"/> (its
    /// <c>device</c>), the pump settings document at <paramref name="pumpSettingPath"/> and the fan
    /// screen settings document at <paramref name="fanScreenSettingPath"/>.
    /// <paramref name="readsEffects"/> is false in the builds that do not drive lighting, which then
    /// never look for an effect or a device's sync switch. The pump and fan screen settings are read
    /// here, once; a channel, an effect or a sync switch is read when it is asked for.
    /// </summary>
    public LConnectWirelessConfiguration(
        string wirelessDirectory, string deviceDirectory, string pumpSettingPath, string fanScreenSettingPath, bool readsEffects, ILog log) {
        _wirelessDirectory = wirelessDirectory ?? throw new ArgumentNullException(nameof(wirelessDirectory));
        _deviceDirectory = deviceDirectory ?? throw new ArgumentNullException(nameof(deviceDirectory));
        if (pumpSettingPath is null) {
            throw new ArgumentNullException(nameof(pumpSettingPath));
        }

        if (fanScreenSettingPath is null) {
            throw new ArgumentNullException(nameof(fanScreenSettingPath));
        }

        _readsEffects = readsEffects;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _presentations = ReadPumpSettings(pumpSettingPath);
        _fanScreens = ReadFanScreenSettings(fanScreenSettingPath);
    }

    /// <inheritdoc />
    public int? FindChannel(string masterMacText) {
        if (masterMacText is null) {
            throw new ArgumentNullException(nameof(masterMacText));
        }

        try {
            return WirelessChannelConfigurationReader.Read(_wirelessDirectory, masterMacText);
        } catch (Exception ex) when (IsSettingsFault(ex)) {
            Report("channel for master " + masterMacText, Path.Combine(_wirelessDirectory, masterMacText + ".config"), ex);
            return null;
        }
    }

    /// <inheritdoc />
    public WirelessSavedEffect? FindEffect(string macText) {
        if (macText is null) {
            throw new ArgumentNullException(nameof(macText));
        }

        if (!_readsEffects) {
            return null;
        }

        string path = Path.Combine(_wirelessDirectory, macText + ".effect");
        try {
            return File.Exists(path) ? ParseEffect(File.ReadAllText(path)) : null;
        } catch (Exception ex) when (IsSettingsFault(ex)) {
            Report("lighting effect for " + macText, path, ex);
            return null;
        }
    }

    /// <inheritdoc />
    public bool FindMotherboardArgbSync(string macText) {
        if (macText is null) {
            throw new ArgumentNullException(nameof(macText));
        }

        if (!_readsEffects) {
            return false;
        }

        string path = LConnectLocations.WirelessDeviceSettingPath(_deviceDirectory, macText, MotherboardArgbSyncSettingKey);
        try {
            return File.Exists(path) && ParseMotherboardArgbSync(LConnectFile.Read(path));
        } catch (Exception ex) when (IsSettingsFault(ex)) {
            Report("sync switch for " + macText, path, ex);
            return false;
        }
    }

    /// <inheritdoc />
    public WirelessAioPresentation? FindPumpPresentation(string macText) {
        if (macText is null) {
            throw new ArgumentNullException(nameof(macText));
        }

        return _presentations.TryGetValue(macText, out WirelessAioPresentation? presentation) ? presentation : null;
    }

    /// <inheritdoc />
    public WirelessFanScreenPresentation? FindFanScreenPresentation(string macText) {
        if (macText is null) {
            throw new ArgumentNullException(nameof(macText));
        }

        return _fanScreens.TryGetValue(macText, out WirelessFanScreenPresentation? presentation) ? presentation : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<WirelessLockedDevice>? FindLockedDevices(string masterMacText) {
        if (masterMacText is null) {
            throw new ArgumentNullException(nameof(masterMacText));
        }

        string path = Path.Combine(_wirelessDirectory, LockedDevicesFileName);
        try {
            return File.Exists(path) ? ParseLockedDevices(File.ReadAllText(path), masterMacText) : null;
        } catch (Exception ex) when (IsSettingsFault(ex)) {
            Report("locked device list", path, ex);
            return null;
        }
    }

    /// <summary>
    /// Parse L-Connect's locked device list: its <c>rfList</c>, serialised with <c>System.Text.Json</c>
    /// as a list of <c>RfDevice</c>, whose byte arrays are base64 strings. L-Connect writes it with
    /// <c>ReferenceHandler.Preserve</c>, so the root is an object whose <c>$values</c> member is
    /// the array; older releases wrote the array itself, and both are read. As
    /// <c>RFController.CheckLockAndInitData</c> reads it, empty text or an empty list is no lock,
    /// and a list whose first device names another master is not this master's lock. Throws
    /// <see cref="FormatException"/> for a list that is not what L-Connect writes, which L-Connect
    /// treats as no lock too.
    /// </summary>
    public static IReadOnlyList<WirelessLockedDevice>? ParseLockedDevices(string json, string masterMacText) {
        if (json is null) {
            throw new ArgumentNullException(nameof(json));
        }

        if (masterMacText is null) {
            throw new ArgumentNullException(nameof(masterMacText));
        }

        if (json.Trim().Length == 0) {
            return null;
        }

        JsonValue root = JsonValue.Parse(json);
        if (!root.IsArray) {
            root = root.Member("$values") ?? throw new FormatException("The locked list is not a list.");
            if (!root.IsArray) {
                throw new FormatException("The locked list's \"$values\" is not a list.");
            }
        }

        IReadOnlyList<JsonValue> entries = root.Elements;
        if (entries.Count == 0) {
            return null;
        }

        var locked = new List<WirelessLockedDevice>();
        foreach (JsonValue entry in entries) {
            locked.Add(ParseLockedDevice(entry));
        }

        string first = entries[0].Member("MasterMacStr")?.AsString() ?? throw Missing("MasterMacStr");
        return NormaliseAddress(first) == masterMacText ? locked : null;
    }

    /// <summary>
    /// Parse one saved effect document. L-Connect writes it with <c>System.Text.Json</c> from its
    /// <c>RgbEffect</c> (<c>RfDevice.rgbEffect</c>'s setter), so the two byte arrays are base64 strings.
    /// Throws <see cref="FormatException"/>, naming what is wrong, when a required member is missing
    /// or malformed or the effect is larger than the firmware accepts.
    /// </summary>
    public static WirelessSavedEffect ParseEffect(string json) {
        if (json is null) {
            throw new ArgumentNullException(nameof(json));
        }

        JsonValue root = JsonValue.Parse(json);
        byte[] data = Base64(root, "data");
        if (data.Length == 0 || data.Length > MaxEffectBytes) {
            throw new FormatException(string.Format(
                CultureInfo.InvariantCulture, "\"data\" is {0} bytes; an effect carries 1-{1}.", data.Length, MaxEffectBytes));
        }

        byte[] effectIndex = Base64(root, "effect_index");
        if (effectIndex.Length != WirelessProtocol.EffectIndexLength) {
            throw new FormatException("\"effect_index\" is not " + WirelessProtocol.EffectIndexLength + " bytes.");
        }

        int totalFrame = Number(root, "total_frame");
        if (totalFrame <= 0) {
            throw new FormatException("\"total_frame\" is not positive.");
        }

        int ledNum = Number(root, "led_num");
        if (ledNum < 0 || ledNum > byte.MaxValue) {
            throw new FormatException("\"led_num\" is not a byte.");
        }

        double interval = root.Member("interval")?.AsDouble() ?? throw Missing("interval");
        return new WirelessSavedEffect(
            data,
            effectIndex,
            totalFrame,
            root.Member("total_sub_frame")?.AsInt() ?? 0,
            (byte)ledNum,
            interval,
            root.Member("sub_interval")?.AsDouble() ?? 0.0);
    }

    /// <summary>
    /// Parse L-Connect's wireless pump settings into a screen presentation per device. The document
    /// is L-Connect's <c>DeviceSetting</c>: <c>{ DeviceID, Type, Data }</c> where <c>Data</c> maps an
    /// address to that block's <c>LWirelessPumpConfig</c>. L-Connect writes the address with colons,
    /// which are dropped here so it keys the same way the receiver reports it. A document without
    /// <c>Data</c> throws <see cref="FormatException"/>; one device whose settings are incomplete is
    /// left out, with the reason added to <paramref name="problems"/>, so that block alone falls back
    /// to L-Connect's default screen.
    /// </summary>
    public static IReadOnlyDictionary<string, WirelessAioPresentation> ParsePumpSettings(string json, ICollection<string> problems) {
        if (json is null) {
            throw new ArgumentNullException(nameof(json));
        }

        if (problems is null) {
            throw new ArgumentNullException(nameof(problems));
        }

        JsonValue data = JsonValue.Parse(json).Member("Data") ?? throw Missing("Data");
        var presentations = new Dictionary<string, WirelessAioPresentation>(StringComparer.Ordinal);
        foreach (string key in data.MemberNames) {
            try {
                // The key came from MemberNames, so the member is there.
                presentations[NormaliseAddress(key)] = ParsePresentation(data.Member(key)!);
            } catch (FormatException ex) {
                problems.Add(key + ": " + ex.Message);
            }
        }

        return presentations;
    }

    /// <summary>
    /// Parse L-Connect's wireless LCD FLEX fan screen settings into a presentation per group. The
    /// document is L-Connect's <c>DeviceSetting</c>: <c>{ DeviceID, Type, Data }</c> where <c>Data</c>
    /// maps an address to that group's <c>LWirelessLCDConfig</c>. A group's presentation is what
    /// <c>LWirelessController.applyWirelessLCDMode</c> puts in the screen table for it: the theme of
    /// fan <c>n</c> is <c>TemplateParams.Theme(n+1)</c>; its data source is the one saved for that
    /// fan under that theme (<c>TemplateFanSettings["n:theme"]</c>, else <c>FanSettings["n"]</c> when
    /// its theme matches), else <c>TemplateParams.DataType(n+1)</c>; the brightness and rotation are
    /// the template's. A document without <c>Data</c> throws <see cref="FormatException"/>; one
    /// group whose settings are malformed is left out, with the reason added to
    /// <paramref name="problems"/>, so that group alone gets L-Connect's default entry.
    /// </summary>
    public static IReadOnlyDictionary<string, WirelessFanScreenPresentation> ParseFanScreenSettings(string json, ICollection<string> problems) {
        if (json is null) {
            throw new ArgumentNullException(nameof(json));
        }

        if (problems is null) {
            throw new ArgumentNullException(nameof(problems));
        }

        JsonValue data = JsonValue.Parse(json).Member("Data") ?? throw Missing("Data");
        var presentations = new Dictionary<string, WirelessFanScreenPresentation>(StringComparer.Ordinal);
        foreach (string key in data.MemberNames) {
            try {
                // The key came from MemberNames, so the member is there.
                presentations[NormaliseAddress(key)] = ParseFanScreens(data.Member(key)!);
            } catch (FormatException ex) {
                problems.Add(key + ": " + ex.Message);
            }
        }

        return presentations;
    }

    /// <summary>
    /// Whether a saved <c>MotherboardARGBSync</c> document says the switch is on: its <c>Type</c> is
    /// that setting's and its <c>Data</c> is the JSON <c>true</c>. Anything else reads as off, which
    /// is what L-Connect's own <c>TryParseData&lt;bool&gt;</c> falls back to.
    /// </summary>
    public static bool ParseMotherboardArgbSync(JsonValue document) {
        if (document is null) {
            throw new ArgumentNullException(nameof(document));
        }

        return document.Member("Type")?.AsString() == MotherboardArgbSyncSettingKey && (document.Member("Data")?.AsBool() ?? false);
    }

    /// <summary>An RF address as the plugin keys it: lowercase hex, no separators.</summary>
    public static string NormaliseAddress(string address) {
        if (address is null) {
            throw new ArgumentNullException(nameof(address));
        }

        return address.Replace(":", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
    }

    // One LWirelessPumpConfig: its AioParams carries every saved screen setting, all of which
    // L-Connect serialises; the theme is the config's WirelessTemplateIndex and the mode its
    // IsAdvanceMode, which an older document may not have and System.Text.Json then leaves at 0
    // and false.
    private static WirelessAioPresentation ParsePresentation(JsonValue device) {
        JsonValue aio = device.Member("AioParams") ?? throw Missing("AioParams");
        return new WirelessAioPresentation(
            Number(aio, "LoopInterval"),
            Flag(aio, "PumpEnable"),
            Number(aio, "FanSpeed"),
            Flag(aio, "FanSpeedEnable"),
            Number(aio, "LcdBrightness"),
            device.Member("WirelessTemplateIndex")?.AsInt() ?? 0,
            Number(aio, "Rotation"),
            Colour(aio, "StrColor"),
            Colour(aio, "ValColor"),
            Colour(aio, "UintColor"),
            device.Member("IsAdvanceMode")?.AsBool() ?? false);
    }

    // One LWirelessLCDConfig. A missing member reads as System.Text.Json leaves it: a document
    // without TemplateParams is a fresh template (themes 0, brightness 0, rotation 3), IsAdvanceMode
    // is false, and a colour is 0. FanSettings is keyed by the fan index and TemplateFanSettings by
    // "index:theme" (LWirelessController.buildTemplateFanSettingKey). The fan setting chosen for a
    // fan also carries its theme's colours (WirelessFanInfoDto's eight uints); a fan with none gets
    // L-Connect's own colours for its theme.
    private static WirelessFanScreenPresentation ParseFanScreens(JsonValue device) {
        JsonValue? template = device.Member("TemplateParams");
        JsonValue? fanSettings = device.Member("FanSettings");
        JsonValue? templateFanSettings = device.Member("TemplateFanSettings");
        var themes = new byte[WirelessProtocol.SlotsPerGroup];
        var dataSources = new byte[WirelessProtocol.SlotsPerGroup];
        var colours = new WirelessThemeColours[WirelessProtocol.SlotsPerGroup];
        for (int fan = 0; fan < WirelessProtocol.SlotsPerGroup; fan++) {
            string ordinal = (fan + 1).ToString(CultureInfo.InvariantCulture);
            themes[fan] = OptionalByte(template, "Theme" + ordinal, 0);
            JsonValue? saved = templateFanSettings?.Member(fan.ToString(CultureInfo.InvariantCulture) + ":" + themes[fan]);
            if (saved is null) {
                JsonValue? forFan = fanSettings?.Member(fan.ToString(CultureInfo.InvariantCulture));
                if (forFan != null && OptionalByte(forFan, "FanThemeIndex", WirelessFanScreenPresentation.NoTheme) == themes[fan]) {
                    saved = forFan;
                }
            }

            if (saved is null) {
                dataSources[fan] = OptionalByte(template, "DataType" + ordinal, 0);
                colours[fan] = WirelessThemeColours.ForTheme(themes[fan]);
            } else {
                dataSources[fan] = OptionalByte(saved, "FanDataSourceIndex", 0);
                colours[fan] = WirelessThemeColours.FromSaved(
                    themes[fan],
                    OptionalUnsigned(saved, "GraphColor1"),
                    OptionalUnsigned(saved, "GraphColor2"),
                    OptionalUnsigned(saved, "FontTitleColor"),
                    OptionalUnsigned(saved, "FontDataColor"),
                    OptionalUnsigned(saved, "FontUnitColor"),
                    OptionalUnsigned(saved, "FontTitleShadowColor"),
                    OptionalUnsigned(saved, "FontDataShadowColor"),
                    OptionalUnsigned(saved, "FontUnitShadowColor"));
            }
        }

        return new WirelessFanScreenPresentation(
            themes,
            dataSources,
            OptionalByte(template, "Brightness", 0),
            WirelessFanScreenPresentation.DirectionFromRotation(OptionalByte(template, "Rotation", 3)),
            device.Member("IsAdvanceMode")?.AsBool() ?? false,
            colours);
    }

    // A uint member (a packed ARGB colour) System.Text.Json would leave at 0 when absent, and
    // refuse when it is not a whole number in a uint's range.
    private static uint OptionalUnsigned(JsonValue parent, string name) {
        JsonValue? member = parent.Member(name);
        if (member is null) {
            return 0;
        }

        double value = member.AsDouble() ?? throw Missing(name);
        if (value < 0 || value > uint.MaxValue || value != Math.Floor(value)) {
            throw new FormatException("\"" + name + "\" is not an unsigned integer.");
        }

        return (uint)value;
    }

    // A byte member System.Text.Json would leave at its default when absent, and refuse when it is
    // not a byte.
    private static byte OptionalByte(JsonValue? parent, string name, byte fallback) {
        JsonValue? member = parent?.Member(name);
        if (member is null) {
            return fallback;
        }

        int value = member.AsInt() ?? throw Missing(name);
        if (value < 0 || value > byte.MaxValue) {
            throw new FormatException("\"" + name + "\" is not a byte.");
        }

        return (byte)value;
    }

    // A colour L-Connect serialised from a WPF Color: the four components among its members.
    private static WirelessAioPresentation.Argb Colour(JsonValue parent, string name) {
        JsonValue colour = parent.Member(name) ?? throw Missing(name);
        return new WirelessAioPresentation.Argb(
            Component(colour, name, "A"), Component(colour, name, "R"), Component(colour, name, "G"), Component(colour, name, "B"));
    }

    private static byte Component(JsonValue colour, string colourName, string name) {
        int value = colour.Member(name)?.AsInt() ?? throw Missing(colourName + "." + name);
        if (value < 0 || value > byte.MaxValue) {
            throw new FormatException("\"" + colourName + "." + name + "\" is not a byte.");
        }

        return (byte)value;
    }

    // One RfDevice of the locked list: what RefreshList last read into it, the receiver slot it
    // should be on (_target_rx_type) and the speeds it was driven at (target_fans_pwm). fan_num is
    // stored already less the ten a right-hand end-cap adds, and which side the end cap is on is
    // stored beside it (isINFRightAttach), which an older list may lack.
    private static WirelessLockedDevice ParseLockedDevice(JsonValue device) {
        int fanCount = Number(device, "fan_num");
        if (fanCount < 0 || fanCount > WirelessProtocol.SlotsPerGroup) {
            throw new FormatException("\"fan_num\" is not 0-" + WirelessProtocol.SlotsPerGroup + ".");
        }

        var record = new WirelessDeviceRecord(
            Base64(device, "mac_addr"),
            Base64(device, "master_mac_addr"),
            Byte(device, "channel"),
            Byte(device, "_rx_type"),
            0,
            Byte(device, "dev_type"),
            fanCount,
            Base64(device, "effect_index"),
            Base64(device, "fans_type"),
            new int[WirelessProtocol.SlotsPerGroup],
            Base64(device, "fans_pwm"),
            0,
            device.Member("isINFRightAttach")?.AsBool() ?? false,
            0,
            false);
        return new WirelessLockedDevice(record, Byte(device, "_target_rx_type"), Base64(device, "target_fans_pwm"));
    }

    private static byte Byte(JsonValue parent, string name) {
        int value = Number(parent, name);
        if (value < 0 || value > byte.MaxValue) {
            throw new FormatException("\"" + name + "\" is not a byte.");
        }

        return (byte)value;
    }

    private static int Number(JsonValue parent, string name) => parent.Member(name)?.AsInt() ?? throw Missing(name);

    private static bool Flag(JsonValue parent, string name) => parent.Member(name)?.AsBool() ?? throw Missing(name);

    private static byte[] Base64(JsonValue parent, string name) {
        string text = parent.Member(name)?.AsString() ?? throw Missing(name);
        return Convert.FromBase64String(text);
    }

    private static FormatException Missing(string name) => new FormatException("\"" + name + "\" is missing or of the wrong type.");

    /// <summary>
    /// Whether <paramref name="ex"/> is one of the ways a settings file L-Connect owns can be
    /// unusable - a missing directory or sharing violation (<see cref="IOException"/>), an ACL
    /// (<see cref="UnauthorizedAccessException"/>, <see cref="SecurityException"/>), not gzip
    /// (<see cref="InvalidDataException"/>), bad path characters (<see cref="ArgumentException"/>,
    /// <see cref="NotSupportedException"/>), or content that is not what L-Connect writes
    /// (<see cref="FormatException"/> from the parsers, <see cref="OverflowException"/> from the
    /// channel number) - rather than a fault in the plugin, which is left to propagate.
    /// </summary>
    public static bool IsSettingsFault(Exception ex)
        => ex is IOException
            || ex is UnauthorizedAccessException
            || ex is SecurityException
            || ex is InvalidDataException
            || ex is ArgumentException
            || ex is NotSupportedException
            || ex is FormatException
            || ex is OverflowException;

    private IReadOnlyDictionary<string, WirelessAioPresentation> ReadPumpSettings(string path) {
        var none = new Dictionary<string, WirelessAioPresentation>(StringComparer.Ordinal);
        try {
            if (!File.Exists(path)) {
                return none;
            }

            var problems = new List<string>();
            IReadOnlyDictionary<string, WirelessAioPresentation> presentations = ParsePumpSettings(LConnectFile.ReadText(path), problems);
            foreach (string problem in problems) {
                _log.Write("wireless: saved screen settings for " + problem + "; that water block keeps L-Connect's default screen");
            }

            return presentations;
        } catch (Exception ex) when (IsSettingsFault(ex)) {
            Report("pump settings", path, ex);
            return none;
        }
    }

    private IReadOnlyDictionary<string, WirelessFanScreenPresentation> ReadFanScreenSettings(string path) {
        var none = new Dictionary<string, WirelessFanScreenPresentation>(StringComparer.Ordinal);
        try {
            if (!File.Exists(path)) {
                return none;
            }

            var problems = new List<string>();
            IReadOnlyDictionary<string, WirelessFanScreenPresentation> presentations = ParseFanScreenSettings(LConnectFile.ReadText(path), problems);
            foreach (string problem in problems) {
                _log.Write("wireless: saved screen settings for " + problem + "; that group's screens get L-Connect's default entry");
            }

            return presentations;
        } catch (Exception ex) when (IsSettingsFault(ex)) {
            Report("fan screen settings", path, ex);
            return none;
        }
    }

    private void Report(string what, string path, Exception ex)
        => _log.Write(string.Format(
            CultureInfo.InvariantCulture,
            "wireless: saved {0} unusable, ignored ({1}): {2}: {3}",
            what,
            path,
            ex.GetType().Name,
            ex.Message));
}
