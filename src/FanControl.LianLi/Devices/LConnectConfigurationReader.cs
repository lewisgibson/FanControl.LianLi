#if ENABLE_LIGHTING
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using FanControl.LianLi.Protocol;

namespace FanControl.LianLi.Devices;

/// <summary>
/// Reads L-Connect's own saved lighting configuration directly from its config directory and
/// groups it into a per-controller <see cref="LConnectControllerConfiguration"/>. There is no
/// intermediary export file: the Lighting plugin reproduces whatever look L-Connect last
/// saved. Each setting is a gzipped JSON file holding <c>{ DeviceID, Type, Data }</c>; the
/// reader keeps the look settings (<c>LightingPort*</c>, <c>FanQuantity</c>, <c>MergeOrder</c> and
/// the other families' equivalents) and the per-controller <c>MotherboardARGBSync</c> switch, and
/// ignores the rest (fan curves, screen settings). Files that are not device settings are skipped by
/// content; a genuinely unreadable file throws and lets the caller disable lighting rather than
/// apply a partial look.
/// </summary>
internal static class LConnectConfigurationReader
{
    // DeviceID looks like "...&mi_01#d&71d6ab5&0&0000#{...}". The instance token is the first
    // long hex run inside the HID-interface instance segment. The interface number varies by
    // device (SL-Infinity is mi_01; the Strimer Plus may differ), so match any mi_NN.
    private static readonly Regex InstanceSegment = new Regex(@"mi_[0-9a-fA-F]+#(.*?)#\{", RegexOptions.CultureInvariant);
    private static readonly Regex HexToken = new Regex("[0-9a-fA-F]{5,}", RegexOptions.CultureInvariant);

    // Strimer Plus saves its per-port look under settings named "Port0".."Port11" (vs the Uni
    // fans' "LightingPort0"..). Both carry the same Port/Mode/Speed/Direction/Brightness/Colors
    // shape, so the reader treats them identically; the encoder is chosen later by device pid.
    private static readonly Regex StrimerPortType = new Regex("^Port[0-9]+$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Read every controller's saved look from <paramref name="directory"/>. Returns an empty
    /// list when the directory is absent (L-Connect not installed). Throws on a corrupt file so
    /// the caller can disable lighting deliberately.
    /// </summary>
    public static IReadOnlyList<LConnectControllerConfiguration> Read(string directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return Array.Empty<LConnectControllerConfiguration>();
        }

        var builders = new Dictionary<string, Builder>(StringComparer.OrdinalIgnoreCase);

        foreach (string folder in Directory.GetDirectories(directory))
        {
            foreach (string file in Directory.GetFiles(folder, "*.0"))
            {
                JsonValue root = LConnectFile.Read(file);
                string? deviceId = root.Member("DeviceID")?.AsString();
                string? type = root.Member("Type")?.AsString();
                if (deviceId is null || type is null)
                {
                    continue;
                }

                string? token = ExtractInstanceToken(deviceId);
                if (token is null)
                {
                    continue;
                }

                if (!builders.TryGetValue(token, out Builder? builder))
                {
                    builder = new Builder(token);
                    builders[token] = builder;
                }

                JsonValue? data = root.Member("Data");
                if (data is null)
                {
                    continue;
                }

                if (type.StartsWith("LightingPort", StringComparison.Ordinal) || StrimerPortType.IsMatch(type))
                {
                    builder.AddPort(ReadPort(data));
                }
                else if (type == "FanQuantity")
                {
                    builder.SetQuantity(ReadIntArray(data));
                }
                else if (type == "MergeOrder")
                {
                    // Uni SL-Infinity, SL v2 and AL v2: the order the four fan groups chain in a
                    // merge effect, written to the controller on every start.
                    builder.SetMergeOrder(ReadIntArray(data));
                }
                else if (type == "FanLEDLighting")
                {
                    // Galahad II Trinity and Vision, HydroShift LCD: a single FanLightingSetting object.
                    builder.SetGalahadFan(ReadGalahadFan(data));
                }
                else if (type == "ScreenLEDLighting")
                {
                    // Galahad II Vision: the ring of LEDs around its screen, a ScreenLEDLightingSetting.
                    builder.SetGalahadScreen(ReadGalahadScreen(data));
                }
                else if (type == "PumpLEDLighting")
                {
                    // Galahad II pump: an array of PumpLightingSetting, one for the whole cap or,
                    // in L-Connect's individual mode, one each for the Inner and Outer scopes.
                    builder.SetGalahadPumps(ReadGalahadPumps(data));
                }
                else if (type == "Lighting")
                {
                    // Uni Fan TL: a nested LightingConfigCollection of per-fan looks.
                    builder.AddTlFans(ReadTlFans(data));
                }
                else if (type == "MotherboardARGBSync")
                {
                    // L-Connect keeps its "sync to motherboard" switch per controller, as a
                    // bare JSON bool saved beside that controller's look (the Uni families, TL,
                    // Galahad II Trinity and Strimer Plus write it). A value that is not a bool
                    // is read as off, which is what L-Connect's own TryParseData falls back to.
                    builder.SetMotherboardArgbSync(data.AsBool() ?? false);
                }
            }
        }

        var configurations = new List<LConnectControllerConfiguration>();
        foreach (Builder builder in builders.Values)
        {
            LConnectControllerConfiguration? configuration = builder.Build();
            if (configuration != null)
            {
                configurations.Add(configuration);
            }
        }

        return configurations;
    }

