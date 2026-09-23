using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Protocol;
using FanControl.LianLi.Tests.Fakes;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

/// <summary>
/// Reading what L-Connect saved for the wireless devices - the master's channel, the rendered effect
/// per device, every water block's screen - and what a bad file costs: only its own feature, with a
/// log line naming the file and the reason.
/// </summary>
public sealed class LConnectWirelessConfigurationTests : IDisposable {
    // The shape System.Text.Json gives L-Connect's saved RgbEffect (RfDevice.rgbEffect's setter).
    private const string SavedEffect = "{\"total_frame\":40,\"interval\":33.5,\"rgb_data\":null,\"data\":\"AQIDBA==\","
        + "\"RgbEffectType\":0,\"effect_index\":\"ChQeKA==\",\"total_sub_frame\":2,\"sub_interval\":11,\"led_num\":96}";

    // One LWirelessPumpConfig as DeviceSettingManager saves it.
    private const string FullBlock = "{\"WirelessTemplateIndex\":6,\"AioParams\":{"
        + "\"LoopInterval\":4,\"PumpEnable\":false,\"FanSpeed\":1800,\"FanSpeedEnable\":true,\"LcdBrightness\":55,\"Rotation\":2,"
        + "\"StrColor\":{\"A\":255,\"R\":1,\"G\":2,\"B\":3,\"ScA\":1.0},"
        + "\"ValColor\":{\"A\":254,\"R\":4,\"G\":5,\"B\":6},"
        + "\"UintColor\":{\"A\":253,\"R\":7,\"G\":8,\"B\":9}}}";

    private readonly string _root;
    private readonly string _wireless;
    private readonly string _pumpFile;
    private readonly FakeLogger _log = new FakeLogger();

    public LConnectWirelessConfigurationTests() {
        _root = Path.Combine(Path.GetTempPath(), "lianli-wireless-config-" + Guid.NewGuid().ToString("N"));
        _wireless = Path.Combine(_root, "slv3", "config");
        Directory.CreateDirectory(_wireless);
        _pumpFile = Path.Combine(_root, "pump.0");
    }

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void WritePumpSettings(string json) => WriteGzip(Encoding.UTF8.GetBytes(json));

    private void WriteGzip(byte[] bytes) {
        using FileStream file = File.Create(_pumpFile);
        using var gzip = new GZipStream(file, CompressionMode.Compress);
        gzip.Write(bytes, 0, bytes.Length);
    }

    private LConnectWirelessConfiguration Configuration(bool readsEffects = true)
        => new LConnectWirelessConfiguration(_wireless, _pumpFile, readsEffects, _log);

