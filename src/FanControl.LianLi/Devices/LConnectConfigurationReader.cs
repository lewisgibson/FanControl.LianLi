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
/// reader keeps the <c>LightingPort*</c> and <c>FanQuantity</c> settings and ignores the rest
/// (fan curves, merge order, motherboard sync). Files that are not device settings are
/// skipped by content; a genuinely unreadable file throws and lets the caller disable
/// lighting rather than apply a partial look.
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
                else if (type == "FanLEDLighting")
                {
                    // Galahad II fan ring: a single FanLightingSetting object.
                    builder.SetGalahadFan(ReadGalahadFan(data));
                }
                else if (type == "PumpLEDLighting")
                {
                    // Galahad II pump: an array of PumpLightingSetting (normally one).
                    builder.SetGalahadPump(ReadGalahadPump(data));
                }
                else if (type == "Lighting")
                {
                    // Uni Fan TL: a nested LightingConfigCollection of per-fan looks.
                    builder.AddTlFans(ReadTlFans(data));
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
                int r = color.Member("R")?.AsInt() ?? 0;
                int g = color.Member("G")?.AsInt() ?? 0;
                int b = color.Member("B")?.AsInt() ?? 0;
                colors.Add(new RgbColor((byte)r, (byte)g, (byte)b));
            }
        }

        return colors;
    }

    private static Galahad2FanLightingState ReadGalahadFan(JsonValue data)
    {
        int mode = data.Member("Mode")?.AsInt() ?? 0;
        int brightness = data.Member("Brightness")?.AsInt() ?? 0;
        int speed = data.Member("Speed")?.AsInt() ?? 0;
        int direction = data.Member("Direction")?.AsInt() ?? 0;
        int numberOfLed = data.Member("NumberOfLED")?.AsInt() ?? 24; // L-Connect's default ring size
        bool syncToPump = data.Member("SyncToPump")?.AsBool() ?? false;
        return new Galahad2FanLightingState(mode, speed, direction, brightness, numberOfLed, syncToPump, ReadColors(data.Member("Colors")));
    }

    private static Galahad2PumpLightingState? ReadGalahadPump(JsonValue data)
    {
        // The pump look is saved as an array of settings (normally one); replay the first.
        foreach (JsonValue element in data.Elements)
        {
            int scope = element.Member("Scope")?.AsInt() ?? 0;
            int mode = element.Member("Mode")?.AsInt() ?? 0;
            int brightness = element.Member("Brightness")?.AsInt() ?? 0;
            int speed = element.Member("Speed")?.AsInt() ?? 0;
            int direction = element.Member("Direction")?.AsInt() ?? 0;
            return new Galahad2PumpLightingState(scope, mode, speed, direction, brightness, ReadColors(element.Member("Colors")));
        }

        return null;
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
        private Galahad2FanLightingState? _galahadFan;
        private Galahad2PumpLightingState? _galahadPump;

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

        public void SetGalahadFan(Galahad2FanLightingState fan)
        {
            _galahadFan = fan;
        }

        public void SetGalahadPump(Galahad2PumpLightingState? pump)
        {
            if (pump != null)
            {
                _galahadPump = pump;
            }
        }

        public void AddTlFans(IEnumerable<TlFanLightingState> fans)
        {
            _tlFans.AddRange(fans);
        }

        public LConnectControllerConfiguration? Build()
        {
            bool hasLook = _ports.Count > 0 || _tlFans.Count > 0 || _galahadFan != null || _galahadPump != null;
            if (!hasLook)
            {
                return null;
            }

            return new LConnectControllerConfiguration(
                _token,
                _ports,
                _quantity,
                _tlFans.Count > 0 ? _tlFans : null,
                _galahadFan,
                _galahadPump);
        }
    }
}
#endif