    private static LightingPortState? ReadPort(JsonValue data)
    {
        int? port = data.Member("Port")?.AsInt();
        int? mode = data.Member("Mode")?.AsInt();
        if (port is null || mode is null)
        {
            return null;
        }

        int speed = data.Member("Speed")?.AsInt() ?? 0;
        int direction = data.Member("Direction")?.AsInt() ?? 0;
        int brightness = data.Member("Brightness")?.AsInt() ?? 0;

        return new LightingPortState(port.Value, mode.Value, speed, direction, brightness, ReadColors(data.Member("Colors")));
    }

    // L-Connect persists colours as {"R":..,"G":..,"B":..} for every family (System.Windows.Media.Color).
    private static List<RgbColor> ReadColors(JsonValue? colorArray)
    {
        var colors = new List<RgbColor>();
        if (colorArray != null)
        {
            foreach (JsonValue color in colorArray.Elements)
            {
                colors.Add(ReadColor(color));
            }
        }

        return colors;
    }

    private static RgbColor ReadColor(JsonValue color)
    {
        int r = color.Member("R")?.AsInt() ?? 0;
        int g = color.Member("G")?.AsInt() ?? 0;
        int b = color.Member("B")?.AsInt() ?? 0;
        return new RgbColor((byte)r, (byte)g, (byte)b);
    }

    private static Galahad2FanLightingState ReadGalahadFan(JsonValue data)
    {
        int mode = data.Member("Mode")?.AsInt() ?? 0;
        int brightness = data.Member("Brightness")?.AsInt() ?? 0;
        int speed = data.Member("Speed")?.AsInt() ?? 0;
        int direction = data.Member("Direction")?.AsInt() ?? 0;
        int numberOfLed = data.Member("NumberOfLED")?.AsInt() ?? 24; // L-Connect's default ring size
        bool syncToPump = data.Member("SyncToPump")?.AsBool() ?? false;
        return new Galahad2FanLightingState(mode, speed, direction, brightness, numberOfLed, syncToPump, ReadFanColors(data));
    }

    // The Trinity and the HydroShift LCD save the fan colours as a Colors array; the Vision's
    // FanLightingSetting has four named members, Color1 to Color4, instead. Either way the encoder
    // writes them in order, so the named ones are read into the same list.
    private static List<RgbColor> ReadFanColors(JsonValue data)
    {
        JsonValue? colorArray = data.Member("Colors");
        if (colorArray != null)
        {
            return ReadColors(colorArray);
        }

        var colors = new List<RgbColor>();
        for (int i = 1; i <= 4; i++)
        {
            JsonValue? color = data.Member("Color" + i.ToString(CultureInfo.InvariantCulture));
            if (color is null)
            {
                break;
            }

            colors.Add(ReadColor(color));
        }

        return colors;
    }

    // The Vision's ScreenLEDLightingSetting: the ring mode, whether it follows a live sensor
    // (IsDynamicMode, with the DynamicHigh/DynamicLow looks the plugin cannot drive), and the
    // static look's colours and 0 to 100 sliders. A missing slider is read as L-Connect's own
    // "unset" (int.MinValue), which the encoder writes as 0.
    private static Galahad2ScreenLightingState ReadGalahadScreen(JsonValue data)
    {
        int mode = data.Member("Mode")?.AsInt() ?? 0;
        bool isDynamicMode = data.Member("IsDynamicMode")?.AsBool() ?? false;
        JsonValue? staticLook = data.Member("Static");
        int speed = staticLook?.Member("Speed")?.AsInt() ?? int.MinValue;
        int brightness = staticLook?.Member("Brightness")?.AsInt() ?? int.MinValue;
        int direction = staticLook?.Member("Direction")?.AsInt() ?? 0;
        return new Galahad2ScreenLightingState(mode, isDynamicMode, speed, brightness, direction, ReadColors(staticLook?.Member("Colors")));
    }

    // Every saved pump setting, in its saved order: Galahad2TrinityController.setPumpLEDLighting
    // writes each one, so an individual-mode look (Inner then Outer) needs both replayed.
    private static List<Galahad2PumpLightingState> ReadGalahadPumps(JsonValue data)
    {
        var pumps = new List<Galahad2PumpLightingState>();
        foreach (JsonValue element in data.Elements)
        {
            int scope = element.Member("Scope")?.AsInt() ?? 0;
            int mode = element.Member("Mode")?.AsInt() ?? 0;
            int brightness = element.Member("Brightness")?.AsInt() ?? 0;
            int speed = element.Member("Speed")?.AsInt() ?? 0;
            int direction = element.Member("Direction")?.AsInt() ?? 0;
            pumps.Add(new Galahad2PumpLightingState(scope, mode, speed, direction, brightness, ReadColors(element.Member("Colors"))));
        }

        return pumps;
    }