    [Fact]
    public void ParseEffect_ReadsEveryMemberOfASavedEffect() {
        WirelessSavedEffect effect = LConnectWirelessConfiguration.ParseEffect(SavedEffect);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, effect.Data);
        Assert.Equal(new byte[] { 10, 20, 30, 40 }, effect.EffectIndex);
        Assert.Equal(40, effect.TotalFrame);
        Assert.Equal(2, effect.TotalSubFrame);
        Assert.Equal(96, effect.LedNum);
        Assert.Equal(33.5, effect.Interval);
        Assert.Equal(11.0, effect.SubInterval);
    }

    [Fact]
    public void ParseEffect_TheOptionalMembersDefaultToZero() {
        WirelessSavedEffect effect = LConnectWirelessConfiguration.ParseEffect(
            "{\"data\":\"AQID\",\"effect_index\":\"ChQeKA==\",\"total_frame\":1,\"interval\":2,\"led_num\":1}");

        Assert.Equal(0, effect.TotalSubFrame);
        Assert.Equal(0.0, effect.SubInterval);
    }

    [Theory]
    [InlineData("not json", "invalid")]
    [InlineData("{\"total_frame\":40,\"interval\":33.5,\"led_num\":96}", "\"data\" is missing")]
    [InlineData("{\"data\":\"\",\"effect_index\":\"ChQeKA==\",\"total_frame\":1,\"interval\":1,\"led_num\":1}", "\"data\" is 0 bytes")]
    [InlineData("{\"data\":\"AQID\",\"effect_index\":\"AQI=\",\"total_frame\":1,\"interval\":1,\"led_num\":1}", "\"effect_index\" is not 4 bytes")]
    [InlineData("{\"data\":\"AQID\",\"effect_index\":\"ChQeKA==\",\"total_frame\":0,\"interval\":1,\"led_num\":1}", "\"total_frame\" is not positive")]
    [InlineData("{\"data\":\"AQID\",\"effect_index\":\"ChQeKA==\",\"total_frame\":1,\"interval\":1,\"led_num\":256}", "\"led_num\" is not a byte")]
    [InlineData("{\"data\":\"AQID\",\"effect_index\":\"ChQeKA==\",\"total_frame\":1,\"interval\":1,\"led_num\":-1}", "\"led_num\" is not a byte")]
    [InlineData("{\"data\":\"AQID\",\"effect_index\":\"ChQeKA==\",\"total_frame\":1,\"led_num\":1}", "\"interval\" is missing")]
    [InlineData("{\"data\":\"AQID\",\"effect_index\":\"ChQeKA==\",\"interval\":1,\"led_num\":1}", "\"total_frame\" is missing")]
    public void ParseEffect_NamesWhatIsWrongWithAMalformedDocument(string json, string reason)
        => Assert.Contains(reason, Assert.Throws<FormatException>(() => LConnectWirelessConfiguration.ParseEffect(json)).Message);

    [Fact]
    public void ParseEffect_RejectsBadBase64AndAnOversizedEffect() {
        Assert.Throws<FormatException>(() => LConnectWirelessConfiguration.ParseEffect(
            "{\"data\":\"***\",\"effect_index\":\"ChQeKA==\",\"total_frame\":1,\"interval\":1,\"led_num\":1}"));
        // The stream counts its chunks, header included, in one byte: 254 chunks of 220 bytes at most.
        string oversized = "{\"data\":\"" + Convert.ToBase64String(new byte[55881])
            + "\",\"effect_index\":\"ChQeKA==\",\"total_frame\":1,\"interval\":1,\"led_num\":1}";
        Assert.Contains("55881 bytes", Assert.Throws<FormatException>(() => LConnectWirelessConfiguration.ParseEffect(oversized)).Message);
        string largest = "{\"data\":\"" + Convert.ToBase64String(new byte[55880])
            + "\",\"effect_index\":\"ChQeKA==\",\"total_frame\":1,\"interval\":1,\"led_num\":1}";
        Assert.Equal(55880, LConnectWirelessConfiguration.ParseEffect(largest).Data.Length);
    }

    // RfDevice.rgbEffect: slv3\config\<address without colons>.effect.
    [Fact]
    public void FindEffect_ReadsTheDevicesFileOrReturnsNull() {
        File.WriteAllText(Path.Combine(_wireless, "a0000000000a.effect"), SavedEffect);
        LConnectWirelessConfiguration configuration = Configuration();

        Assert.NotNull(configuration.FindEffect("a0000000000a"));
        Assert.Null(configuration.FindEffect("b0000000000b"));
        Assert.Empty(_log.Messages);
    }

    [Fact]
    public void FindEffect_IsAlwaysNullWhenLightingIsNotDriven() {
        File.WriteAllText(Path.Combine(_wireless, "a0000000000a.effect"), SavedEffect);

        Assert.Null(Configuration(readsEffects: false).FindEffect("a0000000000a"));
    }

    [Fact]
    public void FindEffect_ACorruptFileIsLoggedAndCostsOnlyTheLook() {
        string path = Path.Combine(_wireless, "a0000000000a.effect");
        File.WriteAllText(path, "{\"data\":");

        Assert.Null(Configuration().FindEffect("a0000000000a"));
        Assert.Contains(_log.Messages, m => m.StartsWith("wireless: saved lighting effect for a0000000000a unusable, ignored (" + path + "): FormatException: ", StringComparison.Ordinal));
    }

    // L-Connect rewrites the file in place (StreamWriter, append: false), so it can be held open.
    [Fact]
    public void FindEffect_ALockedFileIsLoggedAndCostsOnlyTheLook() {
        string path = Path.Combine(_wireless, "a0000000000a.effect");
        File.WriteAllText(path, SavedEffect);
        LConnectWirelessConfiguration configuration = Configuration();

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            Assert.Null(configuration.FindEffect("a0000000000a"));
        }

        Assert.Contains(_log.Messages, m => m.Contains("saved lighting effect for a0000000000a unusable") && m.Contains("IOException"));
    }

    [Fact]
    public void FindChannel_ReadsThisMastersFile() {
        File.WriteAllText(Path.Combine(_wireless, "112233445566.config"), "21");

        Assert.Equal(21, Configuration().FindChannel("112233445566"));
        Assert.Null(Configuration().FindChannel("aabbccddeeff"));
    }

    // RFController.GetChannel catches, logs and returns 0 for a file Convert.ToByte cannot read.
    [Fact]
    public void FindChannel_ABadFileIsLoggedAndTheDefaultStands() {
        string path = Path.Combine(_wireless, "112233445566.config");
        File.WriteAllText(path, "999");

        Assert.Null(Configuration().FindChannel("112233445566"));
        Assert.Contains(_log.Messages, m => m.StartsWith("wireless: saved channel for master 112233445566 unusable, ignored (" + path + "): OverflowException: ", StringComparison.Ordinal));
    }

    [Fact]
    public void ParsePumpSettings_ReadsEveryScreenSettingAndKeysItWithoutSeparators() {
        var problems = new List<string>();

        IReadOnlyDictionary<string, WirelessAioPresentation> presentations = LConnectWirelessConfiguration.ParsePumpSettings(
            "{\"DeviceID\":\"LWireless-Controller\",\"Type\":\"Pump\",\"Data\":{\"AA:BB:CC:DD:EE:FF\":" + FullBlock + "}}", problems);

        WirelessAioPresentation presentation = Assert.Contains("aabbccddeeff", presentations);
        Assert.Empty(problems);
        Assert.Equal(4, presentation.RefreshInterval);
        Assert.False(presentation.PumpTemperatureShown);
        Assert.Equal(1800, presentation.FanSpeed);
        Assert.True(presentation.FanSpeedShown);
        Assert.Equal(55, presentation.Brightness);
        Assert.Equal(6, presentation.ThemeIndex);
        Assert.Equal(2, presentation.Rotation);
        Assert.Equal(255, presentation.Title.Alpha);
        Assert.Equal(1, presentation.Title.Red);
        Assert.Equal(2, presentation.Title.Green);
        Assert.Equal(3, presentation.Title.Blue);
        Assert.Equal(5, presentation.Value.Green);
        Assert.Equal(9, presentation.Unit.Blue);
    }

    // System.Text.Json leaves a missing WirelessTemplateIndex at 0.
    [Fact]
    public void ParsePumpSettings_AMissingThemeIsThemeZero() {
        string block = FullBlock.Replace("\"WirelessTemplateIndex\":6,", string.Empty);

        WirelessAioPresentation presentation = Assert.Contains(
            "aabbccddeeff", LConnectWirelessConfiguration.ParsePumpSettings("{\"Data\":{\"aabbccddeeff\":" + block + "}}", new List<string>()));

        Assert.Equal(0, presentation.ThemeIndex);
    }

    // A screen in advance mode plays streamed content; L-Connect never applies the wireless theme
    // to it, so its theme goes out as 0 while every other setting is kept.
    [Theory]
    [InlineData("true", 0)]
    [InlineData("false", 6)]
    public void ParsePumpSettings_AScreenInAdvanceMode_HasNoTheme(string advanceMode, int theme) {
        string block = FullBlock.Replace("{\"WirelessTemplateIndex\":6,", "{\"IsAdvanceMode\":" + advanceMode + ",\"WirelessTemplateIndex\":6,");

        WirelessAioPresentation presentation = Assert.Contains(
            "aabbccddeeff", LConnectWirelessConfiguration.ParsePumpSettings("{\"Data\":{\"aabbccddeeff\":" + block + "}}", new List<string>()));

        Assert.Equal(theme, presentation.ThemeIndex);
        Assert.Equal(advanceMode == "true", presentation.AdvanceMode);
    }

    // One LWirelessPumpConfig with one member (or one colour component, "Colour.X") left out.
    private static string BlockWithout(string omit) {
        string Colour(string name, int a, int r, int g, int b) {
            var parts = new List<string>();
            foreach ((string component, int value) in new[] { ("A", a), ("R", r), ("G", g), ("B", b) }) {
                if (omit != name + "." + component) {
                    parts.Add("\"" + component + "\":" + value);
                }
            }

            return "{" + string.Join(",", parts) + "}";
        }

        var members = new List<(string, string)> {
            ("LoopInterval", "4"),
            ("PumpEnable", "false"),
            ("FanSpeed", "1800"),
            ("FanSpeedEnable", "true"),
            ("LcdBrightness", "55"),
            ("Rotation", "2"),
            ("StrColor", Colour("StrColor", 255, 1, 2, 3)),
            ("ValColor", Colour("ValColor", 254, 4, 5, 6)),
            ("UintColor", Colour("UintColor", 253, 7, 8, 9)),
        };
        members.RemoveAll(m => m.Item1 == omit);
        return "{\"WirelessTemplateIndex\":6,\"AioParams\":{" + string.Join(",", members.ConvertAll(m => "\"" + m.Item1 + "\":" + m.Item2)) + "}}";
    }

    // One incomplete block is left out and named; the others still load.
    [Theory]
    [InlineData("LoopInterval")]
    [InlineData("PumpEnable")]
    [InlineData("FanSpeed")]
    [InlineData("FanSpeedEnable")]
    [InlineData("LcdBrightness")]
    [InlineData("Rotation")]
    [InlineData("StrColor")]
    [InlineData("ValColor.R")]
    [InlineData("UintColor.G")]
    public void ParsePumpSettings_AnIncompleteBlockIsLeftOutAndNamed(string omit) {
        var problems = new List<string>();

        IReadOnlyDictionary<string, WirelessAioPresentation> presentations = LConnectWirelessConfiguration.ParsePumpSettings(
            "{\"Data\":{\"aabbccddeeff\":" + BlockWithout(omit) + ",\"112233445566\":" + FullBlock + "}}", problems);

        Assert.DoesNotContain("aabbccddeeff", presentations.Keys);
        Assert.Contains("112233445566", presentations.Keys);
        Assert.Equal("aabbccddeeff: \"" + omit + "\" is missing or of the wrong type.", Assert.Single(problems));
    }

    [Fact]
    public void ParsePumpSettings_ABlockWithoutAioParamsIsLeftOutAndNamed() {
        var problems = new List<string>();

        LConnectWirelessConfiguration.ParsePumpSettings("{\"Data\":{\"aabbccddeeff\":{\"RPMConfig\":{}}}}", problems);

        Assert.Equal("aabbccddeeff: \"AioParams\" is missing or of the wrong type.", Assert.Single(problems));
    }

    [Fact]
    public void BlockWithout_NothingIsTheFullBlock()
        => Assert.Equal(FullBlock.Replace(",\"ScA\":1.0", string.Empty), BlockWithout("none"));

    [Theory]
    [InlineData(256)]
    [InlineData(-1)]
    public void ParsePumpSettings_AColourComponentThatIsNotAByteIsNamed(int value) {
        var problems = new List<string>();

        LConnectWirelessConfiguration.ParsePumpSettings(
            "{\"Data\":{\"aabbccddeeff\":" + FullBlock.Replace("\"B\":9", "\"B\":" + value) + "}}", problems);

        Assert.Contains("\"UintColor.B\" is not a byte", Assert.Single(problems));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"Type\":\"Pump\"}")]
    public void ParsePumpSettings_ADocumentWithoutDataThrows(string json)
        => Assert.Throws<FormatException>(() => LConnectWirelessConfiguration.ParsePumpSettings(json, new List<string>()));

    [Fact]
    public void FindPumpPresentation_ReadsTheGzippedDocument() {
        WritePumpSettings("{\"Data\":{\"aa:bb:cc:dd:ee:ff\":" + FullBlock + "}}");
        LConnectWirelessConfiguration configuration = Configuration();

        Assert.Equal(55, configuration.FindPumpPresentation("aabbccddeeff")!.Brightness);
        Assert.Null(configuration.FindPumpPresentation("000000000000"));
        Assert.Empty(_log.Messages);
    }

    [Fact]
    public void FindPumpPresentation_IsNullWhenLConnectSavedNothing()
        => Assert.Null(Configuration().FindPumpPresentation("aabbccddeeff"));

    [Fact]
    public void AnIncompleteBlock_IsLoggedAndThatBlockKeepsTheDefaultScreen() {
        WritePumpSettings("{\"Data\":{\"aabbccddeeff\":{\"RPMConfig\":{}}}}");

        Assert.Null(Configuration().FindPumpPresentation("aabbccddeeff"));
        Assert.Contains(
            "wireless: saved screen settings for aabbccddeeff: \"AioParams\" is missing or of the wrong type.; that water block keeps L-Connect's default screen",
            _log.Messages);
    }

    // The service's loadSettings catches and logs a settings file it cannot load.
    [Fact]
    public void ACorruptGzip_IsLoggedAndCostsOnlyTheScreens() {
        File.WriteAllBytes(_pumpFile, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });

        LConnectWirelessConfiguration configuration = Configuration();

        Assert.Null(configuration.FindPumpPresentation("aabbccddeeff"));
        Assert.Contains(_log.Messages, m => m.StartsWith("wireless: saved pump settings unusable, ignored (" + _pumpFile + "): InvalidDataException: ", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOversizedDocument_IsLoggedAndCostsOnlyTheScreens() {
        WriteGzip(new byte[(8 * 1024 * 1024) + 1]);

        Assert.Null(Configuration().FindPumpPresentation("aabbccddeeff"));
        Assert.Contains(_log.Messages, m => m.Contains("saved pump settings unusable") && m.Contains("FormatException"));
    }

    [Fact]
    public void ALockedDocument_IsLoggedAndCostsOnlyTheScreens() {
        WritePumpSettings("{\"Data\":{}}");

        using (new FileStream(_pumpFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            Assert.Null(Configuration().FindPumpPresentation("aabbccddeeff"));
        }

        Assert.Contains(_log.Messages, m => m.Contains("saved pump settings unusable") && m.Contains("IOException"));
    }

    [Fact]
    public void ADocumentThatIsNotJson_IsLoggedAndCostsOnlyTheScreens() {
        WritePumpSettings("{{{");

        Assert.Null(Configuration().FindPumpPresentation("aabbccddeeff"));
        Assert.Contains(_log.Messages, m => m.Contains("saved pump settings unusable") && m.Contains("FormatException"));
    }

    [Fact]
    public void IsSettingsFault_IsEveryWayAFileCanBeUnusableAndNothingElse() {
        Assert.True(LConnectWirelessConfiguration.IsSettingsFault(new IOException()));
        Assert.True(LConnectWirelessConfiguration.IsSettingsFault(new FileNotFoundException()));
        Assert.True(LConnectWirelessConfiguration.IsSettingsFault(new UnauthorizedAccessException()));
        Assert.True(LConnectWirelessConfiguration.IsSettingsFault(new SecurityException()));
        Assert.True(LConnectWirelessConfiguration.IsSettingsFault(new InvalidDataException()));
        Assert.True(LConnectWirelessConfiguration.IsSettingsFault(new ArgumentException()));
        Assert.True(LConnectWirelessConfiguration.IsSettingsFault(new NotSupportedException()));
        Assert.True(LConnectWirelessConfiguration.IsSettingsFault(new FormatException()));
        Assert.True(LConnectWirelessConfiguration.IsSettingsFault(new OverflowException()));
        Assert.False(LConnectWirelessConfiguration.IsSettingsFault(new InvalidOperationException()));
        Assert.False(LConnectWirelessConfiguration.IsSettingsFault(new KeyNotFoundException()));
    }

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF", "aabbccddeeff")]
    [InlineData("aa-bb-cc-dd-ee-ff", "aabbccddeeff")]
    [InlineData("aabbccddeeff", "aabbccddeeff")]
    public void NormaliseAddress_StripsSeparatorsAndCase(string address, string expected)
        => Assert.Equal(expected, LConnectWirelessConfiguration.NormaliseAddress(address));

    [Fact]
    public void NullArgumentsThrow() {
        LConnectWirelessConfiguration configuration = Configuration();
        Assert.Throws<ArgumentNullException>(() => new LConnectWirelessConfiguration(null!, _pumpFile, true, _log));
        Assert.Throws<ArgumentNullException>(() => new LConnectWirelessConfiguration(_wireless, null!, true, _log));
        Assert.Throws<ArgumentNullException>(() => new LConnectWirelessConfiguration(_wireless, _pumpFile, true, null!));
        Assert.Throws<ArgumentNullException>(() => configuration.FindChannel(null!));
        Assert.Throws<ArgumentNullException>(() => configuration.FindEffect(null!));
        Assert.Throws<ArgumentNullException>(() => configuration.FindPumpPresentation(null!));
        Assert.Throws<ArgumentNullException>(() => LConnectWirelessConfiguration.ParseEffect(null!));
        Assert.Throws<ArgumentNullException>(() => LConnectWirelessConfiguration.ParsePumpSettings(null!, new List<string>()));
        Assert.Throws<ArgumentNullException>(() => LConnectWirelessConfiguration.ParsePumpSettings("{}", null!));
        Assert.Throws<ArgumentNullException>(() => LConnectWirelessConfiguration.NormaliseAddress(null!));
    }

    // ---------- the locked device list (savedDevices.config) ----------

    // One RfDevice as System.Text.Json writes it: byte arrays as base64, the rest as numbers, plus
    // the fields the plugin does not read.
    private static string LockedEntry(string mac, string master, int devType = 0, int fanNum = 2, string masterText = "aa:bb:cc:dd:ee:ff")
        => "{\"changingEffect\":false,\"MacStr\":\"x\",\"MasterMacStr\":\"" + masterText + "\",\"_target_rx_type\":4,\"_rx_type\":3,"
            + "\"bind_to_master\":true,\"mac_addr\":\"" + mac + "\",\"master_mac_addr\":\"" + master + "\",\"channel\":8,"
            + "\"dev_type\":" + devType + ",\"fan_num\":" + fanNum + ",\"effect_index\":\"AAAAAA==\",\"fans_type\":\"JCQAAA==\","
            + "\"fans_pwm\":\"MjIyMg==\",\"target_fans_pwm\":\"ZGRkZA==\",\"visual\":false}";

    // 0xAABBCCDDEEFF and 0xA00000000001 / 0xA00000000002 in base64.
    private const string MasterBase64 = "qrvM3e7/";

    [Fact]
    public void ParseLockedDevices_ReadsTheList_InItsOrder() {
        string json = "[" + LockedEntry("oAAAAAAC", MasterBase64) + "," + LockedEntry("oAAAAAAB", MasterBase64, devType: 10, fanNum: 1) + "]";

        IReadOnlyList<WirelessLockedDevice> locked = LConnectWirelessConfiguration.ParseLockedDevices(json, "aabbccddeeff")!;

        Assert.Equal(new[] { "a00000000002", "a00000000001" }, locked.Select(d => d.Record.MacText));
        WirelessLockedDevice first = locked[0];
        Assert.Equal(3, first.Record.ReceiverType);
        Assert.Equal(4, first.TargetReceiverType);
        Assert.Equal(8, first.Record.Channel);
        Assert.Equal(2, first.Record.FanCount);
        Assert.Equal(new byte[] { 36, 36, 0, 0 }, first.Record.FanTypes);
        Assert.Equal(new byte[] { 50, 50, 50, 50 }, first.Record.Pwm);
        Assert.Equal(new byte[] { 100, 100, 100, 100 }, first.TargetPwm);
        Assert.True(first.Record.IsBoundTo(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF }));
        Assert.Equal(10, locked[1].Record.DeviceType);
    }

    // CheckLockAndInitData: nothing, an empty list, or a list saved for another master is no lock.
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("[]")]
    public void ParseLockedDevices_NothingSaved_IsNoLock(string json)
        => Assert.Null(LConnectWirelessConfiguration.ParseLockedDevices(json, "aabbccddeeff"));

    [Fact]
    public void ParseLockedDevices_AnotherMastersList_IsNoLock()
        => Assert.Null(LConnectWirelessConfiguration.ParseLockedDevices(
            "[" + LockedEntry("oAAAAAAB", MasterBase64, masterText: "11:22:33:44:55:66") + "]", "aabbccddeeff"));

    [Theory]
    [InlineData("{}")]
    [InlineData("[{}]")]
    [InlineData("[{\"fan_num\":9}]")]
    [InlineData("[{\"fan_num\":-1}]")]
    public void ParseLockedDevices_ThatIsNotWhatLConnectWrites_Throws(string json)
        => Assert.Throws<FormatException>(() => LConnectWirelessConfiguration.ParseLockedDevices(json, "aabbccddeeff"));

    [Fact]
    public void ParseLockedDevices_AByteOutOfRange_OrAFirstEntryWithNoMaster_Throws() {
        string badChannel = LockedEntry("oAAAAAAB", MasterBase64).Replace("\"channel\":8", "\"channel\":300");
        string negativeChannel = LockedEntry("oAAAAAAB", MasterBase64).Replace("\"channel\":8", "\"channel\":-1");
        Assert.Throws<FormatException>(() => LConnectWirelessConfiguration.ParseLockedDevices("[" + negativeChannel + "]", "aabbccddeeff"));
        string noMaster = LockedEntry("oAAAAAAB", MasterBase64).Replace("\"MasterMacStr\":\"aa:bb:cc:dd:ee:ff\",", string.Empty);

        Assert.Throws<FormatException>(() => LConnectWirelessConfiguration.ParseLockedDevices("[" + badChannel + "]", "aabbccddeeff"));
        Assert.Throws<FormatException>(() => LConnectWirelessConfiguration.ParseLockedDevices("[" + noMaster + "]", "aabbccddeeff"));
        Assert.Throws<ArgumentNullException>(() => LConnectWirelessConfiguration.ParseLockedDevices(null!, "aabbccddeeff"));
        Assert.Throws<ArgumentNullException>(() => LConnectWirelessConfiguration.ParseLockedDevices("[]", null!));
    }

    [Fact]
    public void FindLockedDevices_ReadsTheFile_AndABadOneIsLoggedAsNoLock() {
        Assert.Null(Configuration().FindLockedDevices("aabbccddeeff"));

        File.WriteAllText(Path.Combine(_wireless, "savedDevices.config"), "[" + LockedEntry("oAAAAAAB", MasterBase64) + "]");
        Assert.Single(Configuration().FindLockedDevices("aabbccddeeff")!);

        File.WriteAllText(Path.Combine(_wireless, "savedDevices.config"), "[{{");
        Assert.Null(Configuration().FindLockedDevices("aabbccddeeff"));
        Assert.Contains(_log.Messages, m => m.Contains("saved locked device list unusable"));
        Assert.Throws<ArgumentNullException>(() => Configuration().FindLockedDevices(null!));
    }
}