    // Parse a TL LightingConfigCollection into per-fan looks. The structure is
    // LightingConfigs[port] -> { PortType -> GroupElement[] }; the LED port (PortType 1) carries the
    // fan lighting. Only per-fan (non-grouped) elements give an absolute fan index - one Config per
    // fan, accumulated across groups. A grouped element holds a single whole-group look without a
    // per-fan count here, so it is skipped rather than addressed by guesswork.
    private static List<TlFanLightingState> ReadTlFans(JsonValue data)
    {
        var fans = new List<TlFanLightingState>();
        JsonValue? ports = data.Member("LightingConfigs");
        if (ports is null)
        {
            return fans;
        }

        int port = 0;
        foreach (JsonValue portEntry in ports.Elements)
        {
            // PortType.LED == 1 (System.Text.Json serialises the enum dictionary key as its number).
            JsonValue? ledGroups = portEntry.Member("1");
            if (ledGroups != null)
            {
                int fanIndex = 0;
                foreach (JsonValue group in ledGroups.Elements)
                {
                    bool isGrouping = group.Member("IsGrouping")?.AsBool() ?? false;
                    JsonValue? configurations = group.Member("Configs");
                    if (isGrouping || configurations is null)
                    {
                        continue;
                    }

                    foreach (JsonValue configuration in configurations.Elements)
                    {
                        fans.Add(ReadTlFan(port, fanIndex, configuration));
                        fanIndex++;
                    }
                }
            }

            port++;
        }

        return fans;
    }

    private static TlFanLightingState ReadTlFan(int port, int fanIndex, JsonValue configuration)
    {
        int mode = configuration.Member("Mode")?.AsInt() ?? 0;
        int speed = configuration.Member("Speed")?.AsInt() ?? 0;
        int direction = configuration.Member("Direction")?.AsInt() ?? 0;
        int brightness = configuration.Member("Brightness")?.AsInt() ?? 0;
        return new TlFanLightingState(port, fanIndex, mode, speed, direction, brightness, ReadColors(configuration.Member("Colors")));
    }

    private static List<int> ReadIntArray(JsonValue data)
    {
        var values = new List<int>();
        foreach (JsonValue value in data.Elements)
        {
            values.Add(value.AsInt() ?? 0);
        }

        return values;
    }

    private static string? ExtractInstanceToken(string deviceId)
    {
        Match segment = InstanceSegment.Match(deviceId);
        string inner = segment.Success ? segment.Groups[1].Value : deviceId;
        Match token = HexToken.Match(inner);
        return token.Success ? token.Value : null;
    }

    // Accumulates the settings that share one instance token into a single controller look.
    private sealed class Builder
    {
        private readonly string _token;
        private readonly List<LightingPortState> _ports = new List<LightingPortState>();
        private readonly List<TlFanLightingState> _tlFans = new List<TlFanLightingState>();
        private IReadOnlyList<int>? _quantity;
        private IReadOnlyList<int>? _mergeOrder;
        private Galahad2FanLightingState? _galahadFan;
        private IReadOnlyList<Galahad2PumpLightingState>? _galahadPumps;
        private Galahad2ScreenLightingState? _galahadScreen;
        private bool _motherboardArgbSync;

        public Builder(string token)
        {
            _token = token;
        }

        public void AddPort(LightingPortState? port)
        {
            if (port != null)
            {
                _ports.Add(port);
            }
        }

        public void SetQuantity(IReadOnlyList<int> quantity)
        {
            _quantity = quantity;
        }

        public void SetMergeOrder(IReadOnlyList<int> mergeOrder)
        {
            _mergeOrder = mergeOrder;
        }

        public void SetGalahadFan(Galahad2FanLightingState fan)
        {
            _galahadFan = fan;
        }

        // An empty array is no pump look: L-Connect's own handler keeps its current settings then.
        public void SetGalahadPumps(List<Galahad2PumpLightingState> pumps)
        {
            if (pumps.Count > 0)
            {
                _galahadPumps = pumps;
            }
        }

        public void SetGalahadScreen(Galahad2ScreenLightingState screen)
        {
            _galahadScreen = screen;
        }

        public void AddTlFans(IEnumerable<TlFanLightingState> fans)
        {
            _tlFans.AddRange(fans);
        }

        public void SetMotherboardArgbSync(bool motherboardArgbSync)
        {
            _motherboardArgbSync = motherboardArgbSync;
        }

        // A controller with the switch on is a configuration even with no look saved: L-Connect
        // hands its LEDs to the motherboard whether or not a look was ever applied to it.
        public LConnectControllerConfiguration? Build()
        {
            bool hasLook = _ports.Count > 0 || _tlFans.Count > 0 || _galahadFan != null || _galahadPumps != null || _galahadScreen != null;
            if (!hasLook && !_motherboardArgbSync)
            {
                return null;
            }

            return new LConnectControllerConfiguration(
                _token,
                _ports,
                _quantity,
                _tlFans.Count > 0 ? _tlFans : null,
                _galahadFan,
                _galahadPumps,
                _motherboardArgbSync,
                _galahadScreen,
                _mergeOrder);
        }
    }
}
#endif
